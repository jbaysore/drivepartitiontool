using System.Diagnostics;
using System.Text.Json;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Clone;

public sealed record CloneOptions
{
    /// <summary>When the destination is larger, grow the last partition into the extra space.</summary>
    public bool ExtendLastPartition { get; init; } = true;

    /// <summary>Rebuild boot files on the copy when the source holds Windows.</summary>
    public bool MakeBootable { get; init; } = true;
}

/// <summary>
/// Whole-disk clone: recreate the source's partition layout on the destination (new disk and partition IDs), take the
/// destination offline, copy every partition (NTFS: only used clusters, read from a Volume Shadow Copy snapshot; others:
/// raw), bring it online, optionally grow the last partition and rebuild boot files. The source is only read.
/// </summary>
public static class CloneEngine
{
    private const long Mb = 1024 * 1024;

    public static void Register() => PlanRunner.AdvancedSteps[DiskStepKind.CloneDisk] = Run;

    private static int Run(DiskPlan plan, DiskStep step, StatusWriter status)
    {
        var options = step.Options is { Length: > 0 } json ? JsonSerializer.Deserialize<CloneOptions>(json, PipeProtocol.Json) ?? new() : new CloneOptions();
        var disks = StorageInventory.GetDisks();
        var source = disks.Single(d => d.Number == plan.Source!.Number);
        var target = disks.Single(d => d.Number == plan.Target.Number);
        var lastEnd = source.Partitions.Count == 0 ? 0 : source.Partitions.Max(p => p.Offset + p.Size);
        if (target.Size < lastEnd + Mb)
        {
            status.Write(DiskProgressState.Failed, $"The destination ({target.Size / Mb:N0} MB) is smaller than the space the source's partitions use ({lastEnd / Mb:N0} MB). Nothing was changed.");
            return DiskWorkerExit.Failed;
        }

        var totalBytes = source.Partitions.Sum(p => p.Used ?? p.Size);
        long copiedBytes = 0;
        void Copied(long n)
        {
            copiedBytes += n;
            if (copiedBytes % (64 * Mb) < n) status.Write(DiskProgressState.Progress, $"Copied {copiedBytes / Mb:N0} MB", 0.1 + 0.8 * Math.Min(1, (double)copiedBytes / Math.Max(1, totalBytes)));
        }

        using var ops = new StorageOperations();

        // 1. Same layout, new identity.
        status.Write(DiskProgressState.Progress, $"Preparing disk {target.Number} with the same {source.PartitionStyle} layout as disk {source.Number}", 0.02);
        ops.ClearAndInitialize(target.Number, source.PartitionStyle == "MBR" ? "MBR" : "GPT");
        var map = new List<(PartitionInfo Source, int TargetNumber)>();
        foreach (var p in source.Partitions.OrderBy(p => p.Offset))
        {
            var number = ops.CreatePartitionExact(target.Number, p.Offset, p.Size, source.PartitionStyle == "GPT" ? p.GptType : null,
                source.PartitionStyle == "MBR" ? p.MbrType : null, p.IsActive);
            map.Add((p, number));
        }

        // 2. Nothing else may write to the destination while it's filled.
        ops.SetOffline(target.Number, true);
        try
        {
            using var src = RawIo.Open(RawIo.PhysicalDrive(source.Number), write: false);
            using var dst = RawIo.Open(RawIo.PhysicalDrive(target.Number), write: true);
            var buffer = new byte[4 * (int)Mb];
            foreach (var (p, _) in map)
            {
                var label = p.DriveLetter is { } l ? $"{l}:" : p.Role.ToString();
                if (string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) && p.VolumePath is { } volume)
                {
                    status.Write(DiskProgressState.Progress, $"Copying {label} (used space only, from a snapshot)", 0.1 + 0.8 * copiedBytes / Math.Max(1, totalBytes));
                    using var shadow = ShadowCopy.Create(volume);
                    using var vol = RawIo.Open(shadow.Device, write: false);
                    var (cluster, clusters) = RawIo.NtfsGeometry(vol);
                    foreach (var (lcn, count) in RawIo.UsedClusterRuns(vol))
                        RawIo.Copy(vol, lcn * cluster, dst, p.Offset + lcn * cluster, count * cluster, buffer, Copied);
                    // The backup boot sector lives after the last cluster, outside the bitmap.
                    var tail = p.Size - clusters * cluster;
                    if (tail > 0) RawIo.Copy(src, p.Offset + clusters * cluster, dst, p.Offset + clusters * cluster, tail, buffer);
                }
                else
                {
                    status.Write(DiskProgressState.Progress, $"Copying {label} ({p.Size / Mb:N0} MB, every byte)", 0.1 + 0.8 * copiedBytes / Math.Max(1, totalBytes));
                    RawIo.Copy(src, p.Offset, dst, p.Offset, p.Size, buffer, Copied);
                }
            }
        }
        finally
        {
            ops.SetOffline(target.Number, false);
        }

