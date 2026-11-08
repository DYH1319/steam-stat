namespace SteamStat.Core.Memoir;

/// <summary>
/// 一个账号一款游戏的累计时长随时间的变化，每个时刻都带证据等级。
/// <para>
/// 锚点按可信度排：官方首玩（之前一定是 0）、官方时长的平台期（playtime_snapshot，平台期内值不变）、
/// Steam Stat 记录的会话（平台期之后的增量）。安装之前、第一个平台期之前的部分只知道总量，
/// 按痕迹（成就、截图、云存档）的日期分摊，结果是推算；连从哪天开始都不知道时是未知，只给上限。
/// </para>
/// </summary>
public sealed class AppPlaytimeTimeline
{
    /// <summary>没有痕迹的日子也分一点，否则两次痕迹之间的时长是一条水平线，不像真的。</summary>
    internal const double QuietDayWeight = 0.02;
    /// <summary>不到一分钟的差额当作没有：官方时长按分钟取整。</summary>
    private const long Slack = 60;

    private readonly long[] _starts;
    private readonly long[] _cumulative;
    private readonly IReadOnlyList<MemoirOfficialPlaytime> _plateaus;
    private readonly long[] _traceDays;

    public AppPlaytimeTimeline(
        string steamId,
        int appId,
        IEnumerable<(long Start, long Seconds)> sessions,
        IEnumerable<MemoirOfficialPlaytime> plateaus,
        long? firstPlayed,
        IEnumerable<long> traceTimes,
        long? officialNowSeconds,
        long now)
    {
        SteamId = steamId;
        AppId = appId;
        var ordered = sessions.Where(item => item.Seconds > 0).OrderBy(item => item.Start).ToArray();
        _starts = ordered.Select(item => item.Start).ToArray();
        _cumulative = new long[ordered.Length + 1];
        for (var index = 0; index < ordered.Length; index++) _cumulative[index + 1] = _cumulative[index] + ordered[index].Seconds;
        _plateaus = plateaus.OrderBy(item => item.FirstObservedAt).ThenBy(item => item.Minutes).ToList();
        FirstPlayed = firstPlayed is > 0 ? firstPlayed : null;
        _traceDays = traceTimes.Where(time => time > 0).Select(time => time / 86400).Distinct().Order().ToArray();

        if (_plateaus.Count > 0)
        {
            BaseTime = _plateaus[0].FirstObservedAt;
            BaseEvidence = MemoirEvidence.Official;
            Untracked = Math.Max(0, _plateaus[0].Minutes * 60L - RecordedThrough(BaseTime));
        }
        else if (_starts.Length > 0)
        {
            // 没有官方快照：官方总时长减去记录的总时长，差额当作安装之前玩的
            BaseTime = _starts[0] - 1;
            BaseEvidence = MemoirEvidence.Inferred;
            Untracked = officialNowSeconds is { } official ? Math.Max(0, official - RecordedTotal) : 0;
            if (officialNowSeconds == null) BaseEvidence = MemoirEvidence.Recorded;
        }
        else
        {
            BaseTime = now;
            BaseEvidence = officialNowSeconds == null ? MemoirEvidence.Unknown : MemoirEvidence.Official;
            Untracked = officialNowSeconds ?? 0;
        }
        if (Untracked < Slack) Untracked = 0;

        var earliestTrace = _traceDays.Length > 0 ? _traceDays[0] * 86400 : (long?)null;
        var earliestSession = _starts.Length > 0 ? _starts[0] : (long?)null;
        PrehistoryStart = new[] { FirstPlayed, earliestTrace, earliestSession }.Where(time => time is > 0 && time < BaseTime).Min();
    }

    public string SteamId { get; }

    public int AppId { get; }

    public long? FirstPlayed { get; }

    /// <summary>史前和记录的分界：第一个官方平台期，没有时是第一局之前。</summary>
    public long BaseTime { get; }

    public MemoirEvidence BaseEvidence { get; }

    /// <summary>分界之前、记录解释不了的时长。</summary>
    public long Untracked { get; }

    /// <summary>史前从什么时候开始：官方首玩、最早的痕迹或最早的会话。</summary>
    public long? PrehistoryStart { get; }

    public long RecordedTotal => _cumulative[^1];

    public bool HasSessions => _starts.Length > 0;

    /// <summary>开始时间不晚于 t 的会话总时长。</summary>
    public long RecordedThrough(long t)
    {
        var index = UpperBound(_starts, t);
        return _cumulative[index];
    }

    /// <summary>开始时间在 (from, to] 的会话总时长。</summary>
    public long RecordedBetween(long from, long to) => to <= from ? 0 : RecordedThrough(to) - RecordedThrough(from);

