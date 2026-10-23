using System.Windows;
using Dpt.App.ViewModels;
using Dpt.Data;
using Microsoft.Win32;

namespace Dpt.App.Views;

public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow(RuleEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private RuleEditorViewModel Vm => (RuleEditorViewModel)DataContext;

    public long? SavedId { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Vm.Problem is not null) return;
        SavedId = Vm.Save();
        DialogResult = true;
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Folder for this rule to look in" };
        if (d.ShowDialog(this) == true) Vm.AddFolder(d.FolderName);
    }

    private void OnBrowseDest(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Move matching items to…" };
        if (d.ShowDialog(this) == true) Vm.MoveTo = d.FolderName;
    }

    /// <summary>Opens the editor; after saving a rule that moves files, offers its preview.</summary>
    public static async Task<long?> EditAsync(StoredRule? rule)
    {
        var w = new RuleEditorWindow(new RuleEditorViewModel(rule)) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.SavedId is not { } id) return null;
        var saved = Services.AppServices.Current.Rules.Get(id);
        if (saved is { Previewed: false } && saved.Definition.Moves) await RulePreviewWindow.ShowAsync(saved);
        return id;
    }
}
