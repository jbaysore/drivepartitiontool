using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Dpt.App.UITests;

/// <summary>M3: Drives and Largest items pages (UI Automation reads only; no keyboard/mouse input).</summary>
[Trait("Category", "UI")]
public class StorageViewTests
{
    private static AutomationElement? Row(AppSession s, string list, string name) =>
        s.Find(list)?.FindFirstDescendant(s.Cf.ByControlType(ControlType.DataItem).Or(s.Cf.ByControlType(ControlType.ListItem)).And(s.Cf.ByName(name)));

    [Fact]
    public void Largest_items_lists_biggest_files_and_folders()
    {
        using var s = new AppSession(arguments: "--open LargestItems", prepare: b =>
        {
            Directory.CreateDirectory(Path.Combine(b, "Movies", "2024"));
            File.WriteAllBytes(Path.Combine(b, "Movies", "2024", "holiday.mkv"), new byte[3_000_000]);
            File.WriteAllBytes(Path.Combine(b, "small.txt"), new byte[10]);
        });
        s.WaitUntil(() => Row(s, "LargestList", "holiday.mkv") is not null, "largest file listed", TimeSpan.FromSeconds(20));
        var first = s.Get("LargestList").FindFirstDescendant(s.Cf.ByControlType(ControlType.DataItem).Or(s.Cf.ByControlType(ControlType.ListItem)));
        Assert.Equal("holiday.mkv", first?.Name);

        s.Get("ShowFolders").AsRadioButton().IsChecked = true;
        s.WaitUntil(() => Row(s, "LargestList", "2024") is not null, "specific heavy folder listed", TimeSpan.FromSeconds(10));
        Assert.Null(Row(s, "LargestList", "Movies")); // fully explained by its 2024 subfolder
    }

    [Fact]
    public void Drives_page_lists_disks()
    {
        using var s = new AppSession(arguments: "--open Drives");
        s.WaitUntil(() => s.Find("DiskList")?.FindFirstDescendant(s.Cf.ByAutomationId("DiskTitle")) is not null, "a disk card", TimeSpan.FromSeconds(15));
        var system = Path.GetPathRoot(Environment.SystemDirectory)![..2];
        s.WaitUntil(() => s.Find("DiskList")?.FindFirstDescendant(s.Cf.ByName($"Local Disk ({system})")) is not null ||
                          s.Find("DiskList")?.FindFirstDescendant(s.Cf.ByName("Windows is here")) is not null, "system volume row", TimeSpan.FromSeconds(5));
    }
}
