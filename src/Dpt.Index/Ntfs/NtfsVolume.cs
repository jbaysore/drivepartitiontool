using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Dpt.Index.Native;
using Microsoft.Win32.SafeHandles;

namespace Dpt.Index.Ntfs;

public sealed record UsnChange(ulong FileRef, ulong ParentRef, string Name, uint Reason, uint Attributes);

/// <summary>
/// Reads an NTFS volume's Master File Table directly (names, parents, sizes, times in one sequential pass) and tails its
/// USN change journal. Requires administrator/SYSTEM rights. Read-only: the volume is opened for reading only.
/// </summary>
public sealed unsafe class NtfsVolume : IDisposable
{
    public const ulong RootRecord = 5;
    private const int FirstUserRecord = 16;

    public const uint UsnReasonDataOverwrite = 0x00000001;
    public const uint UsnReasonDataExtend = 0x00000002;
    public const uint UsnReasonDataTruncation = 0x00000004;
    public const uint UsnReasonFileCreate = 0x00000100;
    public const uint UsnReasonFileDelete = 0x00000200;
    public const uint UsnReasonRenameOldName = 0x00001000;
    public const uint UsnReasonRenameNewName = 0x00002000;
    public const uint UsnReasonBasicInfoChange = 0x00008000;
    public const uint UsnReasonHardLinkChange = 0x00010000;
    public const uint UsnReasonClose = 0x80000000;

    private readonly SafeFileHandle _volume;
    private readonly SafeFileHandle _rootHandle;
    private Win32.NTFS_VOLUME_DATA_BUFFER _data;

