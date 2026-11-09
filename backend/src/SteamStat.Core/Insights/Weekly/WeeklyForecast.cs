namespace SteamStat.Core.Insights;

/// <summary>
/// 下周预报：用最近八周同一星期几的情况估计下周。这是频率，不是模型，
/// 所以观测不足四周时干脆不发预报。
/// </summary>
internal static class WeeklyForecastBuilder
{
    private const int Weeks = 8;
    private const int MinimumWeeks = 4;
    private const long MinimumDaySeconds = 15 * 60;

    private static readonly string[] Parts = ["night", "morning", "afternoon", "evening"];

    internal static WeeklyForecast? Build(InsightArchive archive, InsightPeriod period)
    {
        var to = InsightDays.Parse(period.ToDay);
        var from = to.AddDays(-(Weeks * 7 - 1));
        var sessions = archive.SessionsIn(InsightDays.Format(from), period.ToDay).ToList();
        var weeksWithData = sessions.Select(session => (InsightDays.Parse(session.Day).DayNumber - from.DayNumber) / 7).Distinct().Count();
        if (weeksWithData < MinimumWeeks) return null;

        var zone = archive.Options.TimeZone;
        var perDay = sessions.GroupBy(session => session.Day).ToDictionary(group => group.Key, group => group.Sum(item => item.Seconds));
        var days = new List<WeeklyForecastDay>();
        for (var offset = 1; offset <= 7; offset++)
        {
            var date = to.AddDays(offset);
            var history = Enumerable.Range(1, Weeks).Select(week => date.AddDays(-7 * week)).Where(day => day >= from).ToList();
            var played = history.Count(day => perDay.GetValueOrDefault(InsightDays.Format(day)) >= MinimumDaySeconds);
            var probability = history.Count == 0 ? 0 : (double)played / history.Count;
            var expected = history.Count == 0 ? 0 : history.Sum(day => InsightMath.Hours(perDay.GetValueOrDefault(InsightDays.Format(day)))) / history.Count;
            var weekday = date.DayOfWeek;
            var part = BusiestPart(sessions.Where(session => InsightDays.Parse(session.Day).DayOfWeek == weekday), zone);
            var sky = probability >= 0.75 ? "clear" : probability >= 0.5 ? "cloudy" : probability >= 0.25 ? "showers" : "calm";
            days.Add(new WeeklyForecastDay(
                InsightDays.Format(date),
                sky,
                Math.Round(probability, 2),
                part,
                Math.Round(expected, 1),
                WeeklyCopy.Skies[sky]));
        }

        var best = days.OrderByDescending(day => day.Probability).ThenByDescending(day => day.ExpectedHours).First();
        var bestDay = Say.Weekday(InsightDays.Parse(best.Day).DayOfWeek);
        var summary = Say.Fill(WeeklyCopy.ForecastSummary, ("day", bestDay), ("part", WeeklyCopy.Parts[best.Part]), ("probability", Say.Percent(best.Probability)));
        return new WeeklyForecast(summary, days, FriendNote(archive, from, to), weeksWithData);
    }

    private static string BusiestPart(IEnumerable<InsightSession> sessions, TimeZoneInfo zone)
    {
        var totals = new long[Parts.Length];
        foreach (var session in sessions)
            for (var index = 0; index < Parts.Length; index++)
                totals[index] += SessionTagger.WindowSeconds(session.Start, session.End, index * 6, index * 6 + 6, zone);
        var max = Array.IndexOf(totals, totals.Max());
        return totals[max] == 0 ? "evening" : Parts[max];
    }

    /// <summary>
    /// 哪位好友最可能上线。只看手动追踪的好友（只有他们有在线记录），
    /// 也只说一句话，不展示别人的作息明细。
    /// </summary>
    private static Bilingual? FriendNote(InsightArchive archive, DateOnly from, DateOnly to)
    {
        var options = archive.Options;
        var slots = new Dictionary<(string SteamId, DayOfWeek Day, int Part), HashSet<int>>();
        var names = new Dictionary<string, string>();
        foreach (var online in archive.Friends.Online)
        {
            var day = options.LogicalDate(online.Start);
            if (day < from || day > to) continue;
            names[online.SteamId] = online.Name;
            var local = options.Local(online.Start);
            var key = (online.SteamId, day.DayOfWeek, local.Hour / 6);
            if (!slots.TryGetValue(key, out var weeks)) slots[key] = weeks = [];
            weeks.Add((day.DayNumber - from.DayNumber) / 7);
        }
        if (slots.Count == 0) return null;
        var best = slots.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key.SteamId, StringComparer.Ordinal).First();
        if (best.Value.Count < MinimumWeeks) return null;
        return Say.Fill(
            WeeklyCopy.ForecastFriend,
            ("friend", Say.Friend(best.Key.SteamId, names[best.Key.SteamId])),
            ("day", Say.Weekday(best.Key.Day)),
            ("part", WeeklyCopy.Parts[Parts[best.Key.Part]]),
            ("share", Say.Percent((double)best.Value.Count / Weeks)));
    }
}
