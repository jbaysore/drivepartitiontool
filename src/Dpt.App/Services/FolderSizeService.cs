using System.Collections.Concurrent;
using Dpt.Core.Navigation;
using Dpt.Shell.FileSystem;

namespace Dpt.App.Services;

/// <summary>Instant folder sizes from the search catalog (index service).</summary>
public interface IFolderSizeSource
{
    /// <summary>True when the catalog covers this folder; size includes everything nested inside it.</summary>
    bool TryGetFolderSize(string path, out long size);
}

/// <summary>
/// Folder sizes for the size column. Uses the catalog when available, otherwise walks the folder in the background
/// (at most two at a time) and caches the result.
/// </summary>
public sealed class FolderSizeService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (long Size, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _walkers = new(2);

    public IFolderSizeSource? Catalog { get; set; }

    public long? TryGetImmediate(string path)
    {
        if (Catalog is not null && Catalog.TryGetFolderSize(path, out var s)) return s;
        return _cache.TryGetValue(path, out var c) && DateTime.UtcNow - c.At < CacheLifetime ? c.Size : null;
    }

    public async Task<long?> GetAsync(string path, CancellationToken ct)
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
