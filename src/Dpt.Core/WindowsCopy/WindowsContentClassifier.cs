namespace Dpt.Core.WindowsCopy;

public enum ContentGroup
{
    /// <summary>Windows itself: needed to start (always copied).</summary>
    Windows,

    /// <summary>Installed programs: Program Files, ProgramData (except Windows' own).</summary>
    Programs,

    /// <summary>Per-user app settings (AppData) — one entry per user.</summary>
    AccountSettings,

    /// <summary>Per-user personal folders (Documents, Pictures, …) — one entry per user and folder.</summary>
    PersonalFolder,

    /// <summary>Anything else at the top of the drive.</summary>
    Other,

    /// <summary>Never copied (page/hibernation files, Recycle Bin, restore points).</summary>
    AlwaysExcluded,
}

/// <summary>One choosable piece of a Windows drive, e.g. "Alice · Pictures". <see cref="RelativePath"/> is from the drive root.</summary>
public sealed record ContentItem(ContentGroup Group, string RelativePath, string Title, string? User, bool Required);
public sealed record InstalledProgram(string Name, string RelativePath);
public sealed record PersonalLocation(string User, string RelativePath, string Title);

/// <summary>
/// Sorts a Windows drive's top-level content into plain-language groups for the "copy Windows" options. Works from a
/// directory listing (no file system access here), so it's unit-testable.
/// </summary>
public static class WindowsContentClassifier
{
    public static readonly string[] AlwaysExcludedNames =
    [
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "DumpStack.log", "System Volume Information", "$Recycle.Bin",
        "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Recovery",
    ];

