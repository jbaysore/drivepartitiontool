using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Infrastructure;
using Dpt.Core.Formatting;
using Dpt.Core.Locations;
using Dpt.Core.Navigation;
using Dpt.Ipc;
using Dpt.Shell;
using Dpt.Shell.FileSystem;

namespace Dpt.App.ViewModels;

public sealed record FilterOption(string Label, string Token);

/// <summary>Search results across selected drives (or within one folder), with filter chips, paging and a preview pane.</summary>
public sealed partial class SearchPageViewModel : PageViewModel
{
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _offset;

    public SearchPageViewModel(TabViewModel tab, Location location) : base(tab, location)
    {
        _query = location.Query ?? "";
        _scopePath = location.Path;
        _showPreview = Services.Settings.ShowSearchPreview;
        _typeFilter = TypeOptions[0];
        _sizeFilter = SizeOptions[0];
        _dateFilter = DateOptions[0];
        _kindFilter = KindOptions[0];
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = RunAsync(reset: true);
        }, Application.Current.Dispatcher);
        _debounce.Stop();
        Services.Catalog.StatusChanged += OnCatalogStatus;
        Services.Catalog.Changed += OnCatalogChanged;
        if (!string.IsNullOrWhiteSpace(_query)) _ = RunAsync(reset: true);
        else UpdateCatalogNote();
    }

    public override string IconKey => "Icon.Search";
    public override string Title => string.IsNullOrWhiteSpace(Query) ? "Search" : $"Search: {Query}";
    public override string? CurrentFolder => ScopePath;

    public BulkObservableCollection<FileItemViewModel> Results { get; } = new();

    [ObservableProperty] private string _query;
    [ObservableProperty] private string? _scopePath;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private int _total;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _catalogNote;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private SortField _sort = SortField.Relevance;
    [ObservableProperty] private bool _descending = true;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private FileItemViewModel? _selected;
    [ObservableProperty] private string? _previewText;
    [ObservableProperty] private System.Windows.Media.ImageSource? _previewImage;

    public IReadOnlyList<FilterOption> TypeOptions { get; } =
    [
        new("Any type", ""), new("Pictures", "type:image"), new("Videos", "type:video"), new("Music", "type:audio"),
        new("Documents", "type:doc"), new("Archives", "type:archive"), new("Apps", "type:app"),
    ];

    public IReadOnlyList<FilterOption> SizeOptions { get; } =
    [
        new("Any size", ""), new("Over 10 MB", "size:>10mb"), new("Over 100 MB", "size:>100mb"), new("Over 1 GB", "size:>1gb"), new("Under 1 MB", "size:<1mb"),
    ];

    public IReadOnlyList<FilterOption> DateOptions { get; } =
    [
        new("Any time", ""), new("Today", "modified:today"), new("Past week", "modified:lastweek"), new("Past month", "modified:lastmonth"),
        new("Past year", "modified:lastyear"), new("Older than a year", "modified:<" + DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd")),
    ];

    public IReadOnlyList<FilterOption> KindOptions { get; } = [new("Files and folders", ""), new("Files only", "file:"), new("Folders only", "folder:")];

    [ObservableProperty] private FilterOption? _typeFilter;
    [ObservableProperty] private FilterOption? _sizeFilter;
    [ObservableProperty] private FilterOption? _dateFilter;
    [ObservableProperty] private FilterOption? _kindFilter;

    public bool HasScope => ScopePath is not null;
    public string ScopeLabel => ScopePath is null ? "All selected drives" : $"In {PathUtil.DisplayName(ScopePath)} and its subfolders";
    public bool IsEmpty => !IsSearching && Total == 0 && !string.IsNullOrWhiteSpace(EffectiveQuery);
    public bool ShowTips => string.IsNullOrWhiteSpace(EffectiveQuery);

    private string EffectiveQuery =>
        string.Join(' ', new[] { Query, TypeFilter?.Token, SizeFilter?.Token, DateFilter?.Token, KindFilter?.Token }.Where(s => !string.IsNullOrWhiteSpace(s)));

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(Title));
        Schedule();
    }

    partial void OnTypeFilterChanged(FilterOption? value) => Schedule();
    partial void OnSizeFilterChanged(FilterOption? value) => Schedule();
    partial void OnDateFilterChanged(FilterOption? value) => Schedule();
    partial void OnKindFilterChanged(FilterOption? value) => Schedule();

    partial void OnScopePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(ScopeLabel));
        Schedule();
    }

    partial void OnSelectedChanged(FileItemViewModel? value) => _ = LoadPreviewAsync(value);

    partial void OnShowPreviewChanged(bool value) => _ = LoadPreviewAsync(Selected);

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public override void Refresh() => _ = RunAsync(reset: true);

    /// <summary>Live results: when a changed file's name matches the query words (or a listed result changed), search again.</summary>
    private void OnCatalogChanged(object? sender, IReadOnlyList<CatalogChange> changes)
    {
        var terms = Dpt.Core.Search.SearchQuery.Parse(EffectiveQuery).Terms;
        var listed = Results.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevant = changes.Any(c =>
            listed.Contains(c.Path) || (c.OldPath is not null && listed.Contains(c.OldPath)) ||
            (terms.Count > 0 && terms.All(t => Path.GetFileName(c.Path).Contains(t, StringComparison.OrdinalIgnoreCase))));
        if (relevant) Application.Current?.Dispatcher.BeginInvoke(Schedule);
    }

    private bool _lastWasPartial;

    private void OnCatalogStatus(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        UpdateCatalogNote();
        // Results gathered while the catalog was still building: refresh once it's ready.
        if (_lastWasPartial && Services.Catalog.Status?.IsReady == true && !IsSearching)
        {
            _lastWasPartial = false;
            _ = RunAsync(reset: true);
        }
    });

    private void UpdateCatalogNote()
    {
        var status = Services.Catalog.Status;
        if (Services.Catalog.Catalog is null)
        {
            CatalogNote = "The search catalog is turned off. Turn on \"Build the catalog inside the app\" or install the search service in Settings.";
            return;
        }

        var scanning = status?.Volumes.Where(v => v.Selected && v.State is VolumeState.Scanning or VolumeState.Waiting).ToList() ?? [];
        CatalogNote = scanning.Count == 0
            ? null
            : "Still cataloguing " + string.Join(", ", scanning.Select(v => $"{v.Root.TrimEnd('\\')} ({v.Progress:P0})")) + " — results may be incomplete.";
    }

    private Dictionary<string, double> LocationBoosts()
    {
        var now = DateTimeOffset.Now;
        var visits = Services.Locations.GetVisits();
        if (visits.Count == 0) return [];
        var scores = visits.ToDictionary(v => v.Path.TrimEnd('\\'), v => FrequentLocations.Score(v, now), StringComparer.OrdinalIgnoreCase);
        var max = scores.Values.Max();
        return max <= 0 ? [] : scores.ToDictionary(k => k.Key, k => k.Value / max, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RunAsync(bool reset)
    {
        var catalog = Services.Catalog.Catalog;
        UpdateCatalogNote();
        var q = EffectiveQuery;
        if (string.IsNullOrWhiteSpace(q) || catalog is null)
        {
            Results.ReplaceAll([]);
            Total = 0;
            Summary = "";
            HasMore = false;
            NotifyState();
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (reset) _offset = 0;
        IsSearching = true;
        NotifyState();
        try
        {
            var settings = Services.Settings;
            var request = new SearchRequest
            {
                Query = q,
                ScopePath = ScopePath,
                Offset = _offset,
                Limit = Math.Max(20, settings.SearchPageSize),
                IncludeHidden = settings.SearchIncludeHidden,
                IncludeSystem = settings.SearchIncludeHidden && settings.ShowSystemItems,
                LocationBoosts = LocationBoosts(),
                TypeGroups = settings.TypeGroups,
                CategoryPaths = await ResolveCategoriesAsync(q),
                Sort = Sort,
                Descending = Descending,
            };
            var response = await catalog.SearchAsync(request, cts.Token);
            if (cts.IsCancellationRequested) return;
            var showExt = settings.ShowExtensions;
            var items = response.Items.Select(i => ToRow(i, showExt)).ToList();
            if (reset) Results.ReplaceAll(items);
            else Results.AddRange(items);
            _offset += items.Count;
            Total = response.Total;
            _lastWasPartial = response.Partial;
            HasMore = _offset < response.Total;
            Summary = $"{response.Total:N0} result{(response.Total == 1 ? "" : "s")} · {response.ElapsedMs:N0} ms" + (response.Partial ? " · catalog still building" : "");
            StatusText = Summary;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            Summary = "Search failed: " + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsSearching = false;
            NotifyState();
        }
    }

    /// <summary>Hook for categories (M5): returns the tagged paths for cat: filters, or null when the query has none.</summary>
    public static Func<string, Task<IReadOnlyList<string>?>>? CategoryResolver { get; set; }

    private static Task<IReadOnlyList<string>?> ResolveCategoriesAsync(string q) =>
        CategoryResolver?.Invoke(q) ?? Task.FromResult<IReadOnlyList<string>?>(null);

    public static FileItemViewModel ToRow(CatalogItem i, bool showExt)
    {
        var entry = new FileEntry(i.Path, i.Name, i.IsFolder, i.IsFolder ? -1 : i.Size, i.ModifiedUtc.ToLocalTime(), i.ModifiedUtc.ToLocalTime(), (FileAttributes)i.Attributes);
        var row = new FileItemViewModel(entry, showExt);
        if (i.IsFolder) row.Size = i.Size;
        return row;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowTips));
    }

    [RelayCommand]
    private Task LoadMore() => RunAsync(reset: false);

    [RelayCommand]
    private void SortBy(SortField field)
    {
        if (Sort == field) Descending = !Descending;
        else
        {
            Sort = field;
            Descending = field is SortField.Relevance or SortField.Size or SortField.Modified;
        }

        _ = RunAsync(reset: true);
    }

    [RelayCommand]
    private void ClearScope() => ScopePath = null;

    [RelayCommand]
    private void ClearFilters()
    {
        TypeFilter = TypeOptions[0];
        SizeFilter = SizeOptions[0];
        DateFilter = DateOptions[0];
        KindFilter = KindOptions[0];
    }

    [RelayCommand]
    private void TogglePreview() => ShowPreview = !ShowPreview;

    [RelayCommand]
    private void Open(FileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        if (item.IsFolder)
        {
            Tab.Navigate(Location.Folder(item.Path));
            return;
        }

        try
        {
            ShellLauncher.Open(item.Path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Services.FileOps.Dialogs.ShowError("Can't open", ex.Message);
        }
    }

    /// <summary>Opens the folder that contains the item (in this tab, or a new one with Ctrl).</summary>
    [RelayCommand]
    private void OpenLocation(FileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        var parent = Path.GetDirectoryName(item.Path);
        if (parent is null) return;
        Tab.Main.OpenTab(Location.Folder(parent), activate: true);
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (Selected is { } s) FileClipboard.SetText(s.Path);
    }

    [RelayCommand]
    private void Copy()
    {
        if (Selected is { } s) FileClipboard.SetFiles([s.Path], ClipboardFileAction.Copy);
    }

    public async Task RecycleAsync(IReadOnlyList<FileItemViewModel> items)
    {
        var result = await Services.FileOps.RecycleAsync(items.Select(i => i.Path).ToList(), knownSize: p => items.FirstOrDefault(i => i.Path == p)?.Size);
        if (result is null) return;
        var gone = result.Completed.Select(c => c.Source).Where(s => s is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Results.Where(r => gone.Contains(r.Path)).ToList()) Results.Remove(r);
        Total -= gone.Count;
    }

    private async Task LoadPreviewAsync(FileItemViewModel? item)
    {
        PreviewText = null;
        PreviewImage = null;
        if (!ShowPreview || item is null || item.IsFolder) return;
        var path = item.Path;
        PreviewImage = await StaWorker.Shared.Run(() => ShellInfo.Thumbnail(path, 256));
        if (!Services.Settings.ShowSearchPreview && !ShowPreview) return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string[] textExts = [".txt", ".md", ".log", ".csv", ".json", ".xml", ".ini", ".cs", ".ps1", ".py", ".js", ".ts", ".html", ".css", ".yml", ".yaml"];
        if (!textExts.Contains(ext)) return;
        try
        {
            var text = await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[4096];
                var n = fs.Read(buffer, 0, buffer.Length);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, n);
            });
            if (Selected == item) PreviewText = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string FormatSize(long bytes) => SizeFormatter.Format(bytes);

    public override void Dispose()
    {
        _cts?.Cancel();
        _debounce.Stop();
        Services.Catalog.StatusChanged -= OnCatalogStatus;
        Services.Catalog.Changed -= OnCatalogChanged;
        base.Dispose();
    }
}
