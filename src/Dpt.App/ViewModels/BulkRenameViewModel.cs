using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.Core.Operations;

namespace Dpt.App.ViewModels;

/// <summary>Options and live preview for renaming several items at once.</summary>
public sealed partial class BulkRenameViewModel : ObservableObject
{
    private readonly IReadOnlyList<RenameInput> _items;

    public BulkRenameViewModel(IReadOnlyList<RenameInput> items)
    {
        _items = items;
        Update();
    }

    public ObservableCollection<RenamePlanItem> Preview { get; } = [];

    public IReadOnlyList<string> CaseOptions { get; } = ["Keep as is", "lowercase", "UPPERCASE", "Title Case"];

    [ObservableProperty] private string _template = "{name}";
    [ObservableProperty] private string _find = "";
    [ObservableProperty] private string _replace = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private int _caseIndex;
    [ObservableProperty] private string _startNumber = "1";
    [ObservableProperty] private string _extension = "";
    [ObservableProperty] private bool _changeExtension;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canApply;

    partial void OnTemplateChanged(string value) => Update();
    partial void OnFindChanged(string value) => Update();
    partial void OnReplaceChanged(string value) => Update();
    partial void OnUseRegexChanged(bool value) => Update();
    partial void OnMatchCaseChanged(bool value) => Update();
    partial void OnCaseIndexChanged(int value) => Update();
    partial void OnStartNumberChanged(string value) => Update();
    partial void OnExtensionChanged(string value) => Update();
    partial void OnChangeExtensionChanged(bool value) => Update();

    public IReadOnlyList<RenamePlanItem> Plan { get; private set; } = [];

    private void Update()
    {
        var options = new BulkRenameOptions
        {
            Template = Template,
            Find = Find,
            Replace = Replace,
            UseRegex = UseRegex,
            MatchCase = MatchCase,
            Case = (NameCase)Math.Clamp(CaseIndex, 0, 3),
            StartNumber = int.TryParse(StartNumber, out var n) ? n : 1,
            ChangeExtension = ChangeExtension ? Extension : null,
        };
        Plan = BulkRenamePlanner.Plan(_items, options, p => File.Exists(p) || Directory.Exists(p));
        Preview.Clear();
        foreach (var p in Plan) Preview.Add(p);
        var problems = Plan.Count(p => p.Problem is not null);
        var changes = Plan.Count(p => p.Changes);
        CanApply = problems == 0 && changes > 0;
        Summary = problems > 0 ? $"{problems} problem{(problems == 1 ? "" : "s")} to fix before renaming."
            : changes == 0 ? "Nothing would change yet." : $"{changes} of {Plan.Count} items will be renamed. You can undo this with Ctrl+Z.";
    }
}
