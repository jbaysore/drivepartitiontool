using System.IO.Enumeration;

namespace Dpt.Shell.FileSystem;

/// <summary>
/// Fallback folder-size computation by walking the tree. Used when the index service is unavailable.
/// Does not follow junctions/symlinks (matching how Explorer's Properties dialog counts).
/// </summary>
public static class FolderSizeCalculator
{
    public static long Compute(string folder, CancellationToken ct = default)
    {
        long total = 0;
        var enumeration = new FileSystemEnumerable<long>(folder, (ref FileSystemEntry e) => e.IsDirectory ? 0 : e.Length,
            new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            });
        foreach (var size in enumeration)
        {
            ct.ThrowIfCancellationRequested();
            total += size;
        }

        return total;
    }
}
