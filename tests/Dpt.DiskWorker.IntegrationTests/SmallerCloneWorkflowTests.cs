using Dpt.DiskWorker.Clone;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.IntegrationTests;

/// <summary>Pure/fake-platform coverage only: no VHD, raw handle, VSS or privileged tool is opened.</summary>
public class SmallerCloneWorkflowTests
{
    private const long Mb = ClonePlanning.Mb;
    private static readonly DiskInfo Source = new() { Number = 3, Size = 1024 * Mb, UniqueId = "source", PartitionStyle = "GPT", BusType = "File Backed Virtual", Partitions = [
        new() { PartitionNumber = 1, Offset = Mb, Size = 64 * Mb, Role = PartitionRole.Efi, GptType = StorageInventory.GptEfi, FileSystem = "FAT32" },
        new() { PartitionNumber = 2, Offset = 65 * Mb, Size = 16 * Mb, Role = PartitionRole.Reserved, GptType = StorageInventory.GptMsr },
        new() { PartitionNumber = 3, Offset = 81 * Mb, Size = 900 * Mb, Role = PartitionRole.Data, GptType = StorageInventory.GptBasicData, FileSystem = "NTFS", VolumePath = @"\\?\Volume{source}\", VolumeSize = 900 * Mb, FreeSpace = 800 * Mb },
        new() { PartitionNumber = 4, Offset = 981 * Mb, Size = 32 * Mb, Role = PartitionRole.Recovery, GptType = StorageInventory.GptRecovery, FileSystem = "NTFS" },
    ] };
    private static readonly DiskInfo Target = new() { Number = 4, Size = 500 * Mb, UniqueId = "target", PartitionStyle = "GPT", BusType = "File Backed Virtual" };
    private static DiskPlan Plan() => new() { Id = "test", Title = "Clone", Source = Fingerprint.Of(Source), Target = Fingerprint.Of(Target), Steps = [new() { Kind = DiskStepKind.CloneDisk }],
        StatusFile = "unused", ExpectedConfirmation = "DISK 4", Confirmation = "disk 4" };
    private static readonly CloneOptions Options = new() { TemporaryImageFolder = @"S:\temporary-images" };

    [Fact]
    public void Data_disk_cloning_does_not_require_a_Windows_boot_code_tool()
    {
        var data = new DiskInfo { PartitionStyle = "MBR", Partitions = [new() { Role = PartitionRole.Data, FileSystem = "NTFS" }] };
        Assert.Empty(BootFiles.ToolsFor(data));
        var windows = data with { Partitions = [new() { Role = PartitionRole.Windows, FileSystem = "NTFS" }] };
        Assert.Equal(["bcdboot.exe", "bootsect.exe"], BootFiles.ToolsFor(windows));
        Assert.Equal(["bcdboot.exe"], BootFiles.ToolsFor(windows with { PartitionStyle = "GPT" }));
    }

    private sealed class Platform(string? fail = null, long bytes = 100 * Mb) : ISmallerClonePlatform
    {
        public List<string> Calls { get; } = [];
        private void Call(string stage) { Calls.Add(stage); if (stage == fail) throw new IOException("fixture " + stage); }
        public IReadOnlyDictionary<int, long> SnapshotAndMeasure(CloneLayout layout) { Call("snapshot"); return new Dictionary<int, long> { [3] = bytes }; }
        public void Capture(CloneLayout layout) => Call("capture");
        public void Prepare(CloneLayout layout) { Assert.Equal([false, false, true, false], layout.Partitions.Select(p => p.Capture)); Call("prepare"); }
        public void CopyRaw(CloneLayout layout) => Call("raw");
        public void Apply(CloneLayout layout) => Call("apply");
        public void Boot() => Call("boot");
        public void Verify(CloneLayout layout) => Call("verify");
        public void Dispose() => Calls.Add("dispose");
    }

    [Fact]
    public void Layout_keeps_fixed_partitions_places_recovery_last_and_shrinks_only_file_imaged_ntfs()
    {
        var layout = ClonePlanning.Layout(Source, Target.Size);
        Assert.True(layout.UsesImages);
        Assert.Equal(Source.Partitions.Select(p => p.PartitionNumber), layout.Partitions.Select(p => p.Source.PartitionNumber));
        Assert.Equal(386 * Mb, layout.Partitions[2].Size);
        Assert.Equal(32 * Mb, layout.Partitions[3].Size);
        Assert.Equal(499 * Mb, layout.Partitions.Last().Offset + layout.Partitions.Last().Size);
        Assert.All(layout.Partitions, p => Assert.Equal(0, p.Offset % Mb));
        Assert.Equal(164 * Mb, layout.StagingBytes);
        var raw = ClonePlanning.Layout(Source, 1200 * Mb);
        Assert.False(raw.UsesImages);
        Assert.All(raw.Partitions, p => { Assert.False(p.Capture); Assert.Equal(p.Source.Offset, p.Offset); Assert.Equal(p.Source.Size, p.Size); });
    }

    [Fact]
    public void Snapshot_logical_sizes_can_refuse_a_disk_that_looked_large_enough_from_allocation_data()
    {
        using var status = new StatusWriter(""); var platform = new Platform(bytes: 600 * Mb);
        Assert.Equal(DiskWorkerExit.Failed, SmallerCloneEngine.Execute(Plan(), Source, Target, Options, platform, status, true));
        Assert.Equal(["snapshot", "dispose"], platform.Calls);
        Assert.Throws<ArgumentException>(() => ClonePlanning.Layout(Source, Target.Size, new Dictionary<int, long>()));
        Assert.Throws<ArgumentException>(() => ClonePlanning.Layout(Source with { Partitions = Source.Partitions.Select(p => p.PartitionNumber == 3 ? p with { FreeSpace = null } : p).ToList() }, Target.Size));
    }

    [Fact]
    public void All_images_are_complete_before_erasure_and_every_stage_verifies_and_cleans_up()
    {
        using var status = new StatusWriter(""); var platform = new Platform();
        Assert.Equal(DiskWorkerExit.Ok, SmallerCloneEngine.Execute(Plan(), Source, Target, Options, platform, status, true));
        Assert.Equal(["snapshot", "capture", "prepare", "raw", "apply", "boot", "verify", "dispose"], platform.Calls);
    }

    [Theory]
    [InlineData("snapshot")][InlineData("capture")][InlineData("prepare")][InlineData("raw")][InlineData("apply")][InlineData("boot")][InlineData("verify")]
    public void Failure_never_continues_and_always_cleans_up(string stage)
    {
        using var status = new StatusWriter(""); var platform = new Platform(stage);
        Assert.Equal(DiskWorkerExit.Failed, SmallerCloneEngine.Execute(Plan(), Source, Target, Options, platform, status, true));
        Assert.Equal(stage, platform.Calls[^2]); Assert.Equal("dispose", platform.Calls.Last());
        if (stage is "snapshot" or "capture") Assert.DoesNotContain("prepare", platform.Calls);
    }

    [Fact]
    public void Confirmation_identity_protection_single_step_and_staging_refusals_precede_platform_access()
    {
        using var status = new StatusWriter(""); var platform = new Platform();
        Assert.Equal(DiskWorkerExit.Failed, SmallerCloneEngine.Execute(Plan() with { ExpectedConfirmation = null }, Source, Target, Options, platform, status, true));
        Assert.Equal(["dispose"], platform.Calls);
        Assert.Throws<ArgumentException>(() => SmallerCloneEngine.Validate(Plan(), Source, Target with { IsSystem = true }, true));
        Assert.Throws<ArgumentException>(() => SmallerCloneEngine.Validate(Plan(), Source, Target with { UniqueId = "different" }, true));
        Assert.Throws<ArgumentException>(() => SmallerCloneEngine.Validate(Plan(), Source with { BusType = "NVMe" }, Target, true));
        Assert.Throws<ArgumentException>(() => SmallerCloneEngine.Validate(Plan() with { Steps = [new() { Kind = DiskStepKind.CloneDisk }, new() { Kind = DiskStepKind.Probe }] }, Source, Target, true));
        Assert.Throws<ArgumentException>(() => SmallerClonePlatform.ValidateStage(Source, 3, 4, true));
        Assert.Throws<ArgumentException>(() => SmallerClonePlatform.ValidateStage(Target, 3, 4, true));
        Assert.Throws<ArgumentException>(() => SmallerClonePlatform.ValidateStage(Target with { Number = 5, BusType = "USB" }, 3, 4, true));
        SmallerClonePlatform.ValidateStage(Target with { Number = 5 }, 3, 4, true);
    }
}
