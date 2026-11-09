using System.Globalization;
using System.Text.Json;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services.Collectors;

/// <summary>
/// 全球在线人数。公共数据仓的历史分支覆盖热门游戏，这里只补它不覆盖的冷门游戏：
/// 本机装着的、最近玩过的优先。只发应用 ID，不带账号信息，关闭公共数据层时不访问。
/// </summary>
internal sealed class PlayerCountCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    IPublicDataSwitch publicData,
    PlayerHistoryService history,
    IInsightCatalog catalog,
    ISettingsStore settingsStore,
    TimeProvider timeProvider) : IArchiveCollector
{
    private const int MaxPerPass = 300;
    /// <summary>最近这么多天里连续拿不到数据的游戏，暂时不再查。</summary>
    private const int NoDataDays = 30;
    private const int NoDataTries = 3;

    internal TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(1);

    public string Id => "players";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        if (!publicData.Enabled) return CollectorOutcome.Skipped;
        var covered = await history.GetCoverageAsync(cancellationToken).ConfigureAwait(false);
        var due = await DueAppIdsAsync(covered, cancellationToken).ConfigureAwait(false);
        var sampled = 0;
        var failures = 0;
        foreach (var appId in due)
        {
            var count = await gateway.GetPlayerCountAsync(appId, cancellationToken).ConfigureAwait(false);
            if (count == null)
            {
                // 连续失败说明网络有问题，这一轮不用再试了
                if (++failures >= 5) return sampled > 0 ? CollectorOutcome.Ok(sampled) : CollectorOutcome.Failed(CollectorErrors.Network);
                continue;
            }
            failures = 0;
            await RecordAsync(appId, count.Value, cancellationToken).ConfigureAwait(false);
            sampled++;
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return CollectorOutcome.Ok(sampled);
    }

    private static string Today(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal async Task<IReadOnlyList<int>> DueAppIdsAsync(IReadOnlySet<int> covered, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var today = Today(now);
        var since = Today(now.AddDays(-NoDataDays));
        var hidden = (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var recent = await db.AppPlayerCountTable.AsNoTracking()
            .Where(row => string.Compare(row.Day, since) >= 0)
            .Select(row => new { row.AppId, row.Day, row.Samples })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var sampledToday = recent.Where(row => row.Day == today).Select(row => row.AppId).ToHashSet();
        var noData = recent.GroupBy(row => row.AppId)
            .Where(group => group.All(row => row.Samples == 0) && group.Count() >= NoDataTries)
            .Select(group => group.Key)
            .ToHashSet();

        var installed = await db.SteamAppTable.AsNoTracking().Where(app => app.Installed).Select(app => app.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = now.AddDays(-180).ToUnixTimeSeconds();
        var played = await db.UseAppRecordTable.AsNoTracking().Where(record => record.StartTime >= cutoff).OrderByDescending(record => record.StartTime).Select(record => record.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var owned = (await catalog.GetOwnedGamesAsync(InsightScope.All, cancellationToken).ConfigureAwait(false))
            .Where(game => game.PlaytimeMinutes > 0)
            .OrderByDescending(game => game.LastPlayed)
            .Select(game => game.AppId);
        return played.Concat(installed).Concat(owned)
            .Where(appId => appId > 0 && !hidden.Contains(appId) && !covered.Contains(appId) && !sampledToday.Contains(appId) && !noData.Contains(appId))
            .Distinct()
            .Take(MaxPerPass)
            .ToArray();
    }

    internal async Task RecordAsync(int appId, int count, CancellationToken cancellationToken = default)
    {
        var day = Today(timeProvider.GetUtcNow());
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AppPlayerCountTable.FirstOrDefaultAsync(item => item.AppId == appId && item.Day == day, cancellationToken).ConfigureAwait(false);
        if (row == null)
        {
            row = new AppPlayerCount { AppId = appId, Day = day };
            db.AppPlayerCountTable.Add(row);
            if (count >= 0)
            {
                row.Peak = count;
                row.Low = count;
                row.Samples = 1;
            }
        }
        else if (count >= 0)
        {
            row.Peak = Math.Max(row.Peak, count);
            row.Low = row.Samples == 0 ? count : Math.Min(row.Low, count);
            row.Samples++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 疑似关服。Steam 没有这个字段，只看纯联机游戏：官方公告里的停服字样，或者人数连续两周为 0。
/// 这一项每款游戏要多发一个请求，所以每周一次、每次限量。
/// </summary>
internal sealed class ServerShutdownCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    IPublicDataSwitch publicData,
    PlayerHistoryService history,
    TimeProvider timeProvider) : IArchiveCollector
{
    private const int MaxPerPass = 100;

    internal TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(2);

    public string Id => "serverShutdown";

    public TimeSpan Interval => TimeSpan.FromDays(7);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        if (!publicData.Enabled) return CollectorOutcome.Skipped;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var flagged = (await db.AppArtifactEventTable.AsNoTracking().Where(row => row.Kind == ArtifactEventKinds.ServerShutdown).Select(row => row.AppId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var candidates = (await db.AppStoreStatusTable.AsNoTracking().Where(status => status.CategoryIds != null).Select(status => new { status.AppId, status.CategoryIds }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(status => (status.AppId, Categories: ParseIds(status.CategoryIds)))
            .Where(item => !flagged.Contains(item.AppId) && ServerShutdownDetector.IsOnlineOnly(item.Categories))
            .Take(MaxPerPass)
            .ToArray();

        var found = 0;
        foreach (var (appId, categories) in candidates)
        {
            var news = await gateway.GetNewsAsync(appId, cancellationToken).ConfigureAwait(false);
            if (news == null) return CollectorOutcome.Failed(CollectorErrors.Network);
            var curve = await history.GetCurveAsync(appId, 3, cancellationToken).ConfigureAwait(false);
            var players = curve.Select(point => new DailyPlayers(point.Day, point.Players)).ToArray();
            var evidence = ServerShutdownDetector.Evaluate(categories, news, players);
            if (evidence != null)
            {
                db.AppArtifactEventTable.Add(new AppArtifactEvent
                {
                    AppId = appId,
                    Kind = ArtifactEventKinds.ServerShutdown,
                    ObservedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                    Before = evidence.Source,
                    After = evidence.Detail
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                found++;
            }
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return CollectorOutcome.Ok(found);
    }

    internal static IReadOnlyCollection<int> ParseIds(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<int[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
