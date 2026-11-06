using System.Globalization;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Friends;

namespace ElectronNet.Features.Reliability;

internal readonly record struct PlaytimeGap(int From, int To, long UntrackedSeconds);

/// <summary>体检各项的判定规则（纯函数）。文案在渲染层，这里只给状态和数字。</summary>
internal static class ArchiveHealthRules
{
    internal const int SnapshotStaleDays = 7;
    internal const int BackupWarningDays = 30;
    internal const int BackupErrorDays = 180;
    internal const int OfficialStaleDays = 14;
    internal const double RecoveredWarningShare = 0.1;
    internal const double PollCaughtWarningShare = 0.5;
    internal const int MinimumRichPresenceChanges = 20;
    /// <summary>未记录时长低于这个值不报告：Steam 的时长按分钟取整，会话之间总有零头。</summary>
    internal const long MinimumGapSeconds = 30 * 60;

    private const long Day = 86400;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static ArchiveHealthCheckDto Check(string id, string status, string? route = null, params (string Key, string Value)[] values) => new()
    {
        Id = id,
        Status = status,
        ActionRoute = route,
        Values = values.ToDictionary(item => item.Key, item => item.Value)
    };

    internal static ArchiveHealthCheckDto Integrity(bool healthy, long sizeBytes)
        => Check("integrity", healthy ? "ok" : "error", healthy ? null : "/data-health#snapshots", ("sizeBytes", Text(sizeBytes)));

    internal static ArchiveHealthCheckDto Snapshots(IReadOnlyList<DatabaseSnapshotDto> snapshots, long now)
    {
        var startup = snapshots.Where(snapshot => snapshot.Kind == "startup").ToList();
        if (startup.Count == 0) return Check("snapshots", "warning", "/data-health#snapshots", ("count", "0"));
        var newest = startup.Max(snapshot => snapshot.CreatedAt);
        var days = Math.Max(0, (now - newest) / Day);
        return Check("snapshots", days > SnapshotStaleDays ? "warning" : "ok", "/data-health#snapshots",
            ("count", Text(startup.Count)), ("days", Text(days)));
    }

    /// <summary>手动备份和多设备同步都算：它们都让数据离开这块硬盘。启动快照不算。</summary>
    internal static ArchiveHealthCheckDto Backup(long? lastBackupAt, long? lastSyncAt, long now)
    {
        var latest = new[] { lastBackupAt, lastSyncAt }.Max();
        if (latest == null) return Check("backup", "warning", "/data-transfer");
        var days = Math.Max(0, (now - latest.Value) / Day);
        var status = days >= BackupErrorDays ? "error" : days >= BackupWarningDays ? "warning" : "ok";
        return Check("backup", status, status == "ok" ? null : "/data-transfer", ("days", Text(days)));
    }

    /// <summary>
    /// 记录断档：官方时长涨了但本地没有会话的区间，按逻辑日合并相邻或重叠的区间。
    /// 这个区间只能精确到两次观测之间，实际游玩可能只占其中一部分。
    /// </summary>
    internal static IReadOnlyList<ArchiveHealthGapDto> MergeGaps(IEnumerable<PlaytimeGap> gaps, int boundaryHour, TimeZoneInfo timeZone)
    {
        var merged = new List<(DateOnly From, DateOnly To, long Seconds)>();
        foreach (var gap in gaps.Where(item => item.UntrackedSeconds > 0).OrderBy(item => item.From))
        {
            var from = LogicalDay(gap.From, boundaryHour, timeZone);
            var to = LogicalDay(Math.Max(gap.From, gap.To), boundaryHour, timeZone);
            if (merged.Count > 0 && from <= merged[^1].To.AddDays(1))
            {
                var last = merged[^1];
                merged[^1] = (last.From, to > last.To ? to : last.To, last.Seconds + gap.UntrackedSeconds);
            }
            else
            {
                merged.Add((from, to, gap.UntrackedSeconds));
            }
        }
        return merged
            .Where(item => item.Seconds >= MinimumGapSeconds)
            .OrderByDescending(item => item.To)
            .Select(item => new ArchiveHealthGapDto
            {
                FromDay = item.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ToDay = item.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Days = item.To.DayNumber - item.From.DayNumber + 1,
                UntrackedSeconds = item.Seconds
            })
            .ToList();
    }

    private static DateOnly LogicalDay(int timestamp, int boundaryHour, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp - boundaryHour * 3600L), timeZone).DateTime);

    internal static ArchiveHealthCheckDto Gaps(IReadOnlyList<ArchiveHealthGapDto> gaps)
        => Check("gaps", gaps.Count > 0 ? "warning" : "ok", gaps.Count > 0 ? "/history" : null,
            ("count", Text(gaps.Count)), ("seconds", Text(gaps.Sum(gap => gap.UntrackedSeconds))));

    internal static ArchiveHealthCheckDto Official(int games, int untracked, int overcounted, int? lastCapturedAt, long now)
    {
        if (games == 0 || lastCapturedAt == null) return Check("official", "unknown", "/history");
        var days = Math.Max(0, (now - lastCapturedAt.Value) / Day);
        var status = overcounted > 0 || days > OfficialStaleDays ? "warning" : "ok";
        return Check("official", status, status == "ok" ? null : "/history",
            ("games", Text(games)), ("untracked", Text(untracked)), ("overcounted", Text(overcounted)), ("days", Text(days)));
    }

    internal static ArchiveHealthCheckDto Recovered(long total, long recovered)
    {
        var share = total == 0 ? 0 : (double)recovered / total;
        return Check("recovered", share >= RecoveredWarningShare ? "warning" : "ok", null,
            ("total", Text(total)), ("recovered", Text(recovered)));
    }

    internal static ArchiveHealthCheckDto RichPresence(IReadOnlyList<RichPresenceCoverage> coverage)
    {
        if (coverage.Count == 0) return Check("richPresence", "unknown", "/data-source/cm");
        var changes = coverage.Sum(item => item.RecordedChanges);
        var caught = coverage.Sum(item => item.ChangesCaughtByPoll);
        var status = changes >= MinimumRichPresenceChanges && (double)caught / changes >= PollCaughtWarningShare ? "warning" : "ok";
        return Check("richPresence", status, null,
            ("changes", Text(changes)), ("caught", Text(caught)),
            ("pushes", Text(coverage.Sum(item => item.PushUpdates))), ("polls", Text(coverage.Sum(item => item.PollResponses))));
    }

    /// <summary>
    /// 成就口径对账：Steam 概览的总数和这里按成就集算出的总数差多少。
    /// 概览的 total 是否按玩家计算还没用真实账号确认，这个比例就是判断依据，不代表哪边错了。
    /// </summary>
    internal static ArchiveHealthCheckDto AchievementScope(long games, long different, long pending)
    {
        if (games == 0 && pending == 0) return Check("achievementScope", "unknown", "/achievements");
        return Check("achievementScope", pending > 0 ? "warning" : "ok", pending > 0 ? "/achievements" : null,
            ("games", Text(games)), ("different", Text(different)), ("pending", Text(pending)));
    }

    internal static ArchiveHealthCheckDto Seals(ArchiveSealOverviewDto overview)
    {
        var damaged = overview.Seals.Count(seal => !seal.Intact);
        var status = damaged > 0 ? "error" : overview.AvailableYears.Count > 0 ? "warning" : "ok";
        return Check("seals", status, status == "ok" ? null : "/data-health#seals",
            ("sealed", Text(overview.Seals.Count)), ("damaged", Text(damaged)), ("available", Text(overview.AvailableYears.Count)));
    }
}
