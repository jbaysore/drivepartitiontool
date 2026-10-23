using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dpt.App.Services;
using Dpt.Shell.Operations;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dpt.App.Tests;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
    {
        // App services use the per-user database; point them at a throwaway folder.
        Environment.SetEnvironmentVariable("DPT_DATA_DIR", Path.Combine(Path.GetTempPath(), "dpt-app-tests", Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("DPT_CATALOG", "off");
    }
}

/// <summary>A scripted "owner" that answers every dialog and records what was asked.</summary>
public sealed class ScriptedDialogs : IDialogService
{
    public bool ConfirmPermanent { get; set; }
    public bool PermanentlyDeleteBlocked { get; set; }
    public bool ContinueAfterWarning { get; set; } = true;
    public ConflictChoice ConflictAnswer { get; set; } = ConflictChoice.KeepBoth;
    public bool CancelConflicts { get; set; }
    public List<string> Asked { get; } = [];
    public IReadOnlyList<BlockedItem> LastBlocked { get; private set; } = [];

    public IReadOnlyDictionary<string, ConflictChoice>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts, bool isMove)
    {
        Asked.Add("conflicts");
        return CancelConflicts ? null : conflicts.ToDictionary(c => c.Source, _ => ConflictAnswer, StringComparer.OrdinalIgnoreCase);
    }

    public bool ConfirmPermanentDelete(IReadOnlyList<string> paths, string reason)
    {
        Asked.Add("confirm-permanent");
        return ConfirmPermanent;
    }

    public bool AskPermanentDeleteForBlocked(IReadOnlyList<BlockedItem> blocked)
    {
        Asked.Add("blocked");
        LastBlocked = blocked;
        return PermanentlyDeleteBlocked;
    }

    public bool WarnBeforeRecycle(IReadOnlyList<(string Path, string Reason)> warnings)
    {
        Asked.Add("warn");
        return ContinueAfterWarning;
    }

    public bool Confirm(string title, string message, string confirmText, bool danger = false) => true;
    public string? Prompt(string title, string message, string initial) => initial;
    public void ShowError(string title, string message) => Asked.Add("error: " + message);
    public void ShowInfo(string title, string message) { }
}

public sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "dpt-app-tests-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string rel, string content = "x")
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Dir(string rel) => Directory.CreateDirectory(Path.Combine(Root, rel)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
    }
}

/// <summary>Maps a free drive letter to a folder with SUBST. SUBST drives have no Recycle Bin.</summary>
public sealed class Subst : IDisposable
{
    private Subst(string letter) => Letter = letter;

    public string Letter { get; }

    public static Subst Map(string folder)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "PQRSTUVWXYZ".First(c => !used.Contains(c)) + ":";
        Run($"{letter} \"{folder}\"");
        return new Subst(letter);
    }

    private static void Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("subst.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
    }

    public void Dispose() => Run($"{Letter} /d");
}

/// <summary>
/// M4: the app-level file operations — the owner's decisions are always respected, nothing is permanently deleted
/// without explicit confirmation, conflicts are asked about, and moves/renames/creates/copies/recycles can be undone.
/// </summary>
public class FileOperationsServiceTests
{
    private readonly ScriptedDialogs _dialogs = new();
    private readonly FileOperationsService _ops;

    public FileOperationsServiceTests()
    {
        var services = AppServices.Initialize();
        services.FileOps.Dialogs = _dialogs;
        _ops = services.FileOps;
    }

