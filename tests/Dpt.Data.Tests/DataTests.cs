using Dpt.Core.Settings;

namespace Dpt.Data.Tests;

public sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Folder = Path.Combine(Path.GetTempPath(), "dpt-data-tests", Guid.NewGuid().ToString("N"));
        Db = new AppDatabase(Path.Combine(Folder, "user.db"));
    }

    public string Folder { get; }
    public AppDatabase Db { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, true); }
        catch (IOException) { }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_when_empty()
    {
        using var t = new TempDb();
        var s = new SettingsStore(t.Db).Load();
        Assert.Equal(StartView.Home, s.StartView);
        Assert.True(s.ShowFolderSizes);
    }

    [Fact]
    public void Round_trip()
    {
        using var t = new TempDb();
        var store = new SettingsStore(t.Db);
        var s = store.Load();
        s.StartView = StartView.RestoreTabs;
        s.LastTabs = ["Folder|C:\\|"];
        s.FrequentCount = 9;
        store.Save(s);
        var back = new SettingsStore(t.Db).Load();
        Assert.Equal(StartView.RestoreTabs, back.StartView);
        Assert.Equal(9, back.FrequentCount);
        Assert.Single(back.LastTabs);
    }

    [Fact]
    public void Migrations_are_idempotent()
    {
        using var t = new TempDb();
        _ = new AppDatabase(t.Db.DatabasePath);
        _ = new AppDatabase(t.Db.DatabasePath);
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal((long)AppDatabase.Migrations.Length, (long)cmd.ExecuteScalar()!);
    }
}

public class LocationsRepositoryTests
{
    [Fact]
    public void Pins_add_order_rename_remove()
    {
        using var t = new TempDb();
        var r = new LocationsRepository(t.Db);
        r.AddPin(@"C:\A");
        r.AddPin(@"C:\B");
        r.AddPin(@"c:\a"); // duplicate (case-insensitive) ignored
        Assert.Equal([@"C:\A", @"C:\B"], r.GetPins().Select(p => p.Path));
        r.MovePin(@"C:\B", -1);
        Assert.Equal(@"C:\B", r.GetPins()[0].Path);
        r.RenamePin(@"C:\B", "Bee");
        Assert.Equal("Bee", r.GetPins()[0].Display);
        r.RemovePin(@"C:\B");
        Assert.Single(r.GetPins());
        Assert.Equal("A", r.GetPins()[0].Display);
    }

    [Fact]
    public void Visits_count_up()
    {
        using var t = new TempDb();
        var r = new LocationsRepository(t.Db);
        r.RecordVisit(@"C:\A", DateTimeOffset.Now.AddDays(-1));
        r.RecordVisit(@"C:\A", DateTimeOffset.Now);
        var v = Assert.Single(r.GetVisits());
        Assert.Equal(2, v.Count);
        r.ForgetVisit(@"C:\A");
        Assert.Empty(r.GetVisits());
    }
}

public class JournalRepositoryTests
{
    [Fact]
    public void Records_batches_and_marks_undone()
    {
        using var t = new TempDb();
        var j = new JournalRepository(t.Db);
        var id = j.Record("Move 2 items", "manual", [(JournalKind.Move, @"C:\a.txt", @"D:\a.txt", false), (JournalKind.Move, @"C:\b", @"D:\b", true)]);
        Assert.True(id > 0);
        var entries = j.GetEntries(id);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[1].IsFolder);
        j.MarkUndone(id);
        Assert.Empty(j.GetBatches(10));
        Assert.Single(j.GetBatches(10, includeUndone: true));
    }

    [Fact]
    public void Empty_batches_are_not_recorded()
    {
        using var t = new TempDb();
        Assert.Equal(0, new JournalRepository(t.Db).Record("Nothing", "manual", []));
    }

    [Fact]
    public void Trim_keeps_newest()
    {
        using var t = new TempDb();
        var j = new JournalRepository(t.Db);
        for (var i = 0; i < 5; i++) j.Record($"b{i}", "manual", [(JournalKind.Create, null, $@"C:\{i}", false)]);
        j.Trim(2);
        Assert.Equal(["b4", "b3"], j.GetBatches(10).Select(b => b.Title));
    }
}
