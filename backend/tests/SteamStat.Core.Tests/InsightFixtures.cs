using SteamStat.Core.Insights;

namespace SteamStat.Core.Tests;

/// <summary>造档案的小工具。时区固定 UTC、分界点 0，这样日期和时刻一眼能对上。</summary>
internal static class InsightFixtures
{
    internal const string Me = "76561198000000001";
    internal static readonly long Now = Unix("2026-02-02", 12);
    internal static readonly InsightOptions Options = new(DayBoundaryHour: 0, WeekStart: 1, PauseDays: 30, AbandonDays: 90, Zone: TimeZoneInfo.Utc);

    internal static long Unix(string day, double hour = 0)
        => new DateTimeOffset(InsightDays.Parse(day).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds() + (long)(hour * 3600);

    internal static InsightSession Session(int appId, string day, double hour, long seconds, long idle = 0, SessionTags tags = SessionTags.None, string? name = null, long id = 0)
    {
        var start = Unix(day, hour);
        return new InsightSession(id == 0 ? start + appId : id, Me, appId, name ?? $"Game {appId}", start, start + seconds, seconds, idle, day, tags, null);
    }

    internal static InsightArchive Archive(IEnumerable<InsightSession>? sessions = null, long? now = null, InsightOptions? options = null)
        => new()
        {
            Now = now ?? Now,
            Options = options ?? Options,
            Sessions = (sessions ?? []).OrderBy(session => session.Start).ToList()
        };

    /// <summary>从 InsightArchive 复制一份，只改列出的字段。</summary>
    internal static InsightArchive With(
        this InsightArchive archive,
        IReadOnlyList<InsightOwnedGame>? owned = null,
        IReadOnlyList<InsightGameStatus>? statuses = null,
        IReadOnlyList<InsightAcquisition>? acquisitions = null,
        IReadOnlyList<InsightUnlock>? unlocks = null,
        IReadOnlyList<InsightProgress>? progress = null,
        IReadOnlyList<InsightBuildChange>? builds = null,
        IReadOnlyList<InsightDlc>? dlcs = null,
        IReadOnlyList<CrashBurst>? crashes = null,
        IReadOnlyDictionary<int, InsightAppFacts>? apps = null,
        InsightFriendData? friends = null,
        InsightReconciliation? reconciliation = null)
        => new()
        {
            Now = archive.Now,
            Options = archive.Options,
            Sessions = archive.Sessions,
            Unlocks = unlocks ?? archive.Unlocks,
            Progress = progress ?? archive.Progress,
            SetCompletions = archive.SetCompletions,
            Dlcs = dlcs ?? archive.Dlcs,
            Owned = owned ?? archive.Owned,
            Statuses = statuses ?? archive.Statuses,
            Acquisitions = acquisitions ?? archive.Acquisitions,
            Builds = builds ?? archive.Builds,
            SchemaChanges = archive.SchemaChanges,
            Crashes = crashes ?? archive.Crashes,
            Apps = apps ?? archive.Apps,
            Friends = friends ?? archive.Friends,
            Reconciliation = reconciliation ?? archive.Reconciliation
        };

    internal static IEnumerable<string> Days(string from, int count, int step = 1)
        => Enumerable.Range(0, count).Select(index => InsightDays.Shift(from, index * step));
}
