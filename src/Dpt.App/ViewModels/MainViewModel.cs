using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Core.Navigation;
using Dpt.Core.Settings;
using Dpt.Data;
using Dpt.Shell;

namespace Dpt.App.ViewModels;

/// <summary>The window: tabs, navigation pane (pages, pins, drives), activity and undo.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(IReadOnlyList<Location>? startLocations = null)
    {
        Services.Locations.PinsChanged += (_, _) => LoadPins();
        Services.SettingsChanged += (_, s) => ShowNavigationPane = s.ShowNavigationPane;
        Services.Journal.Changed += (_, _) => UndoCommand.NotifyCanExecuteChanged();
        _showNavigationPane = Services.Settings.ShowNavigationPane;
        LoadPins();
        FirstRunPins();

        var locations = startLocations is { Count: > 0 } ? startLocations : StartLocations(Services.Settings);
        foreach (var l in locations) OpenTab(l, activate: false);
        SelectedTab = Tabs.FirstOrDefault();
    }

    private static AppServices Services => AppServices.Current;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public ObservableCollection<Pin> Pins { get; } = [];
    public ObservableCollection<VolumeSummary> Volumes => Services.Drives.Volumes;
    public ActivityHub Activity => Services.Activity;

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private bool _showNavigationPane;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _isActivityOpen;

    public LocationKind ActiveKind => SelectedTab?.Location.Kind ?? LocationKind.Home;
    public string? ActiveFolder => SelectedTab?.Location.Kind == LocationKind.Folder ? SelectedTab.Location.Path : null;

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        OnPropertyChanged(nameof(ActiveKind));
        OnPropertyChanged(nameof(ActiveFolder));
    }

    public void OnTabNavigated(TabViewModel tab)
    {
        if (tab != SelectedTab) return;
        OnPropertyChanged(nameof(ActiveKind));
        OnPropertyChanged(nameof(ActiveFolder));
    }

    private static IReadOnlyList<Location> StartLocations(AppSettings s)
    {
        switch (s.StartView)
        {
            case StartView.Folder:
                var folder = string.IsNullOrWhiteSpace(s.StartFolder) || !Directory.Exists(s.StartFolder) ? KnownFolders.UserProfile : s.StartFolder;
                return [Location.Folder(folder)];
            case StartView.RestoreTabs:
                var restored = s.LastTabs.Select(Deserialize).Where(l => l is not null).Cast<Location>().ToList();
                return restored.Count > 0 ? restored : [Location.Home];
            default:
                return [Location.Home];
        }
    }

    public static string Serialize(Location l) => $"{l.Kind}|{l.Path}|{l.Query}";

    public static Location? Deserialize(string s)
    {
        var parts = s.Split('|');
        if (parts.Length < 3 || !Enum.TryParse<LocationKind>(parts[0], out var kind)) return null;
        var path = parts[1].Length > 0 ? parts[1] : null;
        if (kind == LocationKind.Folder && (path is null || !Directory.Exists(path))) return null;
        return new Location(kind, path, parts[2].Length > 0 ? parts[2] : null);
    }

    public void SaveTabs()
    {
        var s = Services.Settings.Clone();
        s.LastTabs = Tabs.Select(t => Serialize(t.Location)).ToList();
        Services.SaveSettings(s);
    }

    private void FirstRunPins()
    {
        if (Pins.Count > 0 || Services.Locations.GetVisits().Count > 0) return;
        foreach (var p in KnownFolders.DefaultPins) Services.Locations.AddPin(p);
    }

    private void LoadPins()
    {
        Pins.Clear();
        foreach (var p in Services.Locations.GetPins()) Pins.Add(p);
    }

    public TabViewModel OpenTab(Location location, bool activate = true)
    {
        var tab = new TabViewModel(this, location);
        var index = SelectedTab is null ? Tabs.Count : Tabs.IndexOf(SelectedTab) + 1;
        Tabs.Insert(Math.Min(index, Tabs.Count), tab);
        if (activate || SelectedTab is null) SelectedTab = tab;
        return tab;
    }

    [RelayCommand]
    private void NewTab() => OpenTab(Location.Home);

    [RelayCommand]
    private void CloseTab(TabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();
        if (Tabs.Count == 0)
        {
            OpenTab(Location.Home);
            return;
        }

        if (SelectedTab == tab || SelectedTab is null) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab) + 1) % Tabs.Count];
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab) - 1 + Tabs.Count) % Tabs.Count];
    }

    public void MoveTab(TabViewModel tab, int newIndex)
    {
        var old = Tabs.IndexOf(tab);
        if (old < 0 || newIndex < 0 || newIndex >= Tabs.Count || old == newIndex) return;
        Tabs.Move(old, newIndex);
    }

    [RelayCommand]
    private void GoTo(string kind)
    {
        if (!Enum.TryParse<LocationKind>(kind, out var k)) return;
        Navigate(k == LocationKind.Home ? Location.Home : Location.Page(k));
    }

    [RelayCommand]
    private void OpenFolder(string path) => Navigate(Location.Folder(path));

    [RelayCommand]
    private void OpenFolderInNewTab(string path) => OpenTab(Location.Folder(path), activate: false);

    [RelayCommand]
    private void Unpin(string path) => Services.Locations.RemovePin(path);

    public void Navigate(Location location)
    {
        if (SelectedTab is null) OpenTab(location);
        else SelectedTab.Navigate(location);
    }

    [RelayCommand]
    private void ToggleNavigationPane()
    {
        var s = Services.Settings.Clone();
        s.ShowNavigationPane = !s.ShowNavigationPane;
        Services.SaveSettings(s);
    }

    [RelayCommand]
    private void ToggleActivity() => IsActivityOpen = !IsActivityOpen;

    private bool CanUndo() => Services.FileOps.CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        var message = await Services.FileOps.UndoAsync();
        if (message is not null) ShowToast(message);
        UndoCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource? _toastCts;

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = message;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
            Toast = null;
        }
        catch (TaskCanceledException)
        {
        }
    }
}
