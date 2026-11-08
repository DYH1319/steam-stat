using System.Globalization;
using SteamStat.Core.Insights;

namespace SteamStat.Core.Memoir;

/// <summary>语义视图之外的数据：好友快照、年度回顾、游戏库里的首玩时间和游戏名。由 Host 提供，隐藏游戏由它剔除。</summary>
public interface IMemoirCatalog
{
    Task<IReadOnlyList<MemoirFriend>> GetFriendsAsync(InsightScope scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<YearReview>> GetYearReviewsAsync(InsightScope scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoirFirstPlay>> GetLibraryFirstPlaysAsync(InsightScope scope, CancellationToken cancellationToken = default);

    IReadOnlyDictionary<int, string> GetNames(IEnumerable<int> appIds);
}

/// <summary>在洞察档案上补读史前考古需要的证据。SQL 只经语义视图，隐藏游戏和口径设置自动生效。</summary>
public static class MemoirArchiveLoader
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    public static async Task<MemoirArchive> LoadAsync(
        InsightArchive insight,
        IInsightDataSource data,
        IMemoirCatalog catalog,
        InsightScope scope,
        CancellationToken cancellationToken = default)
    {
        async Task<IReadOnlyList<object?[]>> Query(string sql)
            => await data.QueryAsync(sql, NoParameters, cancellationToken).ConfigureAwait(false);

        var official = (await Query("SELECT steam_id, app_id, playtime_minutes, first_observed_at, last_observed_at FROM v_official_playtime").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirOfficialPlaytime(Text(row[0]) ?? string.Empty, Int(row[1]), Int(row[2]), Long(row[3]), Long(row[4])))
            .Where(row => row.AppId > 0)
            .ToList();

        var firstPlays = (await Query("SELECT steam_id, app_id, first_played FROM v_platform_playtime WHERE first_played > 0").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirFirstPlay(Text(row[0]) ?? string.Empty, Int(row[1]), Long(row[2])))
            .Concat(await catalog.GetLibraryFirstPlaysAsync(scope, cancellationToken).ConfigureAwait(false))
            .ToList();

        var reviews = await catalog.GetYearReviewsAsync(scope, cancellationToken).ConfigureAwait(false);
        firstPlays.AddRange(reviews.SelectMany(review => review.Games
            .Where(game => game.FirstPlayed is > 0)
            .Select(game => new MemoirFirstPlay(review.SteamId, game.AppId, game.FirstPlayed!.Value))));
        // 每个账号每款游戏只留最早的一个：几个来源说法不同时，最早的那个才是首玩
        firstPlays = firstPlays
            .Where(item => item.AppId > 0 && item.FirstPlayed > 0)
            .GroupBy(item => (item.SteamId, item.AppId))
            .Select(group => group.MinBy(item => item.FirstPlayed)!)
            .ToList();

        var traces = new List<MemoirTrace>();
        traces.AddRange((await Query("SELECT steam_id, app_id, unlock_time FROM v_achievement_unlock").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirTrace(Text(row[0]) ?? string.Empty, Int(row[1]), MemoirTraceKind.Achievement, Long(row[2]))));
        traces.AddRange((await Query("SELECT steam_id, app_id, taken_at FROM v_screenshot WHERE taken_at > 0").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirTrace(Text(row[0]) ?? string.Empty, Int(row[1]), MemoirTraceKind.Screenshot, Long(row[2]))));
        traces.AddRange((await Query("SELECT steam_id, app_id, written_at FROM v_cloud_save_trace").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirTrace(Text(row[0]) ?? string.Empty, Int(row[1]), MemoirTraceKind.CloudSave, Long(row[2]))));
        traces.AddRange(firstPlays.Select(item => new MemoirTrace(item.SteamId, item.AppId, MemoirTraceKind.FirstPlay, item.FirstPlayed)));
        traces = traces.Where(trace => trace.AppId > 0 && trace.Time > 0 && trace.Time <= insight.Now).OrderBy(trace => trace.Time).ToList();

        var friendRows = (await Query("""
                SELECT steam_id, friend_steam_id, friend_persona_name, change_type, previous_value, current_value, observed_at
                FROM v_friend_event
                WHERE change_type IN ('relationship', 'personaName')
                ORDER BY observed_at
                """).ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .ToList();
        var changes = new List<MemoirFriendChange>();
        foreach (var owner in friendRows.GroupBy(row => Text(row[0]) ?? string.Empty))
        {
            var timeline = FriendTimeline.Build(owner.Select(row => new FriendEventRow(Text(row[1]) ?? string.Empty, Text(row[2]) ?? string.Empty, Text(row[3]) ?? string.Empty, Text(row[4]), Text(row[5]), Long(row[6]))));
            changes.AddRange(timeline.Changes.Select(change => new MemoirFriendChange(owner.Key, change.SteamId, change.Name, change.Kind, change.From, change.To, change.Time)));
        }
        var knownSince = (await Query("SELECT steam_id, MIN(observed_at) FROM v_friend_event GROUP BY steam_id").ConfigureAwait(false))
            .Where(row => Text(row[0]) is { } id && scope.Includes(id))
            .ToDictionary(row => Text(row[0])!, row => Long(row[1]));

        var profile = (await Query("SELECT steam_id, field, value, observed_at FROM v_profile_change WHERE field IN ('personaName', 'level') ORDER BY observed_at").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirProfileChange(Text(row[0]) ?? string.Empty, Text(row[1]) ?? string.Empty, Text(row[2]) ?? string.Empty, Long(row[3])))
            .ToList();

        var lifeEvents = (await Query("SELECT event_id, title, note, kind, start_day, end_day, boundary, updated_at FROM v_life_event ORDER BY start_day, event_id").ConfigureAwait(false))
            .Select(row => new MemoirLifeEvent(Int(row[0]), Text(row[1]) ?? string.Empty, Text(row[2]), Text(row[3]) ?? "other", Int(row[4]), row[5] == null ? null : Int(row[5]), Int(row[6]) != 0, Long(row[7])))
            .ToList();

        var acquisitions = (await Query("SELECT steam_id, app_id, package_id, acquired_at FROM v_library_acquisition WHERE app_id > 0").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new MemoirAcquisition(Text(row[0]) ?? string.Empty, Int(row[1]), Long(row[2]), Long(row[3])))
            .ToList();

        var friends = await catalog.GetFriendsAsync(scope, cancellationToken).ConfigureAwait(false);

        var names = new Dictionary<int, string>();
        foreach (var game in insight.Owned) names.TryAdd(game.AppId, game.Name);
        foreach (var session in insight.Sessions) names.TryAdd(session.AppId, session.Name);
        foreach (var progress in insight.Progress) names.TryAdd(progress.AppId, progress.Name);
        var missing = traces.Select(trace => trace.AppId)
            .Concat(official.Select(item => item.AppId))
            .Concat(reviews.SelectMany(review => review.Games.Select(game => game.AppId)))
            .Where(appId => !names.ContainsKey(appId))
            .Distinct()
            .ToList();
        if (missing.Count > 0)
            foreach (var (appId, name) in catalog.GetNames(missing))
                names.TryAdd(appId, name);

        return new MemoirArchive
        {
            Insight = insight,
            Official = official,
            FirstPlays = firstPlays,
            Traces = traces,
            YearReviews = reviews,
            Friends = friends,
            FriendChanges = changes,
            FriendsKnownSince = knownSince,
            Profile = profile,
            LifeEvents = lifeEvents,
            Acquisitions = acquisitions,
            Names = names
        };
    }

    /// <summary>
    /// 一段时间里好友在玩什么。好友记录可能有上百万条，所以只读这一段。前后各多读半天：
    /// 跨过两端的那一局要读到它的开始和结束才能闭合，读进来以后再裁到这一段。
    /// </summary>
    public static async Task<IReadOnlyList<InsightFriendPlay>> LoadFriendPlaysAsync(
        IInsightDataSource data,
        InsightScope scope,
        long from,
        long to,
        CancellationToken cancellationToken = default)
    {
        var rows = await data.QueryAsync("""
                SELECT steam_id, friend_steam_id, friend_persona_name, change_type, previous_value, current_value, observed_at
                FROM v_friend_event
                WHERE observed_at >= $from AND observed_at < $to AND change_type IN ('state', 'game')
                ORDER BY observed_at
                """, new Dictionary<string, object?> { ["$from"] = from - 12 * 3600, ["$to"] = to + 12 * 3600 }, cancellationToken).ConfigureAwait(false);
        var events = rows
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new FriendEventRow(Text(row[1]) ?? string.Empty, Text(row[2]) ?? string.Empty, Text(row[3]) ?? string.Empty, Text(row[4]), Text(row[5]), Long(row[6])));
        return FriendTimeline.Build(events).Plays
            .Select(play => play with { Start = Math.Max(play.Start, from), End = Math.Min(play.End, to) })
            .Where(play => play.End > play.Start)
            .ToList();
    }

    private static string? Text(object? value) => value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static long Long(object? value) => value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static int Int(object? value) => value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
}
