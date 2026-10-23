using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Infrastructure;
using Dpt.Core.Formatting;
using Dpt.Shell;
using Dpt.Shell.FileSystem;

namespace Dpt.App.ViewModels;

/// <summary>A row in a folder list or search results.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private string? _typeName;
    private SortKey? _nameSortKey;
    public SortKey NameSortKey => _nameSortKey ??= CultureInfo.CurrentCulture.CompareInfo.GetSortKey(Name, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public FileItemViewModel(FileEntry entry, bool showExtension = true)
    {
        Entry = entry;
        ShowExtension = showExtension;
        _size = entry.IsFolder ? null : entry.Size;
    }

    public FileEntry Entry { get; private set; }
    public bool ShowExtension { get; }

    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public string DisplayName => ShowExtension || IsFolder ? Entry.Name : System.IO.Path.GetFileNameWithoutExtension(Entry.Name);
    public DateTime Modified => Entry.Modified;
    public DateTime Created => Entry.Created;
    public string ModifiedText => Entry.Modified.ToString("g", CultureInfo.CurrentCulture);
    public string CreatedText => Entry.Created.ToString("g", CultureInfo.CurrentCulture);
    public string Location => System.IO.Path.GetDirectoryName(Entry.Path) ?? "";
    public string Drive => Dpt.Core.Navigation.PathUtil.DriveOf(Entry.Path);
    public bool IsHidden => Entry.IsHidden;
    public double RowOpacity => IsHidden ? 0.6 : 1.0;
    public string AttributesText =>
        $"{(Entry.Attributes.HasFlag(FileAttributes.ReadOnly) ? "R" : "")}{(Entry.IsHidden ? "H" : "")}{(Entry.IsSystem ? "S" : "")}{(Entry.Attributes.HasFlag(FileAttributes.Archive) ? "A" : "")}";

    public string TypeName => _typeName ??= ShellInfo.TypeName(Entry.Path, Entry.IsFolder);

    /// <summary>File size, or the folder's total size including everything nested once known.</summary>
    [ObservableProperty] private long? _size;

    [ObservableProperty] private bool _sizePending;

    public string SizeText => Size is { } s ? SizeFormatter.Format(s) : SizePending ? "…" : IsFolder ? "" : "";

    partial void OnSizeChanged(long? value) => OnPropertyChanged(nameof(SizeText));
    partial void OnSizePendingChanged(bool value) => OnPropertyChanged(nameof(SizeText));

    private string? _categoriesText;

    /// <summary>Categories on this item, e.g. "Photos, Taxes" (looked up lazily for the optional Categories column).</summary>
    public string CategoriesText => _categoriesText ??= string.Join(", ", Services.AppServices.Current.Categories.CategoriesOf(Entry.Path));

    public void RefreshCategories()
    {
        _categoriesText = null;
        OnPropertyChanged(nameof(CategoriesText));
    }

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _isCut;

    public ImageSource? Icon
    {
        get
        {
            if (_icon is not null || _iconRequested) return _icon;
            _iconRequested = true;
            var path = Entry.Path;
            var isFolder = Entry.IsFolder;
            StaWorker.Shared.Post(() =>
            {
                var img = ShellInfo.SmallIcon(path, isFolder);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _icon = img;
                    OnPropertyChanged(nameof(Icon));
                });
            });
            return null;
        }
    }

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null || _thumbnailRequested) return _thumbnail;
            _thumbnailRequested = true;
            var path = Entry.Path;
            StaWorker.Shared.Post(() =>
            {
                var img = ShellInfo.Thumbnail(path, 160);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _thumbnail = img;
                    OnPropertyChanged(nameof(Thumbnail));
                });
            });
            return null;
        }
    }

    /// <summary>Updates metadata after a change on disk without losing selection or cached images.</summary>
    public void Update(FileEntry entry)
    {
        var nameChanged = !string.Equals(entry.Name, Entry.Name, StringComparison.Ordinal);
        Entry = entry;
        if (!entry.IsFolder) Size = entry.Size;
        if (nameChanged)
        {
            _typeName = null;
            _nameSortKey = null;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(TypeName));
        }

        OnPropertyChanged(nameof(Modified));
        OnPropertyChanged(nameof(ModifiedText));
    }

    /// <summary>Culture-aware natural name order: file2 before file10. Precomputed keys avoid repeated native collation work.</summary>
    public static int CompareNames(string a, string b) => CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
}
