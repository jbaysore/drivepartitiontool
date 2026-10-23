using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.ViewModels;

/// <summary>One partition change: inputs, a before/after picture, plain-language consequences, confirmation, and the plan.</summary>
public sealed partial class PartitionActionViewModel : ObservableObject
{
    private const long Mb = 1024 * 1024;

    public PartitionActionViewModel(PartitionAction action, DiskInfo disk, PartitionInfo? partition, UnallocatedSpace? gap, bool allowSystem)
    {
        Action = action;
        Disk = disk;
        Partition = partition;
        Gap = gap;
        AllowSystem = allowSystem;
        var used = partition?.Used ?? 0;
        var following = partition is null ? null : disk.Unallocated.FirstOrDefault(u => Math.Abs(u.Offset - (partition.Offset + partition.Size)) < 2 * Mb);
        switch (action)
        {
            case PartitionAction.Shrink:
                MinMb = Math.Max(1, (used + used / 10 + 64 * Mb) / Mb);
                MaxMb = Math.Max(MinMb, partition!.Size / Mb - 1);
                _sizeMb = Math.Max(MinMb, partition.Size / Mb / 2);
                break;
            case PartitionAction.Extend:
                MinMb = partition!.Size / Mb + 1;
                MaxMb = (partition.Size + (following?.Size ?? 0)) / Mb;
                _sizeMb = MaxMb;
                break;
            case PartitionAction.Create:
                MinMb = 32;
                MaxMb = gap!.Size / Mb;
                _sizeMb = MaxMb;
                _fileSystem = "NTFS";
                _label = "New Volume";
                break;
            case PartitionAction.Format:
                _fileSystem = partition!.FileSystem is { Length: > 0 } fs && FileSystems.Contains(fs) ? fs : "NTFS";
                _label = partition.Label ?? "";
                break;
            case PartitionAction.Label:
                _label = partition!.Label ?? "";
                break;
            case PartitionAction.Letter:
                var used2 = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
                Letters = new[] { "No letter (hide from File Explorer)" }.Concat("DEFGHIJKLMNOPQRSTUVWXYZ".Where(c => !used2.Contains(c) || c == partition!.DriveLetter).Select(c => $"{c}:")).ToList();
                _letter = partition!.DriveLetter is { } l ? $"{l}:" : Letters[0];
                break;
        }

        ExpectedConfirmation = action switch
        {
            PartitionAction.Delete or PartitionAction.Format => partition!.DriveLetter is { } dl ? $"{dl}:" : $"DISK {disk.Number} PARTITION {partition.PartitionNumber}",
            _ => null,
        };
        Recompute();
    }

    public PartitionAction Action { get; }
    public DiskInfo Disk { get; }
    public PartitionInfo? Partition { get; }
    public UnallocatedSpace? Gap { get; }
    public bool AllowSystem { get; }

    public long MinMb { get; }
    public long MaxMb { get; }
    public IReadOnlyList<string> FileSystems { get; } = ["NTFS", "exFAT", "FAT32"];
    public IReadOnlyList<string> Letters { get; } = [];
    public string? ExpectedConfirmation { get; }

    public bool ShowSize => Action is PartitionAction.Shrink or PartitionAction.Extend or PartitionAction.Create;
    public bool ShowFileSystem => Action is PartitionAction.Create or PartitionAction.Format;
    public bool ShowLabel => Action is PartitionAction.Create or PartitionAction.Format or PartitionAction.Label;
    public bool ShowLetter => Action == PartitionAction.Letter;
    public bool NeedsTypedConfirmation => ExpectedConfirmation is not null;
    public bool IsDestructive => Action is PartitionAction.Delete or PartitionAction.Format;

    public string Title => Action switch
    {
        PartitionAction.Shrink => "Make the partition smaller",
        PartitionAction.Extend => "Make the partition bigger",
        PartitionAction.Create => "Create a partition",
        PartitionAction.Delete => "Delete the partition",
        PartitionAction.Format => "Format (erase) the partition",
        PartitionAction.Letter => "Change the drive letter",
        _ => "Rename the partition",
    };

