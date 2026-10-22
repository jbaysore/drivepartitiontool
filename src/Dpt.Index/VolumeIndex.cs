using System.Runtime.InteropServices;

namespace Dpt.Index;

/// <summary>
/// In-memory catalog of one volume: a compact array of entries plus one shared name buffer. Folders keep rolled-up
/// totals (size, size on disk, file count) of everything beneath them. One writer, many readers (<see cref="Lock"/>).
/// </summary>
public sealed class VolumeIndex
{
    public const int NoEntry = -1;

    [Flags]
    internal enum EntryFlags : byte
    {
        InUse = 1,
        Directory = 2,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Entry
    {
        public ulong Ref;
        public int Parent;
        public int FirstChild;
        public int NextSibling;
        public int NameStart;
        public ushort NameLength;
        public EntryFlags Flags;
        public uint Attributes;
        public long Size;
        public long SizeOnDisk;
        public long ModifiedUtcTicks;
        public long TotalSize;
        public long TotalOnDisk;
        public long FileCount;

        public readonly bool InUse => (Flags & EntryFlags.InUse) != 0;
        public readonly bool IsDirectory => (Flags & EntryFlags.Directory) != 0;
    }

    internal Entry[] Entries = new Entry[1024];
    internal char[] Names = new char[16 * 1024];
    private int _count;
    private int _namesLength;
    private readonly Dictionary<ulong, int> _byRef = new();
    private readonly Stack<int> _free = new();
    private ulong[]? _bulkParents;

    public VolumeIndex(string root, uint serial, string fileSystem, string label)
    {
        Root = root.EndsWith('\\') ? root : root + "\\";
        Serial = serial;
        FileSystem = fileSystem;
        Label = label;
    }

    public string Root { get; }
    public uint Serial { get; }
    public string FileSystem { get; }
    public string Label { get; }
    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.SupportsRecursion);
    public int RootIndex { get; private set; } = NoEntry;
    public ulong RootRef { get; private set; }
    public int Capacity => _count;
    public long LiveCount { get; private set; }
    public DateTime LastUpdatedUtc { get; set; }

    /// <summary>NTFS journal position this index reflects (MFT mode).</summary>
    public ulong JournalId { get; set; }

    public long NextUsn { get; set; }

    internal ReadOnlySpan<char> NameOf(int i) => Names.AsSpan(Entries[i].NameStart, Entries[i].NameLength);

    public string Name(int i) => NameOf(i).ToString();
    public bool IsDirectory(int i) => Entries[i].IsDirectory;
    public bool InUse(int i) => i >= 0 && i < _count && Entries[i].InUse;
    public long Size(int i) => Entries[i].IsDirectory ? Entries[i].TotalSize : Entries[i].Size;
    public long SizeOnDisk(int i) => Entries[i].IsDirectory ? Entries[i].TotalOnDisk : Entries[i].SizeOnDisk;
    public long FileCount(int i) => Entries[i].FileCount;
    public uint Attributes(int i) => Entries[i].Attributes;
    public DateTime ModifiedUtc(int i) => new(Entries[i].ModifiedUtcTicks, DateTimeKind.Utc);
    public int Parent(int i) => Entries[i].Parent;
    public ulong RefOf(int i) => Entries[i].Ref;

    public int IndexOf(ulong fileRef) => _byRef.TryGetValue(fileRef, out var i) ? i : NoEntry;

    // ---------------- Bulk load (MFT/scan) ----------------

    public void BeginBulk(int expected)
    {
        Clear();
        EnsureCapacity(Math.Max(expected, 1024));
        _bulkParents = new ulong[Entries.Length];
    }

    /// <summary>Adds an entry whose parent may not be loaded yet; parents are linked in <see cref="EndBulk"/>.</summary>
    public void BulkAdd(ulong fileRef, ulong parentRef, ReadOnlySpan<char> name, bool isDirectory, uint attributes, long size, long sizeOnDisk, long modifiedUtcTicks)
    {
        var i = NewSlot();
        if (_bulkParents!.Length < Entries.Length) Array.Resize(ref _bulkParents, Entries.Length);
        _bulkParents[i] = parentRef;
        Set(i, fileRef, name, isDirectory, attributes, size, sizeOnDisk, modifiedUtcTicks);
        _byRef[fileRef] = i;
    }

    public void SetRoot(ulong rootRef)
    {
        RootRef = rootRef;
        RootIndex = IndexOf(rootRef);
    }

