using System.Runtime.InteropServices;
using Microsoft.Management.Infrastructure;

namespace Dpt.Storage;

/// <summary>
/// Changes disks through the Windows Storage Management API. Only the elevated disk worker calls this, and only after
/// the plan's safety checks pass (virtual-only mode, fingerprint, protection, confirmation).
/// </summary>
public sealed class StorageOperations : IDisposable
{
    private const string Ns = @"root\Microsoft\Windows\Storage";
    private readonly CimSession _session = CimSession.Create(null);

    public sealed class StorageOperationException(string message) : Exception(message);

    private CimInstance Disk(int number) =>
        _session.QueryInstances(Ns, "WQL", $"SELECT * FROM MSFT_Disk WHERE Number = {number}").FirstOrDefault()
        ?? throw new StorageOperationException($"Disk {number} was not found.");

    private CimInstance Partition(int disk, int partition) =>
        _session.QueryInstances(Ns, "WQL", $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk} AND PartitionNumber = {partition}").FirstOrDefault()
        ?? throw new StorageOperationException($"Partition {partition} on disk {disk} was not found.");

    private CimInstance? VolumeOf(CimInstance partition) =>
        _session.EnumerateAssociatedInstances(Ns, partition, "MSFT_PartitionToVolume", "MSFT_Volume", "Partition", "Volume").FirstOrDefault();

    private CimMethodResult Invoke(CimInstance target, string method, CimMethodParametersCollection? args = null)
    {
        var result = _session.InvokeMethod(Ns, target, method, args ?? new CimMethodParametersCollection());
        var code = Convert.ToUInt32(result.ReturnValue.Value);
        if (code != 0)
        {
            var status = result.OutParameters["ExtendedStatus"]?.Value as CimInstance;
            var message = status?.CimInstanceProperties["Message"]?.Value as string;
            throw new StorageOperationException($"{method} failed (code {code}){(message is null ? "" : ": " + message)}");
        }

        return result;
    }

    private static CimMethodParameter P(string name, object? value, CimType type) => CimMethodParameter.Create(name, value, type, CimFlags.In);

    public void InitializeDisk(int disk, string style)
    {
        var args = new CimMethodParametersCollection { P("PartitionStyle", (ushort)(style.Equals("MBR", StringComparison.OrdinalIgnoreCase) ? 1 : 2), CimType.UInt16) };
        Invoke(Disk(disk), "Initialize", args);
    }

    /// <summary>Creates a partition (in the given gap, or the largest free space) and optionally formats it. Returns the partition number.</summary>
    public int CreatePartition(int disk, long? offset, long? size, char? letter, bool autoLetter)
    {
        var args = new CimMethodParametersCollection();
        if (size is { } s) args.Add(P("Size", (ulong)s, CimType.UInt64));
        else args.Add(P("UseMaximumSize", true, CimType.Boolean));
        if (offset is { } o) args.Add(P("Offset", (ulong)o, CimType.UInt64));
        if (letter is { } l) args.Add(P("DriveLetter", l, CimType.Char16));
        else if (autoLetter) args.Add(P("AssignDriveLetter", true, CimType.Boolean));
        var r = Invoke(Disk(disk), "CreatePartition", args);
        var created = r.OutParameters["CreatedPartition"]?.Value as CimInstance
                      ?? throw new StorageOperationException("Windows didn't report the new partition.");
        return Convert.ToInt32(created.CimInstanceProperties["PartitionNumber"].Value);
    }

    public void DeletePartition(int disk, int partition) => Invoke(Partition(disk, partition), "DeleteObject");

    public (long Min, long Max) SupportedSize(int disk, int partition)
    {
        var r = Invoke(Partition(disk, partition), "GetSupportedSize");
        return ((long)Convert.ToUInt64(r.OutParameters["SizeMin"].Value), (long)Convert.ToUInt64(r.OutParameters["SizeMax"].Value));
    }

    public void ResizePartition(int disk, int partition, long newSize)
    {
        var args = new CimMethodParametersCollection { P("Size", (ulong)newSize, CimType.UInt64) };
        Invoke(Partition(disk, partition), "Resize", args);
    }

    public void Format(int disk, int partition, string fileSystem, string label)
    {
        var part = Partition(disk, partition);
        var vol = VolumeOf(part) ?? throw new StorageOperationException("The partition has no volume to format.");
        var args = new CimMethodParametersCollection
        {
            P("FileSystem", fileSystem, CimType.String),
            P("FileSystemLabel", label, CimType.String),
            P("Full", false, CimType.Boolean),
            P("Force", true, CimType.Boolean),
        };
        Invoke(vol, "Format", args);
    }

    /// <summary>Assigns a letter (removing any existing one first), or removes the letter when <paramref name="letter"/> is null.</summary>
    public void SetDriveLetter(int disk, int partition, char? letter)
    {
        var part = Partition(disk, partition);
        if (part.CimInstanceProperties["DriveLetter"]?.Value is char current && current != '\0')
        {
            Invoke(part, "RemoveAccessPath", new CimMethodParametersCollection { P("AccessPath", $"{current}:\\", CimType.String) });
            part = Partition(disk, partition);
        }

        if (letter is { } l)
            Invoke(part, "AddAccessPath", new CimMethodParametersCollection { P("AccessPath", $"{char.ToUpperInvariant(l)}:\\", CimType.String) });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetVolumeLabel(string root, string label);

    public void SetLabel(int disk, int partition, string label)
    {
        var part = Partition(disk, partition);
        var paths = part.CimInstanceProperties["AccessPaths"]?.Value as string[] ?? [];
        var root = paths.FirstOrDefault(p => p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase)) ?? paths.FirstOrDefault()
                   ?? throw new StorageOperationException("The partition has no volume to rename.");
        if (!SetVolumeLabel(root, label)) throw new StorageOperationException($"Couldn't set the label (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Takes a disk offline/online (used while cloning so nothing else writes to the destination).</summary>
    public void SetOffline(int disk, bool offline) => Invoke(Disk(disk), offline ? "Offline" : "Online");

    public void Dispose() => _session.Dispose();
}
