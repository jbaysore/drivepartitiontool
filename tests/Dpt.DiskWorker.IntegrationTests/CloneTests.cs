using System.Security.Cryptography;
using System.Text.Json;
using Dpt.DiskWorker.Clone;
using Dpt.Ipc;
using Dpt.Storage;
using Dpt.TestSupport;

namespace Dpt.DiskWorker.IntegrationTests;

/// <summary>M8: whole-disk clone between test VHDs through the real elevated worker (byte-verified; boot test deferred).</summary>
[Trait("Category", "Elevated")]
public class CloneTests
{
    private const long Mb = 1024 * 1024;

    private static DiskInfo Disk(int number) => StorageInventory.GetDisks().Single(d => d.Number == number);

    private static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
            .Where(f => !f.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    private static DiskPlan ClonePlan(DiskInfo source, DiskInfo target, bool extend = true, string? staging = null) => Plans.For(target,
        new DiskStep { Kind = DiskStepKind.CloneDisk, Options = JsonSerializer.Serialize(new CloneOptions { ExtendLastPartition = extend, TemporaryImageFolder = staging }, PipeProtocol.Json) }) with
    {
        Source = Fingerprint.Of(source),
        ExpectedConfirmation = $"DISK {target.Number}",
        Confirmation = $"disk {target.Number}",
    };

    [Fact]
    public void Clone_makes_an_identical_copy_and_uses_extra_space()
    {
        using var source = TestVhd.CreateSystemLike(600, "DPTSRC");
        // Some content: nested folders, a larger random file, small files.
        var rng = new Random(5);
        Directory.CreateDirectory(Path.Combine(source.Root, "Users", "Test", "Documents"));
        var big = new byte[12 * Mb];
        rng.NextBytes(big);
        File.WriteAllBytes(Path.Combine(source.Root, "Users", "Test", "Documents", "big.bin"), big);
        for (var i = 0; i < 200; i++) File.WriteAllText(Path.Combine(source.Root, "Users", "Test", $"note{i}.txt"), $"note {i} {rng.Next()}");
        var sourceHashes = Hashes(source.Root);

        using var target = TestVhd.Create(1000, "NTFS", "OLDDATA");
        File.WriteAllText(Path.Combine(target.Root, "old.txt"), "will be replaced");
        var src = Disk(source.DiskNumber);
        var dst = Disk(target.DiskNumber);

        var r = Plans.RunWorker(ClonePlan(src, dst));
        Assert.True(r.Code == DiskWorkerExit.Ok, string.Join(" | ", r.Progress.Select(p => $"{p.State}: {p.Message}")));

        // Source untouched.
        Assert.Equal(sourceHashes, Hashes(source.Root));
        Assert.Equal(Fingerprint.LayoutHash(src), Fingerprint.LayoutHash(Disk(source.DiskNumber)));

        // Same layout (types and offsets); last partition grew; new disk identity.
        var copy = Disk(target.DiskNumber);
        Assert.Equal("GPT", copy.PartitionStyle);
        Assert.Equal(src.Partitions.Count, copy.Partitions.Count);
        for (var i = 0; i < src.Partitions.Count; i++)
        {
            Assert.Equal(src.Partitions[i].Offset, copy.Partitions[i].Offset);
            Assert.Equal(src.Partitions[i].GptType, copy.Partitions[i].GptType, ignoreCase: true);
            if (i < src.Partitions.Count - 1) Assert.Equal(src.Partitions[i].Size, copy.Partitions[i].Size);
        }

        Assert.True(copy.Partitions[^1].Size > src.Partitions[^1].Size + 300 * Mb, "the last partition should use the extra space");
        Assert.NotEqual(src.UniqueId, copy.UniqueId);

        // EFI partition byte-identical.
        var efi = src.Partitions.Single(p => p.Role == PartitionRole.Efi);
        using (var a = RawIo.Open(RawIo.PhysicalDrive(src.Number), write: false))
        using (var b = RawIo.Open(RawIo.PhysicalDrive(copy.Number), write: false))
            Assert.True(RawIo.Equal(a, efi.Offset, b, efi.Offset, efi.Size), "EFI partition differs");

        // Every file identical on the copy.
        var data = copy.Partitions[^1];
        var root = data.Root ?? TestVhd.EnsureLetter(copy.Number, data.PartitionNumber);
        Assert.Equal(sourceHashes, Hashes(root));
        Assert.False(File.Exists(Path.Combine(root, "old.txt")));
        Assert.Equal("DPTSRC", Disk(copy.Number).Partitions[^1].Label);
    }

    [Fact]
    public void Clone_refuses_a_destination_that_is_too_small()
    {
        using var source = TestVhd.CreateSystemLike(600, "DPTBIG");
        using var target = TestVhd.Create(300, "NTFS", "KEEPME");
        File.WriteAllText(Path.Combine(target.Root, "keep.txt"), "keep");
        var r = Plans.RunWorker(ClonePlan(Disk(source.DiskNumber), Disk(target.DiskNumber)));
        Assert.Equal(DiskWorkerExit.Failed, r.Code);
        Assert.Contains(r.Progress, p => p.Message.Contains("smaller"));
        Assert.True(File.Exists(Path.Combine(target.Root, "keep.txt")), "Nothing on the destination may change when the clone is refused.");
    }

    [Fact]
    public void Clone_requires_the_typed_disk_number()
    {
        using var source = TestVhd.CreateSystemLike(300, "DPTS");
        using var target = TestVhd.Create(400, "NTFS", "KEEP2");
        var plan = ClonePlan(Disk(source.DiskNumber), Disk(target.DiskNumber)) with { Confirmation = "yes" };
        Assert.Equal(DiskWorkerExit.RefusedConfirmation, Plans.RunWorker(plan).Code);
        Assert.Equal("KEEP2", Disk(target.DiskNumber).Partitions.Single(p => p.DriveLetter == target.Letter).Label);
    }

    [Fact]
    public void Smaller_destination_uses_images_and_preserves_source_and_staging_files()
    {
        using var source = TestVhd.CreateSystemLike(600, "DPTSMALLSRC");
        File.WriteAllText(Path.Combine(source.Root, "keep.txt"), "source content");
        File.WriteAllText(Path.Combine(source.Root, "keep.txt:metadata"), "alternate stream");
        Directory.CreateDirectory(Path.Combine(source.Root, "nested"));
        File.WriteAllBytes(Path.Combine(source.Root, "nested", "data.bin"), new byte[12 * Mb]);
        using var target = TestVhd.Create(400, "NTFS", "REPLACED");
        using var stage = TestVhd.Create(900, "NTFS", "DPTSTAGE");
        File.WriteAllText(Path.Combine(stage.Root, "leave.txt"), "unrelated staging file");
        var src = Disk(source.DiskNumber); var hashes = Hashes(source.Root);
        var result = Plans.RunWorker(ClonePlan(src, Disk(target.DiskNumber), staging: stage.Root));
        Assert.True(result.Code == DiskWorkerExit.Ok, string.Join(" | ", result.Progress.Select(p => p.Message)));
        var copy = Disk(target.DiskNumber);
        var data = copy.Partitions.Single(p => p.FileSystem == "NTFS");
        var root = data.Root ?? TestVhd.EnsureLetter(copy.Number, data.PartitionNumber);
        Assert.Equal(hashes, Hashes(root));
        Assert.Equal("alternate stream", File.ReadAllText(Path.Combine(root, "keep.txt:metadata")));
        Assert.Equal(hashes, Hashes(source.Root));
        Assert.Equal(Fingerprint.LayoutHash(src), Fingerprint.LayoutHash(Disk(src.Number)));
        Assert.Equal("unrelated staging file", File.ReadAllText(Path.Combine(stage.Root, "leave.txt")));
        Assert.Empty(Directory.GetDirectories(stage.Root, "dpt-clone-*"));
        Assert.True(data.Size < src.Partitions.Single(p => p.FileSystem == "NTFS").Size);
        Assert.NotEqual(src.UniqueId, copy.UniqueId);
    }
}
