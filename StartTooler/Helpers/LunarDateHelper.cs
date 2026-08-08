using System;
using System.Globalization;

namespace StartTooler.Helpers;

/// <summary>
/// v0.12: 农历与周次辅助类，用于日记页头部展示。
/// </summary>
public static class LunarDateHelper
{
    private static readonly ChineseLunisolarCalendar s_lunarCalendar = new();

    private static readonly string[] s_lunarMonths =
    {
        "正", "二", "三", "四", "五", "六",
        "七", "八", "九", "十", "冬", "腊"
    };

    private static readonly string[] s_lunarDays =
    {
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    private static readonly string[] s_weekdays =
    {
        "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"
    };

    /// <summary>
    /// 获取农历日期文本，例如 "农历六月初十"。
    /// </summary>
    public static string GetLunarDateText(DateTime date)
    {
        var lunarYear = s_lunarCalendar.GetYear(date);
        var lunarMonth = s_lunarCalendar.GetMonth(date);
        var lunarDay = s_lunarCalendar.GetDayOfMonth(date);
        var isLeap = s_lunarCalendar.IsLeapMonth(lunarYear, lunarMonth);

        var leapText = isLeap ? "闰" : string.Empty;
        var monthText = s_lunarMonths[lunarMonth - 1];
        var dayText = s_lunarDays[lunarDay - 1];

        return $"农历{leapText}{monthText}月{dayText}";
    }

    /// <summary>
    /// 获取星期文本，例如 "星期五"。
    /// </summary>
    public static string GetWeekdayText(DateTime date)
    {
        return s_weekdays[(int)date.DayOfWeek];
    }

    /// <summary>
    /// 获取月份第几周标签，例如 "7月第4周"。
    /// 以周一作为一周起始。
    /// </summary>
    public static string GetMonthWeekLabel(DateTime date)
    {
        var firstDayOfMonth = new DateTime(date.Year, date.Month, 1);
        var firstDayOfWeek = (int)firstDayOfMonth.DayOfWeek; // 0=Sunday
        var offset = firstDayOfWeek == 0 ? 6 : firstDayOfWeek - 1; // 转换为周一=0
        var weekOfMonth = (date.Day + offset - 1) / 7 + 1;
        return $"{date.Month}月第{weekOfMonth}周";
    }
}
