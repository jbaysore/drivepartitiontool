using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dpt.Shell.Interop;

namespace Dpt.Shell;

/// <summary>File type names and icons from the shell, cached by extension so large folders stay fast.</summary>
public static class ShellInfo
{
    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    // Extensions whose icon differs per file, so they can't be cached by extension.
    private static readonly HashSet<string> PerFileIcon = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".appref-ms" };

    public static string TypeName(string path, bool isFolder)
    {
        if (isFolder) return "File folder";
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return "File";
        return TypeNames.GetOrAdd(ext, e =>
        {
            var info = new SHFILEINFO();
            NativeMethods.SHGetFileInfo(e, NativeMethods.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                NativeMethods.SHGFI_TYPENAME | NativeMethods.SHGFI_USEFILEATTRIBUTES);
            return string.IsNullOrEmpty(info.szTypeName) ? $"{e.TrimStart('.').ToUpperInvariant()} File" : info.szTypeName;
        });
    }

    /// <summary>Small (16px) icon. Must be called on a thread that can create frozen WPF images (any thread is fine).</summary>
    public static ImageSource? SmallIcon(string path, bool isFolder, bool isDriveRoot = false)
    {
        var ext = Path.GetExtension(path);
        var perFile = isDriveRoot || (!isFolder && PerFileIcon.Contains(ext));
        var key = isDriveRoot ? path : isFolder ? "<folder>" : string.IsNullOrEmpty(ext) ? "<file>" : ext;
        if (!perFile && Icons.TryGetValue(key, out var cached)) return cached;

        var info = new SHFILEINFO();
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        uint attrs = isFolder ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        var query = path;
        if (!perFile)
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            query = isFolder ? "folder" : string.IsNullOrEmpty(ext) ? "file" : "x" + ext;
        }

        NativeMethods.SHGetFileInfo(query, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        ImageSource? image = null;
        if (info.hIcon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                image = bmp;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }

        if (!perFile) Icons[key] = image;
        return image;
    }

    /// <summary>Thumbnail (or large icon when no thumbnail exists) via IShellItemImageFactory. Call from an STA or MTA background thread.</summary>
    public static ImageSource? Thumbnail(string path, int size, bool thumbnailOnly = false)
    {
        try
        {
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, NativeMethods.IID_IShellItemImageFactory, out var obj);
            var factory = (IShellItemImageFactory)obj;
            try
            {
                var flags = SIIGBF.BIGGERSIZEOK | (thumbnailOnly ? SIIGBF.THUMBNAILONLY : SIIGBF.RESIZETOFIT);
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
                finally
                {
                    NativeMethods.DeleteObject(hbm);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