    public string Explanation => Action switch
    {
        PartitionAction.Shrink => Explanations.Shrink,
        PartitionAction.Extend => Explanations.Extend,
        PartitionAction.Create => Explanations.Create + " " + Explanations.FileSystems,
        PartitionAction.Delete => Explanations.Delete,
        PartitionAction.Format => Explanations.Format + " " + Explanations.FileSystems,
        PartitionAction.Letter => Explanations.Letter,
        _ => Explanations.Label,
    };

    public string DataNote => Action switch
    {
        PartitionAction.Delete or PartitionAction.Format => $"All {SizeFormatter.Format(Partition!.Used ?? Partition.Size)} of data on this partition will be lost.",
        PartitionAction.Shrink => $"Files stay. {SizeFormatter.Format(Partition!.Used ?? 0)} is in use, so it can't be made smaller than about {SizeFormatter.Format(MinMb * Mb)}.",
        PartitionAction.Create => "No existing files are affected.",
        _ => "Files stay as they are.",
    };

    public string UndoNote => Action switch
    {
        PartitionAction.Delete or PartitionAction.Format => "This can't be undone.",
        PartitionAction.Shrink => "You can extend it again later if the freed space is still unallocated.",
        PartitionAction.Extend => "You can shrink it again later.",
        PartitionAction.Create => "You can delete the new partition later.",
        _ => "You can change it back later.",
    };

    [ObservableProperty] private long _sizeMb;
    [ObservableProperty] private string _fileSystem = "NTFS";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _letter = "";
    [ObservableProperty] private string _confirmation = "";
    [ObservableProperty] private IReadOnlyList<DiskSegment> _before = [];
    [ObservableProperty] private IReadOnlyList<DiskSegment> _after = [];
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _canRun;

    partial void OnSizeMbChanged(long value) => Recompute();
    partial void OnFileSystemChanged(string value) => Recompute();
    partial void OnLabelChanged(string value) => Recompute();
    partial void OnLetterChanged(string value) => Recompute();
    partial void OnConfirmationChanged(string value) => Recompute();

    private static IReadOnlyList<DiskSegment> Bar(IEnumerable<(long Offset, long Size, string Label, string Brush, bool Gap)> parts) =>
        parts.OrderBy(p => p.Offset).Select(p => new DiskSegment(p.Label, $"{p.Label} {SizeFormatter.Format(p.Size)}".Trim(), p.Size, p.Brush, p.Gap)).ToList();

    private static string Brush(PartitionInfo p) => p.Role switch
    {
        PartitionRole.Windows => "DptAccent",
        PartitionRole.Efi or PartitionRole.Reserved => "DptAmber",
        PartitionRole.Recovery => "DptMuted",
        _ => "DptGreen",
    };

