namespace SteamStat.Core.Insights;

/// <summary>回坑时间点附近发生过的事。Strength 决定谁排在前面，也是这条解释的把握。</summary>
public sealed record ComebackCause(string Kind, double Strength, int? DaysBefore = null, Bilingual? Name = null);

public sealed record Comeback(int AppId, string Name, long Start, string Day, int GapDays, long SecondsAfter, IReadOnlyList<ComebackCause> Causes)
{
    public ComebackCause? MainCause => Causes.Count > 0 ? Causes[0] : null;
}

/// <summary>
/// 因果叠加：把版本更新、DLC、好友、节假日、特卖叠到回坑的时间点上。
/// 这些都只是相关，文案也只说「此前发生了什么」，不下因果结论。
/// </summary>
public static class ComebackAnalysis
{
    public const int UpdateWindowDays = 14;
    public const int DlcWindowDays = 30;
    public const int FriendWindowDays = 7;
    /// <summary>回坑前这么多小时的游戏不算回坑：至少要玩过点什么，才谈得上回来。</summary>
    public const int MinPriorSeconds = 3600;

    public static IReadOnlyList<Comeback> Find(InsightArchive archive, string fromDay, string toDay)
    {
        var result = new List<Comeback>();
        foreach (var group in archive.Sessions.GroupBy(session => session.AppId))
        {
            var sessions = group.OrderBy(session => session.Start).ToList();
            long priorSeconds = 0;
            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                if (index > 0
                    && (session.Tags & SessionTags.Comeback) != 0
                    && priorSeconds >= MinPriorSeconds
                    && string.CompareOrdinal(session.Day, fromDay) >= 0
                    && string.CompareOrdinal(session.Day, toDay) <= 0)
                {
                    var gapDays = (int)((session.Start - sessions[index - 1].End) / 86400);
                    var after = sessions.Skip(index).TakeWhile(item => string.CompareOrdinal(item.Day, toDay) <= 0).Sum(item => item.Seconds);
                    result.Add(new Comeback(session.AppId, session.Name, session.Start, session.Day, gapDays, after, CausesOf(archive, session)));
                }
                priorSeconds += session.Seconds;
            }
        }
        return result.OrderByDescending(item => item.GapDays).ToList();
    }

    private static IReadOnlyList<ComebackCause> CausesOf(InsightArchive archive, InsightSession session)
    {
        var causes = new List<ComebackCause>();
        var start = session.Start;

        var update = archive.Builds
            .Where(build => build.AppId == session.AppId && !build.IsBaseline)
            .Select(build => build.ObservedAt)
            .Concat(archive.SchemaChanges.Where(change => change.AppId == session.AppId).Select(change => change.DetectedAt))
            .Where(time => time <= start && time >= start - UpdateWindowDays * 86400L)
            .DefaultIfEmpty(0)
            .Max();
        if (update > 0)
        {
            var days = (int)((start - update) / 86400);
            causes.Add(new ComebackCause("update", InsightMath.Clamp(0.9 - days * 0.015, 0.6, 0.9), days));
        }

        var dlcIds = archive.DlcOf(session.AppId);
        if (dlcIds.Count > 0)
        {
            var bought = archive.Acquisitions
                .Where(item => dlcIds.Contains(item.AppId) && item.AcquiredAt <= start && item.AcquiredAt >= start - DlcWindowDays * 86400L)
                .Select(item => item.AcquiredAt)
                .DefaultIfEmpty(0)
                .Max();
            if (bought > 0)
            {
                var days = (int)((start - bought) / 86400);
                causes.Add(new ComebackCause("dlc", InsightMath.Clamp(0.85 - days * 0.01, 0.6, 0.85), days));
            }
        }

        var friend = archive.Friends.PlaysOf(session.AppId)
            .Where(play => play.Start <= start && play.End >= start - FriendWindowDays * 86400L)
            .MaxBy(play => play.End);
        if (friend != null)
        {
            var days = (int)(Math.Max(0, start - friend.End) / 86400);
            causes.Add(new ComebackCause("friend", 0.75, days, new Bilingual(Say.Friend(friend.SteamId, friend.Name), Say.Friend(friend.SteamId, friend.Name))));
        }

        var day = archive.Options.LogicalDate(start);
        var holiday = SteamCalendar.HolidayOn(day, archive.Options.Language);
        if (holiday != null) causes.Add(new ComebackCause("holiday", 0.62, null, holiday.Name));
        var sale = SteamCalendar.SaleOn(day);
        if (sale != null) causes.Add(new ComebackCause("sale", 0.6, null, sale.Name));

        return causes.OrderByDescending(cause => cause.Strength).ToList();
    }
}
