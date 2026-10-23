using Dpt.Core.Navigation;
using Dpt.Core.Settings;
using Dpt.Data;
using Dpt.Shell;
using Dpt.Shell.Operations;

namespace Dpt.App.Services;

/// <summary>Composition root: the app's long-lived services. Created once at startup.</summary>
public sealed class AppServices
{
    private static AppServices? _current;

    private AppServices()
    {
        Database = AppDatabase.OpenDefault();
        SettingsStore = new SettingsStore(Database);
        Settings = SettingsStore.Load();
        Locations = new LocationsRepository(Database);
        Journal = new JournalRepository(Database);
        Rules = new RulesRepository(Database);
        Categories = new CategoryService(new CategoriesRepository(Database), Journal);
        Engine = new FileOperationEngine();
        Activity = new ActivityHub();
        FolderSizes = new FolderSizeService();
        Drives = new DriveService();
        Theme = new ThemeService();
        Addresses = new AddressResolver(
            text => Environment.ExpandEnvironmentVariables(text) is var e && !e.Contains('%') ? e : null,
            KnownFolders.ResolveShellName);
        FileOps = new FileOperationsService(this);
        Catalog = new CatalogService(this);
        FolderSizes.Catalog = Catalog;
        RuleRunner = new RuleService(this);
        Layouts = new LayoutService(this);
    }

    public static AppServices Current => _current ?? throw new InvalidOperationException("Services not initialized.");

    public static AppServices Initialize() => _current ??= new AppServices();

    public AppDatabase Database { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public LocationsRepository Locations { get; }
    public JournalRepository Journal { get; }
    public RulesRepository Rules { get; }
    public CategoryService Categories { get; }
    public RuleService RuleRunner { get; }
    public LayoutService Layouts { get; }
    public FileOperationEngine Engine { get; }
    public FileOperationsService FileOps { get; }
    public CatalogService Catalog { get; }
    public ActivityHub Activity { get; }
    public FolderSizeService FolderSizes { get; }
    public DriveService Drives { get; }
    public ThemeService Theme { get; }
    public AddressResolver Addresses { get; }

    /// <summary>Main window handle, used as the owner for shell dialogs.</summary>
    public IntPtr OwnerWindow { get; set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        SettingsStore.Save(settings);
        SettingsChanged?.Invoke(this, settings);
    }
}
