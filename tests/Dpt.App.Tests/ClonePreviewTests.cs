using System.Text.Json;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.Tests;

public class ClonePreviewTests
{
    [Fact]
    public void Smaller_destination_requires_staging_and_reconfirmation_and_emits_the_reviewed_path()
    {
        AppServices.Initialize(); using var box = new Sandbox();
        const long mb = ClonePlanning.Mb;
        var source = new DiskInfo { Number = 3, UniqueId = "source", Size = 1000 * mb, PartitionStyle = "GPT", BusType = "File Backed Virtual", Partitions = [
            new() { PartitionNumber = 1, Offset = mb, Size = 998 * mb, FileSystem = "NTFS", VolumePath = @"\\?\Volume{source}\", VolumeSize = 998 * mb, FreeSpace = 898 * mb, GptType = StorageInventory.GptBasicData } ] };
        var target = new DiskInfo { Number = 4, UniqueId = "target", Size = 500 * mb, PartitionStyle = "GPT", BusType = "File Backed Virtual" };
        var vm = new CloneViewModel(source, [source, target, target with { Number = 5, IsSystem = true }]);
        Assert.Single(vm.Destinations); Assert.True(vm.NeedsImages);
        vm.Confirmation = "DISK 4";
        Assert.False(vm.CanRun); Assert.Throws<InvalidOperationException>(vm.BuildPlan);
        vm.TemporaryImageFolder = box.Root;
        Assert.Empty(vm.Confirmation); Assert.Contains("third disk", vm.SizeNote);
        vm.Confirmation = "DISK 4"; Assert.True(vm.CanRun);
        using var json = JsonDocument.Parse(vm.BuildPlan().Steps.Single().Options!);
        Assert.Equal(box.Root, json.RootElement.GetProperty("temporaryImageFolder").GetString());
        vm.ExtendLast = !vm.ExtendLast; Assert.Empty(vm.Confirmation); Assert.False(vm.CanRun);
    }
}
