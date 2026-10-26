using SteamStat.Contracts.Ipc;

namespace ElectronNet.Features.UsageTracking;

public sealed record SessionCleaningRules(
    int MinDurationSeconds,
    int MergeGapSeconds,
    IReadOnlySet<int> ExcludedAppIds);

/// <summary>
/// 会话清洗规则（查询期应用，不修改原始数据）：
/// 剔除被排除的应用 → 按 gap 合并相邻会话 → 过滤过短会话。
/// </summary>
public static class SessionCleaner
{
    public static List<UseAppRecordDto> Apply(IEnumerable<UseAppRecordDto> records, SessionCleaningRules rules)
    {
        var list = records
            .Where(record => !rules.ExcludedAppIds.Contains(record.AppId))
            .ToList();

        if (rules.MergeGapSeconds > 0)
        {
            list = MergeByGap(list, rules.MergeGapSeconds);
        }

        if (rules.MinDurationSeconds > 0)
        {
            list = list.Where(record => record.Duration >= rules.MinDurationSeconds).ToList();
        }

        return list.OrderBy(record => record.StartTime).ToList();
    }

    private static List<UseAppRecordDto> MergeByGap(List<UseAppRecordDto> records, int gapSeconds)
    {
        var merged = new List<UseAppRecordDto>();
        foreach (var group in records.GroupBy(record => (record.SteamId, record.AppId)))
        {
            UseAppRecordDto? current = null;
            foreach (var next in group.OrderBy(record => record.StartTime))
            {
                if (current == null)
                {
                    current = next;
                    continue;
                }

                if (next.StartTime - current.EndTime <= gapSeconds)
                {
                    current = new UseAppRecordDto
                    {
                        AppId = current.AppId,
                        SteamId = current.SteamId,
                        StartTime = current.StartTime,
                        EndTime = Math.Max(current.EndTime, next.EndTime),
                        Duration = current.Duration + next.Duration,
                        IdleDuration = current.IdleDuration + next.IdleDuration,
                        ForegroundDuration = current.ForegroundDuration == null || next.ForegroundDuration == null
                            ? null
                            : current.ForegroundDuration + next.ForegroundDuration,
                        AppName = current.AppName,
                        AppNameLocalized = current.AppNameLocalized,
                        UserPersonaName = current.UserPersonaName
                    };
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            if (current != null) merged.Add(current);
        }
        return merged;
    }
}
