using Dpt.App.Diagnostics;
using Dpt.Data;
using Dpt.Index;
using Dpt.Ipc;

namespace Dpt.App.Services;

/// <summary>
/// The app's view of the search catalog. Uses the background service when it is installed and running; otherwise
/// (if enabled in Settings) runs the same engine inside the app in scan mode. Surfaces build progress in the activity pill.
/// </summary>
public sealed class CatalogService : IFolderSizeSource, IDisposable
{
    private readonly AppServices _services;
    private readonly Timer _poll;
    private IndexEngine? _local;
    private CatalogPipeClient? _client;
    private ActivityItem? _activity;
    private int _polling;

    public CatalogService(AppServices services)
    {
        _services = services;
        _poll = new Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The active catalog, or null when none is available.</summary>
    public ICatalog? Catalog { get; private set; }

    public bool IsService => _client is not null;
    public CatalogStatus? Status { get; private set; }
    public event EventHandler? StatusChanged;
    public event EventHandler<IReadOnlyList<CatalogChange>>? Changed;

    /// <summary>Test hook / power-user option: index only these folders (semicolon-separated) instead of all drives.</summary>
    private static IReadOnlyList<string>? FixedRoots =>
        Environment.GetEnvironmentVariable("DPT_CATALOG_ROOTS") is { Length: > 0 } s ? s.Split(';', StringSplitOptions.RemoveEmptyEntries) : null;

    private static bool Disabled => Environment.GetEnvironmentVariable("DPT_CATALOG") == "off";

    public async Task InitializeAsync()
    {
        if (Disabled) return;
        if (FixedRoots is null && await CatalogPipeClient.IsAvailableAsync())
        {
            UseService();
        }
        else if (_services.Settings.UseLocalCatalog || FixedRoots is not null)
        {
            StartLocal();
        }

        _poll.Change(0, 1000);
    }

    private void UseService()
    {
        StopLocal();
        _client = new CatalogPipeClient();
        _client.Changed += OnChanged;
        Catalog = _client;
        Log.Info("Catalog", "Using the background catalog service.");
    }

    private void StartLocal()
    {
        if (_local is not null) return;
        Dpt.Index.Diagnostics.Sink = (area, ex) => Log.Error("Catalog " + area, ex);
        _local = new IndexEngine(new EngineOptions
        {
            DataDirectory = Path.Combine(AppDatabase.DefaultDataDirectory, "catalog"),
            UseMft = false,
            FixedRoots = FixedRoots,
        });
        _local.Changed += OnChanged;
        _local.Start();
        Catalog = _local;
        Log.Info("Catalog", "Using the in-app catalog (scan mode).");
    }

    private void StopLocal()
    {
        if (_local is null) return;
        _local.Changed -= OnChanged;
        _local.Dispose();
        _local = null;
    }

    /// <summary>After installing the service: switch over if it now answers.</summary>
    public async Task<bool> TrySwitchToServiceAsync()
    {
        if (_client is not null) return true;
        for (var i = 0; i < 20; i++)
        {
            if (await CatalogPipeClient.IsAvailableAsync())
            {
                UseService();
                await PollAsync();
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    /// <summary>After uninstalling the service: fall back to the in-app catalog.</summary>
    public void FallBackToLocal()
    {
        if (_client is not null)
        {
            _client.Changed -= OnChanged;
            _client.Dispose();
            _client = null;
        }

        Catalog = null;
        if (_services.Settings.UseLocalCatalog) StartLocal();
    }

    private void OnChanged(object? sender, IReadOnlyList<CatalogChange> changes)
    {
        Changed?.Invoke(this, changes);
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            if (Catalog is null) return;
            CatalogStatus status;
            try
            {
                status = await Catalog.GetStatusAsync();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                // Service stopped: fall back.
                if (_client is not null) FallBackToLocal();
                return;
            }

            Status = status;
            UpdateActivity(status);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void UpdateActivity(CatalogStatus status)
    {
        var scanning = status.Volumes.Where(v => v.Selected && v.State == VolumeState.Scanning).ToList();
        if (scanning.Count == 0)
        {
            if (_activity is { IsRunning: true })
            {
                var items = status.Volumes.Where(v => v.Selected).Sum(v => v.Items);
                _activity.Complete($"Catalog ready · {items:N0} items");
            }

            _activity = null;
            return;
        }

        var v = scanning[0];
        var title = $"Scanning {v.Root.TrimEnd('\\')}";
        if (_activity is null || !_activity.IsRunning) _activity = _services.Activity.Start(title);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_activity is null) return;
            _activity.Title = title + (scanning.Count > 1 ? $" (+{scanning.Count - 1})" : "");
            _activity.Progress = v.Progress;
            _activity.Detail = v.Message ?? $"{v.Items:N0} items so far";
        });
    }

    public bool IsVolumeReady(string path)
    {
        var s = Status;
        if (s is null) return false;
        return s.Volumes.Any(v => v.Selected && v.State == VolumeState.Ready && path.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));
    }

    async Task<IReadOnlyList<long?>> IFolderSizeSource.GetFolderSizesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (Catalog is null) return paths.Select(_ => (long?)null).ToList();
        try
        {
            return await Catalog.GetFolderSizesAsync(paths, ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return paths.Select(_ => (long?)null).ToList();
        }
    }

    public void Rebuild(string root) => _local?.Rebuild(root);

    public void Dispose()
    {
        _poll.Dispose();
        _client?.Dispose();
        StopLocal();
    }
}
