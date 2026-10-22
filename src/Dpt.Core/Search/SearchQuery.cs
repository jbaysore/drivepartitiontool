using System.Globalization;
using Dpt.Core.Formatting;

namespace Dpt.Core.Search;

public enum ItemKindFilter
{
    Any,
    FilesOnly,
    FoldersOnly,
}

/// <summary>
/// A parsed search. Free words must all appear in the name (any order, any case). Optional filters:
/// <c>ext:pdf,docx</c> · <c>type:image</c> · <c>size:&gt;1gb</c> / <c>size:10mb..2gb</c> · <c>modified:today|yesterday|lastweek|lastmonth|2025|2025-03|2025-01-01..2025-02-01</c>
/// · <c>in:C:\path</c> · <c>drive:E</c> · <c>cat:Taxes</c> · <c>folder:</c> / <c>file:</c> · quoted "exact phrase".
/// Unknown "key:value" tokens are treated as plain words.
/// </summary>
public sealed record SearchQuery
{
    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlySet<string>? Extensions { get; init; }
    public string? TypeGroup { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedFrom { get; init; }
    public DateTime? ModifiedTo { get; init; }
    public string? InPath { get; init; }
    public IReadOnlyList<string> Drives { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public ItemKindFilter Kind { get; init; }

    public bool IsEmpty => Terms.Count == 0 && Extensions is null && TypeGroup is null && MinSize is null && MaxSize is null &&
                           ModifiedFrom is null && ModifiedTo is null && Categories.Count == 0 && Kind == ItemKindFilter.Any;

    public static SearchQuery Parse(string text, DateTime? now = null, TypeGroups? groups = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var terms = new List<string>();
        HashSet<string>? exts = null;
        string? type = null;
        long? min = null, max = null;
        DateTime? from = null, to = null;
        string? inPath = null;
        var drives = new List<string>();
        var cats = new List<string>();
        var kind = ItemKindFilter.Any;

        foreach (var token in Tokenize(text))
        {
            var colon = token.IndexOf(':');
            // "C:\..." is a path, not a filter.
            if (colon > 1 && !(token.Length > colon + 1 && token[colon + 1] == '\\'))
            {
                var key = token[..colon].ToLowerInvariant();
                var value = token[(colon + 1)..].Trim('"');
                switch (key)
                {
                    case "ext":
                        exts ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var e in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) exts.Add(e.TrimStart('.', '*'));
                        continue;
                    case "type":
                        if ((groups ?? TypeGroups.Default).TryGet(value, out _)) type = value.ToLowerInvariant();
                        else if (value is "folder" or "folders" or "dir") kind = ItemKindFilter.FoldersOnly;
                        else if (value is "file" or "files") kind = ItemKindFilter.FilesOnly;
                        else terms.Add(token);
                        continue;
                    case "size":
                        if (TryParseSize(value, out var lo, out var hi))
                        {
                            min = lo;
                            max = hi;
                        }
                        else terms.Add(token);

                        continue;
                    case "modified" or "date" or "dm":
                        if (TryParseDate(value, today, out var f, out var t))
                        {
                            from = f;
                            to = t;
                        }
                        else terms.Add(token);

                        continue;
                    case "in" or "path":
                        if (value.Length > 0) inPath = value;
                        continue;
                    case "drive":
                        foreach (var d in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            drives.Add(char.ToUpperInvariant(d[0]) + ":");
                        continue;
                    case "cat" or "category" or "tag":
                        if (value.Length > 0) cats.Add(value);
                        continue;
                    case "folder" or "folders":
                        kind = ItemKindFilter.FoldersOnly;
                        if (value.Length > 0) terms.Add(value);
                        continue;
                    case "file" or "files":
                        kind = ItemKindFilter.FilesOnly;
                        if (value.Length > 0) terms.Add(value);
                        continue;
                }
            }

            var term = token.Trim('"');
            if (term.Length == 0) continue;
            // "*.pdf" style → extension filter.
            if (term.StartsWith("*.", StringComparison.Ordinal) && term.Length > 2 && term.IndexOfAny(['*', '?'], 2) < 0)
            {
                exts ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                exts.Add(term[2..]);
                continue;
            }

            terms.Add(term.Replace("*", ""));
        }

        return new SearchQuery
        {
            Terms = terms.Where(t => t.Length > 0).ToList(),
            Extensions = exts,
            TypeGroup = type,
            MinSize = min,
            MaxSize = max,
            ModifiedFrom = from,
            ModifiedTo = to,
            InPath = inPath,
            Drives = drives,
            Categories = cats,
            Kind = kind,
        };
    }

    /// <summary>Splits on spaces, keeping "quoted phrases" (and key:"quoted values") together.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                current.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    private static bool TryParseSize(string value, out long? min, out long? max)
    {
        min = max = null;
        var v = value.Trim();
        if (v.Contains(".."))
        {
            var parts = v.Split("..");
            if (parts.Length != 2) return false;
            if (parts[0].Length > 0)
            {
                if (!SizeFormatter.TryParse(parts[0], out var a)) return false;
                min = a;
            }

            if (parts[1].Length > 0)
            {
                if (!SizeFormatter.TryParse(parts[1], out var b)) return false;
                max = b;
            }

            return min is not null || max is not null;
        }

        if (v.StartsWith(">=", StringComparison.Ordinal) || v.StartsWith("<=", StringComparison.Ordinal))
        {
            if (!SizeFormatter.TryParse(v[2..], out var n)) return false;
            if (v[0] == '>') min = n;
            else max = n;
            return true;
        }

        if (v.StartsWith('>') || v.StartsWith('<'))
        {
            if (!SizeFormatter.TryParse(v[1..], out var n)) return false;
            if (v[0] == '>') min = n + 1;
            else max = n - 1;
            return true;
        }

        switch (v.ToLowerInvariant())
        {
            case "empty": min = 0; max = 0; return true;
            case "tiny": min = 1; max = 16 * 1024; return true;
            case "small": min = 16 * 1024 + 1; max = 1024 * 1024; return true;
            case "medium": min = 1024 * 1024 + 1; max = 128L * 1024 * 1024; return true;
            case "large": min = 128L * 1024 * 1024 + 1; max = 1024L * 1024 * 1024; return true;
            case "huge": min = 1024L * 1024 * 1024 + 1; return true;
        }

        if (!SizeFormatter.TryParse(v, out var exact)) return false;
        min = exact;
        max = exact;
        return true;
    }

    private static bool TryParseDate(string value, DateTime today, out DateTime? from, out DateTime? to)
    {
        from = to = null;
        var v = value.Trim().ToLowerInvariant();
        switch (v)
        {
            case "today": from = today; to = today.AddDays(1); return true;
            case "yesterday": from = today.AddDays(-1); to = today; return true;
            case "thisweek": from = today.AddDays(-(int)today.DayOfWeek); to = today.AddDays(1); return true;
            case "lastweek" or "pastweek": from = today.AddDays(-7); to = today.AddDays(1); return true;
            case "thismonth": from = new DateTime(today.Year, today.Month, 1); to = today.AddDays(1); return true;
            case "lastmonth" or "pastmonth": from = today.AddMonths(-1); to = today.AddDays(1); return true;
            case "thisyear": from = new DateTime(today.Year, 1, 1); to = today.AddDays(1); return true;
            case "lastyear" or "pastyear": from = today.AddYears(-1); to = today.AddDays(1); return true;
        }

        if (v.Contains(".."))
        {
            var parts = v.Split("..");
            if (parts.Length != 2) return false;
            if (parts[0].Length > 0)
            {
                if (!TryParseDatePart(parts[0], out var a, out _)) return false;
                from = a;
            }

            if (parts[1].Length > 0)
            {
                if (!TryParseDatePart(parts[1], out _, out var b)) return false;
                to = b;
            }

            return from is not null || to is not null;
        }

        if (v.StartsWith('>') || v.StartsWith('<'))
        {
            if (!TryParseDatePart(v[1..], out var a, out var b)) return false;
            if (v[0] == '>') from = b;
            else to = a;
            return true;
        }

        if (!TryParseDatePart(v, out var s, out var e)) return false;
        from = s;
        to = e;
        return true;
    }

    /// <summary>"2025" → whole year; "2025-03" → that month; "2025-03-14" → that day.</summary>
    private static bool TryParseDatePart(string v, out DateTime start, out DateTime end)
    {
        start = end = default;
        if (v.Length == 4 && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1900 and < 3000)
        {
            start = new DateTime(y, 1, 1);
            end = start.AddYears(1);
            return true;
        }

        if (DateTime.TryParseExact(v, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
        {
            start = m;
            end = m.AddMonths(1);
            return true;
        }

        if (DateTime.TryParseExact(v, ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            DateTime.TryParse(v, CultureInfo.CurrentCulture, DateTimeStyles.None, out d))
        {
            start = d.Date;
            end = d.Date.AddDays(1);
            return true;
        }

        return false;
    }
}
