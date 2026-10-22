using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Services;
using Dpt.Core.Navigation;

namespace Dpt.App.ViewModels;

/// <summary>Base for everything a tab can show (Home, a folder, search results, Drives, Settings…).</summary>
public abstract partial class PageViewModel(TabViewModel tab, Location location) : ObservableObject, IDisposable
{
    public TabViewModel Tab { get; } = tab;
    public Location Location { get; protected set; } = location;
    protected AppServices Services => AppServices.Current;

    public virtual string Title => Location.Title;
    public virtual string IconKey => "Icon.Folder";

    /// <summary>Folder used as the scope of the search box and as the target for paste/new, if any.</summary>
    public virtual string? CurrentFolder => null;

    [ObservableProperty] private string _statusText = "";

    public virtual void Activate()
    {
    }

    public virtual void Refresh()
    {
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>Shown for pages whose milestone has not been built yet.</summary>
public sealed class PlaceholderPageViewModel(TabViewModel tab, Location location, string message) : PageViewModel(tab, location)
{
    public string Message { get; } = message;

    public override string IconKey => Location.Kind switch
    {
        LocationKind.Search => "Icon.Search",
        LocationKind.LargestItems => "Icon.ChartPie",
        LocationKind.Drives => "Icon.HardDrive",
        LocationKind.Rules => "Icon.ListFilter",
        LocationKind.Layouts => "Icon.Layout",
        LocationKind.DiskTools => "Icon.Sliders",
        _ => "Icon.Info",
    };
}
