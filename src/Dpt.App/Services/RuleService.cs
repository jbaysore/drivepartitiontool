using System.Collections.Concurrent;
using System.IO.Enumeration;
using Dpt.App.Diagnostics;
using Dpt.Core.Navigation;
using Dpt.Core.Rules;
using Dpt.Core.Search;
using Dpt.Data;
using Dpt.Shell.Operations;

namespace Dpt.App.Services;

public sealed record RulePlanEntry(string Path, bool IsFolder, long Size, RuleAction Action, bool DestinationExists);

public sealed record RulePlan(StoredRule Rule, IReadOnlyList<RulePlanEntry> Entries, int Scanned)
{
    public int MoveCount => Entries.Count(e => e.Action.Moves);
    public int TagCount => Entries.Count(e => e.Action.AddCategories.Count > 0);
    public long MoveBytes => Entries.Where(e => e.Action.Moves).Sum(e => e.Size);
    public int ConflictCount => Entries.Count(e => e.DestinationExists);
}

public sealed record RuleRunResult(int Moved, int Tagged, int Skipped, int Failed, long BatchId);

/// <summary>
/// Plans and runs rules: previews (what would move/tag), manual runs over existing items, and watchers that apply
/// automatic rules to new items. Every run is one undoable journal batch.
/// </summary>
public sealed class RuleService : IDisposable
{
    private static readonly string[] TemporarySuffixes = [".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload"];
    private readonly AppServices _services;
    private readonly Dictionary<long, List<FileSystemWatcher>> _watchers = [];
    private readonly ConcurrentDictionary<string, (long RuleId, DateTime Seen)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _settleTimer;
    private readonly SemaphoreSlim _runGate = new(1);

    public RuleService(AppServices services)
    {
        _services = services;
        _settleTimer = new Timer(_ => _ = ProcessPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
        services.Rules.Changed += (_, _) => RefreshWatchers();
    }

    public event EventHandler<string>? Notified;

    private TypeGroups Groups => new(_services.Settings.TypeGroups);

    // ---------------- Planning ----------------

    public Task<RulePlan> PlanAsync(StoredRule rule, CancellationToken ct = default) => Task.Run(() =>
    {
        var entries = new List<RulePlanEntry>();
        var scanned = 0;
        foreach (var folder in rule.Definition.Folders.Where(Directory.Exists))
        {
            foreach (var (path, isDir, size, modified, created) in Enumerate(folder, rule.Definition.IncludeSubfolders))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                var plan = PlanItem(rule.Definition, path, isDir, size, modified, created);
                if (plan is not null) entries.Add(plan);
            }
        }

        return new RulePlan(rule, entries, scanned);
    }, ct);

    private RulePlanEntry? PlanItem(RuleDefinition def, string path, bool isDir, long size, DateTime modified, DateTime created)
    {
        var facts = new ItemFacts(path, isDir, size, modified, created, _services.Categories.CategoriesOf(path));
        if (!RuleEngine.Applies(def, facts, Groups)) return null;
        var action = RuleEngine.Plan(def, facts, Groups);
        if (!action.Moves && action.AddCategories.Count == 0 && action.Note is null) return null; // nothing to do
        var exists = action.Moves && (File.Exists(action.Destination) || Directory.Exists(action.Destination));
        return new RulePlanEntry(path, isDir, size, action, exists);
    }

