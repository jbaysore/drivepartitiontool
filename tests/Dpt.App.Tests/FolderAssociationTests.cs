using Dpt.App.Services;
using Dpt.Core.Navigation;
using Microsoft.Win32;

namespace Dpt.App.Tests;

/// <summary>Writes only a GUID-named test key under Software\DrivePartitionTool.Tests, never the live Classes tree.</summary>
public sealed class FolderAssociationTests : IDisposable
{
    private readonly string _path = @"Software\DrivePartitionTool.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    public FolderAssociationTests() => _root = Registry.CurrentUser.CreateSubKey(_path);
    private FolderAssociationService Service(string exe = @"C:\Program Files\DPT\DrivePartitionTool.exe", Action? notify = null) => new(_root, "Classes", "Backup", exe, notify ?? (() => { }));

    [Fact]
    public void Enable_and_disable_restore_typed_defaults_missing_values_and_existing_commands()
    {
        using (var shell = _root.CreateSubKey(@"Classes\Folder\shell")) shell.SetValue("", "%OTHER_FOLDER_VERB%", RegistryValueKind.ExpandString);
        using (var existing = _root.CreateSubKey(@"Classes\Folder\shell\open\command")) existing.SetValue("", "existing-open");
        var notices = 0;
        var service = Service(notify: () => notices++);
        Assert.False(service.IsEnabled);
        service.SetEnabled(true);
        Assert.True(service.IsEnabled);
        using (var command = _root.OpenSubKey(@"Classes\Folder\shell\DrivePartitionTool.Open\command"))
            Assert.Equal("\"C:\\Program Files\\DPT\\DrivePartitionTool.exe\" \"%1\\.\"", command!.GetValue(""));
        service.SetEnabled(true); // must not replace the original backup with our own default
        Assert.Null(service.SetEnabled(false));
        Assert.False(service.IsEnabled);
        using var restored = _root.OpenSubKey(@"Classes\Folder\shell");
        Assert.Equal(RegistryValueKind.ExpandString, restored!.GetValueKind(""));
        Assert.Equal("%OTHER_FOLDER_VERB%", restored.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        using var original = restored.OpenSubKey(@"open\command");
        Assert.Equal("existing-open", original!.GetValue(""));
        Assert.Null(_root.OpenSubKey(@"Classes\Directory"));
        Assert.Null(_root.OpenSubKey(@"Classes\Drive"));
        Assert.Null(_root.OpenSubKey("Backup"));
        Assert.Equal(3, notices);
    }

    [Fact]
    public void Disabling_keeps_later_external_changes_instead_of_overwriting_them()
    {
        var service = Service(); service.SetEnabled(true);
        using (var shell = _root.OpenSubKey(@"Classes\Folder\shell", true)) shell!.SetValue("", "OtherApp.Open");
        using (var command = _root.OpenSubKey(@"Classes\Drive\shell\DrivePartitionTool.Open\command", true)) command!.SetValue("", "other-command");
        Assert.False(service.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true));
        Assert.Contains("kept", service.SetEnabled(false));
        using var folder = _root.OpenSubKey(@"Classes\Folder\shell");
        Assert.Equal("OtherApp.Open", folder!.GetValue(""));
        using var changed = _root.OpenSubKey(@"Classes\Drive\shell\DrivePartitionTool.Open\command");
        Assert.Equal("other-command", changed!.GetValue(""));
        using var drive = _root.OpenSubKey(@"Classes\Drive\shell");
        Assert.DoesNotContain("", drive!.GetValueNames());
    }

    [Fact]
    public void Moving_the_app_updates_the_owned_command_and_keeps_the_original_restore_point()
    {
        Service().SetEnabled(true);
        var moved = Service(@"D:\Tools\DrivePartitionTool.exe");
        moved.SetEnabled(true);
        Assert.True(moved.IsEnabled);
        moved.SetEnabled(false);
        Assert.Null(_root.OpenSubKey(@"Classes\Folder"));
    }

    [Fact]
    public void A_colliding_verb_or_missing_backup_is_not_silently_overwritten()
    {
        using (var key = _root.CreateSubKey(@"Classes\Folder\shell\DrivePartitionTool.Open\command")) key.SetValue("", "not-owned");
        Assert.Throws<InvalidOperationException>(() => Service().SetEnabled(true));
        Assert.Null(_root.OpenSubKey("Backup"));
        using (var key = _root.OpenSubKey(@"Classes\Folder\shell", true)) key!.DeleteSubKeyTree("DrivePartitionTool.Open");
        var service = Service(); service.SetEnabled(true);
        _root.DeleteSubKeyTree("Backup");
        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(false));
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void Quoted_folder_arguments_round_trip_spaces_symbols_and_volume_roots()
    {
        using var box = new Sandbox();
        var folder = box.Dir("folder with spaces & %literal%");
        var location = Assert.Single(CommandLine.StartLocations([folder + @"\."]));
        Assert.Equal(Path.GetFullPath(folder), location.Path);
        var root = Path.GetPathRoot(box.Root)!;
        Assert.Equal(root, Assert.Single(CommandLine.StartLocations([root + @"\."])).Path);
        Assert.Throws<ArgumentException>(() => FolderAssociationService.CommandFor("relative.exe"));
        Assert.Throws<ArgumentException>(() => FolderAssociationService.CommandFor("C:\\bad\".exe"));
    }

    [Fact]
    public void Restoring_a_partial_owned_registration_does_not_leave_a_broken_default()
    {
        var service = Service(); service.SetEnabled(true);
        using (var key = _root.OpenSubKey(@"Classes\Folder\shell\DrivePartitionTool.Open", true))
        { key!.DeleteSubKeyTree("command"); key.DeleteValue("DptOwner"); }
        Assert.True(service.HasSavedState);
        Assert.False(service.IsEnabled);
        service.SetEnabled(false);
        Assert.False(service.HasSavedState);
        Assert.Null(_root.OpenSubKey(@"Classes\Folder"));
    }

    public void Dispose() { _root.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(_path); }
}
