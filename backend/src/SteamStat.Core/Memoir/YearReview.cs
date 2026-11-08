using ProtoBuf;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Memoir;

/// <summary>Steam 年度回顾里一个月的情况。时长和占比可能只有其一，取决于 Steam 这一年给了什么。</summary>
public sealed record YearReviewMonth(int Month, long? Seconds, double? Share, int? Sessions, IReadOnlyList<YearReviewMonthGame> Games);

/// <summary>这个月里一款游戏。Share 是占当月的比例。</summary>
public sealed record YearReviewMonthGame(int AppId, long? Seconds, double? Share);

public sealed record YearReviewGame(int AppId, long? Seconds, double? Share, int? Sessions, bool NewThisYear, long? FirstPlayed);

public sealed record YearReviewStreak(int Days, long? Start, IReadOnlyList<int> AppIds);

/// <summary>全球中位数和用户的百分位。by_numbers 里 *_pct 的含义（是不是百分位）待真实账号验证。</summary>
public sealed record YearReviewPercentiles(
    int? MedianAchievements,
    int? MedianGames,
    int? MedianStreak,
    int? GamesPlayedPercentile,
    int? AchievementsPercentile,
    int? StreakPercentile,
    int? NewReleasesShare,
    int? RecentReleasesShare,
    int? ClassicReleasesShare,
    int? RecentCutoffYear);

/// <summary>一个账号一年的 Steam 年度回顾。官方汇总，没有逐日数据。</summary>
public sealed record YearReview(
    string SteamId,
    int Year,
    long FetchedAt,
    long? Seconds,
    int? Sessions,
    int GamesPlayed,
    int NewGames,
    int? Achievements,
    YearReviewStreak? Streak,
    IReadOnlyList<YearReviewMonth> Months,
    IReadOnlyList<YearReviewGame> Games,
    YearReviewPercentiles? Percentiles,
    IReadOnlyDictionary<string, double> Platforms)
{
    public bool IsEmpty => GamesPlayed == 0 && (Seconds ?? 0) == 0 && Months.All(month => (month.Seconds ?? 0) == 0 && month.Games.Count == 0);

    /// <summary>某月某款游戏的秒数，Steam 只给占比时按当月时长换算。</summary>
    public long? SecondsOf(int month, int appId)
    {
        var entry = Months.FirstOrDefault(item => item.Month == month);
        var game = entry?.Games.FirstOrDefault(item => item.AppId == appId);
        if (game == null) return null;
        if (game.Seconds is { } seconds) return seconds;
        return game.Share is { } share && entry!.Seconds is { } total ? (long)Math.Round(total * share) : null;
    }
}

/// <summary>
/// 解析 SaleFeature.GetUserYearInReview 的回复。只给出 Steam 明确给了的数字；
/// 占比是 x100 的整数（1234 = 12.34%）。原始字节由 Host 保存，解析器升级时加 <see cref="Version"/>。
/// </summary>
public static class YearReviewParser
{
    public const int Version = 1;

