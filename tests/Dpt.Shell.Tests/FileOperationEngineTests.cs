using System.Diagnostics;
using Dpt.Shell.Operations;

namespace Dpt.Shell.Tests;

/// <summary>A unique folder under %TEMP% that is removed afterwards. All file-operation tests stay inside one.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dpt-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public class FileOperationEngineTests
{
    private readonly FileOperationEngine _engine = new();

    [Fact]
    public async Task Copy_and_move()
    {
        using var t = new TempFolder();
        var a = t.File(@"src\a.txt", "hello");
        var dest = t.Dir("dest");
        var copy = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Copy, Sources = [a], DestinationFolder = dest });
        Assert.True(copy.Succeeded);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(a));
        var c = Assert.Single(copy.Completed);
        Assert.Equal(a, c.Source, ignoreCase: true);
        Assert.Equal(Path.Combine(dest, "a.txt"), c.Destination, ignoreCase: true);

        var dest2 = t.Dir("dest2");
        var move = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Move, Sources = [a], DestinationFolder = dest2 });
        Assert.True(move.Succeeded);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(Path.Combine(dest2, "a.txt")));
    }

    [Fact]
    public async Task Copy_into_same_folder_makes_copy_name()
    {
        using var t = new TempFolder();
        var a = t.File("report.pdf");
        var r = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Copy, Sources = [a], DestinationFolder = t.Path });
        Assert.True(r.Succeeded);
        Assert.True(File.Exists(Path.Combine(t.Path, "report - Copy.pdf")));
    }

    [Fact]
    public async Task Conflicts_default_to_keep_both()
    {
        using var t = new TempFolder();
        var a = t.File(@"src\a.txt", "new");
        t.File(@"dest\a.txt", "old");
        var dest = Path.Combine(t.Path, "dest");
        Assert.Single(FileOperationEngine.FindConflicts([a], dest));
        var r = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Copy, Sources = [a], DestinationFolder = dest });
        Assert.True(r.Succeeded);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "a (2).txt")));
    }

    [Fact]
    public async Task Conflict_replace_and_skip()
    {
        using var t = new TempFolder();
        var a = t.File(@"src\a.txt", "new-a");
        var b = t.File(@"src\b.txt", "new-b");
        t.File(@"dest\a.txt", "old-a");
        t.File(@"dest\b.txt", "old-b");
        var dest = Path.Combine(t.Path, "dest");
        var r = await _engine.RunAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.Copy,
            Sources = [a, b],
            DestinationFolder = dest,
            Conflicts = new Dictionary<string, ConflictChoice> { [a] = ConflictChoice.Replace, [b] = ConflictChoice.Skip },
        });
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("old-b", File.ReadAllText(Path.Combine(dest, "b.txt")));
        Assert.Contains(b, r.Skipped);
    }

    [Fact]
    public async Task Rename_new_folder_new_file()
    {
        using var t = new TempFolder();
        var nf = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.NewFolder, DestinationFolder = t.Path, NewName = "Projects" });
        Assert.True(Directory.Exists(Path.Combine(t.Path, "Projects")));
        Assert.True(Assert.Single(nf.Completed).IsFolder);

        await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.NewFile, DestinationFolder = t.Path, NewName = "notes.txt" });
        Assert.True(File.Exists(Path.Combine(t.Path, "notes.txt")));

        var rn = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Rename, Sources = [Path.Combine(t.Path, "notes.txt")], NewName = "todo.txt" });
        Assert.True(rn.Succeeded);
        Assert.True(File.Exists(Path.Combine(t.Path, "todo.txt")));
    }

    [Fact]
    public void UniqueName_counts_up()
    {
        using var t = new TempFolder();
        t.Dir("New folder");
        t.Dir("New folder (2)");
        Assert.Equal("New folder (3)", FileOperationEngine.UniqueName(t.Path, "New folder", isFolder: true));
        t.File("a.txt");
        Assert.Equal("a (2).txt", FileOperationEngine.UniqueName(t.Path, "a.txt", isFolder: false));
    }

    [Fact]
    public async Task Recycle_then_restore()
    {
        using var t = new TempFolder();
        var a = t.File("recycle-me.txt", "keep me");
        var r = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Recycle, Sources = [a] });
        Assert.True(r.Succeeded, string.Join("; ", r.Failed.Select(f => f.Message)));
        Assert.False(File.Exists(a));
        var done = Assert.Single(r.Completed);
        Assert.NotNull(done.Destination); // the $R path inside the Recycle Bin
        Assert.True(RecycleBin.Restore(done.Destination!, a));
        Assert.Equal("keep me", File.ReadAllText(a));
    }

    [Fact]
    public async Task Permanent_delete_requires_confirmation()
    {
        using var t = new TempFolder();
        var a = t.File("a.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.PermanentDelete, Sources = [a] }));
        var b = t.File("b.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.RunAsync(new FileOperationRequest
            {
                Kind = FileOperationKind.PermanentDelete,
                Sources = [a, b],
                Confirmation = PermanentDeleteConfirmation.ConfirmedByUser([a]), // does not cover b
            }));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));

        var ok = await _engine.RunAsync(new FileOperationRequest
        {
            Kind = FileOperationKind.PermanentDelete,
            Sources = [a],
            Confirmation = PermanentDeleteConfirmation.ConfirmedByUser([a]),
        });
        Assert.True(ok.Succeeded);
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Guard_flags()
    {
        Assert.True(RecycleGuard.WouldBePermanent(0));
        Assert.True(RecycleGuard.WouldBePermanent(0x400));
        Assert.False(RecycleGuard.WouldBePermanent(0x80));
        Assert.False(RecycleGuard.WouldBePermanent(0x80 | 0x40));
    }

    /// <summary>
    /// A SUBST drive has no Recycle Bin, so Windows would delete permanently. The guard must block every item and leave it on disk.
    /// </summary>
    [Fact]
    public async Task Recycle_on_drive_without_recycle_bin_blocks_and_keeps_items()
    {
        using var t = new TempFolder();
        using var subst = SubstDrive.Create(t.Path);
        var a = t.File("big-video.mkv", "pretend this is huge");
        var b = t.File(@"folder\inner.txt", "inner");
        var onSubst = new[] { Path.Combine(subst.Root, "big-video.mkv"), Path.Combine(subst.Root, "folder") };

        var r = await _engine.RunAsync(new FileOperationRequest { Kind = FileOperationKind.Recycle, Sources = onSubst });

        Assert.True(File.Exists(a), "File must not be deleted when it can't be recycled.");
        Assert.True(File.Exists(b), "Folder contents must not be deleted when it can't be recycled.");
        Assert.Equal(2, r.BlockedFromRecycle.Count);
        Assert.Empty(r.Completed);
        Assert.False(r.Succeeded);
    }
}

/// <summary>Maps a free drive letter to a folder with SUBST (no admin needed) and removes it afterwards.</summary>
public sealed class SubstDrive : IDisposable
{
    private SubstDrive(string letter) => Letter = letter;

    public string Letter { get; }
    public string Root => Letter + @"\";

    public static SubstDrive Create(string folder)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "PQRSTUVWXYZ".First(c => !used.Contains(c)) + ":";
        Run($"{letter} \"{folder}\"");
        return new SubstDrive(letter);
    }

    private static void Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("subst.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"subst {args} failed ({p.ExitCode})");
    }

    public void Dispose() => Run($"{Letter} /d");
}
