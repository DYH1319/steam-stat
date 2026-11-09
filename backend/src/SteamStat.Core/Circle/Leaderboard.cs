namespace SteamStat.Core.Circle;

/// <summary>好友时长快照的一行：值变化才写，第一条是基线。</summary>
public sealed record PlaytimeObservation(string SteamId, int AppId, int Minutes, long Time);

public static class BoardSources
{
    public const string Me = "me";
    /// <summary>好友时长快照，自动更新，不需要对方安装。</summary>
    public const string Snapshot = "snapshot";
    /// <summary>对方发来的交换卡，停在生成那一刻。</summary>
    public const string Card = "card";
}

/// <summary>
/// 排行榜的一行。时长都是分钟；窗口内的数字在观测不够长时为空，不拿不完整的差值去和别人比。
/// </summary>
public sealed record BoardEntry(
    string SteamId,
    string Source,
    long AsOf,
    int? Last7,
    int? Last30,
    int? Lifetime,
    int? Games,
    int? Achievements,
    /// <summary>快照开始的时间。窗口比它长时窗口数字为空。</summary>
    long? ObservedSince);

public sealed record GameBoardEntry(string SteamId, string Source, long AsOf, int Minutes, int? Last30);

/// <summary>交换卡上与排行榜有关的数字。</summary>
public sealed record CardNumbers(string SteamId, long GeneratedAt, int Last7, int Last30, int Lifetime, int Games, int Achievements, IReadOnlyDictionary<int, int> GameMinutes);

public static class Leaderboard
{
    /// <summary>
    /// 快照的差值：窗口末的值减去窗口开始时的值。窗口开始时还没有快照就不知道，返回 null。
    /// 窗口开始时快照里没有的游戏当时是 0：采集时没玩过的游戏不写，所以没有记录就是 0。
    /// </summary>
    /// <param name="observedSince">这个人的第一次快照。只看一款游戏时要传进来：这款游戏晚出现不代表观测开始得晚。</param>
    public static int? Delta(IReadOnlyList<PlaytimeObservation> rows, long from, long? observedSince = null)
    {
        if (rows.Count == 0) return observedSince is { } known && known <= from ? 0 : null;
        var baseline = observedSince ?? rows.Min(row => row.Time);
        if (baseline > from) return null;
        var total = 0;
        foreach (var app in rows.GroupBy(row => row.AppId))
        {
            var latest = app.MaxBy(row => row.Time)!.Minutes;
            var start = app.Where(row => row.Time <= from).MaxBy(row => row.Time)?.Minutes ?? 0;
            total += Math.Max(0, latest - start);
        }
        return total;
    }

    public static IReadOnlyDictionary<int, int> Latest(IEnumerable<PlaytimeObservation> rows)
        => rows.GroupBy(row => row.AppId).ToDictionary(group => group.Key, group => group.MaxBy(row => row.Time)!.Minutes);

    /// <summary>
    /// 合并三种来源。同一个人既有快照又有交换卡时，窗口数字用快照（自动更新），快照不够长时用卡；
    /// 总时长和游戏数取卡和快照里较新的那个：私密游戏库只有卡里有。
    /// </summary>
    public static IReadOnlyList<BoardEntry> Build(
        BoardEntry me,
        IReadOnlyDictionary<string, IReadOnlyList<PlaytimeObservation>> snapshots,
        IReadOnlyDictionary<string, int> achievements,
        IReadOnlyList<CardNumbers> cards,
        long now)
    {
        var entries = new List<BoardEntry> { me };
        var cardBySender = cards.GroupBy(card => card.SteamId).ToDictionary(group => group.Key, group => group.MaxBy(card => card.GeneratedAt)!);
        foreach (var steamId in snapshots.Keys.Union(cardBySender.Keys).Where(id => id != me.SteamId))
        {
            BoardEntry? snapshot = null;
            if (snapshots.TryGetValue(steamId, out var rows) && rows.Count > 0)
            {
                var latest = Latest(rows);
                snapshot = new BoardEntry(
                    steamId, BoardSources.Snapshot, rows.Max(row => row.Time),
                    Delta(rows, now - 7 * 86400L), Delta(rows, now - 30 * 86400L),
                    latest.Values.Sum(), latest.Count(pair => pair.Value > 0),
                    achievements.TryGetValue(steamId, out var unlocked) ? unlocked : null,
                    rows.Min(row => row.Time));
            }
            cardBySender.TryGetValue(steamId, out var card);
            if (snapshot == null)
            {
                entries.Add(new BoardEntry(steamId, BoardSources.Card, card!.GeneratedAt, card.Last7, card.Last30, card.Lifetime, card.Games, card.Achievements, null));
                continue;
            }
            if (card == null)
            {
                entries.Add(snapshot);
                continue;
            }
            var cardNewer = card.GeneratedAt > snapshot.AsOf;
            entries.Add(snapshot with
            {
                Last7 = snapshot.Last7 ?? card.Last7,
                Last30 = snapshot.Last30 ?? card.Last30,
                Lifetime = cardNewer ? card.Lifetime : snapshot.Lifetime,
                Games = cardNewer ? card.Games : snapshot.Games,
                Achievements = snapshot.Achievements ?? card.Achievements
            });
        }
        return entries;
    }

    public static IReadOnlyList<GameBoardEntry> ForGame(
        int appId,
        GameBoardEntry? me,
        IReadOnlyDictionary<string, IReadOnlyList<PlaytimeObservation>> snapshots,
        IReadOnlyList<CardNumbers> cards,
        long now)
    {
        var entries = new List<GameBoardEntry>();
        if (me != null) entries.Add(me);
        var seen = new HashSet<string>(entries.Select(entry => entry.SteamId));
        foreach (var (steamId, rows) in snapshots)
        {
            var app = rows.Where(row => row.AppId == appId).ToList();
            if (app.Count == 0 || !seen.Add(steamId)) continue;
            entries.Add(new GameBoardEntry(steamId, BoardSources.Snapshot, app.Max(row => row.Time), app.MaxBy(row => row.Time)!.Minutes, Delta(app, now - 30 * 86400L, rows.Min(row => row.Time))));
        }
        foreach (var card in cards.GroupBy(card => card.SteamId).Select(group => group.MaxBy(card => card.GeneratedAt)!))
        {
            if (!card.GameMinutes.TryGetValue(appId, out var minutes) || !seen.Add(card.SteamId)) continue;
            entries.Add(new GameBoardEntry(card.SteamId, BoardSources.Card, card.GeneratedAt, minutes, null));
        }
        return entries.OrderByDescending(entry => entry.Minutes).ToList();
    }
}
