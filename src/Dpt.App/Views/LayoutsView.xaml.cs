using System.Windows;
using System.Windows.Controls;
using Dpt.App.ViewModels;
using Microsoft.Win32;

namespace Dpt.App.Views;

public partial class LayoutsView : UserControl
{
    public LayoutsView() => InitializeComponent();

    private LayoutsPageViewModel? Vm => DataContext as LayoutsPageViewModel;

    private void OnAddSource(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Folder to reorganize" };
        if (d.ShowDialog(Window.GetWindow(this)) == true) Vm?.AddSource(d.FolderName);
    }

    private void OnBrowseTarget(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Build the new structure in…" };
        if (d.ShowDialog(Window.GetWindow(this)) == true && Vm is { } vm) vm.TargetRoot = d.FolderName;
    }
}
