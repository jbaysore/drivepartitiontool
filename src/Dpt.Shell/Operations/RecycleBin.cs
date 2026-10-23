using Microsoft.Win32;

namespace Dpt.Shell.Operations;

/// <summary>Recycle Bin helpers: size pre-checks and restoring items the app recycled.</summary>
public static class RecycleBin
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, System.Text.StringBuilder lpszVolumeName, int cchBufferLength);

    /// <summary>
    /// The configured maximum Recycle Bin size for the volume holding <paramref name="path"/>, in bytes, or null when unknown.
    /// Returns 0 when the volume is set to delete immediately ("Don't move files to the Recycle Bin").
    /// </summary>
    public static long? MaxCapacityBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return null;
        var sb = new System.Text.StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, sb, sb.Capacity)) return null;
        var volume = sb.ToString(); // \\?\Volume{GUID}\
        var start = volume.IndexOf('{');
        var end = volume.IndexOf('}');
        if (start < 0 || end < start) return null;
        var guid = volume[start..(end + 1)];
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");
        if (key is null) return null;
        if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0) return 0;
        return key.GetValue("MaxCapacity") is int mb ? mb * 1024L * 1024L : null;
    }

    /// <summary>
    /// The Recycle Bin limit Windows will apply to this item's drive: the configured size, 0 when the drive deletes
    /// immediately, or (when no size is configured) a cautious estimate of Windows' default (5 % of the drive).
    /// </summary>
    public static long? EffectiveCapacityBytes(string path)
    {
        if (MaxCapacityBytes(path) is { } configured) return configured;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).TotalSize / 20;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Total size of a file, or of everything inside a folder.</summary>
    public static long? SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path)) return FileSystem.FolderSizeCalculator.Compute(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Why an item can't go to the Recycle Bin, or null if it can. The engine applies this before handing anything to
    /// Windows, because Windows' "recycle if possible" mode permanently deletes items that are too big for the bin.
    /// </summary>
    public static string? PrecheckReason(string path, long? sizeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return null;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "Network locations don't have a Recycle Bin.";
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network) return "Network drives don't have a Recycle Bin.";
            var max = EffectiveCapacityBytes(path);
            if (max == 0) return "This drive is set to delete items immediately instead of using the Recycle Bin.";
            if (max is { } m && sizeBytes is { } s && s > m)
                return $"It is larger than this drive's Recycle Bin limit ({Core.Formatting.SizeFormatter.Format(m)}).";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Restores an item the app recycled. <paramref name="recycledPath"/> is the "$R…" path reported by the shell when it was recycled.
    /// Mirrors what Explorer's Restore does: move $R back to its original path and remove the matching $I metadata file.
    /// </summary>
    public static bool Restore(string recycledPath, string originalPath)
    {
        var isDir = Directory.Exists(recycledPath);
        if (!isDir && !File.Exists(recycledPath)) return false;
        if (File.Exists(originalPath) || Directory.Exists(originalPath)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        if (isDir) Directory.Move(recycledPath, originalPath);
        else File.Move(recycledPath, originalPath);

        var name = Path.GetFileName(recycledPath);
        if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            var info = Path.Combine(Path.GetDirectoryName(recycledPath)!, "$I" + name[2..]);
            try { File.Delete(info); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return true;
    }
}