    /// <summary>Links parents/children (dropping orphans) and computes folder totals.</summary>
    public void EndBulk()
    {
        var parents = _bulkParents!;
        _bulkParents = null;
        if (RootIndex == NoEntry) throw new InvalidOperationException("Root entry was not loaded.");
        for (var i = 0; i < _count; i++)
        {
            ref var e = ref Entries[i];
            if (!e.InUse || i == RootIndex) continue;
            var p = IndexOf(parents[i]);
            if (p == NoEntry || p == i || !Entries[p].IsDirectory)
            {
                // Orphan (parent not in use, e.g. a metafile or a deleted folder's leftovers).
                e.Flags &= ~EntryFlags.InUse;
                _byRef.Remove(e.Ref);
                _free.Push(i);
                continue;
            }

            e.Parent = p;
            e.NextSibling = Entries[p].FirstChild;
            Entries[p].FirstChild = i;
        }

        Entries[RootIndex].Parent = NoEntry;
        // Drop entries not reachable from the root (cycles from stale records).
        RecomputeTotals();
    }

    /// <summary>Recomputes every folder's totals from scratch (iterative post-order walk).</summary>
    public void RecomputeTotals()
    {
        long live = 0;
        var reachable = new bool[_count];
        if (RootIndex != NoEntry)
        {
            var order = new List<int>(_count);
            var stack = new Stack<int>();
            stack.Push(RootIndex);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                if (reachable[i]) continue;
                reachable[i] = true;
                order.Add(i);
                for (var c = Entries[i].FirstChild; c != NoEntry; c = Entries[c].NextSibling) stack.Push(c);
            }

            for (var k = order.Count - 1; k >= 0; k--)
            {
                var i = order[k];
                ref var e = ref Entries[i];
                if (e.IsDirectory)
                {
                    long size = 0, disk = 0, files = 0;
                    for (var c = e.FirstChild; c != NoEntry; c = Entries[c].NextSibling)
                    {
                        ref var ce = ref Entries[c];
                        if (ce.IsDirectory)
                        {
                            size += ce.TotalSize;
                            disk += ce.TotalOnDisk;
                            files += ce.FileCount;
                        }
                        else
                        {
                            size += ce.Size;
                            disk += ce.SizeOnDisk;
                            files++;
                        }
                    }

                    e.TotalSize = size;
                    e.TotalOnDisk = disk;
                    e.FileCount = files;
                }
                else
                {
                    e.TotalSize = e.Size;
                    e.TotalOnDisk = e.SizeOnDisk;
                }
            }

            live = order.Count;
        }

        for (var i = 0; i < _count; i++)
        {
            if (Entries[i].InUse && !reachable[i])
            {
                Entries[i].Flags &= ~EntryFlags.InUse;
                _byRef.Remove(Entries[i].Ref);
                _free.Push(i);
            }
        }

