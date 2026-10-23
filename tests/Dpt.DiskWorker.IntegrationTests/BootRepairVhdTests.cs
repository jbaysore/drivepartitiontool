using System.Security.Cryptography;
using System.Text.Json;
using Dpt.Core.Boot;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;
using Dpt.TestSupport;

namespace Dpt.DiskWorker.IntegrationTests;

public sealed class BootFixtureFactAttribute : FactAttribute
{
    public BootFixtureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DPT_BOOT_TEST_WINDOWS_ROOT")))
            Skip = "Needs an attached, disposable Windows VHD source: set DPT_BOOT_TEST_WINDOWS_ROOT. See docs/PROGRESS.md; this is deferred, not a boot proof.";
    }
}

/// <summary>Prepared for the later admin phase. Never uses the host Windows install as its source or destination.</summary>
[Trait("Category", "Elevated")]
public class BootRepairVhdTests
{
    [BootFixtureFact]
    public void Rebuilds_only_the_test_vhd_efi_and_preserves_the_previous_bcd_and_source_template()
    {
        var root = Environment.GetEnvironmentVariable("DPT_BOOT_TEST_WINDOWS_ROOT")!;
        var options = new BootRepairOptions { SourceRoot = root, Firmware = BootFirmware.Uefi };
        BootRepairPlanning.Validate(options);
        var source = StorageInventory.GetDisks().Single(d => d.Partitions.Any(p => string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase)));
        Assert.True(source.IsVirtual, "The boot fixture source must be File Backed Virtual, never the host Windows disk.");
        Assert.False(source.IsBoot || source.IsSystem, "Use a separate attached test Windows VHD, not a running/system disk.");
        var template = Path.Combine(root, @"Windows\System32\config\BCD-Template");
        Assert.True(File.Exists(template), "The test VHD must contain an actual Windows installation.");
        var before = SHA256.HashData(File.ReadAllBytes(template));
        var sourceLayout = Fingerprint.Of(source);

        using var target = TestVhd.CreateSystemLike(2048, "DPTBOOTFIX");
        var disk = StorageInventory.GetDisks().Single(d => d.Number == target.DiskNumber);
        Assert.True(disk.IsVirtual);
        var efi = disk.Partitions.Single(p => p.Role == PartitionRole.Efi);
        DiskPlan Plan() => Plans.For(disk, new DiskStep { Kind = DiskStepKind.RebuildBootFiles, PartitionNumber = efi.PartitionNumber, Options = JsonSerializer.Serialize(options, PipeProtocol.Json) }) with
        { Source = sourceLayout, AllowSystemPartitions = true, Confirmation = $"DISK {disk.Number}", ExpectedConfirmation = $"DISK {disk.Number}" };
        var first = Plans.RunWorker(Plan());
        Assert.True(first.Code == DiskWorkerExit.Ok, string.Join(" | ", first.Progress.Select(p => p.Message)));
        var system = TestVhd.EnsureLetter(disk.Number, efi.PartitionNumber);
        var bcd = Path.Combine(system, @"EFI\Microsoft\Boot\BCD");
        Assert.True(File.Exists(bcd));
        var bcdBefore = SHA256.HashData(File.ReadAllBytes(bcd));
        var second = Plans.RunWorker(Plan());
        Assert.True(second.Code == DiskWorkerExit.Ok, string.Join(" | ", second.Progress.Select(p => p.Message)));
        var backup = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(bcd)!, "BCD.dpt-backup-*"));
        Assert.Equal(bcdBefore, SHA256.HashData(File.ReadAllBytes(backup)));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(template)));
        Assert.Null(Fingerprint.Mismatch(sourceLayout, StorageInventory.GetDisks().Single(d => d.Number == source.Number)));
    }
}
