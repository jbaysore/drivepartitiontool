namespace Dpt.Storage;

public sealed record ClonePartition(PartitionInfo Source, long Offset, long Size, bool Capture);
public sealed record CloneLayout(IReadOnlyList<ClonePartition> Partitions, bool UsesImages, long StagingBytes);

/// <summary>Pure layout/capacity calculation shared by the preview and clone worker.</summary>
public static class ClonePlanning
{
    public const long Mb = 1024 * 1024;
    private static long Align(long value) => checked((value + Mb - 1) / Mb * Mb);

    public static CloneLayout Layout(DiskInfo source, long destinationBytes, IReadOnlyDictionary<int, long>? measured = null)
    {
        if (source.PartitionStyle is not ("GPT" or "MBR") || source.Partitions.Count == 0 || destinationBytes <= 2 * Mb)
            throw new ArgumentException("Choose an initialized source with partitions and a destination large enough for its content.");
        var ordered = source.Partitions.OrderBy(p => p.Offset).ToList();
        long previous = Mb;
        foreach (var p in ordered)
        {
            if (p.Offset < previous || p.Size <= 0 || p.Offset % 512 != 0 || p.Size % 512 != 0 || p.PartitionNumber < 1)
                throw new ArgumentException("The source layout overlaps or is not supported by the sector-aligned clone path.");
            previous = checked(p.Offset + p.Size);
            if (previous > source.Size) throw new ArgumentException("The source partition extends beyond its disk.");
            if (source.PartitionStyle == "MBR" && p.MbrType is 5 or 0xF or 0x85) throw new ArgumentException("Extended MBR partitions need a separate cloning workflow.");
        }
        if (destinationBytes >= checked(previous + Mb)) return new(ordered.Select(p => new ClonePartition(p, p.Offset, p.Size, false)).ToList(), false, 0);
        long cursor = Mb, staged = 0;
        var result = new List<ClonePartition>();
        foreach (var p in ordered)
        {
            cursor = Align(cursor);
            var image = p.FileSystem?.Equals("NTFS", StringComparison.OrdinalIgnoreCase) == true && p.Role is PartitionRole.Data or PartitionRole.Windows;
            var size = p.Size;
            if (image)
            {
                if (p.Used is not { } used || used < 0 || used > p.Size || p.VolumePath is not { Length: > 0 })
                    throw new ArgumentException("The smaller destination needs readable NTFS volumes with known used sizes.");
                if (measured is not null)
                {
                    if (!measured.TryGetValue(p.PartitionNumber, out var actual) || actual < 0) throw new ArgumentException("A source snapshot measurement is missing.");
                    used = Math.Max(used, actual);
                }
                var headroom = Math.Max(64 * Mb, used / 10);
                size = Align(checked(used + headroom));
                staged = checked(staged + used + Math.Max(64 * Mb, used / 5));
            }
            result.Add(new(p, cursor, size, image)); cursor = checked(cursor + size);
        }
        if (cursor + Mb > destinationBytes || !result.Any(p => p.Capture))
            throw new ArgumentException("The smaller destination cannot hold all content, fixed partitions and filesystem working space.");
        // Keep the trailing recovery/utility partitions after Windows and give remaining space to the last imaged data partition.
        var spare = (destinationBytes - cursor - Mb) / Mb * Mb;
        var grow = result.FindLastIndex(p => p.Capture);
        result[grow] = result[grow] with { Size = checked(result[grow].Size + spare) };
        for (var i = grow + 1; i < result.Count; i++) result[i] = result[i] with { Offset = checked(result[i].Offset + spare) };
        return new(result, true, staged);
    }
}
