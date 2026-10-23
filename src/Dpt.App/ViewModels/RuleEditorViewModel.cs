using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Core.Rules;
using Dpt.Data;

namespace Dpt.App.ViewModels;

public sealed record Choice<T>(string Label, T Value);

/// <summary>One "field operator value" row in the rule editor.</summary>
public sealed partial class ClauseRow : ObservableObject
{
    private readonly Action _changed;

    public static IReadOnlyList<Choice<ConditionField>> Fields { get; } =
    [
        new("Name", ConditionField.Name), new("Type", ConditionField.TypeGroup), new("Extension", ConditionField.Extension),
        new("Size", ConditionField.Size), new("Date modified", ConditionField.Modified), new("Date created", ConditionField.Created),
        new("Category", ConditionField.Category), new("Location", ConditionField.PathContains),
    ];

    public static IReadOnlyList<string> TypeValues { get; } = ["image", "video", "audio", "doc", "archive", "app", "code"];

    public ClauseRow(RuleClause clause, Action changed)
    {
        _changed = changed;
        _field = Fields.First(f => f.Value == clause.Field);
        Operators = OperatorsFor(clause.Field);
        _operator = Operators.FirstOrDefault(o => o.Value == clause.Operator) ?? Operators[0];
        _value = clause.Value;
    }

    [ObservableProperty] private Choice<ConditionField> _field;
    [ObservableProperty] private Choice<ConditionOperator> _operator;
    [ObservableProperty] private string _value;
    [ObservableProperty] private IReadOnlyList<Choice<ConditionOperator>> _operators;

    public bool IsType => Field.Value == ConditionField.TypeGroup;
    public bool IsText => !IsType;

    public string Hint => Field.Value switch
    {
        ConditionField.Extension => "e.g. jpg, png",
        ConditionField.Size => "e.g. 100 MB",
        ConditionField.Modified or ConditionField.Created => Operator.Value is ConditionOperator.OlderThanDays or ConditionOperator.NewerThanDays ? "days" : "yyyy-mm-dd",
        ConditionField.Name => Operator.Value == ConditionOperator.MatchesPattern ? "e.g. IMG_*.jpg" : "",
        _ => "",
    };

    partial void OnFieldChanged(Choice<ConditionField> value)
    {
        Operators = OperatorsFor(value.Value);
        Operator = Operators[0];
        if (value.Value == ConditionField.TypeGroup && !TypeValues.Contains(Value)) Value = "image";
        OnPropertyChanged(nameof(IsType));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(Hint));
        _changed();
    }

    partial void OnOperatorChanged(Choice<ConditionOperator> value)
    {
        OnPropertyChanged(nameof(Hint));
        _changed();
    }

    partial void OnValueChanged(string value) => _changed();

    public RuleClause ToClause() => new(Field.Value, Operator.Value, Value ?? "");

    public static IReadOnlyList<Choice<ConditionOperator>> OperatorsFor(ConditionField f) => f switch
    {
        ConditionField.Name or ConditionField.PathContains =>
        [
            new("contains", ConditionOperator.Contains), new("doesn't contain", ConditionOperator.DoesNotContain), new("starts with", ConditionOperator.StartsWith),
            new("ends with", ConditionOperator.EndsWith), new("is", ConditionOperator.Is), new("matches pattern", ConditionOperator.MatchesPattern),
        ],
        ConditionField.Extension => [new("is one of", ConditionOperator.IsOneOf), new("is not", ConditionOperator.IsNot)],
        ConditionField.TypeGroup => [new("is", ConditionOperator.Is), new("is not", ConditionOperator.IsNot)],
        ConditionField.Size => [new("is bigger than", ConditionOperator.GreaterThan), new("is smaller than", ConditionOperator.LessThan)],
        ConditionField.Modified or ConditionField.Created =>
        [
            new("more than N days ago", ConditionOperator.OlderThanDays), new("within the last N days", ConditionOperator.NewerThanDays),
            new("after", ConditionOperator.GreaterThan), new("before", ConditionOperator.LessThan),
        ],
        ConditionField.Category => [new("is", ConditionOperator.Is), new("is not", ConditionOperator.IsNot)],
        _ => [new("is", ConditionOperator.Is)],
    };
}

