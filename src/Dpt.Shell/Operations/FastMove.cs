namespace Dpt.Shell.Operations;

/// <summary>
/// Direct same-volume moves (a rename in the file system: instant, no copying). Used for large batches such as applying
/// a folder layout, where the shell's per-item overhead would take minutes. Never overwrites: picks a free name instead.
/// </summary>
public static class FastMove
{
    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves <paramref name="source"/> into <paramref name="destinationFolder"/> keeping its name (or "name (2)" if taken). Returns the new path, or null.</summary>
    public static string? MoveInto(string source, string destinationFolder, ISet<string>? claimed = null)
    {
        var isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source)) return null;
        Directory.CreateDirectory(destinationFolder);
        var name = Path.GetFileName(source.TrimEnd('\\'));
        var target = Path.Combine(destinationFolder, name);
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return target;
        var finalName = FileOperationEngine.UniqueName(destinationFolder, name, isDir, claimed as IReadOnlySet<string>);
        target = Path.Combine(destinationFolder, finalName);
        try
        {
            if (isDir) Directory.Move(source, target);
            else File.Move(source, target, overwrite: false);
            claimed?.Add(finalName);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves an item back to an exact path (undo). Fails rather than overwrite.</summary>
    public static bool MoveTo(string source, string exactTarget)
    {
        try
        {
            if (File.Exists(exactTarget) || Directory.Exists(exactTarget)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(exactTarget)!);
            if (Directory.Exists(source)) Directory.Move(source, exactTarget);
            else File.Move(source, exactTarget, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
