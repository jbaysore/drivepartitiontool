using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Dpt.App.Services;

/// <summary>Opt-in folder verbs with persisted, typed backups. Construction and IsEnabled are read-only.</summary>
public sealed class FolderAssociationService(RegistryKey root, string classesPath, string backupPath, string executable, Action notify)
{
    public const string Verb = "DrivePartitionTool.Open";
    private const string Owner = "DrivePartitionTool.FolderHandler.v1";
    private const string Title = "Open with DrivePartitionTool";
    private static readonly string[] Classes = ["Folder", "Directory", "Drive"];

    public static FolderAssociationService CreateDefault() => new(Registry.CurrentUser, @"Software\Classes", @"Software\DrivePartitionTool\FolderHandlerBackup",
        Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable."), NotifyShell);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    public static string CommandFor(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(c => c < 32 || c == '"')) throw new ArgumentException("The application path must be an absolute, literal path.");
        // Appending \. keeps a volume root's trailing slash from escaping the command-line closing quote.
        return $"\"{Path.GetFullPath(path)}\" \"%1\\.\"";
    }
    private string Command => CommandFor(executable);
    private string Icon => $"\"{Path.GetFullPath(executable)}\",0";
    private string ShellPath(string name) => $@"{classesPath}\{name}\shell";
    private static object? Raw(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    private static bool Exists(RegistryKey key, string name) => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    public bool HasSavedState { get { using var backup = root.OpenSubKey(backupPath); return backup?.GetValue("Owner") as string == Owner; } }

    public bool IsEnabled => Classes.All(name =>
    {
        using var shell = root.OpenSubKey(ShellPath(name));
        using var verb = shell?.OpenSubKey(Verb);
        return shell?.GetValue("") as string == Verb && verb is not null && MatchesRegistration(verb, Command, Icon);
    });

    public string? SetEnabled(bool enabled)
    {
        _ = Command; // validate before creating a backup or writing any key
        if (enabled) { Enable(); notify(); return null; }
        var note = Restore(); notify(); return note;
    }

    private void Enable()
    {
        using var existing = root.OpenSubKey(backupPath);
        if (existing is not null)
        {
            if (existing.GetValue("Owner") as string != Owner) throw new InvalidOperationException("The saved folder-handler state does not belong to this app.");
            if (existing.GetValue("Prepared") as int? != 1)
            {
                // No defaults can have changed before Prepared is persisted. Discard only this app's incomplete snapshot.
                existing.Dispose(); root.DeleteSubKeyTree(backupPath); Enable(); return;
            }
            foreach (var name in Classes)
            {
                using var saved = existing.OpenSubKey(name);
                using var shell = root.OpenSubKey(ShellPath(name));
                using var verb = shell?.OpenSubKey(Verb);
                if (saved is null || shell?.GetValue("") as string != Verb || verb?.GetValue("DptOwner") as string != Owner ||
                    !UnchangedVerb(verb, saved))
                    throw new InvalidOperationException("Folder defaults changed outside this app. Turn the setting off before enabling it again.");
            }
        }
        else
        {
            // Do not overwrite even an apparently unused colliding verb.
            foreach (var name in Classes)
            {
                using var verb = root.OpenSubKey(ShellPath(name) + "\\" + Verb);
                if (verb is not null) throw new InvalidOperationException("A folder verb with this app's name already exists without its saved defaults. Nothing was changed.");
            }
            using var backup = root.CreateSubKey(backupPath);
            backup.SetValue("Owner", Owner);
            foreach (var name in Classes)
            {
                using var saved = backup.CreateSubKey(name);
                using var folderClass = root.OpenSubKey(classesPath + "\\" + name);
                using var shell = root.OpenSubKey(ShellPath(name));
                saved.SetValue("ClassExisted", folderClass is null ? 0 : 1);
                saved.SetValue("ShellExisted", shell is null ? 0 : 1);
                var hadDefault = shell is not null && Exists(shell, "");
                saved.SetValue("HadDefault", hadDefault ? 1 : 0);
                saved.SetValue("VerbWasAbsent", 1);
                if (hadDefault)
                {
                    var kind = shell!.GetValueKind("");
                    saved.SetValue("PreviousKind", (int)kind);
                    saved.SetValue("PreviousDefault", Raw(shell, "") ?? throw new IOException("Could not save the previous folder default."), kind);
                }
            }
            backup.SetValue("Prepared", 1);
        }
        try
        {
            foreach (var name in Classes)
            {
                using var saved = root.CreateSubKey(backupPath + "\\" + name);
                if (saved.GetValue("OwnedCommand") is string previous) saved.SetValue("PreviousOwnedCommand", previous);
                if (saved.GetValue("OwnedIcon") is string previousIcon) saved.SetValue("PreviousOwnedIcon", previousIcon);
                saved.SetValue("OwnedCommand", Command); saved.SetValue("OwnedIcon", Icon);
                using var shell = root.CreateSubKey(ShellPath(name));
                using var verb = shell.CreateSubKey(Verb);
                verb.SetValue("", Title); verb.SetValue("DptOwner", Owner); verb.SetValue("Icon", Icon); verb.SetValue("MultiSelectModel", "Document");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", Command);
                shell.SetValue("", Verb);
            }
        }
        catch
        {
            // Backups are complete before any defaults change; keep them if rollback itself fails.
            Restore();
            throw;
        }
    }

    private string? Restore()
    {
        using var backup = root.OpenSubKey(backupPath);
        if (backup is null)
        {
            foreach (var name in Classes)
            {
                using var shell = root.OpenSubKey(ShellPath(name));
                if (shell?.GetValue("") as string == Verb) throw new InvalidOperationException("Saved folder defaults are unavailable. The current registration was kept so it can be recovered.");
            }
            return null;
        }
        if (backup.GetValue("Owner") as string != Owner) throw new InvalidOperationException("The saved folder-handler state does not belong to this app.");
        var changedElsewhere = false;
        if (backup.GetValue("Prepared") as int? == 1)
            foreach (var name in Classes)
            {
                using var saved = backup.OpenSubKey(name) ?? throw new IOException("A saved folder default is missing. Remaining backups were kept.");
                using (var shell = root.OpenSubKey(ShellPath(name), writable: true))
                {
                    if (shell is null) continue;
                    if (shell.GetValue("") as string == Verb)
                    {
                        if (saved.GetValue("HadDefault") as int? == 1)
                            shell.SetValue("", Raw(saved, "PreviousDefault") ?? throw new IOException("The saved folder default is unreadable."), (RegistryValueKind)(int)saved.GetValue("PreviousKind")!);
                        else shell.DeleteValue("", throwOnMissingValue: false);
                    }
                    else changedElsewhere = true;
                    using (var verb = shell.OpenSubKey(Verb))
                    {
                        if (verb is not null && !OwnedFragments(verb, saved)) changedElsewhere = true;
                    }
                    using var check = shell.OpenSubKey(Verb);
                    if (check is not null && OwnedFragments(check, saved)) shell.DeleteSubKeyTree(Verb);
                }
                RemoveEmpty(ShellPath(name), saved.GetValue("ShellExisted") as int? == 0);
                RemoveEmpty(classesPath + "\\" + name, saved.GetValue("ClassExisted") as int? == 0);
            }
        root.DeleteSubKeyTree(backupPath);
        return changedElsewhere ? "Folder settings changed by another app were kept." : null;
    }

    private static bool UnchangedVerb(RegistryKey verb, RegistryKey saved)
        => MatchesRegistration(verb, saved.GetValue("OwnedCommand") as string, saved.GetValue("OwnedIcon") as string);

    private static bool MatchesRegistration(RegistryKey verb, string? expectedCommand, string? expectedIcon)
    {
        using var command = verb.OpenSubKey("command");
        return verb.GetValue("DptOwner") as string == Owner && verb.GetValue("") as string == Title && verb.GetValue("Icon") as string == expectedIcon &&
            verb.GetValue("MultiSelectModel") as string == "Document" && verb.GetValueNames().Length == 4 && verb.GetSubKeyNames().SequenceEqual(["command"], StringComparer.OrdinalIgnoreCase) &&
            verb.GetValueNames().All(name => verb.GetValueKind(name) == RegistryValueKind.String) &&
            command is not null && command.GetValueNames().Length == 1 && command.SubKeyCount == 0 && command.GetValueKind("") == RegistryValueKind.String && command.GetValue("") as string == expectedCommand;
    }
    private static bool OwnedFragments(RegistryKey verb, RegistryKey saved)
    {
        // A crash between individual registry writes can leave a partial owned key. Remove matching fragments only.
        if (saved.GetValue("VerbWasAbsent") as int? != 1) return UnchangedVerb(verb, saved);
        var expected = new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = [Title], ["DptOwner"] = [Owner], ["MultiSelectModel"] = ["Document"],
            ["Icon"] = [saved.GetValue("OwnedIcon") as string, saved.GetValue("PreviousOwnedIcon") as string],
        };
        if (verb.GetValueNames().Any(name => !expected.TryGetValue(name, out var values) || verb.GetValueKind(name) != RegistryValueKind.String || !values.Contains(Raw(verb, name) as string))) return false;
        if (verb.GetSubKeyNames().Any(name => !name.Equals("command", StringComparison.OrdinalIgnoreCase))) return false;
        using var command = verb.OpenSubKey("command");
        return command is null || command.SubKeyCount == 0 && command.GetValueNames().All(name => name == "" && command.GetValueKind(name) == RegistryValueKind.String &&
            new[] { saved.GetValue("OwnedCommand") as string, saved.GetValue("PreviousOwnedCommand") as string }.Contains(Raw(command, name) as string));
    }
    private void RemoveEmpty(string path, bool created)
    {
        if (!created) return;
        using var key = root.OpenSubKey(path);
        if (key is not null && key.ValueCount == 0 && key.SubKeyCount == 0) root.DeleteSubKey(path);
    }
}
