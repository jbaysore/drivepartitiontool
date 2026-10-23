using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Core.Navigation;
using Dpt.Storage;

namespace Dpt.App.ViewModels;

public enum PartitionAction
{
    Shrink,
    Extend,
    Create,
    Delete,
    Format,
    Letter,
    Label,
}

/// <summary>A clickable block in a disk's bar: a partition or a stretch of unallocated space.</summary>
public sealed partial class SegmentItem(DiskInfo disk, PartitionInfo? partition, UnallocatedSpace? gap) : ObservableObject
{
    public DiskInfo Disk { get; } = disk;
    public PartitionInfo? Partition { get; } = partition;
    public UnallocatedSpace? Gap { get; } = gap;
    public long Size => Partition?.Size ?? Gap!.Size;
    public double Weight => Size;
    public bool IsGap => Gap is not null;

    public string Label => Partition is { } p
        ? (p.DriveLetter is { } l ? $"{l}:" : p.Role switch { PartitionRole.Efi => "EFI", PartitionRole.Recovery => "Rec", PartitionRole.Reserved => "MSR", _ => "" })
        : "";

    public string Detail => Partition is { } p ? $"{new PartitionRow(p).Name} · {SizeFormatter.Format(p.Size)}" : $"Unallocated · {SizeFormatter.Format(Gap!.Size)}";

    public string BrushKey => Partition is { } p
        ? p.Role switch { PartitionRole.Windows => "DptAccent", PartitionRole.Efi or PartitionRole.Reserved => "DptAmber", PartitionRole.Recovery => "DptMuted", _ => "DptGreen" }
        : "DptPanelSoft";

    [ObservableProperty] private bool _isSelected;
}

public sealed record ActionItem(PartitionAction Action, string Title, string Description, bool Enabled, string? DisabledReason);

public sealed partial class DiskToolCard(DiskInfo disk) : ObservableObject
{
    public DiskInfo Disk { get; } = disk;
    public DiskCardViewModel Info { get; } = new(disk);

    public IReadOnlyList<SegmentItem> Segments { get; } =
        disk.Partitions.Select(p => (Offset: p.Offset, Item: new SegmentItem(disk, p, null)))
            .Concat(disk.Unallocated.Select(u => (Offset: u.Offset, Item: new SegmentItem(disk, null, u))))
            .OrderBy(x => x.Offset).Select(x => x.Item).ToList();
}

/// <summary>Advanced disk tools: partitions, cloning, Windows copy and boot health, each with plain-language previews.</summary>
public sealed partial class DiskToolsPageViewModel : PageViewModel
{
    public DiskToolsPageViewModel(TabViewModel tab) : base(tab, Location.Page(LocationKind.DiskTools))
    {
        Services.Drives.Changed += OnDrivesChanged;
        _ = LoadAsync();
    }

    public override string IconKey => "Icon.Sliders";

    public ObservableCollection<DiskToolCard> Disks { get; } = [];
    public ObservableCollection<ActionItem> Actions { get; } = [];

    [ObservableProperty] private SegmentItem? _selected;
    [ObservableProperty] private string _selectionTitle = "Choose a partition or unallocated space above.";
    [ObservableProperty] private string? _selectionNote;

    public bool IsDevelopmentBuild => DiskWorkerLauncher.IsDevelopmentBuild;
    public string DevelopmentNote => Explanations.DevelopmentBuild;
    public string PartitionExplanation => Explanations.Partition;

    /// <summary>Dialog hooks (set by the app).</summary>
    public static Func<PartitionActionViewModel, Task<bool>>? ShowAction { get; set; }

    public static Func<DiskInfo?, Task>? ShowClone { get; set; }
    public static Func<Task>? ShowWindowsCopy { get; set; }
    public static Func<Task>? ShowBootHealth { get; set; }

    private void OnDrivesChanged(object? sender, EventArgs e) => _ = LoadAsync();

