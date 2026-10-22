using Dpt.Core.Formatting;
using Dpt.Core.Locations;
using Dpt.Core.Navigation;
using Dpt.Core.Search;

namespace Dpt.Core.Tests;

public class SizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(84_600L * 1024 * 1024, "82.6 GB")]
    [InlineData(642L * 1024 * 1024, "642 MB")]
    public void Formats_like_explorer(long bytes, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }

    [Theory]
    [InlineData("1gb", 1L << 30)]
    [InlineData("500 MB", 500L << 20)]
    [InlineData("12k", 12L << 10)]
    [InlineData("1024", 1024)]
    [InlineData("1.5g", 3L << 29)]
    public void Parses_sizes(string text, long expected)
    {
        Assert.True(SizeFormatter.TryParse(text, out var b));
        Assert.Equal(expected, b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5mb")]
    public void Rejects_bad_sizes(string text) => Assert.False(SizeFormatter.TryParse(text, out _));
}

public class PathUtilTests
{
    [Theory]
    [InlineData("c:", @"C:\")]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"C:\Users\", @"C:\Users")]
    [InlineData("C:/Users/You", @"C:\Users\You")]
    [InlineData("\"C:\\Temp\"", @"C:\Temp")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, PathUtil.Normalize(input));

    [Fact]
    public void Parent_of_root_is_null() => Assert.Null(PathUtil.Parent(@"C:\"));

    [Fact]
    public void Parent_of_top_folder_is_root() => Assert.Equal(@"C:\", PathUtil.Parent(@"C:\Users"));

    [Fact]
    public void Ancestry_builds_breadcrumb()
    {
        Assert.Equal([@"C:\", @"C:\Users", @"C:\Users\You"], PathUtil.Ancestry(@"C:\Users\You"));
    }

    [Theory]
    [InlineData(@"C:\A\B", @"C:\A", true)]
    [InlineData(@"C:\A", @"C:\A", true)]
    [InlineData(@"C:\AB", @"C:\A", false)]
    [InlineData(@"D:\A", @"C:\A", false)]
    [InlineData(@"C:\A\B", @"C:\", true)]
    public void IsSameOrUnder(string path, string root, bool expected) => Assert.Equal(expected, PathUtil.IsSameOrUnder(path, root));

    [Fact]
    public void DisplayName_of_root_is_letter() => Assert.Equal("C:", PathUtil.DisplayName(@"C:\"));
}

public class NavigationHistoryTests
{
    [Fact]
    public void Back_and_forward()
    {
        var h = new NavigationHistory<string>();
        h.Navigate("a");
        h.Navigate("b");
        h.Navigate("c");
        Assert.Equal("b", h.GoBack());
        Assert.Equal("a", h.GoBack());
        Assert.False(h.CanGoBack);
        Assert.Equal("b", h.GoForward());
        h.Navigate("d");
        Assert.False(h.CanGoForward);
        Assert.Equal(["a", "b", "d"], h.Entries);
    }

    [Fact]
    public void Navigating_to_current_does_not_duplicate()
    {
        var h = new NavigationHistory<string>();
        h.Navigate("a");
        h.Navigate("a");
        Assert.Single(h.Entries);
    }
}

public class LocationTests
{
    [Fact]
    public void Folder_locations_compare_case_insensitively() =>
        Assert.Equal(Location.Folder(@"c:\users"), Location.Folder(@"C:\Users\"));

    [Fact]
    public void Titles() => Assert.Equal("Users", Location.Folder(@"C:\Users").Title);
}

public class AddressResolverTests
{
    private static readonly AddressResolver Resolver = new(
        t => t.Replace("%USERPROFILE%", @"C:\Users\You", StringComparison.OrdinalIgnoreCase),
        name => name.Equals("downloads", StringComparison.OrdinalIgnoreCase) ? @"C:\Users\You\Downloads" : null);

    [Fact]
    public void Plain_path() => Assert.Equal(Location.Folder(@"D:\Media"), Resolver.Resolve(@"D:\Media"));

    [Fact]
    public void Drive_letter_only() => Assert.Equal(Location.Folder(@"E:\"), Resolver.Resolve("e"));

    [Fact]
    public void Environment_variable() => Assert.Equal(Location.Folder(@"C:\Users\You\Documents"), Resolver.Resolve(@"%USERPROFILE%\Documents"));

    [Fact]
    public void Shell_folder_with_tail() => Assert.Equal(Location.Folder(@"C:\Users\You\Downloads\Setup"), Resolver.Resolve(@"shell:Downloads\Setup"));

    [Fact]
    public void Page_words() => Assert.Equal(LocationKind.Drives, Resolver.Resolve("drives")!.Kind);

    [Fact]
    public void Relative_text_is_rejected() => Assert.Null(Resolver.Resolve("not a path"));
}

public class FrequentLocationsTests
{
    [Fact]
    public void Recent_visits_outrank_old_frequent_ones()
    {
        var now = DateTimeOffset.Now;
        var visits = new[]
        {
            new VisitStats(@"C:\Old", 20, now.AddDays(-60)),
            new VisitStats(@"C:\New", 3, now.AddHours(-1)),
        };
        Assert.Equal(@"C:\New", FrequentLocations.Rank(visits, now, 5)[0].Path);
    }

    [Fact]
    public void Excludes_pinned()
    {
        var now = DateTimeOffset.Now;
        var visits = new[] { new VisitStats(@"C:\A", 1, now), new VisitStats(@"C:\B", 1, now) };
        Assert.Single(FrequentLocations.Rank(visits, now, 5, [@"c:\a"]));
    }

    [Fact]
    public void Recency_labels()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Today", FrequentLocations.RecencyLabel(now.AddHours(-2), now));
        Assert.Equal("Yesterday", FrequentLocations.RecencyLabel(now.AddDays(-1), now));
        Assert.Equal("This week", FrequentLocations.RecencyLabel(now.AddDays(-4), now));
    }
}

public class TypeGroupsTests
{
    [Fact]
    public void Default_groups()
    {
        Assert.Equal("image", TypeGroups.Default.GroupOf(".JPG"));
        Assert.Equal("video", TypeGroups.Default.GroupOf("mkv"));
        Assert.Equal("other", TypeGroups.Default.GroupOf("xyz"));
    }

    [Fact]
    public void Custom_definition()
    {
        var g = new TypeGroups("raw=cr3,nef;docs=pdf");
        Assert.True(g.TryGet("RAW", out var exts));
        Assert.Contains("nef", exts);
        Assert.Equal("docs", g.GroupOf("pdf"));
    }
}

internal sealed class CultureScope : IDisposable
{
    private readonly System.Globalization.CultureInfo _old = System.Globalization.CultureInfo.CurrentCulture;

    public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

    public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _old;
}
