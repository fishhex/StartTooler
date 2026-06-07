using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace StartTooler.Services;

public static class ThemeManager
{
    public static ThemeMode CurrentMode { get; private set; } = ThemeMode.Light;

    public static void Initialize()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        // 默认使用 Light 主题
        app.RequestedThemeVariant = ThemeVariant.Light;
        CurrentMode = ThemeMode.Light;
    }

    public static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var themeVariant = mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.RequestedThemeVariant = themeVariant;
        CurrentMode = mode;
    }
}

public enum ThemeMode
{
    Light,
    Dark
}
