using SteamStat.Core.Insights;

namespace SteamStat.Core.Memoir;

public sealed record TimeMachineGame(int AppId, long Seconds, MemoirEvidence Evidence);

public sealed record TimeMachineOfficialMonth(string Month, long? Seconds, IReadOnlyList<int> AppIds);

public sealed record TimeMachineProfile(string? PersonaName, int? Level, MemoirEvidence Evidence, long? ObservedAt);

public sealed record TimeMachineSummary(
    MemoirValue DaySeconds,
    MemoirValue WeekSeconds,
    MemoirValue MonthSeconds,
    MemoirValue TotalSeconds,
    MemoirValue GamesPlayed,
    IReadOnlyList<InsightSession> Sessions,
    IReadOnlyList<MemoirTrace> Traces,
    IReadOnlyList<TimeMachineGame> RecentGames,
    TimeMachineOfficialMonth? OfficialMonth,
    TimeMachineProfile? Profile,
    LifeChapter? Chapter,
    IReadOnlyList<MemoirLifeEvent> LifeEvents);

public sealed record TimeMachineLibraryGame(int AppId, long? AcquiredAt, MemoirEvidence AcquiredEvidence, MemoirValue Playtime, long? FirstPlayed, long? LastPlayed);

public sealed record TimeMachineLibrary(MemoirValue Owned, int Unknown, int Later, IReadOnlyList<TimeMachineLibraryGame> Games);

public sealed record TimeMachineProgress(int AppId, string Name, int Unlocked, int Total, MemoirEvidence Evidence);

public sealed record TimeMachineAchievements(
    MemoirValue Unlocked,
    MemoirValue Completed,
    int Games,
    IReadOnlyList<InsightUnlock> DayUnlocks,
    IReadOnlyList<InsightUnlock> Recent,
    IReadOnlyList<InsightUnlock> Rarest,
    IReadOnlyList<TimeMachineProgress> Progress);

public sealed record TimeMachineFriend(string SteamId, string Name, MemoirEvidence Evidence, long? AddedAt, long? RemovedAt);

public sealed record TimeMachineFriendPlay(string SteamId, string Name, int AppId, long Seconds);

public sealed record TimeMachineFriends(
    MemoirValue Count,
    long? KnownSince,
    IReadOnlyList<TimeMachineFriend> Friends,
    IReadOnlyList<TimeMachineFriendPlay> Activity,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);

public sealed record TimeMachineSnapshot(
    int Day,
    int Today,
    int? FirstDay,
    int? TrackingStartDay,
    TimeMachineSummary Summary,
    TimeMachineLibrary Library,
    TimeMachineAchievements Achievements,
    TimeMachineFriends Friends);

/// <summary>入库记录。PackageId 为 0 时是 Steam Stat 第一次在游戏库里看到它的时间，只是上限。</summary>
public sealed record MemoirAcquisition(string SteamId, int AppId, long PackageId, long AcquiredAt);

/// <summary>
/// 时光机：回到某一天，看那时候的仪表盘、游戏库、成就和好友。
/// 每个数字都带证据等级：能证实的正常显示，推算的带标记，无从知晓的只给范围或灰色占位。
/// </summary>
public static class TimeMachine
{
    private const int RecentDays = 30;
    private const int MaxRecent = 12;

    public static TimeMachineSnapshot At(
        MemoirArchive archive,
        IReadOnlyList<AppPlaytimeTimeline> timelines,
        IReadOnlyList<LifeChapter> chapters,
        IReadOnlyList<InsightFriendPlay> friendPlays,
        int day)
    {
        var options = archive.Options;
        var today = MemoirDays.ToEpoch(options.LogicalDate(archive.Now));
        day = Math.Min(day, today);
        var date = MemoirDays.FromEpoch(day);
        var start = MemoirDays.StartOf(options, date);
        var end = Math.Min(MemoirDays.EndOf(options, date), archive.Now);
        int? first = archive.FirstEvidence() is { } firstTime ? MemoirDays.ToEpoch(options.LogicalDate(firstTime)) : null;
        int? tracking = archive.TrackingStart > 0 ? MemoirDays.ToEpoch(options.LogicalDate(archive.TrackingStart)) : null;
        return new TimeMachineSnapshot(
            day,
            today,
            first,
            tracking,
            Summary(archive, timelines, chapters, date, start, end),
            Library(archive, timelines, end),
            Achievements(archive, start, end),
            Friends(archive, friendPlays, start, end));
    }

