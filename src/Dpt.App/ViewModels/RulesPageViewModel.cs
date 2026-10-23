using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.Core.Navigation;
using Dpt.Core.Rules;
using Dpt.Data;

namespace Dpt.App.ViewModels;

public sealed record CategoryRow(long Id, string Name, string Color, int Count)
{
    public string CountText => Count == 1 ? "1 item" : $"{Count} items";
}

public sealed partial class RuleRow : ObservableObject
{
    private readonly Action<RuleRow, bool> _toggled;
    private bool _enabled;

    public RuleRow(StoredRule rule, Action<RuleRow, bool> toggled)
    {
        Rule = rule;
        _enabled = rule.Enabled;
        _toggled = toggled;
    }

    public StoredRule Rule { get; }
    public string Name => Rule.Definition.Name;
    public string Summary => RuleDescriber.Describe(Rule.Definition);
    public string Badge => Rule.Definition.Trigger == RuleTrigger.Automatic ? "Automatic" : "Manual";

    public string Status => Rule.Definition.Moves && !Rule.Previewed
        ? "Needs a preview before it moves anything"
        : Rule.LastRun is { } t ? $"Last run {t.LocalDateTime:g}" : "Not run yet";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value)) _toggled(this, value);
        }
    }
}

/// <summary>Categories (virtual groupings) and rules (automatic or manual tagging/moving).</summary>
public sealed partial class RulesPageViewModel : PageViewModel
{
    public RulesPageViewModel(TabViewModel tab) : base(tab, Location.Page(LocationKind.Rules))
    {
        Services.Rules.Changed += OnChanged;
        Services.Categories.Changed += OnChanged;
        Load();
    }

    public override string IconKey => "Icon.ListFilter";
    public override string Title => "Rules & categories";

    public ObservableCollection<CategoryRow> Categories { get; } = [];
    public ObservableCollection<RuleRow> Rules { get; } = [];

    [ObservableProperty] private string _newCategory = "";

    public bool HasRules => Rules.Count > 0;
    public bool HasCategories => Categories.Count > 0;

    /// <summary>Dialog hooks (set by the app): edit a rule (returns the saved id or null), preview a rule.</summary>
    public static Func<StoredRule?, Task<long?>>? EditRule { get; set; }

    public static Func<StoredRule, Task>? PreviewRule { get; set; }

    private void OnChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(Load);

    public override void Refresh() => Load();

    private void Load()
    {
        var counts = Services.Categories.Repository.Counts();
        Categories.Clear();
        foreach (var c in Services.Categories.Repository.GetCategories())
            Categories.Add(new CategoryRow(c.Id, c.Name, c.Color, counts.GetValueOrDefault(c.Id)));
        Rules.Clear();
        foreach (var r in Services.Rules.GetRules()) Rules.Add(new RuleRow(r, OnToggled));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasCategories));
        StatusText = $"{Categories.Count} categories · {Rules.Count} rules";
    }

    private void OnToggled(RuleRow row, bool enabled)
    {
        Services.Rules.SetEnabled(row.Rule.Id, enabled);
        Services.RuleRunner.RefreshWatchers();
    }

    [RelayCommand]
    private void AddCategory()
    {
        var name = NewCategory.Trim();
        if (name.Length == 0) return;
        Services.Categories.Repository.GetOrCreate(name);
        NewCategory = "";
    }

    [RelayCommand]
    private void ShowCategory(CategoryRow? c)
    {
        if (c is null) return;
        var q = c.Name.Contains(' ') ? $"cat:\"{c.Name}\"" : $"cat:{c.Name}";
        Tab.Navigate(Location.Search(q, null));
    }

    [RelayCommand]
    private void RenameCategory(CategoryRow? c)
    {
        if (c is null) return;
        var name = Services.FileOps.Dialogs.Prompt("Rename category", "New name for this category:", c.Name);
        if (!string.IsNullOrWhiteSpace(name)) Services.Categories.Repository.Rename(c.Id, name, c.Color);
    }

    [RelayCommand]
    private void DeleteCategory(CategoryRow? c)
    {
        if (c is null) return;
        if (!Services.FileOps.Dialogs.Confirm("Delete category", $"Delete the category \"{c.Name}\"? Items keep their files; only the tag is removed from {c.CountText}.", "Delete", danger: true)) return;
        Services.Categories.Repository.Delete(c.Id);
    }

    [RelayCommand]
    private async Task AddRule()
    {
        if (EditRule is not null) await EditRule(null);
    }

    [RelayCommand]
    private async Task Edit(RuleRow? row)
    {
        if (row is not null && EditRule is not null) await EditRule(row.Rule);
    }

    [RelayCommand]
    private async Task Preview(RuleRow? row)
    {
        if (row is not null && PreviewRule is not null) await PreviewRule(Services.Rules.Get(row.Rule.Id)!);
    }

    [RelayCommand]
    private async Task RunNow(RuleRow? row)
    {
        if (row is null) return;
        var rule = Services.Rules.Get(row.Rule.Id)!;
        if (rule.Definition.Moves && !rule.Previewed)
        {
            // Moving rules always show their preview the first time.
            if (PreviewRule is not null) await PreviewRule(rule);
            return;
        }

        var plan = await Services.RuleRunner.PlanAsync(rule);
        var result = await Services.RuleRunner.RunAsync(plan, fromPreview: false);
        Tab.Main.ShowToast($"\"{rule.Definition.Name}\": {result.Moved} moved, {result.Tagged} tagged. Ctrl+Z undoes it.");
    }

    [RelayCommand]
    private void Delete(RuleRow? row)
    {
        if (row is null) return;
        if (!Services.FileOps.Dialogs.Confirm("Delete rule", $"Delete the rule \"{row.Name}\"? Files it already moved stay where they are.", "Delete", danger: true)) return;
        Services.Rules.Delete(row.Rule.Id);
        Services.RuleRunner.RefreshWatchers();
    }

    public override void Dispose()
    {
        Services.Rules.Changed -= OnChanged;
        Services.Categories.Changed -= OnChanged;
        base.Dispose();
    }
}
