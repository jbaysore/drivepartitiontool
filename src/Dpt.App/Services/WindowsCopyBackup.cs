using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Dpt.Storage;
using Microsoft.Win32.SafeHandles;

namespace Dpt.App.Services;

/// <summary>Resolve folder junctions/mount points before checking that a backup is really on a third disk.</summary>
public static class WindowsCopyBackup
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFolder(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FinalName(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);

    public static string ResolveFolder(string folder)
    {
        using var handle = OpenFolder(folder, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the backup folder.");
        var buffer = new StringBuilder(32768);
        var length = FinalName(handle, buffer, (uint)buffer.Capacity, 1); // VOLUME_NAME_GUID; follows ancestor junctions too.
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Choose a folder on an identifiable local drive.");
        return buffer.ToString();
    }

    public static string? Problem(string resolvedFolder, IReadOnlyList<DiskInfo> disks, int sourceNumber, int destinationNumber)
    {
        var disk = disks.FirstOrDefault(d => d.Partitions.Any(p => p.VolumePath is { Length: > 0 } volume &&
            (resolvedFolder.TrimEnd('\\').Equals(volume.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || resolvedFolder.StartsWith(volume.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))));
        return disk is null ? "Choose a folder on a connected local drive whose volume can be identified."
            : disk.Number == sourceNumber || disk.Number == destinationNumber ? "Choose another physical drive. The source is kept unchanged, and the Windows-copy destination will be erased."
            : disk.IsReadOnly || disk.IsOffline ? "The backup drive is not writable."
            : null;
    }
}
