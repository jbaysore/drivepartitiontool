using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Ipc;

namespace Dpt.App.ViewModels;

public sealed partial class VolumeRowViewModel(VolumeStatus status, Action<VolumeRowViewModel> toggled) : ObservableObject
{
    private bool _selected = status.Selected;

    public VolumeStatus Status { get; private set; } = status;
    public string Root => Status.Root;
    public string Label => string.IsNullOrWhiteSpace(Status.Label) ? Status.Root.TrimEnd('\\') : $"{Status.Label} ({Status.Root.TrimEnd('\\')})";

    public string StateText => Status.State switch
    {
        VolumeState.Ready => $"Ready · {Status.Items:N0} items · {(Status.Mode == "MFT" ? "instant updates" : "updates while the app is open")}",
        VolumeState.Scanning => $"Scanning… {Status.Progress:P0} · {Status.Items:N0} items",
        VolumeState.Waiting => "Waiting",
        VolumeState.NotSelected => "Not searched",
        VolumeState.Unavailable => "Not connected",
        VolumeState.Error => "Problem: " + Status.Message,
        _ => Status.State.ToString(),
    };

    public bool Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            toggled(this);
        }
    }

    public void Update(VolumeStatus s)
    {
        Status = s;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Label));
    }
}

/// <summary>Settings panel: which drives are searched, catalog progress, and the background service.</summary>
public sealed partial class CatalogPanelViewModel : ObservableObject
{
    private static AppServices Services => AppServices.Current;

    public CatalogPanelViewModel()
    {
        Services.Catalog.StatusChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public ObservableCollection<VolumeRowViewModel> Volumes { get; } = [];

    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _serviceText = "";
    [ObservableProperty] private bool _serviceInstalled;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;

    private void Refresh()
    {
        var state = ServiceInstaller.GetState();
        ServiceInstalled = state != CatalogServiceState.NotInstalled;
        ServiceText = state switch
        {
            CatalogServiceState.Running => "The background search service is installed and running. Search results and folder sizes stay current even while the app is closed.",
            CatalogServiceState.Starting => "The background search service is starting.",
            CatalogServiceState.Stopped => "The background search service is installed but stopped.",
            CatalogServiceState.NotInstalled => "The background search service isn't installed. Installing it (one administrator prompt) makes the first scan much faster and keeps the catalog current while the app is closed. It only reads file names and sizes; it never changes files.",
            _ => "",
        };
        ModeText = Services.Catalog.Catalog is null ? "The search catalog is off."
            : Services.Catalog.IsService ? "Searching with the background service." : "Searching with the catalog built inside the app.";

        var status = Services.Catalog.Status;
        if (status is null) return;
        foreach (var v in status.Volumes)
        {
            var row = Volumes.FirstOrDefault(r => string.Equals(r.Root, v.Root, StringComparison.OrdinalIgnoreCase));
            if (row is null) Volumes.Add(new VolumeRowViewModel(v, OnToggled));
            else row.Update(v);
        }
    }

    private async void OnToggled(VolumeRowViewModel _)
    {
        if (Services.Catalog.Catalog is not { } catalog) return;
        await catalog.SetSelectedVolumesAsync(Volumes.Where(v => v.Selected).Select(v => v.Root).ToList());
    }

    [RelayCommand]
    private async Task InstallService()
    {
        Busy = true;
        Message = "Waiting for the administrator prompt…";
        var code = await ServiceInstaller.RunElevatedAsync($"--install-service --owner {ServiceInstaller.CurrentUserSid()}");
        Message = code switch
        {
            null => "Installation cancelled.",
            0 => await Services.Catalog.TrySwitchToServiceAsync() ? "Installed. The service is building the catalog." : "Installed, but the service isn't answering yet.",
            _ => $"Installation failed (code {code}). Details: {Path.Combine(ServiceInstaller.ProgramData, "install.log")}",
        };
        Busy = false;
        Refresh();
    }

    [RelayCommand]
    private async Task UninstallService()
    {
        if (!Services.FileOps.Dialogs.Confirm("Remove search service", "Remove the background search service? Search keeps working with the catalog built inside the app.", "Remove")) return;
        Busy = true;
        var code = await ServiceInstaller.RunElevatedAsync("--uninstall-service");
        if (code == 0) Services.Catalog.FallBackToLocal();
        Message = code switch { null => "Cancelled.", 0 => "The service was removed.", _ => $"Removal failed (code {code})." };
        Busy = false;
        Refresh();
    }

    [RelayCommand]
    private void Rebuild(string root)
    {
        Services.Catalog.Rebuild(root);
        Message = $"Rebuilding the catalog for {root.TrimEnd('\\')}.";
    }
}
