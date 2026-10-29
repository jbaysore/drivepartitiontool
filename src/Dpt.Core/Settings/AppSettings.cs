namespace Dpt.Core.Settings;

public enum StartView
{
    Home,
    Folder,
    RestoreTabs,
}

public enum FolderViewMode
{
    Details,
    Thumbnails,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// All user-configurable behavior. Defaults here are the app's defaults. Serialized as JSON in the user database;
/// unknown or missing properties fall back to these defaults so new settings never break old databases.
/// </summary>
public sealed class AppSettings
{
    // ---- General ----
    [Setting("General", "Start view", "What the app shows when it opens.", Order = 0)]
    public StartView StartView { get; set; } = StartView.Home;

    [Setting("General", "Start folder", "Folder used when the start view is \"Folder\". Leave empty for your user folder.", Order = 1)]
    public string StartFolder { get; set; } = "";

    [Setting("General", "Theme", "Light, dark, or follow Windows.", Order = 2)]
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    [Setting("General", "Keep running in the system tray", "Lets automatic rules keep working while the window is closed.", Order = 3)]
    public bool KeepRunningInTray { get; set; } = true;

    [Setting("General", "Start at sign-in", "Starts the app (in the tray) when you sign in to Windows.", Order = 4)]
    public bool StartAtSignIn { get; set; } = false;

    // ---- Browsing ----
    [Setting("Browsing", "Show navigation pane", "Shows the left pane with pages, pinned locations and drives.", Order = 0)]
    public bool ShowNavigationPane { get; set; } = true;

    [Setting("Browsing", "Default folder view", "Detailed list, or thumbnails for pictures and videos.", Order = 1)]
    public FolderViewMode DefaultFolderView { get; set; } = FolderViewMode.Details;

    [Setting("Browsing", "Show hidden items", "Shows files and folders marked hidden.", Order = 2)]
    public bool ShowHiddenItems { get; set; } = false;

    [Setting("Browsing", "Show protected system files", "Shows files Windows marks as protected system files.", Order = 3, Advanced = true)]
    public bool ShowSystemItems { get; set; } = false;

    [Setting("Browsing", "Show file extensions", "Shows endings such as .pdf or .jpg in names.", Order = 4)]
    public bool ShowExtensions { get; set; } = true;

    [Setting("Browsing", "Sort folders first", "Lists folders before files.", Order = 5)]
    public bool FoldersFirst { get; set; } = true;

    [Setting("Browsing", "Open folders in a new tab with middle-click", "Middle-click or Ctrl+click opens a folder in a new tab.", Order = 6)]
    public bool MiddleClickOpensTab { get; set; } = true;

    [Setting("Browsing", "Show folder sizes", "Shows the total size of everything inside each folder.", Order = 7)]
    public bool ShowFolderSizes { get; set; } = true;

    [Setting("Browsing", "Visible columns", "Comma-separated columns for folder lists: Name, Modified, Type, Size, Categories.", Order = 8)]
    public string FolderColumns { get; set; } = "Name,Modified,Type,Size";

    // ---- Home ----
    [Setting("Home", "Frequently visited count", "How many frequently visited locations Home shows.", Order = 0)]
    public int FrequentCount { get; set; } = 6;

    [Setting("Home", "Show drive space on Home", "Shows a compact drive-space summary on Home.", Order = 1)]
    public bool HomeShowDrives { get; set; } = true;

    [Setting("Home", "Show largest items on Home", "Shows a short list of the largest items on Home.", Order = 2)]
    public bool HomeShowLargest { get; set; } = true;

    // ---- Search ----
    [Setting("Search", "Result columns", "Comma-separated: Name, Location, Drive, Type, Size, Modified, Created.", Order = 0)]
    public string SearchColumns { get; set; } = "Name,Location,Drive,Type,Size,Modified";

    [Setting("Search", "Results per page", "How many results load at a time.", Order = 1)]
    public int SearchPageSize { get; set; } = 200;

    [Setting("Search", "Folder search box searches", "true = this folder and its subfolders; false = all selected drives.", Order = 2)]
    public bool FolderSearchIsLocal { get; set; } = true;

    [Setting("Search", "Include hidden items in search", "Includes hidden files and folders in search results.", Order = 3)]
    public bool SearchIncludeHidden { get; set; } = false;

    [Setting("Search", "Build the catalog inside the app", "When the background search service isn't installed, the app uses its saved catalog while it's open. Whole drives are not rescanned on startup.", Order = 5)]
    public bool UseLocalCatalog { get; set; } = true;

    [Setting("Search", "Show search previews", "Shows a preview pane beside search results (Alt+P toggles it).", Order = 6)]
    public bool ShowSearchPreview { get; set; } = false;

    [Setting("Search", "Type groups", "Extension groups used by type: filters. Format: group=ext,ext;group=ext", Order = 4, Advanced = true)]
    public string TypeGroups { get; set; } = Search.TypeGroups.DefaultDefinition;

    // ---- File operations ----
    [Setting("File operations", "Confirm before recycling", "Asks before sending items to the Recycle Bin.", Order = 0)]
    public bool ConfirmRecycle { get; set; } = false;

    [Setting("File operations", "Warn before recycling large items", "Warns up front when items are bigger than their drive's Recycle Bin can hold.", Order = 1)]
    public bool PrecheckRecycleBin { get; set; } = true;

    [Setting("File operations", "Undo history length", "How many past operations can be undone.", Order = 2)]
    public int UndoDepth { get; set; } = 50;

    // ---- Rules ----
    [Setting("Rules", "Pause automatic rules", "Stops all automatic rules until turned back on.", Order = 0)]
    public bool PauseAutomaticRules { get; set; } = false;

    // ---- Advanced / disk tools ----
    [Setting("Disk tools", "Allow changes to system partitions", "Unlocks partitions Windows needs to start (EFI, recovery, Windows). Extra confirmation is still required.", Order = 0, Advanced = true)]
    public bool AllowSystemPartitions { get; set; } = false;

    [Setting("Disk tools", "Extend last partition after clone", "When the destination drive is larger, grow the last partition to use the extra space.", Order = 1)]
    public bool CloneExtendLastPartition { get; set; } = true;

    [Setting("Integration", "Open folders with DrivePartitionTool", "Makes Windows open folders here instead of File Explorer. Can be turned off at any time.", Order = 0, Advanced = true)]
    public bool DefaultFolderHandler { get; set; } = false;

    // ---- Not shown on the Settings page (state) ----
    public List<string> LastTabs { get; set; } = [];

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
