using System.Collections.Concurrent;
using Dpt.Core.Search;
using Dpt.Data;
using Dpt.Shell.FileSystem;

namespace Dpt.App.Services;

/// <summary>Assigning categories to items and finding items by category (following renames/moves via file IDs).</summary>
public sealed class CategoryService(CategoriesRepository repo, JournalRepository journal)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CategoriesRepository Repository => repo;

    public event EventHandler? Changed;

    private static (uint Serial, ulong Ref, long Created) Id(string path) =>
        FileIdentity.TryGet(path) is { } id ? (id.VolumeSerial, id.FileIndex, id.CreatedTicks) : (0u, 0UL, 0L);

    /// <summary>Category names on an item (cached briefly for list views).</summary>
    public IReadOnlyList<string> CategoriesOf(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var (serial, fileRef, created) = Id(path);
        var cats = repo.GetCategories().ToDictionary(c => c.Id);
        var names = repo.TagsFor(path, serial, fileRef, created).Select(t => cats.TryGetValue(t.CategoryId, out var c) ? c.Name : null)
            .Where(n => n is not null).Cast<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        _cache[path] = names;
        return names;
    }

    /// <summary>Tags items; returns (path, categoryId) pairs actually added (for the journal).</summary>
    public List<(string Path, long CategoryId)> Tag(IEnumerable<string> paths, IEnumerable<string> categoryNames, bool journalIt = true, string title = "Categorize")
    {
        var added = new List<(string, long)>();
        var categories = categoryNames.Select(n => repo.GetOrCreate(n)).ToList();
        foreach (var p in paths)
        {
            var (serial, fileRef, created) = Id(p);
            foreach (var c in categories)
                if (repo.Tag(p, serial, fileRef, c.Id, created)) added.Add((p, c.Id));
            _cache.TryRemove(p, out _);
        }

        if (journalIt && added.Count > 0)
            journal.Record(title, "categories", added.Select(a => (JournalKind.Tag, (string?)a.Item1, (string?)a.Item2.ToString(), false)));
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public void Untag(IEnumerable<string> paths, string categoryName)
    {
        if (repo.Find(categoryName) is not { } c) return;
        foreach (var p in paths)
        {
            var (serial, fileRef, _) = Id(p);
            repo.Untag(p, serial, fileRef, c.Id);
            _cache.TryRemove(p, out _);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UntagById(string path, long categoryId)
    {
        var (serial, fileRef, _) = Id(path);
        repo.Untag(path, serial, fileRef, categoryId);
        _cache.TryRemove(path, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Current paths of everything in a category (stale paths are re-resolved by file ID and updated).</summary>
    public IReadOnlyList<string> PathsOf(string categoryName)
    {
        if (repo.Find(categoryName) is not { } c) return [];
        var list = new List<string>();
        foreach (var t in repo.TagsOf(c.Id))
        {
            if (File.Exists(t.Path) || Directory.Exists(t.Path))
            {
                list.Add(t.Path);
                continue;
            }

            if (t.FileRef != 0 && FileIdentity.TryResolve(new FileId(t.VolumeSerial, t.FileRef)) is { } moved &&
                (t.Created == 0 || FileIdentity.TryGet(moved)?.CreatedTicks == t.Created))
            {
                repo.UpdatePath(t.Id, moved);
                list.Add(moved);
            }
        }

        return list;
    }

    /// <summary>Search hook: for "cat:" filters, the paths carrying ALL requested categories.</summary>
    public Task<IReadOnlyList<string>?> ResolveForSearchAsync(string query) => Task.Run(() =>
    {
        var cats = SearchQuery.Parse(query).Categories;
        if (cats.Count == 0) return (IReadOnlyList<string>?)null;
        IEnumerable<string>? set = null;
        foreach (var c in cats)
        {
            var paths = PathsOf(c);
            set = set is null ? paths : set.Intersect(paths, StringComparer.OrdinalIgnoreCase);
        }

        return (IReadOnlyList<string>?)(set?.ToList() ?? []);
    });

    /// <summary>Called after the app moves/renames items so path-only tags (non-NTFS) follow.</summary>
    public void OnMoved(string from, string to)
    {
        repo.MovePathTags(from, to);
        _cache.Clear();
    }

    public void InvalidateCache() => _cache.Clear();
}
