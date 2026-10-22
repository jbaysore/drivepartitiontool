using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Ipc;
using Dpt.Shell;

namespace Dpt.App.Views;

public partial class SearchView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;

    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text.Length;
        };
    }

    private SearchPageViewModel? Vm => DataContext as SearchPageViewModel;

    private List<FileItemViewModel> Selection => Results.SelectedItems.Cast<FileItemViewModel>().ToList();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItemViewModel item }) Vm?.OpenCommand.Execute(item);
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var handled = true;
        switch (key)
        {
            case Key.Enter when ctrl: Vm.OpenLocationCommand.Execute(null); break;
            case Key.Enter: Vm.OpenCommand.Execute(null); break;
            case Key.C when ctrl && shift: Vm.CopyPathCommand.Execute(null); break;
            case Key.C when ctrl: FileClipboard.SetFiles(Selection.Select(i => i.Path), ClipboardFileAction.Copy); break;
            case Key.X when ctrl: FileClipboard.SetFiles(Selection.Select(i => i.Path), ClipboardFileAction.Cut); break;
            case Key.Delete when shift: await AppServices.Current.FileOps.PermanentDeleteAsync(Selection.Select(i => i.Path).ToList()); Vm.Refresh(); break;
            case Key.Delete: await Vm.RecycleAsync(Selection); break;
            case Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt): Vm.TogglePreviewCommand.Execute(null); break;
            default: handled = false; break;
        }

        e.Handled = handled;
    }

    private async void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        var item = MainWindow.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileItemViewModel;
        if (item is null) return;
        if (!Results.SelectedItems.Contains(item))
        {
            Results.SelectedItems.Clear();
            Results.SelectedItem = item;
        }

        // Shell menus need items from one folder; with mixed folders, show the menu for the clicked item only.
        var sel = Selection;
        var paths = sel.Select(i => Path.GetDirectoryName(i.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? sel.Select(i => i.Path).ToList()
            : [item.Path];
        var p = PointToScreen(e.GetPosition(this));
        e.Handled = true;
        var verb = ShellContextMenu.ShowForItems(AppServices.Current.OwnerWindow, paths, (int)p.X, (int)p.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        switch (verb)
        {
            case "delete": await Vm.RecycleAsync(sel.Where(s => paths.Contains(s.Path)).ToList()); break;
            case "copy": FileClipboard.SetFiles(paths, ClipboardFileAction.Copy); break;
            case "cut": FileClipboard.SetFiles(paths, ClipboardFileAction.Cut); break;
            case "properties": ShellLauncher.ShowProperties(AppServices.Current.OwnerWindow, paths[0]); break;
            case "rename": Vm.OpenLocationCommand.Execute(item); break;
            case "pintohome": foreach (var path in paths.Where(Directory.Exists)) AppServices.Current.Locations.AddPin(path); break;
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
        var field = col == NameCol ? SortField.Name : col == LocationCol ? SortField.Path : col == SizeCol ? SortField.Size : col == ModifiedCol ? SortField.Modified : SortField.Relevance;
        Vm.SortByCommand.Execute(field);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragArmed = MainWindow.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var files = new StringCollection();
        foreach (var i in Selection) files.Add(i.Path);
        if (files.Count == 0) return;
        var data = new DataObject();
        data.SetFileDropList(files);
        DragDrop.DoDragDrop(Results, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }
}
