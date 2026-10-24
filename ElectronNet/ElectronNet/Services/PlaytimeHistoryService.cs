using ElectronNet.Features.History;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Platform;
using SteamStat.Core.Steam.Cache;

namespace ElectronNet.Services;

/// <summary>
/// 官方累计时长快照的采集、落库与对账
/// </summary>
public sealed class PlaytimeHistoryService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    SteamLibraryService libraryService,
    SteamFeatureSnapshotStore snapshotStore,
    TimeProvider timeProvider,
    ILogger<PlaytimeHistoryService> logger)
{
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>
    /// 执行一次快照采集（source: all | localconfig | cm）
    /// </summary>
    public async Task<PlaytimeSnapshotRunResultDto> RunSnapshotAsync(
        string source,
        bool refreshCm,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            var now = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var errors = new List<string>();
            var result = new SnapshotWriteResult();
            var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (source is "all" or "localconfig")
            {
                var localconfigResult = await CollectLocalConfigAsync(now, errors, cancellationToken);
                foreach (var observation in localconfigResult) accounts.Add(observation.SteamId);
                result += await ApplyObservationsAsync(localconfigResult, "localconfig", cancellationToken);
            }

            if (source is "all" or "cm")
            {
                var cmObservations = await CollectCmAsync(refreshCm, now, errors, cancellationToken);
                foreach (var observation in cmObservations) accounts.Add(observation.SteamId);
                result += await ApplyObservationsAsync(cmObservations, "cm", cancellationToken);
            }

            return new PlaytimeSnapshotRunResultDto
            {
                Inserted = result.Inserted,
                Updated = result.Updated,
                BaselinesCreated = result.BaselinesCreated,
                SkippedRunning = result.SkippedRunning,
                Stale = result.Stale,
                Accounts = accounts.Count,
                Errors = errors
            };
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<List<PlaytimeObservation>> CollectLocalConfigAsync(
        int now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var observations = new List<PlaytimeObservation>();
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                errors.Add("steamPathUnavailable");
                return observations;
            }
            var configs = LocalConfigPlaytimeReader.EnumerateUserConfigs(steamPath).ToList();
            if (configs.Count == 0) errors.Add("localConfigNotFound");
            foreach (var (steamId, path) in configs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var modifiedAt = File.GetLastWriteTimeUtc(path);
                    var observedAt = (int)Math.Min(now, new DateTimeOffset(modifiedAt).ToUnixTimeSeconds());
                    observations.AddRange(await Task.Run(
                        () => LocalConfigPlaytimeReader.Read(path, steamId, observedAt), cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to read localconfig.vdf at {Path}", path);
                    errors.Add($"localConfigParseFailed:{Path.GetFileName(path)}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to collect localconfig playtime");
            errors.Add("localConfigFailed");
        }
        return observations;
    }

    private async Task<List<PlaytimeObservation>> CollectCmAsync(
        bool refreshCm,
        int now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var observations = new List<PlaytimeObservation>();
        try
        {
            if (refreshCm)
            {
                try
                {
                    await libraryService.RefreshLibraryAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to refresh Steam library before CM snapshot");
                    errors.Add("cmRefreshFailed");
                }
            }

            var snapshots = await snapshotStore.GetLibrariesAsync(cancellationToken);
            if (snapshots.Count == 0)
            {
                errors.Add("cmNoSnapshot");
                return observations;
            }

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var users = await db.SteamUserTable.AsNoTracking()
                .Select(u => new { u.AccountName, u.SteamId })
                .ToListAsync(cancellationToken);
            var steamIdByAccount = users
                .GroupBy(u => u.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SteamId, StringComparer.OrdinalIgnoreCase);

            foreach (var snapshot in snapshots)
            {
                if (!steamIdByAccount.TryGetValue(snapshot.AccountName, out var steamId))
                {
                    errors.Add($"cmUnknownAccount:{snapshot.AccountName}");
                    continue;
                }
                var observedAt = (int)Math.Min(
                    now,
                    snapshot.LastSuccessfulUpdate.ToUnixTimeSeconds());
                foreach (var game in snapshot.Value)
                {
                    if (game.IsFamilyShared || game.PlaytimeForever <= 0) continue;
                    observations.Add(new PlaytimeObservation(
                        steamId,
                        game.AppId,
                        game.PlaytimeForever,
                        game.RtimeLastPlayed > 0 ? game.RtimeLastPlayed : null,
                        observedAt));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to collect CM playtime");
            errors.Add("cmFailed");
        }
        return observations;
    }

    /// <summary>
    /// 快照写入结果
    /// </summary>
    internal sealed record SnapshotWriteResult
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int BaselinesCreated { get; set; }
        public int SkippedRunning { get; set; }
        public int Stale { get; set; }
        public int Errors { get; set; }

        public static SnapshotWriteResult operator +(SnapshotWriteResult left, SnapshotWriteResult right)
            => new()
            {
                Inserted = left.Inserted + right.Inserted,
                Updated = left.Updated + right.Updated,
                BaselinesCreated = left.BaselinesCreated + right.BaselinesCreated,
                SkippedRunning = left.SkippedRunning + right.SkippedRunning,
                Stale = left.Stale + right.Stale,
                Errors = left.Errors + right.Errors
            };
    }

    /// <summary>
    /// 将一批观测写入 playtime_snapshot（值未变则更新 LastSeenAt，否则插入新行）
    /// </summary>
    internal async Task<SnapshotWriteResult> ApplyObservationsAsync(
        IEnumerable<PlaytimeObservation> observations,
        string source,
        CancellationToken cancellationToken = default)
    {
        var result = new SnapshotWriteResult();
        var groups = observations
            .GroupBy(o => (o.SteamId, o.AppId))
            .Select(g => (Key: g.Key, Observation: g.OrderByDescending(o => o.PlaytimeMinutes).First()))
            .ToList();
        if (groups.Count == 0) return result;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var openPairs = (await db.UseAppRecordTable
                .Where(r => r.EndTime == null)
                .Select(r => new { r.SteamId, r.AppId })
                .ToListAsync(cancellationToken))
            .Select(r => (r.SteamId, r.AppId))
            .ToHashSet();

        var existing = await db.PlaytimeSnapshotTable
            .Where(s => s.Source == source)
            .ToListAsync(cancellationToken);
        var latestByPair = existing
            .GroupBy(s => (s.SteamId, s.AppId))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.CapturedAt).ThenByDescending(s => s.Id).First());

        foreach (var (key, observation) in groups)
        {
            if (openPairs.Contains(key))
            {
                result.SkippedRunning++;
                continue;
            }
            try
            {
                if (latestByPair.TryGetValue(key, out var latest)
                    && observation.ObservedAt <= latest.LastSeenAt)
                {
                    // 观测时间不晚于已记录的最后观察时间，说明该值可能已不是最新，忽略
                    result.Stale++;
                    continue;
                }
                if (latest != null && latest.PlaytimeMinutes == observation.PlaytimeMinutes)
                {
                    latest.LastSeenAt = observation.ObservedAt;
                    latest.LastPlayed = observation.LastPlayed;
                    result.Updated++;
                }
                else
                {
                    var groupHasBaseline = existing.Any(s =>
                        s.SteamId == key.SteamId && s.AppId == key.AppId && s.IsBaseline);
                    var row = new PlaytimeSnapshot
                    {
                        SteamId = key.SteamId,
                        AppId = key.AppId,
                        Source = source,
                        PlaytimeMinutes = observation.PlaytimeMinutes,
                        CapturedAt = observation.ObservedAt,
                        LastSeenAt = observation.ObservedAt,
                        LastPlayed = observation.LastPlayed,
                        IsBaseline = !groupHasBaseline
                    };
                    db.PlaytimeSnapshotTable.Add(row);
                    result.Inserted++;
                    if (row.IsBaseline) result.BaselinesCreated++;
                    existing.Add(row);
                    latestByPair[key] = row;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to apply playtime snapshot for {SteamId}/{AppId}",
                    key.SteamId, key.AppId);
                result.Errors++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// 将指定范围的快照组基线重设为最新一条
    /// </summary>
    public async Task<int> ResetBaselineAsync(
        PlaytimeBaselineResetRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PlaytimeSnapshotTable
            .Where(s => request.SteamId == null || s.SteamId == request.SteamId)
            .Where(s => request.AppIds == null || request.AppIds.Count == 0 || request.AppIds.Contains(s.AppId))
            .ToListAsync(cancellationToken);

        var groups = rows.GroupBy(s => (s.SteamId, s.AppId, s.Source)).ToList();
        foreach (var group in groups)
        {
            var latest = group.OrderByDescending(s => s.CapturedAt).ThenByDescending(s => s.Id).First();
            foreach (var row in group) row.IsBaseline = row.Id == latest.Id;
        }
        await db.SaveChangesAsync(cancellationToken);
        return groups.Count;
    }

    /// <summary>
    /// 对每个（用户, 应用）选取最新来源并对账
    /// </summary>
    public PlaytimeReconcileResultDto GetReconciliation(PlaytimeReconcileQueryRequest request)
    {
        using var db = dbContextFactory.CreateDbContext();
        var snapshots = db.PlaytimeSnapshotTable.AsNoTracking()
            .Where(s => request.SteamIds == null || request.SteamIds.Count == 0 || request.SteamIds.Contains(s.SteamId))
            .ToList();

        var groups = snapshots
            .GroupBy(s => (s.SteamId, s.AppId))
            .ToList();

        var sessions = db.UseAppRecordTable.AsNoTracking()
            .Where(r => r.EndTime != null && r.Duration > 0)
            .Select(r => new { r.SteamId, r.AppId, r.StartTime, EndTime = r.EndTime!.Value, Duration = r.Duration!.Value })
            .ToList()
            .GroupBy(r => (r.SteamId, r.AppId))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LocalSessionSpan>)g
                    .Select(r => new LocalSessionSpan(r.StartTime, r.EndTime, r.Duration))
                    .ToList());

        var apps = db.SteamAppTable.AsNoTracking()
            .Select(a => new { a.AppId, a.Name, a.NameLocalizedJson })
            .ToList()
            .GroupBy(a => a.AppId)
            .ToDictionary(g => g.Key, g => g.First());
        var users = db.SteamUserTable.AsNoTracking()
            .Select(u => new { u.SteamId, u.PersonaName })
            .ToList()
            .GroupBy(u => u.SteamId)
            .ToDictionary(g => g.Key, g => g.First().PersonaName);

        var items = new List<PlaytimeReconcileItemDto>();
        foreach (var group in groups)
        {
            var bySource = group
                .GroupBy(s => s.Source)
                .Select(g => new
                {
                    Source = g.Key,
                    Rows = g.OrderBy(s => s.CapturedAt).ThenBy(s => s.Id).ToList(),
                    LatestSeen = g.Max(s => s.LastSeenAt)
                })
                .OrderByDescending(x => x.LatestSeen)
                .ThenBy(x => x.Source == "cm" ? 0 : 1)
                .First();

            var result = PlaytimeReconciler.Reconcile(
                bySource.Rows
                    .Select(s => new SnapshotPoint(s.PlaytimeMinutes, s.CapturedAt, s.LastSeenAt, s.IsBaseline))
                    .ToList(),
                sessions.TryGetValue(group.Key, out var spans) ? spans : []);

            apps.TryGetValue(group.Key.AppId, out var app);
            users.TryGetValue(group.Key.SteamId, out var personaName);

            items.Add(new PlaytimeReconcileItemDto
            {
                SteamId = group.Key.SteamId,
                UserPersonaName = personaName,
                AppId = group.Key.AppId,
                AppName = app?.Name,
                AppNameLocalized = app?.NameLocalizedJson,
                Source = bySource.Source,
                BaselineMinutes = result.BaselineMinutes,
                BaselineCapturedAt = result.BaselineCapturedAt,
                OfficialMinutes = result.OfficialMinutes,
                OfficialSeenAt = result.OfficialSeenAt,
                SnapshotCount = result.SnapshotCount,
                PreTrackingSeconds = result.PreTrackingSeconds,
                OfficialDeltaSeconds = result.OfficialDeltaSeconds,
                LocalSinceBaselineSeconds = result.LocalSinceBaselineSeconds,
                UnaccountedSeconds = result.UnaccountedSeconds,
                Status = result.Status,
                Intervals = result.Intervals.Select(i => new PlaytimeReconcileIntervalDto
                {
                    From = i.From,
                    To = i.To,
                    OfficialDeltaSeconds = i.OfficialDeltaSeconds,
                    LocalSeconds = i.LocalSeconds,
                    UnaccountedSeconds = i.UnaccountedSeconds
                }).ToList()
            });
        }

        return new PlaytimeReconcileResultDto
        {
            Items = items
                .OrderByDescending(i => Math.Abs(i.UnaccountedSeconds))
                .ThenBy(i => i.AppName ?? i.AppNameLocalized ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LastRunAt = snapshots.Count > 0 ? snapshots.Max(s => s.LastSeenAt) : null,
            LocalConfigAvailable = CheckLocalConfigAvailable(),
            CmAvailable = CheckCmAvailable()
        };
    }

    private bool CheckLocalConfigAvailable()
    {
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            return !string.IsNullOrWhiteSpace(steamPath)
                && LocalConfigPlaytimeReader.EnumerateUserConfigs(steamPath).Any();
        }
        catch
        {
            return false;
        }
    }

    private bool CheckCmAvailable()
    {
        try
        {
            return snapshotStore.GetLibrariesAsync().GetAwaiter().GetResult().Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
