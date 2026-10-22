using System.Text.Json;
using Dpt.Storage;
using Dpt.TestSupport;

namespace Dpt.DiskWorker.IntegrationTests;

/// <summary>M3 exit criterion: the Drives view's data matches Windows' own Get-Disk / Get-Partition output. Read-only.</summary>
public class StorageInventoryTests
{
    private sealed record PsDisk(int Number, string FriendlyName, long Size, string PartitionStyle, bool IsBoot);

    private sealed record PsPartition(int DiskNumber, int PartitionNumber, long Offset, long Size, string? DriveLetter, string? GptType);

    private static T Ps<T>(string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command} | ConvertTo-Json -Depth 3\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        if (!json.StartsWith('[')) json = "[" + json + "]";
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    internal static void AssertMatchesWindows(IReadOnlyList<DiskInfo> disks, Func<DiskInfo, bool>? only = null)
    {
        var psDisks = Ps<List<PsDisk>>("Get-Disk | Select-Object Number, FriendlyName, Size, @{n='PartitionStyle';e={[string]$_.PartitionStyle}}, IsBoot");
        var psParts = Ps<List<PsPartition>>("Get-Partition | Select-Object DiskNumber, PartitionNumber, Offset, Size, @{n='DriveLetter';e={[string]$_.DriveLetter}}, GptType");
        foreach (var d in disks.Where(only ?? (_ => true)))
        {
            var w = psDisks.Single(x => x.Number == d.Number);
            Assert.Equal(w.FriendlyName, d.FriendlyName);
            Assert.Equal(w.Size, d.Size);
            Assert.Equal(w.IsBoot, d.IsBoot);
            Assert.Equal(w.PartitionStyle == "RAW" ? "Not initialized" : w.PartitionStyle, d.PartitionStyle);
            var wp = psParts.Where(x => x.DiskNumber == d.Number).OrderBy(x => x.Offset).ToList();
            Assert.Equal(wp.Count, d.Partitions.Count);
            for (var i = 0; i < wp.Count; i++)
            {
                Assert.Equal(wp[i].PartitionNumber, d.Partitions[i].PartitionNumber);
                Assert.Equal(wp[i].Offset, d.Partitions[i].Offset);
                Assert.Equal(wp[i].Size, d.Partitions[i].Size);
                var letter = string.IsNullOrWhiteSpace(wp[i].DriveLetter) || wp[i].DriveLetter == "\0" ? null : wp[i].DriveLetter;
                Assert.Equal(letter, d.Partitions[i].DriveLetter?.ToString());
                if (!string.IsNullOrEmpty(wp[i].GptType)) Assert.Equal(wp[i].GptType, d.Partitions[i].GptType, ignoreCase: true);
            }
        }
    }

    [Fact]
    public void Inventory_matches_get_disk_and_get_partition()
    {
        var disks = StorageInventory.GetDisks();
        Assert.NotEmpty(disks);
        AssertMatchesWindows(disks);
        // The Windows partition is tagged.
        var system = Path.GetPathRoot(Environment.SystemDirectory)![0];
        Assert.Contains(disks.SelectMany(d => d.Partitions), p => p.DriveLetter == system && p.Role == PartitionRole.Windows);
    }

    [Fact]
    public void Gaps_ignore_alignment_and_find_unallocated_space()
    {
        const long mb = 1024 * 1024;
        var parts = new List<PartitionInfo>
        {
            new() { Offset = 1 * mb, Size = 100 * mb },
            new() { Offset = 101 * mb + 512 * 1024, Size = 200 * mb }, // half-MiB alignment gap: ignored
        };
        var gaps = StorageInventory.Gaps(0, 1024 * mb, parts);
        var gap = Assert.Single(gaps);
        Assert.Equal(1024 * mb - (301 * mb + 512 * 1024), gap.Size);
    }

    [Fact]
    [Trait("Category", "Elevated")]
    public void Attached_test_vhd_appears_as_virtual_disk_with_its_partition()
    {
        using var vhd = TestVhd.Create(300, "NTFS", "DPTINV");
        var disks = StorageInventory.GetDisks();
        var disk = disks.Single(d => d.Number == vhd.DiskNumber);
        Assert.True(disk.IsVirtual);
        Assert.Equal("GPT", disk.PartitionStyle);
        Assert.Contains(disk.Partitions, p => p.DriveLetter == vhd.Letter && p.Label == "DPTINV" && p.FileSystem == "NTFS");
        AssertMatchesWindows(disks, d => d.Number == vhd.DiskNumber);
    }
}
