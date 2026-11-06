namespace SteamStat.Core.Archives.Mining;

/// <summary>一条解锁记录。FirstPercent 是第一次看到这个成就时的全球解锁率。</summary>
public sealed record PioneerUnlock(
    int AppId,
    string InternalName,
    long UnlockTime,
    double? FirstPercent,
    long? FirstPercentAt,
    double? CurrentPercent);

public sealed record PioneerEntry(int AppId, string InternalName, long UnlockTime, double FirstPercent, double CurrentPercent, double Lead);

/// <summary>
/// Score 0~100，越高说明解锁时拿到的人比现在少得越多。
/// Fresh 是解锁后不久就观测到的记录，Matured 里的已经过了足够时间，稀有度的变化才有意义。
/// Late 是装应用之前解锁的，当时的稀有度已经无从知晓。
/// </summary>
public sealed record PioneerReport(double? Score, int Fresh, int Matured, int Late, IReadOnlyList<PioneerEntry> Top);

/// <summary>
/// 先行者指数：Steam 只给当前的全球解锁率，不记历史，所以「你解锁时有多少人拿到」只有当时存下来才有。
/// 这是时间敏感数据，存的是第一次观测值，只有解锁后 7 天内观测到的才算数。
/// </summary>
public static class PioneerIndex
{
    public const int FreshWindowDays = 7;
    public const int MatureDays = 30;
    public const int MinSamples = 5;
    private const int TopCount = 20;

    public static PioneerReport Compute(IEnumerable<PioneerUnlock> unlocks, long now)
    {
        ArgumentNullException.ThrowIfNull(unlocks);
        var fresh = 0;
        var late = 0;
        var entries = new List<PioneerEntry>();
        foreach (var unlock in unlocks)
        {
            if (unlock.FirstPercent is not { } first || unlock.FirstPercentAt is not { } firstAt) continue;
            if (firstAt - unlock.UnlockTime > FreshWindowDays * 86400L)
            {
                late++;
                continue;
            }
            fresh++;
            if (now - firstAt < MatureDays * 86400L || unlock.CurrentPercent is not { } current) continue;
            // 解锁率 0 表示当时几乎没人，按 0.01% 算，避免除零
            var floor = Math.Max(first, 0.01);
            var ceiling = Math.Max(current, floor);
            entries.Add(new PioneerEntry(unlock.AppId, unlock.InternalName, unlock.UnlockTime, first, current, 1 - floor / ceiling));
        }

        return new PioneerReport(
            entries.Count >= MinSamples ? Math.Round(entries.Average(entry => entry.Lead) * 100, 1) : null,
            fresh,
            entries.Count,
            late,
            entries.OrderByDescending(entry => entry.Lead).ThenBy(entry => entry.FirstPercent).Take(TopCount).ToArray());
    }
}
