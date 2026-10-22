namespace Dpt.Ipc;

public enum VolumeState
{
    NotSelected,
    Waiting,
    Scanning,
    Ready,
    Unavailable,
    Error,
}

public sealed record VolumeStatus
{
    public required string Root { get; init; }
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public uint Serial { get; init; }
    public bool Selected { get; init; }
    public VolumeState State { get; init; }

    /// <summary>0..1 while scanning; 1 when ready.</summary>
    public double Progress { get; init; }

    public long Items { get; init; }

    /// <summary>"MFT" (direct NTFS read + change journal) or "Scan" (directory walk + watcher).</summary>
    public string Mode { get; init; } = "";

    public DateTime? LastUpdatedUtc { get; init; }
    public string? Message { get; init; }
}

public sealed record CatalogStatus
{
    public IReadOnlyList<VolumeStatus> Volumes { get; init; } = [];

    /// <summary>True when answered by the background service; false for the in-app catalog.</summary>
    public bool IsService { get; init; }

    public string Version { get; init; } = "";
    public bool IsReady => Volumes.Where(v => v.Selected).All(v => v.State is VolumeState.Ready or VolumeState.Unavailable);
}

public sealed record CatalogItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsFolder { get; init; }

    /// <summary>File size, or total size of everything inside a folder.</summary>
    public long Size { get; init; }

    /// <summary>Space actually used on disk (allocation), same rules as <see cref="Size"/>.</summary>
    public long SizeOnDisk { get; init; }

    public DateTime ModifiedUtc { get; init; }
    public uint Attributes { get; init; }

    /// <summary>Number of files inside (folders only).</summary>
    public long FileCount { get; init; }

    public double Score { get; init; }
}

public sealed record SearchRequest
{
    public required string Query { get; init; }

    /// <summary>Restrict to this folder and everything under it.</summary>
    public string? ScopePath { get; init; }

    public int Offset { get; init; }
    public int Limit { get; init; } = 200;
    public bool IncludeHidden { get; init; }
    public bool IncludeSystem { get; init; }

    /// <summary>Folder → boost (0..1) for frequently visited locations.</summary>
    public IReadOnlyDictionary<string, double>? LocationBoosts { get; init; }

    /// <summary>Optional "group=ext,ext;…" definition for type: filters.</summary>
    public string? TypeGroups { get; init; }

    /// <summary>Paths carrying requested categories (resolved by the app from its database); null = no category filter.</summary>
    public IReadOnlyList<string>? CategoryPaths { get; init; }

    public SortField Sort { get; init; } = SortField.Relevance;
    public bool Descending { get; init; } = true;
}

public enum SortField
{
    Relevance,
    Name,
    Size,
    Modified,
    Path,
}

public sealed record SearchResponse
{
    public IReadOnlyList<CatalogItem> Items { get; init; } = [];
    public int Total { get; init; }
    public double ElapsedMs { get; init; }

    /// <summary>True when some selected drives are still being catalogued, so results may be incomplete.</summary>
    public bool Partial { get; init; }
}

public enum LargestKind
{
    Files,
    Folders,
}

public sealed record LargestRequest
{
    public LargestKind Kind { get; init; } = LargestKind.Files;
    public int Count { get; init; } = 100;
    public string? Drive { get; init; }
    public string? UnderPath { get; init; }
    public string? TypeGroup { get; init; }
    public int? OlderThanDays { get; init; }
    public bool IncludeHidden { get; init; } = true;
    public string? TypeGroups { get; init; }

    /// <summary>For folders: skip folders whose parent is also in the list, so nested folders don't crowd the view.</summary>
    public bool TopLevelOnly { get; init; }
}

public enum ChangeKind
{
    Created,
    Deleted,
    Renamed,
    Modified,
}

public sealed record CatalogChange(ChangeKind Kind, string Path, string? OldPath, bool IsFolder);

/// <summary>
/// The search catalog, answered either by the background service (over a named pipe) or by an in-app engine.
/// Read-only except for <see cref="SetSelectedVolumesAsync"/>.
/// </summary>
public interface ICatalog
{
    Task<CatalogStatus> GetStatusAsync(CancellationToken ct = default);
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<long?>> GetFolderSizesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogItem>> GetLargestAsync(LargestRequest request, CancellationToken ct = default);

    /// <summary>Children of a folder with their (rolled-up) sizes, for drill-down views.</summary>
    Task<IReadOnlyList<CatalogItem>> ListFolderAsync(string path, CancellationToken ct = default);

    Task SetSelectedVolumesAsync(IReadOnlyList<string> roots, CancellationToken ct = default);

    /// <summary>Raised for file changes on selected volumes (used by automatic rules).</summary>
    event EventHandler<IReadOnlyList<CatalogChange>>? Changed;
}
