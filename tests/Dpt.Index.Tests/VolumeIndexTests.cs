using System.Diagnostics;
using Dpt.Ipc;

namespace Dpt.Index.Tests;

public class VolumeIndexTests
{
    private const uint Hidden = (uint)FileAttributes.Hidden;
    private static readonly long T = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>
    /// X:\ (5)
    ///   Media (10) → Video (11) → a.mkv (12, 1000), b.mkv (13, 500)
    ///   Docs (20) → tax.pdf (21, 300)
    ///   AppData (30, hidden) → cache.bin (31, 50)
    /// </summary>
    internal static VolumeIndex Sample()
    {
        var v = new VolumeIndex(@"X:\", 1, "NTFS", "Test");
        v.BeginBulk(16);
        v.BulkAdd(12, 11, "a.mkv", false, 0, 1000, 4096, T);
        v.BulkAdd(5, 5, ".", true, 0, 0, 0, T);
        v.BulkAdd(10, 5, "Media", true, 0, 0, 0, T);
        v.BulkAdd(11, 10, "Video", true, 0, 0, 0, T);
        v.BulkAdd(13, 11, "b.mkv", false, 0, 500, 4096, T);
        v.BulkAdd(20, 5, "Docs", true, 0, 0, 0, T);
        v.BulkAdd(21, 20, "tax.pdf", false, 0, 300, 4096, new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        v.BulkAdd(30, 5, "AppData", true, Hidden, 0, 0, T);
        v.BulkAdd(31, 30, "cache.bin", false, 0, 50, 4096, T);
        v.BulkAdd(99, 777, "orphan.txt", false, 0, 9, 9, T); // parent not present
        v.SetRoot(5);
        v.EndBulk();
        return v;
    }

    [Fact]
    public void Bulk_load_links_and_rolls_up()
    {
        var v = Sample();
        Assert.Equal(1850, v.Size(v.RootIndex));
        Assert.Equal(1500, v.Size(v.IndexOf(10)));
        Assert.Equal(2, v.FileCount(v.IndexOf(11)));
        Assert.Equal(VolumeIndex.NoEntry, v.IndexOf(99));
        Assert.Equal(@"X:\Media\Video\a.mkv", v.PathOf(v.IndexOf(12)));
        Assert.Equal(v.IndexOf(21), v.Find(@"x:\docs\TAX.PDF"));
        Assert.Equal(v.RootIndex, v.Find(@"X:\"));
        Assert.Equal(9, v.LiveCount);
    }

    [Fact]
    public void Upsert_size_change_move_rename_and_remove_keep_totals()
    {
        var v = Sample();
        v.Upsert(12, 11, "a.mkv", false, 0, 1500, 4096, T); // grew by 500
        Assert.Equal(2350, v.Size(v.RootIndex));
        Assert.Equal(2000, v.Size(v.IndexOf(11)));

        v.Upsert(12, 20, "a-moved.mkv", false, 0, 1500, 4096, T); // move to Docs + rename
        Assert.Equal(500, v.Size(v.IndexOf(11)));
        Assert.Equal(1800, v.Size(v.IndexOf(20)));
        Assert.Equal(@"X:\Docs\a-moved.mkv", v.PathOf(v.IndexOf(12)));
        Assert.Equal(2350, v.Size(v.RootIndex));

        v.Upsert(40, 20, "new.txt", false, 0, 10, 4096, T); // create
        Assert.Equal(1810, v.Size(v.IndexOf(20)));

        v.Remove(10); // delete Media and everything in it
        Assert.Equal(VolumeIndex.NoEntry, v.IndexOf(13));
        Assert.Equal(1860, v.Size(v.RootIndex));
        Assert.Equal(VolumeIndex.NoEntry, v.Find(@"X:\Media"));
    }

    [Fact]
    public void Moving_folder_into_itself_is_ignored()
    {
        var v = Sample();
        v.Upsert(10, 11, "Media", true, 0, 0, 0, T);
        Assert.Equal(@"X:\Media", v.PathOf(v.IndexOf(10)));
    }

    [Fact]
    public void Snapshot_round_trip()
    {
        var v = Sample();
        v.JournalId = 77;
        v.NextUsn = 12345;
        using var ms = new MemoryStream();
        v.Save(ms);
        ms.Position = 0;
        var back = new VolumeIndex(@"X:\", 1, "NTFS", "Test");
        Assert.True(back.Load(ms));
        Assert.Equal(77UL, back.JournalId);
        Assert.Equal(12345, back.NextUsn);
        Assert.Equal(1850, back.Size(back.RootIndex));
        Assert.Equal(@"X:\Media\Video\b.mkv", back.PathOf(back.IndexOf(13)));

        ms.Position = 0;
        Assert.False(new VolumeIndex(@"X:\", 2, "NTFS", "Other serial").Load(ms));
    }
}

public class SearchExecutorTests
{
    private static SearchResponse Run(string query, string? scope = null, bool hidden = false) =>
        SearchExecutor.Search([VolumeIndexTests.Sample()], new SearchRequest { Query = query, ScopePath = scope, IncludeHidden = hidden, Limit = 50 }, false);

    private static string[] Names(SearchResponse r) => r.Items.Select(i => i.Name).ToArray();

    [Fact]
    public void Words_match_names_in_any_order_and_case()
    {
        Assert.Equal(["a.mkv"], Names(Run("A MKV")));
        Assert.Equal(2, Run("mkv").Total);
    }

    [Fact]
    public void Filters()
    {
        Assert.Equal(["tax.pdf"], Names(Run("ext:pdf")));
        Assert.Equal(2, Run("type:video").Total);
        Assert.Equal(["a.mkv"], Names(Run("size:>600")));
        Assert.Equal(["tax.pdf"], Names(Run("modified:2025")));
        Assert.Equal(3, Run("folder:").Total); // Media, Video, Docs (AppData hidden)
        Assert.Equal(["b.mkv"], Names(Run("b", scope: @"X:\Media")));
        Assert.Empty(Run("tax", scope: @"X:\Media").Items);
        Assert.Equal(["tax.pdf"], Names(Run(@"in:X:\Docs")));
    }

    [Fact]
    public void Hidden_trees_excluded_unless_requested()
    {
        Assert.Empty(Run("cache").Items);
        Assert.Equal(["cache.bin"], Names(Run("cache", hidden: true)));
    }

    [Fact]
    public void Exact_names_rank_first()
    {
        var v = new VolumeIndex(@"Y:\", 3, "NTFS", "");
        v.BeginBulk(8);
        v.BulkAdd(5, 5, ".", true, 0, 0, 0, 0);
        v.BulkAdd(6, 5, "my budget plan.xlsx", false, 0, 1, 1, 0);
        v.BulkAdd(7, 5, "budget.xlsx", false, 0, 1, 1, 0);
        v.BulkAdd(8, 5, "oldbudgets.zip", false, 0, 1, 1, 0);
        v.SetRoot(5);
        v.EndBulk();
        var r = SearchExecutor.Search([v], new SearchRequest { Query = "budget" }, false);
        Assert.Equal(["budget.xlsx", "my budget plan.xlsx", "oldbudgets.zip"], Names(r));
    }

    [Fact]
    public void Largest_files_and_folders()
    {
        var vols = new[] { VolumeIndexTests.Sample() };
        var files = SearchExecutor.Largest(vols, new LargestRequest { Kind = LargestKind.Files, Count = 2 });
        Assert.Equal(["a.mkv", "b.mkv"], files.Select(f => f.Name));
        var folders = SearchExecutor.Largest(vols, new LargestRequest { Kind = LargestKind.Folders, Count = 2, TopLevelOnly = true });
        // Media (1500) is fully explained by Video (1500), so the specific folder is shown.
        Assert.Equal("Video", folders[0].Name);
        Assert.DoesNotContain(folders, f => f.Name == "Media");
    }

    [Fact]
    public void Folder_sizes_and_listing()
    {
        var vols = new[] { VolumeIndexTests.Sample() };
        Assert.Equal(1500, SearchExecutor.FolderSize(vols, @"X:\Media"));
        Assert.Null(SearchExecutor.FolderSize(vols, @"X:\Nope"));
        Assert.Equal(["Video"], SearchExecutor.ListFolder(vols, @"X:\Media").Select(i => i.Name));
    }

    /// <summary>Plan target: first page of results from a 1M-file catalog in under 100 ms. Runs alone (Category=Perf) so timing is fair.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void One_million_entries_first_page_under_100ms()
    {
        var v = new VolumeIndex(@"Z:\", 9, "NTFS", "");
        const int folders = 1000, filesPerFolder = 1000;
        v.BeginBulk(folders * filesPerFolder + folders + 1);
        v.BulkAdd(1, 1, ".", true, 0, 0, 0, 0);
        string[] words = ["report", "photo", "invoice", "holiday", "backup", "notes", "draft", "video", "budget", "scan"];
        string[] exts = ["pdf", "jpg", "docx", "mp4", "txt", "xlsx"];
        ulong r = 2;
        var rng = new Random(7);
        for (var f = 0; f < folders; f++)
        {
            var folderRef = r++;
            v.BulkAdd(folderRef, 1, $"Folder {f} {words[f % words.Length]}", true, 0, 0, 0, 0);
            for (var k = 0; k < filesPerFolder; k++)
                v.BulkAdd(r++, folderRef, $"{words[rng.Next(words.Length)]} {k} {words[rng.Next(words.Length)]}.{exts[rng.Next(exts.Length)]}", false, 0, rng.Next(1, 1 << 24), 4096, rng.NextInt64(1, DateTime.UtcNow.Ticks));
        }

        v.SetRoot(1);
        v.EndBulk();
        Assert.True(v.LiveCount > 1_000_000);

        var request = new SearchRequest { Query = "invoice holiday", Limit = 200 };
        SearchExecutor.Search([v], request, false); // warm-up (JIT)
        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = SearchExecutor.Search([v], request, false);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(200, res.Items.Count);
        }

        var median = times.Order().ElementAt(2);
        Console.WriteLine($"1M search median: {median:F1} ms; samples: {string.Join(", ", times.Select(t => t.ToString("F1")))} ms");
        Assert.True(median < 100, $"Median search took {median:F1} ms (all: {string.Join(", ", times.Select(t => t.ToString("F1")))})");
    }
}

