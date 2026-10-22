using System.Collections.Concurrent;
using System.Text.Json;
using Dpt.Index.Native;
using Dpt.Index.Ntfs;
using Dpt.Index.Scan;
using Dpt.Ipc;

namespace Dpt.Index;

public sealed record EngineOptions
{
    /// <summary>Folder for snapshots and the selection config.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Use direct MFT reading + change journal for NTFS volumes (requires admin/SYSTEM).</summary>
    public bool UseMft { get; init; }

    /// <summary>When set, index exactly these folders (scan mode) instead of discovering drives. Used by tests.</summary>
    public IReadOnlyList<string>? FixedRoots { get; init; }

    public bool IsService { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The search catalog engine: discovers volumes, keeps one <see cref="VolumeIndex"/> per selected volume current
/// (MFT + USN journal, or directory scan + watcher), saves snapshots, and answers <see cref="ICatalog"/> queries.
/// </summary>
public sealed class IndexEngine : ICatalog, IDisposable
{
    private readonly EngineOptions _options;
    private readonly ConcurrentDictionary<string, VolumeWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _configGate = new();
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private Task? _discovery;

    public IndexEngine(EngineOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.DataDirectory);
        LoadConfig();
    }

    public event EventHandler<IReadOnlyList<CatalogChange>>? Changed;

    /// <summary>Raised when any volume's state or progress changes.</summary>
    public event EventHandler? StatusChanged;

    public void Start()
    {
        // Discover once up front so callers immediately see the volumes (as "waiting"/"scanning").
        try { Discover(); }
        catch (Exception ex) { Diagnostics.Trace("discovery", ex); }
        _discovery = Task.Run(() => DiscoveryLoop(_cts.Token));
    }

    private async Task DiscoveryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Discover();
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("discovery", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Starts workers for new selected volumes and marks removed ones unavailable.</summary>
    public void Discover()
    {
        var present = new Dictionary<string, (string Label, uint Serial, string Fs)>(StringComparer.OrdinalIgnoreCase);
        if (_options.FixedRoots is { } fixedRoots)
        {
            foreach (var r in fixedRoots)
            {
                var root = r.EndsWith('\\') ? r : r + "\\";
                if (!Directory.Exists(root)) continue;
                // A fixed root that is a whole volume ("X:\") is catalogued like a drive (MFT when allowed).
                present[root] = root.Length == 3 ? Win32.VolumeInfo(root) : (Path.GetFileName(root.TrimEnd('\\')), (uint)StableHash(root), "FOLDER");
            }
        }
        else
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network or DriveType.NoRootDirectory or DriveType.Ram) continue;
                    var (label, serial, fs) = Win32.VolumeInfo(d.Name);
                    present[d.Name] = (label, serial, fs);
                }
                catch (IOException)
                {
                }
            }
        }

        foreach (var (root, info) in present)
        {
            var selected = !IsExcluded(root);
            if (_workers.TryGetValue(root, out var existing))
            {
                if (existing.Serial != info.Serial)
                {
                    // A different drive now uses this letter.
                    existing.Dispose();
                    _workers.TryRemove(root, out _);
                }
                else
                {
                    existing.SetSelected(selected);
                    continue;
                }
            }

            var useMft = _options.UseMft && root.Length == 3 && string.Equals(info.Fs, "NTFS", StringComparison.OrdinalIgnoreCase);
            var worker = new VolumeWorker(this, root, info.Serial, info.Fs, info.Label, useMft);
            _workers[root] = worker;
            worker.SetSelected(selected);
        }

        foreach (var (root, w) in _workers)
            if (!present.ContainsKey(root)) w.MarkUnavailable();
        RaiseStatus();
    }

    private bool IsExcluded(string root)
    {
        lock (_configGate) return _excluded.Contains(root);
    }

    internal void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

    internal void RaiseChanged(IReadOnlyList<CatalogChange> changes)
    {
        if (changes.Count > 0) Changed?.Invoke(this, changes);
    }

    internal string SnapshotPath(uint serial, string root) =>
        Path.Combine(_options.DataDirectory, $"{serial:X8}-{(uint)StableHash(root):X8}.idx");

    internal EngineOptions Options => _options;

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s.ToUpperInvariant()) h = (h ^ c) * 16777619;
            return h;
        }
    }

    /// <summary>Indexes ready for querying.</summary>
    public IReadOnlyList<VolumeIndex> ReadyVolumes =>
        _workers.Values.Where(w => w.Selected && w.Index is not null && w.State is VolumeState.Ready or VolumeState.Scanning && w.Index?.RootIndex >= 0)
            .Select(w => w.Index!).ToList();

    private bool AnyScanning => _workers.IsEmpty || _workers.Values.Any(w => w.Selected && w.State is VolumeState.Scanning or VolumeState.Waiting);

    /// <summary>Waits until every selected volume has finished its initial load (tests and service startup).</summary>
    public async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (_workers.Count > 0 && _workers.Values.Where(w => w.Selected).All(w => w.State is VolumeState.Ready or VolumeState.Unavailable or VolumeState.Error)) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Catalog did not become ready.");
    }

    // ---------------- ICatalog ----------------

    public Task<CatalogStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(GetStatus());

    public CatalogStatus GetStatus() => new()
    {
        IsService = _options.IsService,
        Version = typeof(IndexEngine).Assembly.GetName().Version?.ToString() ?? "",
        Volumes = _workers.Values.OrderBy(w => w.Root).Select(w => w.Status()).ToList(),
    };

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct = default) =>
        Task.Run(() => SearchExecutor.Search(ReadyVolumes, request, AnyScanning), ct);

    public Task<IReadOnlyList<long?>> GetFolderSizesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var vols = _workers.Values.Where(w => w.Selected && w.State == VolumeState.Ready && w.Index is not null).Select(w => w.Index!).ToList();
        IReadOnlyList<long?> result = paths.Select(p => SearchExecutor.FolderSize(vols, p)).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CatalogItem>> GetLargestAsync(LargestRequest request, CancellationToken ct = default) =>
        Task.Run(() => SearchExecutor.Largest(ReadyVolumes, request), ct);

    public Task<IReadOnlyList<CatalogItem>> ListFolderAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => SearchExecutor.ListFolder(ReadyVolumes, path), ct);

    public Task SetSelectedVolumesAsync(IReadOnlyList<string> roots, CancellationToken ct = default)
    {
        var selected = new HashSet<string>(roots.Select(r => r.EndsWith('\\') ? r : r + "\\"), StringComparer.OrdinalIgnoreCase);
        lock (_configGate)
        {
            _excluded = _workers.Keys.Where(k => !selected.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            SaveConfig();
        }

        Discover();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DiskHealthInfo>> GetDiskHealthAsync(IReadOnlyList<int> diskNumbers, CancellationToken ct = default) => Task.Run(() =>
    {
        var health = Storage.StorageInventory.TryHealthAll(diskNumbers);
        IReadOnlyList<DiskHealthInfo> list = diskNumbers.Zip(health, (n, h) => new DiskHealthInfo(n, h.Available, h.TemperatureC, h.WearPercent, h.PowerOnHours, h.Note)).ToList();
        return list;
    }, ct);

    /// <summary>Discards a volume's catalog and rebuilds it from scratch.</summary>
    public void Rebuild(string root)
    {
        if (_workers.TryGetValue(root, out var w)) w.Rebuild();
    }

    // ---------------- Config ----------------

    private sealed record Config(List<string> Excluded);

    private string ConfigPath => Path.Combine(_options.DataDirectory, "catalog.json");

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) is { } c)
                _excluded = new HashSet<string>(c.Excluded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
    }

    private void SaveConfig() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config([.. _excluded])));

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _workers.Values) w.Dispose();
        _workers.Clear();
    }
}