    private static TimeMachineSummary Summary(MemoirArchive archive, IReadOnlyList<AppPlaytimeTimeline> timelines, IReadOnlyList<LifeChapter> chapters, DateOnly date, long start, long end)
    {
        var options = archive.Options;
        var day = InsightDays.Format(date);
        var weekStart = MemoirDays.StartOf(options, date.AddDays(-6));
        var monthStart = MemoirDays.StartOf(options, date.AddDays(-(RecentDays - 1)));

        var total = timelines.Aggregate(MemoirValue.Zero(MemoirEvidence.Recorded), (sum, timeline) => sum.Plus(timeline.At(end)));
        var played = timelines.Select(timeline => (timeline.AppId, Value: timeline.At(end), timeline.FirstPlayed)).ToList();
        var known = played.Where(item => item.Value.Evidence != MemoirEvidence.Unknown && item.Value.Value > 0 || item.FirstPlayed is { } first && first <= end).Select(item => item.AppId).ToHashSet();
        var unsure = played.Where(item => item.Value.Evidence == MemoirEvidence.Unknown && !known.Contains(item.AppId)).Select(item => item.AppId).ToHashSet();
        var games = new MemoirValue(known.Count, unsure.Count > 0 ? MemoirEvidence.Inferred : MemoirEvidence.Recorded, unsure.Count > 0 ? known.Count + unsure.Count : null);

        var recent = timelines
            .Select(timeline => (timeline.AppId, Value: timeline.Between(monthStart, end)))
            .Where(item => item.Value.Value > 0)
            .GroupBy(item => item.AppId)
            .Select(group => new TimeMachineGame(group.Key, group.Sum(item => item.Value.Value), group.Select(item => item.Value.Evidence).Aggregate(MemoirEvidences.Weakest)))
            .OrderByDescending(game => game.Seconds)
            .Take(MaxRecent)
            .ToList();
        // 只有痕迹、不知道玩了多久的游戏也列出来，时长记 0、标推算
        var traced = archive.Traces
            .Where(trace => trace.Time >= monthStart && trace.Time < end && trace.Kind != MemoirTraceKind.FirstPlay && recent.All(game => game.AppId != trace.AppId))
            .Select(trace => trace.AppId)
            .Distinct()
            .Take(Math.Max(0, MaxRecent - recent.Count))
            .Select(appId => new TimeMachineGame(appId, 0, MemoirEvidence.Inferred));
        recent.AddRange(traced);

        var sessions = archive.Insight.Sessions.Where(session => session.End > start && session.Start < end).ToList();
        var traces = archive.Traces.Where(trace => trace.Time >= start && trace.Time < end).ToList();

        return new TimeMachineSummary(
            Window(archive, timelines, start, end),
            Window(archive, timelines, weekStart, end),
            Window(archive, timelines, monthStart, end),
            total,
            games,
            sessions,
            traces,
            recent,
            OfficialMonth(archive, date),
            Profile(archive, end),
            LifeChapters.At(chapters, MemoirDays.Month(day)),
            archive.LifeEvents.Where(item => item.Covers(MemoirDays.ToEpoch(date))).ToList());
    }

    /// <summary>
    /// 一段时间玩了多久。完全在开始记录之后时，把会话裁到这段时间里相加（和仪表盘同一口径）；
    /// 碰到开始记录之前时，那一部分用累计时长的差推算。
    /// </summary>
    internal static MemoirValue Window(MemoirArchive archive, IReadOnlyList<AppPlaytimeTimeline> timelines, long from, long to)
    {
        if (to <= from) return MemoirValue.Zero(MemoirEvidence.Recorded);
        var tracking = archive.TrackingStart;
        long recorded = 0;
        var recordedFrom = tracking > 0 ? Math.Max(from, tracking) : to;
        if (recordedFrom < to)
            foreach (var session in archive.Insight.Sessions)
            {
                if (session.Start >= to) break;
                var overlap = Math.Min(session.End, to) - Math.Max(session.Start, recordedFrom);
                if (overlap > 0) recorded += Math.Min(overlap, session.Seconds);
            }
        if (tracking > 0 && from >= tracking) return new MemoirValue(recorded, MemoirEvidence.Recorded);

        // 第一局本身算在记录里，史前到它开始前一秒为止
        var preTo = tracking > 0 ? Math.Min(to, tracking - 1) : to;
        var pre = MemoirValue.Zero(MemoirEvidence.Official);
        var weakest = MemoirEvidence.Official;
        var unknown = 0;
        foreach (var timeline in timelines)
        {
            var value = timeline.Between(from, preTo);
            weakest = MemoirEvidences.Weakest(weakest, value.Evidence);
            if (value.Evidence == MemoirEvidence.Unknown) unknown++;
            if (value.Value > 0 || value.Evidence == MemoirEvidence.Unknown) pre = pre.Plus(value);
        }
        // 史前这一段一秒都没有：碰到了开始记录就以记录为准，否则看各款游戏「是 0」的把握
        if (pre.Value == 0 && unknown == 0)
            return new MemoirValue(recorded, recordedFrom < to ? MemoirEvidence.Recorded : timelines.Count == 0 ? MemoirEvidence.Unknown : weakest);
        return new MemoirValue(recorded, MemoirEvidence.Recorded).Plus(pre);
    }

