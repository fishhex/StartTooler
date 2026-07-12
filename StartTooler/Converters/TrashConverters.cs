using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StartTooler.Converters;

/// <summary>
/// unix ms → "2024-07-09 删除"。null / 0 / 负数 → 空串。
/// 垃圾筒卡片第二行日期标签用（spec doc/0.11/spec/04-trash-improve.md §3.1）。
/// </summary>
public class UnixMsToDateStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long ms && ms > 0)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            return $"{dt:yyyy-MM-dd} 删除";
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 字节数 → "12.3 MB"。B 整数显示，KB/MB/GB 一位小数。
/// 垃圾筒卡片第二行大小标签用（spec §3.1）。
/// </summary>
public class BytesToHumanReadableConverter : IValueConverter
{
    private static readonly string[] s_sizes = { "B", "KB", "MB", "GB" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < s_sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return order == 0 ? $"{len:N0} {s_sizes[order]}" : $"{len:N1} {s_sizes[order]}";
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