    public NtfsVolume(string root)
    {
        Root = root.EndsWith('\\') ? root : root + "\\";
        var device = @"\\.\" + Root.TrimEnd('\\');
        _volume = Win32.CreateFile(device, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (_volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Can't open {device} (administrator rights are needed).");
        _rootHandle = Win32.CreateFile(Root, Win32.FILE_READ_ATTRIBUTES, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        fixed (Win32.NTFS_VOLUME_DATA_BUFFER* p = &_data)
        {
            if (!Win32.DeviceIoControl(_volume, Win32.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, p, sizeof(Win32.NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Not an NTFS volume.");
        }
    }

    public string Root { get; }
    public uint BytesPerCluster => _data.BytesPerCluster;
    public long RecordCount => _data.MftValidDataLength / _data.BytesPerFileRecordSegment;

    /// <summary>Reads the whole MFT into <paramref name="index"/> (bulk load), reporting progress 0..1.</summary>
    public void LoadInto(VolumeIndex index, IProgress<double>? progress, CancellationToken ct)
    {
        var recordSize = (int)_data.BytesPerFileRecordSegment;
        var sector = (int)_data.BytesPerSector;
        var cluster = (long)_data.BytesPerCluster;
        var total = RecordCount;

        // Record 0 describes the $MFT itself; its $DATA run list says where the rest of the table lives.
        var first = new byte[Align(recordSize, sector)];
        ReadAt(_data.MftStartLcn * cluster, first);
        if (!MftRecordParser.ApplyFixups(first.AsSpan(0, recordSize), sector)) throw new InvalidDataException("MFT record 0 is damaged.");
        var runs = MftRecordParser.DecodeRuns(MftRecordParser.FindDataRuns(first.AsSpan(0, recordSize)));

        var names = new string?[total];
        var parents = new ulong[total];
        var dirs = new bool[total];
        var attrs = new uint[total];
        var sizes = new long[total];
        var allocs = new long[total];
        var times = new long[total];
        var inUse = new bool[total];
        var nsPref = new byte[total];

        const int chunkBytes = 4 * 1024 * 1024;
        var buffer = new byte[chunkBytes];
        long record = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var runBytes = clusters * cluster;
            long done = 0;
            while (done < runBytes && record < total)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(chunkBytes, runBytes - done);
                toRead -= toRead % recordSize;
                if (toRead <= 0) break;
                var span = buffer.AsSpan(0, toRead);
                if (lcn < 0) span.Clear();
                else ReadAt(lcn * cluster + done, span);
                for (var off = 0; off + recordSize <= toRead && record < total; off += recordSize, record++)
                {
                    var rec = span.Slice(off, recordSize);
                    if (record < FirstUserRecord && record != (long)RootRecord) continue;
                    if (!MftRecordParser.ApplyFixups(rec, sector)) continue;
                    if (!MftRecordParser.TryParse(rec, (ulong)record, out var info)) continue;
                    var b = (long)info.BaseRecord;
                    if (b >= total) continue;
                    if (info.IsBase)
                    {
                        inUse[b] = true;
                        dirs[b] = info.IsDirectory;
                        if (info.HasStandardInfo)
                        {
                            attrs[b] = info.Attributes;
                            times[b] = info.ModifiedUtcTicks;
                        }
                    }

                    if (info.HasName && (names[b] is null || (nsPref[b] == 2 && info.NameNamespace != 2)))
                    {
                        names[b] = info.Name;
                        parents[b] = info.ParentRecord;
                        nsPref[b] = info.NameNamespace;
                        if (times[b] == 0) times[b] = info.ModifiedUtcTicks;
                    }

                    if (info.HasData)
                    {
                        sizes[b] = info.DataSize;
                        allocs[b] = info.AllocatedSize;
                    }
                }

                done += toRead;
                progress?.Report(0.95 * record / total);
            }
        }

        index.BeginBulk((int)Math.Min(total, int.MaxValue));
        for (long r = 0; r < total; r++)
        {
            if (!inUse[r] || names[r] is null) continue;
            if (r < FirstUserRecord && r != (long)RootRecord) continue;
            index.BulkAdd((ulong)r, r == (long)RootRecord ? RootRecord : parents[r], names[r], dirs[r], attrs[r],
                dirs[r] ? 0 : sizes[r], dirs[r] ? 0 : allocs[r], times[r]);
        }

        index.SetRoot(RootRecord);
        index.EndBulk();
        progress?.Report(1);
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    private void ReadAt(long offset, Span<byte> buffer)
    {
        var read = RandomAccess.Read(_volume, buffer, offset);
        if (read != buffer.Length) throw new IOException($"Short read at {offset} on {Root}.");
    }

    // ---------------- USN journal ----------------

    internal Win32.USN_JOURNAL_DATA_V0 QueryJournal(bool createIfMissing)
    {
        Win32.USN_JOURNAL_DATA_V0 j;
        if (Win32.DeviceIoControl(_volume, Win32.FSCTL_QUERY_USN_JOURNAL, null, 0, &j, sizeof(Win32.USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
            return j;
        var err = Marshal.GetLastWin32Error();
        if (err != Win32.ERROR_JOURNAL_NOT_ACTIVE || !createIfMissing) throw new Win32Exception(err, "Can't query the change journal.");
        // Creating a journal only enables Windows' own change log (metadata); it doesn't change any files.
        var create = new Win32.CREATE_USN_JOURNAL_DATA { MaximumSize = 32UL * 1024 * 1024, AllocationDelta = 4UL * 1024 * 1024 };
        if (!Win32.DeviceIoControl(_volume, Win32.FSCTL_CREATE_USN_JOURNAL, &create, sizeof(Win32.CREATE_USN_JOURNAL_DATA), null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't create the change journal.");
        return QueryJournal(false);
    }

    /// <summary>Reads journal records after <paramref name="startUsn"/>. Returns the next USN; throws on journal reset/wrap.</summary>
    public long ReadChanges(ulong journalId, long startUsn, List<UsnChange> into)
    {
        var buffer = new byte[256 * 1024];
        var usn = startUsn;
        while (true)
        {
            var req = new Win32.READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = usn,
                ReasonMask = 0xFFFFFFFF,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = journalId,
            };
            int returned;
            fixed (byte* p = buffer)
            {
                if (!Win32.DeviceIoControl(_volume, Win32.FSCTL_READ_USN_JOURNAL, &req, sizeof(Win32.READ_USN_JOURNAL_DATA_V0), p, buffer.Length, out returned, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == Win32.ERROR_HANDLE_EOF) return usn;
                    throw new JournalResetException(err);
                }
            }

            if (returned < 8) return usn;
            var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            var offset = 8;
            while (offset + 60 <= returned)
            {
                var span = buffer.AsSpan(offset);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
                if (length == 0 || offset + length > returned) break;
                var major = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
                if (major == 2)
                {
                    var frn = MftRecordParser.RecordNumber(BinaryPrimitives.ReadUInt64LittleEndian(span[8..]));
                    var parent = MftRecordParser.RecordNumber(BinaryPrimitives.ReadUInt64LittleEndian(span[16..]));
                    var reason = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[52..]);
                    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
                    var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[58..]);
                    var name = new string(MemoryMarshal.Cast<byte, char>(span.Slice(nameOffset, nameLength)));
                    into.Add(new UsnChange(frn, parent, name, reason, attributes));
                }

                offset += length;
            }

            if (next == usn) return usn;
            usn = next;
            if (returned < buffer.Length / 2) return usn;
        }
    }

    /// <summary>Current size/time/attributes of a file by its MFT record number, or null if it no longer exists.</summary>
    public (bool IsDirectory, uint Attributes, long Size, long Allocated, long ModifiedUtcTicks)? Stat(ulong record)
    {
        var id = new Win32.FILE_ID_DESCRIPTOR { dwSize = (uint)sizeof(Win32.FILE_ID_DESCRIPTOR), Type = 0, FileId = (long)record };
        using var h = Win32.OpenFileById(_rootHandle, ref id, Win32.FILE_READ_ATTRIBUTES,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.FILE_FLAG_BACKUP_SEMANTICS);
        if (h.IsInvalid) return null;
        Win32.FILE_BASIC_INFO basic;
        Win32.FILE_STANDARD_INFO standard;
        if (!Win32.GetFileInformationByHandleEx(h, Win32.FileBasicInfo, &basic, sizeof(Win32.FILE_BASIC_INFO))) return null;
        if (!Win32.GetFileInformationByHandleEx(h, Win32.FileStandardInfo, &standard, sizeof(Win32.FILE_STANDARD_INFO))) return null;
        return (standard.Directory != 0, basic.FileAttributes, standard.EndOfFile, standard.AllocationSize, MftRecordParser.FileTimeToTicks(basic.LastWriteTime));
    }

    public void Dispose()
    {
        _rootHandle.Dispose();
        _volume.Dispose();
    }
}

/// <summary>The change journal was deleted, recreated or wrapped past our position: a full re-read is needed.</summary>
public sealed class JournalResetException(int error) : Exception($"Change journal reset (error {error}).")
{
    public int Error { get; } = error;
}
