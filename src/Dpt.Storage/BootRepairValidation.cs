using Dpt.Core.Boot;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;

namespace Dpt.Storage;

/// <summary>Shared pure repair checks. This method performs no device or file access.</summary>
public static class BootRepairValidation
{
    public static PartitionInfo Validate(DiskPlan plan, DiskInfo source, DiskInfo target, BootRepairOptions options, bool virtualOnly)
    {
        BootRepairPlanning.Validate(options);
        if (plan.Source is null || Fingerprint.Mismatch(plan.Source, source) is not null || Fingerprint.Mismatch(plan.Target, target) is not null)
            throw new ArgumentException("The source or boot partition no longer matches the preview.");
        if (target.IsOffline || target.IsReadOnly || (virtualOnly && (!source.IsVirtual || !target.IsVirtual)))
            throw new ArgumentException("The destination must be writable; development/test builds only use virtual disks.");
        if (!source.Partitions.Any(p => string.Equals(p.Root, options.SourceRoot, StringComparison.OrdinalIgnoreCase) && string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The Windows source is not an NTFS volume on the selected source disk.");
        if (plan.Steps.Count != 1 || plan.Steps[0].Kind != DiskStepKind.RebuildBootFiles)
            throw new ArgumentException("A boot-repair plan must contain only the previewed boot-files step.");
        var partition = target.Partitions.SingleOrDefault(p => p.PartitionNumber == plan.Steps[0].PartitionNumber)
            ?? throw new ArgumentException("The chosen boot partition is missing.");
        if (partition.IsProtected && !plan.AllowSystemPartitions)
            throw new ArgumentException("Unlock system partitions in Settings → Disk tools, then reopen this preview. Extra confirmation is still required.");
        if (options.Firmware == BootFirmware.Uefi && (partition.Role != PartitionRole.Efi || !string.Equals(partition.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("UEFI boot files need an EFI partition whose FAT32 format can be verified.");
        if (options.Firmware == BootFirmware.Bios && (target.PartitionStyle != "MBR" || !partition.IsActive || !string.Equals(partition.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("BIOS boot files need an active NTFS partition on an MBR disk.");
        var expected = $"DISK {target.Number}";
        if (!string.Equals(plan.ExpectedConfirmation, expected, StringComparison.OrdinalIgnoreCase) || !string.Equals(plan.Confirmation?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Type {expected} to confirm writing the selected boot partition.");
        return partition;
    }
}
