using ElectronNET.API;
using ElectronNet.Features.Archive;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 截图档案：只读索引 Steam 截图目录，按游戏和日期浏览；文件被删除后记录保留（标记缺失）。
/// 与会话的关联在渲染层按时间区间完成。
/// </summary>
internal sealed class ScreenshotArchiveService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    ISettingsStore settingsStore,
    TimeProvider timeProvider,
    ILogger<ScreenshotArchiveService> logger) : IEventHandler<UsageSessionEnded>, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _pendingLock = new();
    private ITimer? _timer;
    private ITimer? _pending;
    private long _lastIndexedAt;

    /// <summary>启动后延迟首次索引，之后每小时一次（受设置开关控制）。</summary>
    public void StartSchedule()
    {
        if (_timer != null) return;
        _timer = timeProvider.CreateTimer(_ => _ = RunScheduledAsync(), null, InitialDelay, Interval);
    }

    /// <summary>游戏会话结束后稍等片刻再索引（Steam 写入截图与 screenshots.vdf 有延迟）。</summary>
    public void ScheduleSoon()
    {
        lock (_pendingLock)
        {
            _pending?.Dispose();
            _pending = timeProvider.CreateTimer(_ => _ = RunScheduledAsync(), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        }
    }

    public Task HandleAsync(UsageSessionEnded message, CancellationToken cancellationToken)
    {
        ScheduleSoon();
        return Task.CompletedTask;
    }

    private async Task RunScheduledAsync()
    {
        if (settingsStore.GetSettings().Archive?.ScreenshotIndexEnabled == false) return;
        var result = await RefreshAsync().ConfigureAwait(false);
        if (result.Added > 0 || result.Missing > 0)
            logger.LogInformation(
                "Screenshot index: {Total} total, {Added} added, {Missing} missing", result.Total, result.Added, result.Missing);
    }

    public async Task<ScreenshotIndexResultDto> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                return new ScreenshotIndexResultDto { Success = false, Total = 0, Added = 0, Updated = 0, Missing = 0, Error = "steamNotFound" };
            var scanned = await Task.Run(() => ScreenshotScanner.ScanAll(steamPath), cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.ScreenshotArchiveTable.ToListAsync(cancellationToken).ConfigureAwait(false);
            var byKey = existing.ToDictionary(row => (row.AccountId, row.AppId, row.FileName.ToLowerInvariant()));
            var seen = new HashSet<int>();
            int added = 0, updated = 0, missing = 0;
            foreach (var file in scanned)
            {
                if (byKey.TryGetValue((file.AccountId, file.AppId, file.FileName.ToLowerInvariant()), out var row))
                {
                    seen.Add(row.Id);
                    var changed = row.FilePath != file.FilePath || row.ThumbnailPath != file.ThumbnailPath
                        || row.FileSize != file.FileSize || row.MissingSince != null || row.Caption != file.Caption
                        || row.Width != file.Width || row.Height != file.Height || row.TakenAt != file.TakenAt;
                    if (!changed) continue;
                    row.SteamId = file.SteamId;
                    row.FilePath = file.FilePath;
                    row.ThumbnailPath = file.ThumbnailPath;
                    row.TakenAt = file.TakenAt;
                    row.FileSize = file.FileSize;
                    row.Width = file.Width;
                    row.Height = file.Height;
                    row.Caption = Truncate(file.Caption, 1024);
                    row.MissingSince = null;
                    row.IndexedAt = now;
                    updated++;
                    continue;
                }
                var created = new ScreenshotArchive
                {
                    AccountId = file.AccountId,
                    SteamId = file.SteamId,
                    AppId = file.AppId,
                    FileName = file.FileName,
                    FilePath = file.FilePath,
                    ThumbnailPath = file.ThumbnailPath,
                    TakenAt = file.TakenAt,
                    FileSize = file.FileSize,
                    Width = file.Width,
                    Height = file.Height,
                    Caption = Truncate(file.Caption, 1024),
                    IndexedAt = now
                };
                db.ScreenshotArchiveTable.Add(created);
                byKey[(file.AccountId, file.AppId, file.FileName.ToLowerInvariant())] = created;
                added++;
            }
            foreach (var row in existing)
            {
                if (seen.Contains(row.Id) || row.MissingSince != null) continue;
                row.MissingSince = now;
                missing++;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastIndexedAt, now);
            return new ScreenshotIndexResultDto
            {
                Success = true,
                Total = scanned.Count,
                Added = added,
                Updated = updated,
                Missing = missing
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to index Steam screenshots");
            return new ScreenshotIndexResultDto { Success = false, Total = 0, Added = 0, Updated = 0, Missing = 0, Error = "failed" };
        }
        finally
        {
            _gate.Release();
        }
    }

    public ScreenshotPageDto GetList(ScreenshotQueryRequest request)
    {
        using var db = dbContextFactory.CreateDbContext();
        var query = db.ScreenshotArchiveTable.AsNoTracking();
        if (request.IncludeMissing != true) query = query.Where(row => row.MissingSince == null);
        if (request.AppId.HasValue) query = query.Where(row => row.AppId == request.AppId.Value);
        if (!string.IsNullOrWhiteSpace(request.SteamId)) query = query.Where(row => row.SteamId == request.SteamId);
        if (request.From.HasValue) query = query.Where(row => row.TakenAt >= request.From.Value);
        if (request.To.HasValue) query = query.Where(row => row.TakenAt <= request.To.Value);
        var total = query.Count();
        var items = query.OrderByDescending(row => row.TakenAt).ThenByDescending(row => row.Id)
            .Skip(request.Offset)
            .Take(request.Limit)
            .AsEnumerable()
            .Select(ToDto)
            .ToArray();
        var next = request.Offset + items.Length;
        var indexedAt = Interlocked.Read(ref _lastIndexedAt);
        return new ScreenshotPageDto
        {
            Total = total,
            Items = items,
            NextOffset = next < total ? next : null,
            IndexedAt = indexedAt > 0 ? indexedAt : null
        };
    }

    public IReadOnlyList<ScreenshotAppDto> GetApps()
    {
        using var db = dbContextFactory.CreateDbContext();
        var rows = db.ScreenshotArchiveTable.AsNoTracking()
            .Where(row => row.MissingSince == null)
            .GroupBy(row => row.AppId)
            .Select(group => new
            {
                AppId = group.Key,
                Count = group.Count(),
                First = group.Min(row => row.TakenAt),
                Last = group.Max(row => row.TakenAt),
                Size = group.Sum(row => row.FileSize)
            })
            .ToList();
        var names = AppNameHelper.LookupNames(db, rows.Select(row => row.AppId));
        return rows
            .OrderByDescending(row => row.Last)
            .Select(row => new ScreenshotAppDto
            {
                AppId = (int)row.AppId,
                AppName = names.GetValueOrDefault(row.AppId, string.Empty),
                Count = row.Count,
                FirstTakenAt = row.First,
                LastTakenAt = row.Last,
                TotalSize = row.Size
            })
            .ToArray();
    }

    /// <summary>用系统默认程序打开截图，或在资源管理器中定位（路径由 Host 按 Id 解析）。</summary>
    public async Task<bool> RunActionAsync(ScreenshotActionRequest request)
    {
        string? path;
        await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            path = await db.ScreenshotArchiveTable.AsNoTracking()
                .Where(row => row.Id == request.Id)
                .Select(row => row.FilePath)
                .FirstOrDefaultAsync().ConfigureAwait(false);
        }
        if (path == null || !File.Exists(path)) return false;
        try
        {
            if (request.Action == "reveal") await Electron.Shell.ShowItemInFolderAsync(path).ConfigureAwait(false);
            else await Electron.Shell.OpenPathAsync(path).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open a screenshot");
            return false;
        }
    }

    private static ScreenshotDto ToDto(ScreenshotArchive row) => new()
    {
        Id = row.Id,
        AppId = (int)row.AppId,
        SteamId = row.SteamId,
        FileName = row.FileName,
        FilePath = row.FilePath,
        ThumbnailPath = row.ThumbnailPath,
        TakenAt = row.TakenAt,
        FileSize = row.FileSize,
        Width = row.Width,
        Height = row.Height,
        Caption = row.Caption,
        Missing = row.MissingSince != null
    };

    private static string? Truncate(string? value, int length)
        => value == null ? null : value.Length <= length ? value : value[..length];

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_pendingLock) _pending?.Dispose();
        _gate.Dispose();
    }
}
