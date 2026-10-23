using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Core.Boot;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.Tests;

public class BootHealthPreviewTests
{
    private static readonly DiskInfo Disk = new() { Number = 3, UniqueId = "boot", BusType = "File Backed Virtual", Size = 8000L * 1024 * 1024, IsBoot = true, IsSystem = true, PartitionStyle = "GPT",
        Partitions = [new() { PartitionNumber = 1, Role = PartitionRole.Efi, FileSystem = "FAT32", Size = 260L * 1024 * 1024, FreeSpace = 100L * 1024 * 1024 },
            new() { PartitionNumber = 2, Role = PartitionRole.Windows, FileSystem = "NTFS", DriveLetter = 'W' }] };
    [Fact]
    public async Task Unavailable_BCD_is_explained_and_the_repair_plan_stays_separate_from_read_only_health()
    {
        using var vm = new BootHealthViewModel(_ => Task.FromResult(new BootHealthSnapshot([Disk], new(false, "", "Administrator access through the background service is needed."))), true, true);
        await vm.RefreshAsync();
        Assert.Contains("disk 3", vm.Summary);
        Assert.Contains("Administrator", vm.ConfigurationNote);
        Assert.Empty(vm.Entries);
        Assert.Contains("100 MB", vm.BootPartitions.Single().Space);
        Assert.False(vm.CanRun);
        vm.Confirmation = "DISK 3";
        Assert.True(vm.CanRun);
        var plan = vm.BuildPlan();
        Assert.Equal(DiskStepKind.RebuildBootFiles, plan.Steps.Single().Kind);
        Assert.Equal(1, plan.Steps.Single().PartitionNumber);
        Assert.Equal(plan.Source!.Number, plan.Target.Number);
        Assert.Contains("does not format", vm.Preview);
    }
    [Fact]
    public async Task Locked_system_partitions_and_physical_development_targets_stay_blocked()
    {
        using var locked = new BootHealthViewModel(_ => Task.FromResult(new BootHealthSnapshot([Disk], new(false, "", "Unavailable"))), false, true);
        await locked.RefreshAsync(); locked.Confirmation = "DISK 3";
        Assert.False(locked.CanRun);
        Assert.Contains("Unlock", locked.Problem);
        using var physical = new BootHealthViewModel(_ => Task.FromResult(new BootHealthSnapshot([Disk with { BusType = "NVMe" }], new(false, "", "Unavailable"))), true, true);
        await physical.RefreshAsync(); physical.Confirmation = "DISK 3";
        Assert.False(physical.CanRun);
        Assert.Contains("virtual", physical.Problem);
    }
    [Fact]
    public async Task Localized_output_is_retained_without_inventing_a_health_conclusion()
    {
        using var vm = new BootHealthViewModel(_ => Task.FromResult(new BootHealthSnapshot([Disk], new(true, "Localized output", null))), true, true);
        await vm.RefreshAsync();
        Assert.Equal("Localized output", vm.RawConfiguration);
        Assert.Contains("no health conclusion", vm.ConfigurationNote);
    }
}
