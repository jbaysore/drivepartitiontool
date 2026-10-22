using System.Diagnostics;
using System.Security.Principal;
using Dpt.Ipc;

namespace Dpt.Index.Tests;

/// <summary>Scan-mode catalog over a temp folder (no admin): changes must show up within 2 seconds.</summary>
public sealed class ScanEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dpt-index-tests", Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly IndexEngine _engine;

    public ScanEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tree", "Photos", "2025"));
        File.WriteAllBytes(Path.Combine(_root, "tree", "Photos", "2025", "beach.jpg"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "tree", "readme.txt"), new byte[100]);
        _data = Path.Combine(_root, "data");
        _engine = new IndexEngine(new EngineOptions
        {
            DataDirectory = _data,
            FixedRoots = [Path.Combine(_root, "tree")],
            PollInterval = TimeSpan.FromMilliseconds(200),
        });
        _engine.Start();
        _engine.WaitUntilReadyAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }

    private string P(string relative) => Path.Combine(_root, "tree", relative);

    private async Task<bool> Eventually(Func<Task<bool>> check, int ms = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (await check()) return true;
            await Task.Delay(50);
        }

        return await check();
    }

    private async Task<int> Count(string q) => (await _engine.SearchAsync(new SearchRequest { Query = q, IncludeHidden = true })).Total;

    [Fact]
    public async Task Initial_scan_and_sizes()
    {
        Assert.Equal(1, await Count("beach"));
        var sizes = await _engine.GetFolderSizesAsync([P("Photos"), P(""), P("nope")]);
        Assert.Equal(3000, sizes[0]);
        Assert.Equal(3100, sizes[1]);
        Assert.Null(sizes[2]);
    }

    [Fact]
    public async Task Create_rename_move_delete_within_two_seconds()
    {
        var changes = new List<CatalogChange>();
        _engine.Changed += (_, c) => { lock (changes) changes.AddRange(c); };

        File.WriteAllBytes(P("invoice-42.pdf"), new byte[500]);
        Assert.True(await Eventually(async () => await Count("invoice-42") == 1), "create");

        File.Move(P("invoice-42.pdf"), P("invoice-43.pdf"));
        Assert.True(await Eventually(async () => await Count("invoice-43") == 1 && await Count("invoice-42") == 0), "rename");

        File.Move(P("invoice-43.pdf"), P(@"Photos\invoice-43.pdf"));
        Assert.True(await Eventually(async () => (await _engine.GetFolderSizesAsync([P("Photos")]))[0] == 3500), "move updates folder size");

        Directory.CreateDirectory(P("NewFolder"));
        File.WriteAllBytes(P(@"NewFolder\inner.bin"), new byte[42]);
        Assert.True(await Eventually(async () => (await _engine.GetFolderSizesAsync([P("NewFolder")]))[0] == 42), "new folder with file");

        Directory.Delete(P("Photos"), true);
        Assert.True(await Eventually(async () => await Count("beach") == 0 && await Count("invoice-43") == 0), "delete tree");
        Assert.True(await Eventually(async () => (await _engine.GetFolderSizesAsync([P("")]))[0] == 142), "root total after delete");
        lock (changes) Assert.Contains(changes, c => c.Kind == ChangeKind.Created && c.Path.EndsWith("invoice-42.pdf"));
    }

    [Fact]
    public async Task Nested_folders_created_after_scan_are_searchable_without_hidden()
    {
        Directory.CreateDirectory(P(@"Deep\Er\Still"));
        File.WriteAllText(P(@"Deep\Er\Still\Quarterly Budget 2026.xlsx"), "x");
        File.WriteAllText(P("report.pdf"), "p");
        Assert.True(await Eventually(async () =>
            (await _engine.SearchAsync(new SearchRequest { Query = "budget quarterly" })).Total == 1 &&
            (await _engine.SearchAsync(new SearchRequest { Query = "report ext:pdf" })).Total == 1, 3000));
    }

    [Fact]
    public async Task Pipe_round_trip()
    {
        var name = "dpt-test-" + Guid.NewGuid().ToString("N");
        using var me = WindowsIdentity.GetCurrent();
        using var server = new CatalogPipeServer(_engine, [me.User!], name);
        server.Start(2);
        using var client = new CatalogPipeClient(name);
        Assert.True(await CatalogPipeClient.IsAvailableAsync(name, 2000));
        var status = await client.GetStatusAsync();
        Assert.True(status.IsService);
        Assert.Single(status.Volumes);
        var res = await client.SearchAsync(new SearchRequest { Query = "beach", IncludeHidden = true });
        Assert.Equal("beach.jpg", Assert.Single(res.Items).Name);
        Assert.Equal(3000, (await client.GetFolderSizesAsync([P("Photos")]))[0]);
        Assert.Equal("beach.jpg", (await client.GetLargestAsync(new LargestRequest { Count = 1 }))[0].Name);
        Assert.Contains((await client.ListFolderAsync(P(""))), i => i.Name == "Photos");

        var got = new TaskCompletionSource<CatalogChange>();
        client.Changed += (_, c) => { foreach (var x in c) if (x.Path.EndsWith("ping.txt")) got.TrySetResult(x); };
        await Task.Delay(500); // let the subscription connect
        File.WriteAllText(P("ping.txt"), "x");
        var change = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ChangeKind.Created, change.Kind);
    }

    [Fact]
    public async Task Snapshot_is_written_and_reused()
    {
        _engine.Dispose();
        Assert.NotEmpty(Directory.GetFiles(_data, "*.idx"));
        using var again = new IndexEngine(new EngineOptions { DataDirectory = _data, FixedRoots = [P("")] });
        again.Start();
        // Snapshot makes results available immediately, even while the background refresh runs.
        var sw = Stopwatch.StartNew();
        while ((await again.SearchAsync(new SearchRequest { Query = "beach", IncludeHidden = true })).Total == 0 && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(20);
        Assert.Equal(1, (await again.SearchAsync(new SearchRequest { Query = "beach", IncludeHidden = true })).Total);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
