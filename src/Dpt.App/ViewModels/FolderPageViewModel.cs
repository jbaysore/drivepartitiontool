using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Infrastructure;
using Dpt.Core.Formatting;
using Dpt.Core.Navigation;
using Dpt.Core.Settings;
using Dpt.Shell;
using Dpt.Shell.FileSystem;

namespace Dpt.App.ViewModels;

public enum SortColumn
{
    Name,
    Modified,
    Type,
    Size,
    Created,
}

/// <summary>A folder shown as a detailed list (default) or thumbnails.</summary>
public sealed partial class FolderPageViewModel : PageViewModel
{
    private readonly DispatcherTimer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _sizeCts;
    private List<FileItemViewModel> _all = [];

    public FolderPageViewModel(TabViewModel tab, Location location) : base(tab, location)
    {
        FolderPath = location.Path!;
        var s = Services.Settings;
        _viewMode = s.DefaultFolderView;
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            _ = LoadAsync(preserve: true);
        }, Application.Current.Dispatcher);
        _reloadTimer.Stop();
        Services.FileOps.FilesChanged += OnFilesChanged;
        Services.SettingsChanged += OnSettingsChanged;
        _ = LoadAsync(preserve: false);
    }

    public string FolderPath { get; }
    public override string? CurrentFolder => FolderPath;
    public override string IconKey => PathUtil.IsDriveRoot(FolderPath) ? "Icon.HardDrive" : "Icon.Folder";
    public string Heading => PathUtil.DisplayName(FolderPath);

    public BulkObservableCollection<FileItemViewModel> Items { get; } = new();

    /// <summary>Set by the view whenever the list selection changes.</summary>
    public IReadOnlyList<FileItemViewModel> SelectedItems { get; private set; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private FolderViewMode _viewMode;
    [ObservableProperty] private SortColumn _sortColumn = SortColumn.Name;
    [ObservableProperty] private bool _sortAscending = true;

    public bool IsDetails => ViewMode == FolderViewMode.Details;
    public bool IsThumbnails => ViewMode == FolderViewMode.Thumbnails;
    public bool HasSelection => SelectedItems.Count > 0;
    public bool IsEmpty => !IsLoading && ErrorMessage is null && Items.Count == 0;

    /// <summary>Raised to ask the view to select (and optionally start renaming) an item.</summary>
    public event EventHandler<(FileItemViewModel Item, bool Rename)>? SelectRequested;

    partial void OnViewModeChanged(FolderViewMode value)
    {
        OnPropertyChanged(nameof(IsDetails));
        OnPropertyChanged(nameof(IsThumbnails));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    public void SetSelection(IReadOnlyList<FileItemViewModel> selected)
    {
        SelectedItems = selected;
        OnPropertyChanged(nameof(HasSelection));
        UpdateStatus();
        RecycleCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
    }

    public override void Activate()
    {
        Services.Locations.RecordVisit(FolderPath, DateTimeOffset.Now);
    }

    public override void Refresh() => _ = LoadAsync(preserve: true);

    private async Task LoadAsync(bool preserve)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        if (!preserve) IsLoading = true;
        var s = Services.Settings;
        try
        {
            var entries = await Task.Run(() => DirectoryLister.List(FolderPath, new ListOptions(s.ShowHiddenItems, s.ShowSystemItems), cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            ErrorMessage = null;
            ApplyEntries(entries, preserve, s.ShowExtensions);
            EnsureWatcher();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex is UnauthorizedAccessException
                ? "You don't have permission to open this folder."
                : Directory.Exists(FolderPath) ? ex.Message : "This folder no longer exists.";
            Items.ReplaceAll([]);
            _all = [];
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            UpdateStatus();
        }
    }

    private void ApplyEntries(List<FileEntry> entries, bool preserve, bool showExtensions)
    {
        var existing = preserve ? _all.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase) : [];
        var list = new List<FileItemViewModel>(entries.Count);
        foreach (var e in entries)
        {
            if (existing.TryGetValue(e.Path, out var vm))
            {
                vm.Update(e);
                list.Add(vm);
            }
            else
            {
                list.Add(new FileItemViewModel(e, showExtensions));
            }
        }

        _all = list;
        var sorted = Sorted(list).ToList();
        if (preserve) Merge(sorted);
        else Items.ReplaceAll(sorted);
        RequestFolderSizes();
    }

    /// <summary>
    /// Applies a refreshed listing with minimal collection changes so row containers (selection, focus, an open rename box) survive.
    /// </summary>
    private void Merge(List<FileItemViewModel> sorted)
    {
        var keep = new HashSet<FileItemViewModel>(sorted);
        var removals = Items.Where(i => !keep.Contains(i)).ToList();
        var current = new HashSet<FileItemViewModel>(Items);
        var additions = sorted.Count(i => !current.Contains(i));
        if (removals.Count + additions > 500)
        {
            Items.ReplaceAll(sorted);
            return;
        }

        foreach (var r in removals) Items.Remove(r);
        for (var i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            if (i < Items.Count && ReferenceEquals(Items[i], item)) continue;
            var existing = Items.IndexOf(item);
            if (existing >= 0) Items.Move(existing, i);
            else Items.Insert(i, item);
        }
    }

    private IEnumerable<FileItemViewModel> Sorted(IEnumerable<FileItemViewModel> items)
    {
        var foldersFirst = Services.Settings.FoldersFirst;
        var comparer = Comparer<FileItemViewModel>.Create((a, b) =>
        {
            if (foldersFirst && a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            var c = SortColumn switch
            {
                SortColumn.Modified => a.Modified.CompareTo(b.Modified),
                SortColumn.Created => a.Created.CompareTo(b.Created),
                SortColumn.Type => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
                SortColumn.Size => (a.Size ?? -1).CompareTo(b.Size ?? -1),
                _ => 0,
            };
            if (c == 0) c = FileItemViewModel.CompareNames(a.Name, b.Name);
            return SortAscending ? c : -c;
        });
        var sorted = items.ToList();
        sorted.Sort(comparer);
        return sorted;
    }

    [RelayCommand]
    private void Sort(SortColumn column)
    {
        if (SortColumn == column) SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = column is SortColumn.Name or SortColumn.Type;
        }

        Items.ReplaceAll(Sorted(_all));
    }

    private void RequestFolderSizes()
    {
        _sizeCts?.Cancel();
        if (!Services.Settings.ShowFolderSizes) return;
        var cts = _sizeCts = new CancellationTokenSource();
        var folders = _all.Where(i => i.IsFolder).ToList();
        foreach (var f in folders)
        {
            if (Services.FolderSizes.TryGetImmediate(f.Path) is { } known) f.Size = known;
            else f.SizePending = true;
        }

        _ = Task.Run(async () =>
        {
            // 1) Ask the catalog for everything at once (instant when the drive is catalogued).
            var pending = folders.Where(f => f.SizePending).ToList();
            try
            {
                var known = await Services.FolderSizes.FromCatalogAsync(pending.Select(f => f.Path).ToList(), cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    for (var k = 0; k < pending.Count && k < known.Count; k++)
                    {
                        if (known[k] is not { } s) continue;
                        pending[k].SizePending = false;
                        pending[k].Size = s;
                    }

                    if (SortColumn == SortColumn.Size) _reloadSortPending = true;
                    UpdateStatus();
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
            }

            // 2) Walk the rest in the background.
            foreach (var f in folders.Where(f => f.SizePending))
            {
                if (cts.IsCancellationRequested) return;
                var size = await Services.FolderSizes.WalkAsync(f.Path, cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    f.SizePending = false;
                    f.Size = size;
                    if (SortColumn == SortColumn.Size) _reloadSortPending = true;
                    UpdateStatus();
                });
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_reloadSortPending && !cts.IsCancellationRequested) Items.ReplaceAll(Sorted(_all));
                _reloadSortPending = false;
            });
        });
    }

    private bool _reloadSortPending;

    private void EnsureWatcher()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = new FileSystemWatcher(FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler changed = (_, _) => ScheduleReload();
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.Error += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;
        }
    }

    private void ScheduleReload() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    private void OnFilesChanged(object? sender, IReadOnlyCollection<string> folders)
    {
        if (folders.Any(f => string.Equals(PathUtil.Normalize(f), FolderPath, StringComparison.OrdinalIgnoreCase) || PathUtil.IsSameOrUnder(f, FolderPath)))
            ScheduleReload();
    }

    private void OnSettingsChanged(object? sender, AppSettings e) => _ = LoadAsync(preserve: false);

    private void UpdateStatus()
    {
        var count = Items.Count;
        var sel = SelectedItems.Count;
        var text = $"{count} item{(count == 1 ? "" : "s")}";
        if (sel > 0)
        {
            var bytes = SelectedItems.Sum(i => i.Size ?? 0);
            var known = SelectedItems.All(i => i.Size is not null);
            text += $" · {sel} selected" + (bytes > 0 ? $" · {SizeFormatter.Format(bytes)}{(known ? "" : "+")}" : "");
        }

        StatusText = text;
    }

    // ---------- Commands ----------

    [RelayCommand]
    private void Open(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null) return;
        if (item.IsFolder)
        {
            Tab.Navigate(Location.Folder(item.Path));
            return;
        }

        if (SelectedItems.Count > 1 && SelectedItems.Contains(item))
        {
            foreach (var f in SelectedItems.Where(i => !i.IsFolder).Take(15)) TryOpen(f.Path);
            return;
        }

        TryOpen(item.Path);
    }

    private void TryOpen(string path)
    {
        try
        {
            ShellLauncher.Open(path, FolderPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (ex is Win32Exception { NativeErrorCode: 1155 }) ShellLauncher.OpenWith(path);
            else if (ex is not Win32Exception { NativeErrorCode: 1223 }) Services.FileOps.Dialogs.ShowError("Can't open", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenInNewTab(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item?.IsFolder == true) Tab.Main.OpenTab(Location.Folder(item.Path), activate: false);
    }

    [RelayCommand]
    private async Task NewFolder()
    {
        var created = await Services.FileOps.NewFolderAsync(FolderPath);
        if (created is not null) await SelectWhenListedAsync(created, rename: true);
    }

    [RelayCommand]
    private async Task NewFile()
    {
        var created = await Services.FileOps.NewFileAsync(FolderPath, "New Text Document.txt");
        if (created is not null) await SelectWhenListedAsync(created, rename: true);
    }

    private async Task SelectWhenListedAsync(string path, bool rename)
    {
        await LoadAsync(preserve: true);
        var item = _all.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        SelectRequested?.Invoke(this, (item, rename));
    }

    /// <summary>Category dialog (set by the app at startup).</summary>
    public static Func<IReadOnlyList<string>, bool>? Categorize { get; set; }

    [RelayCommand]
    private void EditCategories()
    {
        var targets = SelectedItems.Count > 0 ? SelectedItems.Select(i => i.Path).ToList() : [FolderPath];
        if (Categorize?.Invoke(targets) == true)
            foreach (var i in SelectedItems) i.RefreshCategories();
    }

    /// <summary>Bulk-rename dialog (set by the app at startup).</summary>
    public static Func<IReadOnlyList<Dpt.Core.Operations.RenameInput>, Task<int>>? BulkRename { get; set; }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Rename()
    {
        if (SelectedItems.Count > 1 && BulkRename is not null)
        {
            await BulkRename(SelectedItems.Select(i => new Dpt.Core.Operations.RenameInput(i.Path, i.IsFolder, i.Modified)).ToList());
            return;
        }

        var item = SelectedItems.FirstOrDefault();
        if (item is null) return;
        BeginRename(item);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Duplicate() => await Services.FileOps.DuplicateAsync(SelectedItems.Select(i => i.Path).ToList());

    public void BeginRename(FileItemViewModel item)
    {
        foreach (var other in _all.Where(i => i.IsEditing)) other.IsEditing = false;
        item.EditName = item.Name;
        item.IsEditing = true;
    }

    public async Task CommitRenameAsync(FileItemViewModel item)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        var newName = item.EditName.Trim();
        if (newName.Length == 0 || newName == item.Name) return;
        var result = await Services.FileOps.RenameAsync(item.Path, newName);
        if (result is not null) await SelectWhenListedAsync(result, rename: false);
    }

    public void CancelRename(FileItemViewModel item) => item.IsEditing = false;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy() => SetClipboard(ClipboardFileAction.Copy);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut() => SetClipboard(ClipboardFileAction.Cut);

    private void SetClipboard(ClipboardFileAction action)
    {
        if (SelectedItems.Count == 0) return;
        FileClipboard.SetFiles(SelectedItems.Select(i => i.Path), action);
        foreach (var i in _all) i.IsCut = action == ClipboardFileAction.Cut && SelectedItems.Contains(i);
        StatusText = action == ClipboardFileAction.Cut ? $"Cut {SelectedItems.Count} item(s)" : $"Copied {SelectedItems.Count} item(s)";
    }

    [RelayCommand]
    private async Task Paste()
    {
        var (paths, action) = FileClipboard.GetFiles();
        if (paths.Count == 0) return;
        if (action == ClipboardFileAction.Cut)
        {
            var result = await Services.FileOps.MoveAsync(paths, FolderPath);
            if (result is { Succeeded: true }) FileClipboard.Clear();
        }
        else
        {
            await Services.FileOps.CopyAsync(paths, FolderPath);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Recycle()
    {
        var paths = SelectedItems.Select(i => i.Path).ToList();
        var sizes = SelectedItems.ToDictionary(i => i.Path, i => i.Size, StringComparer.OrdinalIgnoreCase);
        await Services.FileOps.RecycleAsync(paths, knownSize: p => sizes.GetValueOrDefault(p));
    }

    [RelayCommand]
    private async Task PermanentDelete()
    {
        var paths = SelectedItems.Select(i => i.Path).ToList();
        await Services.FileOps.PermanentDeleteAsync(paths);
    }

    [RelayCommand]
    private void CopyPath()
    {
        var text = SelectedItems.Count > 0 ? string.Join(Environment.NewLine, SelectedItems.Select(i => $"\"{i.Path}\"")) : FolderPath;
        FileClipboard.SetText(text);
        StatusText = "Path copied";
    }

    [RelayCommand]
    private void Properties()
    {
        var target = SelectedItems.FirstOrDefault()?.Path ?? FolderPath;
        ShellLauncher.ShowProperties(Services.OwnerWindow, target);
    }

    [RelayCommand]
    private void Pin()
    {
        var targets = SelectedItems.Where(i => i.IsFolder).Select(i => i.Path).ToList();
        if (targets.Count == 0) targets.Add(FolderPath);
        foreach (var t in targets) Services.Locations.AddPin(t);
        StatusText = targets.Count == 1 ? $"Pinned {PathUtil.DisplayName(targets[0])}" : $"Pinned {targets.Count} folders";
    }

    [RelayCommand]
    private void ToggleViewMode() => ViewMode = ViewMode == FolderViewMode.Details ? FolderViewMode.Thumbnails : FolderViewMode.Details;

    [RelayCommand]
    private void Organize() => Tab.Navigate(new Location(LocationKind.Layouts, FolderPath));

    [RelayCommand]
    private void LargestHere() => Tab.Navigate(new Location(LocationKind.LargestItems, FolderPath));

    [RelayCommand]
    private void OpenInExplorer() => ShellLauncher.RevealInExplorer(SelectedItems.FirstOrDefault()?.Path ?? FolderPath);

    [RelayCommand]
    private void OpenTerminal() => ShellLauncher.OpenTerminal(FolderPath);

    /// <summary>Handles a verb returned by the native context menu that the app performs itself.</summary>
    public async Task HandleShellVerbAsync(string verb)
    {
        switch (verb)
        {
            case "delete": await Recycle(); break;
            case "rename": await Rename(); break;
            case "cut": Cut(); break;
            case "copy": Copy(); break;
            case "paste": await Paste(); break;
            case "properties": Properties(); break;
            case "pintohome": Pin(); break;
        }
    }

    /// <summary>Drop from Explorer or another tab: Shift = move, Ctrl = copy, otherwise move on the same drive and copy across drives.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths, string? targetFolder, bool forceMove, bool forceCopy)
    {
        var dest = targetFolder ?? FolderPath;
        if (paths.Count == 0) return;
        var sameDrive = paths.All(p => string.Equals(PathUtil.DriveOf(p), PathUtil.DriveOf(dest), StringComparison.OrdinalIgnoreCase));
        var move = forceMove || (!forceCopy && sameDrive);
        if (move && paths.All(p => string.Equals(Path.GetDirectoryName(p), dest, StringComparison.OrdinalIgnoreCase))) return;
        if (move) await Services.FileOps.MoveAsync(paths, dest);
        else await Services.FileOps.CopyAsync(paths, dest);
    }

    public override void Dispose()
    {
        _loadCts?.Cancel();
        _sizeCts?.Cancel();
        _reloadTimer.Stop();
        _watcher?.Dispose();
        Services.FileOps.FilesChanged -= OnFilesChanged;
        Services.SettingsChanged -= OnSettingsChanged;
        base.Dispose();
    }
}
