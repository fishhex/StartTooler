using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using StartTooler.Models;

namespace StartTooler.Converters;

public static class RefreshButtonConverters
{
    public static readonly IMultiValueConverter Label = new RefreshLabelConverter();

    private class RefreshLabelConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var hasProject = values.Count > 0 && values[0] is bool b && b;
            var state = values.Count > 1 && values[1] is RefreshState refreshState
                ? refreshState
                : RefreshState.Idle;

            if (!hasProject)
            {
                return "请先选择项目";
            }

            return state == RefreshState.Scanning ? "停止" : "刷新";
        }

        public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

    }
}
