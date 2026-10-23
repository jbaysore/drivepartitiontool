using Dpt.Core.Formatting;
using Dpt.Core.Navigation;
using Dpt.Data;
using Dpt.Shell.Operations;

namespace Dpt.App.Services;

/// <summary>
/// App-level file operations: asks the owner about conflicts and blocked recycles, shows progress in the activity hub,
/// journals results for undo, and invalidates cached folder sizes.
/// </summary>
public sealed class FileOperationsService(AppServices services)
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public IDialogService Dialogs { get; set; } = null!;

    /// <summary>Raised after any operation changes files (for list refresh). Argument: affected folders.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? FilesChanged;

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name can't be empty.";
        if (name.IndexOfAny(InvalidNameChars) >= 0) return "A name can't contain any of these characters: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "A name can't end with a dot or a space.";
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return reserved.Contains(stem) ? $"\"{name}\" is reserved by Windows." : null;
    }

    public Task<FileOperationResult?> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, string source = "manual") =>
        TransferAsync(FileOperationKind.Copy, sources, destinationFolder, source);

    public Task<FileOperationResult?> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, string source = "manual") =>
        TransferAsync(FileOperationKind.Move, sources, destinationFolder, source);

    private async Task<FileOperationResult?> TransferAsync(FileOperationKind kind, IReadOnlyList<string> sources, string destinationFolder, string source)
    {
        if (sources.Count == 0) return null;
        if (kind == FileOperationKind.Move || kind == FileOperationKind.Copy)
        {
            foreach (var s in sources)
            {
                if (Directory.Exists(s) && PathUtil.IsSameOrUnder(destinationFolder, s) &&
                    !string.Equals(PathUtil.Normalize(s), PathUtil.Normalize(destinationFolder), StringComparison.OrdinalIgnoreCase))
                {
                    Dialogs.ShowError("Can't do that", $"\"{PathUtil.DisplayName(s)}\" can't be {(kind == FileOperationKind.Move ? "moved" : "copied")} into one of its own folders.");
                    return null;
                }
            }
        }

        var conflicts = FileOperationEngine.FindConflicts(sources, destinationFolder)
            .Select(c => new ConflictInfo(c.Source, c.ExistingTarget, Directory.Exists(c.Source))).ToList();
        IReadOnlyDictionary<string, ConflictChoice>? decisions = null;
        if (conflicts.Count > 0)
        {
            decisions = Dialogs.ResolveConflicts(conflicts, kind == FileOperationKind.Move);
            if (decisions is null) return null;
        }

        var verb = kind == FileOperationKind.Copy ? "Copying" : "Moving";
        var title = sources.Count == 1 ? $"{verb} {PathUtil.DisplayName(sources[0])}" : $"{verb} {sources.Count} items";
        var request = new FileOperationRequest
        {
            Kind = kind,
            Sources = sources,
            DestinationFolder = destinationFolder,
            Conflicts = decisions,
            OwnerWindow = services.OwnerWindow,
            Title = title,
        };
        var result = await RunWithActivityAsync(request, title);
        if (kind == FileOperationKind.Move)
            foreach (var c in result.Completed.Where(c => c.Source is not null && c.Destination is not null))
                services.Categories.OnMoved(c.Source!, c.Destination!);
        var journalKind = kind == FileOperationKind.Copy ? JournalKind.Copy : JournalKind.Move;
        Journal(title, source, result.Completed.Where(c => c.Source is not null && c.Destination is not null)
            .Select(c => (journalKind, c.Source, c.Destination, c.IsFolder)));
        ReportFailures(result, kind == FileOperationKind.Copy ? "copied" : "moved");
        Notify(sources.Select(s => Path.GetDirectoryName(s) ?? s).Append(destinationFolder));
        return result;
    }

    public async Task<string?> RenameAsync(string path, string newName)
    {
        newName = newName.Trim();
        if (ValidateName(newName) is { } problem)
        {
            Dialogs.ShowError("Can't rename", problem);
            return null;
        }

        var folder = Path.GetDirectoryName(path)!;
        if (string.Equals(Path.GetFileName(path), newName, StringComparison.Ordinal)) return path;
        var target = Path.Combine(folder, newName);
        var caseOnly = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
        {
            Dialogs.ShowError("Can't rename", $"There is already an item named \"{newName}\" here.");
            return null;
        }

        var result = await services.Engine.RunAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.Rename,
            Sources = [path],
            NewName = newName,
            OwnerWindow = services.OwnerWindow,
        });
        ReportFailures(result, "renamed");
        var done = result.Completed.FirstOrDefault();
        var newPath = done?.Destination ?? (File.Exists(target) || Directory.Exists(target) ? target : null);
        if (newPath is not null)
        {
            services.Categories.OnMoved(path, newPath);
            Journal($"Rename {Path.GetFileName(path)}", "manual", [(JournalKind.Rename, path, newPath, Directory.Exists(newPath))]);
        }
        Notify([folder]);
        return newPath;
    }

    /// <summary>
    /// Renames many items in one undoable batch. Uses temporary names first when a new name is another selected item's
    /// current name (so swaps and chains like 1→2, 2→3 work).
    /// </summary>
    public async Task<int> RenameManyAsync(IReadOnlyList<(string Path, string NewName)> renames, string title, string source = "manual")
    {
        var changes = renames.Where(r => !string.Equals(Path.GetFileName(r.Path), r.NewName, StringComparison.Ordinal)).ToList();
        if (changes.Count == 0) return 0;
        var activity = services.Activity.Start(title);
        var done = await RenameManyCoreAsync(changes, activity);
        Journal(title, source, done.Select(d => (JournalKind.Rename, (string?)d.From, (string?)d.To, Directory.Exists(d.To))));
        activity.Complete($"Renamed {done.Count} of {changes.Count}", error: done.Count < changes.Count);
        if (done.Count < changes.Count) Dialogs.ShowError("Some items couldn't be renamed", $"{changes.Count - done.Count} item(s) kept their old names (in use or access denied).");
        Notify(changes.Select(c => Path.GetDirectoryName(c.Path)!));
        return done.Count;
    }

    private async Task<List<(string From, string To)>> RenameManyCoreAsync(IReadOnlyList<(string Path, string NewName)> changes, ActivityItem? activity)
    {
        var selected = new HashSet<string>(changes.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        var needsTemp = changes.Any(c => selected.Contains(Path.Combine(Path.GetDirectoryName(c.Path)!, c.NewName)) &&
                                         !string.Equals(c.Path, Path.Combine(Path.GetDirectoryName(c.Path)!, c.NewName), StringComparison.OrdinalIgnoreCase));
        var current = changes.Select(c => c.Path).ToList();
        if (needsTemp)
        {
            for (var i = 0; i < changes.Count; i++)
            {
                var temp = $"~dpt-rename-{Guid.NewGuid():N}{Path.GetExtension(changes[i].Path)}";
                var r = await services.Engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Rename, Sources = [current[i]], NewName = temp, OwnerWindow = services.OwnerWindow });
                if (r.Succeeded) current[i] = Path.Combine(Path.GetDirectoryName(current[i])!, temp);
            }
        }

        var done = new List<(string, string)>();
        for (var i = 0; i < changes.Count; i++)
        {
            if (activity is not null) activity.Progress = (double)i / changes.Count;
            var target = Path.Combine(Path.GetDirectoryName(changes[i].Path)!, changes[i].NewName);
            var r = await services.Engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Rename, Sources = [current[i]], NewName = changes[i].NewName, OwnerWindow = services.OwnerWindow });
            if (r.Succeeded && Exists(target)) done.Add((changes[i].Path, target));
            else if (!string.Equals(current[i], changes[i].Path, StringComparison.OrdinalIgnoreCase))
            {
                // Put a temporarily renamed item back.
                await services.Engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Rename, Sources = [current[i]], NewName = Path.GetFileName(changes[i].Path) });
            }
        }

        return done;
    }

    /// <summary>"Duplicate here": copies items next to themselves ("Report - Copy.pdf").</summary>
    public async Task<FileOperationResult?> DuplicateAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var byFolder = paths.GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        FileOperationResult? last = null;
        foreach (var g in byFolder) last = await CopyAsync(g.ToList(), g.Key);
        return last;
    }

    public async Task<string?> NewFolderAsync(string folder, string? name = null)
    {
        name = FileOperationEngine.UniqueName(folder, name ?? "New folder", isFolder: true);
        var result = await services.Engine.RunAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.NewFolder,
            DestinationFolder = folder,
            NewName = name,
            OwnerWindow = services.OwnerWindow,
        });
        ReportFailures(result, "created");
        var created = result.Completed.FirstOrDefault()?.Destination ?? Path.Combine(folder, name);
        if (!Directory.Exists(created)) return null;
        Journal($"New folder {name}", "manual", [(JournalKind.Create, null, created, true)]);
        Notify([folder]);
        return created;
    }

    public async Task<string?> NewFileAsync(string folder, string name)
    {
        if (ValidateName(name) is { } problem)
        {
            Dialogs.ShowError("Can't create file", problem);
            return null;
        }

        name = FileOperationEngine.UniqueName(folder, name, isFolder: false);
        var result = await services.Engine.RunAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.NewFile,
            DestinationFolder = folder,
            NewName = name,
            OwnerWindow = services.OwnerWindow,
        });
        ReportFailures(result, "created");
        var created = result.Completed.FirstOrDefault()?.Destination ?? Path.Combine(folder, name);
        if (!File.Exists(created)) return null;
        Journal($"New file {name}", "manual", [(JournalKind.Create, null, created, false)]);
        Notify([folder]);
        return created;
    }

    /// <summary>
    /// Sends items to the Recycle Bin. Anything Windows can't recycle is never deleted automatically: the owner is asked,
    /// and only an explicit "Permanently delete" runs a separate, confirmed permanent deletion.
    /// </summary>
    public async Task<FileOperationResult?> RecycleAsync(IReadOnlyList<string> paths, string source = "manual", Func<string, long?>? knownSize = null)
    {
        if (paths.Count == 0) return null;
        if (services.Settings.ConfirmRecycle &&
            !Dialogs.Confirm("Move to Recycle Bin", paths.Count == 1 ? $"Move \"{PathUtil.DisplayName(paths[0])}\" to the Recycle Bin?" : $"Move {paths.Count} items to the Recycle Bin?", "Recycle"))
            return null;

        if (services.Settings.PrecheckRecycleBin)
        {
            var warnings = new List<(string, string)>();
            foreach (var p in paths)
            {
                long? size = File.Exists(p) ? new FileInfo(p).Length : knownSize?.Invoke(p) ?? services.FolderSizes.TryGetImmediate(p);
                if (RecycleBin.PrecheckReason(p, size) is { } reason) warnings.Add((p, reason));
            }

            if (warnings.Count > 0 && !Dialogs.WarnBeforeRecycle(warnings)) return null;
        }

        var title = paths.Count == 1 ? $"Recycling {PathUtil.DisplayName(paths[0])}" : $"Recycling {paths.Count} items";
        var result = await RunWithActivityAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.Recycle,
            Sources = paths,
            OwnerWindow = services.OwnerWindow,
            Title = title,
        }, title);

        Journal(title, source, result.Completed.Where(c => c.Source is not null)
            .Select(c => (JournalKind.Recycle, c.Source, c.Destination, Directory.Exists(c.Destination ?? ""))));
        ReportFailures(result, "recycled");
        Notify(paths.Select(p => Path.GetDirectoryName(p) ?? p));

        if (result.BlockedFromRecycle.Count > 0 && Dialogs.AskPermanentDeleteForBlocked(result.BlockedFromRecycle))
        {
            var blockedPaths = result.BlockedFromRecycle.Select(b => b.Path).ToList();
            await PermanentDeleteConfirmedAsync(blockedPaths, PermanentDeleteConfirmation.ConfirmedByUser(blockedPaths));
        }

        return result;
    }

    /// <summary>Shift+Delete: asks for explicit confirmation, then deletes permanently.</summary>
    public async Task<FileOperationResult?> PermanentDeleteAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        if (!Dialogs.ConfirmPermanentDelete(paths, "These items will be deleted permanently. They will not go to the Recycle Bin and can't be restored."))
            return null;
        return await PermanentDeleteConfirmedAsync(paths, PermanentDeleteConfirmation.ConfirmedByUser(paths));
    }

    private async Task<FileOperationResult> PermanentDeleteConfirmedAsync(IReadOnlyList<string> paths, PermanentDeleteConfirmation confirmation)
    {
        var title = paths.Count == 1 ? $"Permanently deleting {PathUtil.DisplayName(paths[0])}" : $"Permanently deleting {paths.Count} items";
        var result = await RunWithActivityAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.PermanentDelete,
            Sources = paths,
            Confirmation = confirmation,
            OwnerWindow = services.OwnerWindow,
            Title = title,
        }, title);
        ReportFailures(result, "deleted");
        Notify(paths.Select(p => Path.GetDirectoryName(p) ?? p));
        return result;
    }

    public bool CanUndo => services.Journal.GetBatches(1).Count > 0;

    public string? UndoDescription => services.Journal.GetBatches(1).FirstOrDefault()?.Title;

    /// <summary>Undoes the most recent batch (or a specific one). Returns a summary for the status line.</summary>
    public async Task<string?> UndoAsync(long? batchId = null)
    {
        var batch = batchId is { } id
            ? services.Journal.GetBatches(500).FirstOrDefault(b => b.Id == id)
            : services.Journal.GetBatches(1).FirstOrDefault();
        if (batch is null) return null;
        var entries = services.Journal.GetEntries(batch.Id).Reverse().ToList();
        var failures = 0;
        if (entries.Count > 1 && entries.All(e => e.Kind == JournalKind.Rename && e.SourcePath is not null && e.DestPath is not null))
        {
            // Bulk rename: undo as one batch (handles swaps/chains with temporary names).
            var back = entries.Where(e => Exists(e.DestPath!)).Select(e => (e.DestPath!, Path.GetFileName(e.SourcePath!))).ToList();
            var restored = await RenameManyCoreAsync(back, null);
            services.Journal.MarkUndone(batch.Id);
            Notify(entries.Select(e => Path.GetDirectoryName(e.SourcePath!)!));
            var missed = entries.Count - restored.Count;
            return missed == 0 ? $"Undid: {batch.Title}" : $"Undid {batch.Title} ({missed} item(s) could not be restored)";
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toRecycle = new List<string>();

        foreach (var e in entries)
        {
            try
            {
                switch (e.Kind)
                {
                    case JournalKind.Move when e.SourcePath is not null && e.DestPath is not null:
                    case JournalKind.Rename when e.SourcePath is not null && e.DestPath is not null:
                    {
                        if (!Exists(e.DestPath) || Exists(e.SourcePath))
                        {
                            failures++;
                            break;
                        }

                        var originalFolder = Path.GetDirectoryName(e.SourcePath)!;
                        Directory.CreateDirectory(originalFolder);
                        var r = await services.Engine.RunAsync(e.Kind == JournalKind.Rename
                            ? new FileOperationRequest { Kind = FileOperationKind.Rename, Sources = [e.DestPath], NewName = Path.GetFileName(e.SourcePath), OwnerWindow = services.OwnerWindow }
                            : new FileOperationRequest
                            {
                                Kind = FileOperationKind.Move,
                                Sources = [e.DestPath],
                                DestinationFolder = originalFolder,
                                OwnerWindow = services.OwnerWindow,
                            });
                        if (!r.Succeeded) failures++;
                        // A move into a different folder keeps the destination name; restore the original name too.
                        var landed = Path.Combine(originalFolder, Path.GetFileName(e.DestPath));
                        if (e.Kind == JournalKind.Move && !string.Equals(landed, e.SourcePath, StringComparison.OrdinalIgnoreCase) && Exists(landed))
                            await services.Engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Rename, Sources = [landed], NewName = Path.GetFileName(e.SourcePath) });
                        folders.Add(originalFolder);
                        folders.Add(Path.GetDirectoryName(e.DestPath)!);
                        break;
                    }
                    case JournalKind.Copy or JournalKind.Create when e.DestPath is not null:
                        if (Exists(e.DestPath)) toRecycle.Add(e.DestPath);
                        folders.Add(Path.GetDirectoryName(e.DestPath)!);
                        break;
                    case JournalKind.Tag when e.SourcePath is not null && long.TryParse(e.DestPath, out var categoryId):
                        // Tags were added before any move in the same batch; by now the item is back at its original path.
                        services.Categories.UntagById(e.SourcePath, categoryId);
                        break;
                    case JournalKind.Recycle when e.SourcePath is not null && e.DestPath is not null:
                        if (!RecycleBin.Restore(e.DestPath, e.SourcePath)) failures++;
                        folders.Add(Path.GetDirectoryName(e.SourcePath)!);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }

        if (toRecycle.Count > 0)
        {
            var r = await services.Engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Recycle, Sources = toRecycle, OwnerWindow = services.OwnerWindow });
            failures += r.Failed.Count + r.BlockedFromRecycle.Count;
        }

        services.Journal.MarkUndone(batch.Id);
        Notify(folders);
        return failures == 0 ? $"Undid: {batch.Title}" : $"Undid {batch.Title} ({failures} item(s) could not be restored)";
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private async Task<FileOperationResult> RunWithActivityAsync(FileOperationRequest request, string title)
    {
        var control = new OperationControl();
        var activity = services.Activity.Start(title, control.Cancel, paused => { if (paused) control.Pause(); else control.Resume(); });
        var progress = new Progress<FileOperationProgress>(p =>
        {
            activity.Progress = p.WorkTotal == 0 ? -1 : p.Fraction;
            if (p.CurrentItem is not null) activity.Detail = PathUtil.DisplayName(p.CurrentItem);
        });
        try
        {
            var result = await services.Engine.RunAsync(request, progress, control);
            var summary = result.Cancelled ? "Cancelled" :
                result.Failed.Count > 0 ? $"{result.Failed.Count} item(s) failed" :
                result.BlockedFromRecycle.Count > 0 ? $"{result.BlockedFromRecycle.Count} item(s) need your decision" :
                $"Done · {result.Completed.Count} item(s)";
            activity.Complete(summary, error: result.Failed.Count > 0);
            return result;
        }
        catch (Exception ex)
        {
            activity.Complete(ex.Message, error: true);
            Diagnostics.Log.Error("FileOps", ex);
            throw;
        }
    }

    private void Journal(string title, string source, IEnumerable<(string Kind, string? Source, string? Dest, bool IsFolder)> entries)
    {
        services.Journal.Record(title, source, entries);
        services.Journal.Trim(Math.Max(1, services.Settings.UndoDepth));
    }

    private void ReportFailures(FileOperationResult result, string verb)
    {
        if (result.Failed.Count == 0) return;
        var lines = result.Failed.Take(8).Select(f => $"• {PathUtil.DisplayName(f.Path)}: {f.Message}");
        var more = result.Failed.Count > 8 ? $"\n…and {result.Failed.Count - 8} more." : "";
        Dialogs.ShowError("Some items couldn't be " + verb, string.Join("\n", lines) + more);
    }

    public void NotifyChanged(IEnumerable<string> folders) => Notify(folders);

    private void Notify(IEnumerable<string> folders)
    {
        var set = folders.Where(f => !string.IsNullOrEmpty(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in set) services.FolderSizes.Invalidate(f);
        FilesChanged?.Invoke(this, set);
    }

    public static string DescribeSize(long? bytes) => bytes is { } b ? SizeFormatter.Format(b) : "unknown size";
}
