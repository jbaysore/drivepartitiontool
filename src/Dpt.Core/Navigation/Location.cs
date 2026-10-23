namespace Dpt.Core.Navigation;

public enum LocationKind
{
    Home,
    Folder,
    Search,
    LargestItems,
    Drives,
    Rules,
    Layouts,
    DiskTools,
    Settings,
}

/// <summary>What a tab is showing. Folder locations carry a full Windows path; Search carries the query.</summary>
public sealed record Location(LocationKind Kind, string? Path = null, string? Query = null)
{
    public static Location Home { get; } = new(LocationKind.Home);

    public static Location Folder(string path) => new(LocationKind.Folder, PathUtil.Normalize(path));

    public static Location Search(string query, string? scopePath) => new(LocationKind.Search, scopePath, query);

    public static Location Page(LocationKind kind) => new(kind);

    public string Title => Kind switch
    {
        LocationKind.Home => "Home",
        LocationKind.Folder => PathUtil.DisplayName(Path!),
        LocationKind.Search => $"Search: {Query}",
        LocationKind.LargestItems => "Largest items",
        LocationKind.Drives => "Drives",
        LocationKind.Rules => "Rules & categories",
        LocationKind.Layouts => "Folder layouts",
        LocationKind.DiskTools => "Disk tools",
        LocationKind.Settings => "Settings",
        _ => Kind.ToString(),
    };

    public bool Equals(Location? other) =>
        other is not null && Kind == other.Kind &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Query, other.Query, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, Path?.ToUpperInvariant(), Query);
}