    private static IEnumerable<(string Path, bool IsDir, long Size, DateTime Modified, DateTime Created)> Enumerate(string folder, bool recurse)
    {
        var e = new FileSystemEnumerable<(string, bool, long, DateTime, DateTime)>(folder,
            (ref FileSystemEntry x) => (x.ToFullPath(), x.IsDirectory, x.IsDirectory ? 0 : x.Length, x.LastWriteTimeUtc.LocalDateTime, x.CreationTimeUtc.LocalDateTime),
            new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System });
        return e;
    }

    // ---------------- Running ----------------

    /// <summary>
    /// Runs a plan. Rules that move files must have been previewed first (<paramref name="fromPreview"/> marks that).
    /// </summary>
    public async Task<RuleRunResult> RunAsync(RulePlan plan, bool fromPreview, string source = "rule")
    {
        var rule = plan.Rule;
        if (rule.Definition.Moves && !rule.Previewed && !fromPreview)
            throw new InvalidOperationException("Rules that move files must be previewed before their first run.");
        if (fromPreview && !rule.Previewed) _services.Rules.MarkPreviewed(rule.Id);

        await _runGate.WaitAsync();
        try
        {
            var journal = new List<(string Kind, string? Source, string? Dest, bool IsFolder)>();
            int moved = 0, tagged = 0, skipped = 0, failed = 0;
            var activity = _services.Activity.Start($"Rule: {rule.Definition.Name}");
            var entries = plan.Entries.Where(e => e.Action.Note is null).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                activity.Progress = (double)i / Math.Max(1, entries.Count);
                if (!File.Exists(e.Path) && !Directory.Exists(e.Path))
                {
                    skipped++;
                    continue;
                }

                // Tag first (tags follow the file ID through the move).
                if (e.Action.AddCategories.Count > 0)
                {
                    var added = _services.Categories.Tag([e.Path], e.Action.AddCategories, journalIt: false);
                    journal.AddRange(added.Select(a => (JournalKind.Tag, (string?)a.Path, (string?)a.CategoryId.ToString(), false)));
                    if (added.Count > 0) tagged++;
                }

                if (!e.Action.Moves) continue;
                var destFolder = e.Action.MoveToFolder!;
                Directory.CreateDirectory(destFolder);
                var target = e.Action.Destination!;
                ConflictChoice? choice = null;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    choice = rule.Definition.OnConflict switch { ConflictPolicy.Skip => ConflictChoice.Skip, ConflictPolicy.Replace => ConflictChoice.Replace, _ => ConflictChoice.KeepBoth };
                    if (choice == ConflictChoice.Skip)
                    {
                        skipped++;
                        continue;
                    }
                }

                var r = await _services.Engine.RunAsync(new FileOperationRequest
                {
                    Kind = FileOperationKind.Move,
                    Sources = [e.Path],
                    DestinationFolder = destFolder,
                    Conflicts = choice is { } c ? new Dictionary<string, ConflictChoice> { [e.Path] = c } : null,
                    OwnerWindow = _services.OwnerWindow,
                });
                var done = r.Completed.FirstOrDefault(x => x.Source is not null && x.Destination is not null);
                if (done is null)
                {
                    failed++;
                    continue;
                }

                moved++;
                _services.Categories.OnMoved(done.Source!, done.Destination!);
                journal.Add((JournalKind.Move, done.Source, done.Destination, e.IsFolder));
                _services.FolderSizes.Invalidate(Path.GetDirectoryName(done.Source!)!);
                _services.FolderSizes.Invalidate(destFolder);
            }

            var title = $"Rule \"{rule.Definition.Name}\": {moved} moved, {tagged} tagged";
            var batch = _services.Journal.Record(title, source, journal);
            _services.Rules.MarkRun(rule.Id);
            activity.Complete(title, error: failed > 0);
            if (moved + tagged > 0) _services.FileOps.NotifyChanged(journal.SelectMany(j => new[] { j.Source, j.Dest }).Where(p => p is not null && Path.IsPathRooted(p)).Select(p => Path.GetDirectoryName(p!)!));
            return new RuleRunResult(moved, tagged, skipped, failed, batch);
        }
        finally
        {
            _runGate.Release();
        }
    }

    // ---------------- Automatic rules ----------------

    public void Start() => RefreshWatchers();

    public void RefreshWatchers()
    {
        lock (_watchers)
        {
            foreach (var list in _watchers.Values)
                foreach (var w in list) w.Dispose();
            _watchers.Clear();
            if (_services.Settings.PauseAutomaticRules) return;
            foreach (var rule in _services.Rules.GetRules().Where(r => r.Enabled && r.Definition.Trigger == RuleTrigger.Automatic && r.Definition.Scope != RuleScope.ExistingItems))
            {
                var list = new List<FileSystemWatcher>();
                foreach (var folder in rule.Definition.Folders.Where(Directory.Exists))
                {
                    try
                    {
                        var w = new FileSystemWatcher(folder)
                        {
                            IncludeSubdirectories = rule.Definition.IncludeSubfolders,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                        };
                        var id = rule.Id;
                        w.Created += (_, e) => Queue(e.FullPath, id);
                        w.Renamed += (_, e) => Queue(e.FullPath, id);
                        w.Changed += (_, e) => { if (_pending.ContainsKey(e.FullPath)) Queue(e.FullPath, id); };
                        w.EnableRaisingEvents = true;
                        list.Add(w);
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                    {
                        Log.Error("Rules", ex);
                    }
                }

                _watchers[rule.Id] = list;
            }
        }
    }

    private void Queue(string path, long ruleId)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("~$", StringComparison.Ordinal) || TemporarySuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return;
        _pending[path] = (ruleId, DateTime.UtcNow);
        _settleTimer.Change(1000, Timeout.Infinite);
    }

    /// <summary>Applies automatic rules to new items once they've stopped changing (e.g. a download finished).</summary>
    public async Task ProcessPendingAsync()
    {
        var now = DateTime.UtcNow;
        var ready = new List<(string Path, long RuleId)>();
        foreach (var (path, (ruleId, seen)) in _pending)
        {
            var rule = _services.Rules.Get(ruleId);
            if (rule is null)
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            if ((now - seen).TotalSeconds < rule.Definition.SettleSeconds) continue;
            if (_pending.TryRemove(path, out _) && IsSettled(path)) ready.Add((path, ruleId));
        }

        foreach (var group in ready.GroupBy(r => r.RuleId))
        {
            var rule = _services.Rules.Get(group.Key);
            if (rule is null || !rule.Enabled || _services.Settings.PauseAutomaticRules) continue;
            if (rule.Definition.Moves && !rule.Previewed)
            {
                Notified?.Invoke(this, $"Rule \"{rule.Definition.Name}\" is waiting for you to preview it before it moves anything.");
                continue;
            }

            var entries = new List<RulePlanEntry>();
            foreach (var (path, _) in group)
            {
                var isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) continue;
                FileSystemInfo info = isDir ? new DirectoryInfo(path) : new FileInfo(path);
                var e = PlanItem(rule.Definition, path, isDir, isDir ? 0 : ((FileInfo)info).Length, info.LastWriteTime, info.CreationTime);
                if (e is not null) entries.Add(e);
            }

            if (entries.Count == 0) continue;
            try
            {
                var result = await RunAsync(new RulePlan(rule, entries, entries.Count), fromPreview: false, source: "automatic rule");
                Notified?.Invoke(this, $"Rule \"{rule.Definition.Name}\": {result.Moved} moved, {result.Tagged} tagged. Ctrl+Z undoes it.");
            }
            catch (Exception ex)
            {
                Log.Error("Rules", ex);
            }
        }

        if (!_pending.IsEmpty) _settleTimer.Change(1000, Timeout.Infinite);
    }

    private static bool IsSettled(string path)
    {
        if (Directory.Exists(path)) return true;
        try
        {
            // A file still being written (e.g. a download) can't be opened without sharing writes.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool HasAutomaticRules => _services.Rules.GetRules().Any(r => r.Enabled && r.Definition.Trigger == RuleTrigger.Automatic);

    public void Dispose()
    {
        _settleTimer.Dispose();
        lock (_watchers)
            foreach (var list in _watchers.Values)
                foreach (var w in list) w.Dispose();
    }
}
