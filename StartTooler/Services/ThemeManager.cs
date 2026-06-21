using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StartTooler.Services;

public class ThemeManager
{
    private static ResourceDictionary? _overrideDict;

    public static bool IsRedNightVision { get; private set; }

    public static void Initialize()
    {
        IsRedNightVision = false;
    }

    public static void SetTheme(bool redNightVision)
    {
        var app = Application.Current;
        if (app == null) return;

        var mergedDictionaries = app.Resources.MergedDictionaries;

        if (_overrideDict != null)
        {
            mergedDictionaries.Remove(_overrideDict);
            _overrideDict = null;
        }

        if (redNightVision)
        {
            _overrideDict = new ResourceDictionary();
            _overrideDict.Add("Bg.Outer", Avalonia.Media.Color.Parse("#000000"));
            _overrideDict.Add("Bg.Surface", Avalonia.Media.Color.Parse("#0A0000"));
            _overrideDict.Add("Bg.SurfaceElevated", Avalonia.Media.Color.Parse("#140000"));
            _overrideDict.Add("Bg.Divider", Avalonia.Media.Color.Parse("#2A0000"));
            _overrideDict.Add("Bg.Hover", Avalonia.Media.Color.Parse("#1F0000"));
            _overrideDict.Add("Text.Primary", Avalonia.Media.Color.Parse("#FF6B6B"));
            _overrideDict.Add("Text.Secondary", Avalonia.Media.Color.Parse("#B53030"));
            _overrideDict.Add("Text.Tertiary", Avalonia.Media.Color.Parse("#802020"));
            _overrideDict.Add("Text.Disabled", Avalonia.Media.Color.Parse("#4D0000"));
            _overrideDict.Add("Accent.Stellar", Avalonia.Media.Color.Parse("#FF3030"));
            _overrideDict.Add("Accent.Nebula", Avalonia.Media.Color.Parse("#FF6060"));
            _overrideDict.Add("Accent.Aurora", Avalonia.Media.Color.Parse("#FF8080"));
            _overrideDict.Add("State.Success", Avalonia.Media.Color.Parse("#FF8080"));
            _overrideDict.Add("State.Warning", Avalonia.Media.Color.Parse("#FFAA80"));
            _overrideDict.Add("State.Danger", Avalonia.Media.Color.Parse("#FF3030"));

            var headerBrush = new LinearGradientBrush();
            headerBrush.StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative);
            headerBrush.EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative);
            headerBrush.GradientStops.Add(new GradientStop(Avalonia.Media.Color.Parse("#000000"), 0));
            headerBrush.GradientStops.Add(new GradientStop(Avalonia.Media.Color.Parse("#0A0000"), 1));
            _overrideDict.Add("Gradient.Header", headerBrush);

            var buttonBrush = new LinearGradientBrush();
            buttonBrush.StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative);
            buttonBrush.EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative);
            buttonBrush.GradientStops.Add(new GradientStop(Avalonia.Media.Color.Parse("#CC3030"), 0));
            buttonBrush.GradientStops.Add(new GradientStop(Avalonia.Media.Color.Parse("#FF6060"), 1));
            _overrideDict.Add("Gradient.PrimaryButton", buttonBrush);

            mergedDictionaries.Add(_overrideDict);
        }

        IsRedNightVision = redNightVision;
    }
}