        // 3. Use the extra space, if any.
        if (options.ExtendLastPartition && map.Count > 0 && target.Size > lastEnd + 16 * Mb)
        {
            var last = map.Last();
            if (last.Source.FileSystem is "NTFS")
            {
                status.Write(DiskProgressState.Progress, "Growing the last partition into the extra space", 0.92);
                WaitForVolumes(target.Number, map.Count);
                ops.ExtendToMaximum(target.Number, last.TargetNumber);
            }
        }

        // 4. Boot files, when the source holds Windows.
        if (options.MakeBootable)
        {
            var bootResult = BootFiles.RebuildIfWindows(ops, target.Number, map.Select(m => (m.Source, m.TargetNumber)).ToList(), status);
            if (bootResult is { } error)
            {
                status.Write(DiskProgressState.Failed, error);
                return DiskWorkerExit.Failed;
            }
        }

        status.Write(DiskProgressState.Progress, "Clone finished.", 0.99);
        return DiskWorkerExit.Ok;
    }

    /// <summary>After bringing the disk online, wait until Windows has mounted its volumes.</summary>
    internal static void WaitForVolumes(int disk, int partitions)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            var d = StorageInventory.GetDisks().FirstOrDefault(x => x.Number == disk);
            if (d is not null && d.Partitions.Count >= partitions && d.Partitions.Where(p => p.Role is PartitionRole.Data or PartitionRole.Windows).All(p => p.FileSystem is { Length: > 0 }))
                return;
            Thread.Sleep(500);
        }
    }
}

/// <summary>Rebuilds Windows boot files (bcdboot) on a disk that holds a copy of Windows.</summary>
public static class BootFiles
{
    /// <summary>Returns an error message, or null when done (or when the copy holds no Windows).</summary>
    public static string? RebuildIfWindows(StorageOperations ops, int disk, IReadOnlyList<(PartitionInfo Source, int TargetNumber)> map, StatusWriter status)
    {
        CloneEngine.WaitForVolumes(disk, map.Count);
        var current = StorageInventory.GetDisks().Single(d => d.Number == disk);
        var efi = current.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Efi);
        PartitionInfo? windows = null;
        var temp = new List<(int Partition, string Path)>();
        try
        {
            foreach (var p in current.Partitions.Where(p => p.FileSystem is "NTFS"))
            {
                var root = p.Root ?? Assign(ops, disk, p.PartitionNumber, temp);
                if (root is not null && File.Exists(Path.Combine(root, "Windows", "System32", "config", "SYSTEM")))
                {
                    windows = p with { DriveLetter = root[0] };
                    break;
                }
            }

            if (windows is null)
            {
                status.Write(DiskProgressState.Progress, "No Windows installation on this disk, so no boot files are needed.", 0.95);
                return null;
            }

            var gpt = current.PartitionStyle == "GPT";
            string? system;
            if (gpt)
            {
                if (efi is null) return "The copy has no EFI system partition, so Windows can't be made to start from it.";
                system = Assign(ops, disk, efi.PartitionNumber, temp);
            }
            else
            {
                system = windows.Root;
            }

            if (system is null) return "Couldn't give the boot partition a temporary drive letter.";
            status.Write(DiskProgressState.Progress, "Writing boot files", 0.96);
            var psi = new ProcessStartInfo("bcdboot.exe", $"{windows.DriveLetter}:\\Windows /s {system.TrimEnd('\\')} /f {(gpt ? "UEFI" : "BIOS")}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) return "bcdboot couldn't write the boot files: " + output.Trim();
            status.Write(DiskProgressState.Progress, "Boot files written: " + output.Trim(), 0.98);
            return null;
        }
        finally
        {
            foreach (var (partition, path) in temp)
            {
                try { ops.RemoveAccessPath(disk, partition, path); }
                catch (Exception) { }
            }
        }
    }

    private static string? Assign(StorageOperations ops, int disk, int partition, List<(int, string)> temp)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "RSTUVWXYZQP".FirstOrDefault(c => !used.Contains(c));
        if (letter == default) return null;
        var path = $"{letter}:\\";
        try
        {
            ops.AddAccessPath(disk, partition, path);
            temp.Add((partition, path));
            return path;
        }
        catch (Exception ex) when (ex is StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException)
        {
            return null;
        }
    }
}
