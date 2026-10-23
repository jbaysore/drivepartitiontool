using Dpt.Core.WindowsCopy;
using Dpt.DiskWorker.Windows;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.IntegrationTests;

/// <summary>No disk or privileged process access: this fixture exercises the production orchestration through a fake platform.</summary>
public class WindowsCopyWorkflowTests
{
    private static readonly DiskInfo Source = new() { Number = 3, UniqueId = "source", BusType = "File Backed Virtual", Size = 10_000 * WindowsCopyPlanning.Mb,
        Partitions = [new() { DriveLetter = 'X', FileSystem = "NTFS", Size = 8000 * WindowsCopyPlanning.Mb }] };
    private static readonly DiskInfo Target = new() { Number = 4, UniqueId = "target", BusType = "File Backed Virtual", Size = 10_000 * WindowsCopyPlanning.Mb };
    private static readonly WindowsCopyOptions Options = new() { SourceRoot = @"X:\", Exclusions = ["Games"] };
    private static DiskPlan Plan() => new() { Id = "test", Title = "Copy", Source = Fingerprint.Of(Source), Target = Fingerprint.Of(Target),
        StatusFile = "unused", Steps = [new() { Kind = DiskStepKind.WindowsCopy }], ExpectedConfirmation = "DISK 4", Confirmation = "DISK 4" };

    private sealed class Platform(string? failAt = null, long bytes = 100, long? recoveryBytes = null) : IWindowsCopyPlatform
    {
        public List<string> Calls { get; } = [];
        public WindowsCopyLayout? Layout { get; private set; }
        private void Call(string name) { Calls.Add(name); if (name == failAt) throw new IOException("test failure at " + name); }
        public string OpenSnapshot() { Call("snapshot"); return "snapshot"; }
        public WindowsCopyMeasurement Measure(string snapshot, IReadOnlyList<string> exclusions) { Assert.Contains("Games", exclusions); Call("measure"); return new(bytes, recoveryBytes); }
        public void Prepare(WindowsCopyLayout layout) { Assert.Equal("Recovery", layout.Partitions.Last().Role); Layout = layout; Call("prepare"); }
        public void Capture(string snapshot, string config) { Assert.Contains("\\Games", config); Call("capture"); }
        public void Apply() => Call("apply");
        public void CopyRecovery() => Call("recovery");
        public void WriteBootFiles() => Call("boot");
        public void Verify() => Call("verify");
        public void Dispose() => Calls.Add("dispose");
    }

    [Fact]
    public void Successful_copy_measures_before_erasing_and_verifies_before_cleanup()
    {
        var platform = new Platform();
        using var status = new StatusWriter("");
        Assert.Equal(DiskWorkerExit.Ok, WindowsCopyEngine.Execute(Plan(), Source, Target, Options, platform, status, true));
        Assert.Equal(["snapshot", "measure", "prepare", "capture", "apply", "recovery", "boot", "verify", "dispose"], platform.Calls);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("measure")]
    [InlineData("prepare")]
    [InlineData("capture")]
    [InlineData("apply")]
    [InlineData("recovery")]
    [InlineData("boot")]
    [InlineData("verify")]
    public void Failure_always_releases_resources_and_never_continues_to_the_next_stage(string stage)
    {
        var platform = new Platform(stage);
        using var status = new StatusWriter("");
        Assert.Equal(DiskWorkerExit.Failed, WindowsCopyEngine.Execute(Plan(), Source, Target, Options, platform, status, true));
        Assert.Equal("dispose", platform.Calls.Last());
        Assert.Equal(stage, platform.Calls[^2]);
        if (stage is "snapshot" or "measure") Assert.DoesNotContain("prepare", platform.Calls);
        Assert.Contains(status.Written, p => p.State == DiskProgressState.Failed && p.Message.Contains("source was not modified"));
    }

    [Fact]
    public void Missing_confirmation_or_capacity_cannot_erase_the_destination()
    {
        using var status = new StatusWriter("");
        var missing = new Platform();
        Assert.Equal(DiskWorkerExit.Failed, WindowsCopyEngine.Execute(Plan() with { ExpectedConfirmation = null, Confirmation = null }, Source, Target, Options, missing, status, true));
        Assert.Equal(["dispose"], missing.Calls);
        var tooMuch = new Platform(bytes: Target.Size);
        Assert.Equal(DiskWorkerExit.Failed, WindowsCopyEngine.Execute(Plan(), Source, Target, Options, tooMuch, status, true));
        Assert.DoesNotContain("prepare", tooMuch.Calls);
    }

    [Fact]
    public void Recovery_measurement_grows_the_partition_or_refuses_capacity_before_erasing()
    {
        using var status = new StatusWriter("");
        var platform = new Platform(recoveryBytes: 1200 * WindowsCopyPlanning.Mb);
        Assert.Equal(DiskWorkerExit.Ok, WindowsCopyEngine.Execute(Plan(), Source, Target, Options, platform, status, true));
        Assert.Equal(1450 * WindowsCopyPlanning.Mb, platform.Layout!.Partitions.Last().Size);
        var oversized = new Platform(recoveryBytes: 10_000 * WindowsCopyPlanning.Mb);
        Assert.Equal(DiskWorkerExit.Failed, WindowsCopyEngine.Execute(Plan(), Source, Target, Options, oversized, status, true));
        Assert.DoesNotContain("prepare", oversized.Calls);
    }

    [Fact]
    public void Physical_protected_stale_and_same_disk_plans_are_refused_by_pure_preflight()
    {
        Assert.Throws<ArgumentException>(() => WindowsCopyEngine.ValidatePreflight(Plan(), Source, Target with { BusType = "NVMe" }, Options, true));
        Assert.Throws<ArgumentException>(() => WindowsCopyEngine.ValidatePreflight(Plan(), Source, Target with { IsBoot = true }, Options, false));
        Assert.Throws<ArgumentException>(() => WindowsCopyEngine.ValidatePreflight(Plan(), Source, Target with { UniqueId = "replacement" }, Options, false));
        Assert.Throws<ArgumentException>(() => WindowsCopyEngine.ValidatePreflight(Plan() with { Source = Fingerprint.Of(Target) }, Target, Target, Options, true));
        Assert.Throws<ArgumentException>(() => WindowsCopyEngine.ValidatePreflight(Plan(), Source, Target, Options with { Exclusions = ["Windows"] }, true));
    }
}
