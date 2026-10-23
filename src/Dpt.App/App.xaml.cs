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
    /// <summary>Extension point for later milestones: runs after services exist, before the window opens.</summary>
    public static event Action<AppServices>? Starting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled", ev.ExceptionObject.ToString() ?? "");
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("UnobservedTask", ev.Exception);
            ev.SetObserved();
        };

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
        SettingsView.ExtraContent = () => new CatalogPanel { DataContext = new CatalogPanelViewModel() };
        Starting?.Invoke(services);
        Log.Info("App", "Started");

        var start = CommandLine.StartLocations(e.Args);
        var main = new MainViewModel(start);
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Current.Catalog.Dispose(); }
        catch (InvalidOperationException) { }
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Dispatcher", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to:\n{Log.CurrentFile}",
            "DrivePartitionTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
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
