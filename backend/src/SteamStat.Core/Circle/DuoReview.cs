namespace SteamStat.Core.Circle;

public sealed record DuoGame(int AppId, string Name, int Mine, int Theirs);

public sealed record DuoTogetherGame(int AppId, long Seconds, long ConfirmedSeconds);

public sealed record DuoReviewReport(
    int Year,
    /// <summary>对方的卡不是这一年的：数字照样比，界面要说明。</summary>
    int TheirYear,
    long TheirGeneratedAt,
    int MyMinutes,
    int TheirMinutes,
    IReadOnlyList<int> MyMonths,
    IReadOnlyList<int> TheirMonths,
    /// <summary>两个人这一年都玩过的游戏，按两人里较少的那个排序。</summary>
    IReadOnlyList<DuoGame> Shared,
    IReadOnlyList<DuoGame> OnlyMine,
    IReadOnlyList<DuoGame> OnlyTheirs,
    double? Schedule,
    double? Taste,
    /// <summary>两个人都常玩的时段（我的本地时间，星期从周一开始 × 24 + 小时）。</summary>
    int? BestCell,
    int? MyPeakCell,
    int? TheirPeakCell,
    /// <summary>两个人都玩得多的月份（1..12）。</summary>
    int? BusiestMonth,
    long TogetherSeconds,
    long ConfirmedSeconds,
    long? FirstTogether,
    IReadOnlyList<DuoTogetherGame> TogetherGames,
    int MyAchievements,
    int TheirAchievements,
    string? MyPersona,
    string? TheirPersona,
    /// <summary>人格四个字母里相同的位置。</summary>
    IReadOnlyList<int> SameAxes,
    long? Since,
    IReadOnlyList<int> MyHeatmap,
    /// <summary>对方的作息挪到了我的时区。</summary>
    IReadOnlyList<int> TheirHeatmap);

/// <summary>
/// 双人回顾：我的卡（当场生成）和对方的卡合成一份年度回顾。一起玩的时间只来自我的档案：对方的卡里没有和谁玩。
/// </summary>
public static class DuoReview
{
    public static DuoReviewReport Build(CardPayload mine, CardPayload theirs, IReadOnlyList<TogetherSegment> together, long? since)
    {
        var my = mine.Games.Where(game => game.YearMinutes > 0).ToDictionary(game => game.AppId);
        var their = theirs.Games.Where(game => game.YearMinutes > 0).ToDictionary(game => game.AppId);
        var shared = my.Keys.Intersect(their.Keys)
            .Select(appId => new DuoGame(appId, my[appId].Name, my[appId].YearMinutes, their[appId].YearMinutes))
            .OrderByDescending(game => Math.Min(game.Mine, game.Theirs))
            .ToList();
        var onlyMine = my.Values.Where(game => !their.ContainsKey(game.AppId))
            .OrderByDescending(game => game.YearMinutes).Take(5)
            .Select(game => new DuoGame(game.AppId, game.Name, game.YearMinutes, 0)).ToList();
        var onlyTheirs = their.Values.Where(game => !my.ContainsKey(game.AppId))
            .OrderByDescending(game => game.YearMinutes).Take(5)
            .Select(game => new DuoGame(game.AppId, game.Name, 0, game.YearMinutes)).ToList();

        // 对方的作息是对方的本地时间，挪到我的时区再比
        var mySchedule = mine.Heatmap.Select(value => (double)value).ToArray();
        var shift = (int)Math.Round((mine.UtcOffsetMinutes - theirs.UtcOffsetMinutes) / 60.0);
        var theirSchedule = WeekHours.Shift(theirs.Heatmap.Select(value => (double)value).ToArray(), shift);
        var schedule = WeekHours.Cosine(mySchedule, theirSchedule);
        int? best = null, myPeak = null, theirPeak = null;
        if (schedule != null)
        {
            best = Enumerable.Range(0, WeekHours.Cells).MaxBy(index => Math.Min(mySchedule[index], theirSchedule[index]));
            if (Math.Min(mySchedule[best.Value], theirSchedule[best.Value]) <= 0) best = null;
            myPeak = Enumerable.Range(0, WeekHours.Cells).MaxBy(index => mySchedule[index]);
            theirPeak = Enumerable.Range(0, WeekHours.Cells).MaxBy(index => theirSchedule[index]);
        }

        double? taste = my.Count >= 3 && their.Count >= 3
            ? Compatibility.WeightedJaccard(my.ToDictionary(pair => pair.Key, pair => pair.Value.YearMinutes), their.ToDictionary(pair => pair.Key, pair => pair.Value.YearMinutes))
            : null;

        int? busiest = null;
        if (mine.Months.Count == 12 && theirs.Months.Count == 12)
        {
            var month = Enumerable.Range(0, 12).MaxBy(index => Math.Min(mine.Months[index], theirs.Months[index]));
            if (Math.Min(mine.Months[month], theirs.Months[month]) > 0) busiest = month + 1;
        }

        var sameAxes = new List<int>();
        if (mine.Persona?.Code is { Length: > 0 } a && theirs.Persona?.Code is { Length: > 0 } b)
            for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
                if (a[index] == b[index]) sameAxes.Add(index);

        return new DuoReviewReport(
            mine.Year,
            theirs.Year,
            theirs.GeneratedAt,
            mine.Totals.YearMinutes,
            theirs.Totals.YearMinutes,
            mine.Months,
            theirs.Months,
            shared,
            onlyMine,
            onlyTheirs,
            schedule is { } s ? Math.Round(s, 3) : null,
            taste is { } t ? Math.Round(t, 3) : null,
            best,
            myPeak,
            theirPeak,
            busiest,
            together.Sum(segment => segment.Seconds),
            together.Sum(segment => segment.ConfirmedSeconds),
            together.Count == 0 ? null : together.Min(segment => segment.Start),
            together.GroupBy(segment => segment.AppId)
                .Select(group => new DuoTogetherGame(group.Key, group.Sum(segment => segment.Seconds), group.Sum(segment => segment.ConfirmedSeconds)))
                .OrderByDescending(game => game.Seconds).Take(5).ToList(),
            mine.Totals.YearAchievements,
            theirs.Totals.YearAchievements,
            mine.Persona?.Code,
            theirs.Persona?.Code,
            sameAxes,
            since,
            mine.Heatmap,
            theirSchedule.Select(value => (int)value).ToList());
    }
}