        LiveCount = live;
    }

    // ---------------- Incremental updates (journal/watcher) ----------------

    /// <summary>Adds or updates an entry, moving it if its parent changed and keeping all folder totals correct.</summary>
    public int Upsert(ulong fileRef, ulong parentRef, ReadOnlySpan<char> name, bool isDirectory, uint attributes, long size, long sizeOnDisk, long modifiedUtcTicks)
    {
        var parent = IndexOf(parentRef);
        if (parent == NoEntry || !Entries[parent].IsDirectory) return NoEntry;
        var i = IndexOf(fileRef);
        if (i == NoEntry)
        {
            i = NewSlot();
            Set(i, fileRef, name, isDirectory, attributes, size, sizeOnDisk, modifiedUtcTicks);
            _byRef[fileRef] = i;
            ref var e = ref Entries[i];
            e.TotalSize = isDirectory ? 0 : size;
            e.TotalOnDisk = isDirectory ? 0 : sizeOnDisk;
            e.FileCount = 0;
            Link(i, parent);
            AddToAncestors(parent, isDirectory ? 0 : size, isDirectory ? 0 : sizeOnDisk, isDirectory ? 0 : 1);
            LiveCount++;
            return i;
        }

        ref var cur = ref Entries[i];
        if (!NameOf(i).SequenceEqual(name)) SetName(i, name);
        cur.Attributes = attributes;
        cur.ModifiedUtcTicks = modifiedUtcTicks;
        if (cur.Parent != parent && i != RootIndex && !IsAncestor(i, parent))
        {
            var (ts, td, tf) = Contribution(i);
            AddToAncestors(cur.Parent, -ts, -td, -tf);
            Unlink(i);
            Link(i, parent);
            AddToAncestors(parent, ts, td, tf);
        }

        if (!cur.IsDirectory && !isDirectory && (cur.Size != size || cur.SizeOnDisk != sizeOnDisk))
        {
            var ds = size - cur.Size;
            var dd = sizeOnDisk - cur.SizeOnDisk;
            cur.Size = size;
            cur.SizeOnDisk = sizeOnDisk;
            cur.TotalSize = size;
            cur.TotalOnDisk = sizeOnDisk;
            AddToAncestors(cur.Parent, ds, dd, 0);
        }

        return i;
    }

    /// <summary>Removes an entry and everything under it.</summary>
    public void Remove(ulong fileRef)
    {
        var i = IndexOf(fileRef);
        if (i == NoEntry || i == RootIndex) return;
        var (ts, td, tf) = Contribution(i);
        AddToAncestors(Entries[i].Parent, -ts, -td, -tf);
        Unlink(i);
        var stack = new Stack<int>();
        stack.Push(i);
        while (stack.Count > 0)
        {
            var k = stack.Pop();
            for (var c = Entries[k].FirstChild; c != NoEntry; c = Entries[c].NextSibling) stack.Push(c);
            Entries[k].Flags &= ~EntryFlags.InUse;
            Entries[k].FirstChild = NoEntry;
            _byRef.Remove(Entries[k].Ref);
            _free.Push(k);
            LiveCount--;
        }
    }

    private (long Size, long Disk, long Files) Contribution(int i)
    {
        ref var e = ref Entries[i];
        return e.IsDirectory ? (e.TotalSize, e.TotalOnDisk, e.FileCount) : (e.Size, e.SizeOnDisk, 1);
    }

    private void AddToAncestors(int start, long size, long disk, long files)
    {
        for (var p = start; p != NoEntry; p = Entries[p].Parent)
        {
            Entries[p].TotalSize += size;
            Entries[p].TotalOnDisk += disk;
            Entries[p].FileCount += files;
        }
    }

    private bool IsAncestor(int candidateAncestor, int of)
    {
        for (var p = of; p != NoEntry; p = Entries[p].Parent)
            if (p == candidateAncestor) return true;
        return false;
    }

    private void Link(int i, int parent)
    {
        Entries[i].Parent = parent;
        Entries[i].NextSibling = Entries[parent].FirstChild;
        Entries[parent].FirstChild = i;
    }

    private void Unlink(int i)
    {
        var p = Entries[i].Parent;
        if (p == NoEntry) return;
        if (Entries[p].FirstChild == i)
        {
            Entries[p].FirstChild = Entries[i].NextSibling;
        }
        else
        {
            for (var c = Entries[p].FirstChild; c != NoEntry; c = Entries[c].NextSibling)
            {
                if (Entries[c].NextSibling != i) continue;
                Entries[c].NextSibling = Entries[i].NextSibling;
                break;
            }
        }

        Entries[i].Parent = NoEntry;
        Entries[i].NextSibling = NoEntry;
    }

    // ---------------- Paths ----------------

    public string PathOf(int i)
    {
        if (i == RootIndex) return Root;
        Span<int> chain = stackalloc int[256];
        var depth = 0;
        for (var p = i; p != NoEntry && p != RootIndex && depth < chain.Length; p = Entries[p].Parent) chain[depth++] = p;
        var length = Root.Length;
        for (var k = 0; k < depth; k++) length += Entries[chain[k]].NameLength + 1;
        return string.Create(length - 1, (this, depth, chain.ToArray()), static (span, state) =>
        {
            var (index, d, ch) = state;
            index.Root.AsSpan().CopyTo(span);
            var pos = index.Root.Length;
            for (var k = d - 1; k >= 0; k--)
            {
                var name = index.NameOf(ch[k]);
                name.CopyTo(span[pos..]);
                pos += name.Length;
                if (k > 0) span[pos++] = '\\';
            }
        });
    }

    /// <summary>Finds an entry by full path (case-insensitive), or <see cref="NoEntry"/>.</summary>
    public int Find(string path)
    {
        if (RootIndex == NoEntry) return NoEntry;
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path.TrimEnd('\\') + "\\", Root, StringComparison.OrdinalIgnoreCase)) return NoEntry;
        var rest = path.Length > Root.Length ? path.AsSpan(Root.Length).TrimEnd('\\') : [];
        var current = RootIndex;
        while (rest.Length > 0)
        {
            var slash = rest.IndexOf('\\');
            var part = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? [] : rest[(slash + 1)..];
            var found = NoEntry;
            for (var c = Entries[current].FirstChild; c != NoEntry; c = Entries[c].NextSibling)
            {
                if (NameOf(c).Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    found = c;
                    break;
                }
            }

            if (found == NoEntry) return NoEntry;
            current = found;
        }

        return current;
    }

    public IEnumerable<int> Children(int i)
    {
        for (var c = Entries[i].FirstChild; c != NoEntry; c = Entries[c].NextSibling) yield return c;
    }

    /// <summary>True when <paramref name="i"/> is <paramref name="ancestor"/> or below it.</summary>
    public bool IsUnder(int i, int ancestor)
    {
        for (var p = i; p != NoEntry; p = Entries[p].Parent)
            if (p == ancestor) return true;
        return false;
    }

    /// <summary>True when the entry or any folder above it is hidden or system (e.g. AppData).</summary>
    public bool IsInHiddenTree(int i, uint mask)
    {
        for (var p = i; p != NoEntry && p != RootIndex; p = Entries[p].Parent)
            if ((Entries[p].Attributes & mask) != 0) return true;
        return false;
    }

    // ---------------- Storage ----------------

    private int NewSlot()
    {
        if (_free.Count > 0) return _free.Pop();
        EnsureCapacity(_count + 1);
        return _count++;
    }

    private void EnsureCapacity(int n)
    {
        if (Entries.Length >= n) return;
        var size = Entries.Length;
        while (size < n) size *= 2;
        Array.Resize(ref Entries, size);
    }

    private void Set(int i, ulong fileRef, ReadOnlySpan<char> name, bool isDirectory, uint attributes, long size, long sizeOnDisk, long modified)
    {
        ref var e = ref Entries[i];
        e = default;
        e.Ref = fileRef;
        e.Parent = NoEntry;
        e.FirstChild = NoEntry;
        e.NextSibling = NoEntry;
        e.Flags = EntryFlags.InUse | (isDirectory ? EntryFlags.Directory : 0);
        e.Attributes = attributes;
        e.Size = isDirectory ? 0 : size;
        e.SizeOnDisk = isDirectory ? 0 : sizeOnDisk;
        e.ModifiedUtcTicks = modified;
        SetName(i, name);
    }

    private void SetName(int i, ReadOnlySpan<char> name)
    {
        if (_namesLength + name.Length > Names.Length)
        {
            var size = Names.Length;
            while (size < _namesLength + name.Length) size *= 2;
            Array.Resize(ref Names, size);
        }

        name.CopyTo(Names.AsSpan(_namesLength));
        Entries[i].NameStart = _namesLength;
        Entries[i].NameLength = (ushort)Math.Min(name.Length, ushort.MaxValue);
        _namesLength += name.Length;
    }

    public void Clear()
    {
        _count = 0;
        _namesLength = 0;
        _byRef.Clear();
        _free.Clear();
        RootIndex = NoEntry;
        LiveCount = 0;
    }

    // ---------------- Snapshot ----------------

    private const uint SnapshotMagic = 0x44505449; // "DPTI"
    private const int SnapshotVersion = 2;

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.Unicode, leaveOpen: true);
        w.Write(SnapshotMagic);
        w.Write(SnapshotVersion);
        w.Write(Serial);
        w.Write(FileSystem);
        w.Write(JournalId);
        w.Write(NextUsn);
        w.Write(RootRef);
        w.Write(LastUpdatedUtc.Ticks);
        var live = 0;
        for (var i = 0; i < _count; i++) if (Entries[i].InUse) live++;
        w.Write(live);
        for (var i = 0; i < _count; i++)
        {
            ref var e = ref Entries[i];
            if (!e.InUse) continue;
            w.Write(e.Ref);
            w.Write(e.Parent == NoEntry ? 0UL : Entries[e.Parent].Ref);
            w.Write((byte)e.Flags);
            w.Write(e.Attributes);
            w.Write(e.Size);
            w.Write(e.SizeOnDisk);
            w.Write(e.ModifiedUtcTicks);
            w.Write(e.NameLength);
            w.Write(MemoryMarshal.AsBytes(NameOf(i)));
        }
    }

    /// <summary>Loads a snapshot written by <see cref="Save"/>. Returns false if it doesn't match this volume.</summary>
    public bool Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.Unicode, leaveOpen: true);
        if (r.ReadUInt32() != SnapshotMagic || r.ReadInt32() != SnapshotVersion) return false;
        if (r.ReadUInt32() != Serial) return false;
        if (r.ReadString() != FileSystem) return false;
        JournalId = r.ReadUInt64();
        NextUsn = r.ReadInt64();
        var rootRef = r.ReadUInt64();
        LastUpdatedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        var count = r.ReadInt32();
        BeginBulk(count);
        var nameBuffer = new char[ushort.MaxValue];
        for (var k = 0; k < count; k++)
        {
            var fileRef = r.ReadUInt64();
            var parentRef = r.ReadUInt64();
            var flags = (EntryFlags)r.ReadByte();
            var attrs = r.ReadUInt32();
            var size = r.ReadInt64();
            var disk = r.ReadInt64();
            var modified = r.ReadInt64();
            var len = r.ReadUInt16();
            var bytes = MemoryMarshal.AsBytes(nameBuffer.AsSpan(0, len));
            if (r.Read(bytes) != bytes.Length) return false;
            BulkAdd(fileRef, parentRef, nameBuffer.AsSpan(0, len), (flags & EntryFlags.Directory) != 0, attrs, size, disk, modified);
        }

        SetRoot(rootRef);
        EndBulk();
        return true;
    }

    /// <summary>Rough memory use, for status display.</summary>
    public long ApproximateBytes => (long)Entries.Length * Marshal.SizeOf<Entry>() + Names.Length * 2L + _byRef.Count * 24L;
}
