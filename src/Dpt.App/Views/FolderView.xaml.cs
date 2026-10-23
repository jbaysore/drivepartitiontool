using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Core.Settings;
using Dpt.Shell;
using Microsoft.Win32;

namespace Dpt.App.Views;

public partial class FolderView : UserControl
{
    private FolderPageViewModel? _vm;
    private Point _dragStart;
    private bool _dragArmed;

    public FolderView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusList();
    }

    private Selector ActiveList => _vm?.IsThumbnails == true ? Tiles : List;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SelectRequested -= OnSelectRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as FolderPageViewModel;
        if (_vm is null) return;
        _vm.SelectRequested += OnSelectRequested;
        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateViewToggle();
        ApplyColumnSettings();
        Dispatcher.BeginInvoke(FocusList, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderPageViewModel.ViewMode))
        {
            UpdateViewToggle();
            Dispatcher.BeginInvoke(FocusList, System.Windows.Threading.DispatcherPriority.Input);
        }
        else if (e.PropertyName is nameof(FolderPageViewModel.SortColumn) or nameof(FolderPageViewModel.SortAscending))
        {
            UpdateSortGlyphs();
        }
    }

    private void UpdateViewToggle()
    {
        var thumbs = _vm?.ViewMode == FolderViewMode.Thumbnails;
        ViewIcon.Data = (Geometry)FindResource(thumbs ? "Icon.List" : "Icon.Grid");
        ViewText.Text = thumbs ? "Details" : "Thumbnails";
    }

    private void ApplyColumnSettings()
    {
        var visible = AppServices.Current.Settings.FolderColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (List.View is not GridView grid) return;
        void Toggle(GridViewColumn col, string key, double width)
        {
            var show = visible.Contains(key) || key == "Name";
            if (show && !grid.Columns.Contains(col)) grid.Columns.Add(col);
            if (!show) grid.Columns.Remove(col);
            if (show && col.Width == 0) col.Width = width;
        }

        Toggle(ModifiedColumn, "Modified", 160);
        Toggle(TypeColumn, "Type", 170);
        Toggle(CategoriesColumn, "Categories", 150);
        Toggle(SizeColumn, "Size", 110);
        UpdateSortGlyphs();
    }

    private void UpdateSortGlyphs()
    {
        if (_vm is null) return;
        var arrow = _vm.SortAscending ? " ▲" : " ▼";
        NameColumn.Header = "Name" + (_vm.SortColumn == SortColumn.Name ? arrow : "");
        ModifiedColumn.Header = "Date modified" + (_vm.SortColumn == SortColumn.Modified ? arrow : "");
        TypeColumn.Header = "Type" + (_vm.SortColumn == SortColumn.Type ? arrow : "");
        SizeColumn.Header = "Size" + (_vm.SortColumn == SortColumn.Size ? arrow : "");
    }

    private void FocusList()
    {
        var list = ActiveList;
        if (!list.IsVisible) return;
        if (list.SelectedIndex < 0 && list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        if (list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) is UIElement row) row.Focus();
        else list.Focus();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not ListBox list) return;
        _vm.SetSelection(list.SelectedItems.Cast<FileItemViewModel>().ToList());
    }

    private void OnSelectRequested(object? sender, (FileItemViewModel Item, bool Rename) e)
    {
        var list = (ListBox)ActiveList;
        list.SelectedItems.Clear();
        list.SelectedItem = e.Item;
        list.ScrollIntoView(e.Item);
        Dispatcher.BeginInvoke(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromItem(e.Item) is ListBoxItem row) row.Focus();
            if (e.Rename) _vm?.BeginRename(e.Item);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
        var column = col == ModifiedColumn ? SortColumn.Modified : col == TypeColumn ? SortColumn.Type : col == SizeColumn ? SortColumn.Size : SortColumn.Name;
        _vm.SortCommand.Execute(column);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement { DataContext: FileItemViewModel item } || item.IsEditing) return;
        _vm?.OpenCommand.Execute(item);
        e.Handled = true;
    }

    private void OnListMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _vm is null) return;
        if (ItemUnder(e.OriginalSource) is { IsFolder: true } item && AppServices.Current.Settings.MiddleClickOpensTab)
        {
            _vm.OpenInNewTabCommand.Execute(item);
            e.Handled = true;
        }
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.OriginalSource is TextBox) return; // rename box handles its own keys
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var handled = true;
        switch (key)
        {
            case Key.Enter when alt: _vm.PropertiesCommand.Execute(null); break;
            case Key.Enter when ctrl: _vm.OpenInNewTabCommand.Execute(null); break;
            case Key.Enter: _vm.OpenCommand.Execute(null); break;
            case Key.Back: _vm.Tab.UpCommand.Execute(null); break;
            case Key.F2: await _vm.RenameCommand.ExecuteAsync(null); break;
            case Key.Delete when shift: await _vm.PermanentDeleteCommand.ExecuteAsync(null); break;
            case Key.Delete: await _vm.RecycleCommand.ExecuteAsync(null); break;
            case Key.C when ctrl && shift: _vm.CopyPathCommand.Execute(null); break;
            case Key.C when ctrl: _vm.CopyCommand.Execute(null); break;
            case Key.X when ctrl: _vm.CutCommand.Execute(null); break;
            case Key.V when ctrl: await _vm.PasteCommand.ExecuteAsync(null); break;
            case Key.N when ctrl && shift: await _vm.NewFolderCommand.ExecuteAsync(null); break;
            case Key.D when ctrl: await _vm.DuplicateCommand.ExecuteAsync(null); break;
            case Key.K when ctrl: _vm.EditCategoriesCommand.Execute(null); break;
            case Key.Apps: ShowContextMenu(null); break;
            default: handled = false; break;
        }

        e.Handled = handled;
    }

    // ----- Inline rename -----

    private void OnRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            var text = box.Text;
            var isFolder = (box.DataContext as FileItemViewModel)?.IsFolder == true;
            var dot = isFolder ? -1 : text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : text.Length);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItemViewModel item } || _vm is null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.CommitRenameAsync(item);
            FocusList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CancelRename(item);
            FocusList();
        }
    }

    private async void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileItemViewModel { IsEditing: true } item } && _vm is not null)
            await _vm.CommitRenameAsync(item);
    }

    // ----- Native context menu -----

    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemUnder(e.OriginalSource);
        var list = (ListBox)sender;
        if (item is not null && !list.SelectedItems.Contains(item))
        {
            list.SelectedItems.Clear();
            list.SelectedItem = item;
        }
        else if (item is null)
        {
            list.SelectedItems.Clear();
        }

        ShowContextMenu(PointToScreen(e.GetPosition(this)));
        e.Handled = true;
    }

    private async void ShowContextMenu(Point? screen)
    {
        if (_vm is null) return;
        var p = screen ?? PointToScreen(new Point(ActualWidth / 3, ActualHeight / 3));
        var extended = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var owner = AppServices.Current.OwnerWindow;
        var verb = _vm.SelectedItems.Count > 0
            ? ShellContextMenu.ShowForItems(owner, _vm.SelectedItems.Select(i => i.Path).ToList(), (int)p.X, (int)p.Y, extended)
            : ShellContextMenu.ShowForFolderBackground(owner, _vm.FolderPath, (int)p.X, (int)p.Y, extended);
        if (verb is not null) await _vm.HandleShellVerbAsync(verb);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // ----- Move to / Copy to -----

    private async void OnMoveToClick(object sender, RoutedEventArgs e) => await TransferToAsync(move: true);

    private async void OnCopyToClick(object sender, RoutedEventArgs e) => await TransferToAsync(move: false);

    private async Task TransferToAsync(bool move)
    {
        if (_vm is null || _vm.SelectedItems.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = move ? "Move to…" : "Copy to…", InitialDirectory = _vm.FolderPath };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var paths = _vm.SelectedItems.Select(i => i.Path).ToList();
        if (move) await AppServices.Current.FileOps.MoveAsync(paths, dialog.FolderName);
        else await AppServices.Current.FileOps.CopyAsync(paths, dialog.FolderName);
    }

    // ----- Drag and drop (Explorer-compatible) -----

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragArmed = ItemUnder(e.OriginalSource) is not null && e.OriginalSource is not TextBox;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _vm is null || _vm.SelectedItems.Count == 0) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var files = new StringCollection();
        foreach (var i in _vm.SelectedItems) files.Add(i.Path);
        var data = new DataObject();
        data.SetFileDropList(files);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _vm is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        var target = ItemUnder(e.OriginalSource) is { IsFolder: true } f ? f.Path : _vm.FolderPath;
        if (paths.Any(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase)))
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            var ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            var shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            var sameDrive = paths.All(p => string.Equals(Path.GetPathRoot(p), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
            e.Effects = shift ? DragDropEffects.Move : ctrl ? DragDropEffects.Copy : sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        var target = ItemUnder(e.OriginalSource) is { IsFolder: true } f ? f.Path : null;
        var ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        e.Handled = true;
        await _vm.DropAsync(paths, target, forceMove: shift, forceCopy: ctrl);
    }

    private static FileItemViewModel? ItemUnder(object source) =>
        MainWindow.FindAncestor<ListBoxItem>(source as DependencyObject)?.DataContext as FileItemViewModel;
}