    /// <summary>Profile folders that are personal content (shown separately per user).</summary>
    public static readonly string[] PersonalFolders = ["Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "Favorites", "Contacts", "Saved Games", "OneDrive", "Links", "Searches"];

    private static readonly HashSet<string> SystemProfiles = new(StringComparer.OrdinalIgnoreCase) { "Default", "Default User", "All Users", "defaultuser0", "WDAGUtilityAccount" };

    public static bool IsSystemProfile(string name) => SystemProfiles.Contains(name);

    /// <param name="rootEntries">Names of entries at the drive root (folders and files).</param>
    /// <param name="listDirectory">Lists immediate entry names at a relative directory. Unreadable branches must be reported by the caller.</param>
    /// <param name="programs">Installed-program locations relative to this Windows volume.</param>
    public static IReadOnlyList<ContentItem> Classify(IEnumerable<string> rootEntries, Func<string, IEnumerable<string>> listDirectory, IEnumerable<InstalledProgram>? programs = null, IEnumerable<PersonalLocation>? personalLocations = null)
    {
        var items = new List<ContentItem>();
        var installed = (programs ?? []).Select(p => p with { RelativePath = WindowsCopyPlanning.NormalizeRelativePath(p.RelativePath) }).ToList();
        var personalFolders = (personalLocations ?? []).Select(p => p with { RelativePath = WindowsCopyPlanning.NormalizeRelativePath(p.RelativePath) }).ToList();
        void Add(ContentGroup group, string path, string title, string? user = null, bool required = false) =>
            items.Add(new(group, path, title, user, required));
        void Optional(string path, ContentGroup fallback, string? user = null)
        {
            var program = installed.FirstOrDefault(p => p.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (program is not null) { Add(ContentGroup.Programs, path, program.Name, user); return; }
            var personal = personalFolders.FirstOrDefault(p => p.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (personal is not null && !installed.Any(p => WindowsCopyPlanning.Under(p.RelativePath, path)))
            { Add(ContentGroup.PersonalFolder, path, personal.Title, personal.User); return; }
            if (personal is not null) { fallback = ContentGroup.PersonalFolder; user = personal.User; }
            if (installed.Any(p => WindowsCopyPlanning.Under(p.RelativePath, path)) || personalFolders.Any(p => !p.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) && WindowsCopyPlanning.Under(p.RelativePath, path)))
            {
                foreach (var entry in listDirectory(path)) Optional(path + "\\" + entry, fallback, user);
                return;
            }
            Add(fallback, path, user is null ? path : $"{user} · {path[(path.LastIndexOf('\\') + 1)..]}", user);
        }
        foreach (var name in rootEntries)
        {
            WindowsCopyPlanning.NormalizeRelativePath(name);
            if (AlwaysExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ContentItem(ContentGroup.AlwaysExcluded, name, name, null, false));
                continue;
            }

            switch (name.ToLowerInvariant())
            {
                case "windows":
                    items.Add(new ContentItem(ContentGroup.Windows, name, "Windows itself", null, true));
                    break;
                case "program files" or "program files (x86)":
                    foreach (var entry in listDirectory(name))
                    {
                        var path = name + "\\" + entry;
                        // These shared components are used by Windows and by several programs.
                        if (entry.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) || entry.Equals("Common Files", StringComparison.OrdinalIgnoreCase))
                            Add(ContentGroup.Windows, path, path + " (shared components)", required: true);
                        else Optional(path, ContentGroup.Programs);
                    }
                    break;
                case "programdata":
                    foreach (var entry in listDirectory(name))
                        if (entry.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) Add(ContentGroup.Windows, name + "\\" + entry, "Windows shared settings", required: true);
                        else Optional(name + "\\" + entry, ContentGroup.Programs);
                    break;
                case "boot" or "bootmgr" or "bootnxt" or "bootsect.bak":
                    items.Add(new ContentItem(ContentGroup.Windows, name, "Boot files", null, true));
                    break;
                case "users":
                    foreach (var user in listDirectory(name))
                    {
                        var userPath = Path.Combine(name, user);
                        if (SystemProfiles.Contains(user))
                        {
                            items.Add(new ContentItem(ContentGroup.Windows, userPath, $"Windows profile \"{user}\"", null, true));
                            continue;
                        }

                        foreach (var entry in listDirectory(userPath))
                        {
                            var rel = Path.Combine(userPath, entry);
                            if (PersonalFolders.Contains(entry, StringComparer.OrdinalIgnoreCase))
                                Optional(rel, ContentGroup.PersonalFolder, user);
                            else if (string.Equals(entry, "AppData", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var branch in listDirectory(rel))
                                {
                                    var branchPath = rel + "\\" + branch;
                                    if (branch.Equals("Local", StringComparison.OrdinalIgnoreCase) || branch.Equals("LocalLow", StringComparison.OrdinalIgnoreCase) || branch.Equals("Roaming", StringComparison.OrdinalIgnoreCase))
                                        foreach (var app in listDirectory(branchPath))
                                        {
                                            var appPath = branchPath + "\\" + app;
                                            if (app.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) Add(ContentGroup.Windows, appPath, $"{user} · Windows account settings ({branch})", user, true);
                                            else Optional(appPath, ContentGroup.AccountSettings, user);
                                        }
                                    else Optional(branchPath, ContentGroup.AccountSettings, user);
                                }
                            }
                            else if (entry.StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase))
                                items.Add(new ContentItem(ContentGroup.Windows, rel, $"{user} · account (sign-in) data", user, true));
                            else
                                Optional(rel, ContentGroup.PersonalFolder, user);
                        }
                    }

                    break;
                default:
                    Optional(name, ContentGroup.Other);
                    break;
            }
        }

        return items.OrderBy(i => i.Group).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Plain-language consequence of leaving a group out (neutral, no recommendation).</summary>
    public static string Consequence(ContentGroup group) => group switch
    {
        ContentGroup.Windows => "Needed for Windows to start; always copied.",
        ContentGroup.Programs => "If left out, programs stay listed in Settings but won't open until they're reinstalled.",
        ContentGroup.AccountSettings => "If left out, these app settings and saved logins stay only on the source. Windows account files are always kept.",
        ContentGroup.PersonalFolder => "If left out, the folder is empty on the copy. The original stays on the current drive.",
        ContentGroup.Other => "If left out, it isn't on the copy. The original stays on the current drive.",
        _ => "Never copied: Windows recreates these automatically.",
    };

    /// <summary>Relative paths to leave out of the copy: excluded choices plus the always-excluded items.</summary>
    public static IReadOnlyList<string> Exclusions(IEnumerable<ContentItem> items, IReadOnlySet<string> chosen) =>
        WindowsCopyPlanning.ValidateExclusions(items.Where(i => i.Group == ContentGroup.AlwaysExcluded || (!i.Required && !chosen.Contains(i.RelativePath)))
            .Select(i => i.RelativePath));
}
