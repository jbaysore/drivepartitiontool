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

    private static readonly HashSet<string> SystemProfiles = new(StringComparer.OrdinalIgnoreCase) { "Default", "Default User", "Public", "All Users", "defaultuser0", "WDAGUtilityAccount" };

    /// <param name="rootEntries">Names of entries at the drive root (folders and files).</param>
    /// <param name="listProfile">Lists the entries inside Users\{name} (folders and files).</param>
    public static IReadOnlyList<ContentItem> Classify(IEnumerable<string> rootEntries, Func<string, IEnumerable<string>> listProfile, Func<string, IEnumerable<string>> listUsers)
    {
        var items = new List<ContentItem>();
        foreach (var name in rootEntries)
        {
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
                case "program files" or "program files (x86)" or "programdata":
                    items.Add(new ContentItem(ContentGroup.Programs, name, name, null, false));
                    break;
                case "boot" or "bootmgr" or "bootnxt" or "bootsect.bak":
                    items.Add(new ContentItem(ContentGroup.Windows, name, "Boot files", null, true));
                    break;
                case "users":
                    foreach (var user in listUsers(name))
                    {
                        var userPath = Path.Combine(name, user);
                        if (SystemProfiles.Contains(user))
                        {
                            items.Add(new ContentItem(ContentGroup.Windows, userPath, $"Windows profile \"{user}\"", null, true));
                            continue;
                        }

                        foreach (var entry in listProfile(user))
                        {
                            var rel = Path.Combine(userPath, entry);
                            if (PersonalFolders.Contains(entry, StringComparer.OrdinalIgnoreCase))
                                items.Add(new ContentItem(ContentGroup.PersonalFolder, rel, $"{user} · {entry}", user, false));
                            else if (string.Equals(entry, "AppData", StringComparison.OrdinalIgnoreCase))
                                items.Add(new ContentItem(ContentGroup.AccountSettings, rel, $"{user} · app settings (AppData)", user, false));
                            else if (entry.StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("ntuser", StringComparison.Ordinal))
                                items.Add(new ContentItem(ContentGroup.Windows, rel, $"{user} · account (sign-in) data", user, true));
                            else
                                items.Add(new ContentItem(ContentGroup.PersonalFolder, rel, $"{user} · {entry}", user, false));
                        }
                    }

                    break;
                default:
                    items.Add(new ContentItem(ContentGroup.Other, name, name, null, false));
                    break;
            }
        }

        return items;
    }

    /// <summary>Plain-language consequence of leaving a group out (neutral, no recommendation).</summary>
    public static string Consequence(ContentGroup group) => group switch
    {
        ContentGroup.Windows => "Needed for Windows to start; always copied.",
        ContentGroup.Programs => "If left out, programs stay listed in Settings but won't open until they're reinstalled.",
        ContentGroup.AccountSettings => "If left out, the account signs in with default app settings (browsers, saved logins and app preferences start fresh).",
        ContentGroup.PersonalFolder => "If left out, the folder is empty on the copy. The original stays on the current drive.",
        ContentGroup.Other => "If left out, it isn't on the copy. The original stays on the current drive.",
        _ => "Never copied: Windows recreates these automatically.",
    };

    /// <summary>Relative paths to leave out of the copy: excluded choices plus the always-excluded items.</summary>
    public static IReadOnlyList<string> Exclusions(IEnumerable<ContentItem> items, IReadOnlySet<string> chosen) =>
        items.Where(i => i.Group == ContentGroup.AlwaysExcluded || (!i.Required && !chosen.Contains(i.RelativePath)))
            .Select(i => i.RelativePath).ToList();
}
