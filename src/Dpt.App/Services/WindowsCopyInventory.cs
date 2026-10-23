using Dpt.Core.WindowsCopy;
using Dpt.Shell;
using Microsoft.Win32;

namespace Dpt.App.Services;

public sealed record ContentEstimate(ContentItem Item, long Bytes, bool Complete);
public sealed record WindowsCopyInventory(IReadOnlyList<ContentEstimate> Items, IReadOnlyList<string> Notes);

/// <summary>Read-only discovery. Permission failures are visible and unlisted branches are retained, never implicitly excluded.</summary>
public static class WindowsCopyDiscovery
{
    public static Task<WindowsCopyInventory> ScanAsync(string root, CancellationToken token, IProgress<string>? progress = null) =>
        Task.Run(() => Scan(root, token, progress), token);

    public static WindowsCopyInventory Scan(string root, CancellationToken token = default, IProgress<string>? progress = null, IEnumerable<InstalledProgram>? programs = null)
    {
        var notes = new List<string>();
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> List(string relative)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var directory = new DirectoryInfo(Path.Combine(root, relative));
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { unreadable.Add(relative); return []; }
                return directory.EnumerateFileSystemInfos().Select(e => e.Name).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(relative);
                return [];
            }
        }
        // Root failure aborts discovery instead of showing an apparently empty volume.
        var names = new DirectoryInfo(root).EnumerateFileSystemInfos().Select(e => e.Name).ToList();
        var personal = new List<PersonalLocation>();
        if (root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
            foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos" })
            {
                var location = KnownFolders.ResolveShellName(name);
                if (string.IsNullOrEmpty(location) || !Directory.Exists(location)) continue;
                var relative = Path.GetRelativePath(root, location);
                try
                {
                    relative = WindowsCopyPlanning.NormalizeRelativePath(relative);
                    if (!WindowsCopyPlanning.ProtectsWindowsContent(relative)) personal.Add(new(Environment.UserName, relative, Environment.UserName + " · " + name));
                }
                catch (ArgumentException) { notes.Add($"{name} is outside this Windows volume ({location}) and is not included in this copy. Copy it separately if needed."); }
            }
        var items = WindowsContentClassifier.Classify(names, List, programs ?? InstalledPrograms(root), personal).ToList();
        foreach (var path in unreadable)
            items.Add(new(ContentGroup.Other, path, path + " (all content retained: preview unavailable)", null, true));
        if (unreadable.Count > 0) notes.Add("Some branches could not be listed without administrator access. They are kept in full; the worker rechecks all selected content before erasing the destination.");
        if (!Path.GetPathRoot(Path.GetFullPath(root))!.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
            notes.Add("Program names and redirected personal-folder settings from another Windows installation cannot be read without loading its registry. Its folders remain selectable by location.");
        var estimates = new List<ContentEstimate>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("Measuring " + item.Title);
            long bytes = 0;
            var complete = true;
            void Measure(string path)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { complete = false; return; }
                    if ((attributes & FileAttributes.Directory) == 0) { bytes = checked(bytes + new FileInfo(path).Length); return; }
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) Measure(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
            }
            if (item.Group != ContentGroup.AlwaysExcluded) Measure(Path.Combine(root, item.RelativePath));
            estimates.Add(new(item, bytes, complete));
        }
        if (estimates.Any(e => !e.Complete)) notes.Add("Some sizes are incomplete (permissions, links or cloud files). The space estimate is a lower bound, and the elevated worker checks the snapshot again before writing.");
        notes.Add("Links outside the source volume are not followed. Their target files are not brought onto the copy; cloud-only files may need to be downloaded before copying.");
        return new(estimates, notes);
    }

    private static IReadOnlyList<InstalledProgram> InstalledPrograms(string sourceRoot)
    {
        var result = new List<InstalledProgram>();
        if (!sourceRoot.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)) return result;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key?.GetValue("DisplayName") is not string title || key.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location)) continue;
                        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(location.Trim('"')));
                        var relative = Path.GetRelativePath(sourceRoot, path).TrimEnd('\\');
                        if (!Directory.Exists(path)) continue;
                        relative = WindowsCopyPlanning.NormalizeRelativePath(relative);
                        // Registry hints cannot turn a required Windows branch into an optional program.
                        if (WindowsCopyPlanning.ProtectsWindowsContent(relative)) continue;
                        result.Add(new(title, relative));
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
        return result.DistinctBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
