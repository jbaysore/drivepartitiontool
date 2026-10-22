using System.Globalization;

namespace Dpt.Core.Formatting;

/// <summary>Formats byte counts the way File Explorer does (binary units, labelled KB/MB/GB/TB).</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes.ToString(CultureInfo.CurrentCulture)} bytes";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Three significant digits, like "8.61 GB", "84.6 GB", "642 MB".
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    /// <summary>Parses user input such as "1gb", "500 MB", "12k" or "1024" into bytes.</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToLowerInvariant().Replace(" ", "");
        long multiplier = 1;
        string[] suffixes = ["pb", "tb", "gb", "mb", "kb", "p", "t", "g", "m", "k", "b", "bytes"];
        foreach (var suffix in suffixes)
        {
            if (!s.EndsWith(suffix, StringComparison.Ordinal)) continue;
            multiplier = suffix[0] switch
            {
                'p' => 1L << 50,
                't' => 1L << 40,
                'g' => 1L << 30,
                'm' => 1L << 20,
                'k' => 1L << 10,
                _ => 1,
            };
            s = s[..^suffix.Length];
            break;
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0) return false;
        bytes = (long)(number * multiplier);
        return true;
    }
}
