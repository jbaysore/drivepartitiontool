using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Core.Layouts;
using Dpt.Core.Navigation;
using Dpt.Core.Rules;

namespace Dpt.App.ViewModels;

/// <summary>One "these files go here" row in the layout editor.</summary>
public sealed partial class TemplateRow : ObservableObject
{
    public static IReadOnlyList<Choice<string?>> Types { get; } =
    [
        new("Any file", null), new("Pictures", "image"), new("Videos", "video"), new("Music", "audio"), new("Documents", "doc"),
        new("Archives", "archive"), new("Apps & installers", "app"), new("Code", "code"),
    ];

    public TemplateRow(LayoutTemplate t)
    {
        var type = t.Conditions.Clauses.FirstOrDefault(c => c.Field == ConditionField.TypeGroup)?.Value;
        _type = Types.FirstOrDefault(x => x.Value == type) ?? Types[0];
        _nameContains = t.Conditions.Clauses.FirstOrDefault(c => c.Field == ConditionField.Name)?.Value ?? "";
        _extensions = t.Conditions.Clauses.FirstOrDefault(c => c.Field == ConditionField.Extension)?.Value ?? "";
        _path = t.Path;
    }

    [ObservableProperty] private Choice<string?> _type;
    [ObservableProperty] private string _nameContains;
    [ObservableProperty] private string _extensions;
    [ObservableProperty] private string _path;

    public LayoutTemplate ToTemplate()
    {
        var clauses = new List<RuleClause>();
        if (Type.Value is { } t) clauses.Add(new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, t));
        if (!string.IsNullOrWhiteSpace(Extensions)) clauses.Add(new RuleClause(ConditionField.Extension, ConditionOperator.IsOneOf, Extensions.Trim()));
        if (!string.IsNullOrWhiteSpace(NameContains)) clauses.Add(new RuleClause(ConditionField.Name, ConditionOperator.Contains, NameContains.Trim()));
        return new LayoutTemplate { Conditions = new RuleConditionGroup { Clauses = clauses }, Path = Path.Trim() };
    }
}

public sealed record TreeRow(string Name, string Detail, IReadOnlyList<TreeRow> Children);

/// <summary>A previewed layout shown in the comparison area.</summary>
public sealed record LayoutPreview(StoredLayout Layout, LayoutPlan Plan)
{
    public string Name => Layout.Definition.Name;

    public string Stats =>
        $"{Plan.MoveCount:N0} files move ({SizeFormatter.Format(Plan.MoveBytes)}) · {Plan.UnchangedCount:N0} already in place · {Plan.UnmatchedCount:N0} left where they are · {Plan.FolderCount:N0} folders" +
        (Plan.CollisionCount > 0 ? $" · {Plan.CollisionCount:N0} name clashes (numbers added)" : "") +
        (Plan.ProblemCount > 0 ? $" · {Plan.ProblemCount:N0} problems" : "");

    public IReadOnlyList<TreeRow> Tree => [ToRow(Plan.Tree)];

    private static TreeRow ToRow(LayoutNode n) =>
        new(n.Name, $"{n.TotalFiles:N0} files · {SizeFormatter.Format(n.TotalBytes)}", n.Children.Values.Select(ToRow).ToList());
}

/// <summary>Try out folder structures: edit layouts, preview them side by side without moving anything, then apply one or turn it into rules.</summary>
public sealed partial class LayoutsPageViewModel : PageViewModel
{
    public LayoutsPageViewModel(TabViewModel tab, Location location) : base(tab, location)
    {
        Load();
        if (Layouts.Count == 0) New(location.Path);
        else Selected = Layouts[0];
    }

    public override string IconKey => "Icon.Layout";

    public ObservableCollection<StoredLayout> Layouts { get; } = [];
    public ObservableCollection<StoredLayout> CompareSet { get; } = [];
    public ObservableCollection<LayoutPreview> Previews { get; } = [];
    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<TemplateRow> Templates { get; } = [];

    [ObservableProperty] private StoredLayout? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _targetRoot = "";
    [ObservableProperty] private string _fallback = "Other";
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private bool _removeEmptyFolders = true;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private string? _message;

    private long? _editingId;

    partial void OnSelectedChanged(StoredLayout? value)
    {
        if (value is null) return;
        _editingId = value.Id;
        var d = value.Definition;
        Name = d.Name;
        TargetRoot = d.TargetRoot;
        Fallback = d.FallbackPath ?? "";
        IncludeSubfolders = d.IncludeSubfolders;
        RemoveEmptyFolders = d.RemoveEmptyFolders;
        Sources.Clear();
        foreach (var s in d.Sources) Sources.Add(s);
        Templates.Clear();
        foreach (var t in d.Templates) Templates.Add(new TemplateRow(t));
    }

    private void Load()
    {
        var id = Selected?.Id;
        Layouts.Clear();
        foreach (var l in Services.Layouts.GetLayouts()) Layouts.Add(l);
        if (id is not null) Selected = Layouts.FirstOrDefault(l => l.Id == id);
    }

