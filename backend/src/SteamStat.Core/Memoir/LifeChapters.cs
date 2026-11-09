namespace SteamStat.Core.Memoir;

/// <summary>一个月玩了什么。安装之后是记录，之前优先用年度回顾（官方），没有时由痕迹推算。</summary>
public sealed record MemoirMonth(string Month, long Seconds, MemoirEvidence Evidence, IReadOnlyDictionary<int, long> Games);

/// <summary>多年历史的逐月序列，人生章节和静态站都用它。</summary>
public static class MemoirMonths
{
    public static IReadOnlyList<MemoirMonth> Build(MemoirArchive archive, IReadOnlyList<AppPlaytimeTimeline> timelines)
    {
        var options = archive.Options;
        if (archive.FirstEvidence() is not { } first) return [];
        var fromMonth = MemoirDays.Month(options.LogicalDate(first));
        var toMonth = MemoirDays.Month(options.LogicalDate(archive.Now));
        if (string.CompareOrdinal(fromMonth, toMonth) > 0) return [];
        var trackingStart = archive.TrackingStart;
        var trackingMonth = trackingStart > 0 ? MemoirDays.Month(options.LogicalDate(trackingStart)) : null;

        var recorded = archive.Insight.Sessions
            .GroupBy(session => MemoirDays.Month(session.Day))
            .ToDictionary(group => group.Key, group => group.GroupBy(session => session.AppId).ToDictionary(apps => apps.Key, apps => apps.Sum(session => session.Seconds)));
        var reviews = archive.YearReviews.ToLookup(review => review.Year);

        var result = new List<MemoirMonth>();
        foreach (var month in MemoirDays.MonthsBetween(fromMonth, toMonth))
        {
            var games = new Dictionary<int, long>();
            long seconds = 0;
            var evidence = MemoirEvidence.Recorded;
            var anyKnown = false;

            if (recorded.TryGetValue(month, out var played))
            {
                foreach (var (appId, value) in played) games[appId] = games.GetValueOrDefault(appId) + value;
                seconds += played.Values.Sum();
                anyKnown = true;
            }

            var beforeTracking = trackingMonth == null || string.CompareOrdinal(month, trackingMonth) <= 0;
            if (beforeTracking)
            {
                var start = MemoirDays.MonthStart(month);
                var from = MemoirDays.StartOf(options, start);
                var to = Math.Min(MemoirDays.StartOf(options, start.AddMonths(1)), trackingStart > 0 ? trackingStart : archive.Now);
                var official = trackingMonth != month ? Official(reviews[start.Year], start.Month) : null;
                if (official is { } yir)
                {
                    foreach (var (appId, value) in yir.Games) games[appId] = games.GetValueOrDefault(appId) + value;
                    seconds += yir.Seconds;
                    evidence = MemoirEvidences.Weakest(evidence, MemoirEvidence.Official);
                    anyKnown = true;
                }
                else if (to > from)
                {
                    var pre = MemoirEvidence.Recorded;
                    var unknown = 0;
                    foreach (var timeline in timelines)
                    {
                        var value = timeline.Between(from, to);
                        if (value.Evidence == MemoirEvidence.Unknown)
                        {
                            unknown++;
                            continue;
                        }
                        if (value.Value <= 0) continue;
                        games[timeline.AppId] = games.GetValueOrDefault(timeline.AppId) + value.Value;
                        seconds += value.Value;
                        pre = MemoirEvidences.Weakest(pre, value.Evidence);
                        anyKnown = true;
                    }
                    evidence = MemoirEvidences.Weakest(evidence, pre);
                    if (!anyKnown && unknown > 0) evidence = MemoirEvidence.Unknown;
                    else if (!anyKnown) evidence = MemoirEvidence.Official;
                }
            }
            result.Add(new MemoirMonth(month, seconds, evidence, games));
        }
        // 开头没有时长的月份不算：最早的证据可能只是一次入库或一张截图
        var firstPlayed = result.FindIndex(month => month.Seconds > 0);
        return firstPlayed < 0 ? [] : result.Skip(firstPlayed).ToList();
    }

    private static (long Seconds, Dictionary<int, long> Games)? Official(IEnumerable<YearReview> reviews, int month)
    {
        long seconds = 0;
        var games = new Dictionary<int, long>();
        var found = false;
        foreach (var review in reviews)
        {
            var entry = review.Months.FirstOrDefault(item => item.Month == month);
            if (entry?.Seconds is not { } total) continue;
            found = true;
            seconds += total;
            foreach (var game in entry.Games)
                if (review.SecondsOf(month, game.AppId) is { } value and > 0)
                    games[game.AppId] = games.GetValueOrDefault(game.AppId) + value;
        }
        return found ? (seconds, games) : null;
    }
}

public sealed record LifeChapterGame(int AppId, long Seconds, double Share);

public sealed record LifeChapter(
    int Index,
    string FromMonth,
    string ToMonth,
    int Months,
    string Kind,
    IReadOnlyList<int> AppIds,
    long Seconds,
    long MonthlySeconds,
    string PeakMonth,
    int NewGames,
    int Unlocks,
    double? NightShare,
    IReadOnlyList<LifeChapterGame> TopGames,
    MemoirEvidence Evidence,
    int? StartEventId)
{
    public bool Contains(string month) => string.CompareOrdinal(month, FromMonth) >= 0 && string.CompareOrdinal(month, ToMonth) <= 0;
}

