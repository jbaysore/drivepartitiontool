using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dpt.Shell.FileSystem;

/// <summary>A file's stable identity on its volume (survives renames and moves within the volume).</summary>
public readonly record struct FileId(uint VolumeSerial, ulong FileIndex, long CreatedTicks = 0);

/// <summary>Gets a file's ID from its path, and finds its current path from the ID (no admin rights needed).</summary>
public static class FileIdentity
{
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint SHARE_ALL = 7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public uint Type;
        public long FileId;
        public long Padding;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle hint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sec, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize, out uint serial, out uint maxComp, out uint flags, StringBuilder? fs, int fsSize);

    public static FileId? TryGet(string path)
    {
        using var h = CreateFile(path, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return new FileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.CreationTime);
    }

    /// <summary>Current full path of a file ID, or null if it no longer exists (or the volume isn't mounted).</summary>
    public static string? TryResolve(FileId id)
    {
        foreach (var root in DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name))
        {
            if (!GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0) || serial != id.VolumeSerial) continue;
            using var hint = CreateFile(root, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (hint.IsInvalid) continue;
            var desc = new FILE_ID_DESCRIPTOR { dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = 0, FileId = (long)id.FileIndex };
            using var h = OpenFileById(hint, ref desc, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
            if (h.IsInvalid) continue;
            var sb = new StringBuilder(1024);
            var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n > sb.Capacity) continue;
            var p = sb.ToString();
            if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p[8..];
            return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
        }

        return null;
    }
}
