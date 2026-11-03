using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Features.Archive;
using ElectronNet.Helpers;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 存档备份：定位存档目录，按内容变化产生只读快照并保留版本历史。
/// 只读取存档目录（共享只读打开），版本内容写入应用自己的备份库；
/// 「导出」只会写到备份库下的新文件夹，绝不写回原存档目录。
/// </summary>
internal sealed class SaveBackupService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ISteamInstallLocator installLocator,
    ISettingsStore settingsStore,
    IMainWindowAccessor mainWindowAccessor,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<SaveBackupService> logger) : IEventHandler<UsageSessionEnded>, IDisposable
{
    private const int MaxFilesPerTarget = 20_000;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SessionEndDelay = TimeSpan.FromSeconds(30);

    private readonly SaveBackupObjectStore _store = new(Path.Combine(appPaths.UserDataDirectory, "SaveBackups"));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _statusLock = new();
    private readonly Dictionary<int, ITimer> _sessionTimers = [];
    private SaveBackupStatusDto _status = new() { State = "idle", Total = 0, Done = 0, Failed = 0, Created = 0 };
    private CancellationTokenSource? _runCancellation;
    private Task _publishChain = Task.CompletedTask;
    private ITimer? _timer;

    #region 调度

    /// <summary>启动后延迟首次执行，之后每 30 分钟快照到期（超过间隔未扫描）的已启用目录；总开关默认关闭。</summary>
    public void StartSchedule()
    {
        if (_timer != null) return;
        _timer = timeProvider.CreateTimer(_ => _ = RunScheduledAsync(), null, InitialDelay, TickInterval);
    }

    private async Task RunScheduledAsync()
    {
        try
        {
            var settings = settingsStore.GetSettings().Archive;
            if (settings?.SaveBackupEnabled != true) return;
            var interval = Math.Clamp(settings?.SaveBackupIntervalHours ?? 6, 1, 168) * 3600L;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            List<int> due;
            await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
            {
                due = await db.SaveBackupTargetTable.AsNoTracking()
                    .Where(row => row.Enabled && (row.LastScanAt == null || row.LastScanAt <= now - interval))
                    .Select(row => row.Id)
                    .ToListAsync().ConfigureAwait(false);
            }
            if (due.Count > 0) StartRun(due, "schedule");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Scheduled save backup failed");
        }
    }

    /// <summary>游戏会话结束 30 秒后快照该游戏的存档（等待游戏与 Steam 云完成写入）。</summary>
    public Task HandleAsync(UsageSessionEnded message, CancellationToken cancellationToken)
    {
        var settings = settingsStore.GetSettings().Archive;
        if (settings?.SaveBackupEnabled != true || settings.SaveBackupOnSessionEnd == false || message.AppId <= 0)
            return Task.CompletedTask;
        lock (_statusLock)
        {
            if (_sessionTimers.Remove(message.AppId, out var previous)) previous.Dispose();
            _sessionTimers[message.AppId] = timeProvider.CreateTimer(
                _ => _ = RunAfterSessionAsync(message.AppId), null, SessionEndDelay, Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    private async Task RunAfterSessionAsync(int appId)
    {
        lock (_statusLock)
        {
            if (_sessionTimers.Remove(appId, out var timer)) timer.Dispose();
        }
        try
        {
            List<int> ids;
            await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
            {
                ids = await db.SaveBackupTargetTable.AsNoTracking()
                    .Where(row => row.Enabled && row.AppId == appId)
                    .Select(row => row.Id)
                    .ToListAsync().ConfigureAwait(false);
            }
            if (ids.Count > 0 && !StartRun(ids, "sessionEnd"))
            {
                // 正在执行其他快照任务：下一轮定时任务会覆盖这些目录
                logger.LogInformation("Save backup after session end of app {AppId} deferred: another run is active", appId);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Save backup after session end of app {AppId} failed", appId);
        }
    }

    #endregion

    #region 目录管理

    public IReadOnlyList<SaveBackupTargetDto> GetTargets()
    {
        using var db = dbContextFactory.CreateDbContext();
        var targets = db.SaveBackupTargetTable.AsNoTracking().OrderBy(row => row.AppId).ThenBy(row => row.Id).ToList();
        var snapshots = db.SaveBackupSnapshotTable.AsNoTracking()
            .Select(row => new { row.Id, row.TargetId, row.CreatedAt, row.TotalSize, row.FileCount })
            .ToList();
        var latest = snapshots.GroupBy(row => row.TargetId)
            .ToDictionary(group => group.Key, group => (Count: group.Count(), Latest: group.MaxBy(row => row.CreatedAt)!));
        var stored = (from file in db.SaveBackupFileTable.AsNoTracking()
                      join snapshot in db.SaveBackupSnapshotTable.AsNoTracking() on file.SnapshotId equals snapshot.Id
                      select new { snapshot.TargetId, file.Sha256, file.Size })
            .Distinct()
            .AsEnumerable()
            .GroupBy(row => row.TargetId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Size));
        var names = AppNameHelper.LookupNames(db, targets.Select(row => row.AppId));
        return targets.Select(row =>
        {
            latest.TryGetValue(row.Id, out var info);
            return new SaveBackupTargetDto
            {
                Id = row.Id,
                AppId = (int)row.AppId,
                AppName = names.GetValueOrDefault(row.AppId, string.Empty),
                Kind = row.Kind,
                Path = row.Path,
                SteamId = row.SteamId,
                Label = row.Label,
                Enabled = row.Enabled,
                Exists = Directory.Exists(row.Path),
                CreatedAt = row.CreatedAt,
                LastScanAt = row.LastScanAt,
                LastSnapshotAt = row.LastSnapshotAt,
                LastError = row.LastError,
                SnapshotCount = info.Count,
                LatestSize = info.Latest?.TotalSize ?? 0,
                LatestFileCount = info.Latest?.FileCount ?? 0,
                StoredSize = stored.GetValueOrDefault(row.Id)
            };
        }).ToArray();
    }

    /// <summary>
    /// 发现可能的存档目录（Steam 云存档 + Auto-Cloud 推断），只作为候选返回，由用户确认后加入，从不自动添加。
    /// </summary>
    public async Task<SaveBackupDiscoverResultDto> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var steamPath = installLocator.ReadSteamRegistry().SteamPath;
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            return new SaveBackupDiscoverResultDto { Suggestions = [] };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installDirs = (await db.SteamAppTable.AsNoTracking()
                .Where(app => app.InstallDirPath != null)
                .Select(app => new { app.AppId, app.InstallDirPath })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(app => (long)app.AppId)
            .ToDictionary(group => group.Key, group => group.First().InstallDirPath);
        var candidates = await Task.Run(
            () => SaveLocationDiscovery.Discover(steamPath, appId => installDirs.GetValueOrDefault(appId)),
            cancellationToken).ConfigureAwait(false);
        var known = (await db.SaveBackupTargetTable.AsNoTracking().Select(row => row.Path).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(NormalizeKey)
            .ToHashSet();
        var suggestions = candidates.Where(candidate => !known.Contains(NormalizeKey(candidate.Path))).ToList();
        var names = AppNameHelper.LookupNames(db, suggestions.Select(item => item.AppId));
        return new SaveBackupDiscoverResultDto
        {
            Suggestions = suggestions
                .DistinctBy(item => NormalizeKey(item.Path))
                .Select(item => new SaveBackupSuggestionDto
                {
                    AppId = (int)item.AppId,
                    AppName = names.GetValueOrDefault(item.AppId, string.Empty),
                    Kind = item.Kind,
                    Path = item.Path,
                    SteamId = item.SteamId,
                    FileCount = item.FileCount
                })
                .ToArray()
        };
    }

    public async Task<SaveBackupTargetAddResultDto> AddTargetAsync(SaveBackupTargetAddRequest request)
    {
        string path;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(request.Path))
                return new SaveBackupTargetAddResultDto { Success = false, Error = "notFound" };
            path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(request.Path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SaveBackupTargetAddResultDto { Success = false, Error = "notFound" };
        }
        if (File.Exists(path)) return new SaveBackupTargetAddResultDto { Success = false, Error = "notDirectory" };
        if (!Directory.Exists(path)) return new SaveBackupTargetAddResultDto { Success = false, Error = "notFound" };
        if (IsForbidden(path)) return new SaveBackupTargetAddResultDto { Success = false, Error = "forbidden" };
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var key = NormalizeKey(path);
            var paths = await db.SaveBackupTargetTable.AsNoTracking().Select(row => row.Path).ToListAsync().ConfigureAwait(false);
            if (paths.Any(item => NormalizeKey(item) == key))
                return new SaveBackupTargetAddResultDto { Success = false, Error = "duplicate" };
            var row = new SaveBackupTarget
            {
                AppId = request.AppId,
                Kind = request.Kind ?? "custom",
                Path = path,
                SteamId = string.IsNullOrWhiteSpace(request.SteamId) ? null : request.SteamId,
                Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
                Enabled = true,
                CreatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
            db.SaveBackupTargetTable.Add(row);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return new SaveBackupTargetAddResultDto
            {
                Success = true,
                Target = GetTargets().FirstOrDefault(item => item.Id == row.Id)
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to add a save backup target");
            return new SaveBackupTargetAddResultDto { Success = false, Error = "failed" };
        }
    }

    public async Task<string?> PickDirectoryAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        try
        {
            var result = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
            {
                Properties = [OpenDialogProperty.openDirectory]
            }).ConfigureAwait(false);
            return result is { Length: > 0 } ? result[0] : null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the directory picker");
            return null;
        }
    }

    public async Task<bool> UpdateTargetAsync(SaveBackupTargetUpdateRequest request)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var row = await db.SaveBackupTargetTable.FirstOrDefaultAsync(item => item.Id == request.Id).ConfigureAwait(false);
        if (row == null) return false;
        if (request.Enabled.HasValue) row.Enabled = request.Enabled.Value;
        if (request.Label != null) row.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 移除目录。默认保留历史版本（目录被禁用并保留，档案永久保留）；DeleteSnapshots 时连同版本一起删除。
    /// </summary>
    public async Task<bool> RemoveTargetAsync(SaveBackupTargetRemoveRequest request)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var row = await db.SaveBackupTargetTable.FirstOrDefaultAsync(item => item.Id == request.Id).ConfigureAwait(false);
            if (row == null) return false;
            var snapshotIds = await db.SaveBackupSnapshotTable.Where(item => item.TargetId == row.Id)
                .Select(item => item.Id).ToListAsync().ConfigureAwait(false);
            if (snapshotIds.Count > 0 && request.DeleteSnapshots != true)
            {
                row.Enabled = false;
                await db.SaveChangesAsync().ConfigureAwait(false);
                return true;
            }
            await DeleteSnapshotsAsync(db, snapshotIds).ConfigureAwait(false);
            db.SaveBackupTargetTable.Remove(row);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>在资源管理器中打开存档目录（路径由 Host 按 Id 解析）。</summary>
    public async Task<bool> OpenTargetAsync(int targetId)
    {
        string? path;
        await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            path = await db.SaveBackupTargetTable.AsNoTracking().Where(row => row.Id == targetId)
                .Select(row => row.Path).FirstOrDefaultAsync().ConfigureAwait(false);
        }
        if (path == null || !Directory.Exists(path)) return false;
        try
        {
            await Electron.Shell.OpenPathAsync(path).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open a save backup target");
            return false;
        }
    }

    #endregion

    #region 快照任务

    public SaveBackupStatusDto GetStatus()
    {
        lock (_statusLock) return _status;
    }

    public async Task<bool> RunAsync(SaveBackupRunRequest request)
    {
        List<int> ids;
        await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            var query = db.SaveBackupTargetTable.AsNoTracking();
            ids = request.TargetIds is { Count: > 0 } requested
                ? await query.Where(row => requested.Contains(row.Id)).Select(row => row.Id).ToListAsync().ConfigureAwait(false)
                : await query.Where(row => row.Enabled).Select(row => row.Id).ToListAsync().ConfigureAwait(false);
        }
        return ids.Count > 0 && StartRun(ids, "manual");
    }

    public void Cancel()
    {
        lock (_statusLock) _runCancellation?.Cancel();
    }

    private bool StartRun(IReadOnlyList<int> targetIds, string trigger)
    {
        CancellationToken token;
        lock (_statusLock)
        {
            if (_status.State == "running") return false;
            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            token = _runCancellation.Token;
            _status = new SaveBackupStatusDto
            {
                State = "running",
                Total = targetIds.Count,
                Done = 0,
                Failed = 0,
                Created = 0,
                Trigger = trigger,
                StartedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
        }
        Publish();
        _ = Task.Run(() => RunTargetsAsync(targetIds, trigger, token), CancellationToken.None);
        return true;
    }

    private async Task RunTargetsAsync(IReadOnlyList<int> targetIds, string trigger, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var targetId in targetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await SnapshotTargetAsync(targetId, trigger, label =>
                    Update(status => status with { CurrentTargetId = targetId, CurrentLabel = label }), cancellationToken).ConfigureAwait(false);
                Update(status => status with
                {
                    Done = status.Done + 1,
                    Failed = status.Failed + (outcome.Error != null ? 1 : 0),
                    Created = status.Created + (outcome.Created ? 1 : 0)
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
            logger.LogWarning(exception, "Save backup run failed");
            Finish("failed", exception.GetType().Name);
        }
    }

    /// <summary>快照单个目录：内容与最近一个版本相同则只更新扫描时间，不产生新版本。</summary>
    internal async Task<(bool Created, string? Error)> SnapshotTargetAsync(
        int targetId,
        string trigger,
        Action<string>? onStart,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var target = await db.SaveBackupTargetTable.FirstOrDefaultAsync(row => row.Id == targetId, cancellationToken).ConfigureAwait(false);
            if (target == null) return (false, "notFound");
            var names = AppNameHelper.LookupNames(db, [target.AppId]);
            onStart?.Invoke(target.Label ?? names.GetValueOrDefault(target.AppId) ?? System.IO.Path.GetFileName(target.Path));

            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var maxBytes = Math.Clamp(settingsStore.GetSettings().Archive?.SaveBackupMaxTargetMb ?? 1024, 1, 102_400) * 1024L * 1024L;
            var scan = await Task.Run(() => SaveBackupObjectStore.Scan(target.Path, MaxFilesPerTarget, maxBytes), cancellationToken)
                .ConfigureAwait(false);
            target.LastScanAt = now;
            if (scan.Error != null)
            {
                target.LastError = scan.Error;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (false, scan.Error);
            }

            var previous = await db.SaveBackupSnapshotTable.AsNoTracking()
                .Where(row => row.TargetId == targetId)
                .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var previousFiles = previous == null
                ? new Dictionary<string, SaveBackupFile>(StringComparer.Ordinal)
                : await db.SaveBackupFileTable.AsNoTracking()
                    .Where(row => row.SnapshotId == previous.Id)
                    .ToDictionaryAsync(row => row.RelativePath, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

            var manifest = new List<SaveManifestEntry>(scan.Files.Count);
            long addedSize = 0;
            var changed = 0;
            foreach (var file in scan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 大小与修改时间均未变且对象仍在备份库中：沿用上一版本的哈希，避免重复读取
                if (previousFiles.TryGetValue(file.RelativePath, out var known)
                    && known.Size == file.Size && known.ModifiedAt == file.ModifiedAt && _store.Exists(known.Sha256))
                {
                    manifest.Add(new SaveManifestEntry(file.RelativePath, file.Size, file.ModifiedAt, known.Sha256));
                    continue;
                }
                try
                {
                    var (sha, added) = await _store.StoreAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                    if (added) addedSize += file.Size;
                    if (known == null || known.Sha256 != sha) changed++;
                    manifest.Add(new SaveManifestEntry(file.RelativePath, file.Size, file.ModifiedAt, sha));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 游戏正独占写入等情况：跳过该文件，下一轮再试
                    logger.LogDebug("Skipped an unreadable save file with {ExceptionType}", exception.GetType().Name);
                }
            }
            var current = manifest.Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
            changed += previousFiles.Keys.Count(path => !current.Contains(path));

            var manifestHash = SaveBackupObjectStore.ManifestHash(manifest);
            if (previous != null && previous.ManifestHash == manifestHash)
            {
                target.LastError = null;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (false, null);
            }
            if (manifest.Count == 0 && previous == null)
            {
                target.LastError = "empty";
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (false, null);
            }

            var snapshot = new SaveBackupSnapshot
            {
                TargetId = targetId,
                CreatedAt = now,
                FileCount = manifest.Count,
                TotalSize = manifest.Sum(item => item.Size),
                AddedSize = addedSize,
                ChangedFiles = changed,
                Trigger = trigger,
                ManifestHash = manifestHash,
                LatestModifiedAt = manifest.Count == 0 ? null : manifest.Max(item => item.ModifiedAt)
            };
            db.SaveBackupSnapshotTable.Add(snapshot);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.SaveBackupFileTable.AddRange(manifest.Select(item => new SaveBackupFile
            {
                SnapshotId = snapshot.Id,
                RelativePath = item.RelativePath,
                Size = item.Size,
                ModifiedAt = item.ModifiedAt,
                Sha256 = item.Sha256
            }));
            target.LastSnapshotAt = now;
            target.LastError = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await ApplyRetentionAsync(db, targetId).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to snapshot save backup target {TargetId}", targetId);
            return (false, "failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ApplyRetentionAsync(AppDbContext db, int targetId)
    {
        var keep = settingsStore.GetSettings().Archive?.SaveBackupKeepVersions ?? 0;
        if (keep <= 0) return;
        var stale = await db.SaveBackupSnapshotTable.AsNoTracking()
            .Where(row => row.TargetId == targetId)
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Skip(keep)
            .Select(row => row.Id)
            .ToListAsync().ConfigureAwait(false);
        if (stale.Count == 0) return;
        await DeleteSnapshotsAsync(db, stale).ConfigureAwait(false);
    }

    /// <summary>删除版本并回收不再被任何版本引用的对象。</summary>
    private async Task DeleteSnapshotsAsync(AppDbContext db, IReadOnlyCollection<int> snapshotIds)
    {
        if (snapshotIds.Count == 0) return;
        var shas = await db.SaveBackupFileTable.AsNoTracking()
            .Where(row => snapshotIds.Contains(row.SnapshotId))
            .Select(row => row.Sha256)
            .Distinct()
            .ToListAsync().ConfigureAwait(false);
        await db.SaveBackupFileTable.Where(row => snapshotIds.Contains(row.SnapshotId)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.SaveBackupSnapshotTable.Where(row => snapshotIds.Contains(row.Id)).ExecuteDeleteAsync().ConfigureAwait(false);
        foreach (var chunk in shas.Chunk(500))
        {
            var stillUsed = (await db.SaveBackupFileTable.AsNoTracking()
                    .Where(row => chunk.Contains(row.Sha256))
                    .Select(row => row.Sha256)
                    .Distinct()
                    .ToListAsync().ConfigureAwait(false))
                .ToHashSet();
            foreach (var sha in chunk.Where(sha => !stillUsed.Contains(sha)))
            {
                try
                {
                    _store.Delete(sha);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug("Failed to delete an unreferenced backup object with {ExceptionType}", exception.GetType().Name);
                }
            }
        }
    }

    private void Update(Func<SaveBackupStatusDto, SaveBackupStatusDto> change)
    {
        lock (_statusLock) _status = change(_status);
        Publish();
    }

    private void Finish(string state, string? error)
        => Update(status => status with
        {
            State = state,
            Error = error,
            CurrentTargetId = null,
            CurrentLabel = null,
            FinishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
        });

    // 串行转发，保证渲染层按顺序收到进度
    private void Publish()
    {
        lock (_statusLock)
        {
            var snapshot = _status;
            _publishChain = _publishChain
                .ContinueWith(_ => eventBus.PublishAsync(new SaveBackupProgressChanged(snapshot)), TaskScheduler.Default)
                .Unwrap();
        }
    }

    #endregion

    #region 版本浏览

    public IReadOnlyList<SaveBackupSnapshotDto> GetSnapshots(int targetId)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.SaveBackupSnapshotTable.AsNoTracking()
            .Where(row => row.TargetId == targetId)
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .AsEnumerable()
            .Select(row => new SaveBackupSnapshotDto
            {
                Id = row.Id,
                TargetId = row.TargetId,
                CreatedAt = row.CreatedAt,
                FileCount = row.FileCount,
                TotalSize = row.TotalSize,
                AddedSize = row.AddedSize,
                ChangedFiles = row.ChangedFiles,
                Trigger = row.Trigger,
                LatestModifiedAt = row.LatestModifiedAt
            })
            .ToArray();
    }

    public SaveBackupSnapshotFilesDto GetSnapshotFiles(int snapshotId)
    {
        using var db = dbContextFactory.CreateDbContext();
        var snapshot = db.SaveBackupSnapshotTable.AsNoTracking().FirstOrDefault(row => row.Id == snapshotId);
        if (snapshot == null) return new SaveBackupSnapshotFilesDto { SnapshotId = snapshotId, Items = [], Removed = [] };
        var previous = db.SaveBackupSnapshotTable.AsNoTracking()
            .Where(row => row.TargetId == snapshot.TargetId
                          && (row.CreatedAt < snapshot.CreatedAt || (row.CreatedAt == snapshot.CreatedAt && row.Id < snapshot.Id)))
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .FirstOrDefault();
        var files = db.SaveBackupFileTable.AsNoTracking().Where(row => row.SnapshotId == snapshotId).ToList();
        var before = previous == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : db.SaveBackupFileTable.AsNoTracking().Where(row => row.SnapshotId == previous.Id)
                .ToDictionary(row => row.RelativePath, row => row.Sha256, StringComparer.Ordinal);
        var current = files.Select(row => row.RelativePath).ToHashSet(StringComparer.Ordinal);
        return new SaveBackupSnapshotFilesDto
        {
            SnapshotId = snapshotId,
            PreviousSnapshotId = previous?.Id,
            Items = files
                .OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(row => new SaveBackupFileDto
                {
                    RelativePath = row.RelativePath,
                    Size = row.Size,
                    ModifiedAt = row.ModifiedAt,
                    Sha256 = row.Sha256,
                    Change = previous == null || !before.TryGetValue(row.RelativePath, out var sha) ? "added"
                        : sha == row.Sha256 ? "unchanged" : "modified"
                })
                .ToArray(),
            Removed = before.Keys.Where(path => !current.Contains(path)).Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public async Task<bool> DeleteSnapshotAsync(int snapshotId)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            if (!await db.SaveBackupSnapshotTable.AnyAsync(row => row.Id == snapshotId).ConfigureAwait(false)) return false;
            await DeleteSnapshotsAsync(db, [snapshotId]).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>把版本还原到备份库下的新文件夹并在资源管理器中打开（用户自行决定如何使用，软件从不写回存档目录）。</summary>
    public async Task<SaveBackupExportResultDto> ExportSnapshotAsync(int snapshotId)
    {
        try
        {
            SaveBackupSnapshot? snapshot;
            SaveBackupTarget? target;
            List<SaveManifestEntry> files;
            await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
            {
                snapshot = await db.SaveBackupSnapshotTable.AsNoTracking().FirstOrDefaultAsync(row => row.Id == snapshotId).ConfigureAwait(false);
                if (snapshot == null) return new SaveBackupExportResultDto { Success = false, Error = "notFound" };
                target = await db.SaveBackupTargetTable.AsNoTracking().FirstOrDefaultAsync(row => row.Id == snapshot.TargetId).ConfigureAwait(false);
                files = await db.SaveBackupFileTable.AsNoTracking().Where(row => row.SnapshotId == snapshotId)
                    .Select(row => new SaveManifestEntry(row.RelativePath, row.Size, row.ModifiedAt, row.Sha256))
                    .ToListAsync().ConfigureAwait(false);
            }
            var stamp = DateTimeOffset.FromUnixTimeSeconds(snapshot.CreatedAt).ToLocalTime().ToString("yyyyMMdd-HHmmss");
            var folder = $"{target?.AppId ?? 0}-{snapshotId}-{stamp}";
            var path = await Task.Run(() => _store.Export(files, folder)).ConfigureAwait(false);
            try
            {
                await Electron.Shell.OpenPathAsync(path).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogDebug("Failed to open the export folder with {ExceptionType}", exception.GetType().Name);
            }
            return new SaveBackupExportResultDto { Success = true, Path = path };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to export save backup snapshot {SnapshotId}", snapshotId);
            return new SaveBackupExportResultDto { Success = false, Error = "failed" };
        }
    }

    #endregion

    /// <summary>不允许把整个磁盘根目录、用户目录或备份库本身当作存档目录。</summary>
    private bool IsForbidden(string path)
    {
        var key = NormalizeKey(path);
        if (System.IO.Path.GetPathRoot(path) is { } root && NormalizeKey(root) == key) return true;
        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };
        if (forbidden.Where(item => !string.IsNullOrEmpty(item)).Any(item => NormalizeKey(item) == key)) return true;
        var store = NormalizeKey(_store.Root);
        return key == store || key.StartsWith(store + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || store.StartsWith(key + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string NormalizeKey(string path)
    {
        try
        {
            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_statusLock)
        {
            foreach (var timer in _sessionTimers.Values) timer.Dispose();
            _sessionTimers.Clear();
            _runCancellation?.Cancel();
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
        _writeLock.Dispose();
    }
}

internal sealed record SaveBackupProgressChanged(SaveBackupStatusDto Status);
