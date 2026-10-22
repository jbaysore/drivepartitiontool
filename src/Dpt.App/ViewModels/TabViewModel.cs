using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Core.Navigation;

namespace Dpt.App.ViewModels;

public sealed record BreadcrumbItem(string Label, string Path, bool IsLast);

/// <summary>Creates the page for a location. Later milestones register their pages here.</summary>
public static class PageFactory
{
    private static readonly Dictionary<LocationKind, Func<TabViewModel, Location, PageViewModel>> Factories = new()
    {
        [LocationKind.Home] = (t, _) => new HomePageViewModel(t),
        [LocationKind.Folder] = (t, l) => new FolderPageViewModel(t, l),
        [LocationKind.Settings] = (t, _) => new SettingsPageViewModel(t),
        [LocationKind.Search] = (t, l) => new SearchPageViewModel(t, l),
        [LocationKind.Drives] = (t, _) => new DrivesPageViewModel(t),
        [LocationKind.LargestItems] = (t, l) => new LargestPageViewModel(t, l),
    };

    public static void Register(LocationKind kind, Func<TabViewModel, Location, PageViewModel> factory) => Factories[kind] = factory;

    public static PageViewModel Create(TabViewModel tab, Location location) =>
        Factories.TryGetValue(location.Kind, out var f)
            ? f(tab, location)
            : new PlaceholderPageViewModel(tab, location, $"{location.Title} is not available yet.");
}

/// <summary>One browser-style tab: its history, address bar and current page.</summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private readonly NavigationHistory<Location> _history = new();

    public TabViewModel(MainViewModel main, Location location)
    {
        Main = main;
        Navigate(location);
    }

    public MainViewModel Main { get; }

    [ObservableProperty] private PageViewModel? _page;
    [ObservableProperty] private string _addressText = "";
    [ObservableProperty] private bool _isEditingAddress;
    [ObservableProperty] private string _searchText = "";

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public Location Location => _history.Current ?? Location.Home;
    public string Title => Page?.Title ?? "";
    public string IconKey => Page?.IconKey ?? "Icon.Folder";
    public bool IsHome => Location.Kind == LocationKind.Home;
    public bool ShowSearchBox => !IsHome;

    public string SearchPlaceholder => Page?.CurrentFolder is { } f && AppServices.Current.Settings.FolderSearchIsLocal
        ? $"Search {PathUtil.DisplayName(f)}"
        : "Search all selected drives";

    public void Navigate(Location location, bool addToHistory = true)
    {
        if (location.Kind == LocationKind.Folder && location.Path is { } p && !Directory.Exists(p))
        {
            AppServices.Current.FileOps.Dialogs?.ShowError("Can't open location", $"Windows can't find \"{p}\".");
            UpdateAddress();
            return;
        }

        if (addToHistory) _history.Navigate(location);
        var old = Page;
        Page = PageFactory.Create(this, location);
        old?.Dispose();
        Page.Activate();
        IsEditingAddress = false;
        SearchText = location.Kind == LocationKind.Search ? location.Query ?? "" : "";
        UpdateAddress();
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(ShowSearchBox));
        OnPropertyChanged(nameof(SearchPlaceholder));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
        Main.OnTabNavigated(this);
    }

    private void UpdateAddress()
    {
        Breadcrumbs.Clear();
        var loc = Location;
        if (loc.Kind == LocationKind.Folder && loc.Path is { } path)
        {
            AddressText = path;
            var parts = PathUtil.Ancestry(path);
            for (var i = 0; i < parts.Count; i++)
                Breadcrumbs.Add(new BreadcrumbItem(PathUtil.DisplayName(parts[i]), parts[i], i == parts.Count - 1));
        }
        else if (loc.Kind == LocationKind.Search && loc.Path is { } scope)
        {
            AddressText = scope;
            Breadcrumbs.Add(new BreadcrumbItem($"Search in {PathUtil.DisplayName(scope)}", scope, true));
        }
        else
        {
            AddressText = loc.Title;
            Breadcrumbs.Add(new BreadcrumbItem(loc.Title, "", true));
        }
    }

    private bool CanBack() => _history.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        if (_history.CanGoBack) Navigate(_history.GoBack(), addToHistory: false);
    }

    private bool CanForward() => _history.CanGoForward;

    [RelayCommand(CanExecute = nameof(CanForward))]
    private void Forward()
    {
        if (_history.CanGoForward) Navigate(_history.GoForward(), addToHistory: false);
    }

    private bool CanUp() => Location.Kind is LocationKind.Folder or LocationKind.Search && Location.Path is not null;

    [RelayCommand(CanExecute = nameof(CanUp))]
    private void Up()
    {
        if (Location.Path is not { } p) return;
        if (Location.Kind == LocationKind.Search)
        {
            Navigate(Location.Folder(p));
            return;
        }

        var parent = PathUtil.Parent(p);
        Navigate(parent is null ? Location.Home : Location.Folder(parent));
    }

    [RelayCommand]
    private void Refresh() => Page?.Refresh();

    [RelayCommand]
    private void EditAddress()
    {
        AddressText = Location.Kind == LocationKind.Folder ? Location.Path ?? "" : "";
        IsEditingAddress = true;
    }

    [RelayCommand]
    private void CommitAddress()
    {
        var resolved = AppServices.Current.Addresses.Resolve(AddressText);
        if (resolved is null)
        {
            AppServices.Current.FileOps.Dialogs.ShowError("Can't open location", $"Windows can't find \"{AddressText}\". Check the spelling and try again.");
            return;
        }

        Navigate(resolved);
    }

    [RelayCommand]
    private void CancelAddress()
    {
        IsEditingAddress = false;
        UpdateAddress();
    }

    [RelayCommand]
    private void OpenBreadcrumb(string path)
    {
        if (!string.IsNullOrEmpty(path)) Navigate(Location.Folder(path));
    }

    [RelayCommand]
    private void Search()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;
        var scope = AppServices.Current.Settings.FolderSearchIsLocal ? Page?.CurrentFolder : null;
        if (Location.Kind == LocationKind.Search) scope = Location.Path;
        Navigate(Location.Search(text, scope));
    }

    public void Dispose() => Page?.Dispose();
}
