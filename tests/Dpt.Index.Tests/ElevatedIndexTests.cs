using System.Diagnostics;
using System.Runtime.InteropServices;
using Dpt.Index.Ntfs;
using Dpt.Ipc;
using Dpt.TestSupport;
using Xunit.Abstractions;

namespace Dpt.Index.Tests;

/// <summary>
/// MFT + change-journal tests. Run with ./build.ps1 -Elevated (Administrator). The system drive is only READ;
/// all changes happen on a throwaway virtual disk.
/// </summary>
[Trait("Category", "Elevated")]
public class ElevatedIndexTests(ITestOutputHelper output)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    [Fact]
    public void Reads_system_drive_mft_quickly_and_correctly()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var index = new VolumeIndex(root, 1, "NTFS", "");
        using var ntfs = new NtfsVolume(root);
        var sw = Stopwatch.StartNew();
        index.Lock.EnterWriteLock();
        ntfs.LoadInto(index, null, CancellationToken.None);
        index.Lock.ExitWriteLock();
        sw.Stop();
        var perMillion = sw.Elapsed.TotalSeconds / Math.Max(1, index.LiveCount / 1_000_000.0);
        output.WriteLine($"{root}: {index.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s ({perMillion:F1}s per million); total {index.Size(index.RootIndex):N0} bytes");
        var drive = new DriveInfo(root);
        output.WriteLine($"size on disk {index.SizeOnDisk(index.RootIndex):N0} bytes; volume used {drive.TotalSize - drive.TotalFreeSpace:N0} bytes");
        Assert.True(index.LiveCount > 50_000);
        Assert.True(perMillion < 30, $"MFT load too slow: {perMillion:F1}s per million entries");

        // Spot-check: a known file's size matches the file system.
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var i = index.Find(notepad);
        Assert.NotEqual(VolumeIndex.NoEntry, i);
        Assert.Equal(new FileInfo(notepad).Length, index.Size(i));
        // A folder total roughly matches a directory walk (hard links/inaccessible files can differ slightly).
        var drivers = Path.Combine(Environment.SystemDirectory, "drivers", "etc");
        var walk = new DirectoryInfo(drivers).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        Assert.Equal(walk, index.Size(index.Find(drivers)));
    }

    [Fact]
    public async Task Journal_reflects_changes_within_two_seconds_and_hard_links_count_once()
    {
        var stepLog = Path.Combine(TestVhd.Folder, "journal-test.log");
        Directory.CreateDirectory(TestVhd.Folder);
        void Step(string s) => File.AppendAllText(stepLog, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
        Step("creating vhd");
        using var vhd = TestVhd.Create(256);
        Step($"vhd ready at {vhd.Root}");
        var r = vhd.Root;
        Directory.CreateDirectory(Path.Combine(r, "Media", "Video"));
        File.WriteAllBytes(Path.Combine(r, "Media", "Video", "clip.mp4"), new byte[100_000]);
        File.WriteAllBytes(Path.Combine(r, "Media", "big.bin"), new byte[1_000_000]);
        Assert.True(CreateHardLink(Path.Combine(r, "Media", "Video", "big-link.bin"), Path.Combine(r, "Media", "big.bin"), IntPtr.Zero));

        var data = Path.Combine(TestVhd.Folder, "engine-" + Guid.NewGuid().ToString("N"));
        using var engine = new IndexEngine(new EngineOptions { DataDirectory = data, UseMft = true, FixedRoots = [r], PollInterval = TimeSpan.FromMilliseconds(250) });
        Step("engine starting");
        engine.Start();
        await engine.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
        var status = engine.GetStatus().Volumes.Single();
        Step($"engine ready: {status.State} {status.Mode} {status.Items} {status.Message}");
        Assert.Equal("MFT", status.Mode);

        // Hard link: the 1 MB file is counted once in the volume total.
        var sizes = await engine.GetFolderSizesAsync([Path.Combine(r, "Media"), r]);
        Assert.Equal(1_100_000, sizes[0]);

        async Task Within2s(Func<Task<bool>> check, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000)
            {
                if (await check()) return;
                await Task.Delay(50);
            }

            Step($"{what}: done={await check()} after {sw.ElapsedMilliseconds} ms");
            Assert.True(await check(), $"{what} not reflected within 2 s");
            output.WriteLine($"{what}: {sw.ElapsedMilliseconds} ms");
        }

        async Task<int> Count(string q) => (await engine.SearchAsync(new SearchRequest { Query = q, IncludeHidden = true })).Total;

        File.WriteAllBytes(Path.Combine(r, "invoice-2026.pdf"), new byte[5000]);
        await Within2s(async () => await Count("invoice-2026") == 1, "create");

        File.Move(Path.Combine(r, "invoice-2026.pdf"), Path.Combine(r, "invoice-final.pdf"));
        await Within2s(async () => await Count("invoice-final") == 1 && await Count("invoice-2026") == 0, "rename");

        File.Move(Path.Combine(r, "invoice-final.pdf"), Path.Combine(r, "Media", "invoice-final.pdf"));
        await Within2s(async () => (await engine.GetFolderSizesAsync([Path.Combine(r, "Media")]))[0] == 1_105_000, "move");

        File.WriteAllBytes(Path.Combine(r, "Media", "Video", "clip.mp4"), new byte[300_000]);
        // Grew by 200 KB. (The hard-linked 1 MB file is counted once, under whichever folder holds its primary name.)
        await Within2s(async () => (await engine.GetFolderSizesAsync([Path.Combine(r, "Media")]))[0] == 1_305_000, "size change");

        Directory.Delete(Path.Combine(r, "Media"), true);
        // (The volume root also holds Windows' own "System Volume Information", so check the deleted tree is gone.)
        await Within2s(async () => await Count("clip") == 0 && await Count("invoice-final") == 0 &&
                                   (await engine.GetFolderSizesAsync([Path.Combine(r, "Media")]))[0] is null, "delete tree");
        Step("disposing");
    }
}
