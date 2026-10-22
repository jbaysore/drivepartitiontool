using System.Collections.Concurrent;
using Dpt.Core.Navigation;
using Dpt.Shell.FileSystem;

namespace Dpt.App.Services;

/// <summary>Folder sizes from the search catalog (instant).</summary>
public interface IFolderSizeSource
{
    /// <summary>Sizes including everything nested; null where the catalog doesn't (yet) know the folder.</summary>
    Task<IReadOnlyList<long?>> GetFolderSizesAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>
/// Folder sizes for the size column: asks the catalog first; folders it can't answer are walked in the background
/// (at most two at a time) and cached.
/// </summary>
public sealed class FolderSizeService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (long Size, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _walkers = new(2);

    public IFolderSizeSource? Catalog { get; set; }

    public long? TryGetImmediate(string path) =>
        _cache.TryGetValue(path, out var c) && DateTime.UtcNow - c.At < CacheLifetime ? c.Size : null;

    /// <summary>Catalog lookup for many folders at once. Results are cached briefly.</summary>
    public async Task<IReadOnlyList<long?>> FromCatalogAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (Catalog is null || paths.Count == 0) return paths.Select(_ => (long?)null).ToList();
        var sizes = await Catalog.GetFolderSizesAsync(paths, ct).ConfigureAwait(false);
        for (var i = 0; i < paths.Count && i < sizes.Count; i++)
            if (sizes[i] is { } s) _cache[paths[i]] = (s, DateTime.UtcNow);
        return sizes;
    }

    /// <summary>Walks a folder to total its size (used when the catalog doesn't cover it).</summary>
    public async Task<long?> WalkAsync(string path, CancellationToken ct)
    {
        if (TryGetImmediate(path) is { } known) return known;
        await _walkers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetImmediate(path) is { } again) return again;
            var size = await Task.Run(() => FolderSizeCalculator.Compute(path, ct), ct).ConfigureAwait(false);
            _cache[path] = (size, DateTime.UtcNow);
            return size;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _walkers.Release();
        }
    }

    /// <summary>Forget cached sizes for a path and every folder above it (after changes inside it).</summary>
    public void Invalidate(string path)
    {
        for (string? p = path; p is not null; p = PathUtil.Parent(p)) _cache.TryRemove(p, out _);
    }
}
