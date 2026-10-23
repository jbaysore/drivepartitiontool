using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Dpt.Core.Navigation;
using Dpt.Core.Settings;

namespace Dpt.App.ViewModels;

public enum SettingEditor
{
    Toggle,
    Choice,
    Text,
    Number,
}

/// <summary>One generated row on the Settings page, bound to an <see cref="AppSettings"/> property.</summary>
public sealed partial class SettingItemViewModel : ObservableObject
{
    private readonly PropertyInfo _property;
    private readonly Action<SettingItemViewModel> _changed;
    private bool _loading;

    public SettingItemViewModel(PropertyInfo property, SettingAttribute meta, AppSettings settings, Action<SettingItemViewModel> changed)
    {
        _property = property;
        _changed = changed;
        Label = meta.Label;
        Description = meta.Description;
        Advanced = meta.Advanced;
        Key = property.Name;
        var t = property.PropertyType;
        Editor = t == typeof(bool) ? SettingEditor.Toggle : t.IsEnum ? SettingEditor.Choice : t == typeof(int) ? SettingEditor.Number : SettingEditor.Text;
        Choices = t.IsEnum ? Enum.GetNames(t).Select(Humanize).ToList() : [];
        Load(settings);
    }

    public string Key { get; }
    public string Label { get; }
    public string Description { get; }
    public bool Advanced { get; }
    public SettingEditor Editor { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool IsToggle => Editor == SettingEditor.Toggle;
    public bool IsChoice => Editor == SettingEditor.Choice;
    public bool IsText => Editor is SettingEditor.Text or SettingEditor.Number;

    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private int _choiceIndex;
    [ObservableProperty] private string? _error;

    public void Load(AppSettings settings)
    {
        _loading = true;
        var v = _property.GetValue(settings);
        switch (Editor)
        {
            case SettingEditor.Toggle: BoolValue = (bool)v!; break;
            case SettingEditor.Choice: ChoiceIndex = Array.IndexOf(Enum.GetValues(_property.PropertyType), v); break;
            default: TextValue = Convert.ToString(v, CultureInfo.CurrentCulture) ?? ""; break;
        }

        _loading = false;
    }

    public bool TryApply(AppSettings settings)
    {
        switch (Editor)
        {
            case SettingEditor.Toggle:
                _property.SetValue(settings, BoolValue);
                break;
            case SettingEditor.Choice:
                _property.SetValue(settings, Enum.GetValues(_property.PropertyType).GetValue(Math.Max(0, ChoiceIndex)));
                break;
            case SettingEditor.Number:
                if (!int.TryParse(TextValue, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n < 0)
                {
                    Error = "Enter a whole number.";
                    return false;
                }

                _property.SetValue(settings, n);
                break;
            default:
                _property.SetValue(settings, TextValue);
                break;
        }

        Error = null;
        return true;
    }

    partial void OnBoolValueChanged(bool value) => Notify();
    partial void OnChoiceIndexChanged(int value) => Notify();
    partial void OnTextValueChanged(string value) => Notify();

    private void Notify()
    {
        if (!_loading) _changed(this);
    }

    private static string Humanize(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}

public sealed record SettingSection(string Name, IReadOnlyList<SettingItemViewModel> Items);

/// <summary>Settings page generated from <see cref="SettingAttribute"/>s. Changes save immediately.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    public SettingsPageViewModel(TabViewModel tab) : base(tab, Location.Page(LocationKind.Settings))
    {
        var settings = Services.Settings.Clone();
        settings.DefaultFolderHandler = Services.FolderAssociation.IsEnabled;
        var items = typeof(AppSettings).GetProperties()
            .Select(p => (Property: p, Meta: p.GetCustomAttribute<SettingAttribute>()))
            .Where(x => x.Meta is not null)
            .Select(x => (x.Meta!, Item: new SettingItemViewModel(x.Property, x.Meta!, settings, OnItemChanged)))
            .ToList();
        foreach (var group in items.GroupBy(i => i.Item1.Section))
            Sections.Add(new SettingSection(group.Key, group.OrderBy(i => i.Item1.Advanced).ThenBy(i => i.Item1.Order).Select(i => i.Item).ToList()));
        Services.SettingsChanged += OnSettingsChanged;
    }

    public override string IconKey => "Icon.Settings";

    public ObservableCollection<SettingSection> Sections { get; } = [];

    public Func<string?>? ExtraInfo { get; set; }

    public string DataFolder => Data.AppDatabase.DefaultDataDirectory;
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        foreach (var item in Sections.SelectMany(s => s.Items)) { item.Load(settings); item.Error = null; }
    }
    public override void Dispose() { Services.SettingsChanged -= OnSettingsChanged; base.Dispose(); }

    private void OnItemChanged(SettingItemViewModel item)
    {
        var copy = Services.Settings.Clone();
        if (!item.TryApply(copy)) return;
        string? integrationNote = null;
        if (item.Key == nameof(AppSettings.DefaultFolderHandler))
        {
            try { integrationNote = Services.FolderAssociation.SetEnabled(copy.DefaultFolderHandler); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException)
            {
                var actual = Services.Settings.Clone(); actual.DefaultFolderHandler = Services.FolderAssociation.IsEnabled;
                item.Load(actual); item.Error = "Folder integration was not changed completely: " + ex.Message;
                return;
            }
        }
        Services.SaveSettings(copy);
        if (item.Key == nameof(AppSettings.Theme)) Services.Theme.Apply(copy.Theme);
        // Only touch the real sign-in entry when the owner flips this switch.
        if (item.Key == nameof(AppSettings.StartAtSignIn)) SignInStartup.Apply(copy.StartAtSignIn);
        if (item.Key == nameof(AppSettings.PauseAutomaticRules)) Services.RuleRunner.RefreshWatchers();
        StatusText = integrationNote ?? "Saved";
    }
}
