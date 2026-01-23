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

            var records = db.UseAppRecordTable.Where(r => r.EndTime != null).ToList();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var record in records)
            {
                record.EndTime = currentTime;
                record.Duration = -1;
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.DB} 初始化 UseAppRecord 表成功，结束了 {records.Count} 个未正常完成的使用记录");
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
    public static List<UseAppRecord>? GetAllValid()
    {
        try
        {
            var db = AppDbContext.Instance;
            var result = db.UseAppRecordTable.Where(r => r.Duration > 0).ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAllValid)} UseAppRecord 表失败: {ex.Message}");
            return null;
        }
    }
}