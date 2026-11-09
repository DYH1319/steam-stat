using SteamStat.Core.Insights;

namespace SteamStat.Core.Circle;

/// <summary>好友快照里的一位好友（多个账号的好友已合并）。</summary>
public sealed record CircleFriend(string SteamId, string Name, bool IsFriend, bool Tracked, long LastLogOff, bool Online);

/// <summary>成为好友的时间从哪里来，和回忆录的证据分级同名。</summary>
public static class SinceEvidence
{
    /// <summary>Steam Web API 的 friend_since。</summary>
    public const string Official = "official";
    /// <summary>Steam Stat 记下了加好友的那一刻。</summary>
    public const string Recorded = "recorded";
    public const string Unknown = "unknown";
}

public static class BondStatus
{
    /// <summary>最近 90 天一起玩过。</summary>
    public const string Active = "active";
    /// <summary>一起玩过不少（至少两小时），但半年没有了。</summary>
    public const string Lost = "lost";
    /// <summary>对方半年没上过线。</summary>
    public const string Dormant = "dormant";
    /// <summary>已经不在好友列表里。</summary>
    public const string Removed = "removed";
    /// <summary>30 天内新加的。</summary>
    public const string New = "new";
    /// <summary>一起玩过，但不多也不算失联。</summary>
    public const string Occasional = "occasional";
    /// <summary>还没一起玩过（没追踪的好友都是这样）。</summary>
    public const string Quiet = "quiet";
}

public sealed record TogetherMoment(long Time, int AppId, bool Confirmed);

public sealed record YearSeconds(int Year, long Seconds);

public sealed record FriendBond(
    string SteamId,
    string Name,
    bool IsFriend,
    bool Tracked,
    long? Since,
    string SinceEvidence,
    long? RemovedAt,
    long TogetherSeconds,
    long ConfirmedSeconds,
    int TogetherCount,
    TogetherMoment? FirstTogether,
    long? LastTogether,
    IReadOnlyList<YearSeconds> Years,
    int? PeakYear,
    long? NextAnniversary,
    int? NextAnniversaryYears,
    long LastLogOff,
    string Status)
{
    /// <summary>一起玩得最多的几款游戏。</summary>
    public IReadOnlyList<int> TopApps { get; init; } = [];
}

public static class FriendshipEventKinds
{
    public const string Met = "met";
    public const string Added = "added";
    public const string Removed = "removed";
    public const string FirstTogether = "firstTogether";
    public const string Peak = "peak";
    public const string Anniversary = "anniversary";
    public const string Lost = "lost";
}

/// <summary>友情时间线上的一件事。Value 随类型变：周年是年数，高峰是秒数。</summary>
public sealed record FriendshipEvent(string Kind, string SteamId, string Name, long Time, int? AppId = null, long? Value = null, bool Confirmed = false, string Evidence = SinceEvidence.Recorded);

public sealed record FriendshipReport(IReadOnlyList<FriendBond> Bonds, IReadOnlyList<FriendshipEvent> Events);

/// <summary>
/// 友情时间线：成为好友的时间、第一次一起玩、最高峰的一年、失联老友。
/// 一起玩的数据只有手动追踪的好友才有，所以没追踪的好友只有成为好友的时间和周年。
/// </summary>
public static class Friendship
{
    public const long ActiveSeconds = 90 * 86400L;
    public const long LostAfterSeconds = 180 * 86400L;
    public const long LostMinTogether = 2 * 3600L;
    public const long DormantSeconds = 180 * 86400L;
    public const long NewSeconds = 30 * 86400L;
    /// <summary>高峰年至少一起玩了这么久才说。</summary>
    public const long PeakMinSeconds = 3600L;
    public static readonly IReadOnlySet<int> Milestones = new HashSet<int> { 1, 3, 5, 10, 15, 20 };
    private const int MaxEvents = 2000;

