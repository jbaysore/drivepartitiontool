using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Infrastructure;
using Dpt.Core.Formatting;
using Dpt.Core.Navigation;
using Dpt.Ipc;
using Dpt.Shell;

namespace Dpt.App.ViewModels;

/// <summary>A row in the largest-items list: name, where it is, size, and a bar relative to the biggest item.</summary>
public sealed partial class SizeRowViewModel(CatalogItem item, double fraction) : ObservableObject
{
    public CatalogItem Item { get; } = item;
    public string Path => Item.Path;
    public string Name => Item.Name;
    public bool IsFolder => Item.IsFolder;
    public string Location => System.IO.Path.GetDirectoryName(Item.Path) ?? "";
    public string SizeText => SizeFormatter.Format(Item.Size);
    public string OnDiskText => SizeFormatter.Format(Item.SizeOnDisk);
    public string ModifiedText => Item.ModifiedUtc.ToLocalTime().ToString("d");
    public string FilesText => Item.IsFolder ? $"{Item.FileCount:N0} files" : "";
    public double Fraction { get; } = fraction;
    public string IconKey => Item.IsFolder ? "Icon.Folder" : "Icon.File";
}

public sealed record OptionItem(string Label, string? Value);

/// <summary>Largest files and folders across selected drives, with filters and a folder drill-down.</summary>
public sealed partial class LargestPageViewModel : PageViewModel
{
    private readonly Stack<string> _drill = new();
    private CancellationTokenSource? _cts;

    public LargestPageViewModel(TabViewModel tab, Location location) : base(tab, location)
    {
        UnderPath = location.Path;
        DriveOptions.Add(new OptionItem("All selected drives", null));
        foreach (var v in Services.Catalog.Status?.Volumes.Where(v => v.Selected) ?? [])
            DriveOptions.Add(new OptionItem($"{v.Root.TrimEnd('\\')} {v.Label}".Trim(), v.Root));
        _drive = DriveOptions[0];
        _type = TypeOptions[0];
        _age = AgeOptions[0];
        Services.Catalog.StatusChanged += OnCatalogStatus;
        Services.FileOps.FilesChanged += OnFilesChanged;
        _ = LoadAsync();
    }

    public override string IconKey => "Icon.ChartPie";
    public override string Title => UnderPath is null ? "Largest items" : $"Largest in {PathUtil.DisplayName(UnderPath)}";

    public BulkObservableCollection<SizeRowViewModel> Items { get; } = new();
    public ObservableCollection<OptionItem> DriveOptions { get; } = [];

    public IReadOnlyList<OptionItem> TypeOptions { get; } =
    [
        new("Any type", null), new("Videos", "video"), new("Pictures", "image"), new("Music", "audio"), new("Documents", "doc"),
        new("Archives", "archive"), new("Apps & installers", "app"),
    ];

    public IReadOnlyList<OptionItem> AgeOptions { get; } =
    [
        new("Any age", null), new("Not changed in 1 year", "365"), new("Not changed in 2 years", "730"), new("Not changed in 5 years", "1825"),
    ];

    [ObservableProperty] private bool _showFolders;
    [ObservableProperty] private OptionItem? _drive;
    [ObservableProperty] private OptionItem? _type;
    [ObservableProperty] private OptionItem? _age;
    [ObservableProperty] private bool _hideParentFolders = true;
    [ObservableProperty] private bool _includeHidden = true;
    [ObservableProperty] private string? _underPath;
    [ObservableProperty] private string? _drillPath;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private SizeRowViewModel? _selected;

    public bool IsDrilling => DrillPath is not null;
    public bool ShowFiles => !ShowFolders;
    public string DrillTitle => DrillPath is null ? "" : $"Inside {DrillPath}";

