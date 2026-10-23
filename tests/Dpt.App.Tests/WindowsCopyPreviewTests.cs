using System.Text.Json;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.Tests;

public class WindowsCopyPreviewTests
{
    private static DiskInfo Disk(int number, char letter, bool windows = false) => new()
    {
        Number = number, UniqueId = "disk" + number, BusType = "File Backed Virtual", Size = 10_000 * WindowsCopyPlanning.Mb,
        PartitionStyle = "GPT", Partitions = [new() { DiskNumber = number, PartitionNumber = 1, DriveLetter = letter, FileSystem = "NTFS", Size = 9000 * WindowsCopyPlanning.Mb, Role = windows ? PartitionRole.Windows : PartitionRole.Data }],
    };
    private static readonly WindowsCopyInventory Inventory = new([
        new(new(ContentGroup.Windows, "Windows", "Windows itself", null, true), 100, true),
        new(new(ContentGroup.PersonalFolder, @"Users\Alice\Pictures", "Alice · Pictures", "Alice", false), 200, true),
        new(new(ContentGroup.AlwaysExcluded, "pagefile.sys", "pagefile.sys", null, false), 0, true),
    ], []);

    [Fact]
    public async Task Preview_requires_loaded_choices_and_destination_confirmation_and_builds_the_reviewed_plan()
    {
        var source = Disk(1, 'X', true) with { IsBoot = true };
        var target = Disk(2, 'Y');
        using var vm = new WindowsCopyViewModel([source, target, Disk(3, 'Z') with { IsSystem = true }], (_, _, _) => Task.FromResult(Inventory), true);
        Assert.Single(vm.Destinations);
        vm.Confirmation = "DISK 2";
        Assert.False(vm.CanRun);
        Assert.Throws<InvalidOperationException>(vm.BuildPlan);
        await vm.LoadAsync();
        var personal = vm.Items.Single(i => i.Item.Group == ContentGroup.PersonalFolder);
        personal.Selected = false;
        Assert.Empty(vm.Confirmation);
        Assert.Equal([@"X:\Users\Alice\Pictures"], vm.ExcludedPersonalPaths);
        Assert.Contains("Recovery", vm.LayoutText);
        Assert.Equal("Stays only on source", personal.Disposition);
        vm.Confirmation = "DISK 2";
        Assert.True(vm.CanRun);
        var plan = vm.BuildPlan();
        Assert.Equal(2, plan.Target.Number);
        Assert.Equal(1, plan.Source!.Number);
        var options = JsonSerializer.Deserialize<WindowsCopyOptions>(plan.Steps.Single().Options!, PipeProtocol.Json)!;
        Assert.Contains(@"Users\Alice\Pictures", options.Exclusions);
        Assert.DoesNotContain("Windows", options.Exclusions);
        vm.Items.Single(i => i.Item.Required).Selected = false;
        Assert.True(vm.Items.Single(i => i.Item.Required).Selected);
        vm.Items.Single(i => i.Item.Group == ContentGroup.AlwaysExcluded).Selected = true;
        Assert.False(vm.Items.Single(i => i.Item.Group == ContentGroup.AlwaysExcluded).Selected);
    }

    [Fact]
    public async Task A_late_scan_cannot_replace_the_new_sources_choices()
    {
        var pending = new TaskCompletionSource<WindowsCopyInventory>();
        using var vm = new WindowsCopyViewModel([Disk(1, 'X', true), Disk(2, 'Y')], (_, _, _) => pending.Task, true);
        var loading = vm.LoadAsync();
        vm.Source = vm.Sources.Single(s => s.Root == @"Y:\");
        pending.SetResult(Inventory);
        await loading;
        Assert.Empty(vm.Items);
        Assert.False(vm.CanRun);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task Capacity_and_virtual_only_checks_still_block_a_typed_confirmation()
    {
        using var small = new WindowsCopyViewModel([Disk(1, 'X', true), Disk(2, 'Y') with { Size = 100 * WindowsCopyPlanning.Mb }], (_, _, _) => Task.FromResult(Inventory), true);
        await small.LoadAsync(); small.Confirmation = "DISK 2";
        Assert.False(small.CanRun);
        using var physical = new WindowsCopyViewModel([Disk(1, 'X', true), Disk(2, 'Y') with { BusType = "NVMe" }], (_, _, _) => Task.FromResult(Inventory), true);
        await physical.LoadAsync(); physical.Confirmation = "DISK 2";
        Assert.False(physical.CanRun);
        Assert.Contains("virtual disks", physical.Problem);
    }

    [Fact]
    public void Discovery_keeps_required_branches_and_sizes_the_selected_temp_tree()
    {
        using var box = new Sandbox();
        box.File(@"Windows\System32\config\SYSTEM", "windows");
        box.File(@"ProgramData\Microsoft\Windows\state", "required");
        box.File(@"ProgramData\Vendor\state", "optional");
        box.File(@"Users\Alice\NTUSER.DAT", "user");
        box.File(@"Users\Alice\AppData\Local\Microsoft\Windows\UsrClass.dat", "hive");
        box.File(@"Users\Alice\Pictures\photo.jpg", "pixels");
        box.File(@"Tools\Editor\app.exe", "app");
        var inventory = WindowsCopyDiscovery.Scan(box.Root, programs: [new("My editor", @"Tools\Editor")]);
        Assert.True(inventory.Items.Single(i => i.Item.RelativePath == @"ProgramData\Microsoft").Item.Required);
        Assert.True(inventory.Items.Single(i => i.Item.RelativePath == @"Users\Alice\AppData\Local\Microsoft").Item.Required);
        Assert.Equal(6, inventory.Items.Single(i => i.Item.RelativePath == @"Users\Alice\Pictures").Bytes);
        Assert.Contains(inventory.Items, i => i.Item.Title == "My editor" && i.Item.Group == ContentGroup.Programs);
        Assert.All(inventory.Items, i => Assert.True(i.Complete));
    }

    [Fact]
    public void A_backup_alias_cannot_place_personal_data_on_the_source_or_the_disk_to_be_erased()
    {
        DiskInfo WithVolume(int number, string volume) => Disk(number, (char)('W' + number)) with { Partitions = [new() { VolumePath = volume }] };
        var source = WithVolume(1, @"\\?\Volume{source}\");
        var target = WithVolume(2, @"\\?\Volume{target}\");
        var backup = WithVolume(3, @"\\?\Volume{backup}\");
        Assert.NotNull(WindowsCopyBackup.Problem(@"\\?\Volume{source}\backup", [source, target, backup], 1, 2));
        Assert.NotNull(WindowsCopyBackup.Problem(@"\\?\Volume{target}\backup", [source, target, backup], 1, 2));
        Assert.NotNull(WindowsCopyBackup.Problem(@"\\server\unknown\backup", [source, target, backup], 1, 2));
        Assert.Null(WindowsCopyBackup.Problem(@"\\?\Volume{backup}\chosen-folder", [source, target, backup], 1, 2));
        using var box = new Sandbox();
        Assert.StartsWith(@"\\?\Volume{", WindowsCopyBackup.ResolveFolder(box.Root));
    }
}
