using System.Runtime.InteropServices;
using System.Text.Json;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Windows;

/// <summary>The privileged operations are replaceable so ordering, refusals and cleanup can be tested without elevation.</summary>
internal interface IWindowsCopyPlatform : IDisposable
{
    string OpenSnapshot();
    long Measure(string snapshot, IReadOnlyList<string> exclusions);
    void Prepare(WindowsCopyLayout layout);
    void Capture(string snapshot, string exclusionConfig);
    void Apply();
    void CopyRecovery();
    void WriteBootFiles();
    void Verify();
}

public static class WindowsCopyEngine
{
    public static void Register() => PlanRunner.AdvancedSteps[DiskStepKind.WindowsCopy] = Run;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out int type);

    public static bool IsUefi() => GetFirmwareType(out var type) && type is 1 or 2
        ? type == 2 : throw new IOException("Windows could not identify the firmware type. Choose it explicitly in the copy preview.");

    /// <summary>Pure preflight; no platform operations may happen before this succeeds.</summary>
    public static void ValidatePreflight(DiskPlan plan, DiskInfo source, DiskInfo target, WindowsCopyOptions options, bool virtualOnly)
    {
        WindowsCopyPlanning.Validate(options);
        if (plan.Source is null || Fingerprint.Mismatch(plan.Source, source) is { } || Fingerprint.Mismatch(plan.Target, target) is { })
            throw new ArgumentException("The source or destination no longer matches the preview.");
        if (source.Number == target.Number || target.IsBoot || target.IsSystem || target.IsReadOnly || target.IsOffline)
            throw new ArgumentException("Choose a different, writable destination disk that Windows is not running from.");
        if (virtualOnly && (!source.IsVirtual || !target.IsVirtual)) throw new ArgumentException("This build only copies between virtual disks.");
        var expected = $"DISK {target.Number}";
        if (!string.Equals(plan.ExpectedConfirmation, expected, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.Confirmation?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Type {expected} to confirm replacing the destination.");
        var part = source.Partitions.SingleOrDefault(p => string.Equals(p.Root, options.SourceRoot, StringComparison.OrdinalIgnoreCase));
        if (part is null || !string.Equals(part.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected source must be an NTFS volume on the source disk.");
        if (plan.Steps.Count != 1 || plan.Steps[0].Kind != DiskStepKind.WindowsCopy)
            throw new ArgumentException("A Windows-copy plan must contain only the previewed copy step.");
        // Reject an impossibly small layout even before taking a snapshot.
        WindowsCopyPlanning.Layout(options, target.Size, 0);
    }

    private static int Run(DiskPlan plan, DiskStep step, StatusWriter status)
    {
        try
        {
            var options = JsonSerializer.Deserialize<WindowsCopyOptions>(step.Options ?? "{}", PipeProtocol.Json)
                ?? throw new ArgumentException("Missing copy options.");
            var disks = StorageInventory.GetDisks();
            var source = disks.SingleOrDefault(d => d.Number == plan.Source?.Number) ?? throw new ArgumentException("Missing source disk.");
            var target = disks.SingleOrDefault(d => d.Number == plan.Target.Number) ?? throw new ArgumentException("Missing destination disk.");
            return Execute(plan, source, target, options, new WindowsCopyPlatform(source, target, options, status), status, SafetyMode.Current);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            status.Write(DiskProgressState.Refused, "Invalid Windows-copy plan: " + ex.Message);
            return DiskWorkerExit.BadArguments;
        }
    }

    internal static int Execute(DiskPlan plan, DiskInfo source, DiskInfo target, WindowsCopyOptions options,
        IWindowsCopyPlatform platform, StatusWriter status, bool virtualOnly)
    {
        try
        {
            using (platform)
            {
                ValidatePreflight(plan, source, target, options, virtualOnly);
                var exclusions = WindowsCopyPlanning.ValidateExclusions(options.Exclusions).Concat(WindowsContentClassifier.AlwaysExcludedNames).ToList();
                var snapshot = platform.OpenSnapshot();
                var selectedBytes = platform.Measure(snapshot, exclusions);
                var layout = WindowsCopyPlanning.Layout(options, target.Size, selectedBytes);
                // Everything above is read-only on the source. Destination erasure begins here, after all preflight checks.
                platform.Prepare(layout);
                platform.Capture(snapshot, WindowsCopyPlanning.ExclusionConfig(options));
                platform.Apply();
                platform.CopyRecovery();
                if (options.MakeBootable) platform.WriteBootFiles();
                platform.Verify();
                status.Write(DiskProgressState.Progress, "The copy is written and its Windows files are readable. Booting it still needs verification.", 0.99);
                return DiskWorkerExit.Ok;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or OverflowException or
            StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException or System.ComponentModel.Win32Exception)
        {
            status.Write(DiskProgressState.Failed, "Copying Windows stopped: " + ex.Message + " The source was not modified. If writing had started, the destination may contain an incomplete copy.");
            return DiskWorkerExit.Failed;
        }
    }
}
