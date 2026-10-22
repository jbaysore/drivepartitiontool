namespace Dpt.Storage;

public enum PartitionRole
{
    Data,
    Windows,
    Efi,
    Recovery,
    Reserved,
    Unknown,
}

/// <summary>A partition (with its volume, if mounted) on a physical disk.</summary>
public sealed record PartitionInfo
{
    public int DiskNumber { get; init; }
    public int PartitionNumber { get; init; }
    public long Offset { get; init; }
    public long Size { get; init; }
    public char? DriveLetter { get; init; }
    public string? GptType { get; init; }
    public int? MbrType { get; init; }
    public bool IsActive { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsHidden { get; init; }
    public string? VolumePath { get; init; }
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long? VolumeSize { get; init; }
    public long? FreeSpace { get; init; }
    public long? RecycleBinLimit { get; init; }
    public PartitionRole Role { get; init; }

    public string? Root => DriveLetter is { } l ? $"{l}:\\" : null;
    public long? Used => VolumeSize is { } s && FreeSpace is { } f ? s - f : null;

    /// <summary>Plain-language role tags shown on the Drives page.</summary>
    public IReadOnlyList<string> RoleTags => Role switch
    {
        PartitionRole.Windows => ["Windows is here"],
        PartitionRole.Efi => ["Starts your PC (EFI)"],
        PartitionRole.Recovery => ["Recovery"],
        PartitionRole.Reserved => ["Reserved"],
        _ when IsActive => ["Starts your PC (active)"],
        _ => [],
    };

    public bool IsProtected => Role is PartitionRole.Windows or PartitionRole.Efi or PartitionRole.Recovery or PartitionRole.Reserved || IsBoot || IsSystem;
}

/// <summary>A gap on a disk that no partition uses.</summary>
public sealed record UnallocatedSpace(int DiskNumber, long Offset, long Size);

public sealed record DiskHealth(bool Available, int? TemperatureC, int? WearPercent, long? PowerOnHours, string? Note);

/// <summary>A physical (or virtual) disk with its partitions.</summary>
public sealed record DiskInfo
{
    public int Number { get; init; }
    public string FriendlyName { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string BusType { get; init; } = "";
    public string MediaType { get; init; } = "";
    public long Size { get; init; }
    public string PartitionStyle { get; init; } = "";
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public string HealthStatus { get; init; } = "";
    public string UniqueId { get; init; } = "";
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = [];
    public IReadOnlyList<UnallocatedSpace> Unallocated { get; init; } = [];
    public DiskHealth? Health { get; init; }

    public bool IsVirtual => BusType.Equals("File Backed Virtual", StringComparison.OrdinalIgnoreCase);
    public bool IsUsb => BusType.Equals("USB", StringComparison.OrdinalIgnoreCase);
    public bool HoldsWindows => Partitions.Any(p => p.Role == PartitionRole.Windows);
    public long Unused => Unallocated.Sum(u => u.Size);
}
