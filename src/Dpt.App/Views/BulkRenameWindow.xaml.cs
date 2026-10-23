using System.Windows;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Core.Operations;

namespace Dpt.App.Views;

public partial class BulkRenameWindow : Window
{
    public BulkRenameWindow(BulkRenameViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private BulkRenameViewModel Vm => (BulkRenameViewModel)DataContext;

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Shows the dialog for the given items and performs the rename if confirmed.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<RenameInput> items)
    {
        var vm = new BulkRenameViewModel(items);
        var w = new BulkRenameWindow(vm) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true) return 0;
        var changes = vm.Plan.Where(p => p.Changes).Select(p => (p.Path, p.NewName)).ToList();
        return await AppServices.Current.FileOps.RenameManyAsync(changes, $"Rename {changes.Count} items");
    }
}
