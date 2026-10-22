using System.Runtime.InteropServices;

namespace Dpt.Shell;

/// <summary>Resolves Windows known folders (Downloads, Documents, …) and "shell:" names.</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string Downloads => GetKnownFolder(DownloadsId) ?? Path.Combine(UserProfile, "Downloads");
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The standard folders offered as default pins on first run.</summary>
    public static IReadOnlyList<string> DefaultPins => new[] { Desktop, Downloads, Documents, Pictures, Music, Videos }
        .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).ToList();

    public static string? GetKnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Resolves names used after "shell:", e.g. "downloads", "documents", "appdata", "startup".</summary>
    public static string? ResolveShellName(string name)
    {
        Environment.SpecialFolder? sf = name.ToLowerInvariant() switch
        {
            "desktop" => Environment.SpecialFolder.DesktopDirectory,
            "documents" or "personal" or "mydocuments" => Environment.SpecialFolder.MyDocuments,
            "pictures" or "my pictures" => Environment.SpecialFolder.MyPictures,
            "music" or "my music" => Environment.SpecialFolder.MyMusic,
            "videos" or "my video" => Environment.SpecialFolder.MyVideos,
            "profile" or "userprofile" => Environment.SpecialFolder.UserProfile,
            "appdata" => Environment.SpecialFolder.ApplicationData,
            "local appdata" or "localappdata" => Environment.SpecialFolder.LocalApplicationData,
            "programfiles" => Environment.SpecialFolder.ProgramFiles,
            "programfilesx86" => Environment.SpecialFolder.ProgramFilesX86,
            "windows" => Environment.SpecialFolder.Windows,
            "system" => Environment.SpecialFolder.System,
            "startup" => Environment.SpecialFolder.Startup,
            "common startup" => Environment.SpecialFolder.CommonStartup,
            "sendto" => Environment.SpecialFolder.SendTo,
            "recent" => Environment.SpecialFolder.Recent,
            "fonts" => Environment.SpecialFolder.Fonts,
            "templates" => Environment.SpecialFolder.Templates,
            "start menu" => Environment.SpecialFolder.StartMenu,
            "programs" => Environment.SpecialFolder.Programs,
            "common appdata" => Environment.SpecialFolder.CommonApplicationData,
            _ => null,
        };
        if (sf is { } folder)
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        return name.Equals("downloads", StringComparison.OrdinalIgnoreCase) ? Downloads : null;
    }
}
