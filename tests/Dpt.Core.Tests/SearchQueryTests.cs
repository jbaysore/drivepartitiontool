using Dpt.Core.Search;

namespace Dpt.Core.Tests;

public class SearchQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 15, 0, 0);

    private static SearchQuery P(string s) => SearchQuery.Parse(s, Now);

    [Fact]
    public void Plain_words() => Assert.Equal(["tax", "2024"], P("tax 2024").Terms);

    [Fact]
    public void Quoted_phrase_is_one_term() => Assert.Equal(["annual report"], P("\"annual report\"").Terms);

    [Fact]
    public void Extensions_and_wildcards()
    {
        Assert.Equal(["docx", "pdf"], P("ext:pdf,.docx").Extensions!.Order());
        Assert.Contains("jpg", P("*.jpg").Extensions!);
    }

    [Fact]
    public void Type_group_or_kind()
    {
        Assert.Equal("image", P("type:image").TypeGroup);
        Assert.Equal(ItemKindFilter.FoldersOnly, P("type:folder").Kind);
        Assert.Equal(ItemKindFilter.FilesOnly, P("file:").Kind);
        Assert.Equal(["photos"], P("folder:photos").Terms);
    }

    [Theory]
    [InlineData("size:>1gb", 1073741825L, null)]
    [InlineData("size:<10kb", null, 10239L)]
    [InlineData("size:10mb..2gb", 10485760L, 2147483648L)]
    [InlineData("size:huge", 1073741825L, null)]
    public void Sizes(string q, long? min, long? max)
    {
        var p = P(q);
        Assert.Equal(min, p.MinSize);
        Assert.Equal(max, p.MaxSize);
    }

    [Fact]
    public void Dates()
    {
        var today = P("modified:today");
        Assert.Equal(Now.Date, today.ModifiedFrom);
        Assert.Equal(Now.Date.AddDays(1), today.ModifiedTo);
        var year = P("modified:2025");
        Assert.Equal(new DateTime(2025, 1, 1), year.ModifiedFrom);
        Assert.Equal(new DateTime(2026, 1, 1), year.ModifiedTo);
        var month = P("dm:2025-03");
        Assert.Equal(new DateTime(2025, 3, 1), month.ModifiedFrom);
        var range = P("modified:2025-01-01..2025-02-01");
        Assert.Equal(new DateTime(2025, 2, 2), range.ModifiedTo);
        Assert.Equal(Now.Date.AddDays(-7), P("modified:lastweek").ModifiedFrom);
    }

    [Fact]
    public void Paths_drives_categories()
    {
        var p = P(@"in:""C:\My Files"" drive:e cat:Taxes report");
        Assert.Equal(@"C:\My Files", p.InPath);
        Assert.Equal(["E:"], p.Drives);
        Assert.Equal(["Taxes"], p.Categories);
        Assert.Equal(["report"], p.Terms);
    }

    [Fact]
    public void Windows_path_token_is_a_word_not_a_filter() => Assert.Equal([@"C:\temp"], P(@"C:\temp").Terms);

    [Fact]
    public void Unknown_filters_and_bad_values_become_words()
    {
        Assert.Equal(["color:blue"], P("color:blue").Terms);
        Assert.Equal(["size:lots"], P("size:lots").Terms);
    }

    [Fact]
    public void Word_boundary()
    {
        Assert.True(SearchRanking.StartsAtWordBoundary("my budget.xlsx", "budget"));
        Assert.True(SearchRanking.StartsAtWordBoundary("MyBudget.xlsx", "budget"));
        Assert.False(SearchRanking.StartsAtWordBoundary("oldbudgets.zip", "budget"));
    }
}
