namespace SteamStat.Core.Memoir;

public sealed record PrehistoryFirstPlay(int AppId, long FirstPlayed, bool BeforeTracking);

public sealed record PrehistoryUntracked(int AppId, long OfficialSeconds, long RecordedSeconds, long UntrackedSeconds, long? BaselineAt, MemoirEvidence Evidence);

public sealed record PrehistoryTraceDay(string Day, IReadOnlyList<int> AppIds, IReadOnlyList<MemoirTraceKind> Kinds, int Count);

public sealed record PrehistoryTraceCount(MemoirTraceKind Kind, int Total, int BeforeTracking);

public sealed record PrehistoryReport(
    long? TrackingStart,
    long? FirstEvidence,
    IReadOnlyList<PrehistoryFirstPlay> FirstPlays,
    IReadOnlyList<PrehistoryUntracked> Untracked,
    IReadOnlyList<PrehistoryTraceDay> Calendar,
    IReadOnlyList<PrehistoryTraceCount> TraceCounts);

/// <summary>
/// 史前考古：把安装 Steam Stat 之前的历史找回来。
/// 官方首玩和安装时的官方时长是证实的；痕迹（成就、截图、云存档）重建出的游玩日历全部是推算：
/// 有痕迹说明那天大概率玩过，但不知道玩了多久，没有痕迹也不代表没玩。
/// </summary>
public static class Prehistory
{
    private const long MinUntracked = 3600;

    public static PrehistoryReport Build(MemoirArchive archive, IReadOnlyList<AppPlaytimeTimeline> timelines)
    {
        var options = archive.Options;
        var tracking = archive.TrackingStart;
        bool Before(long time) => tracking == 0 || time < tracking;

        var firstPlays = archive.FirstPlays
            .GroupBy(item => item.AppId)
            .Select(group =>
            {
                var time = group.Min(item => item.FirstPlayed);
                return new PrehistoryFirstPlay(group.Key, time, Before(time));
            })
            .OrderBy(item => item.FirstPlayed)
            .ToList();

        var untracked = timelines
            .Where(timeline => timeline.Untracked >= MinUntracked)
            .GroupBy(timeline => timeline.AppId)
            .Select(group =>
            {
                var recorded = group.Sum(timeline => timeline.RecordedTotal);
                var gap = group.Sum(timeline => timeline.Untracked);
                var official = group.All(timeline => timeline.BaseEvidence == MemoirEvidence.Official);
                long? baseline = official ? group.Min(timeline => timeline.BaseTime) : null;
                return new PrehistoryUntracked(group.Key, recorded + gap, recorded, gap, baseline, official ? MemoirEvidence.Official : MemoirEvidence.Inferred);
            })
            .OrderByDescending(item => item.UntrackedSeconds)
            .ToList();

        var calendar = archive.Traces
            .Where(trace => Before(trace.Time))
            .GroupBy(trace => options.DayOf(trace.Time))
            .Select(group => new PrehistoryTraceDay(
                group.Key,
                group.Select(trace => trace.AppId).Distinct().ToList(),
                group.Select(trace => trace.Kind).Distinct().Order().ToList(),
                group.Count()))
            .OrderBy(day => day.Day, StringComparer.Ordinal)
            .ToList();

        var counts = Enum.GetValues<MemoirTraceKind>()
            .Select(kind => new PrehistoryTraceCount(
                kind,
                archive.Traces.Count(trace => trace.Kind == kind),
                archive.Traces.Count(trace => trace.Kind == kind && Before(trace.Time))))
            .ToList();

        return new PrehistoryReport(tracking > 0 ? tracking : null, archive.FirstEvidence(), firstPlays, untracked, calendar, counts);
    }
}
