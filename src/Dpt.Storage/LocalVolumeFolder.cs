using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dpt.Storage;

/// <summary>Resolves ancestor links/mount points to a volume GUID without writing the folder.</summary>
public static class LocalVolumeFolder
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFolder(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FinalName(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);

    public static string Resolve(string folder)
    {
        using var handle = OpenFolder(folder, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the local folder.");
        var buffer = new StringBuilder(32768);
        var length = FinalName(handle, buffer, (uint)buffer.Capacity, 1);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Choose an identifiable local volume.");
        return buffer.ToString();
    }

    public static DiskInfo? DiskOf(string folder, IReadOnlyList<DiskInfo> disks) => disks.FirstOrDefault(d => d.Partitions.Any(p => p.VolumePath is { Length: > 0 } volume &&
        (folder.TrimEnd('\\').Equals(volume.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || folder.StartsWith(volume.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))));
}
