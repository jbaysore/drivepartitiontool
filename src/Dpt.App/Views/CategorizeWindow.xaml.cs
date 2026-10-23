using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.App.Services;
using Dpt.Data;

namespace Dpt.App.Views;

public sealed partial class CategoryCheck : ObservableObject
{
    public required string Name { get; init; }
    public required string Color { get; init; }
    public bool Mixed { get; init; }
    public bool? Initial { get; init; }

    [ObservableProperty] private bool? _checked;
}

/// <summary>Assign/remove categories for the selected items (tri-state when only some items have a category).</summary>
public partial class CategorizeWindow : Window
{
    private readonly IReadOnlyList<string> _paths;
    private readonly ObservableCollection<CategoryCheck> _items = [];

    public CategorizeWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        _paths = paths;
        Heading.Text = paths.Count == 1 ? $"Categories for \"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : $"Categories for {paths.Count} items";
        var cats = AppServices.Current.Categories;
        var perItem = paths.Select(p => cats.CategoriesOf(p).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var c in cats.Repository.GetCategories())
        {
            var count = perItem.Count(s => s.Contains(c.Name));
            bool? state = count == 0 ? false : count == paths.Count ? true : null;
            _items.Add(new CategoryCheck { Name = c.Name, Color = c.Color, Mixed = state is null, Initial = state, Checked = state });
        }

        List.ItemsSource = _items;
        Loaded += (_, _) => NewName.Focus();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = NewName.Text.Trim();
        if (name.Length == 0) return;
        if (_items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            existing.Checked = true;
        }
        else
        {
            var c = AppServices.Current.Categories.Repository.GetOrCreate(name);
            _items.Add(new CategoryCheck { Name = c.Name, Color = c.Color, Initial = false, Checked = true });
        }

        NewName.Clear();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var svc = AppServices.Current.Categories;
        var add = _items.Where(i => i.Checked == true && i.Initial != true).Select(i => i.Name).ToList();
        if (add.Count > 0) svc.Tag(_paths, add, title: _paths.Count == 1 ? $"Categorize {Path.GetFileName(_paths[0])}" : $"Categorize {_paths.Count} items");
        foreach (var i in _items.Where(i => i.Checked == false && i.Initial != false)) svc.Untag(_paths, i.Name);
        svc.InvalidateCache();
        DialogResult = true;
    }

    public static bool Show(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        return new CategorizeWindow(paths) { Owner = Application.Current.MainWindow }.ShowDialog() == true;
    }
}
