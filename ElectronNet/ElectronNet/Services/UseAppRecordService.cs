using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Services;

public sealed class UseAppRecordService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<UseAppRecordService> logger)
{
    /// <summary>
    /// 启动时初始化数据库
    /// </summary>
    public async Task InitDb(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var records = db.UseAppRecordTable.Where(r => r.EndTime == null).ToList();
            var currentTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = -1;
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Initialized usage records and closed {RecordCount} interrupted records", records.Count);
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
    /// 根据参数获取有效的记录
    /// </summary>
    public List<UseAppRecordDto> GetValidByParam(UseAppRecordsQueryRequest param)
    {
        try
        {
            var steamIds = param.SteamIds;
            var startDate = param.StartDate;
            var endDate = param.EndDate;

            using var db = dbContextFactory.CreateDbContext();
            return db.UseAppRecordTable
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
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query valid usage records");
            return [];
        }
    }

    /// <summary>
    /// 开始记录应用使用
    /// </summary>
    public async Task StartRecord(string steamId, int appId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var currentTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

            db.UseAppRecordTable.Add(new UseAppRecord
            {
                SteamId = steamId,
                AppId = appId,
                StartTime = currentTime,
                EndTime = null,
                Duration = null
            });
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Started usage record for {SteamId} and app {AppId}", steamId, appId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start usage record for {SteamId} and app {AppId}", steamId, appId);
        }
    }

    /// <summary>
    /// 结束记录应用使用
    /// </summary>
    public async Task StopRecord(string steamId, int appId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var currentTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

            // 查找最近一条未结束的记录
            var record = db.UseAppRecordTable
                .Where(r => r.SteamId == steamId && r.AppId == appId && r.EndTime == null)
                .OrderByDescending(r => r.StartTime)
                .FirstOrDefault();

            if (record != null)
            {
                record.EndTime = currentTime;
                record.Duration = currentTime - record.StartTime;
                await db.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Stopped usage record for {SteamId} and app {AppId} after {DurationSeconds}s", steamId, appId, record.Duration);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to stop usage record for {SteamId} and app {AppId}", steamId, appId);
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
