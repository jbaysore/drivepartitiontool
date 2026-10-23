using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.ViewModels;

public sealed record DiskChoice(DiskInfo Disk)
{
    public string Label => $"Disk {Disk.Number}: {Disk.FriendlyName} · {SizeFormatter.Format(Disk.Size)} · {Disk.BusType}" + (Disk.IsVirtual ? " (virtual)" : "");

    public string Contents => Disk.Partitions.Count == 0
        ? "Empty (no partitions)"
        : string.Join(", ", Disk.Partitions.Where(p => p.Role is PartitionRole.Data or PartitionRole.Windows).Select(p =>
              $"{(p.DriveLetter is { } l ? l + ": " : "")}{p.Label} ({(p.Used is { } u ? SizeFormatter.Format(u) + " used" : SizeFormatter.Format(p.Size))})"))
          + (Disk.HoldsWindows ? " · contains Windows" : "");

    public IReadOnlyList<DiskSegment> Bar => new DiskCardViewModel(Disk).Segments;
}

/// <summary>Whole-drive clone: pick source and destination, see what happens to each, confirm by typing the destination's number.</summary>
public sealed partial class CloneViewModel : ObservableObject
{
    public CloneViewModel(DiskInfo? preferredSource)
    {
        Disks = StorageInventory.GetDisks().Select(d => new DiskChoice(d)).ToList();
        _source = Disks.FirstOrDefault(d => d.Disk.Number == preferredSource?.Number) ?? Disks.FirstOrDefault(d => d.Disk.HoldsWindows) ?? Disks.FirstOrDefault();
        UpdateDestinations();
    }

    public IReadOnlyList<DiskChoice> Disks { get; }

    public IReadOnlyList<DiskChoice> Destinations { get; private set; } = [];

    [ObservableProperty] private DiskChoice? _source;
    [ObservableProperty] private DiskChoice? _destination;
    [ObservableProperty] private bool _extendLast = AppServices.Current.Settings.CloneExtendLastPartition;
    [ObservableProperty] private string _confirmation = "";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _canRun;

    public string Explanation =>
        "Cloning makes the destination drive an exact copy of the source: the same partitions, the same files and, when the source holds Windows, the ability to start Windows. " +
        "Everything currently on the destination is replaced. The source drive is only read, never changed.";

    public string Outcome =>
        "Afterwards two identical copies exist. If you swap drives, choose the new one first in your PC's boot menu (BIOS/UEFI). " +
        "Keep only one of the two connected the first time you start from the copy.";

    public string ExpectedConfirmation => Destination is null ? "" : $"DISK {Destination.Disk.Number}";

    public string DestinationWarning => Destination is null ? ""
        : $"Everything on disk {Destination.Disk.Number} will be replaced: {Destination.Contents}.";

    public string SizeNote
    {
        get
        {
            if (Source is null || Destination is null) return "";
            var needed = Source.Disk.Partitions.Count == 0 ? 0 : Source.Disk.Partitions.Max(p => p.Offset + p.Size);
            var extra = Destination.Disk.Size - needed;
            return extra < 0
                ? $"The destination is {SizeFormatter.Format(-extra)} too small for the source's partitions."
                : extra > 16 * 1024 * 1024
                    ? $"The destination has {SizeFormatter.Format(extra)} more space than needed." + (ExtendLast ? " The last partition will grow into it." : " It will be left unallocated.")
                    : "The destination is just big enough.";
        }
    }

    partial void OnSourceChanged(DiskChoice? value) => UpdateDestinations();
    partial void OnDestinationChanged(DiskChoice? value) => Recompute();
    partial void OnExtendLastChanged(bool value) => Recompute();
    partial void OnConfirmationChanged(string value) => Recompute();

    private void UpdateDestinations()
    {
        // Never offer the source, or the disk Windows is running from, as a destination.
        Destinations = Disks.Where(d => d.Disk.Number != Source?.Disk.Number && !d.Disk.IsBoot && !d.Disk.IsSystem).ToList();
        OnPropertyChanged(nameof(Destinations));
        if (Destination is null || !Destinations.Contains(Destination)) Destination = Destinations.FirstOrDefault();
        Recompute();
    }

    private void Recompute()
    {
        OnPropertyChanged(nameof(ExpectedConfirmation));
        OnPropertyChanged(nameof(DestinationWarning));
        OnPropertyChanged(nameof(SizeNote));
        var needed = Source?.Disk.Partitions.Count > 0 ? Source.Disk.Partitions.Max(p => p.Offset + p.Size) : 0;
        Problem = Source is null ? "Choose the drive to copy."
            : Destination is null ? "There is no other drive to copy to. Connect one first."
            : Destination.Disk.Size < needed ? "The destination is too small for the source's partitions."
            : DiskWorkerLauncher.VirtualOnly && !(Source.Disk.IsVirtual && Destination.Disk.IsVirtual) ? "Development/test mode: cloning works only between virtual disks."
            : !string.Equals(Confirmation.Trim(), ExpectedConfirmation, StringComparison.OrdinalIgnoreCase) ? $"Type {ExpectedConfirmation} to confirm."
            : null;
        CanRun = Problem is null;
    }

    public DiskPlan BuildPlan()
    {
        var id = Guid.NewGuid().ToString("N");
        return new DiskPlan
        {
            Id = id,
            Title = $"Clone disk {Source!.Disk.Number} to disk {Destination!.Disk.Number}",
            Target = Fingerprint.Of(Destination.Disk),
            Source = Fingerprint.Of(Source.Disk),
            Steps = [new DiskStep { Kind = DiskStepKind.CloneDisk, Options = JsonSerializer.Serialize(new { ExtendLastPartition = ExtendLast, MakeBootable = true }, PipeProtocol.Json) }],
            StatusFile = DiskWorkerLauncher.NewStatusFile(id),
            ExpectedConfirmation = ExpectedConfirmation,
            Confirmation = Confirmation.Trim(),
        };
    }
}