public static class Diagnostics
{
    public static Action<string, Exception>? Sink { get; set; }

    public static void Trace(string area, Exception ex) => Sink?.Invoke(area, ex);
}

/// <summary>Keeps one volume's index current in the background.</summary>
internal sealed class VolumeWorker : IDisposable
{
    private readonly IndexEngine _engine;
    private readonly bool _useMft;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private volatile bool _selected;
    private volatile bool _rebuild;
    private double _progress;
    private string? _message;
    private DateTime _lastSnapshot = DateTime.UtcNow;
    private bool _dirty;

    public VolumeWorker(IndexEngine engine, string root, uint serial, string fs, string label, bool useMft)
    {
        _engine = engine;
        Root = root;
        Serial = serial;
        FileSystem = fs;
        Label = label;
        _useMft = useMft;
    }

    public string Root { get; }
    public uint Serial { get; }
    public string FileSystem { get; }
    public string Label { get; }
    public bool Selected => _selected;
    public VolumeState State { get; private set; } = VolumeState.Waiting;
    public VolumeIndex? Index { get; private set; }

    public VolumeStatus Status() => new()
    {
        Root = Root,
        Label = Label,
        FileSystem = FileSystem,
        Serial = Serial,
        Selected = _selected,
        State = _selected ? State : VolumeState.NotSelected,
        Progress = State == VolumeState.Ready ? 1 : _progress,
        Items = Index?.LiveCount ?? 0,
        Mode = _useMft ? "MFT" : "Scan",
        LastUpdatedUtc = Index?.LastUpdatedUtc,
        Message = _message,
    };

