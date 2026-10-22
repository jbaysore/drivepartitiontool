using System.Buffers.Binary;

namespace Dpt.Index.Ntfs;

/// <summary>What one MFT file record contributes to the catalog.</summary>
public struct MftRecordInfo
{
    /// <summary>Record number of the base record this data belongs to (itself, or the base of an extension record).</summary>
    public ulong BaseRecord;

    public bool IsBase;
    public bool InUse;
    public bool IsDirectory;
    public ulong ParentRecord;
    public string? Name;

    /// <summary>File name namespace: 0 POSIX, 1 Win32, 2 DOS (8.3 only), 3 Win32+DOS.</summary>
    public byte NameNamespace;

    public bool HasName;
    public uint Attributes;
    public bool HasStandardInfo;
    public long ModifiedUtcTicks;
    public bool HasData;
    public long DataSize;
    public long AllocatedSize;
}

/// <summary>
/// Parses raw NTFS file records (after reading them from the volume). Pure byte logic so it can be unit tested
/// with synthetic records. Reference: NTFS on-disk layout (FILE record header, attribute headers, $STANDARD_INFORMATION,
/// $FILE_NAME, $DATA).
/// </summary>
public static class MftRecordParser
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrEnd = 0xFFFFFFFF;

    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;
    private const uint FileAttributeDirectory = 0x10;

    public static ulong RecordNumber(ulong fileReference) => fileReference & 0x0000_FFFF_FFFF_FFFFUL;

    /// <summary>Applies the update-sequence fixups in place. Returns false if the record is torn/corrupt.</summary>
    public static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 48) return false;
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount == 0 || usaOffset + usaCount * 2 > record.Length) return false;
        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[sectorEnd..]) != usn) return false;
            record.Slice(usaOffset + i * 2, 2).CopyTo(record.Slice(sectorEnd, 2));
        }

        return true;
    }

    /// <summary>Parses a fixed-up record. Returns false for unused or non-FILE records.</summary>
    public static bool TryParse(ReadOnlySpan<byte> r, ulong recordNumber, out MftRecordInfo info)
    {
        info = default;
        if (r.Length < 56 || r[0] != (byte)'F' || r[1] != (byte)'I' || r[2] != (byte)'L' || r[3] != (byte)'E') return false;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(r[0x16..]);
        if ((flags & FlagInUse) == 0) return false;
        info.InUse = true;
        info.IsDirectory = (flags & FlagDirectory) != 0;
        var baseRef = BinaryPrimitives.ReadUInt64LittleEndian(r[0x20..]);
        info.IsBase = RecordNumber(baseRef) == 0;
        info.BaseRecord = info.IsBase ? recordNumber : RecordNumber(baseRef);

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]);
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(r[0x18..]), (uint)r.Length);
        while (offset + 16 <= used)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(r[offset..]);
            if (type == AttrEnd) break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(offset + 4)..]);
            if (length <= 0 || offset + length > used) break;
            var a = r.Slice(offset, length);
            var nonResident = a[8] != 0;
            var nameLength = a[9];

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                {
                    var v = ResidentValue(a);
                    if (v.Length >= 0x24)
                    {
                        info.HasStandardInfo = true;
                        info.ModifiedUtcTicks = FileTimeToTicks(BinaryPrimitives.ReadInt64LittleEndian(v[0x08..]));
                        info.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(v[0x20..]);
                    }

                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var v = ResidentValue(a);
                    if (v.Length < 0x42) break;
                    var ns = v[0x41];
                    var len = v[0x40];
                    if (0x42 + len * 2 > v.Length) break;
                    // Prefer Win32/POSIX names over DOS 8.3 short names; keep the first good one.
                    var better = !info.HasName || (info.NameNamespace == 2 && ns != 2);
                    if (!better) break;
                    info.HasName = true;
                    info.NameNamespace = ns;
                    info.ParentRecord = RecordNumber(BinaryPrimitives.ReadUInt64LittleEndian(v));
                    info.Name = new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(v.Slice(0x42, len * 2)));
                    if (!info.HasStandardInfo) info.ModifiedUtcTicks = FileTimeToTicks(BinaryPrimitives.ReadInt64LittleEndian(v[0x10..]));
                    break;
                }
                case AttrData when nameLength == 0:
                {
                    if (nonResident)
                    {
                        if (a.Length < 0x40) break;
                        var startVcn = BinaryPrimitives.ReadInt64LittleEndian(a[0x10..]);
                        if (startVcn != 0) break; // later fragments of an attribute list carry no sizes
                        info.HasData = true;
                        info.AllocatedSize = BinaryPrimitives.ReadInt64LittleEndian(a[0x28..]);
                        info.DataSize = BinaryPrimitives.ReadInt64LittleEndian(a[0x30..]);
                    }
                    else
                    {
                        info.HasData = true;
                        info.DataSize = BinaryPrimitives.ReadUInt32LittleEndian(a[0x10..]);
                        info.AllocatedSize = 0; // stored inside the MFT record itself
                    }

                    break;
                }
            }

            offset += length;
        }

        if (info.HasStandardInfo && (info.Attributes & FileAttributeDirectory) != 0) info.IsDirectory = true;
        return true;
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attribute)
    {
        if (attribute.Length < 0x18) return [];
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        var off = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        return off + len <= attribute.Length ? attribute.Slice(off, len) : [];
    }

    /// <summary>Decodes a non-resident attribute's run list into (starting cluster, cluster count) pairs.</summary>
    public static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var list = new List<(long, long)>();
        long lcn = 0;
        var i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            var header = runs[i++];
            var lenSize = header & 0x0F;
            var offSize = header >> 4;
            if (i + lenSize + offSize > runs.Length) break;
            long length = 0;
            for (var k = 0; k < lenSize; k++) length |= (long)runs[i + k] << (8 * k);
            i += lenSize;
            long delta = 0;
            if (offSize > 0)
            {
                for (var k = 0; k < offSize; k++) delta |= (long)runs[i + k] << (8 * k);
                if ((runs[i + offSize - 1] & 0x80) != 0) delta |= -1L << (8 * offSize); // sign-extend
                i += offSize;
                lcn += delta;
                list.Add((lcn, length));
            }
            else
            {
                list.Add((-1, length)); // sparse run
            }
        }

        return list;
    }

    /// <summary>Finds the unnamed $DATA attribute's run list in a (fixed-up) record, e.g. record 0 ($MFT).</summary>
    public static ReadOnlySpan<byte> FindDataRuns(ReadOnlySpan<byte> r)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]);
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(r[0x18..]), (uint)r.Length);
        while (offset + 16 <= used)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(r[offset..]);
            if (type == AttrEnd) break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(offset + 4)..]);
            if (length <= 0 || offset + length > used) break;
            var a = r.Slice(offset, length);
            if (type == AttrData && a[8] != 0 && a[9] == 0)
            {
                var runOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[0x20..]);
                return a[runOffset..];
            }

            offset += length;
        }

        return [];
    }

    public static long FileTimeToTicks(long fileTime)
    {
        // FILETIME counts 100 ns since 1601-01-01 UTC; DateTime ticks count from 0001-01-01.
        const long epochDelta = 504911232000000000L;
        return fileTime <= 0 || fileTime > DateTime.MaxValue.Ticks - epochDelta ? 0 : fileTime + epochDelta;
    }
}