    private static TimeMachineOfficialMonth? OfficialMonth(MemoirArchive archive, DateOnly date)
    {
        var reviews = archive.YearReviews.Where(review => review.Year == date.Year).ToList();
        if (reviews.Count == 0) return null;
        long? seconds = null;
        var apps = new Dictionary<int, double>();
        foreach (var review in reviews)
        {
            var month = review.Months.FirstOrDefault(item => item.Month == date.Month);
            if (month == null) continue;
            if (month.Seconds is { } value) seconds = (seconds ?? 0) + value;
            foreach (var game in month.Games) apps[game.AppId] = apps.GetValueOrDefault(game.AppId) + (review.SecondsOf(date.Month, game.AppId) ?? (long)((game.Share ?? 0) * 1000));
        }
        if (seconds == null && apps.Count == 0) return null;
        return new TimeMachineOfficialMonth(MemoirDays.Month(date), seconds, apps.OrderByDescending(entry => entry.Value).Select(entry => entry.Key).Take(6).ToList());
    }

    private static TimeMachineProfile? Profile(MemoirArchive archive, long end)
    {
        if (archive.Profile.Count == 0) return null;
        // 多个账号时看记录最多的那个：资料是一个人的，不能把两个账号的名字拼在一起
        var steamId = archive.Insight.Sessions.GroupBy(session => session.SteamId).MaxBy(group => group.Count())?.Key
                      ?? archive.Profile[0].SteamId;
        var changes = archive.Profile.Where(change => change.SteamId == steamId).ToList();
        if (changes.Count == 0) return null;
        var name = changes.LastOrDefault(change => change.Field == "personaName" && change.ObservedAt <= end);
        var level = changes.LastOrDefault(change => change.Field == "level" && change.ObservedAt <= end);
        if (name == null && level == null)
        {
            // 第一次观测之前不知道，只能说「最早记录到的是」
            var earliest = changes.First();
            return new TimeMachineProfile(
                changes.FirstOrDefault(change => change.Field == "personaName")?.Value,
                int.TryParse(changes.FirstOrDefault(change => change.Field == "level")?.Value, out var firstLevel) ? firstLevel : null,
                MemoirEvidence.Unknown,
                earliest.ObservedAt);
        }
        return new TimeMachineProfile(
            name?.Value,
            int.TryParse(level?.Value, out var parsed) ? parsed : null,
            MemoirEvidence.Recorded,
            Math.Max(name?.ObservedAt ?? 0, level?.ObservedAt ?? 0));
    }

    private static TimeMachineLibrary Library(MemoirArchive archive, IReadOnlyList<AppPlaytimeTimeline> timelines, long end)
    {
        var byApp = archive.Acquisitions.Where(item => item.AppId > 0).GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.ToList());
        var timelineByApp = timelines.GroupBy(timeline => timeline.AppId).ToDictionary(group => group.Key, group => group.ToList());
        var lastPlayed = archive.Insight.Sessions.Where(session => session.Start < end)
            .GroupBy(session => session.AppId)
            .ToDictionary(group => group.Key, group => group.Max(session => session.End));
        var traces = archive.Traces.Where(trace => trace.Time < end).GroupBy(trace => trace.AppId).ToDictionary(group => group.Key, group => group.Max(trace => trace.Time));
        var appIds = archive.Insight.Owned.Select(game => game.AppId)
            .Concat(byApp.Keys)
            .Concat(lastPlayed.Keys)
            .ToHashSet();