    [Fact]
    public async Task Items_that_cannot_be_recycled_stay_when_the_owner_cancels()
    {
        using var box = new Sandbox();
        var file = box.File("big.mkv", "data");
        var folder = box.File(@"folder\inner.txt");
        using var subst = Subst.Map(box.Root);
        _dialogs.PermanentlyDeleteBlocked = false;

        var result = await _ops.RecycleAsync([$@"{subst.Letter}\big.mkv", $@"{subst.Letter}\folder"]);

        Assert.Contains("blocked", _dialogs.Asked);
        Assert.Equal(2, _dialogs.LastBlocked.Count);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(folder));
        Assert.Equal(2, result!.BlockedFromRecycle.Count);
    }

    [Fact]
    public async Task Items_that_cannot_be_recycled_are_deleted_only_after_explicit_choice()
    {
        using var box = new Sandbox();
        var file = box.File("old-backup.zip");
        using var subst = Subst.Map(box.Root);
        _dialogs.PermanentlyDeleteBlocked = true;

        await _ops.RecycleAsync([$@"{subst.Letter}\old-backup.zip"]);

        Assert.Contains("blocked", _dialogs.Asked);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Shift_delete_does_nothing_unless_confirmed()
    {
        using var box = new Sandbox();
        var file = box.File("keep.txt");
        _dialogs.ConfirmPermanent = false;
        Assert.Null(await _ops.PermanentDeleteAsync([file]));
        Assert.True(File.Exists(file));

        _dialogs.ConfirmPermanent = true;
        await _ops.PermanentDeleteAsync([file]);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Conflicts_are_asked_and_respected()
    {
        using var box = new Sandbox();
        var src = box.File(@"src\a.txt", "new");
        box.File(@"dest\a.txt", "old");
        var dest = Path.Combine(box.Root, "dest");

        _dialogs.CancelConflicts = true;
        Assert.Null(await _ops.CopyAsync([src], dest));
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "a.txt")));

        _dialogs.CancelConflicts = false;
        _dialogs.ConflictAnswer = ConflictChoice.Skip;
        await _ops.CopyAsync([src], dest);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "a.txt")));

        _dialogs.ConflictAnswer = ConflictChoice.Replace;
        await _ops.CopyAsync([src], dest);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Moving_a_folder_into_itself_is_refused()
    {
        using var box = new Sandbox();
        var folder = box.Dir("Parent");
        var child = box.Dir(@"Parent\Child");
        Assert.Null(await _ops.MoveAsync([folder], child));
        Assert.True(Directory.Exists(child));
        Assert.Contains(_dialogs.Asked, a => a.StartsWith("error:"));
    }

    [Fact]
    public async Task Undo_move_rename_create_copy_and_recycle()
    {
        using var box = new Sandbox();
        var a = box.File(@"in\a.txt", "A");
        var outDir = box.Dir("out");

        await _ops.MoveAsync([a], outDir);
        Assert.True(File.Exists(Path.Combine(outDir, "a.txt")));
        Assert.StartsWith("Undid", await _ops.UndoAsync());
        Assert.True(File.Exists(a));

        var renamed = await _ops.RenameAsync(a, "b.txt");
        Assert.NotNull(renamed);
        await _ops.UndoAsync();
        Assert.True(File.Exists(a));

        var created = await _ops.NewFolderAsync(box.Root);
        Assert.NotNull(created);
        await _ops.UndoAsync();
        Assert.False(Directory.Exists(created));

        await _ops.CopyAsync([a], outDir);
        await _ops.UndoAsync();
        Assert.False(File.Exists(Path.Combine(outDir, "a.txt")));
        Assert.True(File.Exists(a));

        await _ops.RecycleAsync([a]);
        Assert.False(File.Exists(a));
        await _ops.UndoAsync();
        Assert.Equal("A", File.ReadAllText(a));
    }

    [Fact]
    public async Task Bulk_rename_chain_and_undo()
    {
        using var box = new Sandbox();
        var one = box.File("1.txt", "one");
        var two = box.File("2.txt", "two");
        // 1→2 and 2→3 (needs temporary names).
        var n = await _ops.RenameManyAsync([(one, "2.txt"), (two, "3.txt")], "Rename 2 items");
        Assert.Equal(2, n);
        Assert.Equal("one", File.ReadAllText(Path.Combine(box.Root, "2.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(box.Root, "3.txt")));
        Assert.False(File.Exists(one));

        await _ops.UndoAsync();
        Assert.Equal("one", File.ReadAllText(one));
        Assert.Equal("two", File.ReadAllText(two));
        Assert.False(File.Exists(Path.Combine(box.Root, "3.txt")));
    }

    [Fact]
    public async Task Duplicate_creates_copy_next_to_original()
    {
        using var box = new Sandbox();
        var a = box.File("Report.pdf", "r");
        await _ops.DuplicateAsync([a]);
        Assert.True(File.Exists(Path.Combine(box.Root, "Report - Copy.pdf")));
    }

    [Fact]
    public void Invalid_names_are_explained()
    {
        Assert.NotNull(FileOperationsService.ValidateName("a:b"));
        Assert.NotNull(FileOperationsService.ValidateName("CON.txt"));
        Assert.NotNull(FileOperationsService.ValidateName("trailing."));
        Assert.Null(FileOperationsService.ValidateName("Budget 2026.xlsx"));
    }
}
