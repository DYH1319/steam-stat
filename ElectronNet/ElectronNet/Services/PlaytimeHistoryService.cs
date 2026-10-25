using ElectronNet.Features.History;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;
using SteamStat.Core.Platform;
using SteamStat.Core.Steam.Cache;

namespace ElectronNet.Services;

/// <summary>
/// Steam 官方累计时长的快照（基线）与对账：
/// 定期读取 localconfig.vdf 与已缓存的 CM 游戏库，按值变化分段保存；对账时与本地会话比较，发现其他设备上的游玩。
/// </summary>
public sealed class PlaytimeHistoryService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    SteamFeatureSnapshotStore snapshotStore,
    IAppMetadataWriter appMetadataWriter,
    TimeProvider timeProvider,
    ILogger<PlaytimeHistoryService> logger) : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private int _disposed;

    /// <summary>
    /// 启动定时快照（启动后 30 秒首次执行，之后每小时一次）
    /// </summary>
    public void StartSchedule()
    {
        if (_timer != null || Volatile.Read(ref _disposed) != 0) return;
        _timer = timeProvider.CreateTimer(_ => _ = RunScheduledAsync(), null, InitialDelay, Interval);
    }

    private async Task RunScheduledAsync()
    {
        try
        {
            var result = await CaptureAsync();
            logger.LogInformation(
                "Scheduled playtime snapshot: {Observed} observed, {Inserted} inserted, {Updated} updated",
                result.Observed, result.Inserted, result.Updated);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Scheduled playtime snapshot failed");
        }
    }

    /// <summary>
    /// 立即采集一次官方累计时长快照
    /// </summary>
    public async Task<PlaytimeCaptureResultDto> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var observations = new List<PlaytimeObservation>();
            var sources = new List<string>();

            var localConfig = ReadLocalConfig(now);
            if (localConfig.Count > 0)
            {
                observations.AddRange(localConfig);
                sources.Add("localconfig");
            }

            var cm = await ReadCachedLibrariesAsync(cancellationToken);
            if (cm.Count > 0)
            {
                observations.AddRange(cm);
                sources.Add("cm");
            }

            if (observations.Count == 0)
                return new PlaytimeCaptureResultDto { Success = false, Observed = 0, Inserted = 0, Updated = 0, Sources = [], Error = "noSource" };

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var latest = (await db.PlaytimeSnapshotTable.ToListAsync(cancellationToken))
                .GroupBy(s => (s.SteamId, s.AppId))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(s => s.FirstObservedAt).ThenByDescending(s => s.PlaytimeMinutes).First());

            var inserted = PlaytimeSnapshotMerger.Merge(latest, observations);
            db.PlaytimeSnapshotTable.AddRange(inserted);
            var updated = db.ChangeTracker.Entries<PlaytimeSnapshot>().Count(entry => entry.State == EntityState.Modified);
            await db.SaveChangesAsync(cancellationToken);

            return new PlaytimeCaptureResultDto
            {
                Success = true,
                Observed = observations.Count,
                Inserted = inserted.Count,
                Updated = updated,
                Sources = sources
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to capture playtime snapshot");
            return new PlaytimeCaptureResultDto { Success = false, Observed = 0, Inserted = 0, Updated = 0, Sources = [], Error = "failed" };
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<PlaytimeObservation> ReadLocalConfig(int now)
    {
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            return string.IsNullOrWhiteSpace(steamPath) ? [] : LocalConfigPlaytimeReader.ReadAll(steamPath, now);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read playtime from localconfig.vdf");
            return [];
        }
    }

    private async Task<IReadOnlyList<PlaytimeObservation>> ReadCachedLibrariesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var libraries = await snapshotStore.GetLibrariesAsync(cancellationToken);
            if (libraries.Count == 0) return [];

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var steamIds = (await db.SteamUserTable.AsNoTracking()
                    .Select(user => new { user.AccountName, user.SteamId })
                    .ToListAsync(cancellationToken))
                .GroupBy(user => user.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SteamId, StringComparer.OrdinalIgnoreCase);

            var result = new List<PlaytimeObservation>();
            foreach (var library in libraries)
            {
                if (!steamIds.TryGetValue(library.AccountName, out var steamId)) continue;
                var observedAt = (int)library.LastSuccessfulUpdate.ToUnixTimeSeconds();
                result.AddRange(library.Value
                    .Where(game => game.AppId > 0 && game.PlaytimeForever > 0 && !game.IsFamilyShared)
                    .Select(game => new PlaytimeObservation(
                        steamId,
                        game.AppId,
                        game.PlaytimeForever,
                        game.RtimeLastPlayed > 0 ? game.RtimeLastPlayed : null,
                        "cm",
                        observedAt)));
                await appMetadataWriter.EnsureCachedAsync(
                    library.Value.Where(game => !string.IsNullOrEmpty(game.Name))
                        .Select(game => new AppMetadata((uint)game.AppId, game.Name)),
                    cancellationToken);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read playtime from cached Steam libraries");
            return [];
        }
    }

    /// <summary>
    /// 基线与对账概览
    /// </summary>
    public PlaytimeHistoryOverviewDto GetOverview(PlaytimeHistoryQueryRequest request)
    {
        try
        {
            var reconciliations = Reconcile(request, out var snapshotCount, out var firstCapturedAt, out var lastCapturedAt);

            using var db = dbContextFactory.CreateDbContext();
            var appNames = db.SteamAppTable.AsNoTracking()
                .Select(app => new { app.AppId, app.Name })
                .ToList()
                .GroupBy(app => app.AppId)
                .ToDictionary(g => g.Key, g => g.First().Name);
            var personaNames = db.SteamUserTable.AsNoTracking()
                .Select(user => new { user.SteamId, user.PersonaName })
                .ToList()
                .GroupBy(user => user.SteamId)
                .ToDictionary(g => g.Key, g => g.First().PersonaName);

            var games = reconciliations
                .Select(x => new PlaytimeReconciliationDto
                {
                    SteamId = x.Result.SteamId,
                    AppId = x.Result.AppId,
                    AppName = appNames.GetValueOrDefault(x.Result.AppId),
                    UserPersonaName = personaNames.GetValueOrDefault(x.Result.SteamId),
                    OfficialMinutes = x.Result.OfficialMinutes,
                    BaselineSeconds = x.Result.BaselineSeconds,
                    BaselineAt = x.Result.BaselineAt,
                    LastObservedAt = x.Result.LastObservedAt,
                    LastPlayedAt = x.Result.LastPlayedAt,
                    OfficialDeltaSeconds = x.Result.OfficialDeltaSeconds,
                    LocalTrackedSeconds = x.Result.LocalTrackedSeconds,
                    LocalTotalSeconds = x.Result.LocalTotalSeconds,
                    UntrackedSeconds = x.Result.UntrackedSeconds,
                    Status = x.Result.Status,
                    SnapshotCount = x.SnapshotCount,
                    Source = x.Source
                })
                .OrderByDescending(dto => dto.OfficialMinutes)
                .ToList();

            var discrepancies = reconciliations
                .SelectMany(x => x.Result.Discrepancies.Select(d => new PlaytimeDiscrepancyDto
                {
                    SteamId = x.Result.SteamId,
                    AppId = x.Result.AppId,
                    AppName = appNames.GetValueOrDefault(x.Result.AppId),
                    From = d.From,
                    To = d.To,
                    OfficialSeconds = d.OfficialSeconds,
                    LocalSeconds = d.LocalSeconds
                }))
                .OrderByDescending(d => d.To)
                .Take(500)
                .ToList();

            return new PlaytimeHistoryOverviewDto
            {
                Games = games,
                Discrepancies = discrepancies,
                SnapshotCount = snapshotCount,
                FirstCapturedAt = firstCapturedAt,
                LastCapturedAt = lastCapturedAt
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build playtime history overview");
            return new PlaytimeHistoryOverviewDto { Games = [], Discrepancies = [], SnapshotCount = 0 };
        }
    }

    /// <summary>
    /// 各 (SteamId, AppId) 的基线，供统计数据集使用
    /// </summary>
    public IReadOnlyList<StatsBaselineDto> GetBaselines(IReadOnlyList<string>? steamIds)
    {
        try
        {
            return Reconcile(new PlaytimeHistoryQueryRequest { SteamIds = steamIds }, out _, out _, out _)
                .Select(x => new StatsBaselineDto
                {
                    SteamId = x.Result.SteamId,
                    AppId = x.Result.AppId,
                    BaselineSeconds = x.Result.BaselineSeconds,
                    BaselineAt = x.Result.BaselineAt,
                    OfficialMinutes = x.Result.OfficialMinutes,
                    UntrackedSeconds = x.Result.UntrackedSeconds
                })
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to compute playtime baselines");
            return [];
        }
    }

    private List<(PlaytimeReconciliation Result, int SnapshotCount, string Source)> Reconcile(
        PlaytimeHistoryQueryRequest request,
        out int snapshotCount,
        out int? firstCapturedAt,
        out int? lastCapturedAt)
    {
        using var db = dbContextFactory.CreateDbContext();
        var steamIds = request.SteamIds is { Count: > 0 } ? request.SteamIds.ToHashSet() : null;
        var appIds = request.AppIds is { Count: > 0 } ? request.AppIds.ToHashSet() : null;

        var snapshots = db.PlaytimeSnapshotTable.AsNoTracking().ToList()
            .Where(s => (steamIds == null || steamIds.Contains(s.SteamId)) && (appIds == null || appIds.Contains(s.AppId)))
            .ToList();
        snapshotCount = snapshots.Count;
        firstCapturedAt = snapshots.Count == 0 ? null : snapshots.Min(s => s.FirstObservedAt);
        lastCapturedAt = snapshots.Count == 0 ? null : snapshots.Max(s => s.LastObservedAt);
        if (snapshots.Count == 0) return [];

        var sessions = db.UseAppRecordTable.AsNoTracking()
            .Where(r => r.EndTime != null && r.Duration > 0)
            .Select(r => new { r.SteamId, r.AppId, r.StartTime, EndTime = r.EndTime!.Value, Duration = r.Duration!.Value })
            .ToList()
            .GroupBy(r => (r.SteamId, r.AppId))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LocalSessionSpan>)g.Select(r => new LocalSessionSpan(r.StartTime, r.EndTime, r.Duration)).ToList());

        return snapshots
            .GroupBy(s => (s.SteamId, s.AppId))
            .Select(g =>
            {
                var list = g.ToList();
                var result = PlaytimeReconciler.Reconcile(list, sessions.GetValueOrDefault(g.Key) ?? []);
                var source = list.OrderByDescending(s => s.FirstObservedAt).First().Source;
                return (result, list.Count, source);
            })
            .ToList();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _gate.Dispose();
    }
}