    public void SetSelected(bool selected)
    {
        _selected = selected;
        if (selected && _task is null) _task = Task.Run(() => Run(_cts.Token));
        if (!selected && State == VolumeState.Unavailable) return;
    }

    public void MarkUnavailable()
    {
        if (State == VolumeState.Unavailable) return;
        State = VolumeState.Unavailable;
        _cts.Cancel();
    }

    public void Rebuild() => _rebuild = true;

    private void SetState(VolumeState s, double progress = 0, string? message = null)
    {
        State = s;
        _progress = progress;
        _message = message;
        _engine.RaiseStatus();
    }

    private async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_useMft) await RunMft(ct);
                else await RunScan(ct);
                return;
            }
            catch (RebuildRequestedException)
            {
                SaveSnapshotQuietly();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("volume " + Root, ex);
                SetState(VolumeState.Error, 0, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // ---------------- MFT + journal ----------------

    private async Task RunMft(CancellationToken ct)
    {
        using var ntfs = new NtfsVolume(Root);
        var journal = ntfs.QueryJournal(createIfMissing: true);
        var index = new VolumeIndex(Root, Serial, FileSystem, Label);

        var loaded = TryLoadSnapshot(index) && index.JournalId == journal.UsnJournalID && index.NextUsn >= journal.FirstUsn && !_rebuild;
        if (!loaded)
        {
            SetState(VolumeState.Scanning, 0);
            journal = ntfs.QueryJournal(createIfMissing: true);
            index.Lock.EnterWriteLock();
            try
            {
                ntfs.LoadInto(index, new Progress(this), ct);
                index.JournalId = journal.UsnJournalID;
                index.NextUsn = journal.NextUsn;
                index.LastUpdatedUtc = DateTime.UtcNow;
            }
            finally
            {
                index.Lock.ExitWriteLock();
            }

            _dirty = true;
        }

        _rebuild = false;
        Index = index;
        SetState(VolumeState.Ready, 1);
        var pending = new List<UsnChange>();
        while (!ct.IsCancellationRequested)
        {
            if (_selected)
            {
                pending.Clear();
                try
                {
                    var next = ntfs.ReadChanges(index.JournalId, index.NextUsn, pending);
                    if (pending.Count > 0) ApplyUsn(ntfs, index, pending);
                    index.NextUsn = next;
                }
                catch (JournalResetException)
                {
                    _rebuild = true;
                }

                if (_rebuild) throw new RebuildRequestedException();
                MaybeSnapshot(index);
            }

            await Task.Delay(_engine.Options.PollInterval, ct);
        }

        SaveSnapshot(index);
    }

    private void ApplyUsn(NtfsVolume ntfs, VolumeIndex index, List<UsnChange> changes)
    {
        // Keep only the last record per file; collect paths for change events.
        var last = new Dictionary<ulong, UsnChange>();
        var renamedFrom = new Dictionary<ulong, string>();
        var events = new List<CatalogChange>();
        index.Lock.EnterReadLock();
        try
        {
            foreach (var c in changes)
            {
                if ((c.Reason & NtfsVolume.UsnReasonRenameOldName) != 0 && !renamedFrom.ContainsKey(c.FileRef))
                {
                    var i = index.IndexOf(c.FileRef);
                    if (i != VolumeIndex.NoEntry) renamedFrom[c.FileRef] = index.PathOf(i);
                }

                if ((c.Reason & NtfsVolume.UsnReasonRenameOldName) != 0 && (c.Reason & NtfsVolume.UsnReasonRenameNewName) == 0) continue;
                last[c.FileRef] = c;
            }
        }
        finally
        {
            index.Lock.ExitReadLock();
        }

        var stats = last.Values.Where(c => (c.Reason & NtfsVolume.UsnReasonFileDelete) == 0)
            .ToDictionary(c => c.FileRef, c => ntfs.Stat(c.FullFileRef));

        index.Lock.EnterWriteLock();
        try
        {
            foreach (var c in last.Values)
            {
                var existing = index.IndexOf(c.FileRef);
                var existed = existing != VolumeIndex.NoEntry;
                var oldPath = existed ? index.PathOf(existing) : null;
                if ((c.Reason & NtfsVolume.UsnReasonFileDelete) != 0 || stats[c.FileRef] is null)
                {
                    if (!existed) continue;
                    var wasDir = index.IsDirectory(existing);
                    index.Remove(c.FileRef);
                    events.Add(new CatalogChange(ChangeKind.Deleted, oldPath!, null, wasDir));
                    continue;
                }

                var s = stats[c.FileRef]!.Value;
                var i = index.Upsert(c.FileRef, c.ParentRef, c.Name, s.IsDirectory, s.Attributes, s.IsDirectory ? 0 : s.Size,
                    s.IsDirectory ? 0 : s.Allocated, s.ModifiedUtcTicks);
                if (i == VolumeIndex.NoEntry) continue;
                var path = index.PathOf(i);
                var kind = !existed ? ChangeKind.Created
                    : renamedFrom.TryGetValue(c.FileRef, out var from) && !string.Equals(from, path, StringComparison.OrdinalIgnoreCase) ? ChangeKind.Renamed
                    : ChangeKind.Modified;
                events.Add(new CatalogChange(kind, path, kind == ChangeKind.Renamed ? renamedFrom[c.FileRef] : null, s.IsDirectory));
            }

            index.LastUpdatedUtc = DateTime.UtcNow;
        }
        finally
        {
            index.Lock.ExitWriteLock();
        }

        _dirty = true;
        _engine.RaiseChanged(events);
    }

    // ---------------- Scan + watcher ----------------

    private async Task RunScan(CancellationToken ct)
    {
        var scanner = new DirectoryScanner(ClusterSize(Root));
        var index = new VolumeIndex(Root, Serial, FileSystem, Label);
        var fromSnapshot = !_rebuild && TryLoadSnapshot(index);
        if (fromSnapshot)
        {
            // Serve the snapshot immediately, refresh in the background.
            Index = index;
            SetState(VolumeState.Scanning, 0.5, "Refreshing");
        }
        else
        {
            SetState(VolumeState.Scanning, 0);
        }

        var queue = new ConcurrentQueue<(WatcherChangeTypes Type, string Path, string? Old)>();
        using var watcher = CreateWatcher(queue);
        var fresh = new VolumeIndex(Root, Serial, FileSystem, Label);
        await Task.Run(() => scanner.ScanInto(fresh, fromSnapshot ? null : new Progress(this), ct), ct);
        fresh.LastUpdatedUtc = DateTime.UtcNow;
        Index = fresh;
        index = fresh;
        _rebuild = false;
        _dirty = true;
        SetState(VolumeState.Ready, 1);

        while (!ct.IsCancellationRequested)
        {
            if (!queue.IsEmpty) ApplyWatcher(scanner, index, queue);
            if (_rebuild) throw new RebuildRequestedException();
            MaybeSnapshot(index);
            await Task.Delay(Math.Min(500, (int)_engine.Options.PollInterval.TotalMilliseconds), ct);
        }

        SaveSnapshot(index);
    }

    private FileSystemWatcher? CreateWatcher(ConcurrentQueue<(WatcherChangeTypes, string, string?)> queue)
    {
        try
        {
            var w = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 256 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            };
            w.Created += (_, e) => queue.Enqueue((WatcherChangeTypes.Created, e.FullPath, null));
            w.Deleted += (_, e) => queue.Enqueue((WatcherChangeTypes.Deleted, e.FullPath, null));
            w.Changed += (_, e) => queue.Enqueue((WatcherChangeTypes.Changed, e.FullPath, null));
            w.Renamed += (_, e) => queue.Enqueue((WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath));
            w.Error += (_, _) => _rebuild = true;
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ApplyWatcher(DirectoryScanner scanner, VolumeIndex index, ConcurrentQueue<(WatcherChangeTypes Type, string Path, string? Old)> queue)
    {
        var events = new List<CatalogChange>();
        index.Lock.EnterWriteLock();
        try
        {
            while (queue.TryDequeue(out var c))
            {
                switch (c.Type)
                {
                    case WatcherChangeTypes.Deleted:
                    {
                        var i = index.Find(c.Path);
                        if (i == VolumeIndex.NoEntry) break;
                        var dir = index.IsDirectory(i);
                        index.Remove(index.RefOf(i));
                        events.Add(new CatalogChange(ChangeKind.Deleted, c.Path, null, dir));
                        break;
                    }
                    case WatcherChangeTypes.Renamed:
                    {
                        var old = c.Old is null ? VolumeIndex.NoEntry : index.Find(c.Old);
                        var parent = index.Find(Path.GetDirectoryName(c.Path) ?? "");
                        if (old != VolumeIndex.NoEntry && parent != VolumeIndex.NoEntry)
                        {
                            var name = Path.GetFileName(c.Path);
                            var dir = index.IsDirectory(old);
                            index.Upsert(index.RefOf(old), index.RefOf(parent), name, dir, index.Attributes(old),
                                index.IsDirectory(old) ? 0 : index.Size(old), index.IsDirectory(old) ? 0 : index.SizeOnDisk(old), index.Entries[old].ModifiedUtcTicks);
                            events.Add(new CatalogChange(ChangeKind.Renamed, c.Path, c.Old, dir));
                        }
                        else
                        {
                            scanner.AddTree(index, c.Path);
                            events.Add(new CatalogChange(ChangeKind.Created, c.Path, null, Directory.Exists(c.Path)));
                        }

                        break;
                    }
                    default:
                    {
                        var existed = index.Find(c.Path) != VolumeIndex.NoEntry;
                        if (!File.Exists(c.Path) && !Directory.Exists(c.Path)) break;
                        if (c.Type == WatcherChangeTypes.Changed && existed && Directory.Exists(c.Path)) break; // folder timestamp only
                        scanner.AddTree(index, c.Path);
                        events.Add(new CatalogChange(existed ? ChangeKind.Modified : ChangeKind.Created, c.Path, null, Directory.Exists(c.Path)));
                        break;
                    }
                }
            }

            index.LastUpdatedUtc = DateTime.UtcNow;
        }
        finally
        {
            index.Lock.ExitWriteLock();
        }

        _dirty = true;
        _engine.RaiseChanged(events);
    }

    private static long ClusterSize(string root)
    {
        try
        {
            if (GetDiskFreeSpace(Path.GetPathRoot(root) ?? root, out var spc, out var bps, out _, out _)) return (long)spc * bps;
        }
        catch (Exception)
        {
        }

        return 4096;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint free, out uint total);

    // ---------------- Snapshots ----------------

    private bool TryLoadSnapshot(VolumeIndex index)
    {
        var path = _engine.SnapshotPath(Serial, Root);
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            index.Lock.EnterWriteLock();
            try { return index.Load(fs); }
            finally { index.Lock.ExitWriteLock(); }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidOperationException)
        {
            return false;
        }
    }

    private void MaybeSnapshot(VolumeIndex index)
    {
        if (!_dirty || DateTime.UtcNow - _lastSnapshot < _engine.Options.SnapshotInterval) return;
        SaveSnapshot(index);
    }

    public void SaveSnapshot(VolumeIndex index)
    {
        if (index.RootIndex < 0) return;
        var path = _engine.SnapshotPath(Serial, Root);
        var tmp = path + ".tmp";
        try
        {
            index.Lock.EnterReadLock();
            try
            {
                using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                index.Save(fs);
            }
            finally
            {
                index.Lock.ExitReadLock();
            }

            File.Move(tmp, path, overwrite: true);
            _lastSnapshot = DateTime.UtcNow;
            _dirty = false;
        }
        catch (IOException ex)
        {
            Diagnostics.Trace("snapshot", ex);
        }
    }

    private void SaveSnapshotQuietly()
    {
        // A rebuild discards the snapshot so the next pass reads from scratch.
        try { File.Delete(_engine.SnapshotPath(Serial, Root)); }
        catch (IOException) { }
    }

    private sealed class RebuildRequestedException : Exception;

    private sealed class Progress(VolumeWorker w) : IProgress<double>
    {
        private DateTime _last;

        public void Report(double value)
        {
            w._progress = value;
            if (DateTime.UtcNow - _last < TimeSpan.FromMilliseconds(250) && value < 1) return;
            _last = DateTime.UtcNow;
            w._engine.RaiseStatus();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }

        if (Index is { } idx && _dirty) SaveSnapshot(idx);
    }
}
