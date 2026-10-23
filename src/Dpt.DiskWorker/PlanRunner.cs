using System.Text.Json;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker;

/// <summary>Where disk changes are allowed.</summary>
public static class SafetyMode
{
    /// <summary>
    /// True when only virtual disks (VHD/VHDX files) may be touched: always in Debug builds and whenever
    /// DPT_SAFETY=virtual-only (all test runs). Release builds may target physical disks after the app's confirmations.
    /// </summary>
    public static bool VirtualOnly
    {
        get
        {
#if DEBUG
            return true;
#else
            return string.Equals(Environment.GetEnvironmentVariable("DPT_SAFETY"), "virtual-only", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }

    public static bool Current => VirtualOnly;
}

/// <summary>JSON-lines progress file the app tails.</summary>
public sealed class StatusWriter : IDisposable
{
    private readonly StreamWriter? _writer;

    public StatusWriter(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _writer = null;
        }
    }

    public List<DiskProgress> Written { get; } = [];

    public void Write(DiskProgressState state, string message, double fraction = 0, int step = -1)
    {
        var p = new DiskProgress(state, message, fraction, step, DateTime.UtcNow);
        Written.Add(p);
        _writer?.WriteLine(JsonSerializer.Serialize(p, PipeProtocol.Json));
        Console.WriteLine($"[{state}] {message}");
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>Validates a plan against the real disks and executes it, refusing at the first doubt.</summary>
public sealed class PlanRunner(DiskPlan plan, StatusWriter status, bool virtualOnly)
{
    private static readonly DiskStepKind[] Writes =
    [
        DiskStepKind.InitializeDisk, DiskStepKind.CreatePartition, DiskStepKind.DeletePartition, DiskStepKind.ResizePartition,
        DiskStepKind.FormatVolume, DiskStepKind.SetDriveLetter, DiskStepKind.SetLabel, DiskStepKind.CloneDisk, DiskStepKind.WindowsCopy,
        DiskStepKind.RebuildBootFiles,
    ];

    /// <summary>Extension point for M8/M9 steps (clone, Windows copy, boot files).</summary>
    public static Dictionary<DiskStepKind, Func<DiskPlan, DiskStep, StatusWriter, int>> AdvancedSteps { get; } = [];

    public int Run()
    {
        status.Write(DiskProgressState.Started, $"{plan.Title}{(virtualOnly ? " (virtual disks only)" : "")}");

        // 1. The owner's typed confirmation.
        if (plan.ExpectedConfirmation is { } expected && !string.Equals(plan.Confirmation?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            return Refuse(DiskWorkerExit.RefusedConfirmation, "The confirmation text doesn't match. Nothing was changed.");

        var disks = StorageInventory.GetDisks();
        var target = disks.FirstOrDefault(d => d.Number == plan.Target.Number);
        if (target is null) return Refuse(DiskWorkerExit.RefusedFingerprint, $"Disk {plan.Target.Number} is no longer connected. Nothing was changed.");

        // 2. Safety mode: only virtual disks during development and tests (AGENTS.md rule 1).
        if (virtualOnly && !target.IsVirtual)
            return Refuse(DiskWorkerExit.RefusedSafety, $"Refused: disk {target.Number} ({target.FriendlyName}, {target.BusType}) is not a virtual disk, and this build only changes virtual disks.");
        DiskInfo? source = null;
        if (plan.Source is { } src)
        {
            source = disks.FirstOrDefault(d => d.Number == src.Number);
            if (source is null) return Refuse(DiskWorkerExit.RefusedFingerprint, "The source disk is no longer connected. Nothing was changed.");
            if (virtualOnly && !source.IsVirtual) return Refuse(DiskWorkerExit.RefusedSafety, "Refused: the source is not a virtual disk, and this build only uses virtual disks.");
            if (Fingerprint.Mismatch(src, source) is { } why) return Refuse(DiskWorkerExit.RefusedFingerprint, "Source disk: " + why + " Nothing was changed.");
            if (source.Number == target.Number) return Refuse(DiskWorkerExit.RefusedProtected, "The source and destination are the same disk.");
        }

        // 3. The disk is exactly the one (and layout) the owner previewed.
        if (Fingerprint.Mismatch(plan.Target, target) is { } mismatch)
            return Refuse(DiskWorkerExit.RefusedFingerprint, mismatch + " Please review the plan again. Nothing was changed.");

        // 4. Protected partitions and disks.
        if (CheckProtection(plan, target) is { } protectedReason) return Refuse(DiskWorkerExit.RefusedProtected, protectedReason);

        // 5. Execute.
        using var ops = new StorageOperations();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            // Before every write: still the same physical disk (identity and size).
            var now = StorageInventory.GetDisks().FirstOrDefault(d => d.Number == plan.Target.Number);
            if (now is null || !string.Equals(now.UniqueId, plan.Target.UniqueId, StringComparison.OrdinalIgnoreCase) || now.Size != plan.Target.Size || (virtualOnly && !now.IsVirtual))
                return Fail($"Stopped before step {i + 1}: the destination disk changed or disappeared.", i);
            status.Write(DiskProgressState.Step, Describe(step), (double)i / plan.Steps.Count, i);
            try
            {
                switch (step.Kind)
                {
                    case DiskStepKind.Probe:
                        status.Write(DiskProgressState.Progress, $"Disk {now.Number}: {now.FriendlyName}, {now.Partitions.Count} partition(s), {now.BusType}.", (double)(i + 1) / plan.Steps.Count, i);
                        break;
                    case DiskStepKind.InitializeDisk:
                        ops.InitializeDisk(now.Number, step.PartitionStyle ?? "GPT");
                        break;
                    case DiskStepKind.CreatePartition:
                    {
                        var number = ops.CreatePartition(now.Number, step.Offset, step.Size, step.DriveLetter, step.AutoLetter);
                        if (step.FileSystem is { } fs) ops.Format(now.Number, number, fs, step.Label ?? "");
                        status.Write(DiskProgressState.Progress, $"Created partition {number}.", (double)(i + 1) / plan.Steps.Count, i);
                        break;
                    }
                    case DiskStepKind.DeletePartition:
                        ops.DeletePartition(now.Number, Need(step.PartitionNumber));
                        break;
                    case DiskStepKind.ResizePartition:
                    {
                        var (min, max) = ops.SupportedSize(now.Number, Need(step.PartitionNumber));
                        var size = Need(step.Size);
                        if (size < min || size > max)
                            return Fail($"The new size must be between {min:N0} and {max:N0} bytes. Nothing was changed by this step.", i);
                        ops.ResizePartition(now.Number, Need(step.PartitionNumber), size);
                        break;
                    }
                    case DiskStepKind.FormatVolume:
                        ops.Format(now.Number, Need(step.PartitionNumber), step.FileSystem ?? "NTFS", step.Label ?? "");
                        break;
                    case DiskStepKind.SetDriveLetter:
                        ops.SetDriveLetter(now.Number, Need(step.PartitionNumber), step.DriveLetter);
                        break;
                    case DiskStepKind.SetLabel:
                        ops.SetLabel(now.Number, Need(step.PartitionNumber), step.Label ?? "");
                        break;
                    default:
                        if (!AdvancedSteps.TryGetValue(step.Kind, out var handler))
                            return Fail($"This version can't do \"{step.Kind}\" yet.", i);
                        var code = handler(plan, step, status);
                        if (code != DiskWorkerExit.Ok) return code;
                        break;
                }
            }
            catch (Exception ex) when (ex is StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException or IOException or UnauthorizedAccessException)
            {
                return Fail($"Step {i + 1} failed: {ex.Message}", i);
            }
        }

        status.Write(DiskProgressState.Succeeded, "Done.", 1);
        return DiskWorkerExit.Ok;
    }

    /// <summary>Pure check (no disk access): why the plan touches something it must not, or null.</summary>
    public static string? CheckProtection(DiskPlan plan, DiskInfo target)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)![0];
        foreach (var step in plan.Steps.Where(s => Writes.Contains(s.Kind)))
        {
            if (step.Kind is DiskStepKind.InitializeDisk or DiskStepKind.CloneDisk or DiskStepKind.WindowsCopy && (target.IsBoot || target.IsSystem))
                return "Refused: the destination is the disk Windows is running from.";
            if (step.PartitionNumber is not { } pn) continue;
            var part = target.Partitions.FirstOrDefault(p => p.PartitionNumber == pn);
            if (part is null) continue; // created earlier in this plan
            if (part.DriveLetter == systemRoot && step.Kind is DiskStepKind.DeletePartition or DiskStepKind.FormatVolume or DiskStepKind.ResizePartition or DiskStepKind.SetDriveLetter)
                return "Refused: that is the partition Windows is running from.";
            if (part.IsProtected && !plan.AllowSystemPartitions)
                return $"Refused: partition {pn} is needed to start Windows ({string.Join(", ", part.RoleTags)}). Unlock system partitions in Settings first.";
        }

        return null;
    }

    private static T Need<T>(T? value) where T : struct => value ?? throw new StorageOperations.StorageOperationException("The plan is missing a value.");

    private int Refuse(int code, string message)
    {
        status.Write(DiskProgressState.Refused, message);
        return code;
    }

    private int Fail(string message, int step)
    {
        status.Write(DiskProgressState.Failed, message, 0, step);
        return DiskWorkerExit.Failed;
    }

    public static string Describe(DiskStep s) => s.Kind switch
    {
        DiskStepKind.Probe => "Checking the disk",
        DiskStepKind.InitializeDisk => $"Preparing the disk ({s.PartitionStyle ?? "GPT"})",
        DiskStepKind.CreatePartition => $"Creating a partition{(s.FileSystem is { } fs ? $" ({fs})" : "")}",
        DiskStepKind.DeletePartition => $"Deleting partition {s.PartitionNumber}",
        DiskStepKind.ResizePartition => $"Resizing partition {s.PartitionNumber}",
        DiskStepKind.FormatVolume => $"Formatting partition {s.PartitionNumber} as {s.FileSystem}",
        DiskStepKind.SetDriveLetter => s.DriveLetter is { } l ? $"Giving partition {s.PartitionNumber} the letter {l}:" : $"Removing partition {s.PartitionNumber}'s drive letter",
        DiskStepKind.SetLabel => $"Renaming partition {s.PartitionNumber} to \"{s.Label}\"",
        DiskStepKind.CloneDisk => "Cloning the disk",
        DiskStepKind.WindowsCopy => "Copying Windows",
        DiskStepKind.RebuildBootFiles => "Rebuilding boot files",
        _ => s.Kind.ToString(),
    };
}
