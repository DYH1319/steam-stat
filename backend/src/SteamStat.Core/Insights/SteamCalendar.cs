using System.Globalization;

namespace SteamStat.Core.Insights;

/// <summary>一段日历事件。名称两种语言都有，周刊要同时写两份。</summary>
public sealed record CalendarWindow(string Id, Bilingual Name, DateOnly From, DateOnly To)
{
    public bool Contains(DateOnly day) => day >= From && day <= To;
}

/// <summary>
/// 节假日和 Steam 大促的日历，用来回答「为什么回坑」。这些日期不是实测：
/// 大促每年略有浮动，这里按近几年的规律取宽一点的窗口，所以只算弱证据，和版本更新不同。
/// </summary>
public static class SteamCalendar
{
    private static readonly ChineseLunisolarCalendar Lunar = new();

    public static IReadOnlyList<CalendarWindow> Holidays(int year, string language)
    {
        var list = new List<CalendarWindow>();
        if (language == "zh")
        {
            list.Add(Window("newYear", "元旦", "the New Year holiday", year, 1, 1, 3));
            var spring = SpringFestival(year);
            if (spring != null) list.Add(new CalendarWindow("springFestival", new Bilingual("春节", "the Spring Festival"), spring.Value.AddDays(-1), spring.Value.AddDays(6)));
            list.Add(Window("labour", "五一假期", "the May Day holiday", year, 5, 1, 5));
            list.Add(Window("national", "国庆假期", "the National Day holiday", year, 10, 1, 7));
        }
        else
        {
            list.Add(Window("newYear", "新年", "New Year", year, 1, 1, 2));
            list.Add(Window("christmas", "圣诞假期", "the Christmas holidays", year, 12, 23, 31));
        }
        return list;
    }

    public static IReadOnlyList<CalendarWindow> SteamSales(int year) =>
    [
        Window("springSale", "Steam 春季特卖", "the Steam Spring Sale", year, 3, 11, 20),
        Window("summerSale", "Steam 夏季特卖", "the Steam Summer Sale", year, 6, 24, 7, 10),
        // 2025 年起秋促挪到了十月前后，之前在感恩节
        year >= 2025
            ? Window("autumnSale", "Steam 秋季特卖", "the Steam Autumn Sale", year, 9, 28, 10, 8)
            : Window("autumnSale", "Steam 秋季特卖", "the Steam Autumn Sale", year, 11, 20, 12, 2),
        Window("winterSale", "Steam 冬季特卖", "the Steam Winter Sale", year, 12, 18, 12, 31)
    ];

    public static CalendarWindow? HolidayOn(DateOnly day, string language)
        => Holidays(day.Year, language).FirstOrDefault(window => window.Contains(day));

    public static CalendarWindow? SaleOn(DateOnly day)
        => SteamSales(day.Year).FirstOrDefault(window => window.Contains(day))
           // 冬促跨年
           ?? (day is { Month: 1, Day: <= 5 } ? new CalendarWindow("winterSale", new Bilingual("Steam 冬季特卖", "the Steam Winter Sale"), new DateOnly(day.Year, 1, 1), new DateOnly(day.Year, 1, 5)) : null);

    /// <summary>农历正月初一。超出日历范围时返回 null。</summary>
    public static DateOnly? SpringFestival(int year)
    {
        try
        {
            return DateOnly.FromDateTime(Lunar.ToDateTime(year, 1, 1, 0, 0, 0, 0));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static CalendarWindow Window(string id, string zh, string en, int year, int month, int fromDay, int toDay)
        => new(id, new Bilingual(zh, en), new DateOnly(year, month, fromDay), new DateOnly(year, month, toDay));

    private static CalendarWindow Window(string id, string zh, string en, int year, int fromMonth, int fromDay, int toMonth, int toDay)
        => new(id, new Bilingual(zh, en), new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));
}
