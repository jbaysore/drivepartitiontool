using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Clone;

internal interface ISmallerClonePlatform : IDisposable
{
    IReadOnlyDictionary<int, long> SnapshotAndMeasure(CloneLayout layout);
    void Capture(CloneLayout layout);
    void Prepare(CloneLayout layout);
    void CopyRaw(CloneLayout layout);
    void Apply(CloneLayout layout);
    void Boot();
    void Verify(CloneLayout layout);
}

internal static class SmallerCloneEngine
{
    internal static void Validate(DiskPlan plan, DiskInfo source, DiskInfo target, bool virtualOnly)
    {
        if (plan.Source is null || Fingerprint.Mismatch(plan.Source, source) is not null || Fingerprint.Mismatch(plan.Target, target) is not null)
            throw new ArgumentException("The clone disks no longer match the preview.");
        if (source.Number == target.Number || source.IsOffline || target.IsBoot || target.IsSystem || target.IsOffline || target.IsReadOnly)
            throw new ArgumentException("Choose distinct online disks and a writable destination that Windows is not using.");
        if (virtualOnly && (!source.IsVirtual || !target.IsVirtual)) throw new ArgumentException("This build only clones virtual disks.");
        var expected = $"DISK {target.Number}";
        if (!string.Equals(plan.ExpectedConfirmation, expected, StringComparison.OrdinalIgnoreCase) || !string.Equals(plan.Confirmation?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Type " + expected + " to confirm replacing the destination.");
        if (plan.Steps.Count != 1 || plan.Steps[0].Kind != DiskStepKind.CloneDisk) throw new ArgumentException("A clone plan must contain only its previewed clone step.");
    }

    internal static int Execute(DiskPlan plan, DiskInfo source, DiskInfo target, CloneOptions options, ISmallerClonePlatform platform, StatusWriter status, bool virtualOnly)
    {
        try
        {
            using (platform)
            {
                Validate(plan, source, target, virtualOnly);
                var layout = ClonePlanning.Layout(source, target.Size);
                if (!layout.UsesImages || string.IsNullOrWhiteSpace(options.TemporaryImageFolder)) throw new ArgumentException("A smaller destination needs a temporary image folder on a third disk.");
                var measured = platform.SnapshotAndMeasure(layout);
                layout = ClonePlanning.Layout(source, target.Size, measured);
                // Images must be complete and verified before the destination is erased.
                platform.Capture(layout);
                platform.Prepare(layout);
                platform.CopyRaw(layout);
                platform.Apply(layout);
                if (options.MakeBootable) platform.Boot();
                platform.Verify(layout);
                status.Write(DiskProgressState.Progress, "The smaller-disk copy is written. Booting it still needs verification.", 0.99);
                return DiskWorkerExit.Ok;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or OverflowException or System.ComponentModel.Win32Exception or
            StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException)
        {
            status.Write(DiskProgressState.Failed, "Smaller-destination clone stopped: " + ex.Message + " The source was not modified. If writing started, the destination may be incomplete.");
            return DiskWorkerExit.Failed;
        }
    }
}
