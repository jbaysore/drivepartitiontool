using Dpt.Core.Boot;
using Dpt.Core.WindowsCopy;
using Dpt.DiskWorker.Windows;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.IntegrationTests;

public class BootRepairWorkflowTests
{
    private static readonly DiskInfo Disk = new() { Number = 4, UniqueId = "virtual", BusType = "File Backed Virtual", PartitionStyle = "GPT", Size = 10_000 * 1024L * 1024,
        Partitions = [new() { PartitionNumber = 1, Role = PartitionRole.Efi, FileSystem = "FAT32" }, new() { PartitionNumber = 2, Role = PartitionRole.Windows, FileSystem = "NTFS", DriveLetter = 'W' }] };
    private static DiskPlan Plan() => new() { Id = "repair", Title = "Repair", Source = Fingerprint.Of(Disk), Target = Fingerprint.Of(Disk), StatusFile = "unused", AllowSystemPartitions = true,
        Confirmation = "DISK 4", ExpectedConfirmation = "DISK 4", Steps = [new() { Kind = DiskStepKind.RebuildBootFiles, PartitionNumber = 1 }] };
    private sealed class Platform(string? failure = null) : IBootRepairPlatform
    {
        public List<string> Calls { get; } = [];
        private void Call(string name) { Calls.Add(name); if (failure == name) throw new IOException("Test failure: " + name); }
        public void CheckWindowsAndTools(BootRepairOptions options) => Call("check");
        public string SystemRoot(PartitionInfo partition) { Call("mount"); return @"S:\"; }
        public void Rebuild(BootRepairOptions options, string systemRoot) { Assert.Equal(@"S:\", systemRoot); Call("rebuild"); }
        public void Verify(BootFirmware firmware, string systemRoot) => Call("verify");
        public void Dispose() => Calls.Add("dispose");
    }

    [Fact]
    public void Same_disk_repair_is_allowed_without_allowing_same_disk_clones()
    {
        using var status = new StatusWriter("");
        var platform = new Platform();
        Assert.Equal(DiskWorkerExit.Ok, BootRepairEngine.Execute(Plan(), Disk, Disk, new() { SourceRoot = @"W:\" }, platform, status, true));
        Assert.Equal(["check", "mount", "rebuild", "verify", "dispose"], platform.Calls);
        Assert.True(PlanRunner.IsBootRepairOnly(Plan()));
        Assert.False(PlanRunner.IsBootRepairOnly(Plan() with { Steps = [new() { Kind = DiskStepKind.CloneDisk }] }));
    }
    [Theory]
    [InlineData("check")]
    [InlineData("mount")]
    [InlineData("rebuild")]
    [InlineData("verify")]
    public void Repair_failure_stops_and_releases_temporary_resources(string stage)
    {
        using var status = new StatusWriter("");
        var platform = new Platform(stage);
        Assert.Equal(DiskWorkerExit.Failed, BootRepairEngine.Execute(Plan(), Disk, Disk, new() { SourceRoot = @"W:\" }, platform, status, true));
        Assert.Equal([stage, "dispose"], platform.Calls.TakeLast(2));
    }
    [Fact]
    public void Confirmation_protection_disk_identity_and_partition_role_are_checked_before_any_platform_access()
    {
        using var status = new StatusWriter("");
        foreach (var plan in new[] { Plan() with { Confirmation = null, ExpectedConfirmation = null }, Plan() with { AllowSystemPartitions = false },
                     Plan() with { Target = Fingerprint.Of(Disk) with { UniqueId = "stale" } }, Plan() with { Steps = [new() { Kind = DiskStepKind.RebuildBootFiles, PartitionNumber = 2 }] } })
        {
            var platform = new Platform();
            Assert.Equal(DiskWorkerExit.Failed, BootRepairEngine.Execute(plan, Disk, Disk, new() { SourceRoot = @"W:\" }, platform, status, true));
            Assert.Equal(["dispose"], platform.Calls);
        }
        Assert.Throws<ArgumentException>(() => BootRepairValidation.Validate(Plan(), Disk, Disk with { BusType = "NVMe" }, new() { SourceRoot = @"W:\" }, true));
    }
}
