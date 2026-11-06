namespace SteamStat.Core.Insights;

/// <summary>生涯时间线上的一个高光时刻。</summary>
public sealed record CareerHighlight(string Kind, long Time, string Day, int? AppId, Bilingual Text, double? Value = null);

/// <summary>从档案里提取高光，排成时间线。只用记录到的数据，安装之前的历史不在其中。</summary>
public static class CareerAnalyzer
{
    private const double BigGameHours = 100;
    private const double MinimumFriendNightHours = 1;
    private static readonly int[] TotalMilestones = [100, 500, 1000, 2000, 5000, 10000];

    public static IReadOnlyList<CareerHighlight> Build(InsightArchive archive)
    {
        var sessions = archive.Sessions;
        if (sessions.Count == 0) return [];
        var list = new List<CareerHighlight>();

        var first = sessions[0];
        list.Add(new CareerHighlight("firstSession", first.Start, first.Day, first.AppId,
            Say.Fill(new Bilingual("档案里的第一局：《{game}》", "First session on record: {game}"), ("game", Say.Game(first.AppId, first.Name)))));

        AddCompletion(archive, list);
        AddLongestSessions(sessions, list);
        AddRarest(archive, list);
        AddFriendNight(archive, list);
        AddBigGames(sessions, list);
        AddTotals(sessions, list);

        return list.OrderBy(item => item.Time).ThenBy(item => item.Kind, StringComparer.Ordinal).ToList();
    }

    private static void AddCompletion(InsightArchive archive, List<CareerHighlight> list)
    {
        var lastUnlock = archive.Unlocks.GroupBy(unlock => unlock.AppId).ToDictionary(group => group.Key, group => group.MaxBy(unlock => unlock.Time)!);
        var completed = archive.Progress
            .Where(progress => progress.Total > 0 && progress.Unlocked >= progress.Total && lastUnlock.ContainsKey(progress.AppId))
            .Select(progress => (progress.AppId, progress.Name, Time: lastUnlock[progress.AppId].Time))
            .Concat(archive.Statuses
                .Where(status => status is { Status: "completed", CompletedAt: > 0 })
                .Select(status => (status.AppId, Name: archive.Sessions.FirstOrDefault(session => session.AppId == status.AppId)?.Name ?? $"App {status.AppId}", Time: status.CompletedAt!.Value)))
            .OrderBy(item => item.Time)
            .FirstOrDefault();
        if (completed.Name == null) return;
        list.Add(new CareerHighlight("firstCompletion", completed.Time, archive.Options.DayOf(completed.Time), completed.AppId,
            Say.Fill(new Bilingual("第一次通关：《{game}》", "First game finished: {game}"), ("game", Say.Game(completed.AppId, completed.Name)))));
    }

    /// <summary>全部时间的最长一局，加上每年的最长一局，后者让时间线不至于只有开头。</summary>
    private static void AddLongestSessions(IReadOnlyList<InsightSession> sessions, List<CareerHighlight> list)
    {
        var overall = sessions.MaxBy(session => session.Seconds)!;
        list.Add(new CareerHighlight("longestSession", overall.Start, overall.Day, overall.AppId,
            Say.Fill(new Bilingual("生涯最长一局：《{game}》{hours}", "Longest session ever: {hours} of {game}"), ("game", Say.Game(overall.AppId, overall.Name)), ("hours", Say.Seconds(overall.Seconds))),
            InsightMath.Hours(overall.Seconds)));
        foreach (var year in sessions.GroupBy(session => session.Day[..4]))
        {
            var best = year.MaxBy(session => session.Seconds)!;
            if (best.Id == overall.Id) continue;
            list.Add(new CareerHighlight("yearLongest", best.Start, best.Day, best.AppId,
                Say.Fill(new Bilingual("{year} 年最长一局：《{game}》{hours}", "Longest session of {year}: {hours} of {game}"), ("year", year.Key), ("game", Say.Game(best.AppId, best.Name)), ("hours", Say.Seconds(best.Seconds))),
                InsightMath.Hours(best.Seconds)));
        }
    }

