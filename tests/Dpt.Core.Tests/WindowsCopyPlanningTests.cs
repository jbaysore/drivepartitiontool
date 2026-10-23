using Dpt.Core.WindowsCopy;

namespace Dpt.Core.Tests;

public class WindowsCopyPlanningTests
{
    [Theory]
    [InlineData("")]
    [InlineData("..\\Windows")]
    [InlineData("Games\\..\\Windows")]
    [InlineData("C:\\Windows")]
    [InlineData("\\Windows")]
    [InlineData("Users\\*\\NTUSER.DAT")]
    [InlineData("Games\r\n[ExclusionException]")]
    [InlineData("Games.\\save")]
    public void Config_paths_cannot_escape_or_inject_patterns(string path) => Assert.Throws<ArgumentException>(() => WindowsCopyPlanning.ValidateExclusions([path]));

    [Theory]
    [InlineData("Windows\\System32")]
    [InlineData("programdata/microsoft")]
    [InlineData("ProgramData")]
    [InlineData("Users")]
    [InlineData("Users\\Alice")]
    [InlineData("Users\\Default\\Documents")]
    [InlineData("Users\\Alice\\NTUSER.DAT")]
    [InlineData("Users\\Alice\\AppData")]
    [InlineData("Users\\Alice\\AppData\\Local")]
    [InlineData("Users\\Alice\\AppData\\Local\\Microsoft\\Windows\\UsrClass.dat")]
    [InlineData("Program Files")]
    [InlineData("Boot")]
    public void Required_windows_and_account_data_cannot_be_excluded(string path) => Assert.Throws<ArgumentException>(() => WindowsCopyPlanning.ValidateExclusions([path]));

    [Theory]
    [InlineData("Users\\Alice\\Pictures")]
    [InlineData("Users\\Alice\\AppData\\Roaming\\Vendor")]
    [InlineData("Program Files\\Editor")]
    [InlineData("ProgramData\\Vendor")]
    [InlineData("Games")]
    public void Optional_content_can_be_excluded(string path) => Assert.Single(WindowsCopyPlanning.ValidateExclusions([path]));

    [Theory]
    [InlineData(BootFirmware.Uefi)]
    [InlineData(BootFirmware.Bios)]
    public void Layout_is_aligned_has_recovery_and_fits_the_destination(BootFirmware firmware)
    {
        var options = new WindowsCopyOptions { SourceRoot = @"X:\", Firmware = firmware };
        var layout = WindowsCopyPlanning.Layout(options, 10_000 * WindowsCopyPlanning.Mb, 100 * WindowsCopyPlanning.Mb);
        Assert.Equal(firmware == BootFirmware.Uefi ? ["EFI", "MSR", "Windows", "Recovery"] : new[] { "Windows", "Recovery" }, layout.Partitions.Select(p => p.Role));
        Assert.All(layout.Partitions, p => { Assert.True(p.Size > 0); Assert.Equal(0, p.Offset % WindowsCopyPlanning.Mb); });
        Assert.True(layout.Partitions.Last().Offset + layout.Partitions.Last().Size < 10_000 * WindowsCopyPlanning.Mb);
        Assert.True(layout.WorkingBytes > 200 * WindowsCopyPlanning.Mb);
    }

    [Fact]
    public void Capacity_accounts_for_the_temporary_image_and_refuses_unknown_sizes()
    {
        var options = new WindowsCopyOptions { SourceRoot = @"X:\" };
        Assert.Throws<ArgumentException>(() => WindowsCopyPlanning.Layout(options, 2000 * WindowsCopyPlanning.Mb, 600 * WindowsCopyPlanning.Mb));
        Assert.Throws<ArgumentException>(() => WindowsCopyPlanning.Layout(options, 2000 * WindowsCopyPlanning.Mb, -1));
        Assert.Throws<OverflowException>(() => WindowsCopyPlanning.Layout(options, long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void Config_is_literal_root_relative_deduplicated_and_always_excludes_transient_files()
    {
        var config = WindowsCopyPlanning.ExclusionConfig(new() { SourceRoot = @"X:\", Exclusions = ["Games", "games", @"Users/Alice/Pictures"] });
        Assert.StartsWith("[ExclusionList]\r\n\\Games\r\n", config);
        Assert.Contains("\\Users\\Alice\\Pictures\r\n", config);
        Assert.Contains("\\pagefile.sys\r\n", config);
        Assert.DoesNotContain("\\games\r\n", config);
    }
}
