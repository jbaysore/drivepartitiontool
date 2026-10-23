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
    public CloneViewModel(DiskInfo? preferredSource, IReadOnlyList<DiskInfo>? disks = null)
    {
        Disks = (disks ?? StorageInventory.GetDisks()).Select(d => new DiskChoice(d)).ToList();
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
    [ObservableProperty] private string _temporaryImageFolder = "";
    public bool NeedsImages => Source is not null && Destination is not null && Destination.Disk.Size < Source.Disk.Partitions.Select(p => p.Offset + p.Size).DefaultIfEmpty().Max() + ClonePlanning.Mb;

    public string Explanation =>
        "Cloning copies the source's partitions and files. A smaller destination uses file images for NTFS data volumes, with sizes adjusted to fit. Windows boot files are rebuilt when needed; starting the copy still needs verification. " +
        "Everything currently on the destination is replaced. The source drive is only read, never changed.";

    public string Outcome =>
        "Afterwards the source and its copy both contain the chosen drive's files. If you swap drives, choose the new one first in your PC's boot menu (BIOS/UEFI). " +
        "Keep only one of the two connected the first time you start from the copy.";

    public string ExpectedConfirmation => Destination is null ? "" : $"DISK {Destination.Disk.Number}";

    public string DestinationWarning => Destination is null ? ""
        : $"Everything on disk {Destination.Disk.Number} will be replaced: {Destination.Contents}.";

    public string SizeNote
    {
        get
        {
            if (Source is null || Destination is null) return "";
            if (NeedsImages)
            {
                try
                {
                    var layout = ClonePlanning.Layout(Source.Disk, Destination.Disk.Size);
                    return $"The smaller destination fits the known used data and working space. NTFS volumes will be captured and applied; other partitions are copied raw. Temporary images need about {SizeFormatter.Format(layout.StagingBytes)} on a third disk; snapshots are measured and checked again before erasing anything.";
                }
                catch (ArgumentException ex) { return ex.Message; }
            }
            var needed = Source.Disk.Partitions.Count == 0 ? 0 : Source.Disk.Partitions.Max(p => p.Offset + p.Size);
            var extra = Destination.Disk.Size - needed;
            return extra < 0
                ? $"The destination is {SizeFormatter.Format(-extra)} too small for the source's partitions."
                : extra > 16 * 1024 * 1024
                    ? $"The destination has {SizeFormatter.Format(extra)} more space than needed." + (ExtendLast ? " The last partition will grow into it." : " It will be left unallocated.")
                    : "The destination is just big enough.";
        }
    }

    partial void OnSourceChanged(DiskChoice? value) { Confirmation = ""; UpdateDestinations(); }
    partial void OnDestinationChanged(DiskChoice? value) { Confirmation = ""; Recompute(); }
    partial void OnExtendLastChanged(bool value) { Confirmation = ""; Recompute(); }
    partial void OnConfirmationChanged(string value) => Recompute();
    partial void OnTemporaryImageFolderChanged(string value) { Confirmation = ""; Recompute(); }

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
        OnPropertyChanged(nameof(NeedsImages));
        string? layoutProblem = null;
        if (Source is not null && Destination is not null)
            try { _ = ClonePlanning.Layout(Source.Disk, Destination.Disk.Size); }
            catch (Exception ex) when (ex is ArgumentException or OverflowException) { layoutProblem = ex.Message; }
        Problem = Source is null ? "Choose the drive to copy."
            : Destination is null ? "There is no other drive to copy to. Connect one first."
            : Destination.Disk.IsReadOnly || Destination.Disk.IsOffline ? "Choose a writable, online destination."
            : layoutProblem is not null ? layoutProblem
            : NeedsImages && (!Path.IsPathFullyQualified(TemporaryImageFolder) || !Directory.Exists(TemporaryImageFolder)) ? "Choose an existing temporary-image folder on a third local disk. The worker checks its physical volume and space before capture."
            : DiskWorkerLauncher.VirtualOnly && !(Source.Disk.IsVirtual && Destination.Disk.IsVirtual) ? "Development/test mode: cloning works only between virtual disks."
            : !string.Equals(Confirmation.Trim(), ExpectedConfirmation, StringComparison.OrdinalIgnoreCase) ? $"Type {ExpectedConfirmation} to confirm."
            : null;
        CanRun = Problem is null;
    }

    public DiskPlan BuildPlan()
    {
        Recompute();
        if (!CanRun) throw new InvalidOperationException(Problem);
        var id = Guid.NewGuid().ToString("N");
        return new DiskPlan
        {
            Id = id,
            Title = $"Clone disk {Source!.Disk.Number} to disk {Destination!.Disk.Number}",
            Target = Fingerprint.Of(Destination.Disk),
            Source = Fingerprint.Of(Source.Disk),
            Steps = [new DiskStep { Kind = DiskStepKind.CloneDisk, Options = JsonSerializer.Serialize(new { ExtendLastPartition = ExtendLast, MakeBootable = true, TemporaryImageFolder = NeedsImages ? TemporaryImageFolder : null }, PipeProtocol.Json) }],
            StatusFile = DiskWorkerLauncher.NewStatusFile(id),
            ExpectedConfirmation = ExpectedConfirmation,
            Confirmation = Confirmation.Trim(),
        };
    }
}
