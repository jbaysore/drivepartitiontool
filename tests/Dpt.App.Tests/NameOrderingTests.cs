using System.Globalization;
using Dpt.App.ViewModels;
using Dpt.Shell.FileSystem;

namespace Dpt.App.Tests;

public class NameOrderingTests
{
    [Theory]
    [InlineData("file2.txt", "file10.txt")]
    [InlineData("photo9 part2.jpg", "photo9 part12.jpg")]
    [InlineData("report99999999999999999999", "report100000000000000000000")]
    [InlineData("résumé2.txt", "résumé11.txt")]
    public void Natural_keys_order_numeric_runs_without_numeric_overflow(string first, string second)
    {
        static FileItemViewModel Row(string name) => new(new FileEntry("C:\\" + name, name, false, 1, DateTime.Now, DateTime.Now, FileAttributes.Normal));
        var a = Row(first); var b = Row(second);
        Assert.True(SortKey.Compare(a.NameSortKey, b.NameSortKey) < 0);
        Assert.True(FileItemViewModel.CompareNames(first, second) < 0);
        a.Update(a.Entry with { Name = "zzz100.txt" });
        Assert.True(SortKey.Compare(a.NameSortKey, b.NameSortKey) > 0);
    }
}