    private static void AddRarest(InsightArchive archive, List<CareerHighlight> list)
    {
        var rarest = archive.Unlocks.Where(unlock => unlock.GlobalPercent is > 0).MinBy(unlock => unlock.GlobalPercent);
        if (rarest == null) return;
        var name = archive.Progress.FirstOrDefault(progress => progress.AppId == rarest.AppId)?.Name ?? $"App {rarest.AppId}";
        list.Add(new CareerHighlight("rarestAchievement", rarest.Time, rarest.Day, rarest.AppId,
            Say.Fill(new Bilingual("最稀有的成就：《{game}》的「{achievement}」，全球 {percent}% 的玩家拿到", "Rarest achievement: {achievement} in {game}, held by {percent}% of players"),
                ("game", Say.Game(rarest.AppId, name)), ("achievement", rarest.Name), ("percent", Say.Number(rarest.GlobalPercent!.Value))),
            rarest.GlobalPercent));
    }

    /// <summary>和好友最久的一夜：同一逻辑日里和同一位好友在同一款游戏里重叠最久。</summary>
    private static void AddFriendNight(InsightArchive archive, List<CareerHighlight> list)
    {
        var sessionsByApp = archive.Sessions.Where(session => (session.Tags & SessionTags.Coop) != 0).ToLookup(session => session.AppId);
        var nights = new Dictionary<(string Friend, int AppId, string Day), (long Seconds, long Start, string Name, string GameName)>();
        foreach (var play in archive.Friends.Plays)
        {
            foreach (var session in sessionsByApp[play.AppId])
            {
                var overlap = Math.Min(play.End, session.End) - Math.Max(play.Start, session.Start);
                if (overlap <= 0) continue;
                var key = (play.SteamId, play.AppId, session.Day);
                var value = nights.GetValueOrDefault(key);
                nights[key] = (value.Seconds + overlap, value.Start == 0 ? session.Start : Math.Min(value.Start, session.Start), play.Name, session.Name);
            }
        }
        if (nights.Count == 0) return;
        var best = nights.MaxBy(item => item.Value.Seconds);
        if (InsightMath.Hours(best.Value.Seconds) < MinimumFriendNightHours) return;
        list.Add(new CareerHighlight("friendNight", best.Value.Start, best.Key.Day, best.Key.AppId,
            Say.Fill(new Bilingual("和 {friend} 最久的一夜：《{game}》{hours}", "Longest night with {friend}: {hours} of {game}"),
                ("friend", Say.Friend(best.Key.Friend, best.Value.Name)), ("game", Say.Game(best.Key.AppId, best.Value.GameName)), ("hours", Say.Seconds(best.Value.Seconds))),
            InsightMath.Hours(best.Value.Seconds)));
    }

    /// <summary>第一款玩到一百小时的游戏。</summary>
    private static void AddBigGames(IReadOnlyList<InsightSession> sessions, List<CareerHighlight> list)
    {
        var totals = new Dictionary<int, long>();
        foreach (var session in sessions)
        {
            totals[session.AppId] = totals.GetValueOrDefault(session.AppId) + session.Seconds;
            if (InsightMath.Hours(totals[session.AppId]) < BigGameHours) continue;
            list.Add(new CareerHighlight("firstHundred", session.End, session.Day, session.AppId,
                Say.Fill(new Bilingual("第一款玩到 100 小时的游戏：《{game}》", "First game to reach 100 hours: {game}"), ("game", Say.Game(session.AppId, session.Name)))));
            return;
        }
    }

    private static void AddTotals(IReadOnlyList<InsightSession> sessions, List<CareerHighlight> list)
    {
        long total = 0;
        var next = 0;
        foreach (var session in sessions)
        {
            total += session.Seconds;
            while (next < TotalMilestones.Length && InsightMath.Hours(total) >= TotalMilestones[next])
            {
                list.Add(new CareerHighlight("totalMilestone", session.End, session.Day, null,
                    Say.Fill(new Bilingual("累计时长达到 {hours} 小时", "Total playtime reached {hours} hours"), ("hours", TotalMilestones[next])),
                    TotalMilestones[next]));
                next++;
            }
        }
    }
}