    public static FriendshipReport Build(
        IReadOnlyList<CircleFriend> friends,
        IReadOnlyDictionary<string, long> officialSince,
        IReadOnlyList<InsightFriendChange> changes,
        IReadOnlyList<TogetherSegment> together,
        InsightOptions options,
        long now)
    {
        var bySteamId = together.GroupBy(segment => segment.FriendSteamId).ToDictionary(group => group.Key, group => group.OrderBy(segment => segment.Start).ToList());
        var relationship = changes.Where(change => change.Kind is "added" or "removed")
            .GroupBy(change => change.SteamId)
            .ToDictionary(group => group.Key, group => group.OrderBy(change => change.Time).ToList());

        var people = friends.ToDictionary(friend => friend.SteamId);
        // 删掉的好友不在快照里，但记录里有
        foreach (var (steamId, list) in relationship)
            if (!people.ContainsKey(steamId))
                people[steamId] = new CircleFriend(steamId, list[^1].Name, false, false, 0, false);
        foreach (var (steamId, segments) in bySteamId)
            if (!people.ContainsKey(steamId))
                people[steamId] = new CircleFriend(steamId, string.Empty, false, true, 0, false);

        var bonds = new List<FriendBond>();
        var events = new List<FriendshipEvent>();
        foreach (var friend in people.Values)
        {
            var history = relationship.GetValueOrDefault(friend.SteamId) ?? [];
            var lastAdded = history.LastOrDefault(change => change.Kind == "added");
            var lastRemoved = history.LastOrDefault(change => change.Kind == "removed");
            long? since = null;
            var evidence = SinceEvidence.Unknown;
            if (officialSince.TryGetValue(friend.SteamId, out var official) && official > 0)
            {
                since = official;
                evidence = SinceEvidence.Official;
            }
            else if (lastAdded != null && (lastRemoved == null || lastAdded.Time > lastRemoved.Time))
            {
                since = lastAdded.Time;
                evidence = SinceEvidence.Recorded;
            }
            long? removedAt = !friend.IsFriend && lastRemoved != null ? lastRemoved.Time : null;

            var segments = bySteamId.GetValueOrDefault(friend.SteamId) ?? [];
            var totalSeconds = segments.Sum(segment => segment.Seconds);
            var confirmedSeconds = segments.Sum(segment => segment.ConfirmedSeconds);
            var first = segments.FirstOrDefault();
            long? last = segments.Count == 0 ? null : segments.Max(segment => segment.End);
            var years = segments.GroupBy(segment => options.Local(segment.Start).Year)
                .Select(group => new YearSeconds(group.Key, group.Sum(segment => segment.Seconds)))
                .OrderBy(year => year.Year)
                .ToList();
            var peak = years.Where(year => year.Seconds >= PeakMinSeconds).OrderByDescending(year => year.Seconds).ThenBy(year => year.Year).FirstOrDefault();

            (long Time, int Years)? next = null;
            if (since is { } start && friend.IsFriend)
            {
                var origin = options.Local(start);
                var elapsed = Math.Max(1, options.Local(now).Year - origin.Year);
                for (var count = elapsed; count <= elapsed + 1; count++)
                {
                    var date = origin.AddYears(count).ToUnixTimeSeconds();
                    if (date < now - 86400) continue;
                    next = (date, count);
                    break;
                }
            }

            var status = removedAt != null ? BondStatus.Removed
                : since is { } added && now - added < NewSeconds ? BondStatus.New
                : last is { } recent && now - recent < ActiveSeconds ? BondStatus.Active
                : last is { } old && totalSeconds >= LostMinTogether && now - old >= LostAfterSeconds ? BondStatus.Lost
                : !friend.Online && friend.LastLogOff > 0 && now - friend.LastLogOff >= DormantSeconds ? BondStatus.Dormant
                : totalSeconds > 0 ? BondStatus.Occasional
                : BondStatus.Quiet;

            var name = friend.Name.Length > 0 ? friend.Name : history.LastOrDefault()?.Name ?? string.Empty;
            bonds.Add(new FriendBond(
                friend.SteamId, name, friend.IsFriend, friend.Tracked, since, evidence, removedAt,
                totalSeconds, confirmedSeconds, segments.Count,
                first == null ? null : new TogetherMoment(first.Start, first.AppId, first.Confirmed),
                last, years, peak?.Year, next?.Time, next?.Years, friend.LastLogOff, status)
            {
                TopApps = segments.GroupBy(segment => segment.AppId).OrderByDescending(group => group.Sum(segment => segment.Seconds)).Take(3).Select(group => group.Key).ToList()
            });

            if (evidence == SinceEvidence.Official && since is { } met)
                events.Add(new FriendshipEvent(FriendshipEventKinds.Met, friend.SteamId, name, met, Evidence: SinceEvidence.Official));
            foreach (var change in history)
            {
                // 官方时间已经说明了这次加好友，同一天的记录不再重复
                if (change.Kind == "added" && evidence == SinceEvidence.Official && Math.Abs(change.Time - since!.Value) < 86400) continue;
                events.Add(new FriendshipEvent(change.Kind == "added" ? FriendshipEventKinds.Added : FriendshipEventKinds.Removed, friend.SteamId, name, change.Time));
            }
            if (first != null)
                events.Add(new FriendshipEvent(FriendshipEventKinds.FirstTogether, friend.SteamId, name, first.Start, first.AppId, first.Seconds, first.Confirmed));
            if (peak != null && years.Count > 1)
                events.Add(new FriendshipEvent(FriendshipEventKinds.Peak, friend.SteamId, name,
                    new DateTimeOffset(peak.Year, 12, 31, 12, 0, 0, options.Local(now).Offset).ToUnixTimeSeconds(), Value: peak.Seconds));
            if (since is { } anniversaryOrigin && friend.IsFriend)
            {
                var local = options.Local(anniversaryOrigin);
                for (var count = 1; count <= 40; count++)
                {
                    var date = local.AddYears(count).ToUnixTimeSeconds();
                    if (date > now) break;
                    if (Milestones.Contains(count))
                        events.Add(new FriendshipEvent(FriendshipEventKinds.Anniversary, friend.SteamId, name, date, Value: count, Evidence: evidence));
                }
            }
            if (status == BondStatus.Lost && last is { } lostFrom)
                events.Add(new FriendshipEvent(FriendshipEventKinds.Lost, friend.SteamId, name, lostFrom + LostAfterSeconds, Value: totalSeconds));
        }

        return new FriendshipReport(
            bonds.OrderByDescending(bond => bond.TogetherSeconds).ThenBy(bond => bond.Since ?? long.MaxValue).ToList(),
            events.Where(item => item.Time <= now).OrderByDescending(item => item.Time).Take(MaxEvents).ToList());
    }
}
