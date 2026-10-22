using System.IO.Enumeration;

namespace Dpt.Index.Scan;

/// <summary>
/// Builds a catalog by walking folders (any file system, no admin rights needed). Junctions/symlinks are listed but
/// not followed. Entries get synthetic references from <see cref="RefSource"/>.
/// </summary>
public sealed class DirectoryScanner(long clusterSize)
{
    private long _nextRef = 1;

    public ulong NewRef() => (ulong)Interlocked.Increment(ref _nextRef);

    public long ClusterSize { get; } = clusterSize <= 0 ? 4096 : clusterSize;

    public long OnDisk(long size) => size == 0 ? 0 : (size + ClusterSize - 1) / ClusterSize * ClusterSize;

    private readonly struct Item(string name, bool isDir, uint attrs, long size, long modified)
    {
        public readonly string Name = name;
        public readonly bool IsDir = isDir;
        public readonly uint Attrs = attrs;
        public readonly long Size = size;
        public readonly long Modified = modified;
    }

    /// <summary>Full scan of <paramref name="index"/>'s root. Progress is reported per top-level folder.</summary>
    public void ScanInto(VolumeIndex index, IProgress<double>? progress, CancellationToken ct)
    {
        index.BeginBulk(64 * 1024);
        var rootRef = NewRef();
        var rootInfo = new DirectoryInfo(index.Root);
        index.BulkAdd(rootRef, rootRef, "", true, (uint)rootInfo.Attributes, 0, 0, rootInfo.LastWriteTimeUtc.Ticks);
        var top = List(index.Root);
        var done = 0;
        var queue = new Stack<(string Path, ulong Ref)>();
        foreach (var it in top)
        {
            var r = NewRef();
            index.BulkAdd(r, rootRef, it.Name, it.IsDir, it.Attrs, it.Size, OnDisk(it.Size), it.Modified);
            if (it.IsDir && (it.Attrs & (uint)FileAttributes.ReparsePoint) == 0) queue.Push((Path.Combine(index.Root, it.Name), r));
        }

        var topCount = Math.Max(1, queue.Count);
        var topLevel = new HashSet<ulong>(queue.Select(q => q.Ref));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, parentRef) = queue.Pop();
            if (topLevel.Contains(parentRef)) progress?.Report(0.99 * done++ / topCount);
            foreach (var it in List(path))
            {
                var r = NewRef();
                index.BulkAdd(r, parentRef, it.Name, it.IsDir, it.Attrs, it.Size, OnDisk(it.Size), it.Modified);
                if (it.IsDir && (it.Attrs & (uint)FileAttributes.ReparsePoint) == 0) queue.Push((Path.Combine(path, it.Name), r));
            }
        }

        index.SetRoot(rootRef);
        index.EndBulk();
        progress?.Report(1);
    }

    /// <summary>Adds (or refreshes) a path and, for folders, everything under it. Caller holds the write lock.</summary>
    public void AddTree(VolumeIndex index, string path)
    {
        var parentPath = Path.GetDirectoryName(path.TrimEnd('\\'));
        if (parentPath is null) return;
        var parent = index.Find(parentPath);
        if (parent == VolumeIndex.NoEntry) return;
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) return;
        var isDir = info is DirectoryInfo;
        var existing = index.Find(path);
        var fileRef = existing != VolumeIndex.NoEntry ? index.RefOf(existing) : NewRef();
        var size = isDir ? 0 : ((FileInfo)info).Length;
        var i = index.Upsert(fileRef, index.RefOf(parent), info.Name, isDir, (uint)info.Attributes, size, OnDisk(size), info.LastWriteTimeUtc.Ticks);
        if (i == VolumeIndex.NoEntry || !isDir || (info.Attributes & FileAttributes.ReparsePoint) != 0) return;

        var stack = new Stack<(string, ulong)>();
        stack.Push((path, fileRef));
        while (stack.Count > 0)
        {
            var (p, r) = stack.Pop();
            var parentIndex = index.IndexOf(r);
            var known = parentIndex == VolumeIndex.NoEntry
                ? new Dictionary<string, ulong>()
                : index.Children(parentIndex).ToDictionary(c => index.Name(c), c => index.RefOf(c), StringComparer.OrdinalIgnoreCase);
            foreach (var it in List(p))
            {
                var childRef = known.TryGetValue(it.Name, out var k) ? k : NewRef();
                index.Upsert(childRef, r, it.Name, it.IsDir, it.Attrs, it.Size, OnDisk(it.Size), it.Modified);
                if (it.IsDir && (it.Attrs & (uint)FileAttributes.ReparsePoint) == 0) stack.Push((Path.Combine(p, it.Name), childRef));
            }
        }
    }

    private static List<Item> List(string folder)
    {
        var list = new List<Item>();
        try
        {
            var e = new FileSystemEnumerable<Item>(folder,
                (ref FileSystemEntry x) => new Item(x.FileName.ToString(), x.IsDirectory, (uint)x.Attributes, x.IsDirectory ? 0 : x.Length, x.LastWriteTimeUtc.UtcDateTime.Ticks),
                new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false, AttributesToSkip = 0 });
            list.AddRange(e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return list;
    }
}
