namespace Dpt.Shell.Operations;

public enum FileOperationKind
{
    Copy,
    Move,
    Rename,
    NewFolder,
    NewFile,
    Recycle,
    PermanentDelete,
}

public enum ConflictChoice
{
    /// <summary>Overwrite the existing item (folders are merged, as in File Explorer).</summary>
    Replace,

    /// <summary>Leave the existing item and do not copy/move this one.</summary>
    Skip,

    /// <summary>Copy/move under a new, unique name such as "Report (2).pdf".</summary>
    KeepBoth,
}

/// <summary>
/// Proof that the owner explicitly confirmed permanent deletion of exactly these paths in the confirmation dialog.
/// The engine refuses <see cref="FileOperationKind.PermanentDelete"/> without one whose paths match the request.
/// </summary>
public sealed class PermanentDeleteConfirmation
{
    private PermanentDeleteConfirmation(IReadOnlyCollection<string> paths, DateTimeOffset confirmedAt)
    {
        Paths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        ConfirmedAt = confirmedAt;
    }

    public IReadOnlySet<string> Paths { get; }
    public DateTimeOffset ConfirmedAt { get; }

    /// <summary>Only call this from UI code, directly in response to the owner pressing the confirm button.</summary>
    public static PermanentDeleteConfirmation ConfirmedByUser(IReadOnlyCollection<string> paths) => new(paths, DateTimeOffset.Now);

    public bool Covers(IEnumerable<string> paths) => paths.All(Paths.Contains);
}

public sealed record FileOperationRequest
{
    public required FileOperationKind Kind { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public string? DestinationFolder { get; init; }

    /// <summary>New name for Rename / NewFolder / NewFile.</summary>
    public string? NewName { get; init; }

    /// <summary>Per-source conflict decisions for Copy/Move. Unlisted conflicts default to <see cref="ConflictChoice.KeepBoth"/>.</summary>
    public IReadOnlyDictionary<string, ConflictChoice>? Conflicts { get; init; }

    public PermanentDeleteConfirmation? Confirmation { get; init; }
    public IntPtr OwnerWindow { get; init; }
    public string Title { get; init; } = "";
}

public sealed record CompletedItem(string? Source, string? Destination, bool IsFolder);

public sealed record FailedItem(string Path, string Message);

public sealed record BlockedItem(string Path, long? SizeBytes, string Reason);

public sealed class FileOperationResult
{
    public List<CompletedItem> Completed { get; } = [];
    public List<FailedItem> Failed { get; } = [];

    /// <summary>Items Windows could not put in the Recycle Bin. They were NOT deleted.</summary>
    public List<BlockedItem> BlockedFromRecycle { get; } = [];

    public List<string> Skipped { get; } = [];
    public bool Cancelled { get; set; }

    public bool Succeeded => Failed.Count == 0 && BlockedFromRecycle.Count == 0 && !Cancelled;
}

public readonly record struct FileOperationProgress(ulong WorkTotal, ulong WorkDone, string? CurrentItem)
{
    public double Fraction => WorkTotal == 0 ? 0 : Math.Clamp((double)WorkDone / WorkTotal, 0, 1);
}

/// <summary>Pause/resume/cancel handle for a running operation.</summary>
public sealed class OperationControl
{
    private readonly ManualResetEventSlim _running = new(true);
    private volatile bool _cancelled;

    public bool IsPaused => !_running.IsSet;
    public bool IsCancelled => _cancelled;

    public void Pause() => _running.Reset();
    public void Resume() => _running.Set();

    public void Cancel()
    {
        _cancelled = true;
        _running.Set();
    }

    internal void WaitIfPaused() => _running.Wait();
}
