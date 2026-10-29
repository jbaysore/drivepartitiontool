namespace Dpt.Index;

/// <summary>How scan mode may read a catalog root. Whole volumes are never watched recursively.</summary>
public enum CatalogScanKind
{
    /// <summary>The saved catalog is served as-is.</summary>
    ServeSnapshot,

    /// <summary>One background walk, then the saved catalog. No watcher.</summary>
    ThrottledWalk,

    /// <summary>A folder tree is walked and watched while the process is open.</summary>
    WatchedFolder,
}

/// <summary>
/// Decides whether startup may walk or watch a root. A whole volume is a drive root such as C:\.
/// Walking or watching those roots at normal priority can stall the machine, so a saved catalog is served
/// and a first scan or an explicit rebuild runs one drive at a time in the background.
/// </summary>
public static class CatalogScanPolicy
{
    public const int WatcherBufferBytes = 64 * 1024;

    public const string SavedCatalogMessage = "saved catalog";

    /// <summary>Gap between directories on a whole-volume walk, so the disk stays available to the rest of Windows.</summary>
    public static readonly TimeSpan VolumeWalkPause = TimeSpan.FromMilliseconds(1);

    public static bool IsWholeVolume(string root)
    {
        var normalized = root.EndsWith('\\') ? root : root + "\\";
        return normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '\\';
    }

    public static CatalogScanKind Decide(string root, bool hasSnapshot, bool explicitRebuild)
    {
        if (!IsWholeVolume(root)) return CatalogScanKind.WatchedFolder;
        if (hasSnapshot && !explicitRebuild) return CatalogScanKind.ServeSnapshot;
        return CatalogScanKind.ThrottledWalk;
    }

    public static bool Watches(CatalogScanKind kind) => kind == CatalogScanKind.WatchedFolder;
}
