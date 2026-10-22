using Microsoft.Management.Infrastructure;

namespace Dpt.Storage;

/// <summary>
/// Read-only view of disks, partitions and volumes through the Windows Storage Management API
/// (WMI namespace root\Microsoft\Windows\Storage). Works without administrator rights, except reliability counters.
/// </summary>
public static class StorageInventory
{
    private const string Ns = @"root\Microsoft\Windows\Storage";

    public const string GptBasicData = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    public const string GptEfi = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    public const string GptMsr = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    public const string GptRecovery = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";

    /// <summary>Gaps smaller than this (alignment, GPT backup header) are not shown as unallocated space.</summary>
    private const long MinimumGap = 16L * 1024 * 1024;

    public static IReadOnlyList<DiskInfo> GetDisks(bool includeHealth = false)
    {
        using var session = CimSession.Create(null);
        var physical = session.QueryInstances(Ns, "WQL", "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk")
            .ToDictionary(p => Str(p, "DeviceId") ?? "", p => MediaTypeName(U16(p, "MediaType")));
        var volumes = session.QueryInstances(Ns, "WQL", "SELECT Path, DriveLetter, FileSystem, FileSystemLabel, Size, SizeRemaining FROM MSFT_Volume")
            .Where(v => Str(v, "Path") is not null)
            .ToDictionary(v => Str(v, "Path")!, StringComparer.OrdinalIgnoreCase);
        var partitions = session.QueryInstances(Ns, "WQL", "SELECT * FROM MSFT_Partition").ToList();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?[0];

        var disks = new List<DiskInfo>();
        foreach (var d in session.QueryInstances(Ns, "WQL", "SELECT * FROM MSFT_Disk"))
        {
            var number = (int)U32(d, "Number");
            var parts = partitions.Where(p => U32(p, "DiskNumber") == number).Select(p => ToPartition(p, volumes, systemRoot))
                .OrderBy(p => p.Offset).ToList();
            var size = (long)U64(d, "Size");
            disks.Add(new DiskInfo
            {
                Number = number,
                FriendlyName = Str(d, "FriendlyName") ?? Str(d, "Model") ?? $"Disk {number}",
                SerialNumber = (Str(d, "SerialNumber") ?? "").Trim(),
                BusType = BusTypeName(U16(d, "BusType")),
                MediaType = physical.GetValueOrDefault(number.ToString(), ""),
                Size = size,
                PartitionStyle = U16(d, "PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => "Not initialized" },
                IsBoot = Bool(d, "IsBoot"),
                IsSystem = Bool(d, "IsSystem"),
                IsOffline = Bool(d, "IsOffline"),
                IsReadOnly = Bool(d, "IsReadOnly"),
                HealthStatus = U16(d, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                UniqueId = Str(d, "UniqueId") ?? "",
                Partitions = parts,
                Unallocated = Gaps(number, size, parts),
                Health = includeHealth ? TryHealth(session, number) : null,
            });
        }

        return disks.OrderBy(d => d.Number).ToList();
    }

    private static PartitionInfo ToPartition(CimInstance p, Dictionary<string, CimInstance> volumes, char? systemRoot)
    {
        var letter = p.CimInstanceProperties["DriveLetter"]?.Value is char c && c != '\0' ? char.ToUpperInvariant(c) : (char?)null;
        var gpt = Str(p, "GptType");
        int? mbr = p.CimInstanceProperties["MbrType"]?.Value is ushort m && m != 0 ? m : null;
        var paths = p.CimInstanceProperties["AccessPaths"]?.Value as string[] ?? [];
        CimInstance? vol = null;
        foreach (var ap in paths)
            if (volumes.TryGetValue(ap, out var v)) { vol = v; break; }

        var role = PartitionRole.Data;
        if (string.Equals(gpt, GptEfi, StringComparison.OrdinalIgnoreCase) || mbr == 0xEF) role = PartitionRole.Efi;
        else if (string.Equals(gpt, GptMsr, StringComparison.OrdinalIgnoreCase)) role = PartitionRole.Reserved;
        else if (string.Equals(gpt, GptRecovery, StringComparison.OrdinalIgnoreCase) || mbr == 0x27) role = PartitionRole.Recovery;
        else if (letter is not null && letter == systemRoot) role = PartitionRole.Windows;
        else if (gpt is not null && !string.Equals(gpt, GptBasicData, StringComparison.OrdinalIgnoreCase)) role = PartitionRole.Unknown;

        var root = letter is { } l ? $"{l}:\\" : null;
        return new PartitionInfo
        {
            DiskNumber = (int)U32(p, "DiskNumber"),
            PartitionNumber = (int)U32(p, "PartitionNumber"),
            Offset = (long)U64(p, "Offset"),
            Size = (long)U64(p, "Size"),
            DriveLetter = letter,
            GptType = gpt,
            MbrType = mbr,
            IsActive = Bool(p, "IsActive"),
            IsBoot = Bool(p, "IsBoot"),
            IsSystem = Bool(p, "IsSystem"),
            IsHidden = Bool(p, "IsHidden"),
            VolumePath = vol is null ? null : Str(vol, "Path"),
            Label = vol is null ? null : Str(vol, "FileSystemLabel"),
            FileSystem = vol is null ? null : Str(vol, "FileSystem"),
            VolumeSize = vol is null ? null : (long)U64(vol, "Size"),
            FreeSpace = vol is null ? null : (long)U64(vol, "SizeRemaining"),
            RecycleBinLimit = root is null ? null : SafeRecycleLimit(root),
            Role = role,
        };
    }

    private static long? SafeRecycleLimit(string root)
    {
        try { return Shell.Operations.RecycleBin.MaxCapacityBytes(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Unused ranges between partitions (and at the end of the disk).</summary>
    public static IReadOnlyList<UnallocatedSpace> Gaps(int disk, long diskSize, IReadOnlyList<PartitionInfo> parts)
    {
        var gaps = new List<UnallocatedSpace>();
        long cursor = 1024 * 1024; // first MiB holds the partition table
        foreach (var p in parts.OrderBy(p => p.Offset))
        {
            if (p.Offset - cursor >= MinimumGap) gaps.Add(new UnallocatedSpace(disk, cursor, p.Offset - cursor));
            cursor = Math.Max(cursor, p.Offset + p.Size);
        }

        if (diskSize - cursor >= MinimumGap) gaps.Add(new UnallocatedSpace(disk, cursor, diskSize - cursor));
        return gaps;
    }

    /// <summary>Temperature/wear need administrator rights; returns Available=false otherwise.</summary>
    public static DiskHealth TryHealth(CimSession session, int diskNumber)
    {
        try
        {
            var pd = session.QueryInstances(Ns, "WQL", $"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'").FirstOrDefault();
            if (pd is null) return new DiskHealth(false, null, null, null, "No physical disk information.");
            var rc = session.EnumerateAssociatedInstances(Ns, pd, "MSFT_PhysicalDiskToStorageReliabilityCounter", "MSFT_StorageReliabilityCounter", "PhysicalDisk", "StorageReliabilityCounter").FirstOrDefault();
            if (rc is null) return new DiskHealth(false, null, null, null, "This drive doesn't report health details.");
            int? temp = rc.CimInstanceProperties["Temperature"]?.Value is ushort t && t > 0 ? t : null;
            int? wear = rc.CimInstanceProperties["Wear"]?.Value is byte w ? w : null;
            long? hours = rc.CimInstanceProperties["PowerOnHours"]?.Value is uint h ? h : null;
            return new DiskHealth(true, temp, wear, hours, null);
        }
        catch (CimException)
        {
            return new DiskHealth(false, null, null, null, "Health details need the search service or administrator rights.");
        }
    }

    public static IReadOnlyList<DiskHealth> TryHealthAll(IEnumerable<int> disks)
    {
        using var session = CimSession.Create(null);
        return disks.Select(d => TryHealth(session, d)).ToList();
    }

    public static string BusTypeName(ushort v) => v switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File Backed Virtual",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "Unknown",
    };

    public static string MediaTypeName(ushort v) => v switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };

    private static string? Str(CimInstance i, string name) => i.CimInstanceProperties[name]?.Value as string;
    private static ushort U16(CimInstance i, string name) => i.CimInstanceProperties[name]?.Value is ushort v ? v : (ushort)0;
    private static uint U32(CimInstance i, string name) => Convert.ToUInt32(i.CimInstanceProperties[name]?.Value ?? 0u);
    private static ulong U64(CimInstance i, string name) => Convert.ToUInt64(i.CimInstanceProperties[name]?.Value ?? 0UL);
    private static bool Bool(CimInstance i, string name) => i.CimInstanceProperties[name]?.Value is true;
}
