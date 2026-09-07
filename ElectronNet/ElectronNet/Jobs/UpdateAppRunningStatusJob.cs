using ElectronNet.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Jobs;

/// <summary>
/// 定时更新应用运行状态任务
/// </summary>
public sealed class UpdateAppRunningStatusJob(
    ISteamInstallLocator installLocator,
    GlobalStatusService globalStatusService,
    SteamAppService steamAppService,
    UseAppRecordService useAppRecordService,
    TimeProvider timeProvider,
    ILogger<UpdateAppRunningStatusJob> logger) : BackgroundService, IAppRunningStatusJobController
{
    private readonly object _sync = new();
    private CancellationTokenSource _scheduleChanged = new();
    private HashSet<int> _lastRunningApps = [];
    private TimeSpan _intervalTime = TimeSpan.FromSeconds(5);
    private bool _isRunning;
    private long _lastUpdateTime;
    private int _disposed;

    public long LastUpdateTime => Interlocked.Read(ref _lastUpdateTime);

    /// <summary>
    /// 获取该定时任务相关状态
    /// </summary>
    public UpdateAppRunningStatusJobStatusDto GetStatus()
    {
        lock (_sync)
        {
            return new UpdateAppRunningStatusJobStatusDto(
                _isRunning,
                LastUpdateTime,
                _intervalTime.TotalSeconds);
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_isRunning == enabled) return;
            _isRunning = enabled;
        }
        SignalScheduleChanged();
        logger.LogInformation("Application running-status monitoring is {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// 设置更新间隔时间
    /// </summary>
    public void SetInterval(TimeSpan interval)
    {
        if (interval.TotalMilliseconds < 1000)
        {
            logger.LogWarning("应用运行状态更新间隔不能小于 1000ms，已自动设置为 1000ms");
            interval = TimeSpan.FromMilliseconds(1000);
        }

        lock (_sync)
        {
            if (_intervalTime == interval) return;
            _intervalTime = interval;
        }
        SignalScheduleChanged();
        logger.LogInformation("应用运行状态更新间隔已设置为 {IntervalMilliseconds}ms", interval.TotalMilliseconds);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        SignalScheduleChanged();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool enabled;
            TimeSpan interval;
            CancellationTokenSource iteration;
            lock (_sync)
            {
                enabled = _isRunning;
                interval = _intervalTime;
                iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _scheduleChanged.Token);
            }

            using (iteration)
            try
            {
                if (!enabled)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, iteration.Token).ConfigureAwait(false);
                    continue;
                }

                await UpdateAsync(iteration.Token).ConfigureAwait(false);
                using var timer = new PeriodicTimer(interval, timeProvider);
                while (await timer.WaitForNextTickAsync(iteration.Token).ConfigureAwait(false))
                    await UpdateAsync(iteration.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (iteration.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// 执行更新操作
    /// </summary>
    private async Task UpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 读取当前运行的应用列表
            var currentRunningApps = installLocator.ReadAppRegistry()
                .Where(a => a.Value.Running is 1)
                .Select(a => a.Key)
                .ToHashSet();

            HashSet<int> previous;
            lock (_sync) previous = [.. _lastRunningApps];
            var added = currentRunningApps.Except(previous).ToList();
            var removed = previous.Except(currentRunningApps).ToList();

            // 只在有变化时才更新数据库
            if (added.Count > 0 || removed.Count > 0)
            {
                logger.LogInformation("检测到运行应用变化: 新增 {AddedCount} 个, 移除 {RemovedCount} 个", added.Count, removed.Count);
                var globalStatus = await globalStatusService.SyncAndGetOne(log: false, cancellationToken);
                var activeSteamId = globalStatus?.ActiveUserSteamId;

                if (activeSteamId != null)
                {
                    await steamAppService.SyncDb(log: false, cancellationToken);
                    await steamAppService.UpdateAppRunningStatus(added, isRunning: true, cancellationToken);
                    await steamAppService.UpdateAppRunningStatus(removed, isRunning: false, cancellationToken);

                    foreach (var appId in added)
                        await useAppRecordService.StartRecord(activeSteamId, appId, cancellationToken);
                    foreach (var appId in removed)
                        await useAppRecordService.StopRecord(activeSteamId, appId, cancellationToken);
                }
                else
                {
                    logger.LogWarning("未找到活跃用户 SteamID，跳过记录应用使用");
                }

                lock (_sync) _lastRunningApps = currentRunningApps;
            }

            Interlocked.Exchange(ref _lastUpdateTime, timeProvider.GetUtcNow().ToUnixTimeSeconds());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "应用运行状态更新失败");
        }
    }

    private void SignalScheduleChanged()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            previous = _scheduleChanged;
            _scheduleChanged = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancellationTokenSource schedule;
        lock (_sync) schedule = _scheduleChanged;
        schedule.Cancel();
        schedule.Dispose();
        base.Dispose();
    }
}
