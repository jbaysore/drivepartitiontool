using System.Text.Json.Serialization;

namespace Dpt.Core.Rules;

public enum RuleTrigger
{
    /// <summary>Runs only when the owner starts it.</summary>
    Manual,

    /// <summary>Runs by itself when matching items appear (and/or on existing items when first enabled).</summary>
    Automatic,
}

public enum RuleScope
{
    ExistingItems,
    NewItems,
    Both,
}

public enum RuleItemKind
{
    Files,
    Folders,
    Both,
}

public enum ConditionMatch
{
    All,
    Any,
}

public enum ConditionField
{
    Name,
    Extension,
    TypeGroup,
    Size,
    Modified,
    Created,
    Category,
    PathContains,
}

public enum ConditionOperator
{
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,
    Is,
    IsNot,
    MatchesPattern,
    IsOneOf,
    GreaterThan,
    LessThan,
    OlderThanDays,
    NewerThanDays,
}

public enum ConflictPolicy
{
    KeepBoth,
    Skip,
    Replace,
}

/// <summary>One condition: field + operator + value (e.g. Extension IsOneOf "jpg,png").</summary>
public sealed record RuleClause(ConditionField Field, ConditionOperator Operator, string Value);

/// <summary>A group of clauses (all or any must match), optionally with nested groups.</summary>
public sealed record RuleConditionGroup
{
    public ConditionMatch Match { get; init; } = ConditionMatch.All;
    public List<RuleClause> Clauses { get; init; } = [];
    public List<RuleConditionGroup> Groups { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Clauses.Count == 0 && Groups.All(g => g.IsEmpty);
}

/// <summary>
/// A user rule: which items it looks at, what must match, and what happens (tag with categories and/or move to a
/// folder built from a template such as <c>E:\Media\Images\{year}\{ext}</c>).
/// </summary>
public sealed record RuleDefinition
{
    public string Name { get; init; } = "New rule";
    public List<string> Folders { get; init; } = [];
    public bool IncludeSubfolders { get; init; } = true;
    public RuleItemKind Kind { get; init; } = RuleItemKind.Files;
    public RuleConditionGroup Conditions { get; init; } = new();
    public List<string> AddCategories { get; init; } = [];
    public string? MoveTo { get; init; }
    public ConflictPolicy OnConflict { get; init; } = ConflictPolicy.KeepBoth;
    public RuleTrigger Trigger { get; init; } = RuleTrigger.Manual;
    public RuleScope Scope { get; init; } = RuleScope.Both;

    /// <summary>Seconds a new file must stay unchanged before an automatic rule touches it (downloads in progress).</summary>
    public int SettleSeconds { get; init; } = 3;

    [JsonIgnore]
    public bool Moves => !string.IsNullOrWhiteSpace(MoveTo);
}
