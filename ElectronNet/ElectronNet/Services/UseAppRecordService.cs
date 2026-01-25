using ElectronNet.Constants;
using ElectronNet.Models;

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
            var db = AppDbContext.Instance;

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
    public static List<UseAppRecord>? GetAll()
    {
        try
        {
            var db = AppDbContext.Instance;
            var result = db.UseAppRecordTable.ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAll)} UseAppRecord 表失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取所有有效的记录
    /// </summary>
    public static List<dynamic>? GetAllValid()
    {
        try
        {
            var db = AppDbContext.Instance;
            var result = db.UseAppRecordTable
                // ReSharper disable once EntityFramework.UnsupportedServerSideFunctionCall
                .LeftJoin(
                    db.SteamAppTable,
                    record => record.AppId,
                    app => app.AppId,
                    (record, app) => new
                    {
                        record.AppId,
                        record.SteamId,
                        record.StartTime,
                        record.EndTime,
                        record.Duration,
                        AppName = app != null ? app.Name : null,
                        NameLocalized = app != null ? app.NameLocalizedJson : null
                    }
                )
                .Where(x => x.Duration > 0)
                .ToList();
            return result.Cast<dynamic>().ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAllValid)} UseAppRecord 表失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 开始记录应用使用
    /// </summary>
    public static async Task StartRecord(long steamId, int appId)
    {
        try
        {
            var db = AppDbContext.Instance;
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
            var db = AppDbContext.Instance;
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
}