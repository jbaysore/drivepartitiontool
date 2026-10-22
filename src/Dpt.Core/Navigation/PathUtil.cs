namespace Dpt.Core.Navigation;

/// <summary>Windows path helpers that do not touch the file system.</summary>
public static class PathUtil
{
    /// <summary>Normalizes separators and trailing slashes; drive roots keep their slash ("C:\").</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.", nameof(path));
        var p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.Length == 2 && p[1] == ':') return char.ToUpperInvariant(p[0]) + ":\\";
        if (p.Length >= 2 && p[1] == ':') p = char.ToUpperInvariant(p[0]) + p[1..];
        while (p.Length > 3 && p.EndsWith('\\') && !p.EndsWith(":\\", StringComparison.Ordinal)) p = p[..^1];
        return p;
    }

    public static bool IsDriveRoot(string path) => path.Length == 3 && path[1] == ':' && path[2] == '\\';

    public static string DisplayName(string path)
    {
        var p = Normalize(path);
        if (IsDriveRoot(p)) return p[..2];
        var i = p.LastIndexOf('\\');
        return i >= 0 && i < p.Length - 1 ? p[(i + 1)..] : p;
    }

    /// <summary>Parent folder, or null for a drive root.</summary>
    public static string? Parent(string path)
    {
        var p = Normalize(path);
        if (IsDriveRoot(p)) return null;
        var i = p.LastIndexOf('\\');
        if (i < 0) return null;
        if (i == 2 && p[1] == ':') return p[..3];
        if (p.StartsWith(@"\\", StringComparison.Ordinal) && p.Count(c => c == '\\') <= 3) return null;
        return p[..i];
    }

    /// <summary>Path segments for a breadcrumb: "C:\Users\You" → ["C:\", "C:\Users", "C:\Users\You"].</summary>
    public static IReadOnlyList<string> Ancestry(string path)
    {
        var list = new List<string>();
        for (string? p = Normalize(path); p is not null; p = Parent(p)) list.Add(p);
        list.Reverse();
        return list;
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string DriveOf(string path) => Normalize(path) is { Length: >= 2 } p && p[1] == ':' ? p[..2] : "";
}
