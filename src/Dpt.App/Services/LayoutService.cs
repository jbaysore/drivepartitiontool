using System.IO.Enumeration;
using System.Text.Json;
using Dpt.Core.Layouts;
using Dpt.Core.Navigation;
using Dpt.Core.Rules;
using Dpt.Core.Search;
using Dpt.Data;
using Dpt.Shell.Operations;

namespace Dpt.App.Services;

public sealed record StoredLayout(long Id, LayoutDefinition Definition);

public sealed record LayoutApplyResult(int Moved, int Failed, int FoldersRemoved, long BatchId);

/// <summary>Saves layouts, previews them (virtual tree, no disk changes) and applies them as one undoable batch.</summary>
public sealed class LayoutService(AppServices services)
{
    private TypeGroups Groups => new(services.Settings.TypeGroups);

    public IReadOnlyList<StoredLayout> GetLayouts() =>
        services.Rules.GetLayouts()
            .Select(l => new StoredLayout(l.Id, JsonSerializer.Deserialize<LayoutDefinition>(l.Json, RulesRepository.Json) ?? new LayoutDefinition { Name = l.Name }))
            .ToList();

    public long Save(long? id, LayoutDefinition layout) =>
        services.Rules.SaveLayout(id, layout.Name, JsonSerializer.Serialize(layout, RulesRepository.Json));

    public void Delete(long id) => services.Rules.DeleteLayout(id);

    public Task<LayoutPlan> PlanAsync(LayoutDefinition layout, CancellationToken ct = default) => Task.Run(() =>
    {
        var files = new List<ItemFacts>();
        foreach (var src in layout.Sources.Where(Directory.Exists))
        {
            var e = new FileSystemEnumerable<(string, long, DateTime, DateTime)>(src,
                (ref FileSystemEntry x) => (x.ToFullPath(), x.Length, x.LastWriteTimeUtc.LocalDateTime, x.CreationTimeUtc.LocalDateTime),
                new EnumerationOptions { RecurseSubdirectories = layout.IncludeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint })
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            };
            foreach (var (path, size, modified, created) in e)
            {
                ct.ThrowIfCancellationRequested();
                files.Add(new ItemFacts(path, false, size, modified, created, services.Categories.CategoriesOf(path)));
            }
        }

        return LayoutPlanner.Plan(layout, files, p => File.Exists(p) || Directory.Exists(p), Groups);
    }, ct);

    /// <summary>Moves everything the plan says to move (in batches per destination folder) and records one undo batch.</summary>
    public async Task<LayoutApplyResult> ApplyAsync(LayoutPlan plan, IProgress<double>? progress = null)
    {
        var moves = plan.Entries.Where(e => e.Outcome == LayoutOutcome.Move && e.Destination is not null).ToList();
        var journal = new List<(string Kind, string? Source, string? Dest, bool IsFolder)>();
        var failed = 0;
        var activity = services.Activity.Start($"Applying layout \"{plan.Layout.Name}\"");
        var groups = moves.GroupBy(e => Path.GetDirectoryName(e.Destination!)!, StringComparer.OrdinalIgnoreCase).ToList();
        var done = 0;
        foreach (var g in groups)
        {
            Directory.CreateDirectory(g.Key);
            var batch = g.Where(e => File.Exists(e.Source)).ToList();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var slow = new List<LayoutEntry>();
            foreach (var e in batch)
            {
                // Same drive: a direct rename (instant). Never overwrites ("name (2)" when taken).
                if (!FastMove.SameVolume(e.Source, g.Key))
                {
                    slow.Add(e);
                    continue;
                }

                var moved = FastMove.MoveInto(e.Source, g.Key, claimed);
                if (moved is null)
                {
                    failed++;
                    continue;
                }

                journal.Add((JournalKind.Move, e.Source, moved, false));
                services.Categories.OnMoved(e.Source, moved);
            }

            if (slow.Count > 0)
            {
                // Different drive: copy + delete through the shell.
                var result = await services.Engine.RunAsync(new FileOperationRequest
                {
                    Kind = FileOperationKind.Move,
                    Sources = slow.Select(e => e.Source).ToList(),
                    DestinationFolder = g.Key,
                    OwnerWindow = services.OwnerWindow,
                });
                foreach (var c in result.Completed.Where(c => c.Source is not null && c.Destination is not null))
                {
                    journal.Add((JournalKind.Move, c.Source, c.Destination, false));
                    services.Categories.OnMoved(c.Source!, c.Destination!);
                }

                failed += slow.Count - result.Completed.Count;
            }

            done += g.Count();
            activity.Progress = (double)done / Math.Max(1, moves.Count);
            progress?.Report((double)done / Math.Max(1, moves.Count));
        }

        var removed = 0;
        if (plan.Layout.RemoveEmptyFolders)
            foreach (var src in plan.Layout.Sources.Where(Directory.Exists))
                removed += RemoveEmpty(src, keepRoot: true, protect: plan.Layout.TargetRoot);

        var id = services.Journal.Record($"Layout \"{plan.Layout.Name}\": {journal.Count} files moved", "layout", journal);
        activity.Complete($"{journal.Count} moved" + (failed > 0 ? $", {failed} failed" : ""), error: failed > 0);
        services.FileOps.NotifyChanged(plan.Layout.Sources.Append(plan.Layout.TargetRoot));
        foreach (var f in plan.Layout.Sources.Append(plan.Layout.TargetRoot)) services.FolderSizes.Invalidate(f);
        return new LayoutApplyResult(journal.Count, failed, removed, id);
    }

    /// <summary>Deletes folders under <paramref name="folder"/> that are completely empty (never files).</summary>
    private static int RemoveEmpty(string folder, bool keepRoot, string protect)
    {
        var removed = 0;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            removed += RemoveEmpty(sub, keepRoot: false, protect);
        }

        if (keepRoot || PathUtil.IsSameOrUnder(protect, folder)) return removed;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                removed++;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
    }

    /// <summary>Saves the layout's templates as automatic rules for new files (they still need a preview before moving).</summary>
    public IReadOnlyList<long> CreateRules(LayoutDefinition layout) =>
        LayoutPlanner.ToRules(layout).Select(r => services.Rules.Save(null, r)).ToList();
}
