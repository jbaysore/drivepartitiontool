using Dpt.Core.Operations;

namespace Dpt.Core.Tests;

public class BulkRenameTests
{
    private static readonly DateTime D = new(2025, 7, 4);

    private static IReadOnlyList<RenamePlanItem> Plan(BulkRenameOptions o, params string[] names) =>
        BulkRenamePlanner.Plan(names.Select(n => new RenameInput(@"C:\Pics\" + n, false, D)).ToList(), o, _ => false);

    [Fact]
    public void Template_with_numbers_and_dates()
    {
        var r = Plan(new BulkRenameOptions { Template = "Holiday {date} {n:000}" }, "IMG_1.JPG", "IMG_2.JPG");
        Assert.Equal(["Holiday 2025-07-04 001.JPG", "Holiday 2025-07-04 002.JPG"], r.Select(x => x.NewName));
        Assert.All(r, x => Assert.True(x.Changes));
    }

    [Fact]
    public void Find_replace_plain_and_regex()
    {
        Assert.Equal("my-photo.jpg", Plan(new BulkRenameOptions { Find = "_", Replace = "-" }, "my_photo.jpg")[0].NewName);
        Assert.Equal("Scan 7.pdf", Plan(new BulkRenameOptions { Find = @"^scan_0*(\d+)$", Replace = "Scan $1", UseRegex = true }, "scan_0007.pdf")[0].NewName);
        Assert.NotNull(Plan(new BulkRenameOptions { Find = "(", UseRegex = true }, "a.txt")[0].Problem);
    }

    [Fact]
    public void Case_and_extension()
    {
        Assert.Equal("report final.pdf", Plan(new BulkRenameOptions { Case = NameCase.Lower }, "Report FINAL.PDF")[0].NewName);
        Assert.Equal("Report Final.PDF", Plan(new BulkRenameOptions { Case = NameCase.Title }, "report FINAL.PDF")[0].NewName);
        Assert.Equal("notes.md", Plan(new BulkRenameOptions { ChangeExtension = "md" }, "notes.txt")[0].NewName);
    }

    [Fact]
    public void Duplicates_and_clashes_are_flagged()
    {
        var dup = Plan(new BulkRenameOptions { Template = "same" }, "a.txt", "b.txt");
        Assert.All(dup, x => Assert.Equal("Two items would get the same name.", x.Problem));

        var clash = BulkRenamePlanner.Plan([new RenameInput(@"C:\X\a.txt", false, D)], new BulkRenameOptions { Template = "b" }, p => p.EndsWith(@"\b.txt"));
        Assert.Equal("An item with this name already exists.", clash[0].Problem);

        var bad = Plan(new BulkRenameOptions { Template = "a:b" }, "x.txt");
        Assert.NotNull(bad[0].Problem);
    }

    [Fact]
    public void Renaming_onto_another_selected_item_is_allowed()
    {
        // 1→2 and 2→3: "2.txt" exists, but it is itself being renamed.
        var items = new[] { new RenameInput(@"C:\X\1.txt", false, D), new RenameInput(@"C:\X\2.txt", false, D) };
        var r = BulkRenamePlanner.Plan(items, new BulkRenameOptions { Template = "{n}", StartNumber = 2 }, p => p.EndsWith(@"\1.txt") || p.EndsWith(@"\2.txt"));
        Assert.Equal(["2.txt", "3.txt"], r.Select(x => x.NewName));
        Assert.All(r, x => Assert.Null(x.Problem));
    }
}
