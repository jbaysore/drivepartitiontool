using System.Globalization;
using System.Text.RegularExpressions;

namespace Dpt.Core.Operations;

public enum NameCase
{
    Keep,
    Lower,
    Upper,
    Title,
}

/// <summary>
/// Options for renaming many items at once. <see cref="Template"/> builds the new name (without extension) from tokens:
/// <c>{name}</c> original name, <c>{n}</c> / <c>{n:000}</c> sequence number, <c>{date}</c> / <c>{date:yyyy-MM-dd}</c> modified date,
/// <c>{folder}</c> containing folder. Then Find/Replace and case changes apply. Extensions are kept unless <see cref="ChangeExtension"/>.
/// </summary>
public sealed record BulkRenameOptions
{
    public string Template { get; init; } = "{name}";
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    public NameCase Case { get; init; } = NameCase.Keep;
    public int StartNumber { get; init; } = 1;
    public string? ChangeExtension { get; init; }
}

public sealed record RenameInput(string Path, bool IsFolder, DateTime Modified);

public sealed record RenamePlanItem(string Path, string OldName, string NewName, string? Problem)
{
    public bool Changes => Problem is null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

/// <summary>Computes the new names for a bulk rename and flags problems (invalid names, duplicates, clashes with other files).</summary>
public static class BulkRenamePlanner
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    private static readonly Regex Token = new(@"\{(name|n|date|folder)(?::([^}]*))?\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="existsOutsideSelection">Whether a path exists that is not one of the items being renamed.</param>
    public static IReadOnlyList<RenamePlanItem> Plan(IReadOnlyList<RenameInput> items, BulkRenameOptions o, Func<string, bool> existsOutsideSelection)
    {
        var result = new List<RenamePlanItem>(items.Count);
        Regex? regex = null;
        string? regexError = null;
        if (o.UseRegex && o.Find.Length > 0)
        {
            try { regex = new Regex(o.Find, o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { regexError = "Invalid pattern: " + ex.Message; }
        }

        var number = o.StartNumber;
        foreach (var item in items)
        {
            var oldName = Path.GetFileName(item.Path.TrimEnd('\\'));
            if (regexError is not null)
            {
                result.Add(new RenamePlanItem(item.Path, oldName, oldName, regexError));
                continue;
            }

            var ext = item.IsFolder ? "" : Path.GetExtension(oldName);
            var stem = item.IsFolder ? oldName : Path.GetFileNameWithoutExtension(oldName);
            var folder = Path.GetFileName(Path.GetDirectoryName(item.Path) ?? "");
            var n = number++;
            var built = Token.Replace(string.IsNullOrEmpty(o.Template) ? "{name}" : o.Template, m =>
            {
                var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
                return m.Groups[1].Value.ToLowerInvariant() switch
                {
                    "name" => stem,
                    "n" => fmt is null ? n.ToString(CultureInfo.InvariantCulture) : n.ToString(fmt, CultureInfo.InvariantCulture),
                    "date" => item.Modified.ToString(fmt ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "folder" => folder,
                    _ => m.Value,
                };
            });

            if (o.Find.Length > 0)
            {
                built = regex is not null
                    ? regex.Replace(built, o.Replace)
                    : Replace(built, o.Find, o.Replace, o.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }

            built = o.Case switch
            {
                NameCase.Lower => built.ToLowerInvariant(),
                NameCase.Upper => built.ToUpperInvariant(),
                NameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(built.ToLowerInvariant()),
                _ => built,
            };
            if (o.ChangeExtension is { } newExt && !item.IsFolder) ext = newExt.Length == 0 ? "" : "." + newExt.TrimStart('.');
            if (o.Case == NameCase.Lower) ext = ext.ToLowerInvariant();
            else if (o.Case == NameCase.Upper) ext = ext.ToUpperInvariant();
            var newName = built.Trim() + ext;
            result.Add(new RenamePlanItem(item.Path, oldName, newName, Validate(newName)));
        }

        // Duplicates within the batch, and clashes with existing items that aren't being renamed.
        var targets = result.Where(r => r.Problem is null).GroupBy(r => Path.Combine(Path.GetDirectoryName(r.Path) ?? "", r.NewName), StringComparer.OrdinalIgnoreCase);
        var flagged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selection = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var g in targets)
        {
            if (g.Count() > 1)
                foreach (var r in g) flagged[r.Path] = "Two items would get the same name.";
            else if (!selection.Contains(g.Key) && existsOutsideSelection(g.Key) && !string.Equals(g.First().Path, g.Key, StringComparison.OrdinalIgnoreCase))
                flagged[g.First().Path] = "An item with this name already exists.";
        }

        return result.Select(r => flagged.TryGetValue(r.Path, out var p) ? r with { Problem = p } : r).ToList();
    }

    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The name would be empty.";
        if (name.IndexOfAny(Invalid) >= 0) return "The name would contain a character Windows doesn't allow.";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Names can't end with a dot or space.";
        if (name.Length > 255) return "The name would be too long.";
        return null;
    }

    private static string Replace(string s, string find, string replace, StringComparison cmp)
    {
        if (find.Length == 0) return s;
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (true)
        {
            var j = s.IndexOf(find, i, cmp);
            if (j < 0) break;
            sb.Append(s, i, j - i).Append(replace);
            i = j + find.Length;
        }

        return sb.Append(s, i, s.Length - i).ToString();
    }
}
