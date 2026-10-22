using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Dpt.App.Views;

public sealed partial class DialogRow : ObservableObject
{
    public DialogRow(string text, string? detail = null, IReadOnlyList<string>? options = null, int selected = 0)
    {
        Text = text;
        Detail = detail ?? "";
        Options = options ?? [];
        _selected = selected;
    }

    public string Text { get; }
    public string Detail { get; }
    public IReadOnlyList<string> Options { get; }
    public bool HasOptions => Options.Count > 0;

    [ObservableProperty] private int _selected;
}

public sealed record DialogButton(string Label, string Result, Style Style, bool IsDefault = false, bool IsCancel = false)
{
    public string AutomationId => "Dialog_" + Result;
}

/// <summary>One reusable dialog: heading, message, optional input, optional item rows (with per-row choices), and buttons.</summary>
public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            if (HasInput)
            {
                Input.Focus();
                Input.SelectAll();
            }
        };
    }

    public string Heading { get; init; } = "";
    public string Message { get; init; } = "";
    public string IconKey { get; init; } = "Icon.Info";
    public Brush IconBrush { get; init; } = Brushes.Gray;
    public string? InputText { get; set; }
    public bool HasInput { get; init; }
    public ObservableCollection<DialogRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public IReadOnlyList<string> ChoiceLabels { get; init; } = [];
    public bool HasChoices => ChoiceLabels.Count > 0;
    public ObservableCollection<DialogButton> Buttons { get; } = [];

    public string? Result { get; private set; }

    private void OnButton(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DialogButton b })
        {
            Result = b.Result;
            DialogResult = !b.IsCancel;
        }
    }

    private void OnSetAll(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string label }) return;
        foreach (var row in Rows)
        {
            var i = row.Options.ToList().IndexOf(label);
            if (i >= 0) row.Selected = i;
        }
    }
}
