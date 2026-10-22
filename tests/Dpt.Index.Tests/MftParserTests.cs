using System.Buffers.Binary;
using System.Text;
using Dpt.Index.Ntfs;

namespace Dpt.Index.Tests;

/// <summary>Builds synthetic NTFS FILE records for parser tests.</summary>
internal sealed class RecordBuilder
{
    private readonly List<byte[]> _attributes = [];
    public ushort Flags { get; set; } = 0x0001;
    public ulong BaseRecord { get; set; }

    public RecordBuilder StandardInfo(DateTime modifiedUtc, uint attributes)
    {
        var v = new byte[0x48];
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x08), modifiedUtc.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(0x20), attributes);
        return Resident(0x10, v);
    }

    public RecordBuilder FileName(ulong parent, string name, byte ns, ushort parentSeq = 1)
    {
        var v = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, parent | ((ulong)parentSeq << 48));
        v[0x40] = (byte)name.Length;
        v[0x41] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 0x42);
        return Resident(0x30, v);
    }

    public RecordBuilder ResidentData(int length) => Resident(0x80, new byte[length]);

    public RecordBuilder NonResidentData(long size, long allocated, long startVcn = 0, byte[]? runs = null)
    {
        runs ??= [0x11, 0x10, 0x20, 0x00]; // 16 clusters at LCN 0x20
        var a = new byte[0x40 + runs.Length + (8 - runs.Length % 8) % 8];
        BinaryPrimitives.WriteUInt32LittleEndian(a, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)a.Length);
        a[8] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x10), startVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x28), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x30), size);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x38), size);
        runs.CopyTo(a, 0x40);
        _attributes.Add(a);
        return this;
    }

    private RecordBuilder Resident(uint type, byte[] value)
    {
        var len = 0x18 + value.Length;
        len += (8 - len % 8) % 8;
        var a = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)len);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), 0x18);
        value.CopyTo(a, 0x18);
        _attributes.Add(a);
        return this;
    }

    /// <summary>1024-byte record with update-sequence protection applied (as stored on disk).</summary>
    public byte[] Build(ushort usn = 0x0042)
    {
        var r = new byte[1024];
        "FILE"u8.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 0x30); // USA offset
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 3); // USN + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38); // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x20), BaseRecord);
        var offset = 0x38;
        foreach (var a in _attributes)
        {
            a.CopyTo(r, offset);
            offset += a.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(offset), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x18), (uint)(offset + 8));
        // Update sequence: save the real last two bytes of each sector, replace them with the USN.
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x30), usn);
        for (var s = 1; s <= 2; s++)
        {
            var end = s * 512 - 2;
            r[0x30 + s * 2] = r[end];
            r[0x30 + s * 2 + 1] = r[end + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), usn);
        }

        return r;
    }
}

public class MftParserTests
{
    private static readonly DateTime When = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

    [Fact]
    public void Parses_file_with_resident_data()
    {
        var rec = new RecordBuilder().StandardInfo(When, 0x20).FileName(5, "notes.txt", 1).ResidentData(123).Build();
        Assert.True(MftRecordParser.ApplyFixups(rec, 512));
        Assert.True(MftRecordParser.TryParse(rec, 40, out var info));
        Assert.True(info.IsBase);
        Assert.Equal(40UL, info.BaseRecord);
        Assert.False(info.IsDirectory);
        Assert.Equal("notes.txt", info.Name);
        Assert.Equal(5UL, info.ParentRecord); // sequence bits stripped
        Assert.Equal(123, info.DataSize);
        Assert.Equal(When.Ticks, info.ModifiedUtcTicks);
        Assert.Equal(0x20u, info.Attributes);
    }

    [Fact]
    public void Prefers_long_name_over_dos_name()
    {
        var rec = new RecordBuilder().StandardInfo(When, 0).FileName(5, "PROGRA~1", 2).FileName(5, "Program Files", 1).Build();
        MftRecordParser.ApplyFixups(rec, 512);
        MftRecordParser.TryParse(rec, 60, out var info);
        Assert.Equal("Program Files", info.Name);
    }

    [Fact]
    public void Directory_flag_and_nonresident_sizes()
    {
        var dir = new RecordBuilder { Flags = 0x0003 }.StandardInfo(When, 0x10).FileName(5, "Videos", 1).Build();
        MftRecordParser.ApplyFixups(dir, 512);
        MftRecordParser.TryParse(dir, 70, out var d);
        Assert.True(d.IsDirectory);

        var big = new RecordBuilder().StandardInfo(When, 0).FileName(70, "movie.mkv", 3).NonResidentData(5_000_000_000, 5_000_003_584).Build();
        MftRecordParser.ApplyFixups(big, 512);
        MftRecordParser.TryParse(big, 71, out var b);
        Assert.Equal(5_000_000_000, b.DataSize);
        Assert.Equal(5_000_003_584, b.AllocatedSize);
    }

    [Fact]
    public void Extension_record_points_at_base()
    {
        var ext = new RecordBuilder { BaseRecord = 90 | (3UL << 48) }.NonResidentData(42, 4096).Build();
        MftRecordParser.ApplyFixups(ext, 512);
        MftRecordParser.TryParse(ext, 91, out var e);
        Assert.False(e.IsBase);
        Assert.Equal(90UL, e.BaseRecord);
        Assert.Equal(42, e.DataSize);
    }

    [Fact]
    public void Later_fragments_carry_no_size()
    {
        var frag = new RecordBuilder { BaseRecord = 90 }.NonResidentData(999, 999, startVcn: 100).Build();
        MftRecordParser.ApplyFixups(frag, 512);
        MftRecordParser.TryParse(frag, 92, out var f);
        Assert.False(f.HasData);
    }

    [Fact]
    public void Unused_and_torn_records_are_rejected()
    {
        var unused = new RecordBuilder { Flags = 0 }.FileName(5, "gone.txt", 1).Build();
        MftRecordParser.ApplyFixups(unused, 512);
        Assert.False(MftRecordParser.TryParse(unused, 50, out _));

        var torn = new RecordBuilder().FileName(5, "x", 1).Build();
        torn[1022] ^= 0xFF; // sector 2 trailer no longer matches the update sequence number
        Assert.False(MftRecordParser.ApplyFixups(torn, 512));
    }

    [Fact]
    public void Decodes_run_lists_with_negative_offsets_and_sparse_runs()
    {
        // run1: 0x10 clusters at LCN 0x1000; run2: 8 clusters at -0x200 relative; run3: sparse 4 clusters
        byte[] runs = [0x21, 0x10, 0x00, 0x10, 0x21, 0x08, 0x00, 0xFE, 0x01, 0x04, 0x00];
        var list = MftRecordParser.DecodeRuns(runs);
        Assert.Equal([(0x1000L, 0x10L), (0x1000L - 0x200, 8L), (-1L, 4L)], list);
    }

    [Fact]
    public void FileTime_conversion() => Assert.Equal(When.Ticks, MftRecordParser.FileTimeToTicks(When.ToFileTimeUtc()));
}
