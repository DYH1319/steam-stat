using ElectronNet.Features.UsageTracking;
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
    IDesktopActivityProbe desktopActivityProbe,
    TimeProvider timeProvider,
    ILogger<UpdateAppRunningStatusJob> logger) : BackgroundService, IAppRunningStatusJobController
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _recordGate = new(1, 1);
    private readonly Dictionary<int, SessionActivityTracker> _trackers = new();
    private readonly Dictionary<string, int> _installDirAppIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _scheduleChanged = new();
    private HashSet<int> _lastRunningApps = [];
    private TimeSpan _intervalTime = TimeSpan.FromSeconds(5);
    private bool _isRunning;
    private bool _suspended;
    private DateTimeOffset? _suspendedAt;
    private DateTimeOffset? _lastTickAt;
    private bool _afkEnabled = true;
    private TimeSpan _afkThreshold = TimeSpan.FromMinutes(10);
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

    /// <summary>
    /// 设置 AFK 检测
    /// </summary>
    public void SetAfkDetection(bool enabled, TimeSpan threshold)
    {
        lock (_sync)
        {
            _afkEnabled = enabled;
            _afkThreshold = threshold;
        }
    }

    /// <summary>
    /// 系统进入睡眠：按当前时间结算所有会话
    /// </summary>
    public async Task HandleSuspendAsync()
    {
        await _recordGate.WaitAsync();
        try
        {
            var now = timeProvider.GetUtcNow();
            await FinalizeAllTrackersAsync(now, "suspended", CancellationToken.None);
            lock (_sync)
            {
                _lastRunningApps = [];
                _suspended = true;
                _suspendedAt = now;
                _lastTickAt = null;
            }
        }
        finally
        {
            _recordGate.Release();
        }
    }

    /// <summary>
    /// 系统唤醒：立即触发新一轮检测（开始新会话）
    /// </summary>
    public void HandleResume()
    {
        lock (_sync)
        {
            _suspended = false;
            _lastTickAt = null;
        }
        SignalScheduleChanged();
    }

    /// <summary>
    /// 结束（或作废）所有进行中的记录
    /// </summary>
    public async Task<bool> EndAllRecordingsAsync(bool discard)
    {
        await _recordGate.WaitAsync();
        try
        {
            var now = timeProvider.GetUtcNow();
            var trackers = _trackers.Values.ToList();
            _trackers.Clear();

            if (discard)
            {
                return await useAppRecordService.DiscardAllRecordings();
            }

            var endTime = (int)now.ToUnixTimeSeconds();
            foreach (var tracker in trackers)
            {
                await useAppRecordService.StopRecord(
                    tracker.RecordId,
                    endTime,
                    tracker.IdleSecondsAt(now),
                    tracker.ForegroundSeconds,
                    "ended");
            }
            return await useAppRecordService.EndAllRecordings();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to end all recordings (discard={Discard})", discard);
            return false;
        }
        finally
        {
            _recordGate.Release();
        }
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
        await _recordGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            TimeSpan interval;
            lock (_sync)
            {
                interval = _intervalTime;
                if (_suspended)
                {
                    // OnResume 事件丢失时兜底：挂起点距今超过 2 分钟说明机器已醒
                    if (_suspendedAt.HasValue && now - _suspendedAt.Value <= TimeSpan.FromMinutes(2))
                        return;
                    logger.LogInformation("OnResume 事件可能丢失（挂起已超过 2 分钟），恢复正常检测");
                    _suspended = false;
                    _suspendedAt = null;
                    _lastTickAt = null;
                }
            }

            // 系统睡眠等导致的 tick 间隔过大：按上次 tick 时间结算会话（睡眠切分）
            if (_lastTickAt.HasValue && now - _lastTickAt.Value > interval + TimeSpan.FromSeconds(60))
            {
                var suspendAt = _lastTickAt.Value;
                logger.LogInformation("检测到更新间隔异常（可能为系统睡眠），按 {SuspendAt} 结算进行中的会话", suspendAt);
                await FinalizeAllTrackersAsync(suspendAt, "suspended", cancellationToken);
                lock (_sync) _lastRunningApps = [];
            }

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

                // 先结算退出应用的跟踪器：与 Steam 同时退出时可能没有活跃用户
                var nowSeconds = (int)now.ToUnixTimeSeconds();
                foreach (var appId in removed)
                {
                    if (_trackers.Remove(appId, out var removedTracker))
                    {
                        await useAppRecordService.StopRecord(
                            removedTracker.RecordId,
                            nowSeconds,
                            removedTracker.IdleSecondsAt(now),
                            removedTracker.ForegroundSeconds,
                            "exited",
                            cancellationToken);
                    }
                }

                var globalStatus = await globalStatusService.SyncAndGetOne(log: false, cancellationToken);
                var activeSteamId = globalStatus?.ActiveUserSteamId;

                if (activeSteamId != null)
                {
                    await steamAppService.SyncDb(log: false, cancellationToken);
                    await steamAppService.UpdateAppRunningStatus(added, isRunning: true, cancellationToken);
                    await steamAppService.UpdateAppRunningStatus(removed, isRunning: false, cancellationToken);

                    foreach (var appId in added)
                    {
                        var recordId = await useAppRecordService.StartRecord(activeSteamId, appId, nowSeconds, cancellationToken);
                        if (recordId != null)
                        {
                            _trackers[appId] = new SessionActivityTracker(recordId.Value, activeSteamId, appId, now);
                        }
                    }

                    // 运行集合变化后刷新安装目录 → appId 映射（用于前台进程匹配）
                    RefreshInstallDirMap();
                }
                else
                {
                    logger.LogWarning("未找到活跃用户 SteamID，跳过记录应用使用");
                }

                lock (_sync) _lastRunningApps = currentRunningApps;
            }

            // 采样桌面活动并更新跟踪器
            var sample = desktopActivityProbe.Sample();
            int? foregroundAppId = null;
            if (sample.ForegroundProcessPath != null)
            {
                foreach (var (installDir, appId) in _installDirAppIds)
                {
                    if (sample.ForegroundProcessPath.StartsWith(installDir + '\\', StringComparison.OrdinalIgnoreCase))
                    {
                        foregroundAppId = appId;
                        break;
                    }
                }
            }

            bool afkEnabled;
            TimeSpan afkThreshold;
            lock (_sync)
            {
                afkEnabled = _afkEnabled;
                afkThreshold = _afkThreshold;
            }
            foreach (var tracker in _trackers.Values)
            {
                bool? isForeground = sample.ForegroundProcessPath == null
                    ? null
                    : foregroundAppId == tracker.AppId;
                tracker.Observe(now, sample.IdleTime, isForeground, afkEnabled, afkThreshold);
            }

            // 心跳持久化（每 30 秒）
            var nowUnix = (int)now.ToUnixTimeSeconds();
            foreach (var tracker in _trackers.Values)
            {
                if (now - tracker.LastFlushedAt >= TimeSpan.FromSeconds(30))
                {
                    tracker.LastFlushedAt = now;
                    await useAppRecordService.Heartbeat(
                        tracker.RecordId, nowUnix, tracker.IdleSecondsAt(now), tracker.ForegroundSeconds, cancellationToken);
                }
            }

            _lastTickAt = now;
            Interlocked.Exchange(ref _lastUpdateTime, now.ToUnixTimeSeconds());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "应用运行状态更新失败");
        }
        finally
        {
            _recordGate.Release();
        }
    }

    private void RefreshInstallDirMap()
    {
        _installDirAppIds.Clear();
        foreach (var app in steamAppService.GetAllRunning())
        {
            if (!string.IsNullOrEmpty(app.InstallDirPath))
            {
                _installDirAppIds[app.InstallDirPath!] = app.AppId;
            }
        }
    }

    private async Task FinalizeAllTrackersAsync(DateTimeOffset at, string endReason, CancellationToken cancellationToken)
    {
        var atSeconds = (int)at.ToUnixTimeSeconds();
        foreach (var tracker in _trackers.Values)
        {
            await useAppRecordService.StopRecord(
                tracker.RecordId,
                atSeconds,
                tracker.IdleSecondsAt(at),
                tracker.ForegroundSeconds,
                endReason,
                cancellationToken);
        }
        _trackers.Clear();
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
        _recordGate.Dispose();
        base.Dispose();
    }
}
