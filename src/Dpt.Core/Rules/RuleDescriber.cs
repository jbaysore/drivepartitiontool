namespace Dpt.Core.Rules;

/// <summary>Plain-language summaries of rules, e.g. "When a file in Downloads is a picture, tag it Photos and move it to …".</summary>
public static class RuleDescriber
{
    public static string Describe(RuleDefinition r)
    {
        var what = r.Kind switch { RuleItemKind.Folders => "a folder", RuleItemKind.Both => "an item", _ => "a file" };
        var where = r.Folders.Count == 0 ? "anywhere" : "in " + string.Join(" or ", r.Folders.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f));
        if (r.Folders.Count > 0 && r.IncludeSubfolders) where += " (or its subfolders)";
        var cond = Conditions(r.Conditions);
        var actions = new List<string>();
        if (r.AddCategories.Count > 0) actions.Add("tag it " + string.Join(", ", r.AddCategories));
        if (r.Moves) actions.Add("move it to " + r.MoveTo);
        var action = actions.Count == 0 ? "do nothing" : string.Join(" and ", actions);
        var when = r.Trigger == RuleTrigger.Automatic
            ? r.Scope switch { RuleScope.NewItems => "Runs by itself on new items.", RuleScope.ExistingItems => "Runs on existing items when you start it.", _ => "Runs by itself on new items, and on existing items when you start it." }
            : "Runs only when you start it.";
        return $"When {what} {where}{(cond.Length > 0 ? " " + cond : "")}, {action}. {when}";
    }

    private static string Conditions(RuleConditionGroup g)
    {
        var parts = g.Clauses.Select(Clause).Concat(g.Groups.Where(x => !x.IsEmpty).Select(x => "(" + Conditions(x) + ")")).ToList();
        if (parts.Count == 0) return "";
        return string.Join(g.Match == ConditionMatch.All ? " and " : " or ", parts);
    }

    public static string Clause(RuleClause c) => c.Field switch
    {
        ConditionField.Name => $"has a name that {Op(c.Operator)} \"{c.Value}\"",
        ConditionField.PathContains => $"has a location that {Op(c.Operator)} \"{c.Value}\"",
        ConditionField.Extension => c.Operator is ConditionOperator.IsNot or ConditionOperator.DoesNotContain ? $"is not a .{c.Value} file" : $"is a {string.Join(" or ", c.Value.Split(',').Select(e => "." + e.Trim().TrimStart('.')))} file",
        ConditionField.TypeGroup => c.Operator == ConditionOperator.IsNot ? $"is not a {TypeName(c.Value)}" : $"is a {TypeName(c.Value)}",
        ConditionField.Size => $"is {(c.Operator == ConditionOperator.GreaterThan ? "bigger than" : c.Operator == ConditionOperator.LessThan ? "smaller than" : "exactly")} {c.Value}",
        ConditionField.Modified => c.Operator switch
        {
            ConditionOperator.OlderThanDays => $"hasn't changed in {c.Value} days",
            ConditionOperator.NewerThanDays => $"changed in the last {c.Value} days",
            ConditionOperator.GreaterThan => $"changed after {c.Value}",
            ConditionOperator.LessThan => $"changed before {c.Value}",
            _ => $"changed on {c.Value}",
        },
        ConditionField.Created => c.Operator switch
        {
            ConditionOperator.OlderThanDays => $"was created more than {c.Value} days ago",
            ConditionOperator.NewerThanDays => $"was created in the last {c.Value} days",
            _ => $"was created {(c.Operator == ConditionOperator.GreaterThan ? "after" : c.Operator == ConditionOperator.LessThan ? "before" : "on")} {c.Value}",
        },
        ConditionField.Category => c.Operator is ConditionOperator.IsNot or ConditionOperator.DoesNotContain ? $"isn't tagged {c.Value}" : $"is tagged {c.Value}",
        _ => c.ToString(),
    };

    private static string Op(ConditionOperator o) => o switch
    {
        ConditionOperator.Contains => "contains",
        ConditionOperator.DoesNotContain => "doesn't contain",
        ConditionOperator.StartsWith => "starts with",
        ConditionOperator.EndsWith => "ends with",
        ConditionOperator.Is => "is",
        ConditionOperator.IsNot => "isn't",
        ConditionOperator.MatchesPattern => "matches",
        _ => o.ToString(),
    };

    private static string TypeName(string group) => group.ToLowerInvariant() switch
    {
        "image" => "picture",
        "video" => "video",
        "audio" => "music or audio file",
        "doc" => "document",
        "archive" => "archive (zip, rar…)",
        "app" => "program or installer",
        "code" => "code file",
        _ => group + " file",
    };
}
