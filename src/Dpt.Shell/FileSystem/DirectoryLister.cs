using System.IO.Enumeration;

namespace Dpt.Shell.FileSystem;

public sealed record ListOptions(bool ShowHidden = false, bool ShowSystem = false);

/// <summary>Fast folder listing using <see cref="FileSystemEnumerable{TResult}"/> (one native call per batch, no extra stat calls).</summary>
public static class DirectoryLister
{
    public static List<FileEntry> List(string folder, ListOptions options, CancellationToken ct = default)
    {
        var skip = (FileAttributes)0;
        if (!options.ShowHidden) skip |= FileAttributes.Hidden;
        if (!options.ShowSystem) skip |= FileAttributes.System;

        var enumeration = new FileSystemEnumerable<FileEntry>(folder, Transform, new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = skip,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        });

        var list = new List<FileEntry>();
        foreach (var e in enumeration)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(e);
        }

        return list;
    }

    private static FileEntry Transform(ref FileSystemEntry e)
    {
        var isDir = e.IsDirectory;
        return new FileEntry(
            e.ToFullPath(),
            e.FileName.ToString(),
            isDir,
            isDir ? -1 : e.Length,
            e.LastWriteTimeUtc.LocalDateTime,
            e.CreationTimeUtc.LocalDateTime,
            e.Attributes);
    }

    public static FileEntry? TryGet(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
                return new FileEntry(info.FullName, info.Name, false, info.Length, info.LastWriteTime, info.CreationTime, info.Attributes);
            var dir = new DirectoryInfo(path);
            if (dir.Exists)
                return new FileEntry(dir.FullName, dir.Name, true, -1, dir.LastWriteTime, dir.CreationTime, dir.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }
}