        var games = new List<TimeMachineLibraryGame>();
        int owned = 0, unknown = 0, later = 0;
        foreach (var appId in appIds)
        {
            var records = byApp.GetValueOrDefault(appId) ?? [];
            var license = records.Where(item => item.PackageId > 0).Select(item => (long?)item.AcquiredAt).Min();
            var seen = records.Where(item => item.PackageId == 0).Select(item => (long?)item.AcquiredAt).Min();
            var playedBefore = lastPlayed.ContainsKey(appId) || traces.ContainsKey(appId);
            long? acquiredAt;
            MemoirEvidence evidence;
            if (license is { } licensed)
            {
                if (licensed >= end)
                {
                    later++;
                    continue;
                }
                acquiredAt = licensed;
                evidence = MemoirEvidence.Official;
            }
            else if (seen is { } observed && observed < end)
            {
                acquiredAt = null;
                evidence = MemoirEvidence.Recorded;
            }
            else if (playedBefore)
            {
                // 那时候已经玩过，至少库里有它（也可能是家庭共享）
                acquiredAt = null;
                evidence = MemoirEvidence.Inferred;
            }
            else
            {
                unknown++;
                acquiredAt = null;
                evidence = MemoirEvidence.Unknown;
            }
            if (evidence != MemoirEvidence.Unknown) owned++;
            var playtime = (timelineByApp.GetValueOrDefault(appId) ?? []).Aggregate(MemoirValue.Zero(MemoirEvidence.Official), (sum, timeline) => sum.Plus(timeline.At(end)));
            long? firstPlayed = (timelineByApp.GetValueOrDefault(appId) ?? []).Select(timeline => timeline.FirstPlayed).Where(time => time is { } value && value < end).Min();
            long? last = lastPlayed.TryGetValue(appId, out var playedAt) ? playedAt : traces.TryGetValue(appId, out var traceAt) ? traceAt : null;
            games.Add(new TimeMachineLibraryGame(appId, acquiredAt, evidence, playtime, firstPlayed, last));
        }
        var ownedValue = new MemoirValue(owned, unknown > 0 ? MemoirEvidence.Inferred : MemoirEvidence.Official, unknown > 0 ? owned + unknown : null);
        return new TimeMachineLibrary(
            ownedValue,
            unknown,
            later,
            games.OrderByDescending(game => game.Playtime.Value).ThenBy(game => game.AppId).ToList());
    }

    private static TimeMachineAchievements Achievements(MemoirArchive archive, long start, long end)
    {
        var unlocks = archive.Insight.Unlocks.Where(unlock => unlock.Time < end).ToList();
        var progress = archive.Insight.Progress.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.MaxBy(item => item.Total)!);
        var changedLater = archive.Insight.SchemaChanges.Where(change => change.DetectedAt >= end).Select(change => change.AppId).ToHashSet();
        var withDlc = archive.Insight.Dlcs.Select(item => item.AppId).ToHashSet();

        var perApp = unlocks.GroupBy(unlock => unlock.AppId)
            .Select(group =>
            {
                var current = progress.GetValueOrDefault(group.Key);
                var count = group.Select(unlock => unlock.InternalName).Distinct().Count();
                var total = current?.Total ?? 0;
                var evidence = changedLater.Contains(group.Key) || withDlc.Contains(group.Key) ? MemoirEvidence.Inferred : MemoirEvidence.Official;
                return new TimeMachineProgress(group.Key, current?.Name ?? archive.NameOf(group.Key), count, total, total > 0 ? evidence : MemoirEvidence.Unknown);
            })
            .ToList();
        var completed = perApp.Where(item => item.Total > 0 && item.Unlocked >= item.Total).ToList();
        var completedValue = new MemoirValue(completed.Count, completed.Any(item => item.Evidence != MemoirEvidence.Official) ? MemoirEvidence.Inferred : MemoirEvidence.Official);
        var lastUnlock = unlocks.GroupBy(unlock => unlock.AppId).ToDictionary(group => group.Key, group => group.Max(unlock => unlock.Time));
        return new TimeMachineAchievements(
            new MemoirValue(unlocks.Count, MemoirEvidence.Official),
            completedValue,
            perApp.Count,
            unlocks.Where(unlock => unlock.Time >= start).OrderBy(unlock => unlock.Time).ToList(),
            unlocks.OrderByDescending(unlock => unlock.Time).Take(MaxRecent).ToList(),
            unlocks.Where(unlock => unlock.GlobalPercent is > 0).OrderBy(unlock => unlock.GlobalPercent).Take(6).ToList(),
            perApp.OrderByDescending(item => lastUnlock.GetValueOrDefault(item.AppId)).Take(24).ToList());
    }

    private static TimeMachineFriends Friends(MemoirArchive archive, IReadOnlyList<InsightFriendPlay> plays, long start, long end)
    {
        var friends = new Dictionary<string, TimeMachineFriend>();
        long? knownSince = archive.FriendsKnownSince.Count > 0 ? archive.FriendsKnownSince.Values.Min() : null;
        var owners = archive.Friends.Select(friend => friend.OwnerSteamId)
            .Concat(archive.FriendChanges.Select(change => change.OwnerSteamId))
            .Distinct();
        foreach (var owner in owners)
        {
            var since = archive.FriendsKnownSince.TryGetValue(owner, out var value) ? value : (long?)null;
            var changes = archive.FriendChanges.Where(change => change.OwnerSteamId == owner).ToList();
            var current = archive.Friends.Where(friend => friend.OwnerSteamId == owner).ToDictionary(friend => friend.SteamId);
            var everyone = current.Keys.Concat(changes.Select(change => change.SteamId)).Distinct();
            foreach (var steamId in everyone)
            {
                var history = changes.Where(change => change.SteamId == steamId && change.Kind is "added" or "removed").OrderBy(change => change.Time).ToList();
                var before = history.LastOrDefault(change => change.Time < end);
                var after = history.FirstOrDefault(change => change.Time >= end);
                MemoirEvidence evidence;
                bool member;
                if (before != null)
                {
                    member = before.Kind == "added";
                    evidence = MemoirEvidence.Recorded;
                }
                else if (after != null)
                {
                    member = after.Kind == "removed";
                    evidence = member && (since == null || end < since) ? MemoirEvidence.Unknown : MemoirEvidence.Recorded;
                }
                else
                {
                    member = current.ContainsKey(steamId);
                    evidence = since is { } known && end >= known ? MemoirEvidence.Recorded : MemoirEvidence.Unknown;
                }
                if (!member) continue;
                var name = NameAt(changes.Where(change => change.SteamId == steamId && change.Kind == "renamed").ToList(), end)
                           ?? current.GetValueOrDefault(steamId)?.Name
                           ?? history.LastOrDefault()?.Name
                           ?? steamId;
                var added = history.LastOrDefault(change => change.Kind == "added" && change.Time < end)?.Time;
                var removed = history.FirstOrDefault(change => change.Kind == "removed" && change.Time >= end)?.Time;
                // 两个账号都有这个好友时，取证据强的那条
                if (friends.TryGetValue(steamId, out var existing) && existing.Evidence <= evidence) continue;
                friends[steamId] = new TimeMachineFriend(steamId, name, evidence, added, removed);
            }
        }
        var proven = friends.Values.Count(friend => friend.Evidence != MemoirEvidence.Unknown);
        var unsure = friends.Count - proven;
        var count = proven == 0 && unsure > 0
            ? MemoirValue.Unknown(unsure)
            : new MemoirValue(proven, MemoirEvidence.Recorded, unsure > 0 ? proven + unsure : null);
        var activity = plays
            .GroupBy(play => (play.SteamId, play.AppId))
            .Select(group => new TimeMachineFriendPlay(group.Key.SteamId, friends.GetValueOrDefault(group.Key.SteamId)?.Name ?? group.First().Name, group.Key.AppId, Merged(group)))
            .Where(play => play.Seconds > 0)
            .OrderByDescending(play => play.Seconds)
            .Take(30)
            .ToList();
        var dayChanges = archive.FriendChanges.Where(change => change.Time >= start && change.Time < end).ToList();
        return new TimeMachineFriends(
            count,
            knownSince,
            friends.Values.OrderBy(friend => friend.Evidence).ThenByDescending(friend => friend.AddedAt ?? 0).ThenBy(friend => friend.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            activity,
            dayChanges.Where(change => change.Kind == "added").Select(change => change.SteamId).Distinct().ToList(),
            dayChanges.Where(change => change.Kind == "removed").Select(change => change.SteamId).Distinct().ToList());
    }

    /// <summary>那个时候的名字：之前最后一次改名后的名字，没有时用之后第一次改名前的名字。</summary>
    private static string? NameAt(IReadOnlyList<MemoirFriendChange> renames, long time)
    {
        var before = renames.Where(change => change.Time < time).MaxBy(change => change.Time);
        if (before?.To is { Length: > 0 } to) return to;
        var after = renames.Where(change => change.Time >= time).MinBy(change => change.Time);
        return after?.From is { Length: > 0 } from ? from : null;
    }

    /// <summary>同一位好友同一款游戏的几段，两个账号都追踪时会重复，重叠只算一次。</summary>
    private static long Merged(IEnumerable<InsightFriendPlay> plays)
    {
        long total = 0, cursor = long.MinValue;
        foreach (var play in plays.OrderBy(item => item.Start))
        {
            var from = Math.Max(play.Start, cursor);
            if (play.End > from) total += play.End - from;
            cursor = Math.Max(cursor, play.End);
        }
        return total;
    }
}
