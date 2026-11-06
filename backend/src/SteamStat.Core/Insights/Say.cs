using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Insights;

/// <summary>
/// 写文案的小工具。游戏和好友写成占位符而不是纯文本：周刊冻结后，隐藏游戏和主播模式还要在显示时生效，
/// 所以冻结的是「这里是哪款游戏」，渲染层和合订本再决定怎么显示。
/// </summary>
public static partial class Say
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>[[g:570|Dota 2]]</summary>
    public static string Game(int appId, string name) => $"[[g:{appId}|{Clean(name)}]]";

    /// <summary>[[f:7656...|昵称]]</summary>
    public static string Friend(string steamId, string name) => $"[[f:{steamId}|{Clean(name)}]]";

    private static string Clean(string value) => value.Replace('|', '/').Replace('[', '(').Replace(']', ')').Trim();

    /// <summary>把占位符换成文字。隐藏游戏和主播模式都在这一步生效。</summary>
    public static string Resolve(string text, Func<int, string, string> game, Func<string, string, string> friend)
        => Token().Replace(text, match => match.Groups[1].Value == "g"
            ? game(int.Parse(match.Groups[2].Value, Invariant), match.Groups[3].Value)
            : friend(match.Groups[2].Value, match.Groups[3].Value));

    [GeneratedRegex(@"\[\[(g|f):([0-9]+)\|([^\]]*)\]\]")]
    private static partial Regex Token();

    public static Bilingual Hours(double hours)
    {
        if (hours < 1)
        {
            var minutes = Math.Max(1, (int)Math.Round(hours * 60));
            return new Bilingual($"{minutes} 分钟", minutes == 1 ? "1 minute" : $"{minutes} minutes");
        }
        var text = hours >= 100
            ? Math.Round(hours).ToString("0", Invariant)
            : Math.Round(hours, 1).ToString("0.#", Invariant);
        return new Bilingual($"{text} 小时", text == "1" ? "1 hour" : $"{text} hours");
    }

    public static Bilingual Seconds(long seconds) => Hours(seconds / 3600.0);

    public static string Percent(double share) => Math.Round(share * 100).ToString("0", Invariant) + "%";

    public static string Number(double value) => Math.Round(value, 1).ToString("0.#", Invariant);

    public static string Whole(double value) => Math.Round(value).ToString("0", Invariant);

    public static string Clock(double hour)
    {
        var minutes = (int)Math.Round(((hour % 24) + 24) % 24 * 60) % 1440;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    public static Bilingual Date(string day)
    {
        var date = InsightDays.Parse(day);
        return new Bilingual($"{date.Month} 月 {date.Day} 日", date.ToString("MMM d", Invariant));
    }

    public static Bilingual Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => new Bilingual("周一", "Monday"),
        DayOfWeek.Tuesday => new Bilingual("周二", "Tuesday"),
        DayOfWeek.Wednesday => new Bilingual("周三", "Wednesday"),
        DayOfWeek.Thursday => new Bilingual("周四", "Thursday"),
        DayOfWeek.Friday => new Bilingual("周五", "Friday"),
        DayOfWeek.Saturday => new Bilingual("周六", "Saturday"),
        _ => new Bilingual("周日", "Sunday")
    };

    /// <summary>把 {key} 换成对应语言的值。值可以是字符串（两种语言相同）或 <see cref="Bilingual"/>。</summary>
    public static Bilingual Fill(Bilingual template, params (string Key, object Value)[] values)
    {
        var zh = new StringBuilder(template.Zh);
        var en = new StringBuilder(template.En);
        foreach (var (key, value) in values)
        {
            var (zhValue, enValue) = value is Bilingual text ? (text.Zh, text.En) : (Convert.ToString(value, Invariant) ?? "", Convert.ToString(value, Invariant) ?? "");
            zh.Replace($"{{{key}}}", zhValue);
            en.Replace($"{{{key}}}", enValue);
        }
        return new Bilingual(zh.ToString(), en.ToString());
    }

    public static Bilingual Join(string separatorZh, string separatorEn, IEnumerable<Bilingual> parts)
    {
        var list = parts.ToList();
        return new Bilingual(string.Join(separatorZh, list.Select(part => part.Zh)), string.Join(separatorEn, list.Select(part => part.En)));
    }
}
