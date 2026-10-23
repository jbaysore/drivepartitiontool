using System.Text.RegularExpressions;
using Dpt.Core.WindowsCopy;

namespace Dpt.Core.Boot;

public sealed record BootEntry(string Kind, string Identifier, string Description, string Device, string Path, string WindowsDevice, IReadOnlyDictionary<string, string> Fields)
{
    public string Title => string.IsNullOrWhiteSpace(Description) ? Kind : Description;
    public string Meaning => Device.StartsWith("ramdisk=", StringComparison.OrdinalIgnoreCase) ? "Starts recovery or setup tools" : Path.EndsWith("winload.efi", StringComparison.OrdinalIgnoreCase) || Path.EndsWith("winload.exe", StringComparison.OrdinalIgnoreCase)
        ? "Starts a Windows installation" : Path.Contains("bootmgfw", StringComparison.OrdinalIgnoreCase) || Identifier.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase)
            ? "Chooses which Windows installation to start" : "Additional startup or recovery entry";
}

/// <summary>Parses read-only BCDEdit output. Unknown fields are retained instead of being inferred or discarded.</summary>
public static partial class BootConfiguration
{
    [GeneratedRegex(@"\r?\n\s*\r?\n")]
    private static partial Regex Blocks();
    [GeneratedRegex(@"^\s*(\S+)\s{2,}(.*)$")]
    private static partial Regex Field();

    public static IReadOnlyList<BootEntry> Parse(string text)
    {
        var entries = new List<BootEntry>();
        foreach (var block in Blocks().Split(text.Trim()))
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count < 3 || !lines[1].Trim().All(c => c == '-')) continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? last = null;
            foreach (var line in lines.Skip(2))
            {
                var match = Field().Match(line);
                if (match.Success) { last = match.Groups[1].Value; fields[last] = match.Groups[2].Value.Trim(); }
                else if (last is not null && !string.IsNullOrWhiteSpace(line)) fields[last] += " " + line.Trim();
            }
            if (!fields.TryGetValue("identifier", out var id)) continue;
            string Value(string key) => fields.GetValueOrDefault(key, "");
            entries.Add(new(lines[0].Trim(), id, Value("description"), Value("device"), Value("path"), Value("osdevice"), fields));
        }
        return entries;
    }
}

public sealed record BootRepairOptions
{
    public required string SourceRoot { get; init; }
    public BootFirmware Firmware { get; init; } = BootFirmware.Uefi;
}

public static class BootRepairPlanning
{
    public static void Validate(BootRepairOptions options)
    {
        if (options.SourceRoot is not { Length: 3 } || !char.IsAsciiLetter(options.SourceRoot[0]) || options.SourceRoot[1] != ':' || options.SourceRoot[2] != '\\')
            throw new ArgumentException("Choose a Windows volume by its local drive letter.");
        if (!Enum.IsDefined(options.Firmware)) throw new ArgumentException("Unknown firmware type.");
    }

    public static IReadOnlyList<string> Arguments(BootRepairOptions options, string systemRoot)
    {
        Validate(options);
        Validate(options with { SourceRoot = systemRoot });
        return [options.SourceRoot + "Windows", "/s", systemRoot.TrimEnd('\\'), "/f", options.Firmware == BootFirmware.Uefi ? "UEFI" : "BIOS"];
    }
}
