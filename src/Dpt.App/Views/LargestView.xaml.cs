using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Shell;
using Microsoft.Win32;

namespace Dpt.App.Views;

public partial class LargestView : UserControl
{
    public LargestView() => InitializeComponent();

    private LargestPageViewModel? Vm => DataContext as LargestPageViewModel;
    private List<SizeRowViewModel> Selection => List.SelectedItems.Cast<SizeRowViewModel>().ToList();

    private void OnFilesChecked(object sender, RoutedEventArgs e)
    {
        if (Vm is { ShowFolders: true } vm) vm.ShowFolders = false;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SizeRowViewModel row }) Vm?.DrillCommand.Execute(row);
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                Vm.DrillCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Back:
                if (Vm.IsDrilling) Vm.DrillUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                e.Handled = true;
                await Vm.RecycleAsync(Selection);
                break;
        }
    }

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.RecycleAsync(Selection);
    }

    private async void OnMoveTo(object sender, RoutedEventArgs e)
    {
        if (Vm is null || Selection.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = "Move to…" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await Vm.MoveAsync(Selection, dialog.FolderName);
    }

    private async void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || MainWindow.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is not SizeRowViewModel row) return;
        if (!List.SelectedItems.Contains(row))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = row;
        }

        var p = PointToScreen(e.GetPosition(this));
        e.Handled = true;
        var verb = ShellContextMenu.ShowForItems(AppServices.Current.OwnerWindow, [row.Path], (int)p.X, (int)p.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        switch (verb)
        {
            case "delete": await Vm.RecycleAsync([row]); break;
            case "copy": FileClipboard.SetFiles([row.Path], ClipboardFileAction.Copy); break;
            case "cut": FileClipboard.SetFiles([row.Path], ClipboardFileAction.Cut); break;
            case "properties": ShellLauncher.ShowProperties(AppServices.Current.OwnerWindow, row.Path); break;
            case "rename": Vm.OpenLocationCommand.Execute(row); break;
        }
    }
}
