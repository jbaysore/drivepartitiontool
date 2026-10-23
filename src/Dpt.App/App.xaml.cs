using System.Windows;
using System.Windows.Threading;
using Dpt.App.Diagnostics;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.App.Views;
using Dpt.Core.Navigation;

namespace Dpt.App;

public partial class App : Application
{
    public static bool IsFailing { get; private set; }
    private bool _unattendedCrashTest;
    /// <summary>Extension point for later milestones: runs after services exist, before the window opens.</summary>
    public static event Action<AppServices>? Starting;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            var error = ev.ExceptionObject as Exception ?? new Exception(ev.ExceptionObject.ToString());
            Log.Error("Unhandled", error); Log.WriteCrashReport(error, "Background process failure");
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("UnobservedTask", ev.Exception);
            ev.SetObserved();
        };

        try
        {
            var data = Environment.GetEnvironmentVariable("DPT_DATA_DIR");
            _unattendedCrashTest = e.Args.Contains("--diagnostics-test-crash") && data is not null && Path.GetFullPath(data).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            base.OnStartup(e); StartApplication(e);
        }
        catch (Exception ex) { Fail(ex, "Startup"); }
    }

    private void StartApplication(StartupEventArgs e)
    {

        ServiceInstaller.RegisterCommands();
        if (CommandLine.TryRunCommand(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        var services = AppServices.Initialize();
        services.Theme.Initialize(services.Settings.Theme);
        services.FileOps.Dialogs = new WpfDialogService();
        services.Drives.Refresh();
        Dpt.Storage.StorageInventory.RecycleBinLimit = Dpt.Shell.Operations.RecycleBin.MaxCapacityBytes;
        _ = services.Catalog.InitializeAsync();
        FolderPageViewModel.BulkRename = BulkRenameWindow.RunAsync;
        FolderPageViewModel.Categorize = CategorizeWindow.Show;
        RulesPageViewModel.EditRule = RuleEditorWindow.EditAsync;
        DiskToolsPageViewModel.ShowAction = PartitionActionWindow.ShowAsync;
        DiskToolsPageViewModel.ShowClone = CloneWindow.ShowAsync;
        DiskToolsPageViewModel.ShowWindowsCopy = WindowsCopyWindow.ShowAsync;
        DiskToolsPageViewModel.ShowBootHealth = BootHealthWindow.ShowAsync;
        RulesPageViewModel.PreviewRule = RulePreviewWindow.ShowAsync;
        SearchPageViewModel.CategoryResolver = services.Categories.ResolveForSearchAsync;
        services.RuleRunner.Start();
        SettingsView.ExtraContent = () =>
        {
            var panels = new System.Windows.Controls.StackPanel();
            panels.Children.Add(new CatalogPanel { DataContext = new CatalogPanelViewModel(), Margin = new Thickness(0, 0, 0, 12) });
            panels.Children.Add(new IntegrationPanel());
            return panels;
        };
        Starting?.Invoke(services);
        Log.Info("App", "Started");

        var start = CommandLine.StartLocations(e.Args);
        var main = new MainViewModel(start);
        services.RuleRunner.Notified += (_, message) => Dispatcher.BeginInvoke(() => main.ShowToast(message));
        var window = new MainWindow { DataContext = main, StartInTray = e.Args.Contains("--tray") };
        MainWindow = window;
        window.Show();
        if (_unattendedCrashTest)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => throw new InvalidOperationException("Intentional diagnostics test exception.")));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { if (MainWindow?.DataContext is IDisposable viewModel) viewModel.Dispose(); }
        catch (Exception ex) { Log.Error("Close views", ex); }
        AppServices.Shutdown();
        Log.Info("App", $"Exited ({e.ApplicationExitCode})");
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Fail(e.Exception, "UI dispatcher");
    }

    private void Fail(Exception exception, string context)
    {
        if (IsFailing) { Environment.Exit(-2); return; }
        IsFailing = true;
        Log.Error(context, exception);
        var report = Log.WriteCrashReport(exception, context);
        if (!_unattendedCrashTest)
            MessageBox.Show($"DrivePartitionTool must close because of an unexpected error. Any unfinished operations need to be reviewed before retrying.\n\n{exception.Message}\n\n" +
                (report is null ? "The error report could not be saved." : "Error report: " + report), "DrivePartitionTool", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }
}

/// <summary>Command-line handling: folder paths open as tabs; maintenance switches run and exit.</summary>
public static class CommandLine
{
    /// <summary>Maintenance commands (service install etc.) registered by later milestones. Return exit code.</summary>
    public static Dictionary<string, Func<string[], int>> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryRunCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !Commands.TryGetValue(args[0], out var command)) return false;
        exitCode = command(args);
        return true;
    }

    public static IReadOnlyList<Location> StartLocations(string[] args)
    {
        var list = new List<Location>();
        for (var k = 0; k < args.Length; k++)
        {
            var a = args[k];
            if (a == "--open" && k + 1 < args.Length)
            {
                if (Enum.TryParse<LocationKind>(args[++k], ignoreCase: true, out var kind))
                    list.Add(kind == LocationKind.Home ? Location.Home : Location.Page(kind));
                continue;
            }

            if (a == "--search" && k + 1 < args.Length)
            {
                list.Add(Location.Search(args[++k], null));
                continue;
            }

            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            var p = a.Trim('"');
            if (Directory.Exists(p)) list.Add(Location.Folder(Path.GetFullPath(p)));
            else if (File.Exists(p) && Path.GetDirectoryName(Path.GetFullPath(p)) is { } parent) list.Add(Location.Folder(parent));
        }

        return list;
    }
}

/// <summary>"Start at sign-in": a per-user Run entry that starts the app in the tray.</summary>
public static class SignInStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DrivePartitionTool";

    public static void Apply(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --tray");
        else if (!enabled && key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }
}
