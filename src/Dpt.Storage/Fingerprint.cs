using System.Security.Cryptography;
using System.Text;
using Dpt.Ipc;

namespace Dpt.Storage;

/// <summary>Builds the fingerprint a disk plan is tied to (identity + exact partition layout).</summary>
public static class Fingerprint
{
    public static string LayoutHash(DiskInfo d)
    {
        var sb = new StringBuilder();
        sb.Append(d.PartitionStyle).Append('|').Append(d.Size).Append('|');
        foreach (var p in d.Partitions.OrderBy(p => p.Offset))
            sb.Append(p.PartitionNumber).Append(':').Append(p.Offset).Append(':').Append(p.Size).Append(':').Append(p.GptType).Append(':').Append(p.MbrType).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    public static DiskFingerprint Of(DiskInfo d) => new()
    {
        Number = d.Number,
        UniqueId = d.UniqueId,
        SerialNumber = d.SerialNumber,
        Size = d.Size,
        LayoutHash = LayoutHash(d),
        FriendlyName = d.FriendlyName,
    };

    /// <summary>Why the disk no longer matches the plan, or null if it does.</summary>
    public static string? Mismatch(DiskFingerprint expected, DiskInfo actual)
    {
        if (actual.Number != expected.Number) return "The disk number changed.";
        if (!string.Equals(actual.UniqueId, expected.UniqueId, StringComparison.OrdinalIgnoreCase)) return "A different disk is now at this number.";
        if (!string.Equals(actual.SerialNumber, expected.SerialNumber, StringComparison.OrdinalIgnoreCase)) return "The disk's serial number doesn't match.";
        if (actual.Size != expected.Size) return "The disk's size doesn't match.";
        if (LayoutHash(actual) != expected.LayoutHash) return "The disk's partitions changed since the preview.";
        return null;
    }
}
