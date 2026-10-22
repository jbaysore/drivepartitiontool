using System.Collections.Concurrent;
using System.Diagnostics;
using Dpt.Core.Search;
using Dpt.Ipc;

namespace Dpt.Index;

/// <summary>Runs searches, folder-size lookups and largest-item queries over in-memory volume indexes.</summary>
public static class SearchExecutor
{
    private const uint Hidden = (uint)FileAttributes.Hidden;
    private const uint System = (uint)FileAttributes.System;
    private const int ChunkSize = 64 * 1024;

    private readonly record struct Hit(VolumeIndex Volume, int Index, double Score);

    public static SearchResponse Search(IReadOnlyList<VolumeIndex> volumes, SearchRequest request, bool partial)
    {
        var sw = Stopwatch.StartNew();
        var groups = request.TypeGroups is { Length: > 0 } def ? new TypeGroups(def) : TypeGroups.Default;
        var q = SearchQuery.Parse(request.Query, groups: groups);
        var scope = q.InPath ?? request.ScopePath;
        IReadOnlySet<string>? typeExts = q.TypeGroup is not null && groups.TryGet(q.TypeGroup, out var te) ? te : null;
        var now = DateTime.UtcNow;
        var fromTicks = q.ModifiedFrom?.ToUniversalTime().Ticks ?? long.MinValue;
        var toTicks = q.ModifiedTo?.ToUniversalTime().Ticks ?? long.MaxValue;
        var hideMask = (request.IncludeHidden ? 0u : Hidden) | (request.IncludeSystem ? 0u : System);
        var hits = new ConcurrentBag<List<Hit>>();

        HashSet<string>? categorySet = request.CategoryPaths is null ? null : new HashSet<string>(request.CategoryPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var vol in volumes)
        {
            if (q.Drives.Count > 0 && !q.Drives.Any(d => vol.Root.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
            if (scope is not null && !IsOnVolume(scope, vol.Root)) continue;
            vol.Lock.EnterReadLock();
            try
            {
                if (vol.RootIndex == VolumeIndex.NoEntry) continue;
                var scopeIndex = scope is null ? VolumeIndex.NoEntry : vol.Find(scope);
                if (scope is not null && scopeIndex == VolumeIndex.NoEntry) continue;

                bool Matches(int i)
                {
                    ref var e = ref vol.Entries[i];
                    if (!e.InUse || i == vol.RootIndex) return false;
                    if (q.Kind == ItemKindFilter.FilesOnly && e.IsDirectory) return false;
                    if (q.Kind == ItemKindFilter.FoldersOnly && !e.IsDirectory) return false;
                    var name = vol.Names.AsSpan(e.NameStart, e.NameLength);
                    if (q.Extensions is not null || typeExts is not null)
                    {
                        if (e.IsDirectory) return false;
                        var dot = name.LastIndexOf('.');
                        if (dot < 0) return false;
                        var ext = name[(dot + 1)..].ToString();
                        if (q.Extensions is not null && !q.Extensions.Contains(ext)) return false;
                        if (typeExts is not null && !typeExts.Contains(ext)) return false;
                    }

                    // Size filters apply to files, unless folders were asked for explicitly (folder: size:>1gb).
                    if ((q.MinSize is not null || q.MaxSize is not null) && e.IsDirectory && q.Kind != ItemKindFilter.FoldersOnly) return false;
                    var size = e.IsDirectory ? e.TotalSize : e.Size;
                    if (q.MinSize is { } min && size < min) return false;
                    if (q.MaxSize is { } max && size > max) return false;
                    if (e.ModifiedUtcTicks < fromTicks || e.ModifiedUtcTicks >= toTicks) return false;
                    foreach (var t in q.Terms)
                        if (name.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
                    if ((e.Attributes & hideMask) != 0) return false;
                    if (scopeIndex != VolumeIndex.NoEntry && (i == scopeIndex || !vol.IsUnder(i, scopeIndex))) return false;
                    if (hideMask != 0 && vol.IsInHiddenTree(vol.Parent(i), hideMask)) return false;
                    return true;
                }

                double ScoreOf(int i)
                {
                    ref var e = ref vol.Entries[i];
                    return SearchRanking.Score(vol.Names.AsSpan(e.NameStart, e.NameLength), q.Terms, new DateTime(e.ModifiedUtcTicks, DateTimeKind.Utc), now, 0, e.IsDirectory);
                }

                if (categorySet is not null)
                {
                    var list = new List<Hit>();
                    foreach (var p in categorySet)
                    {
                        if (!IsOnVolume(p, vol.Root)) continue;
                        var i = vol.Find(p);
                        if (i != VolumeIndex.NoEntry && Matches(i)) list.Add(new Hit(vol, i, ScoreOf(i)));
                    }

                    hits.Add(list);
                    continue;
                }

                var count = vol.Capacity;
                var chunks = (count + ChunkSize - 1) / ChunkSize;
                Parallel.For(0, chunks, c =>
                {
                    var list = new List<Hit>();
                    var end = Math.Min(count, (c + 1) * ChunkSize);
                    for (var i = c * ChunkSize; i < end; i++)
                        if (Matches(i)) list.Add(new Hit(vol, i, ScoreOf(i)));
                    if (list.Count > 0) hits.Add(list);
                });
            }
            finally
            {
                vol.Lock.ExitReadLock();
            }
        }

        var all = hits.SelectMany(h => h).ToList();
        var needed = Math.Min(all.Count, request.Offset + request.Limit);
        List<Hit> ordered;
        if (request.Sort == SortField.Relevance)
        {
            // Apply location boosts to a generous head of the list, then take the page.
            all.Sort((a, b) => b.Score.CompareTo(a.Score));
            var head = Math.Min(all.Count, Math.Max(needed * 3, 300));
            if (request.LocationBoosts is { Count: > 0 } boosts)
            {
                for (var k = 0; k < head; k++)
                {
                    var h = all[k];
                    var parent = h.Volume.Parent(h.Index);
                    var folder = WithLock(h.Volume, () => parent == VolumeIndex.NoEntry ? h.Volume.Root : h.Volume.PathOf(parent)).TrimEnd('\\');
                    if (boosts.TryGetValue(folder, out var boost)) all[k] = h with { Score = h.Score + Math.Clamp(boost, 0, 1) * 15 };
                }

                all.Sort(0, head, Comparer<Hit>.Create((a, b) => b.Score.CompareTo(a.Score)));
            }

            ordered = all;
        }
        else
        {
            Comparison<Hit> cmp = request.Sort switch
            {
                SortField.Size => (a, b) => a.Volume.Size(a.Index).CompareTo(b.Volume.Size(b.Index)),
                SortField.Modified => (a, b) => a.Volume.Entries[a.Index].ModifiedUtcTicks.CompareTo(b.Volume.Entries[b.Index].ModifiedUtcTicks),
                SortField.Path => (a, b) => string.Compare(WithLock(a.Volume, () => a.Volume.PathOf(a.Index)), WithLock(b.Volume, () => b.Volume.PathOf(b.Index)), StringComparison.OrdinalIgnoreCase),
                _ => (a, b) => a.Volume.NameOf(a.Index).CompareTo(b.Volume.NameOf(b.Index), StringComparison.OrdinalIgnoreCase),
            };
            all.Sort(request.Descending ? (a, b) => cmp(b, a) : cmp);
            ordered = all;
        }

        var page = ordered.Skip(request.Offset).Take(request.Limit).Select(h => ToItem(h.Volume, h.Index, h.Score)).ToList();
        return new SearchResponse { Items = page, Total = all.Count, ElapsedMs = sw.Elapsed.TotalMilliseconds, Partial = partial };
    }

    public static long? FolderSize(IReadOnlyList<VolumeIndex> volumes, string path)
    {
        foreach (var v in volumes)
        {
            if (!IsOnVolume(path, v.Root)) continue;
            v.Lock.EnterReadLock();
            try
            {
                var i = v.Find(path);
                if (i != VolumeIndex.NoEntry) return v.Size(i);
            }
            finally
            {
                v.Lock.ExitReadLock();
            }
        }

        return null;
    }

    public static IReadOnlyList<CatalogItem> ListFolder(IReadOnlyList<VolumeIndex> volumes, string path)
    {
        foreach (var v in volumes)
        {
            if (!IsOnVolume(path, v.Root)) continue;
            v.Lock.EnterReadLock();
            try
            {
                var i = v.Find(path);
                if (i == VolumeIndex.NoEntry) continue;
                return v.Children(i).Select(c => ToItem(v, c, 0)).OrderByDescending(x => x.Size).ToList();
            }
            finally
            {
                v.Lock.ExitReadLock();
            }
        }

        return [];
    }

    public static IReadOnlyList<CatalogItem> Largest(IReadOnlyList<VolumeIndex> volumes, LargestRequest request)
    {
        var groups = request.TypeGroups is { Length: > 0 } def ? new TypeGroups(def) : TypeGroups.Default;
        IReadOnlySet<string>? typeExts = request.TypeGroup is not null && groups.TryGet(request.TypeGroup, out var te) ? te : null;
        var cutoff = request.OlderThanDays is { } days ? DateTime.UtcNow.AddDays(-days).Ticks : long.MaxValue;
        var hideMask = request.IncludeHidden ? 0u : Hidden | System;
        var wantFolders = request.Kind == LargestKind.Folders;
        var take = request.TopLevelOnly && wantFolders ? request.Count * 4 : request.Count;
        var heap = new PriorityQueue<(VolumeIndex V, int I), long>();

        foreach (var v in volumes)
        {
            if (request.Drive is { Length: > 0 } d && !v.Root.StartsWith(d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (request.UnderPath is not null && !IsOnVolume(request.UnderPath, v.Root)) continue;
            v.Lock.EnterReadLock();
            try
            {
                var under = request.UnderPath is null ? VolumeIndex.NoEntry : v.Find(request.UnderPath);
                if (request.UnderPath is not null && under == VolumeIndex.NoEntry) continue;
                for (var i = 0; i < v.Capacity; i++)
                {
                    ref var e = ref v.Entries[i];
                    if (!e.InUse || i == v.RootIndex || e.IsDirectory != wantFolders) continue;
                    if (e.ModifiedUtcTicks > cutoff) continue;
                    if ((e.Attributes & hideMask) != 0) continue;
                    var size = wantFolders ? e.TotalSize : e.Size;
                    if (heap.Count >= take && heap.TryPeek(out _, out var smallest) && size <= smallest) continue;
                    if (typeExts is not null)
                    {
                        var name = v.Names.AsSpan(e.NameStart, e.NameLength);
                        var dot = name.LastIndexOf('.');
                        if (dot < 0 || !typeExts.Contains(name[(dot + 1)..].ToString())) continue;
                    }

                    if (under != VolumeIndex.NoEntry && (i == under || !v.IsUnder(i, under))) continue;
                    if (hideMask != 0 && v.IsInHiddenTree(e.Parent, hideMask)) continue;
                    heap.Enqueue((v, i), size);
                    if (heap.Count > take) heap.Dequeue();
                }
            }
            finally
            {
                v.Lock.ExitReadLock();
            }
        }

        var list = new List<(VolumeIndex V, int I, long Size)>();
        while (heap.TryDequeue(out var item, out var s)) list.Add((item.V, item.I, s));
        list.Reverse();

        if (request.TopLevelOnly && wantFolders)
        {
            // Drop folders that are mostly explained by a bigger-listed descendant (show the specific heavy folder instead).
            var keep = new List<(VolumeIndex V, int I, long Size)>();
            foreach (var c in list)
            {
                var explained = list.Any(o => o.V == c.V && o.I != c.I && o.Size >= c.Size * 0.8 && c.V.IsUnder(o.I, c.I));
                if (!explained) keep.Add(c);
                if (keep.Count >= request.Count) break;
            }

            list = keep;
        }

        return list.Take(request.Count).Select(x => WithLock(x.V, () => ToItem(x.V, x.I, 0))).ToList();
    }

    private static CatalogItem ToItem(VolumeIndex v, int i, double score) => WithLock(v, () => new CatalogItem
    {
        Path = v.PathOf(i),
        Name = v.Name(i),
        IsFolder = v.IsDirectory(i),
        Size = v.Size(i),
        SizeOnDisk = v.SizeOnDisk(i),
        ModifiedUtc = v.ModifiedUtc(i),
        Attributes = v.Attributes(i),
        FileCount = v.IsDirectory(i) ? v.FileCount(i) : 0,
        Score = score,
    });

    private static T WithLock<T>(VolumeIndex v, Func<T> f)
    {
        v.Lock.EnterReadLock();
        try { return f(); }
        finally { v.Lock.ExitReadLock(); }
    }

    public static bool IsOnVolume(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase);
}
