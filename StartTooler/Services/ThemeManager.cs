using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StartTooler.Services;

public static class ThemeManager
{
    private static readonly Uri LightThemeUri = new("avares://StartTooler/Themes/Light.axaml");
    private static readonly Uri DarkThemeUri = new("avares://StartTooler/Themes/Dark.axaml");

    private static ResourceDictionary? _lightTheme;
    private static ResourceDictionary? _darkTheme;
    private static ResourceDictionary? _current;

    public static ThemeMode CurrentMode { get; private set; } = ThemeMode.Light;

    public static void Initialize()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        _lightTheme ??= (ResourceDictionary)AvaloniaXamlLoader.Load(LightThemeUri);
        _darkTheme ??= (ResourceDictionary)AvaloniaXamlLoader.Load(DarkThemeUri);

        if (_current == null)
        {
            _current = _lightTheme;
            if (_current != null)
            {
                app.Resources.MergedDictionaries.Add(_current);
            }
            CurrentMode = ThemeMode.Light;
        }
    }

    public static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        Initialize();

        var target = mode == ThemeMode.Dark ? _darkTheme : _lightTheme;
        if (target == null)
        {
            return;
        }

        if (ReferenceEquals(_current, target))
        {
            CurrentMode = mode;
            return;
        }

        if (_current != null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
        }

        app.Resources.MergedDictionaries.Add(target);
        _current = target;
        CurrentMode = mode;
    }

}

public enum ThemeMode
{
    Light,
    Dark
}