    partial void OnShowFoldersChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFiles));
        _ = LoadAsync();
    }

    partial void OnDriveChanged(OptionItem? value) => _ = LoadAsync();
    partial void OnTypeChanged(OptionItem? value) => _ = LoadAsync();
    partial void OnAgeChanged(OptionItem? value) => _ = LoadAsync();
    partial void OnHideParentFoldersChanged(bool value) => _ = LoadAsync();
    partial void OnIncludeHiddenChanged(bool value) => _ = LoadAsync();

    partial void OnDrillPathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsDrilling));
        OnPropertyChanged(nameof(DrillTitle));
    }

    private bool _loadedWhenReady;

    private void OnCatalogStatus(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (DriveOptions.Count == 1)
            foreach (var v in Services.Catalog.Status?.Volumes.Where(v => v.Selected) ?? [])
                DriveOptions.Add(new OptionItem($"{v.Root.TrimEnd('\\')} {v.Label}".Trim(), v.Root));
        if (!_loadedWhenReady && Services.Catalog.Status?.IsReady == true)
        {
            _loadedWhenReady = true;
            _ = LoadAsync();
        }
    });

    private void OnFilesChanged(object? sender, IReadOnlyCollection<string> folders) => Application.Current?.Dispatcher.BeginInvoke(() => _ = LoadAsync());

    public override void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var catalog = Services.Catalog.Catalog;
        if (catalog is null)
        {
            Note = "Turn on the search catalog in Settings to find your largest items.";
            Items.ReplaceAll([]);
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            IReadOnlyList<CatalogItem> items;
            if (DrillPath is { } drill)
            {
                items = await catalog.ListFolderAsync(drill, cts.Token);
            }
            else
            {
                items = await catalog.GetLargestAsync(new LargestRequest
                {
                    Kind = ShowFolders ? LargestKind.Folders : LargestKind.Files,
                    Count = 200,
                    Drive = Drive?.Value,
                    UnderPath = UnderPath,
                    TypeGroup = ShowFolders ? null : Type?.Value,
                    OlderThanDays = Age?.Value is { } days ? int.Parse(days) : null,
                    IncludeHidden = IncludeHidden,
                    TopLevelOnly = ShowFolders && HideParentFolders,
                    TypeGroups = Services.Settings.TypeGroups,
                }, cts.Token);
            }

            if (cts.IsCancellationRequested) return;
            var max = items.Count == 0 ? 1 : Math.Max(1, items.Max(i => i.Size));
            Items.ReplaceAll(items.Select(i => new SizeRowViewModel(i, (double)i.Size / max)));
            var status = Services.Catalog.Status;
            Note = status is { IsReady: false } ? "The catalog is still being built, so this list may be incomplete." : items.Count == 0 ? "Nothing matches these filters." : null;
            var total = items.Sum(i => i.Size);
            StatusText = DrillPath is null
                ? $"{items.Count} largest {(ShowFolders ? "folders" : "files")} · {SizeFormatter.Format(total)} together"
                : $"{items.Count} items · {SizeFormatter.Format(total)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            Note = "Couldn't read the catalog: " + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }
    }

    [RelayCommand]
    private void Drill(SizeRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (!row.IsFolder)
        {
            OpenLocation(row);
            return;
        }

        if (DrillPath is not null) _drill.Push(DrillPath);
        DrillPath = row.Path;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void DrillUp()
    {
        DrillPath = _drill.Count > 0 ? _drill.Pop() : null;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void OpenLocation(SizeRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;
        Tab.Main.OpenTab(Location.Folder(row.IsFolder ? row.Path : Path.GetDirectoryName(row.Path)!), activate: true);
    }

    [RelayCommand]
    private void Open(SizeRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (row.IsFolder) Tab.Navigate(Location.Folder(row.Path));
        else
        {
            try { ShellLauncher.Open(row.Path); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Services.FileOps.Dialogs.ShowError("Can't open", ex.Message); }
        }
    }

    public async Task RecycleAsync(IReadOnlyList<SizeRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        await Services.FileOps.RecycleAsync(rows.Select(r => r.Path).ToList(), knownSize: p => rows.FirstOrDefault(r => r.Path == p)?.Item.Size);
    }

    public async Task MoveAsync(IReadOnlyList<SizeRowViewModel> rows, string destination)
    {
        if (rows.Count == 0) return;
        await Services.FileOps.MoveAsync(rows.Select(r => r.Path).ToList(), destination);
    }

    [RelayCommand]
    private void ClearScope()
    {
        UnderPath = null;
        OnPropertyChanged(nameof(Title));
        _ = LoadAsync();
    }

    public override void Dispose()
    {
        _cts?.Cancel();
        Services.Catalog.StatusChanged -= OnCatalogStatus;
        Services.FileOps.FilesChanged -= OnFilesChanged;
        base.Dispose();
    }
}
