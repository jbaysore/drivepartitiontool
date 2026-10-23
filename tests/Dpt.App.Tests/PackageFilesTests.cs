using Dpt.App.Services;

namespace Dpt.App.Tests;

public class PackageFilesTests
{
    [Fact]
    public void A_locked_destination_is_reported_before_other_payload_files_are_replaced()
    {
        using var box = new Sandbox();
        var source = box.Dir("package"); var target = box.Dir("installed");
        File.WriteAllText(Path.Combine(source, "first.dll"), "new first");
        File.WriteAllText(Path.Combine(source, "locked.dll"), "new locked");
        File.WriteAllText(Path.Combine(target, "first.dll"), "old first");
        var locked = Path.Combine(target, "locked.dll"); File.WriteAllText(locked, "old locked");
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => PackageFiles.CopyDirectory(source, target));
            Assert.Equal("old first", File.ReadAllText(Path.Combine(target, "first.dll")));
        }
        PackageFiles.CopyDirectory(source, target);
        Assert.Equal("new first", File.ReadAllText(Path.Combine(target, "first.dll")));
        Assert.Equal("new locked", File.ReadAllText(locked));
    }
    [Fact]
    public void A_destination_inside_the_payload_cannot_create_a_recursive_copy()
    {
        using var box = new Sandbox();
        box.File("payload.dll", "content");
        Assert.Throws<IOException>(() => PackageFiles.CopyDirectory(box.Root, Path.Combine(box.Root, "nested")));
        Assert.False(Directory.Exists(Path.Combine(box.Root, "nested")));
    }
}
