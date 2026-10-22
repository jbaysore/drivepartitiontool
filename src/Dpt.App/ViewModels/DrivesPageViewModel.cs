using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.Core.Formatting;
using Dpt.Core.Navigation;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.ViewModels;

/// <summary>One coloured block in a disk's partition bar.</summary>
public sealed record DiskSegment(string Label, string Detail, double Weight, string BrushKey, bool IsUnallocated);

public sealed record PartitionRow(PartitionInfo Partition)
{
    public string Name => Partition.DriveLetter is { } l
        ? $"{(string.IsNullOrWhiteSpace(Partition.Label) ? "Local Disk" : Partition.Label)} ({l}:)"
        : Partition.Role switch
        {
            PartitionRole.Efi => "EFI system partition",
            PartitionRole.Recovery => "Recovery partition",
            PartitionRole.Reserved => "Microsoft reserved partition",
            _ => string.IsNullOrWhiteSpace(Partition.Label) ? $"Partition {Partition.PartitionNumber}" : Partition.Label!,
        };

    public string SizeText => SizeFormatter.Format(Partition.Size);
    public string FileSystem => Partition.FileSystem is { Length: > 0 } fs ? fs : "—";
    public string UsageText => Partition.Used is { } u && Partition.VolumeSize is { } s ? $"{SizeFormatter.Format(u)} used · {SizeFormatter.Format(s - u)} free" : "";
    public double UsedFraction => Partition.Used is { } u && Partition.VolumeSize is { } s && s > 0 ? (double)u / s : 0;
    public bool HasUsage => Partition.Used is not null;
    public string Tags => string.Join(" · ", Partition.RoleTags);
    public bool HasTags => Partition.RoleTags.Count > 0;
    public string RecycleText => Partition.RecycleBinLimit is { } r ? r == 0 ? "No Recycle Bin (deletes immediately)" : $"Recycle Bin holds up to {SizeFormatter.Format(r)}" : "";
    public string? Root => Partition.Root;
    public bool CanOpen => Partition.Root is not null;
}

public sealed partial class DiskCardViewModel : ObservableObject
{
    public DiskCardViewModel(DiskInfo disk)
    {
        Disk = disk;
        Partitions = disk.Partitions.Select(p => new PartitionRow(p)).ToList();
        var segments = new List<(long Offset, DiskSegment Seg)>();
        foreach (var p in disk.Partitions)
        {
            var row = new PartitionRow(p);
            var key = p.Role switch
            {
                PartitionRole.Windows => "DptAccent",
                PartitionRole.Efi or PartitionRole.Reserved => "DptAmber",
                PartitionRole.Recovery => "DptMuted",
                _ => "DptGreen",
            };
            segments.Add((p.Offset, new DiskSegment(p.DriveLetter is { } l ? $"{l}:" : "", $"{row.Name} · {row.SizeText}", p.Size, key, false)));
        }

        foreach (var u in disk.Unallocated)
            segments.Add((u.Offset, new DiskSegment("", $"Unallocated · {SizeFormatter.Format(u.Size)}", u.Size, "DptPanelSoft", true)));
        Segments = segments.OrderBy(s => s.Offset).Select(s => s.Seg).ToList();
    }

    public DiskInfo Disk { get; }
    public IReadOnlyList<PartitionRow> Partitions { get; }
    public IReadOnlyList<DiskSegment> Segments { get; }

    public string Title => Disk.FriendlyName;

    public string Subtitle => string.Join(" · ", new[]
    {
        $"Disk {Disk.Number}",
        string.IsNullOrEmpty(Disk.MediaType) ? Disk.BusType : $"{Disk.BusType} {Disk.MediaType}",
        SizeFormatter.Format(Disk.Size),
        Disk.PartitionStyle,
        Disk.SerialNumber.Length > 0 ? $"Serial {Disk.SerialNumber}" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public IReadOnlyList<string> Badges => new[]
    {
        Disk.HoldsWindows ? "Windows starts from here" : null,
        Disk.IsUsb ? "USB" : null,
        Disk.IsVirtual ? "Virtual disk" : null,
        Disk.IsOffline ? "Offline" : null,
        Disk.Unused > 0 ? $"{SizeFormatter.Format(Disk.Unused)} unallocated" : null,
    }.Where(b => b is not null).Cast<string>().ToList();

    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private bool _healthWarning;

    public void SetHealth(DiskHealthInfo? h)
    {
        var parts = new List<string> { $"Health: {Disk.HealthStatus}" };
        if (h is { Available: true })
        {
            if (h.TemperatureC is { } t) parts.Add($"{t} °C");
            if (h.WearPercent is { } w) parts.Add($"{w}% worn");
            if (h.PowerOnHours is { } hrs) parts.Add($"{hrs:N0} hours powered on");
        }
        else if (h?.Note is { } note)
        {
            parts.Add(note);
        }

        HealthText = string.Join(" · ", parts);
        HealthWarning = Disk.HealthStatus != "Healthy" || h?.WearPercent > 80;
    }
}

/// <summary>Physical drives with their partitions/volumes, role tags, capacity and health.</summary>
public sealed partial class DrivesPageViewModel : PageViewModel
{
    public DrivesPageViewModel(TabViewModel tab) : base(tab, Location.Page(LocationKind.Drives))
    {
        Services.Drives.Changed += OnDrivesChanged;
        _ = LoadAsync();
    }

    public override string IconKey => "Icon.HardDrive";

    public ObservableCollection<DiskCardViewModel> Disks { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    private void OnDrivesChanged(object? sender, EventArgs e) => _ = LoadAsync();

    public override void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var disks = await Task.Run(() => StorageInventory.GetDisks());
            Disks.Clear();
            foreach (var d in disks) Disks.Add(new DiskCardViewModel(d));
            Error = null;
            StatusText = $"{disks.Count} drive{(disks.Count == 1 ? "" : "s")}";

            IReadOnlyList<DiskHealthInfo> health = [];
            if (Services.Catalog.IsService && Services.Catalog.Catalog is { } catalog)
            {
                try { health = await catalog.GetDiskHealthAsync(disks.Select(d => d.Number).ToList()); }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { }
            }

            foreach (var card in Disks)
            {
                var h = health.FirstOrDefault(x => x.DiskNumber == card.Disk.Number)
                        ?? new DiskHealthInfo(card.Disk.Number, false, null, null, null, Services.Catalog.IsService ? null : "Install the search service to see temperature and wear.");
                card.SetHealth(h);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Management.Infrastructure.CimException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = "Windows didn't return drive information: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenVolume(string? root)
    {
        if (root is not null) Tab.Navigate(Location.Folder(root));
    }

    [RelayCommand]
    private void OpenDiskTools() => Tab.Navigate(Location.Page(LocationKind.DiskTools));

    public override void Dispose()
    {
        Services.Drives.Changed -= OnDrivesChanged;
        base.Dispose();
    }
}
