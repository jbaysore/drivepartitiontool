namespace Dpt.App.Services;

/// <summary>Service-upgrade payload copying. Preflights every existing destination before replacing any file.</summary>
public static class PackageFiles
{
    public static void CopyDirectory(string source, string destination)
    {
        source = Path.GetFullPath(source).TrimEnd('\\'); destination = Path.GetFullPath(destination).TrimEnd('\\');
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
        if (destination.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("The install folder cannot be inside the package being copied.");
        var files = new List<string>();
        void Discover(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Install packages cannot contain directory links or reparse points.");
                if (entry is DirectoryInfo directory) Discover(directory.FullName);
                else files.Add(entry.FullName);
            }
        }
        Discover(source);
        foreach (var file in files)
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(target))
                try { using var check = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new IOException("Close the installed app before upgrading. The file cannot be replaced: " + target, ex); }
        }
        Directory.CreateDirectory(destination);
        foreach (var file in files)
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true); // An unexpected failure is reported, never silently treated as an upgrade.
        }
    }
}
