using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;

namespace Dpt.App.UITests;

/// <summary>M2: search from Home across the catalog, search inside a folder, filters, and catalog folder sizes.</summary>
[Trait("Category", "UI")]
public class SearchTests
{
    private static AutomationElement? ResultRow(AppSession s, string name) =>
        s.Find("SearchResults")?.FindFirstDescendant(s.Cf.ByControlType(ControlType.DataItem).Or(s.Cf.ByControlType(ControlType.ListItem)).And(s.Cf.ByName(name)));

    /// <summary>Uses --search so no keyboard input is needed (doesn't take focus from the owner).</summary>
    [Fact]
    public void Search_finds_nested_file_by_partial_name()
    {
        using var s = new AppSession(arguments: "--search \"budget quarterly\"", prepare: b =>
        {
            Directory.CreateDirectory(Path.Combine(b, "Deep", "Er", "Still"));
            File.WriteAllText(Path.Combine(b, "Deep", "Er", "Still", "Quarterly Budget 2026.xlsx"), "x");
        });
        s.WaitUntil(() => ResultRow(s, "Quarterly Budget 2026.xlsx") is not null, "nested file found", TimeSpan.FromSeconds(20));
        Assert.Contains("1 result", s.Get("SearchSummary").Name);
    }

    [Fact]
    public void Search_picks_up_files_created_after_start()
    {
        using var s = new AppSession(arguments: "--search \"late-arrival\"");
        Thread.Sleep(1500);
        File.WriteAllText(Path.Combine(s.Browse, "Alpha", "late-arrival.txt"), "x");
        // Results are live: a newly created matching file appears without searching again.
        s.WaitUntil(() => ResultRow(s, "late-arrival.txt") is not null, "late file appears", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Folder_search_box_is_scoped_to_the_folder()
    {
        using var s = new AppSession();
        File.WriteAllText(Path.Combine(s.Browse, "Alpha", "match-in-alpha.txt"), "a");
        File.WriteAllText(Path.Combine(s.Browse, "Beta", "match-in-beta.txt"), "b");
        s.Select("Alpha");
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => s.Heading == "Alpha", "in Alpha");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
        s.Type("match");
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => ResultRow(s, "match-in-alpha.txt") is not null, "scoped result", TimeSpan.FromSeconds(20));
        Assert.Null(ResultRow(s, "match-in-beta.txt"));
        Assert.Contains("Alpha", s.Get("SearchScope").Name);
    }

    [Fact]
    public void Extension_filter_typed_in_query()
    {
        using var s = new AppSession(arguments: "--search \"report ext:pdf\"", prepare: b =>
        {
            File.WriteAllText(Path.Combine(b, "report.pdf"), "p");
            File.WriteAllText(Path.Combine(b, "report.docx"), "d");
        });
        s.WaitUntil(() => ResultRow(s, "report.pdf") is not null, "pdf found", TimeSpan.FromSeconds(20));
        Assert.Null(ResultRow(s, "report.docx"));
    }
}
