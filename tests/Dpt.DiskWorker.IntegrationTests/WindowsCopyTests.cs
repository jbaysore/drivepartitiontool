using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Dpt.Core.WindowsCopy;
using Dpt.DiskWorker.Windows;
using Dpt.Ipc;
using Dpt.Storage;
using Dpt.TestSupport;
using Microsoft.Win32.SafeHandles;

namespace Dpt.DiskWorker.IntegrationTests;

/// <summary>M9: Windows-only copy between test VHDs (Windows-like folder tree; boot step off — boot test deferred by the owner).</summary>
[Trait("Category", "Elevated")]
public class WindowsCopyTests
{
    private static DiskInfo Disk(int n) => StorageInventory.GetDisks().Single(d => d.Number == n);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existing, IntPtr sec);

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Attr;
        public long C, A, W;
        public uint Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info info);

    private static uint Links(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        GetFileInformationByHandle(h, out var i);
        return i.Links;
    }

    private static string Hash(string root) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(f => !f.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))))));

    [Fact]
    public void Copies_windows_and_chosen_items_only_and_leaves_the_source_unchanged()
    {
        using var source = TestVhd.CreateSystemLike(700, "DPTWIN");
        var r = source.Root;
        void Put(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(r, rel))!);
            File.WriteAllText(Path.Combine(r, rel), text);
        }

        Put(@"Windows\System32\config\SYSTEM", "hive");
        Put(@"Windows\System32\drivers\etc\hosts", "127.0.0.1 localhost");
        Put(@"Windows\WinSxS\amd64_component\library.dll", "component");
        Assert.True(CreateHardLink(Path.Combine(r, @"Windows\System32\library.dll"), Path.Combine(r, @"Windows\WinSxS\amd64_component\library.dll"), IntPtr.Zero));
        Put(@"Program Files\App\app.exe", "app");
        Put(@"ProgramData\Vendor\settings.ini", "x=1");
        Put(@"Users\Alice\NTUSER.DAT", "alice hive");
        Put(@"Users\Alice\Documents\letter.txt", "dear");
        Put(@"Users\Alice\Pictures\holiday.jpg", "pixels");
        Put(@"Users\Alice\AppData\Roaming\Browser\prefs.json", "{}");
        Put(@"Users\Public\Documents\shared.txt", "shared");
        Put(@"Games\save.dat", "save");
        Put("pagefile.sys", "not really a pagefile");
        var before = Hash(r);

        using var target = TestVhd.Create(2500, "NTFS", "OLD");
        var options = new WindowsCopyOptions
        {
            SourceRoot = r,
            Exclusions = [@"Users\Alice\Pictures", @"Users\Alice\AppData\Roaming\Browser", "Games", "pagefile.sys"],
            MakeBootable = false,
            Firmware = WindowsCopyEngine.IsUefi() ? BootFirmware.Uefi : BootFirmware.Bios,
        };
        var dst = Disk(target.DiskNumber);
        var plan = Plans.For(dst, new DiskStep { Kind = DiskStepKind.WindowsCopy, Options = JsonSerializer.Serialize(options, PipeProtocol.Json) }) with
        {
            Source = Fingerprint.Of(Disk(source.DiskNumber)),
            ExpectedConfirmation = $"DISK {dst.Number}",
            Confirmation = $"DISK {dst.Number}",
        };
        var result = Plans.RunWorker(plan);
        Assert.True(result.Code == DiskWorkerExit.Ok, string.Join(" | ", result.Progress.Select(p => $"{p.State}: {p.Message}")));

        Assert.Equal(before, Hash(r)); // source unchanged

        var copy = Disk(target.DiskNumber);
        Assert.Equal(WindowsCopyEngine.IsUefi() ? "GPT" : "MBR", copy.PartitionStyle);
        if (WindowsCopyEngine.IsUefi())
        {
            Assert.Contains(copy.Partitions, p => p.Role == PartitionRole.Efi);
            Assert.Contains(copy.Partitions, p => p.Role == PartitionRole.Reserved);
        }

        var win = copy.Partitions.Single(p => p.Label == "Windows");
        Assert.Contains(copy.Partitions, p => p.Role == PartitionRole.Recovery && p.Size >= 1024L * 1024 * 1024);
        var root = win.Root ?? TestVhd.EnsureLetter(copy.Number, win.PartitionNumber);
        string P(string rel) => Path.Combine(root, rel);
        Assert.Equal("hive", File.ReadAllText(P(@"Windows\System32\config\SYSTEM")));
        Assert.Equal("app", File.ReadAllText(P(@"Program Files\App\app.exe")));
        Assert.Equal("x=1", File.ReadAllText(P(@"ProgramData\Vendor\settings.ini")));
        Assert.Equal("dear", File.ReadAllText(P(@"Users\Alice\Documents\letter.txt")));
        Assert.Equal("alice hive", File.ReadAllText(P(@"Users\Alice\NTUSER.DAT")));
        Assert.True(File.Exists(P(@"Users\Public\Documents\shared.txt")));
        Assert.False(Directory.Exists(P(@"Users\Alice\Pictures")));
        Assert.False(Directory.Exists(P(@"Users\Alice\AppData\Roaming\Browser")));
        Assert.False(Directory.Exists(P("Games")));
        Assert.False(File.Exists(P("pagefile.sys")));
        Assert.Empty(Directory.EnumerateFiles(root, "dpt-transfer-*.wim"));
        // Hard links survive as hard links (important for Windows' component store).
        Assert.Equal(2u, Links(P(@"Windows\System32\library.dll")));
    }
}
