namespace Dpt.Core.WindowsCopy;

public enum BootFirmware { Uefi, Bios }

/// <summary>Shared preview/worker contract. Paths in Exclusions are literal paths relative to SourceRoot.</summary>
public sealed record WindowsCopyOptions
{
    public required string SourceRoot { get; init; }
    public List<string> Exclusions { get; init; } = [];
    public bool MakeBootable { get; init; } = true;
    public BootFirmware Firmware { get; init; } = BootFirmware.Uefi;
    public int EfiSizeMb { get; init; } = 260;
    public int RecoverySizeMb { get; init; } = 1024;
}

public sealed record CopyPartition(string Role, long Offset, long Size);
public sealed record WindowsCopyLayout(IReadOnlyList<CopyPartition> Partitions, long WorkingBytes, long WindowsBytes);

/// <summary>Pure checks used before a destructive operation and by its preview. No disk or registry access.</summary>
public static class WindowsCopyPlanning
{
    public const long Mb = 1024 * 1024;

    public static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An exclusion path is empty.");
        var path = value.Replace('/', '\\');
        if (path.StartsWith('\\') || path.Any(c => c < 32 || ":*?\"<>|".Contains(c)) ||
            path.Split('\\').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new ArgumentException($"Unsupported relative path: {value}");
        return path;
    }

    public static bool Under(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool ProtectsWindowsContent(string path)
    {
        var p = NormalizeRelativePath(path);
        if (Under(p, "Windows") || p.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) || Under(p, @"ProgramData\Microsoft") ||
            new[] { "Boot", "bootmgr", "bootnxt", "EFI", @"Program Files\Common Files", @"Program Files\WindowsApps", @"Program Files (x86)\Common Files", @"Program Files (x86)\WindowsApps" }.Any(s => Under(p, s) || Under(s, p))) return true;
        var parts = p.Split('\\');
        if (!parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length < 3 || WindowsContentClassifier.IsSystemProfile(parts[1])) return true;
        if (parts[2].StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase)) return true;
        if (!parts[2].Equals("AppData", StringComparison.OrdinalIgnoreCase)) return false;
        // Keep the account's Microsoft branches and their ancestors, including UsrClass.dat.
        return parts.Length == 3 ||
            (new[] { "Local", "LocalLow", "Roaming" }.Contains(parts[3], StringComparer.OrdinalIgnoreCase) && (parts.Length == 4 || parts[4].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<string> ValidateExclusions(IEnumerable<string> exclusions)
    {
        var result = exclusions.Select(NormalizeRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (result.FirstOrDefault(ProtectsWindowsContent) is { } required)
            throw new ArgumentException($"{required} contains required Windows or account data and cannot be excluded.");
        return result;
    }

    public static void Validate(WindowsCopyOptions options)
    {
        if (options.SourceRoot is not { Length: 3 } || !char.IsAsciiLetter(options.SourceRoot[0]) || options.SourceRoot[1] != ':' || options.SourceRoot[2] != '\\')
            throw new ArgumentException("Choose the root of a local Windows volume, such as C:\\.");
        if (!Enum.IsDefined(options.Firmware)) throw new ArgumentException("Unknown firmware type.");
        if (options.EfiSizeMb is < 260 or > 4096) throw new ArgumentException("EFI size must be between 260 and 4096 MB.");
        if (options.RecoverySizeMb is < 1024 or > 16384) throw new ArgumentException("Recovery size must be between 1024 and 16384 MB.");
        if (options.Exclusions is null) throw new ArgumentException("The exclusion list is missing.");
        ValidateExclusions(options.Exclusions);
    }

    public static WindowsCopyLayout Layout(WindowsCopyOptions options, long diskBytes, long selectedBytes)
    {
        Validate(options);
        if (diskBytes <= 0 || selectedBytes < 0) throw new ArgumentException("Disk and content sizes must be known.");
        var partitions = new List<CopyPartition>();
        long offset = Mb;
        void Add(string role, long size) { partitions.Add(new(role, offset, size)); offset += size; }
        if (options.Firmware == BootFirmware.Uefi) { Add("EFI", options.EfiSizeMb * Mb); Add("MSR", 16 * Mb); }
        var recoverySize = options.RecoverySizeMb * Mb;
        var windowsSize = (diskBytes - offset - recoverySize - Mb) / Mb * Mb;
        // The temporary image and applied files coexist on the destination; compression is not assumed.
        var working = checked(selectedBytes * 2 + Math.Max(64 * Mb, selectedBytes / 10));
        if (windowsSize < working) throw new ArgumentException("The destination needs space for the selected files, a temporary capture image and working space. Choose a larger disk or leave out more optional content.");
        Add("Windows", windowsSize);
        Add("Recovery", recoverySize);
        return new(partitions, working, windowsSize);
    }

    public static string ExclusionConfig(WindowsCopyOptions options) => "[ExclusionList]\r\n" + string.Join("\r\n",
        ValidateExclusions(options.Exclusions).Concat(WindowsContentClassifier.AlwaysExcludedNames).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => "\\" + p)) + "\r\n";
}
