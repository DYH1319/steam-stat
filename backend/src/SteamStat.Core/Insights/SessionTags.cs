namespace SteamStat.Core.Insights;

/// <summary>会话自动标签，存在 use_app_record.auto_tags。数值是持久化格式，不要改。</summary>
[Flags]
public enum SessionTags
{
    None = 0,
    /// <summary>熬夜局：有至少半小时落在凌晨。</summary>
    LateNight = 1,
    /// <summary>开黑局：好友同时在玩同一款联机游戏。只说明同时在玩，无法确认在同一局。</summary>
    Coop = 2,
    /// <summary>挂机局：一半以上的时间没有操作。</summary>
    Idle = 4,
    /// <summary>回坑局：距上一局超过「暂停」天数。</summary>
    Comeback = 8,
    /// <summary>闪退：连续极短会话中的一条。</summary>
    Crash = 16
}

/// <summary>一条待打标的原始记录。</summary>
public sealed record TagCandidate(long Id, string SteamId, int AppId, long Start, long End, long Seconds, long IdleSeconds, int? BuildId = null);

/// <summary>一串连续闪退。</summary>
public sealed record CrashBurst(string SteamId, int AppId, long Start, long End, int Attempts, long Seconds, int? BuildId)
{
    /// <summary>这串闪退发生前后游戏更新过。</summary>
    public bool AfterUpdate { get; init; }
}

public static class CrashDetector
{
    /// <summary>单条记录不超过这个时长才可能是闪退。</summary>
    public const int MaxSeconds = 120;
    /// <summary>两次启动的间隔不超过这个值才算连续。</summary>
    public const int MaxGapSeconds = 180;
    /// <summary>一两次短会话可能只是看一眼，连续三次才算闪退。</summary>
    public const int MinAttempts = 3;
    /// <summary>闪退前这么久之内更新过，就怀疑是更新导致的。</summary>
    public const int UpdateWindowSeconds = 48 * 3600;

    /// <summary>在原始记录上检测：合并和最短时长规则都会抹掉闪退。</summary>
    public static IReadOnlyList<(CrashBurst Burst, IReadOnlyList<long> RecordIds)> Detect(IEnumerable<TagCandidate> records)
    {
        var bursts = new List<(CrashBurst, IReadOnlyList<long>)>();
        foreach (var group in records.GroupBy(record => (record.SteamId, record.AppId)))
        {
            var run = new List<TagCandidate>();
            foreach (var record in group.OrderBy(item => item.Start).ThenBy(item => item.Id))
            {
                var isShort = record.Seconds <= MaxSeconds;
                if (run.Count > 0 && (!isShort || record.Start - run[^1].End > MaxGapSeconds))
                {
                    Close(run, bursts);
                    run = [];
                }
                if (isShort) run.Add(record);
            }
            Close(run, bursts);
        }
        return bursts.OrderBy(item => item.Item1.Start).ToList();
    }

    private static void Close(List<TagCandidate> run, List<(CrashBurst, IReadOnlyList<long>)> bursts)
    {
        if (run.Count < MinAttempts) return;
        bursts.Add((
            new CrashBurst(run[0].SteamId, run[0].AppId, run[0].Start, run[^1].End, run.Count, run.Sum(item => item.Seconds), run[^1].BuildId),
            run.Select(item => item.Id).ToList()));
    }

    /// <summary>标记更新后出现的闪退。版本史只有本机观测到的变化，第一次看到不算更新。</summary>
    public static IReadOnlyList<CrashBurst> WithUpdateSuspicion(IEnumerable<CrashBurst> bursts, IEnumerable<InsightBuildChange> builds)
    {
        var byApp = builds.Where(build => !build.IsBaseline).ToLookup(build => build.AppId);
        return bursts
            .Select(burst => burst with
            {
                AfterUpdate = byApp[burst.AppId].Any(build => build.ObservedAt <= burst.End && build.ObservedAt >= burst.Start - UpdateWindowSeconds)
            })
            .ToList();
    }
}

