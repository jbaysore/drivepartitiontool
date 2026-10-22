using Dpt.Ipc;

namespace Dpt.Index.Tests;

public class WatcherReproTests
{
    [Fact]
    public async Task File_created_in_existing_subfolder_with_default_options()
    {
        var root = Path.Combine(Path.GetTempPath(), "dpt-repro-" + Guid.NewGuid().ToString("N"));
        var browse = Path.Combine(root, "browse");
        Directory.CreateDirectory(Path.Combine(browse, "Alpha"));
        using var engine = new IndexEngine(new EngineOptions { DataDirectory = Path.Combine(root, "data"), FixedRoots = [browse] });
        engine.Start();
        await engine.WaitUntilReadyAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        File.WriteAllText(Path.Combine(browse, "Alpha", "late-arrival.txt"), "x");
        var found = 0;
        for (var i = 0; i < 40 && found == 0; i++)
        {
            await Task.Delay(100);
            found = (await engine.SearchAsync(new SearchRequest { Query = "late-arrival" })).Total;
        }

        var status = engine.GetStatus();
        Assert.True(found == 1, $"not found; status: {string.Join(";", status.Volumes.Select(v => $"{v.Root} {v.State} {v.Items} {v.Message}"))}");
    }
}