/// <summary>
/// 人生章节：把逐月序列切成几段，每段里玩的东西和玩的多少都差不多。
/// <para>
/// 最优分段（动态规划）：段内代价是「每月玩什么」的加权方差加上「每月玩多久」（取对数）的方差，
/// 每多一段加一个固定罚分，所以只有变化足够大才会切开。人生事件设为分界点时，那个月一定是新一章的开头。
/// 结果只由数据决定，同样的档案每次切出来都一样。
/// </para>
/// </summary>
public static class LifeChapters
{
    internal const double SegmentPenalty = 3.0;
    internal const double ShortPenalty = 4.0;
    internal const int MinMonths = 3;
    internal const double LevelWeight = 0.5;
    private const int Dimensions = 24;
    /// <summary>每月十小时以上，「玩什么」才算数：一个月只开了一次的游戏不该决定章节。</summary>
    private const double FullWeightHours = 10;

    public static IReadOnlyList<LifeChapter> Build(MemoirArchive archive, IReadOnlyList<MemoirMonth> months)
    {
        if (months.Count == 0) return [];
        var events = archive.LifeEvents.Where(item => item.Boundary).ToList();
        var forced = new Dictionary<int, int>();
        for (var index = 1; index < months.Count; index++)
        {
            var match = events.FirstOrDefault(item => MemoirDays.Month(MemoirDays.FromEpoch(item.StartDay)) == months[index].Month);
            if (match != null) forced[index] = match.Id;
        }
        var firstEvent = events.FirstOrDefault(item => MemoirDays.Month(MemoirDays.FromEpoch(item.StartDay)) == months[0].Month);

        var segments = Segment(months, forced.Keys.ToHashSet());
        var firstPlays = FirstPlayMonths(archive);
        var options = archive.Options;
        var chapters = new List<LifeChapter>(segments.Count);
        foreach (var (from, to) in segments)
        {
            var slice = months.Skip(from).Take(to - from + 1).ToList();
            var fromMonth = slice[0].Month;
            var toMonth = slice[^1].Month;
            var seconds = slice.Sum(month => month.Seconds);
            var games = slice.SelectMany(month => month.Games)
                .GroupBy(entry => entry.Key)
                .Select(group => (AppId: group.Key, Seconds: group.Sum(entry => entry.Value)))
                .Where(entry => entry.Seconds > 0)
                .OrderByDescending(entry => entry.Seconds)
                .ToList();
            var top = games.Take(5).Select(entry => new LifeChapterGame(entry.AppId, entry.Seconds, seconds > 0 ? (double)entry.Seconds / seconds : 0)).ToList();
            var newGames = firstPlays.Count(entry => string.CompareOrdinal(entry.Value, fromMonth) >= 0 && string.CompareOrdinal(entry.Value, toMonth) <= 0);
            var unlocks = archive.Insight.Unlocks.Count(unlock => unlock.Day.Length >= 7 && string.CompareOrdinal(MemoirDays.Month(unlock.Day), fromMonth) >= 0 && string.CompareOrdinal(MemoirDays.Month(unlock.Day), toMonth) <= 0);
            var sessions = archive.Insight.Sessions.Where(session => string.CompareOrdinal(MemoirDays.Month(session.Day), fromMonth) >= 0 && string.CompareOrdinal(MemoirDays.Month(session.Day), toMonth) <= 0).ToList();
            double? night = sessions.Count >= 10 ? (double)sessions.Count(session => options.Local(session.Start).Hour < 5) / sessions.Count : null;
            var (kind, leads) = Classify(seconds, slice.Count, top, newGames);
            var inferred = slice.Where(month => month.Evidence >= MemoirEvidence.Inferred).Sum(month => month.Seconds);
            var evidence = seconds > 0 && inferred * 2 > seconds
                ? MemoirEvidence.Inferred
                : slice.Any(month => month.Evidence == MemoirEvidence.Official && month.Seconds > 0) ? MemoirEvidence.Official : MemoirEvidence.Recorded;
            int? startEvent = from == 0 ? firstEvent?.Id : forced.TryGetValue(from, out var eventId) ? eventId : null;
            chapters.Add(new LifeChapter(
                chapters.Count,
                fromMonth,
                toMonth,
                slice.Count,
                kind,
                leads,
                seconds,
                seconds / slice.Count,
                slice.MaxBy(month => month.Seconds)!.Month,
                newGames,
                unlocks,
                night,
                top,
                evidence,
                startEvent));
        }
        return chapters;
    }

    public static LifeChapter? At(IReadOnlyList<LifeChapter> chapters, string month) => chapters.FirstOrDefault(chapter => chapter.Contains(month));