    private LayoutDefinition Current() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Layout" : Name.Trim(),
        Sources = Sources.ToList(),
        IncludeSubfolders = IncludeSubfolders,
        TargetRoot = TargetRoot.Trim(),
        Templates = Templates.Select(t => t.ToTemplate()).Where(t => t.Path.Length > 0).ToList(),
        FallbackPath = string.IsNullOrWhiteSpace(Fallback) ? null : Fallback.Trim(),
        RemoveEmptyFolders = RemoveEmptyFolders,
    };

    private string? Validate(LayoutDefinition d) =>
        d.Sources.Count == 0 ? "Add at least one folder to reorganize."
        : d.TargetRoot.Length < 3 || !Path.IsPathFullyQualified(d.TargetRoot) ? "Choose the folder the new structure goes in."
        : d.Templates.Count == 0 && d.FallbackPath is null ? "Add at least one line saying where files go."
        : null;

    [RelayCommand]
    private void New(string? folder = null)
    {
        _editingId = null;
        Selected = null;
        Name = "My layout";
        Sources.Clear();
        if (folder is not null) Sources.Add(folder);
        TargetRoot = folder ?? "";
        Fallback = "Other";
        IncludeSubfolders = true;
        RemoveEmptyFolders = true;
        Templates.Clear();
        Templates.Add(new TemplateRow(new LayoutTemplate { Conditions = new RuleConditionGroup { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, "image")] }, Path = @"Media\Pictures\{year}" }));
        Templates.Add(new TemplateRow(new LayoutTemplate { Conditions = new RuleConditionGroup { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, "video")] }, Path = @"Media\Videos\{year}" }));
        Templates.Add(new TemplateRow(new LayoutTemplate { Conditions = new RuleConditionGroup { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, "doc")] }, Path = @"Documents\{ext}" }));
    }

    [RelayCommand]
    private void Save()
    {
        var d = Current();
        if (Validate(d) is { } problem)
        {
            Message = problem;
            return;
        }

        _editingId = Services.Layouts.Save(_editingId, d);
        Message = "Saved.";
        Load();
        Selected = Layouts.FirstOrDefault(l => l.Id == _editingId);
    }

    [RelayCommand]
    private void SaveAsCopy()
    {
        _editingId = null;
        Name += " (copy)";
        Save();
    }

    [RelayCommand]
    private void Delete()
    {
        if (_editingId is not { } id) return;
        if (!Services.FileOps.Dialogs.Confirm("Delete layout", $"Delete the layout \"{Name}\"? No files are changed.", "Delete", danger: true)) return;
        Services.Layouts.Delete(id);
        foreach (var p in Previews.Where(p => p.Layout.Id == id).ToList()) Previews.Remove(p);
        Load();
        Selected = Layouts.FirstOrDefault();
        if (Selected is null) New();
    }

    [RelayCommand]
    private void AddTemplate() => Templates.Add(new TemplateRow(new LayoutTemplate { Path = "{type}" }));

    [RelayCommand]
    private void RemoveTemplate(TemplateRow? row)
    {
        if (row is not null) Templates.Remove(row);
    }

    [RelayCommand]
    private void MoveTemplateUp(TemplateRow? row)
    {
        if (row is null) return;
        var i = Templates.IndexOf(row);
        if (i > 0) Templates.Move(i, i - 1);
    }

    public void AddSource(string folder)
    {
        if (!Sources.Contains(folder, StringComparer.OrdinalIgnoreCase)) Sources.Add(folder);
        if (string.IsNullOrWhiteSpace(TargetRoot)) TargetRoot = folder;
    }

    [RelayCommand]
    private void RemoveSource(string? s)
    {
        if (s is not null) Sources.Remove(s);
    }

    /// <summary>Saves the layout being edited and adds it to the side-by-side comparison (up to three).</summary>
    [RelayCommand]
    private async Task Preview()
    {
        Save();
        if (Message != "Saved." || Selected is null) return;
        await AddPreviewAsync(Selected);
    }

    public async Task AddPreviewAsync(StoredLayout layout)
    {
        IsPreviewing = true;
        try
        {
            var plan = await Services.Layouts.PlanAsync(layout.Definition);
            var existing = Previews.FirstOrDefault(p => p.Layout.Id == layout.Id);
            if (existing is not null) Previews.Remove(existing);
            Previews.Insert(0, new LayoutPreview(layout, plan));
            while (Previews.Count > 3) Previews.RemoveAt(Previews.Count - 1);
            Message = null;
            StatusText = $"Previewing {Previews.Count} layout(s). Nothing has been moved.";
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    [RelayCommand]
    private void ClosePreview(LayoutPreview? p)
    {
        if (p is not null) Previews.Remove(p);
    }

    [RelayCommand]
    private async Task Apply(LayoutPreview? p)
    {
        if (p is null) return;
        // Re-plan right before applying so the files moved are exactly what exists now.
        var plan = await Services.Layouts.PlanAsync(p.Layout.Definition);
        if (!Services.FileOps.Dialogs.Confirm("Apply layout",
                $"Move {plan.MoveCount:N0} files ({SizeFormatter.Format(plan.MoveBytes)}) into the \"{p.Name}\" structure under {p.Layout.Definition.TargetRoot}?" +
                (plan.CollisionCount > 0 ? $" {plan.CollisionCount:N0} files share a name with another; numbers will be added so nothing is overwritten." : "") +
                " You can undo the whole layout afterwards with Ctrl+Z.", "Apply")) return;
        var result = await Services.Layouts.ApplyAsync(plan);
        Tab.Main.ShowToast($"Layout applied: {result.Moved:N0} files moved" + (result.Failed > 0 ? $", {result.Failed} couldn't be moved" : "") + ". Ctrl+Z undoes it.");
        await AddPreviewAsync(p.Layout);
    }

    [RelayCommand]
    private void MakeRules(LayoutPreview? p)
    {
        if (p is null) return;
        var ids = Services.Layouts.CreateRules(p.Layout.Definition);
        Tab.Main.ShowToast($"Created {ids.Count} rule(s) for new files. They'll ask for a preview before moving anything.");
        Services.RuleRunner.RefreshWatchers();
    }

    [RelayCommand]
    private void OpenTarget(LayoutPreview? p)
    {
        if (p is not null && Directory.Exists(p.Layout.Definition.TargetRoot)) Tab.Navigate(Location.Folder(p.Layout.Definition.TargetRoot));
    }
}