    private void Recompute()
    {
        var parts = Disk.Partitions.Select(p => (p.Offset, p.Size, Label: p.DriveLetter is { } l ? $"{l}:" : "", Brush: Brush(p), Gap: false)).ToList();
        var gaps = Disk.Unallocated.Select(u => (u.Offset, u.Size, Label: "", Brush: "DptPanelSoft", Gap: true)).ToList();
        Before = Bar(parts.Concat(gaps));

        var after = new List<(long Offset, long Size, string Label, string Brush, bool Gap)>(gaps.Concat(parts));
        var size = SizeMb * Mb;
        switch (Action)
        {
            case PartitionAction.Shrink or PartitionAction.Extend:
                after.RemoveAll(x => x.Offset == Partition!.Offset);
                after.RemoveAll(x => x.Gap && x.Offset >= Partition!.Offset + Partition.Size - 2 * Mb && x.Offset <= Partition.Offset + Partition.Size + 2 * Mb);
                var end = Partition!.Offset + Partition.Size + (Disk.Unallocated.FirstOrDefault(u => Math.Abs(u.Offset - (Partition.Offset + Partition.Size)) < 2 * Mb)?.Size ?? 0);
                after.Add((Partition.Offset, size, Partition.DriveLetter is { } l ? $"{l}:" : "", Brush(Partition), false));
                if (end - (Partition.Offset + size) > 0) after.Add((Partition.Offset + size, end - (Partition.Offset + size), "", "DptPanelSoft", true));
                break;
            case PartitionAction.Create:
                after.RemoveAll(x => x.Gap && x.Offset == Gap!.Offset);
                after.Add((Gap!.Offset, size, "new", "DptGreen", false));
                if (Gap.Size - size > 0) after.Add((Gap.Offset + size, Gap.Size - size, "", "DptPanelSoft", true));
                break;
            case PartitionAction.Delete:
                after.RemoveAll(x => x.Offset == Partition!.Offset);
                after.Add((Partition!.Offset, Partition.Size, "", "DptPanelSoft", true));
                break;
        }

        After = Bar(after);

        Problem = Action switch
        {
            _ when ShowSize && (SizeMb < MinMb || SizeMb > MaxMb) => $"Choose a size between {SizeFormatter.Format(MinMb * Mb)} and {SizeFormatter.Format(MaxMb * Mb)}.",
            _ when ShowSize && Action == PartitionAction.Shrink && SizeMb * Mb >= Partition!.Size => "The new size must be smaller than the current size.",
            PartitionAction.Create or PartitionAction.Format when FileSystem == "FAT32" && SizeMb * Mb > 32L * 1024 * Mb && Action == PartitionAction.Create => "Windows only formats FAT32 up to 32 GB; choose exFAT or NTFS for bigger partitions.",
            PartitionAction.Label when Label.Length > 32 => "Labels can be at most 32 characters.",
            _ when NeedsTypedConfirmation && !string.Equals(Confirmation.Trim(), ExpectedConfirmation, StringComparison.OrdinalIgnoreCase) => $"Type {ExpectedConfirmation} to confirm.",
            _ => null,
        };
        CanRun = Problem is null;
    }

    public DiskPlan BuildPlan()
    {
        var p = Partition;
        var step = Action switch
        {
            PartitionAction.Shrink or PartitionAction.Extend => new DiskStep { Kind = DiskStepKind.ResizePartition, PartitionNumber = p!.PartitionNumber, Size = SizeMb * Mb },
            PartitionAction.Create => new DiskStep { Kind = DiskStepKind.CreatePartition, Offset = Gap!.Offset, Size = SizeMb * Mb >= Gap.Size - Mb ? null : SizeMb * Mb, FileSystem = FileSystem, Label = Label, AutoLetter = true },
            PartitionAction.Delete => new DiskStep { Kind = DiskStepKind.DeletePartition, PartitionNumber = p!.PartitionNumber },
            PartitionAction.Format => new DiskStep { Kind = DiskStepKind.FormatVolume, PartitionNumber = p!.PartitionNumber, FileSystem = FileSystem, Label = Label },
            PartitionAction.Letter => new DiskStep { Kind = DiskStepKind.SetDriveLetter, PartitionNumber = p!.PartitionNumber, DriveLetter = Letter.Length == 2 && Letter[1] == ':' ? Letter[0] : null },
            _ => new DiskStep { Kind = DiskStepKind.SetLabel, PartitionNumber = p!.PartitionNumber, Label = Label },
        };
        var id = Guid.NewGuid().ToString("N");
        return new DiskPlan
        {
            Id = id,
            Title = Title + (p?.DriveLetter is { } l ? $" ({l}:)" : $" (disk {Disk.Number})"),
            Target = Fingerprint.Of(Disk),
            Steps = [step],
            AllowSystemPartitions = AllowSystem,
            StatusFile = DiskWorkerLauncher.NewStatusFile(id),
            ExpectedConfirmation = ExpectedConfirmation,
            Confirmation = NeedsTypedConfirmation ? Confirmation.Trim() : null,
        };
    }
}
