using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.ViewModels;

public sealed record WindowsVolumeChoice(DiskInfo Disk, PartitionInfo Partition)
{
    public string Root => Partition.Root!;
    public string Label => $"{Root} {Partition.Label} · disk {Disk.Number} · {SizeFormatter.Format(Partition.Size)}";
}

public sealed partial class ContentChoice(ContentEstimate estimate) : ObservableObject
{
    public ContentItem Item { get; } = estimate.Item;
    public long Bytes => estimate.Bytes;
    public bool Complete => estimate.Complete;
    public string Title => Item.Title;
    public string Path => Item.RelativePath;
    public bool CanChoose => !Item.Required && Item.Group != ContentGroup.AlwaysExcluded;
    public string Group => Item.Group switch { ContentGroup.Windows => "Windows (required)", ContentGroup.Programs => "Installed programs", ContentGroup.AccountSettings => "Account settings", ContentGroup.PersonalFolder => "Personal folders", ContentGroup.Other => "Other content", _ => "Always excluded" };
    public string SizeText => Item.Group == ContentGroup.AlwaysExcluded ? "Excluded" : (Complete ? "" : "At least ") + SizeFormatter.Format(Bytes);
    public string Consequence => Item.Required && Item.Group == ContentGroup.Other ? "Kept in full because this branch could not be listed without administrator access." : WindowsContentClassifier.Consequence(Item.Group);
    public string Disposition => Item.Group == ContentGroup.AlwaysExcluded || !Selected ? "Stays only on source" : "Copied";
    [ObservableProperty] private bool _selected = estimate.Item.Group != ContentGroup.AlwaysExcluded;
    partial void OnSelectedChanged(bool value)
    {
        if (Item.Required && !value) Selected = true;
        if (Item.Group == ContentGroup.AlwaysExcluded && value) Selected = false;
        OnPropertyChanged(nameof(Disposition));
    }
}