    public static YearReview? Parse(string steamId, int year, long fetchedAt, byte[] payload)
    {
        CSaleFeature_GetUserYearInReview_Response response;
        try
        {
            using var stream = new MemoryStream(payload);
            response = Serializer.Deserialize<CSaleFeature_GetUserYearInReview_Response>(stream);
        }
        catch (Exception exception) when (exception is ProtoException or InvalidOperationException or EndOfStreamException)
        {
            return null;
        }
        var stats = response.stats?.playtime_stats;
        if (stats == null) return new YearReview(steamId, year, fetchedAt, null, null, 0, 0, null, null, [], [], Percentiles(response, null), new Dictionary<string, double>());

        var total = Positive(stats.total_stats?.total_playtime_seconds);
        var summaries = stats.game_summary.Where(game => game.appid > 0 && !game.demo && !game.playtest).ToList();
        var bySummary = summaries.GroupBy(game => game.appid).ToDictionary(group => group.Key, group => group.First());

        var games = new Dictionary<int, YearReviewGame>();
        foreach (var game in stats.games.Where(game => game.appid > 0))
        {
            bySummary.TryGetValue(game.appid, out var summary);
            var share = Share(summary?.total_playtime_percentagex100) ?? Share(game.stats?.total_playtime_percentagex100);
            games[(int)game.appid] = new YearReviewGame(
                (int)game.appid,
                Positive(game.stats?.total_playtime_seconds) ?? Scale(total, share),
                share,
                PositiveInt(game.stats?.total_sessions) ?? PositiveInt(summary?.total_sessions),
                summary?.new_this_year ?? false,
                Positive(summary?.rtime_first_played_lifetime) ?? Positive(game.rtime_first_played));
        }
        foreach (var summary in summaries.Where(summary => !games.ContainsKey((int)summary.appid)))
        {
            var share = Share(summary.total_playtime_percentagex100);
            games[(int)summary.appid] = new YearReviewGame((int)summary.appid, Scale(total, share), share, PositiveInt(summary.total_sessions), summary.new_this_year, Positive(summary.rtime_first_played_lifetime));
        }

        var months = stats.months
            .Where(month => month.rtime_month > 0)
            .Select(month =>
            {
                var monthNumber = DateTimeOffset.FromUnixTimeSeconds(month.rtime_month).UtcDateTime.AddHours(12).Month;
                var share = Share(month.stats?.total_playtime_percentagex100);
                var seconds = Positive(month.stats?.total_playtime_seconds) ?? Scale(total, share);
                var monthGames = new Dictionary<int, YearReviewMonthGame>();
                foreach (var game in month.appid.Where(game => game.appid > 0))
                    monthGames[(int)game.appid] = new YearReviewMonthGame((int)game.appid, Positive(game.stats?.total_playtime_seconds), Share(game.stats?.total_playtime_percentagex100));
                foreach (var game in month.game_summary.Where(game => game.appid > 0 && !monthGames.ContainsKey((int)game.appid)))
                    monthGames[(int)game.appid] = new YearReviewMonthGame((int)game.appid, null, Share(game.relative_playtime_percentagex100) ?? Share(game.total_playtime_percentagex100));
                return new YearReviewMonth(monthNumber, seconds, share, PositiveInt(month.stats?.total_sessions), monthGames.Values.OrderByDescending(game => game.Seconds ?? 0).ThenByDescending(game => game.Share ?? 0).ToList());
            })
            .GroupBy(month => month.Month)
            .Select(group => group.First())
            .OrderBy(month => month.Month)
            .ToList();

        var streak = stats.playtime_streak is { longest_consecutive_days: > 0 } value
            ? new YearReviewStreak((int)value.longest_consecutive_days, Positive(value.rtime_start), value.streak_games.Where(game => game.appid > 0).Select(game => (int)game.appid).Distinct().ToList())
            : null;

        var platforms = new Dictionary<string, double>();
        void Platform(string key, uint? value)
        {
            if (Share(value) is { } share && share > 0) platforms[key] = share;
        }
        Platform("windows", stats.total_stats?.windows_playtime_percentagex100);
        Platform("linux", stats.total_stats?.linux_playtime_percentagex100);
        Platform("mac", stats.total_stats?.macos_playtime_percentagex100);
        Platform("deck", stats.total_stats?.deck_playtime_percentagex100);
        Platform("vr", stats.total_stats?.vr_playtime_percentagex100);
        Platform("controller", stats.total_stats?.controller_playtime_percentagex100);

        return new YearReview(
            steamId,
            year,
            fetchedAt,
            total,
            PositiveInt(stats.total_stats?.total_sessions),
            games.Count,
            games.Values.Count(game => game.NewThisYear),
            PositiveInt(stats.summary_stats?.total_achievements),
            streak,
            months,
            games.Values.OrderByDescending(game => game.Seconds ?? 0).ThenByDescending(game => game.Share ?? 0).ToList(),
            Percentiles(response, stats.by_numbers),
            platforms);
    }

    private static YearReviewPercentiles? Percentiles(CSaleFeature_GetUserYearInReview_Response response, YirByNumbers? numbers)
    {
        var global = response.percentiles;
        var distribution = response.distribution;
        if (global == null && distribution == null && numbers == null) return null;
        return new YearReviewPercentiles(
            PositiveInt(global?.median_achievements),
            PositiveInt(global?.median_games),
            PositiveInt(global?.median_streak),
            PositiveInt(numbers?.games_played_pct),
            PositiveInt(numbers?.achievements_pct),
            PositiveInt(numbers?.game_streak_pct),
            PositiveInt(distribution?.new_releases),
            PositiveInt(distribution?.recent_releases),
            PositiveInt(distribution?.classic_releases),
            PositiveInt(distribution?.recent_cutoff_year));
    }

    private static long? Positive(uint? value) => value is > 0 ? value : null;

    private static int? PositiveInt(uint? value) => value is > 0 and <= int.MaxValue ? (int)value.Value : null;

    private static double? Share(uint? percentageX100) => percentageX100 is > 0 ? Math.Min(1, percentageX100.Value / 10000.0) : null;

    private static long? Scale(long? total, double? share) => total is { } seconds && share is { } value ? (long)Math.Round(seconds * value) : null;
}
