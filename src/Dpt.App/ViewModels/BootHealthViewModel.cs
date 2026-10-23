using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Services;
using Dpt.Core.Boot;
using Dpt.Core.Formatting;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.ViewModels;

public sealed record BootPartitionChoice(DiskInfo Disk, PartitionInfo Partition)
{
    public string Label => $"Disk {Disk.Number}, partition {Partition.PartitionNumber}: {(Partition.Role == PartitionRole.Efi ? "EFI (UEFI)" : "Active (BIOS)")} · {SizeFormatter.Format(Partition.Size)}";
    public string Space => Partition.FreeSpace is { } free ? $"{SizeFormatter.Format(free)} free" : "Free space unavailable without additional volume access";
    public BootFirmware Firmware => Partition.Role == PartitionRole.Efi ? BootFirmware.Uefi : BootFirmware.Bios;
}

public sealed partial class BootHealthViewModel : ObservableObject, IDisposable
{
    private readonly Func<CancellationToken, Task<BootHealthSnapshot>> _read;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _allowSystem, _virtualOnly;
    public BootHealthViewModel(Func<CancellationToken, Task<BootHealthSnapshot>>? read = null, bool? allowSystem = null, bool? virtualOnly = null)
    {
        _read = read ?? BootHealthService.ReadAsync;
        _allowSystem = allowSystem ?? AppServices.Current.Settings.AllowSystemPartitions;
        _virtualOnly = virtualOnly ?? DiskWorkerLauncher.VirtualOnly;
        Recompute();
    }

    public ObservableCollection<WindowsVolumeChoice> Sources { get; } = [];
    public ObservableCollection<BootPartitionChoice> BootPartitions { get; } = [];
    public ObservableCollection<BootEntry> Entries { get; } = [];
    public string ExpectedConfirmation => Destination is null ? "" : $"DISK {Destination.Disk.Number}";
    public string Preview => Source is null || Destination is null ? "Choose the Windows installation and the partition that should start it."
        : $"Rebuild {Destination.Firmware} boot files for {Source.Root}Windows on disk {Destination.Disk.Number}, partition {Destination.Partition.PartitionNumber}. " +
          "Existing startup entries can change. A copy of the existing BCD file is saved on that partition when readable. This does not format the partition or change Windows files. A successful write still needs a restart check.";

    [ObservableProperty] private WindowsVolumeChoice? _source;
    [ObservableProperty] private BootPartitionChoice? _destination;
    [ObservableProperty] private string _confirmation = "";
    [ObservableProperty] private string _summary = "Loading read-only boot information…";
    [ObservableProperty] private string _configurationNote = "";
    [ObservableProperty] private string _rawConfiguration = "";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canRun;

    public async Task RefreshAsync()
    {
        if (IsRunning || IsLoading) return;
        IsLoading = true; Recompute();
        try
        {
            var snapshot = await _read(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            Sources.Clear(); BootPartitions.Clear(); Entries.Clear();
            foreach (var disk in snapshot.Disks)
                foreach (var partition in disk.Partitions)
                {
                    if (partition.Root is not null && string.Equals(partition.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)) Sources.Add(new(disk, partition));
                    if (partition.Role == PartitionRole.Efi || (disk.PartitionStyle == "MBR" && partition.IsActive)) BootPartitions.Add(new(disk, partition));
                }
            Source = Sources.FirstOrDefault(s => s.Partition.Role == PartitionRole.Windows) ?? Sources.FirstOrDefault();
            Destination = BootPartitions.FirstOrDefault(p => p.Partition.IsSystem || p.Disk.IsSystem) ?? BootPartitions.FirstOrDefault();
            var windows = snapshot.Disks.Where(d => d.IsBoot).Select(d => "disk " + d.Number);
            var system = snapshot.Disks.Where(d => d.IsSystem).Select(d => "disk " + d.Number);
            Summary = $"Windows is running from {string.Join(", ", windows.DefaultIfEmpty("an unidentified disk"))}. Startup files used by this PC are on {string.Join(", ", system.DefaultIfEmpty("an unidentified disk"))}. " +
                (BootPartitions.Count == 0 ? "No accessible EFI or active BIOS partition was reported." : $"{BootPartitions.Count} EFI/active boot partition(s) found.");
            RawConfiguration = snapshot.Configuration.Output;
            foreach (var entry in BootConfiguration.Parse(RawConfiguration)) Entries.Add(entry);
            ConfigurationNote = snapshot.Configuration.Available
                ? Entries.Count > 0 ? "Current startup entries (read only)." : "The Windows output could not be interpreted, possibly because it uses different field names. Inspect the raw details below; no health conclusion was inferred."
                : snapshot.Configuration.Note ?? "The startup configuration is unavailable. Administrator access through the background catalog service may be needed.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Management.Infrastructure.CimException)
        { Summary = "Boot information is unavailable: " + ex.Message; }
        finally { IsLoading = false; Recompute(); }
    }
    partial void OnSourceChanged(WindowsVolumeChoice? value) { Confirmation = ""; Recompute(); }
    partial void OnDestinationChanged(BootPartitionChoice? value) { Confirmation = ""; Recompute(); }
    partial void OnConfirmationChanged(string value) => Recompute();
    partial void OnIsRunningChanged(bool value) => Recompute();

    private DiskPlan Candidate()
    {
        var id = Guid.NewGuid().ToString("N");
        return new() { Id = id, Title = "Rebuild Windows boot files", Source = Fingerprint.Of(Source!.Disk), Target = Fingerprint.Of(Destination!.Disk),
            Steps = [new() { Kind = DiskStepKind.RebuildBootFiles, PartitionNumber = Destination.Partition.PartitionNumber,
                Options = JsonSerializer.Serialize(new BootRepairOptions { SourceRoot = Source.Root, Firmware = Destination.Firmware }, PipeProtocol.Json) }],
            AllowSystemPartitions = _allowSystem, ExpectedConfirmation = ExpectedConfirmation, Confirmation = Confirmation.Trim(), StatusFile = DiskWorkerLauncher.NewStatusFile(id) };
    }
    private void Recompute()
    {
        Problem = IsRunning ? "Boot repair is running." : IsLoading ? "Loading read-only information." : Source is null || Destination is null ? "Choose an accessible Windows volume and EFI/active boot partition." : null;
        if (Problem is null)
            try { BootRepairValidation.Validate(Candidate(), Source!.Disk, Destination!.Disk, new() { SourceRoot = Source.Root, Firmware = Destination.Firmware }, _virtualOnly); }
            catch (ArgumentException ex) { Problem = ex.Message; }
        CanRun = Problem is null;
        OnPropertyChanged(nameof(ExpectedConfirmation)); OnPropertyChanged(nameof(Preview));
    }
    public DiskPlan BuildPlan() { Recompute(); return CanRun ? Candidate() : throw new InvalidOperationException(Problem); }
    public void Dispose() => _lifetime.Cancel();
}
