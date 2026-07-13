using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StartTooler.Data;
using StartTooler.Models;

namespace StartTooler.Converters;

// === v0.11: 同步徽章合并（spec doc/0.11/spec/05-ui-interaction-review.md §5.1）
//
// 原 6 个独立 Border（3 持久态 + 3 瞬时态）合并为 1 个 Border + 4 个 Converter：
//   - SyncStatusToSingleBadgeIcon    → IBrush (Geometry 字符串)
//   - SyncStatusToSingleBadgeColor   → IBrush (颜色 key → SolidColorBrush)
//   - SyncStatusToSingleBadgeVis     → bool
//   - SyncStatusToSingleBadgeTooltip → string
//
// 优先级（spec §5.1）：
//   1. UploadStatus = Uploading   → Accent.Stellar  / "上传中..."
//   2. UploadStatus = Failed      → State.Danger     / "上传失败"
//   3. UploadStatus = Paused      → Text.Disabled    / "已暂停"
//   4. SyncStatus   = UploadedAndLocal            → State.Success   / "已同步"
//   5. SyncStatus   = UploadedButMissingLocal     → State.Warning   / "本地已修改"
//   6. SyncStatus   = NotUploaded                 → 隐藏
//
// Avalonia IValueConverter 单值单出 —— 4 个 Converter 共享 GetBadgeInfo(MediaFile) 静态方法。

internal static class SyncBadgeInfo
{
    public static (string IconKey, string ColorKey, string Tooltip, bool Visible) Get(MediaFile mf)
    {
        // 1. 瞬时态优先
        if (mf.UploadStatus == UploadStatus.Uploading)
            return ("Icon.Cloud", "Accent.Stellar", "上传中...", true);
        if (mf.UploadStatus == UploadStatus.Failed)
            return ("Icon.Cloud", "State.Danger", "上传失败", true);
        if (mf.UploadStatus == UploadStatus.Paused)
            return ("Icon.Cloud", "Text.Disabled", "已暂停", true);

        // 2. 持久态
        return mf.SyncStatusValue switch
        {
            SyncStatus.UploadedAndLocal => ("Icon.Cloud", "State.Success", "已同步", true),
            SyncStatus.UploadedButMissingLocal => ("Icon.Cloud", "State.Warning", "本地已修改", true),
            _ => ("", "", "", false),
        };
    }
}

/// <summary>
/// MediaFile → bool（是否显示同步徽章）。用法：IsVisible="{Binding ., Converter={StaticResource SyncBadgeToVis}}"
/// </summary>
public class SyncStatusToSingleBadgeVis : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MediaFile mf && SyncBadgeInfo.Get(mf).Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// MediaFile → IBrush（背景色 key 字符串；Avalonia XAML 端用 DynamicResource 解析为实际 brush）。
/// 实际做法：直接返回 SolidColorBrush 对应的 Color key，省去一层 XAML 端绑定。
/// </summary>
public class SyncStatusToSingleBadgeColor : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile mf) return Avalonia.Media.Brushes.Transparent;
        var info = SyncBadgeInfo.Get(mf);
        if (!info.Visible) return Avalonia.Media.Brushes.Transparent;

        // ColorKey → 实际颜色
        return info.ColorKey switch
        {
            "Accent.Stellar" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4FC3F7")),
            "State.Success"  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4DD0E1")),
            "State.Warning"  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFA726")),
            "State.Danger"   => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF5252")),
            "Text.Disabled"  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4A5273")),
            _ => Avalonia.Media.Brushes.Transparent,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// MediaFile → IGeometry（PathIcon 用）。用法：Data="{Binding ., Converter={StaticResource SyncBadgeToIcon}}"
/// 注：PathIcon.Data 是 StreamGeometry 类型，需要从 Application.Current.Resources 取。
/// 实际做法：把 key 解析为 StreamGeometry；Avalonia ResourceResolver 路径走 IResourceNode。
/// </summary>
public class SyncStatusToSingleBadgeIcon : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile mf) return null;
        var info = SyncBadgeInfo.Get(mf);
        if (!info.Visible) return null;

        // Application.Current?.Resources[key] 取 StreamGeometry
        // Avalonia 11 IResourceNode.TryGetResource(object? key, ThemeVariant? theme, out object? value)
        if (Avalonia.Application.Current?.Resources.TryGetResource(info.IconKey, null, out var res) == true)
            return res;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// MediaFile → string（tooltip 文本）。用法：ToolTip.Tip="{Binding ., Converter={StaticResource SyncBadgeToTooltip}}"
/// </summary>
public class SyncStatusToSingleBadgeTooltip : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile mf) return "";
        return SyncBadgeInfo.Get(mf).Tooltip;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
