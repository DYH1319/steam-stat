using ElectronNet.Hosting;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Sessions;

namespace ElectronNet.Services;

/// <summary>
/// 成就档案：把每次拉取到的单游戏成就解锁明细永久写入 SQLite（只增不删），
/// 并提供按账号批量增量同步（只拉取解锁数与归档不一致的游戏）与目标清单。
/// </summary>
internal sealed class AchievementArchiveService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamAchievementsService achievementsService,
    ISteamAccountIdentityProvider identityProvider,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<AchievementArchiveService> logger) : IDisposable
{
    private const int DefaultPageSize = 500;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _syncLock = new();
    private AchievementArchiveSyncStatusDto _status = new() { State = "idle", Total = 0, Done = 0, Failed = 0, Added = 0 };
    private CancellationTokenSource? _syncCancellation;
    private Task _publishChain = Task.CompletedTask;

    /// <summary>
    /// 透传成就详情结果，顺带把已解锁成就写入档案（失败只记日志，不影响调用方）。
    /// </summary>
    public async Task<SteamAchievementGameResult> CaptureAsync(
        string accountName,
        SteamAchievementGameResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RecordAsync(accountName, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to archive achievements of app {AppId}", result.AppId);
        }
        return result;
    }

    private async Task<int> RecordAsync(
        string accountName,
        SteamAchievementGameResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess || !result.ProgressState.HasValue) return 0;
        if (!identityProvider.TryGetSteamId(accountName, out var steamIdValue)) return 0;
        var steamId = steamIdValue.ToString();
        var appId = (long)result.AppId;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var unlocked = result.Achievements
            .Where(entry => entry.IsUnlocked == true && entry.UnlockTimeUtc is { } time && time.ToUnixTimeSeconds() > 0)
            .ToArray();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.AchievementUnlockArchiveTable
                .Where(row => row.AccountName == accountName && row.AppId == appId)
                .ToDictionaryAsync(row => row.InternalName, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);
            var added = 0;
            foreach (var entry in unlocked)
            {
                var definition = entry.Definition;
                var unlockTime = entry.UnlockTimeUtc!.Value.ToUnixTimeSeconds();
                if (!existing.TryGetValue(definition.InternalName, out var row))
                {
                    row = new AchievementUnlockArchive
                    {
                        AccountName = accountName,
                        AppId = appId,
                        InternalName = definition.InternalName,
                        RecordedAt = now
                    };
                    db.AchievementUnlockArchiveTable.Add(row);
                    existing[definition.InternalName] = row;
                    added++;
                }
                row.SteamId = steamId;
                row.Name = Truncate(definition.LocalizedName, 512);
                row.Description = Truncate(definition.LocalizedDescription, 2048);
                row.Icon = Truncate(definition.Icon, 1024);
                row.Hidden = definition.Hidden;
                row.GlobalPercent = definition.GlobalUnlockedPercent ?? row.GlobalPercent;
                row.UnlockTime = unlockTime;
            }

            var game = await db.AchievementGameArchiveTable
                .FirstOrDefaultAsync(row => row.AccountName == accountName && row.AppId == appId, cancellationToken)
                .ConfigureAwait(false);
            if (game == null)
            {
                game = new AchievementGameArchive { AccountName = accountName, AppId = appId };
                db.AchievementGameArchiveTable.Add(game);
            }
            game.SteamId = steamId;
            game.AppName = Truncate(string.IsNullOrEmpty(result.AppName) ? $"App {result.AppId}" : result.AppName, 512);
            game.Total = result.Summary.Total;
            game.Unlocked = result.Summary.Unlocked;
            game.SyncedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return added;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public AchievementArchivePageDto GetUnlocks(AchievementArchiveQueryRequest request)
    {
        var limit = request.Limit ?? DefaultPageSize;
        using var db = dbContextFactory.CreateDbContext();
        var query = db.AchievementUnlockArchiveTable.AsNoTracking().Where(row => row.AccountName == request.AccountName);
        if (request.BeforeId.HasValue) query = query.Where(row => row.Id < request.BeforeId.Value);
        var items = query.OrderByDescending(row => row.Id).Take(limit).ToList();
        return new AchievementArchivePageDto
        {
            Items = items.Select(row => new AchievementArchiveUnlockDto
            {
                Id = row.Id,
                AppId = (uint)row.AppId,
                InternalName = row.InternalName,
                Name = row.Name,
                Description = row.Description,
                Icon = row.Icon,
                Hidden = row.Hidden,
                GlobalPercent = row.GlobalPercent,
                UnlockTime = row.UnlockTime
            }).ToArray(),
            NextBeforeId = items.Count == limit ? items[^1].Id : null
        };
    }

    public IReadOnlyList<AchievementArchiveGameDto> GetGames(string accountName)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.AchievementGameArchiveTable.AsNoTracking()
            .Where(row => row.AccountName == accountName)
            .OrderBy(row => row.AppId)
            .AsEnumerable()
            .Select(row => new AchievementArchiveGameDto
            {
                AppId = (uint)row.AppId,
                AppName = row.AppName,
                SteamId = row.SteamId,
                Total = row.Total,
                Unlocked = row.Unlocked,
                SyncedAt = row.SyncedAt
            })
            .ToArray();
    }

    #region 批量同步

    public AchievementArchiveSyncStatusDto GetSyncStatus()
    {
        lock (_syncLock) return _status;
    }

    public bool StartSync(string accountName, bool force)
    {
        CancellationToken token;
        lock (_syncLock)
        {
            if (_status.State == "running") return false;
            _syncCancellation?.Dispose();
            _syncCancellation = new CancellationTokenSource();
            token = _syncCancellation.Token;
            _status = new AchievementArchiveSyncStatusDto
            {
                State = "running",
                AccountName = accountName,
                Total = 0,
                Done = 0,
                Failed = 0,
                Added = 0,
                StartedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
        }
        Publish();
        _ = Task.Run(() => RunSyncAsync(accountName, force, token), CancellationToken.None);
        return true;
    }

    public void CancelSync()
    {
        lock (_syncLock) _syncCancellation?.Cancel();
    }

    private async Task RunSyncAsync(string accountName, bool force, CancellationToken cancellationToken)
    {
        try
        {
            var overview = await achievementsService.GetOverviewAsync(accountName, cancellationToken).ConfigureAwait(false);
            if (!overview.ProgressState.HasValue)
            {
                Finish("failed", overview.ProgressState.Failure?.ToString() ?? "overview_unavailable");
                return;
            }
            Dictionary<long, (int Total, int Unlocked)> archived;
            await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                archived = await db.AchievementGameArchiveTable.AsNoTracking()
                    .Where(row => row.AccountName == accountName)
                    .ToDictionaryAsync(row => row.AppId, row => (row.Total, row.Unlocked), cancellationToken)
                    .ConfigureAwait(false);
            }
            // 增量：只拉取总数 / 解锁数与档案不一致（或从未归档）的游戏
            var pending = overview.Games
                .Where(game => game.Progress is { Total: > 0 } progress
                    && (force
                        || !archived.TryGetValue(game.AppId, out var known)
                        || known.Total != progress.Total
                        || known.Unlocked != progress.Unlocked))
                .OrderByDescending(game => game.LastPlayedAt)
                .ToArray();
            Update(status => status with { Total = pending.Length });

            foreach (var game in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!identityProvider.TryGetSteamId(accountName, out _))
                {
                    Finish("failed", "authenticationRequired");
                    return;
                }
                var name = string.IsNullOrEmpty(game.LocalizedName) ? game.Name : game.LocalizedName;
                Update(status => status with { CurrentAppId = game.AppId, CurrentAppName = name });
                var added = 0;
                var failed = false;
                try
                {
                    var result = force
                        ? await achievementsService.RefreshGameAsync(accountName, game.AppId, cancellationToken).ConfigureAwait(false)
                        : await achievementsService.GetGameAsync(accountName, game.AppId, cancellationToken).ConfigureAwait(false);
                    // 缓存里的解锁数落后于概览时强制刷新
                    if (!force && (!result.IsSuccess || !result.ProgressState.HasValue
                        || result.Summary.Unlocked != game.Progress!.Unlocked
                        || result.Summary.Total != game.Progress.Total))
                        result = await achievementsService.RefreshGameAsync(accountName, game.AppId, cancellationToken)
                            .ConfigureAwait(false);
                    if (result.IsSuccess && result.ProgressState.HasValue)
                        added = await RecordAsync(accountName, result, cancellationToken).ConfigureAwait(false);
                    else
                        failed = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to sync achievements of app {AppId}", game.AppId);
                    failed = true;
                }
                Update(status => status with
                {
                    Done = status.Done + 1,
                    Failed = status.Failed + (failed ? 1 : 0),
                    Added = status.Added + added
                });
            }
            Finish("completed", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish("cancelled", null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Achievement archive sync failed for {AccountName}", accountName);
            Finish("failed", exception.GetType().Name);
        }
    }

    private void Update(Func<AchievementArchiveSyncStatusDto, AchievementArchiveSyncStatusDto> change)
    {
        lock (_syncLock) _status = change(_status);
        Publish();
    }

    private void Finish(string state, string? error)
        => Update(status => status with
        {
            State = state,
            Error = error,
            CurrentAppId = null,
            CurrentAppName = null,
            FinishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
        });

    // 串行转发，保证渲染层按顺序收到进度（不会出现 completed 之后又收到 running）
    private void Publish()
    {
        lock (_syncLock)
        {
            var snapshot = _status;
            _publishChain = _publishChain
                .ContinueWith(_ => eventBus.PublishAsync(new AchievementArchiveSyncChanged(snapshot)), TaskScheduler.Default)
                .Unwrap();
        }
    }

    #endregion

    #region 目标清单

    public IReadOnlyList<AchievementTargetDto> GetTargets(string accountName)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.AchievementTargetTable.AsNoTracking()
            .Where(row => row.AccountName == accountName)
            .OrderByDescending(row => row.CreatedAt)
            .AsEnumerable()
            .Select(ToDto)
            .ToArray();
    }

    public async Task<AchievementTargetDto> AddTargetAsync(
        AchievementTargetAddRequest request,
        CancellationToken cancellationToken = default)
    {
        var appId = (long)request.AppId;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AchievementTargetTable.FirstOrDefaultAsync(
            item => item.AccountName == request.AccountName && item.AppId == appId && item.InternalName == request.InternalName,
            cancellationToken).ConfigureAwait(false);
        if (row == null)
        {
            row = new AchievementTarget
            {
                AccountName = request.AccountName,
                AppId = appId,
                InternalName = request.InternalName,
                CreatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
            db.AchievementTargetTable.Add(row);
        }
        row.AppName = request.AppName;
        row.Name = request.Name;
        row.Description = request.Description;
        row.Icon = request.Icon;
        row.Note = string.IsNullOrWhiteSpace(request.Note) ? row.Note : request.Note;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    public async Task<bool> RemoveTargetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.AchievementTargetTable.Where(row => row.Id == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static AchievementTargetDto ToDto(AchievementTarget row) => new()
    {
        Id = row.Id,
        AppId = (uint)row.AppId,
        AppName = row.AppName,
        InternalName = row.InternalName,
        Name = row.Name,
        Description = row.Description,
        Icon = row.Icon,
        Note = row.Note,
        CreatedAt = row.CreatedAt
    };

    #endregion

    private static string Truncate(string? value, int length)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= length ? value : value[..length];

    public void Dispose()
    {
        lock (_syncLock)
        {
            _syncCancellation?.Cancel();
            _syncCancellation?.Dispose();
            _syncCancellation = null;
        }
        _writeLock.Dispose();
    }
}
