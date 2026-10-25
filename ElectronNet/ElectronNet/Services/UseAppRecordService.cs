using ElectronNet.Features.UsageTracking;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

public sealed class UseAppRecordService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    DeviceService deviceService,
    ISettingsStore settingsStore,
    ILogger<UseAppRecordService> logger)
{
    /// <summary>
    /// 启动时初始化数据库：确保当前设备、回填 device_id、恢复中断的记录
    /// </summary>
    public async Task InitDb(CancellationToken cancellationToken = default)
    {
        try
        {
            var deviceId = await deviceService.EnsureCurrentAsync(cancellationToken);

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 回填缺失的设备 ID
            await db.UseAppRecordTable
                .Where(r => r.DeviceId == null && r.Source != "imported")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeviceId, deviceId), cancellationToken);

            // 恢复上次异常退出遗留的未结束记录：按最后心跳补全
            var records = db.UseAppRecordTable.Where(r => r.EndTime == null).ToList();
            foreach (var record in records)
            {
                var endTime = Math.Max(record.LastHeartbeat ?? record.StartTime, record.StartTime);
                record.EndTime = endTime;
                record.Duration = endTime - record.StartTime;
                record.EndReason = "recovered";
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Initialized usage records and recovered {RecordCount} interrupted records", records.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to initialize usage records");
        }
    }

    /// <summary>
    /// 获取所有数据
    /// </summary>
    public List<UseAppRecord> GetAll()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.UseAppRecordTable.AsNoTracking().ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query usage records");
            return [];
        }
    }

    /// <summary>
    /// 根据参数获取有效的记录（应用查询期清洗规则）
    /// </summary>
    public List<UseAppRecordDto> GetValidByParam(UseAppRecordsQueryRequest param)
    {
        try
        {
            var steamIds = param.SteamIds;
            var startDate = param.StartDate;
            var endDate = param.EndDate;

            using var db = dbContextFactory.CreateDbContext();
            var records = db.UseAppRecordTable
                // ReSharper disable once EntityFramework.UnsupportedServerSideFunctionCall
                .LeftJoin(
                    db.SteamAppTable,
                    record => record.AppId,
                    app => app.AppId,
                    (record, app) => new { record, app }
                )
                .LeftJoin(
                    db.SteamUserTable,
                    x => x.record.SteamId,
                    user => user.SteamId,
                    (x, user) => new UseAppRecordDto
                    {
                        AppId = x.record.AppId,
                        SteamId = x.record.SteamId,
                        StartTime = x.record.StartTime,
                        EndTime = x.record.EndTime ?? 0,
                        Duration = x.record.Duration ?? 0,
                        IdleDuration = (int)x.record.IdleDuration,
                        ForegroundDuration = x.record.ForegroundDuration == null ? null : (int)x.record.ForegroundDuration,
                        AppName = x.app != null ? x.app.Name : null,
                        AppNameLocalized = x.app != null ? x.app.NameLocalizedJson : null,
                        UserPersonaName = user != null ? user.PersonaName : null
                    }
                )
                .Where(x => x.Duration > 0)
                .Where(x => steamIds == null || steamIds.Count == 0 || steamIds.Contains(x.SteamId))
                .Where(x => startDate == null || x.StartTime >= startDate)
                .Where(x => endDate == null || x.StartTime <= endDate)
                .OrderBy(x => x.StartTime)
                .ToList();

            var tracking = settingsStore.GetSettings().SessionTracking;
            var rules = new SessionCleaningRules(
                tracking?.MinDurationSeconds ?? 0,
                tracking?.MergeGapSeconds ?? 0,
                (tracking?.ExcludedAppIds ?? []).ToHashSet());
            return SessionCleaner.Apply(records, rules);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query valid usage records");
            return [];
        }
    }

    /// <summary>
    /// 开始记录应用使用，返回新记录 ID（失败返回 null）
    /// </summary>
    public async Task<int?> StartRecord(string steamId, int appId, int startTime, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var deviceId = await deviceService.EnsureCurrentAsync(cancellationToken);

            var record = new UseAppRecord
            {
                SteamId = steamId,
                AppId = appId,
                StartTime = startTime,
                EndTime = null,
                Duration = null,
                LastHeartbeat = startTime,
                DeviceId = deviceId,
                Source = "auto"
            };
            db.UseAppRecordTable.Add(record);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Started usage record {RecordId} for {SteamId} and app {AppId}", record.Id, steamId, appId);
            return record.Id;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start usage record for {SteamId} and app {AppId}", steamId, appId);
            return null;
        }
    }

    /// <summary>
    /// 更新进行中记录的心跳与累计空闲/前台时长
    /// </summary>
    public async Task Heartbeat(int recordId, int heartbeat, long idleSeconds, long? foregroundSeconds, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await db.UseAppRecordTable
                .Where(r => r.Id == recordId && r.EndTime == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.LastHeartbeat, heartbeat)
                    .SetProperty(r => r.IdleDuration, idleSeconds)
                    .SetProperty(r => r.ForegroundDuration, foregroundSeconds), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to heartbeat usage record {RecordId}", recordId);
        }
    }

    /// <summary>
    /// 结束指定记录（仅当记录仍未结束）
    /// </summary>
    public async Task StopRecord(int recordId, int endTime, long idleSeconds, long? foregroundSeconds, string endReason, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.UseAppRecordTable
                .FirstOrDefaultAsync(r => r.Id == recordId && r.EndTime == null, cancellationToken);
            if (record == null) return;

            var duration = endTime - record.StartTime;
            record.EndTime = endTime;
            record.Duration = duration;
            record.IdleDuration = Math.Clamp(idleSeconds, 0, Math.Max(0, duration));
            record.ForegroundDuration = foregroundSeconds == null
                ? null
                : Math.Clamp(foregroundSeconds.Value, 0, Math.Max(0, duration));
            record.EndReason = endReason;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Stopped usage record {RecordId} after {DurationSeconds}s ({EndReason})", recordId, duration, endReason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to stop usage record {RecordId}", recordId);
        }
    }

    /// <summary>
    /// 结束所有正在运行的记录（记录当前时间为结束时间）
    /// </summary>
    public async Task<bool> EndAllRecordings(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var currentTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

            var records = db.UseAppRecordTable
                .Where(r => r.EndTime == null)
                .ToList();

            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = currentTime - record.StartTime;
                record.EndReason = "ended";
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Ended {RecordCount} active usage records", records.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to end active usage records");
            return false;
        }
    }

    /// <summary>
    /// 作废所有正在运行的记录（duration 设为 -1）
    /// </summary>
    public async Task<bool> DiscardAllRecordings(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var currentTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

            var records = db.UseAppRecordTable
                .Where(r => r.EndTime == null)
                .ToList();

            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = -1;
                record.EndReason = "discarded";
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Discarded {RecordCount} active usage records", records.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to discard active usage records");
            return false;
        }
    }
}