    /// <summary>每款游戏第一次玩是哪个月：官方首玩优先，没有时用第一局。</summary>
    private static Dictionary<int, string> FirstPlayMonths(MemoirArchive archive)
    {
        var options = archive.Options;
        var result = new Dictionary<int, long>();
        foreach (var item in archive.FirstPlays) result[item.AppId] = Math.Min(result.GetValueOrDefault(item.AppId, long.MaxValue), item.FirstPlayed);
        foreach (var session in archive.Insight.Sessions) result[session.AppId] = Math.Min(result.GetValueOrDefault(session.AppId, long.MaxValue), session.Start);
        return result.ToDictionary(entry => entry.Key, entry => MemoirDays.Month(options.LogicalDate(entry.Value)));
    }

    internal static (string Kind, IReadOnlyList<int> Leads) Classify(long seconds, int months, IReadOnlyList<LifeChapterGame> top, int newGames)
    {
        var monthlyHours = months == 0 ? 0 : seconds / 3600.0 / months;
        if (monthlyHours < 2 || top.Count == 0) return ("quiet", top.Take(1).Select(game => game.AppId).ToList());
        var first = top[0].Share;
        var second = top.Count > 1 ? top[1].Share : 0;
        if (first >= 0.5) return ("era", [top[0].AppId]);
        if (first + second >= 0.65 && second >= 0.2) return ("duo", [top[0].AppId, top[1].AppId]);
        if (newGames >= Math.Max(6, months) && first < 0.35) return ("sampler", top.Take(3).Select(game => game.AppId).ToList());
        if (first < 0.25) return ("variety", top.Take(3).Select(game => game.AppId).ToList());
        return ("mostly", [top[0].AppId]);
    }

    /// <summary>返回每段的首尾下标（闭区间）。</summary>
    internal static IReadOnlyList<(int From, int To)> Segment(IReadOnlyList<MemoirMonth> months, IReadOnlySet<int> forcedStarts)
    {
        var n = months.Count;
        if (n == 0) return [];
        var top = months.SelectMany(month => month.Games)
            .GroupBy(entry => entry.Key)
            .OrderByDescending(group => group.Sum(entry => entry.Value))
            .Take(Dimensions)
            .Select((group, index) => (group.Key, index))
            .ToDictionary(item => item.Key, item => item.index);
        var dims = top.Count + 1;

        // 前缀和：权重、加权的占比向量、加权的占比平方和、对数时长和它的平方
        var weight = new double[n + 1];
        var shareSum = new double[n + 1, dims];
        var squareSum = new double[n + 1];
        var level = new double[n + 1];
        var levelSquare = new double[n + 1];
        for (var index = 0; index < n; index++)
        {
            var month = months[index];
            var hours = month.Seconds / 3600.0;
            var w = Math.Min(1, hours / FullWeightHours);
            var share = new double[dims];
            if (month.Seconds > 0)
            {
                var known = 0L;
                foreach (var (appId, value) in month.Games)
                {
                    var slot = top.TryGetValue(appId, out var position) ? position : dims - 1;
                    share[slot] += (double)value / month.Seconds;
                    known += value;
                }
                if (known < month.Seconds) share[dims - 1] += (double)(month.Seconds - known) / month.Seconds;
            }
            weight[index + 1] = weight[index] + w;
            var squares = 0.0;
            for (var d = 0; d < dims; d++)
            {
                shareSum[index + 1, d] = shareSum[index, d] + w * share[d];
                squares += share[d] * share[d];
            }
            squareSum[index + 1] = squareSum[index] + w * squares;
            var log = Math.Log(1 + hours);
            level[index + 1] = level[index] + log;
            levelSquare[index + 1] = levelSquare[index] + log * log;
        }

        double Cost(int from, int to)
        {
            var count = to - from + 1;
            var w = weight[to + 1] - weight[from];
            var shareCost = 0.0;
            if (w > 1e-9)
            {
                var norm = 0.0;
                for (var d = 0; d < dims; d++)
                {
                    var sum = shareSum[to + 1, d] - shareSum[from, d];
                    norm += sum * sum;
                }
                shareCost = Math.Max(0, squareSum[to + 1] - squareSum[from] - norm / w);
            }
            var levelTotal = level[to + 1] - level[from];
            var levelCost = Math.Max(0, levelSquare[to + 1] - levelSquare[from] - levelTotal * levelTotal / count);
            return shareCost + LevelWeight * levelCost + SegmentPenalty + (count < MinMonths ? ShortPenalty : 0);
        }

        var best = new double[n + 1];
        var previous = new int[n + 1];
        for (var end = 1; end <= n; end++)
        {
            best[end] = double.MaxValue;
            for (var start = end - 1; start >= 0; start--)
            {
                // 分界点不能落在段的中间
                var crosses = false;
                for (var check = start + 1; check < end; check++)
                {
                    if (!forcedStarts.Contains(check)) continue;
                    crosses = true;
                    break;
                }
                if (crosses) break;
                var candidate = best[start] + Cost(start, end - 1);
                if (candidate < best[end])
                {
                    best[end] = candidate;
                    previous[end] = start;
                }
            }
        }
        var segments = new List<(int, int)>();
        for (var end = n; end > 0; end = previous[end]) segments.Add((previous[end], end - 1));
        segments.Reverse();
        return segments;
    }
}
