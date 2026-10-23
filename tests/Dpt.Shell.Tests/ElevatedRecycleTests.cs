using Dpt.Shell.Operations;
using Dpt.TestSupport;
using Microsoft.Win32;

namespace Dpt.Shell.Tests;

/// <summary>
/// Scope Q12/Q13: an item larger than its drive's Recycle Bin limit must not be silently deleted.
/// Runs on a throwaway VHD whose Recycle Bin limit is set to 1 MB (per-volume HKCU setting, removed afterwards).
/// </summary>
[Trait("Category", "Elevated")]
public class ElevatedRecycleTests
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, System.Text.StringBuilder name, int size);

    [Fact]
    public async Task Item_larger_than_the_recycle_bin_limit_is_blocked_not_deleted()
    {
        using var vhd = TestVhd.Create(256, "NTFS", "DPTBIN");
        var sb = new System.Text.StringBuilder(64);
        Assert.True(GetVolumeNameForVolumeMountPoint(vhd.Root, sb, sb.Capacity));
        var vol = sb.ToString();
        var guid = vol[vol.IndexOf('{')..(vol.IndexOf('}') + 1)];
        var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}";
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue("MaxCapacity", 1, RegistryValueKind.DWord); // 1 MB
            key.SetValue("NukeOnDelete", 0, RegistryValueKind.DWord);
        }

        try
        {
            var big = Path.Combine(vhd.Root, "huge-video.mkv");
            await File.WriteAllBytesAsync(big, new byte[8 * 1024 * 1024]);
            var small = Path.Combine(vhd.Root, "note.txt");
            await File.WriteAllTextAsync(small, "small");

            Assert.Equal(1L * 1024 * 1024, RecycleBin.MaxCapacityBytes(big));
            Assert.NotNull(RecycleBin.PrecheckReason(big, 8 * 1024 * 1024));

            var r = await new FileOperationEngine().RunAsync(new FileOperationRequest { Kind = FileOperationKind.Recycle, Sources = [big, small] });

            if (!File.Exists(big))
            {
                // Where did it go? Recycled anyway (recoverable) or deleted permanently (a real failure)?
                var done = r.Completed.FirstOrDefault(c => string.Equals(c.Source, big, StringComparison.OrdinalIgnoreCase));
                var inBin = Directory.EnumerateFiles(Path.Combine(vhd.Root, "$Recycle.Bin"), "$R*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                    .Where(f => new FileInfo(f).Length == 8 * 1024 * 1024).ToList();
                Assert.Fail($"The oversized file left its folder. Engine result: completed={done is not null} dest={done?.Destination ?? "none"} destExists={(done?.Destination is { } d && File.Exists(d))}; " +
                            $"blocked={r.BlockedFromRecycle.Count}; failed={string.Join(";", r.Failed.Select(f => f.Message))}; 8 MB files in this drive's Recycle Bin: {inBin.Count} ({string.Join(", ", inBin)}).");
            }

            var blocked = Assert.Single(r.BlockedFromRecycle);
            Assert.Equal(big, blocked.Path, ignoreCase: true);
            Assert.False(File.Exists(small), "The small file should have gone to the Recycle Bin.");
            Assert.Contains(r.Completed, c => string.Equals(c.Source, small, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