/// <summary>Read-only choices and plan construction. The view owns the explicit Run action.</summary>
public sealed partial class WindowsCopyViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, CancellationToken, IProgress<string>?, Task<WindowsCopyInventory>> _scan;
    private readonly bool _virtualOnly;
    private CancellationTokenSource? _scanCancellation;
    private bool _loaded;
    private WindowsCopyLayout? _layout;

    public WindowsCopyViewModel(IReadOnlyList<DiskInfo> disks,
        Func<string, CancellationToken, IProgress<string>?, Task<WindowsCopyInventory>>? scan = null, bool? virtualOnly = null)
    {
        Disks = disks;
        _scan = scan ?? WindowsCopyDiscovery.ScanAsync;
        _virtualOnly = virtualOnly ?? DiskWorkerLauncher.IsDevelopmentBuild;
        Sources = disks.SelectMany(d => d.Partitions.Where(p => p.Root is not null && p.FileSystem == "NTFS").Select(p => new WindowsVolumeChoice(d, p))).ToList();
        _source = Sources.FirstOrDefault(s => s.Partition.Role == PartitionRole.Windows) ?? Sources.FirstOrDefault();
        _firmware = _source?.Disk.PartitionStyle == "MBR" ? BootFirmware.Bios : BootFirmware.Uefi;
        UpdateDestinations();
    }

    public IReadOnlyList<DiskInfo> Disks { get; }
    public IReadOnlyList<WindowsVolumeChoice> Sources { get; }
    public IReadOnlyList<DiskChoice> Destinations { get; private set; } = [];
    public ObservableCollection<ContentChoice> Items { get; } = [];
    public IReadOnlyList<BootFirmware> FirmwareOptions { get; } = Enum.GetValues<BootFirmware>();
    public bool HasExcludedPersonal => Items.Any(i => i.Item.Group == ContentGroup.PersonalFolder && !i.Selected);
    public string ExpectedConfirmation => Destination is null ? "" : $"DISK {Destination.Disk.Number}";
    public string Warning => Destination is null ? "Choose a destination disk." : $"Everything on disk {Destination.Disk.Number} will be erased and replaced: {Destination.Contents}. This cannot be undone.";
    public string Outcome => "The original Windows, programs and personal data stay on the source. The copy contains only the chosen content. Starting from it may need a boot-menu change; bootability has not yet been demonstrated by the development tests.";
    public string SpaceNote => $"Selected content: {(Items.Where(i => i.Selected).All(i => i.Complete) ? "" : "at least ")}{SizeFormatter.Format(SelectedBytes)}. The destination also needs room for a temporary image while files are applied.";
    public string LayoutText => _layout is null ? "A destination layout appears when the disk is large enough." : string.Join(" → ", _layout.Partitions.Select(p => $"{p.Role} ({SizeFormatter.Format(p.Size)})"));
    public long SelectedBytes => Items.Where(i => i.Selected).Sum(i => i.Bytes);
    public IReadOnlyList<string> ExcludedPersonalPaths => Source is null ? [] : Items.Where(i => i.Item.Group == ContentGroup.PersonalFolder && !i.Selected).Select(i => Path.Combine(Source.Root, i.Path)).ToList();

    [ObservableProperty] private WindowsVolumeChoice? _source;
    [ObservableProperty] private DiskChoice? _destination;
    [ObservableProperty] private BootFirmware _firmware;
    [ObservableProperty] private string _confirmation = "";
    [ObservableProperty] private string _scanMessage = "Choose a Windows volume, then load its content.";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canRun;

    partial void OnSourceChanged(WindowsVolumeChoice? value)
    {
        _scanCancellation?.Cancel(); _loaded = false;
        Items.Clear(); IsScanning = false; Notes = ""; ScanMessage = "Load the selected volume's content.";
        Firmware = value?.Disk.PartitionStyle == "MBR" ? BootFirmware.Bios : BootFirmware.Uefi;
        Confirmation = ""; UpdateDestinations();
    }
    partial void OnDestinationChanged(DiskChoice? value) { Confirmation = ""; Recompute(); }
    partial void OnFirmwareChanged(BootFirmware value) { Confirmation = ""; Recompute(); }
    partial void OnConfirmationChanged(string value) => Recompute();
    partial void OnIsRunningChanged(bool value) => Recompute();

    private void UpdateDestinations()
    {
        Destinations = Disks.Where(d => d.Number != Source?.Disk.Number && !d.IsBoot && !d.IsSystem && !d.IsReadOnly && !d.IsOffline).Select(d => new DiskChoice(d)).ToList();
        OnPropertyChanged(nameof(Destinations));
        Destination = Destinations.FirstOrDefault(d => d.Disk.Number == Destination?.Disk.Number) ?? Destinations.FirstOrDefault();
        Recompute();
    }

    public async Task LoadAsync()
    {
        _scanCancellation?.Cancel();
        var cancellation = _scanCancellation = new CancellationTokenSource();
        _loaded = false;
        Items.Clear();
        if (Source is null) { Recompute(); return; }
        IsScanning = true;
        Recompute();
        try
        {
            var inventory = await _scan(Source.Root, cancellation.Token, new Progress<string>(text => { if (!cancellation.IsCancellationRequested) ScanMessage = text; }));
            if (cancellation.IsCancellationRequested) return;
            foreach (var estimate in inventory.Items)
            {
                var row = new ContentChoice(estimate);
                row.PropertyChanged += OnChoiceChanged;
                Items.Add(row);
            }
            Notes = string.Join(" ", inventory.Notes);
            _loaded = true;
            ScanMessage = $"{Items.Count} content groups. Review what is copied and what stays only on the source.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { ScanMessage = "Could not build the preview: " + ex.Message; }
        finally { if (!cancellation.IsCancellationRequested) { IsScanning = false; Recompute(); } }
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ContentChoice.Selected)) { Confirmation = ""; Recompute(); } }

    public WindowsCopyOptions BuildOptions() => new()
    {
        SourceRoot = Source?.Root ?? "",
        Firmware = Firmware,
        Exclusions = WindowsContentClassifier.Exclusions(Items.Select(i => i.Item), Items.Where(i => i.Selected).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList(),
    };

    private void Recompute()
    {
        _layout = null;
        Problem = Source is null ? "Choose the Windows volume to copy."
            : Destination is null ? "Connect a separate writable destination disk."
            : IsScanning ? "Content sizes are being checked."
            : !_loaded ? "Load and review the source content first."
            : !Items.Any(i => i.Item.RelativePath.Equals("Windows", StringComparison.OrdinalIgnoreCase)) ? "The source does not contain a Windows folder."
            : _virtualOnly && (!Source.Disk.IsVirtual || !Destination.Disk.IsVirtual) ? "Development build: copying works only between virtual disks."
            : IsRunning ? "The copy is running."
            : null;
        try { if (_loaded && Destination is not null) _layout = WindowsCopyPlanning.Layout(BuildOptions(), Destination.Disk.Size, SelectedBytes); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { Problem = ex.Message; }
        if (Problem is null && !Confirmation.Trim().Equals(ExpectedConfirmation, StringComparison.OrdinalIgnoreCase)) Problem = $"Type {ExpectedConfirmation} to confirm replacing the destination.";
        CanRun = Problem is null;
        foreach (var property in new[] { nameof(Warning), nameof(ExpectedConfirmation), nameof(SpaceNote), nameof(LayoutText), nameof(HasExcludedPersonal), nameof(ExcludedPersonalPaths) }) OnPropertyChanged(property);
    }

    public DiskPlan BuildPlan()
    {
        Recompute();
        if (!CanRun) throw new InvalidOperationException(Problem);
        var id = Guid.NewGuid().ToString("N");
        return new() { Id = id, Title = $"Copy Windows from {Source!.Root} to disk {Destination!.Disk.Number}", Source = Fingerprint.Of(Source.Disk), Target = Fingerprint.Of(Destination.Disk),
            Steps = [new() { Kind = DiskStepKind.WindowsCopy, Options = JsonSerializer.Serialize(BuildOptions(), PipeProtocol.Json) }],
            ExpectedConfirmation = ExpectedConfirmation, Confirmation = Confirmation.Trim(), StatusFile = DiskWorkerLauncher.NewStatusFile(id) };
    }

    public void Dispose() { _scanCancellation?.Cancel(); foreach (var item in Items) item.PropertyChanged -= OnChoiceChanged; }
}
