using System.Runtime.InteropServices;
using Dpt.Shell.Interop;

namespace Dpt.Shell.Operations;

/// <summary>
/// Runs file operations through the Windows shell (<c>IFileOperation</c>) so behavior matches File Explorer
/// (shell copy hooks, elevation prompts, Recycle Bin, undo records), on a dedicated STA thread.
/// </summary>
public sealed class FileOperationEngine
{
    private const int MaxRecyclePasses = 32;

    /// <summary>Returns sources whose destination name already exists in <paramref name="destinationFolder"/>.</summary>
    public static IReadOnlyList<(string Source, string ExistingTarget)> FindConflicts(IEnumerable<string> sources, string destinationFolder)
    {
        var list = new List<(string, string)>();
        foreach (var s in sources)
        {
            var target = Path.Combine(destinationFolder, Path.GetFileName(s.TrimEnd('\\')));
            if (string.Equals(Path.GetFullPath(s), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(target) || Directory.Exists(target)) list.Add((s, target));
        }

        return list;
    }

    /// <summary>"Name (2).ext", "Name (3).ext", … — the first name that does not exist in the folder.</summary>
    public static string UniqueName(string folder, string name, bool isFolder, IReadOnlySet<string>? alsoTaken = null)
    {
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isFolder ? "" : Path.GetExtension(name);
        var candidate = name;
        bool Taken(string n) => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n)) || alsoTaken?.Contains(n) == true;
        for (var i = 2; Taken(candidate); i++)
            candidate = $"{stem} ({i}){ext}";
        return candidate;
    }

    public Task<FileOperationResult> RunAsync(FileOperationRequest request, IProgress<FileOperationProgress>? progress = null,
        OperationControl? control = null)
    {
        Validate(request);
        var tcs = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Execute(request, progress, control ?? new OperationControl()));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"FileOperation {request.Kind}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static void Validate(FileOperationRequest r)
    {
        switch (r.Kind)
        {
            case FileOperationKind.Copy or FileOperationKind.Move:
                if (r.Sources.Count == 0 || string.IsNullOrEmpty(r.DestinationFolder)) throw new ArgumentException("Copy/Move need sources and a destination.");
                break;
            case FileOperationKind.Rename:
                if (r.Sources.Count != 1 || string.IsNullOrWhiteSpace(r.NewName)) throw new ArgumentException("Rename needs one source and a new name.");
                break;
            case FileOperationKind.NewFolder or FileOperationKind.NewFile:
                if (string.IsNullOrEmpty(r.DestinationFolder) || string.IsNullOrWhiteSpace(r.NewName)) throw new ArgumentException("New item needs a folder and a name.");
                break;
            case FileOperationKind.Recycle:
                if (r.Sources.Count == 0) throw new ArgumentException("Nothing to recycle.");
                break;
            case FileOperationKind.PermanentDelete:
                if (r.Sources.Count == 0) throw new ArgumentException("Nothing to delete.");
                if (r.Confirmation is null || !r.Confirmation.Covers(r.Sources))
                    throw new InvalidOperationException("Permanent deletion requires the owner's explicit confirmation for every item.");
                break;
        }
    }

    private static FileOperationResult Execute(FileOperationRequest r, IProgress<FileOperationProgress>? progress, OperationControl control)
    {
        var result = new FileOperationResult();
        if (r.Kind == FileOperationKind.Recycle)
        {
            // Each pass recycles what it can. If Windows reports an item would be permanently deleted, the sink blocks it,
            // which may abort the rest of that pass; the next pass retries the remaining items without the blocked ones.
            var remaining = r.Sources.ToList();
            for (var pass = 0; pass < MaxRecyclePasses && remaining.Count > 0 && !control.IsCancelled; pass++)
            {
                var sink = RunPass(r, remaining, result, progress, control);
                var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in result.Completed) if (c.Source is not null) handled.Add(c.Source);
                foreach (var b in result.BlockedFromRecycle) handled.Add(b.Path);
                foreach (var f in result.Failed) handled.Add(f.Path);
                var next = remaining.Where(p => !handled.Contains(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
                if (!sink.BlockedThisPass || next.Count == remaining.Count) break;
                remaining = next;
            }
        }
        else
        {
            RunPass(r, r.Sources, result, progress, control);
        }

        result.Cancelled = control.IsCancelled;
        return result;
    }

    private static ProgressSink RunPass(FileOperationRequest r, IReadOnlyList<string> sources, FileOperationResult result,
        IProgress<FileOperationProgress>? progress, OperationControl control)
    {
        var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(NativeMethods.CLSID_FileOperation)!)!;
        var sink = new ProgressSink(r.Kind, result, progress, control);
        var cookie = op.Advise(sink);
        try
        {
            var flags = FileOperationFlags.FOF_SILENT | FileOperationFlags.FOF_NOCONFIRMMKDIR | FileOperationFlags.FOFX_SHOWELEVATIONPROMPT |
                        FileOperationFlags.FOFX_NOMINIMIZEBOX;
            switch (r.Kind)
            {
                case FileOperationKind.Recycle:
                    flags |= FileOperationFlags.FOF_ALLOWUNDO | FileOperationFlags.FOFX_RECYCLEONDELETE | FileOperationFlags.FOF_NOCONFIRMATION;
                    break;
                case FileOperationKind.PermanentDelete:
                    flags |= FileOperationFlags.FOF_NOCONFIRMATION;
                    break;
                case FileOperationKind.Copy or FileOperationKind.Move:
                    flags |= FileOperationFlags.FOF_ALLOWUNDO | FileOperationFlags.FOF_NOCONFIRMATION;
                    break;
                default:
                    flags |= FileOperationFlags.FOF_ALLOWUNDO;
                    break;
            }

            op.SetOperationFlags(flags);
            if (r.OwnerWindow != IntPtr.Zero) op.SetOwnerWindow(r.OwnerWindow);

            var queued = Queue(op, r, sources, result);
            if (queued == 0) return sink;

            try
            {
                op.PerformOperations();
            }
            catch (COMException ex) when (ex.HResult is RecycleGuard.BlockHResult or unchecked((int)0x80004004) or unchecked((int)0x80270000))
            {
                // Aborted by our sink (blocked recycle or user cancel). Details are already in the result.
            }
            catch (COMException ex)
            {
                result.Failed.Add(new FailedItem(sources.FirstOrDefault() ?? r.DestinationFolder ?? "", ex.Message));
            }
        }
        finally
        {
            op.Unadvise(cookie);
            Marshal.FinalReleaseComObject(op);
        }

        return sink;
    }

    private static int Queue(IFileOperation op, FileOperationRequest r, IReadOnlyList<string> sources, FileOperationResult result)
    {
        var count = 0;
        switch (r.Kind)
        {
            case FileOperationKind.Copy or FileOperationKind.Move:
            {
                var dest = NativeMethods.CreateItem(r.DestinationFolder!);
                // Names already claimed by earlier items in this same operation: two sources called "photo.jpg" from
                // different folders must never land on one name (the second would overwrite the first).
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in sources)
                {
                    var name = Path.GetFileName(s.TrimEnd('\\'));
                    var isDir = Directory.Exists(s);
                    var target = Path.Combine(r.DestinationFolder!, name);
                    string? newName = null;
                    var sameLocation = string.Equals(Path.GetFullPath(s).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase);
                    if (sameLocation)
                    {
                        if (r.Kind == FileOperationKind.Move)
                        {
                            result.Skipped.Add(s);
                            continue;
                        }

                        newName = UniqueName(r.DestinationFolder!, CopyName(name, isDir), isDir, claimed);
                    }
                    else if (claimed.Contains(name))
                    {
                        // Clashes with another item in this batch: always keep both.
                        newName = UniqueName(r.DestinationFolder!, name, isDir, claimed);
                    }
                    else if (File.Exists(target) || Directory.Exists(target))
                    {
                        var choice = r.Conflicts is not null && r.Conflicts.TryGetValue(s, out var c) ? c : ConflictChoice.KeepBoth;
                        if (choice == ConflictChoice.Skip)
                        {
                            result.Skipped.Add(s);
                            continue;
                        }

                        if (choice == ConflictChoice.KeepBoth) newName = UniqueName(r.DestinationFolder!, name, isDir, claimed);
                    }

                    claimed.Add(newName ?? name);
                    var item = NativeMethods.CreateItem(s);
                    if (r.Kind == FileOperationKind.Copy) op.CopyItem(item, dest, newName, null);
                    else op.MoveItem(item, dest, newName, null);
                    count++;
                }

                break;
            }
            case FileOperationKind.Rename:
                op.RenameItem(NativeMethods.CreateItem(sources[0]), r.NewName!, null);
                count++;
                break;
            case FileOperationKind.NewFolder:
                op.NewItem(NativeMethods.CreateItem(r.DestinationFolder!), NativeMethods.FILE_ATTRIBUTE_DIRECTORY, r.NewName!, null, null);
                count++;
                break;
            case FileOperationKind.NewFile:
                op.NewItem(NativeMethods.CreateItem(r.DestinationFolder!), NativeMethods.FILE_ATTRIBUTE_NORMAL, r.NewName!, null, null);
                count++;
                break;
            case FileOperationKind.Recycle or FileOperationKind.PermanentDelete:
                foreach (var s in sources)
                {
                    try
                    {
                        op.DeleteItem(NativeMethods.CreateItem(s), null);
                        count++;
                    }
                    catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException)
                    {
                        result.Failed.Add(new FailedItem(s, ex.Message));
                    }
                }

                break;
        }

        return count;
    }

    /// <summary>Explorer-style copy name: "Report - Copy.pdf".</summary>
    public static string CopyName(string name, bool isFolder) =>
        isFolder ? $"{name} - Copy" : $"{Path.GetFileNameWithoutExtension(name)} - Copy{Path.GetExtension(name)}";

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ProgressSink(FileOperationKind kind, FileOperationResult result, IProgress<FileOperationProgress>? progress,
        OperationControl control) : IFileOperationProgressSink
    {
        private const int S_OK = 0;
        private const int E_ABORT = unchecked((int)0x80004004);
        private string? _current;

        public bool BlockedThisPass { get; private set; }

        private int Gate()
        {
            control.WaitIfPaused();
            return control.IsCancelled ? E_ABORT : S_OK;
        }

        public int StartOperations() => S_OK;
        public int FinishOperations(int hrResult) => S_OK;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName)
        {
            _current = NativeMethods.GetPath(psiItem);
            return Gate();
        }

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated)
        {
            Record(psiItem, psiNewlyCreated, hrRename);
            return S_OK;
        }

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName)
        {
            _current = NativeMethods.GetPath(psiItem);
            return Gate();
        }

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated)
        {
            Record(psiItem, psiNewlyCreated, hrMove);
            return S_OK;
        }

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName)
        {
            _current = NativeMethods.GetPath(psiItem);
            return Gate();
        }

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated)
        {
            Record(psiItem, psiNewlyCreated, hrCopy);
            return S_OK;
        }

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            var path = NativeMethods.GetPath(psiItem);
            _current = path;
            if (kind == FileOperationKind.Recycle && RecycleGuard.WouldBePermanent(dwFlags))
            {
                if (path is not null && !result.BlockedFromRecycle.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)))
                    result.BlockedFromRecycle.Add(new BlockedItem(path, TrySize(path), "Windows can't put this item in the Recycle Bin."));
                BlockedThisPass = true;
                return RecycleGuard.BlockHResult;
            }

            return Gate();
        }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            var path = NativeMethods.GetPath(psiItem);
            if (path is null) return S_OK;
            if (hrDelete >= 0) result.Completed.Add(new CompletedItem(path, NativeMethods.GetPath(psiNewlyCreated), false));
            else if (hrDelete != RecycleGuard.BlockHResult && hrDelete != E_ABORT) result.Failed.Add(new FailedItem(path, Marshal.GetExceptionForHR(hrDelete)?.Message ?? $"0x{hrDelete:X8}"));
            return S_OK;
        }

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) => Gate();

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes,
            int hrNew, IShellItem? psiNewItem)
        {
            if (hrNew >= 0)
                result.Completed.Add(new CompletedItem(null, NativeMethods.GetPath(psiNewItem),
                    (dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0));
            else
                result.Failed.Add(new FailedItem(Path.Combine(NativeMethods.GetPath(psiDestinationFolder) ?? "", pszNewName ?? ""),
                    Marshal.GetExceptionForHR(hrNew)?.Message ?? $"0x{hrNew:X8}"));
            return S_OK;
        }

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
            progress?.Report(new FileOperationProgress(iWorkTotal, iWorkSoFar, _current));
            return Gate();
        }

        public int ResetTimer() => S_OK;
        public int PauseTimer() => S_OK;
        public int ResumeTimer() => S_OK;

        private void Record(IShellItem source, IShellItem? created, int hr)
        {
            var src = NativeMethods.GetPath(source);
            if (hr >= 0)
            {
                var dest = NativeMethods.GetPath(created);
                result.Completed.Add(new CompletedItem(src, dest, dest is not null && Directory.Exists(dest)));
            }
            else if (hr != E_ABORT && src is not null)
            {
                result.Failed.Add(new FailedItem(src, Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}"));
            }
        }

        private static long? TrySize(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path))
                    return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Sum(f => f.Length);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return null;
        }
    }
}
