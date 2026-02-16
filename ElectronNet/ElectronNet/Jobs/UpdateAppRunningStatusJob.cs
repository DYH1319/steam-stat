using ElectronNet.Constants;
using ElectronNet.Services;

namespace ElectronNet.Jobs;

/// <summary>
/// 定时更新应用运行状态任务
/// </summary>
public static class UpdateAppRunningStatusJob
{
    private static Timer? _timer;
    private static List<int> _lastRunningApps = [];

    private static TimeSpan IntervalTime = TimeSpan.FromSeconds(5);
    private static bool IsRunning;
    public static long LastUpdateTime;
    
    /// <summary>
    /// 获取该定时任务相关状态
    /// </summary>
    public static dynamic GetStatus()
    {
        return new
        {
            IsRunning,
            LastUpdateTime,
            IntervalTime = IntervalTime.TotalSeconds
        };
    }

    /// <summary>
    /// 启动定时更新任务
    /// </summary>
    public static void Start()
    {
        if (IsRunning)
        {
            Console.WriteLine($"{ConsoleLogPrefix.JOB} 应用运行状态更新任务已在运行");
            return;
        }

        Console.WriteLine($"{ConsoleLogPrefix.JOB} 启动应用运行状态更新任务，更新间隔: {IntervalTime.TotalMilliseconds}ms");
        IsRunning = true;

        // 立即执行一次
        // _ = UpdateAppRunningStatusAsync();

        // 启动定时任务
        _timer = new Timer(_ => Task.Run(Update), null, TimeSpan.FromMilliseconds(0), IntervalTime);
    }

    /// <summary>
    /// 停止定时更新任务
    /// </summary>
    public static void Stop()
    {
        if (!IsRunning)
        {
            Console.WriteLine($"{ConsoleLogPrefix.JOB} 应用运行状态更新任务未在运行");
            return;
        }

        Console.WriteLine($"{ConsoleLogPrefix.JOB} 停止应用运行状态更新任务");
        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
    }

    /// <summary>
    /// 设置更新间隔时间
    /// </summary>
    public static void SetInterval(TimeSpan interval)
    {
        if (interval.TotalMilliseconds < 1000)
        {
            Console.WriteLine($"{ConsoleLogPrefix.JOB} 更新间隔时间不能小于1000ms，已自动设置为1000ms");
            interval = TimeSpan.FromMilliseconds(1000);
        }

        IntervalTime = interval;
        Console.WriteLine($"{ConsoleLogPrefix.JOB} 应用运行状态更新间隔已设置为: {IntervalTime.TotalMilliseconds}ms");

        if (IsRunning)
        {
            _timer?.Change(TimeSpan.FromMilliseconds(0), IntervalTime);
        }
    }

    /// <summary>
    /// 执行更新操作
    /// </summary>
    private static async Task Update()
    {
        try
        {
            // 读取当前运行的应用列表
            var currentRunningApps = LocalRegService.ReadSteamAppRegs()
                .Where(a => a.Value.Running is 1)
                .Select(a => a.Key)
                .ToList();

            // 比较新旧值，检测变化
            var added = currentRunningApps.Where(appId => !_lastRunningApps.Contains(appId)).ToList(); // 当前正在运行，但是上一次检测时未在运行
            var removed = _lastRunningApps.Where(appId => !currentRunningApps.Contains(appId)).ToList(); // 上一次检测时在运行，但是当前未运行

            // 只在有变化时才更新数据库
            if (added.Count > 0 || removed.Count > 0)
            {
                Console.WriteLine($"{ConsoleLogPrefix.JOB} 检测到运行应用变化: 新增 {added.Count} 个, 移除 {removed.Count} 个");

                // 获取当前活跃用户的 SteamID
                var globalStatus = await GlobalStatusService.SyncAndGetOne(log: false);
                var activeSteamId = globalStatus?.ActiveUserSteamId;

                if (activeSteamId != null)
                {
                    // 更新应用运行状态
                    await SteamAppService.UpdateAppRunningStatus(currentRunningApps, isRunning: true);

                    // 记录新增的应用
                    foreach (var appId in added)
                    {
                        await UseAppRecordService.StartRecord(activeSteamId, appId);
                    }

                    // 结束移除的应用
                    foreach (var appId in removed)
                    {
                        await UseAppRecordService.StopRecord(activeSteamId, appId);
                    }
                }
                else
                {
                    Console.WriteLine($"{ConsoleLogPrefix.JOB} 未找到活跃用户 SteamID，跳过记录应用使用");
                }

                // 更新上一次的运行应用列表
                _lastRunningApps = currentRunningApps;
            }

            // 更新上一次的检测更新时间
            LastUpdateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} 应用运行状态更新失败: {ex.Message}");
        }
    }
}