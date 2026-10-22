using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Core.Locations;
using Dpt.Core.Navigation;

namespace Dpt.App.ViewModels;

public sealed record LocationEntry(string Path, string Label, string Meta);

public sealed record LargestEntry(string Path, string Label, string SizeText, bool IsFolder);

/// <summary>Home: all-drives search, pinned and frequent locations, modest drive space and largest items.</summary>
public sealed partial class HomePageViewModel : PageViewModel
{
    public HomePageViewModel(TabViewModel tab) : base(tab, Location.Home)
    {
        Services.Locations.PinsChanged += OnPinsChanged;
        Services.Drives.Changed += OnDrivesChanged;
        Load();
    }

    public override string IconKey => "Icon.Home";

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isManagingPins;

    public ObservableCollection<LocationEntry> Pins { get; } = [];
    public ObservableCollection<LocationEntry> Frequent { get; } = [];
    public ObservableCollection<VolumeSummary> Drives { get; } = [];
    public ObservableCollection<LargestEntry> Largest { get; } = [];

    public bool ShowDrives => Services.Settings.HomeShowDrives;
    public bool ShowLargest => Services.Settings.HomeShowLargest;
    public bool HasPins => Pins.Count > 0;
    public bool HasFrequent => Frequent.Count > 0;
    public bool HasLargest => Largest.Count > 0;

    [ObservableProperty] private string _largestNote = "Largest items appear here once the search catalog has scanned your drives.";

    public override void Activate() => Load();

    public override void Refresh() => Load();

    private void Load()
    {
        Pins.Clear();
        var pins = Services.Locations.GetPins();
        foreach (var p in pins) Pins.Add(new LocationEntry(p.Path, p.Label ?? PathUtil.DisplayName(p.Path), PathUtil.DriveOf(p.Path)));

        Frequent.Clear();
        var now = DateTimeOffset.Now;
        var visits = Services.Locations.GetVisits().Where(v => Directory.Exists(v.Path));
        foreach (var v in FrequentLocations.Rank(visits, now, Services.Settings.FrequentCount, pins.Select(p => p.Path)))
            Frequent.Add(new LocationEntry(v.Path, PathUtil.DisplayName(v.Path), FrequentLocations.RecencyLabel(v.LastVisited, now)));

        LoadDrives();
        LoadLargest();
        OnPropertyChanged(nameof(HasPins));
        OnPropertyChanged(nameof(HasFrequent));
    }

    private void LoadDrives()
    {
        Drives.Clear();
        foreach (var d in Services.Drives.Volumes.Where(v => v.Type != DriveType.Network).Take(4)) Drives.Add(d);
    }

    /// <summary>Filled from the catalog (M3). Kept as a hook so Home stays modest: three entries at most.</summary>
    public Func<int, IReadOnlyList<LargestEntry>>? LargestProvider { get; set; } = LargestProviderDefault;

    public static Func<int, IReadOnlyList<LargestEntry>>? LargestProviderDefault { get; set; }

    private void LoadLargest()
    {
        Largest.Clear();
        var items = LargestProvider?.Invoke(3) ?? [];
        foreach (var i in items) Largest.Add(i);
        OnPropertyChanged(nameof(HasLargest));
    }

    private void OnPinsChanged(object? sender, EventArgs e) => Load();

    private void OnDrivesChanged(object? sender, EventArgs e) => LoadDrives();

    [RelayCommand]
    private void Search()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        Tab.Navigate(Location.Search(SearchText.Trim(), null));
    }

    [RelayCommand]
    private void OpenLocation(string path) => Tab.Navigate(Location.Folder(path));

    [RelayCommand]
    private void OpenLocationInNewTab(string path) => Tab.Main.OpenTab(Location.Folder(path), activate: false);

    [RelayCommand]
    private void OpenItemLocation(string path)
    {
        if (Directory.Exists(path)) Tab.Navigate(Location.Folder(path));
        else if (Path.GetDirectoryName(path) is { } parent) Tab.Navigate(Location.Folder(parent));
    }

    [RelayCommand]
    private void Unpin(string path) => Services.Locations.RemovePin(path);

    [RelayCommand]
    private void MovePinUp(string path) => Services.Locations.MovePin(path, -1);

    [RelayCommand]
    private void MovePinDown(string path) => Services.Locations.MovePin(path, +1);

    [RelayCommand]
    private void RenamePin(string path)
    {
        var current = Pins.FirstOrDefault(p => p.Path == path)?.Label ?? "";
        var label = Services.FileOps.Dialogs.Prompt("Rename pin", "Name shown for this pinned location:", current);
        if (label is not null) Services.Locations.RenamePin(path, label);
    }

    [RelayCommand]
    private void ToggleManagePins() => IsManagingPins = !IsManagingPins;

    [RelayCommand]
    private void ForgetFrequent(string path)
    {
        Services.Locations.ForgetVisit(path);
        Load();
    }

    [RelayCommand]
    private void ViewDrives() => Tab.Navigate(Location.Page(LocationKind.Drives));

    [RelayCommand]
    private void ViewLargest() => Tab.Navigate(Location.Page(LocationKind.LargestItems));

    [RelayCommand]
    private void SeeAllFrequent()
    {
        var all = Services.Locations.GetVisits().Where(v => Directory.Exists(v.Path));
        var now = DateTimeOffset.Now;
        Frequent.Clear();
        foreach (var v in FrequentLocations.Rank(all, now, 50))
            Frequent.Add(new LocationEntry(v.Path, PathUtil.DisplayName(v.Path), FrequentLocations.RecencyLabel(v.LastVisited, now)));
    }

    public static string Format(long bytes) => SizeFormatter.Format(bytes);

    public override void Dispose()
    {
        Services.Locations.PinsChanged -= OnPinsChanged;
        Services.Drives.Changed -= OnDrivesChanged;
        base.Dispose();
    }
}