/// <summary>Edits a rule as a sentence: where, what must match, what happens, when it runs.</summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly long? _id;
    private readonly DispatcherTimer _countTimer;
    private CancellationTokenSource? _countCts;
    private readonly bool _wasEnabled;

    public RuleEditorViewModel(StoredRule? existing)
    {
        _id = existing?.Id;
        _wasEnabled = existing?.Enabled ?? true;
        var d = existing?.Definition ?? new RuleDefinition
        {
            Folders = [Dpt.Shell.KnownFolders.Downloads],
            Conditions = new RuleConditionGroup { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, "image")] },
        };
        _name = d.Name;
        foreach (var f in d.Folders) Folders.Add(f);
        _includeSubfolders = d.IncludeSubfolders;
        _kind = KindOptions.First(k => k.Value == d.Kind);
        _match = MatchOptions.First(m => m.Value == d.Conditions.Match);
        foreach (var c in d.Conditions.Clauses) Clauses.Add(new ClauseRow(c, Changed));
        _categories = string.Join(", ", d.AddCategories);
        _moveEnabled = d.Moves;
        _moveTo = d.MoveTo ?? Path.Combine(Dpt.Shell.KnownFolders.Pictures, "{year}");
        _conflict = ConflictOptions.First(c => c.Value == d.OnConflict);
        _trigger = TriggerOptions.First(t => t.Value == d.Trigger);
        _scope = ScopeOptions.First(s => s.Value == d.Scope);
        _countTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _countTimer!.Stop();
            _ = CountAsync();
        }, System.Windows.Application.Current.Dispatcher);
        Changed();
    }

    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<ClauseRow> Clauses { get; } = [];

    public IReadOnlyList<Choice<RuleItemKind>> KindOptions { get; } = [new("file", RuleItemKind.Files), new("folder", RuleItemKind.Folders), new("file or folder", RuleItemKind.Both)];
    public IReadOnlyList<Choice<ConditionMatch>> MatchOptions { get; } = [new("all", ConditionMatch.All), new("any", ConditionMatch.Any)];
    public IReadOnlyList<Choice<ConflictPolicy>> ConflictOptions { get; } =
        [new("keep both (add a number)", ConflictPolicy.KeepBoth), new("skip it", ConflictPolicy.Skip), new("replace the existing one", ConflictPolicy.Replace)];
    public IReadOnlyList<Choice<RuleTrigger>> TriggerOptions { get; } = [new("only when I start it", RuleTrigger.Manual), new("automatically", RuleTrigger.Automatic)];
    public IReadOnlyList<Choice<RuleScope>> ScopeOptions { get; } =
        [new("new items and items already there", RuleScope.Both), new("only new items", RuleScope.NewItems), new("only items already there", RuleScope.ExistingItems)];

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private Choice<RuleItemKind> _kind;
    [ObservableProperty] private Choice<ConditionMatch> _match;
    [ObservableProperty] private string _categories;
    [ObservableProperty] private bool _moveEnabled;
    [ObservableProperty] private string _moveTo;
    [ObservableProperty] private Choice<ConflictPolicy> _conflict;
    [ObservableProperty] private Choice<RuleTrigger> _trigger;
    [ObservableProperty] private Choice<RuleScope> _scope;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _matchCount = "";
    [ObservableProperty] private string? _problem;

    public bool IsAutomatic => Trigger.Value == RuleTrigger.Automatic;

    partial void OnNameChanged(string value) => Changed();
    partial void OnIncludeSubfoldersChanged(bool value) => Changed();
    partial void OnKindChanged(Choice<RuleItemKind> value) => Changed();
    partial void OnMatchChanged(Choice<ConditionMatch> value) => Changed();
    partial void OnCategoriesChanged(string value) => Changed();
    partial void OnMoveEnabledChanged(bool value) => Changed();
    partial void OnMoveToChanged(string value) => Changed();
    partial void OnConflictChanged(Choice<ConflictPolicy> value) => Changed();

    partial void OnTriggerChanged(Choice<RuleTrigger> value)
    {
        OnPropertyChanged(nameof(IsAutomatic));
        Changed();
    }

    partial void OnScopeChanged(Choice<RuleScope> value) => Changed();

    public RuleDefinition ToDefinition() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "New rule" : Name.Trim(),
        Folders = Folders.ToList(),
        IncludeSubfolders = IncludeSubfolders,
        Kind = Kind.Value,
        Conditions = new RuleConditionGroup { Match = Match.Value, Clauses = Clauses.Select(c => c.ToClause()).ToList() },
        AddCategories = Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        MoveTo = MoveEnabled && !string.IsNullOrWhiteSpace(MoveTo) ? MoveTo.Trim() : null,
        OnConflict = Conflict.Value,
        Trigger = Trigger.Value,
        Scope = Scope.Value,
    };

    private void Changed()
    {
        var d = ToDefinition();
        Summary = RuleDescriber.Describe(d);
        Problem = Folders.Count == 0 ? "Choose at least one folder to watch."
            : d.AddCategories.Count == 0 && !d.Moves ? "Choose a category to add, a folder to move to, or both."
            : d.Moves && RuleEngine.ExpandFolder(d.MoveTo!, new ItemFacts(@"C:\x\a.jpg", false, 0, DateTime.Now, DateTime.Now, []), ["x"]) is null
                ? "The destination must be a full folder path, like D:\\Sorted\\{year}."
                : null;
        _countTimer?.Stop();
        _countTimer?.Start();
    }

    private async Task CountAsync()
    {
        _countCts?.Cancel();
        var cts = _countCts = new CancellationTokenSource();
        if (Problem is not null)
        {
            MatchCount = "";
            return;
        }

        MatchCount = "Counting matches…";
        try
        {
            var plan = await AppServices.Current.RuleRunner.PlanAsync(new StoredRule(0, ToDefinition(), true, false, 0, null), cts.Token);
            MatchCount = plan.Entries.Count == 0 ? $"Nothing matches right now ({plan.Scanned:N0} items checked)."
                : $"{plan.Entries.Count:N0} item(s) match right now" + (plan.MoveCount > 0 ? $" · {plan.MoveCount:N0} would move" : "") + ".";
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void AddClause()
    {
        Clauses.Add(new ClauseRow(new RuleClause(ConditionField.Name, ConditionOperator.Contains, ""), Changed));
        Changed();
    }

    [RelayCommand]
    private void RemoveClause(ClauseRow? row)
    {
        if (row is null) return;
        Clauses.Remove(row);
        Changed();
    }

    public void AddFolder(string folder)
    {
        if (!Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Folders.Add(folder);
        Changed();
    }

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (folder is null) return;
        Folders.Remove(folder);
        Changed();
    }

    public long Save()
    {
        var id = AppServices.Current.Rules.Save(_id, ToDefinition(), _wasEnabled);
        AppServices.Current.RuleRunner.RefreshWatchers();
        return id;
    }
}