    /// <summary>t 时刻的累计时长。</summary>
    public MemoirValue At(long t)
    {
        if (FirstPlayed is { } first && t < first) return MemoirValue.Zero(MemoirEvidence.Official);

        var index = LastPlateauAtOrBefore(t);
        if (index >= 0)
        {
            var plateau = _plateaus[index];
            var value = plateau.Minutes * 60L;
            if (t <= plateau.LastObservedAt) return new MemoirValue(value, MemoirEvidence.Official);
            value += RecordedBetween(plateau.LastObservedAt, t);
            if (index + 1 < _plateaus.Count) value = Math.Min(value, _plateaus[index + 1].Minutes * 60L);
            return new MemoirValue(value, MemoirEvidence.Official);
        }

        var recorded = RecordedThrough(t);
        if (t >= BaseTime)
            return Untracked > 0 ? new MemoirValue(Untracked + recorded, MemoirEvidence.Inferred) : new MemoirValue(recorded, BaseEvidence == MemoirEvidence.Unknown ? MemoirEvidence.Unknown : MemoirEvidence.Recorded);

        // 史前
        // 官方说没有差额时史前就是 0；连官方时长都没有时只能假设之前没玩过
        if (Untracked == 0)
            return BaseEvidence switch
            {
                MemoirEvidence.Unknown => MemoirValue.Unknown(),
                MemoirEvidence.Recorded => new MemoirValue(recorded, MemoirEvidence.Inferred),
                _ => new MemoirValue(recorded, MemoirEvidence.Official)
            };
        if (PrehistoryStart is not { } start) return new MemoirValue(recorded, MemoirEvidence.Unknown, recorded + Untracked);
        if (t < start) return new MemoirValue(recorded, FirstPlayed != null ? MemoirEvidence.Official : MemoirEvidence.Inferred);
        var share = Weight(start, t) / Math.Max(1e-9, Weight(start, BaseTime));
        return new MemoirValue(recorded + (long)Math.Round(Untracked * Math.Clamp(share, 0, 1)), MemoirEvidence.Inferred, recorded + Untracked);
    }

    /// <summary>
    /// (from, to] 这段时间玩了多久。完全在分界之后时只看记录（或官方平台期），是证实的；
    /// 碰到史前时用两端累计时长相减，是推算。
    /// </summary>
    public MemoirValue Between(long from, long to)
    {
        if (to <= from) return MemoirValue.Zero(MemoirEvidence.Recorded);
        var end = At(to);
        var begin = At(from);
        var value = Math.Max(0, end.Value - begin.Value);
        if (end.Evidence == MemoirEvidence.Unknown || begin.Evidence == MemoirEvidence.Unknown)
            return new MemoirValue(value, MemoirEvidence.Unknown, Math.Max(value, (end.Upper ?? end.Value) - begin.Value));
        // 分界之后的推算只是基数，增量全是记录（或官方平台期）
        if (from >= BaseTime) return new MemoirValue(value, _plateaus.Count > 0 ? MemoirEvidence.Official : MemoirEvidence.Recorded);
        return new MemoirValue(value, MemoirEvidences.Weakest(end.Evidence, begin.Evidence));
    }

    /// <summary>[from, to] 内这款游戏有痕迹的日子。</summary>
    public int TraceDaysBetween(long from, long to)
        => UpperBound(_traceDays, to / 86400) - LowerBound(_traceDays, from / 86400);

    private double Weight(long from, long to)
    {
        if (to <= from) return 0;
        var days = (to - from) / 86400.0;
        return days * QuietDayWeight + TraceDaysBetween(from, to);
    }

    private int LastPlateauAtOrBefore(long t)
    {
        var result = -1;
        for (var index = 0; index < _plateaus.Count; index++)
        {
            if (_plateaus[index].FirstObservedAt <= t) result = index;
            else break;
        }
        return result;
    }

    private static int UpperBound(long[] values, long value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (values[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int LowerBound(long[] values, long value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

/// <summary>按账号和游戏建 <see cref="AppPlaytimeTimeline"/>。</summary>
public static class PlaytimeTimelines
{
    public static IReadOnlyList<AppPlaytimeTimeline> Build(MemoirArchive archive)
    {
        var sessions = archive.Insight.Sessions.ToLookup(session => (session.SteamId, session.AppId));
        var plateaus = archive.Official.ToLookup(item => (item.SteamId, item.AppId));
        var firstPlays = archive.FirstPlays.GroupBy(item => (item.SteamId, item.AppId)).ToDictionary(group => group.Key, group => group.Min(item => item.FirstPlayed));
        var traces = archive.Traces.Where(trace => trace.Kind != MemoirTraceKind.FirstPlay).ToLookup(trace => (trace.SteamId, trace.AppId));
        var owned = archive.Insight.Owned.ToDictionary(game => game.AppId);

        var keys = sessions.Select(group => group.Key)
            .Concat(plateaus.Select(group => group.Key))
            .Concat(firstPlays.Keys)
            .Distinct()
            .ToList();
        var accountsByApp = keys.GroupBy(key => key.AppId).ToDictionary(group => group.Key, group => group.Count());
        var timelines = new List<AppPlaytimeTimeline>(keys.Count + owned.Count);
        foreach (var key in keys)
        {
            // 游戏库快照只有合并后的官方时长，只有一个账号玩过时才能当作这个账号的
            long? officialNow = owned.TryGetValue(key.AppId, out var game) && accountsByApp[key.AppId] == 1 ? game.PlaytimeMinutes * 60L : null;
            timelines.Add(new AppPlaytimeTimeline(
                key.SteamId,
                key.AppId,
                sessions[key].Select(session => (session.Start, session.Seconds)),
                plateaus[key],
                firstPlays.TryGetValue(key, out var first) ? first : null,
                traces[key].Select(trace => trace.Time),
                officialNow,
                archive.Now));
        }
        var covered = keys.Select(key => key.AppId).ToHashSet();
        foreach (var game in owned.Values.Where(game => !covered.Contains(game.AppId) && game.PlaytimeMinutes > 0))
        {
            var appTraces = archive.Traces.Where(trace => trace.AppId == game.AppId && trace.Kind != MemoirTraceKind.FirstPlay).Select(trace => trace.Time);
            timelines.Add(new AppPlaytimeTimeline(string.Empty, game.AppId, [], [], null, appTraces, game.PlaytimeMinutes * 60L, archive.Now));
        }
        return timelines;
    }
}
