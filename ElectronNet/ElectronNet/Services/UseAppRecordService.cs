using ElectronNet.Constants;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Services;

public static class UseAppRecordService
{
    /// <summary>
    /// 启动时初始化数据库
    /// </summary>
    public static async Task InitDb()
    {
        try
        {
            await using var db = AppDbContext.Create();

            var records = db.UseAppRecordTable.Where(r => r.EndTime == null).ToList();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = -1;
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.DB} 成功初始化 UseAppRecord 表，结束了 {records.Count} 个未正常完成的使用记录");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(InitDb)} UseAppRecord 表失败: {ex}");
        }
    }

    /// <summary>
    /// 获取所有数据
    /// </summary>
    public static List<UseAppRecord> GetAll()
    {
        try
        {
            using var db = AppDbContext.Create();
            var result = db.UseAppRecordTable.AsNoTracking().ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAll)} UseAppRecord 表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 根据参数获取有效的记录
    /// </summary>
    public static List<dynamic> GetValidByParam(object? param)
    {
        try
        {
            var pd = param as Dictionary<string, object>;

            var steamIds = ((List<object>?)pd?.GetValueOrDefault("steamIds"))?.Select(Convert.ToInt64).ToList();
            var startDate = (int?)pd?.GetValueOrDefault("startDate");
            var endDate = (int?)pd?.GetValueOrDefault("endDate");

            using var db = AppDbContext.Create();
            var result = db.UseAppRecordTable
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
                    (x, user) => new
                    {
                        x.record.AppId,
                        x.record.SteamId,
                        x.record.SteamIdStr,
                        x.record.StartTime,
                        x.record.EndTime,
                        x.record.Duration,
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
            return result.Cast<dynamic>().ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetValidByParam)} UseAppRecord 表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 开始记录应用使用
    /// </summary>
    public static async Task StartRecord(long steamId, int appId)
    {
        try
        {
            await using var db = AppDbContext.Create();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var newRecord = new UseAppRecord
            {
                SteamId = steamId,
                AppId = appId,
                StartTime = currentTime,
                EndTime = null,
                Duration = null
            };

            db.UseAppRecordTable.Add(newRecord);
            await db.SaveChangesAsync();

            Console.WriteLine($"{ConsoleLogPrefix.DB} 开始记录应用使用: SteamID={steamId}, AppID={appId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(StartRecord)} UseAppRecord 表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 结束记录应用使用
    /// </summary>
    public static async Task StopRecord(long steamId, int appId)
    {
        try
        {
            await using var db = AppDbContext.Create();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 查找最近一条未结束的记录
            var record = db.UseAppRecordTable
                .Where(r => r.SteamId == steamId && r.AppId == appId && r.EndTime == null)
                .OrderByDescending(r => r.StartTime)
                .FirstOrDefault();

            if (record != null)
            {
                record.EndTime = currentTime;
                record.Duration = currentTime - record.StartTime;
                await db.SaveChangesAsync();

                Console.WriteLine($"{ConsoleLogPrefix.DB} 结束记录应用使用: SteamID={steamId}, AppID={appId}, Duration={record.Duration}s");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(StopRecord)} UseAppRecord 表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 结束所有正在运行的记录（记录当前时间为结束时间）
    /// </summary>
    public static async Task<bool> EndAllRecordings()
    {
        try
        {
            await using var db = AppDbContext.Create();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var records = db.UseAppRecordTable
                .Where(r => r.EndTime == null)
                .ToList();

            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = currentTime - record.StartTime;
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.DB} 结束了 {records.Count} 个正在运行的记录");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(EndAllRecordings)} 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 作废所有正在运行的记录（duration 设为 -1）
    /// </summary>
    public static async Task<bool> DiscardAllRecordings()
    {
        try
        {
            await using var db = AppDbContext.Create();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var records = db.UseAppRecordTable
                .Where(r => r.EndTime == null)
                .ToList();

            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = -1;
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.DB} 作废了 {records.Count} 个正在运行的记录");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(DiscardAllRecordings)} 失败: {ex.Message}");
            return false;
        }
    }
}