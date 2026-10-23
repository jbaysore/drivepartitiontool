using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace Dpt.DiskWorker.Clone;

/// <summary>Raw access to physical disks and volumes (sector-aligned reads/writes).</summary>
internal static unsafe class RawIo
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint SHARE_RW = 3, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr overlapped);

    public static SafeFileHandle Open(string device, bool write)
    {
        var h = CreateFile(device, GENERIC_READ | (write ? GENERIC_WRITE : 0), SHARE_RW, IntPtr.Zero, OPEN_EXISTING,
            write ? FILE_FLAG_WRITE_THROUGH : 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Can't open {device}.");
        return h;
    }

    public static string PhysicalDrive(int number) => $@"\\.\PhysicalDrive{number}";

    /// <summary>Bytes per cluster and total clusters of an NTFS volume handle.</summary>
    public static (long BytesPerCluster, long TotalClusters) NtfsGeometry(SafeFileHandle volume)
    {
        var buf = stackalloc byte[128];
        if (!DeviceIoControl(volume, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, buf, 128, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Not an NTFS volume.");
        var totalClusters = *(long*)(buf + 16);
        var bytesPerCluster = *(uint*)(buf + 44);
        return (bytesPerCluster, totalClusters);
    }

    /// <summary>Runs of in-use clusters (start, count) from the volume's allocation bitmap.</summary>
    public static IEnumerable<(long Lcn, long Count)> UsedClusterRuns(SafeFileHandle volume)
    {
        const int outSize = 1 << 20;
        var buffer = new byte[outSize];
        long start = 0;
        long runStart = -1;
        while (true)
        {
            var (returned, err) = BitmapChunk(volume, start, buffer);

            var startingLcn = BitConverter.ToInt64(buffer, 0);
            var bitmapSize = BitConverter.ToInt64(buffer, 8); // clusters from startingLcn to the end of the volume
            var bitsHere = Math.Min(bitmapSize, (long)(returned - 16) * 8);
            for (long i = 0; i < bitsHere; i++)
            {
                var used = (buffer[16 + (i >> 3)] & (1 << (int)(i & 7))) != 0;
                var lcn = startingLcn + i;
                if (used && runStart < 0) runStart = lcn;
                else if (!used && runStart >= 0)
                {
                    yield return (runStart, lcn - runStart);
                    runStart = -1;
                }
            }

            start = startingLcn + bitsHere;
            if (err != ERROR_MORE_DATA || bitsHere == 0)
            {
                if (runStart >= 0) yield return (runStart, start - runStart);
                yield break;
            }
        }
    }

    private static (int Returned, int Error) BitmapChunk(SafeFileHandle volume, long start, byte[] buffer)
    {
        fixed (byte* outp = buffer)
        {
            var input = start;
            var ok = DeviceIoControl(volume, FSCTL_GET_VOLUME_BITMAP, &input, 8, outp, buffer.Length, out var returned, IntPtr.Zero);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != ERROR_MORE_DATA) throw new Win32Exception(err, "Can't read the volume's allocation bitmap.");
            return (returned, err);
        }
    }

    /// <summary>Copies <paramref name="length"/> bytes from one handle to another in large sector-aligned chunks.</summary>
    public static void Copy(SafeFileHandle from, long fromOffset, SafeFileHandle to, long toOffset, long length, byte[] buffer, Action<long>? copied = null)
    {
        long done = 0;
        while (done < length)
        {
            var n = (int)Math.Min(buffer.Length, length - done);
            var read = RandomAccess.Read(from, buffer.AsSpan(0, n), fromOffset + done);
            if (read != n) throw new IOException($"Short read at {fromOffset + done} ({read} of {n} bytes).");
            RandomAccess.Write(to, buffer.AsSpan(0, n), toOffset + done);
            done += n;
            copied?.Invoke(n);
        }
    }

    /// <summary>Compares two regions byte by byte (verification).</summary>
    public static bool Equal(SafeFileHandle a, long aOffset, SafeFileHandle b, long bOffset, long length)
    {
        var x = new byte[1 << 20];
        var y = new byte[1 << 20];
        long done = 0;
        while (done < length)
        {
            var n = (int)Math.Min(x.Length, length - done);
            if (RandomAccess.Read(a, x.AsSpan(0, n), aOffset + done) != n) return false;
            if (RandomAccess.Read(b, y.AsSpan(0, n), bOffset + done) != n) return false;
            if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
            done += n;
        }

        return true;
    }
}

/// <summary>A Volume Shadow Copy snapshot of one volume (consistent point-in-time view of a live volume). Deleted on dispose.</summary>
internal sealed class ShadowCopy : IDisposable
{
    private readonly CimSession _session = CimSession.Create(null);
    private readonly string _id;

    private ShadowCopy(string id, string device)
    {
        _id = id;
        Device = device;
    }

    /// <summary>Device path, e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12.</summary>
    public string Device { get; }

    public static ShadowCopy Create(string volumePath)
    {
        using var session = CimSession.Create(null);
        var cls = session.GetClass(@"root\cimv2", "Win32_ShadowCopy");
        var args = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Volume", volumePath.EndsWith('\\') ? volumePath : volumePath + "\\", CimType.String, CimFlags.In),
            CimMethodParameter.Create("Context", "ClientAccessible", CimType.String, CimFlags.In),
        };
        var r = session.InvokeMethod(@"root\cimv2", "Win32_ShadowCopy", "Create", args);
        var code = Convert.ToUInt32(r.ReturnValue.Value);
        if (code != 0) throw new IOException($"Windows couldn't take a snapshot of the volume (Volume Shadow Copy error {code}).");
        var id = (string)r.OutParameters["ShadowID"].Value;
        var shadow = session.QueryInstances(@"root\cimv2", "WQL", $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{id}'").First();
        _ = cls;
        return new ShadowCopy(id, (string)shadow.CimInstanceProperties["DeviceObject"].Value);
    }

    public void Dispose()
    {
        try
        {
            foreach (var s in _session.QueryInstances(@"root\cimv2", "WQL", $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{_id}'"))
                _session.DeleteInstance(@"root\cimv2", s);
        }
        catch (CimException)
        {
        }

        _session.Dispose();
    }
}