public sealed record SessionTagRules(int PauseDays, TimeZoneInfo TimeZone);

/// <summary>打标规则（纯函数）。开黑标签要读好友记录，由调用方算好后合并进来。</summary>
public static class SessionTagger
{
    public const int NightStartHour = 0;
    public const int NightEndHour = 5;
    public const int MinNightSeconds = 30 * 60;
    public const double IdleShare = 0.5;
    public const int MinIdleSessionSeconds = 30 * 60;
    public const int MinCoopSeconds = 15 * 60;

    /// <summary>公共数据里表示可以和别人联机的标签。和渲染层 canPlayTogether 一致。</summary>
    private static readonly HashSet<string> OnlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Multiplayer", "Co-op", "Online Co-Op", "Co-op Campaign", "PvP", "Massively Multiplayer", "MMORPG"
    };

    /// <summary>没有标签的游戏无法判断，按可以处理：公共数据没有覆盖全部游戏。</summary>
    public static bool CanPlayTogether(IReadOnlyCollection<string>? tags)
        => tags is not { Count: > 0 } || tags.Any(OnlineTags.Contains);

    /// <summary>除开黑以外的全部标签。records 必须是同一批完整记录，回坑要看上一局。</summary>
    public static IReadOnlyDictionary<long, SessionTags> Tag(IReadOnlyList<TagCandidate> records, SessionTagRules rules)
    {
        var tags = new Dictionary<long, SessionTags>(records.Count);
        foreach (var record in records)
        {
            var value = SessionTags.None;
            if (NightSeconds(record.Start, record.End, rules.TimeZone) >= MinNightSeconds) value |= SessionTags.LateNight;
            if (record.Seconds >= MinIdleSessionSeconds && record.IdleSeconds >= record.Seconds * IdleShare) value |= SessionTags.Idle;
            tags[record.Id] = value;
        }

        foreach (var (burst, ids) in CrashDetector.Detect(records))
            foreach (var id in ids)
                tags[id] |= SessionTags.Crash;

        var pauseSeconds = rules.PauseDays * 86400L;
        foreach (var group in records.GroupBy(record => (record.SteamId, record.AppId)))
        {
            long? previousEnd = null;
            foreach (var record in group.OrderBy(item => item.Start).ThenBy(item => item.Id))
            {
                if ((tags[record.Id] & SessionTags.Crash) != 0) continue;
                if (previousEnd != null && record.Start - previousEnd >= pauseSeconds) tags[record.Id] |= SessionTags.Comeback;
                previousEnd = Math.Max(previousEnd ?? 0, record.End);
            }
        }
        return tags;
    }

    /// <summary>[start, end) 落在凌晨窗口的秒数。</summary>
    public static long NightSeconds(long start, long end, TimeZoneInfo timeZone)
        => WindowSeconds(start, end, NightStartHour, NightEndHour, timeZone);

    /// <summary>
    /// [start, end) 落在每天 fromHour~toHour 的秒数。按本地日历逐天算，夏令时切换日也对。
    /// toHour 可以到 24；要跨午夜时分成两段调用。
    /// </summary>
    public static long WindowSeconds(long start, long end, int fromHour, int toHour, TimeZoneInfo timeZone)
    {
        if (end <= start) return 0;
        long total = 0;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(start), timeZone).DateTime);
        while (true)
        {
            var windowStart = ToUnix(day, fromHour, timeZone);
            if (windowStart >= end) break;
            var windowEnd = ToUnix(day, toHour, timeZone);
            total += Math.Max(0, Math.Min(end, windowEnd) - Math.Max(start, windowStart));
            day = day.AddDays(1);
        }
        return total;
    }

    private static long ToUnix(DateOnly day, int hour, TimeZoneInfo timeZone)
    {
        var local = hour >= 24 ? day.AddDays(1).ToDateTime(TimeOnly.MinValue) : day.ToDateTime(new TimeOnly(hour, 0));
        // 夏令时跳过的时刻不存在，往后挪一小时，这种日子差一小时无所谓
        if (timeZone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    }
}
