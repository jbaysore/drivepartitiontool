using System.Windows;
using Dpt.Core.Settings;
using Microsoft.Win32;

namespace Dpt.App.Services;

/// <summary>Switches between the light and dark palettes (and the matching Fluent control theme).</summary>
public sealed class ThemeService
{
    private ResourceDictionary? _palette;
    private ThemePreference _preference = ThemePreference.System;

    public bool IsDark { get; private set; }

    public void Initialize(ThemePreference preference)
    {
        _preference = preference;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _preference == ThemePreference.System)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(_preference));
        };
        Apply(preference);
    }

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        IsDark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDarkTheme(),
        };

        var app = Application.Current;
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(IsDark ? "Dark" : "Light")}.xaml") };
        if (_palette is not null) app.Resources.MergedDictionaries.Remove(_palette);
        app.Resources.MergedDictionaries.Insert(0, palette);
        _palette = palette;
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
