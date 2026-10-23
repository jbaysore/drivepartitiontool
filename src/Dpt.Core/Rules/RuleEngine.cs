using System.Globalization;
using System.Text.RegularExpressions;
using Dpt.Core.Formatting;
using Dpt.Core.Navigation;
using Dpt.Core.Search;

namespace Dpt.Core.Rules;

/// <summary>Facts about one file or folder that rules can test.</summary>
public sealed record ItemFacts(string Path, bool IsFolder, long Size, DateTime Modified, DateTime Created, IReadOnlyCollection<string> Categories)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Path).TrimStart('.');
}

/// <summary>What a rule would do to one item.</summary>
public sealed record RuleAction(string Path, IReadOnlyList<string> AddCategories, string? MoveToFolder, string? Destination, string? Note)
{
    public bool Moves => Destination is not null && !string.Equals(Destination, Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Evaluates rule conditions and builds destinations. Pure logic (no file system access).</summary>
public static class RuleEngine
{
    private static readonly char[] Invalid = System.IO.Path.GetInvalidFileNameChars();

    public static bool Applies(RuleDefinition rule, ItemFacts item, TypeGroups? groups = null)
    {
        if (rule.Kind == RuleItemKind.Files && item.IsFolder) return false;
        if (rule.Kind == RuleItemKind.Folders && !item.IsFolder) return false;
        if (rule.Folders.Count > 0)
        {
            var inside = rule.Folders.Any(f => rule.IncludeSubfolders
                ? PathUtil.IsSameOrUnder(item.Path, f) && !string.Equals(PathUtil.Normalize(item.Path), PathUtil.Normalize(f), StringComparison.OrdinalIgnoreCase)
                : string.Equals(System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\')), PathUtil.Normalize(f).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!inside) return false;
        }

        return Matches(rule.Conditions, item, groups ?? TypeGroups.Default, DateTime.Now);
    }

    public static bool Matches(RuleConditionGroup group, ItemFacts item, TypeGroups groups, DateTime now)
    {
        if (group.IsEmpty) return true;
        var results = group.Clauses.Select(c => Matches(c, item, groups, now))
            .Concat(group.Groups.Where(g => !g.IsEmpty).Select(g => Matches(g, item, groups, now)));
        return group.Match == ConditionMatch.All ? results.All(r => r) : results.Any(r => r);
    }

    public static bool Matches(RuleClause c, ItemFacts item, TypeGroups groups, DateTime now)
    {
        var v = c.Value ?? "";
        switch (c.Field)
        {
            case ConditionField.Name:
                return Text(item.Name, c.Operator, v);
            case ConditionField.PathContains:
                return Text(item.Path, c.Operator == ConditionOperator.Is ? ConditionOperator.Contains : c.Operator, v);
            case ConditionField.Extension:
            {
                var list = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.TrimStart('.', '*'));
                var hit = list.Contains(item.Extension, StringComparer.OrdinalIgnoreCase);
                return c.Operator is ConditionOperator.IsNot or ConditionOperator.DoesNotContain ? !hit : hit;
            }
            case ConditionField.TypeGroup:
            {
                var hit = !item.IsFolder && string.Equals(groups.GroupOf(item.Extension), v, StringComparison.OrdinalIgnoreCase);
                return c.Operator == ConditionOperator.IsNot ? !hit : hit;
            }
            case ConditionField.Size:
            {
                if (!SizeFormatter.TryParse(v, out var bytes)) return false;
                return c.Operator switch
                {
                    ConditionOperator.GreaterThan => item.Size > bytes,
                    ConditionOperator.LessThan => item.Size < bytes,
                    ConditionOperator.Is => item.Size == bytes,
                    _ => false,
                };
            }
            case ConditionField.Modified or ConditionField.Created:
            {
                var when = c.Field == ConditionField.Modified ? item.Modified : item.Created;
                if (c.Operator is ConditionOperator.OlderThanDays or ConditionOperator.NewerThanDays)
                {
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var days)) return false;
                    var age = (now - when).TotalDays;
                    return c.Operator == ConditionOperator.OlderThanDays ? age > days : age < days;
                }

                if (!DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
                return c.Operator switch
                {
                    ConditionOperator.GreaterThan => when > date,
                    ConditionOperator.LessThan => when < date,
                    ConditionOperator.Is => when.Date == date.Date,
                    _ => false,
                };
            }
            case ConditionField.Category:
            {
                var hit = item.Categories.Contains(v, StringComparer.OrdinalIgnoreCase);
                return c.Operator is ConditionOperator.IsNot or ConditionOperator.DoesNotContain ? !hit : hit;
            }
            default:
                return false;
        }
    }

    private static bool Text(string s, ConditionOperator op, string v) => op switch
    {
        ConditionOperator.Contains => s.Contains(v, StringComparison.OrdinalIgnoreCase),
        ConditionOperator.DoesNotContain => !s.Contains(v, StringComparison.OrdinalIgnoreCase),
        ConditionOperator.StartsWith => s.StartsWith(v, StringComparison.OrdinalIgnoreCase),
        ConditionOperator.EndsWith => s.EndsWith(v, StringComparison.OrdinalIgnoreCase),
        ConditionOperator.Is => string.Equals(s, v, StringComparison.OrdinalIgnoreCase),
        ConditionOperator.IsNot => !string.Equals(s, v, StringComparison.OrdinalIgnoreCase),
        ConditionOperator.MatchesPattern => GlobOrRegex(s, v),
        _ => false,
    };

    /// <summary>"*.jpg" / "IMG_????" style wildcards; "/regex/" for regular expressions.</summary>
    public static bool GlobOrRegex(string s, string pattern)
    {
        try
        {
            if (pattern.Length > 2 && pattern.StartsWith('/') && pattern.EndsWith('/'))
                return Regex.IsMatch(s, pattern[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(s, rx, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Expands a destination template. Tokens: {year} {month} {monthname} {day} {ext} {type} {category} {name} {created:fmt} {modified:fmt}.
    /// Characters Windows doesn't allow are replaced with "_". Returns null if the result isn't an absolute path.
    /// </summary>
    public static string? ExpandFolder(string template, ItemFacts item, IReadOnlyList<string> categoriesAfter, TypeGroups? groups = null)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var g = groups ?? TypeGroups.Default;
        var d = item.Modified;
        var result = Regex.Replace(template, @"\{(\w+)(?::([^}]*))?\}", m =>
        {
            var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
            var value = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "year" => d.ToString("yyyy", CultureInfo.InvariantCulture),
                "month" => d.ToString("MM", CultureInfo.InvariantCulture),
                "monthname" => d.ToString("MMMM", CultureInfo.CurrentCulture),
                "day" => d.ToString("dd", CultureInfo.InvariantCulture),
                "ext" => item.Extension.Length == 0 ? "no extension" : item.Extension.ToLowerInvariant(),
                "type" => item.IsFolder ? "folders" : g.GroupOf(item.Extension),
                "category" => categoriesAfter.FirstOrDefault() ?? "uncategorized",
                "name" => System.IO.Path.GetFileNameWithoutExtension(item.Name),
                "modified" => d.ToString(fmt ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                "created" => item.Created.ToString(fmt ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => m.Value,
            };
            return SafeSegment(value);
        });
        result = result.Replace('/', '\\');
        return result.Length >= 3 && result[1] == ':' && result[2] == '\\' || result.StartsWith(@"\\", StringComparison.Ordinal) ? PathUtil.Normalize(result) : null;
    }

    private static string SafeSegment(string s) => string.Concat(s.Select(ch => Invalid.Contains(ch) && ch != '\\' ? '_' : ch)).Replace('\\', '_').Trim();

    /// <summary>What the rule would do to an item that matches it.</summary>
    public static RuleAction Plan(RuleDefinition rule, ItemFacts item, TypeGroups? groups = null)
    {
        var newCats = rule.AddCategories.Where(c => !item.Categories.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var after = item.Categories.Concat(newCats).ToList();
        if (!rule.Moves) return new RuleAction(item.Path, newCats, null, null, null);
        var folder = ExpandFolder(rule.MoveTo!, item, after, groups);
        if (folder is null) return new RuleAction(item.Path, newCats, null, null, "The destination isn't a full folder path.");
        if (item.IsFolder && PathUtil.IsSameOrUnder(folder, item.Path))
            return new RuleAction(item.Path, newCats, folder, null, "A folder can't be moved inside itself.");
        return new RuleAction(item.Path, newCats, folder, System.IO.Path.Combine(folder, item.Name), null);
    }
}
