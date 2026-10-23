using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Shell;

namespace Dpt.App.Views;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private Point _tabDragStart;
    private TabViewModel? _dragTab;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizedState();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, e) =>
        {
            // Mouse back/forward buttons, like File Explorer and browsers.
            var tab = Vm?.SelectedTab;
            if (e.ChangedButton == MouseButton.XButton1 && tab?.BackCommand.CanExecute(null) == true) tab.BackCommand.Execute(null);
            else if (e.ChangedButton == MouseButton.XButton2 && tab?.ForwardCommand.CanExecute(null) == true) tab.ForwardCommand.Execute(null);
        };
        Closing += OnClosing;
    }

    private Infrastructure.TrayIcon? _tray;
    private bool _exitRequested;
    private bool _toldAboutTray;

    /// <summary>Started with --tray (sign-in): stay hidden in the notification area.</summary>
    public bool StartInTray { get; set; }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsFailing) return;
        Vm?.SaveTabs();
        var s = AppServices.Current;
        var busy = s.Activity.HasWorkInProgress;
        if (busy || !_exitRequested && s.Settings.KeepRunningInTray && s.RuleRunner.HasAutomaticRules)
        {
            // Keep automatic rules working: hide to the notification area instead of exiting.
            e.Cancel = true;
            HideToTray();
            if (!_toldAboutTray)
            {
                _tray?.Balloon("Still running", busy ? "File operations are still running. Open DrivePartitionTool from this icon to review or cancel them." : "Automatic rules keep working. Click the icon to open DrivePartitionTool, or right-click to exit.");
                _toldAboutTray = true;
            }

            return;
        }

        // Atomically refuse jobs that race with closing the last window.
        if (!s.Activity.TryBeginShutdown()) { e.Cancel = true; return; }

        _tray?.Dispose();
    }

    public void HideToTray()
    {
        _tray ??= new Infrastructure.TrayIcon(new WindowInteropHelper(this).Handle);
        _tray.Show("DrivePartitionTool — automatic rules running");
        Hide();
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray?.Hide();
    }

    public void ExitApp()
    {
        if (AppServices.Current.Activity.HasWorkInProgress)
        {
            RestoreFromTray();
            if (Vm is not null) Vm.IsActivityOpen = true;
            AppServices.Current.FileOps.Dialogs.ShowInfo("Operations are still running", "Wait for the file or disk operations to finish, or cancel them in the activity list, before exiting.");
            return;
        }
        _exitRequested = true;
        Close();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open DrivePartitionTool" };
        open.Click += (_, _) => RestoreFromTray();
        var pause = new MenuItem { Header = "Pause automatic rules", IsCheckable = true, IsChecked = AppServices.Current.Settings.PauseAutomaticRules };
        pause.Click += (_, _) =>
        {
            var copy = AppServices.Current.Settings.Clone();
            copy.PauseAutomaticRules = !copy.PauseAutomaticRules;
            AppServices.Current.SaveSettings(copy);
            AppServices.Current.RuleRunner.RefreshWatchers();
        };
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        AppServices.Current.OwnerWindow = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        if (StartInTray) Dispatcher.BeginInvoke(HideToTray, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
            Dispatcher.BeginInvoke(() => AppServices.Current.Drives.Refresh());
        if (msg == Infrastructure.TrayIcon.CallbackMessage)
        {
            var mouse = (int)lParam & 0xFFFF;
            if (mouse is Infrastructure.TrayIcon.WM_LBUTTONUP or Infrastructure.TrayIcon.WM_LBUTTONDBLCLK) Dispatcher.BeginInvoke(RestoreFromTray);
            else if (mouse == Infrastructure.TrayIcon.WM_RBUTTONUP) Dispatcher.BeginInvoke(ShowTrayMenu);
            handled = true;
            return IntPtr.Zero;
        }
        var result = ShellContextMenu.HandleWindowMessage(hwnd, msg, wParam, lParam, ref handled);
        return handled ? result : IntPtr.Zero;
    }

    private void UpdateMaximizedState()
    {
        // With a custom chrome, a maximized window extends past the screen edge by the resize border.
        RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);
        MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var tab = Vm?.SelectedTab;
        // Navigation keys are handled on the way down, before lists and text boxes consume arrow keys.
        if ((alt && key == Key.Left) || key == Key.BrowserBack)
        {
            if (tab?.BackCommand.CanExecute(null) == true) tab.BackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if ((alt && key == Key.Right) || key == Key.BrowserForward)
        {
            if (tab?.ForwardCommand.CanExecute(null) == true) tab.ForwardCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (alt && key == Key.Up)
        {
            if (tab?.UpCommand.CanExecute(null) == true) tab.UpCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F4 && !ctrl && !alt)
        {
            Vm?.SelectedTab?.EditAddressCommand.Execute(null);
            e.Handled = true;
        }
        else if ((ctrl && (key == Key.F || key == Key.E)) || key == Key.F3)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (ctrl && key >= Key.D1 && key <= Key.D9 && Vm is { } vm)
        {
            var index = key == Key.D9 ? vm.Tabs.Count - 1 : key - Key.D1;
            if (index < vm.Tabs.Count) vm.SelectedTab = vm.Tabs[index];
            e.Handled = true;
        }
    }

    public void FocusSearch()
    {
        if (Vm?.SelectedTab?.IsHome == true)
        {
            // Home has its own large search box.
            var box = FindDescendant<TextBox>(this, t => t.Name == "HomeSearchBox");
            box?.Focus();
            box?.SelectAll();
            return;
        }

        TabSearchBox.Focus();
        TabSearchBox.SelectAll();
    }

    private void OnAddressClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;
        Vm?.SelectedTab?.EditAddressCommand.Execute(null);
    }

    private void OnAddressVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (AddressBox.IsVisible)
            Dispatcher.BeginInvoke(() =>
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
            });
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm?.SelectedTab is { IsEditingAddress: true } tab) tab.CancelAddressCommand.Execute(null);
    }

    // ----- Tab strip: drag to reorder, middle-click to close -----

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(TabStrip);
        _dragTab = (e.OriginalSource as DependencyObject) is { } d ? (FindAncestor<ListBoxItem>(d)?.DataContext as TabViewModel) : null;
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed || Vm is null) return;
        var pos = e.GetPosition(TabStrip);
        if (Math.Abs(pos.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        var over = TabStrip.InputHitTest(new Point(pos.X, TabStrip.ActualHeight / 2)) as DependencyObject;
        if (over is not null && FindAncestor<ListBoxItem>(over)?.DataContext is TabViewModel target && target != _dragTab)
        {
            Vm.MoveTab(_dragTab, Vm.Tabs.IndexOf(target));
            Vm.SelectedTab = _dragTab;
        }
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e) => _dragTab = null;

    private void OnTabMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || e.OriginalSource is not DependencyObject d) return;
        if (FindAncestor<ListBoxItem>(d)?.DataContext is TabViewModel tab) Vm?.CloseTabCommand.Execute(tab);
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    public static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            if (FindDescendant(child, match) is { } found) return found;
        }

        return null;
    }
}