    public override void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var disks = await Task.Run(() => StorageInventory.GetDisks());
        var selectedKey = Selected is { } s ? (s.Disk.Number, s.Partition?.PartitionNumber, s.Gap?.Offset) : default;
        Disks.Clear();
        foreach (var d in disks) Disks.Add(new DiskToolCard(d));
        Selected = Disks.SelectMany(c => c.Segments).FirstOrDefault(x => (x.Disk.Number, x.Partition?.PartitionNumber, x.Gap?.Offset) == selectedKey);
        if (Selected is null) UpdateActions();
    }

    [RelayCommand]
    private void Select(SegmentItem? item) => Selected = item;

    partial void OnSelectedChanged(SegmentItem? oldValue, SegmentItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        UpdateActions();
    }

    private void UpdateActions()
    {
        Actions.Clear();
        if (Selected is not { } s)
        {
            SelectionTitle = "Choose a partition or unallocated space above.";
            SelectionNote = null;
            return;
        }

        var allowSystem = Services.Settings.AllowSystemPartitions;
        var devLock = IsDevelopmentBuild && !s.Disk.IsVirtual ? "Development build: only virtual disks can be changed." : null;
        SelectionTitle = $"Disk {s.Disk.Number} · {s.Detail}";
        if (s.Gap is { } gap)
        {
            SelectionNote = null;
            Actions.Add(new ActionItem(PartitionAction.Create, "Create a partition here…", Explanations.Create, devLock is null, devLock));
            return;
        }

        var p = s.Partition!;
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)![0];
        var isWindows = p.DriveLetter == systemRoot;
        var locked = p.IsProtected && !allowSystem ? "Locked: needed to start Windows. See Settings → Disk tools." : null;
        SelectionNote = p.IsProtected ? Explanations.Protected : null;
        string? Reason(bool destructive) => devLock ?? (destructive && isWindows ? "Not possible: Windows is running from this partition." : locked);
        var after = s.Disk.Unallocated.FirstOrDefault(u => u.Offset == p.Offset + p.Size || Math.Abs(u.Offset - (p.Offset + p.Size)) < 2 * 1024 * 1024);
        var hasFs = p.FileSystem is { Length: > 0 };
        Actions.Add(new ActionItem(PartitionAction.Shrink, "Make smaller (shrink)…", Explanations.Shrink, Reason(false) is null && hasFs, Reason(false) ?? (hasFs ? null : "Only partitions with a file system can be shrunk.")));
        Actions.Add(new ActionItem(PartitionAction.Extend, "Make bigger (extend)…", Explanations.Extend, Reason(false) is null && after is not null && hasFs,
            Reason(false) ?? (after is null ? "There is no unallocated space directly after this partition." : hasFs ? null : "Only partitions with a file system can be extended.")));
        Actions.Add(new ActionItem(PartitionAction.Letter, "Change drive letter…", Explanations.Letter, Reason(true) is null, Reason(true)));
        Actions.Add(new ActionItem(PartitionAction.Label, "Rename…", Explanations.Label, Reason(false) is null && hasFs, Reason(false)));
        Actions.Add(new ActionItem(PartitionAction.Format, "Format (erase)…", Explanations.Format, Reason(true) is null, Reason(true)));
        Actions.Add(new ActionItem(PartitionAction.Delete, "Delete partition…", Explanations.Delete, Reason(true) is null, Reason(true)));
    }

    [RelayCommand]
    private async Task Run(ActionItem? action)
    {
        if (action is null || !action.Enabled || Selected is null || ShowAction is null) return;
        var vm = new PartitionActionViewModel(action.Action, Selected.Disk, Selected.Partition, Selected.Gap, Services.Settings.AllowSystemPartitions);
        if (await ShowAction(vm)) await LoadAsync();
    }

    [RelayCommand]
    private async Task Clone()
    {
        if (ShowClone is not null) await ShowClone(Selected?.Disk);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task WindowsCopy()
    {
        if (ShowWindowsCopy is not null) await ShowWindowsCopy();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task BootHealth()
    {
        if (ShowBootHealth is not null) await ShowBootHealth();
    }

    public override void Dispose()
    {
        Services.Drives.Changed -= OnDrivesChanged;
        base.Dispose();
    }
}
