using System.Windows;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Core.Rules;
using Dpt.Data;

namespace Dpt.App.Views;

public sealed record PreviewRow(string Name, string From, string To, string Tags, string Note);

/// <summary>Shows what a rule would do (moves, tags, conflicts) before anything changes; "Run now" applies exactly that plan.</summary>
public partial class RulePreviewWindow : Window
{
    private readonly RulePlan _plan;

    public RulePreviewWindow(RulePlan plan)
    {
        InitializeComponent();
        _plan = plan;
        var d = plan.Rule.Definition;
        Heading.Text = $"Preview: {d.Name}";
        Summary.Text = RuleDescriber.Describe(d);
        Totals.Text = plan.Entries.Count == 0
            ? $"Nothing matches right now ({plan.Scanned:N0} items checked)."
            : $"{plan.Entries.Count:N0} item(s) match · {plan.MoveCount:N0} would move ({SizeFormatter.Format(plan.MoveBytes)}) · {plan.TagCount:N0} would be tagged"
              + (plan.ConflictCount > 0 ? $" · {plan.ConflictCount} already have an item with the same name at the destination" : "");
        List.ItemsSource = plan.Entries.Select(e => new PreviewRow(
            Path.GetFileName(e.Path),
            Path.GetDirectoryName(e.Path) ?? "",
            e.Action.Moves ? e.Action.MoveToFolder! : "(stays)",
            string.Join(", ", e.Action.AddCategories),
            e.Action.Note ?? (e.DestinationExists ? d.OnConflict switch
            {
                ConflictPolicy.Skip => "Name taken: will skip",
                ConflictPolicy.Replace => "Name taken: will replace",
                _ => "Name taken: will keep both",
            } : ""))).ToList();
        RunButton.IsEnabled = plan.Entries.Any(e => e.Action.Note is null);
    }

    public RuleRunResult? Result { get; private set; }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        Result = await AppServices.Current.RuleRunner.RunAsync(_plan, fromPreview: true);
        DialogResult = true;
    }

    public static async Task ShowAsync(StoredRule rule)
    {
        var plan = await AppServices.Current.RuleRunner.PlanAsync(rule);
        var w = new RulePreviewWindow(plan) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() == true && w.Result is { } r && Application.Current.MainWindow?.DataContext is ViewModels.MainViewModel main)
            main.ShowToast($"\"{rule.Definition.Name}\": {r.Moved} moved, {r.Tagged} tagged. Ctrl+Z undoes it.");
    }
}
