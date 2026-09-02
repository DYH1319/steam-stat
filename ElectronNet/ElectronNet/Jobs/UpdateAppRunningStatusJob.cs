using ElectronNet.Constants;
using ElectronNet.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Settings;

namespace ElectronNet.Jobs;

/// <summary>
/// 定时更新应用运行状态任务
/// </summary>
public sealed class UpdateAppRunningStatusJob(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    TimeProvider timeProvider,
    ILogger<UpdateAppRunningStatusJob> logger) : IAppRunningStatusJobController, IHostedService, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _hostStopping = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _hostStopping.CancelAsync();
        await StopLoopAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) StartLoop();
        else StopLoopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 设置更新间隔时间
    /// </summary>
    public void SetInterval(TimeSpan interval)
    {
        if (interval.TotalMilliseconds < 1000)
        {
            logger.LogWarning("更新间隔时间不能小于1000ms，已自动设置为1000ms");
            interval = TimeSpan.FromMilliseconds(1000);
        }

        var restart = false;
        lock (_sync)
        {
            _intervalTime = interval;
            restart = _isRunning;
        }
        logger.LogInformation("应用运行状态更新间隔已设置为: {IntervalMilliseconds}ms", interval.TotalMilliseconds);
        if (restart)
        {
            StopLoopAsync(CancellationToken.None).GetAwaiter().GetResult();
            StartLoop();
        }
    }

    private void StartLoop()
    {
        lock (_sync)
        {
            if (_isRunning || _hostStopping.IsCancellationRequested) return;
            _isRunning = true;
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_hostStopping.Token);
            _runTask = RunAsync(_runCancellation.Token);
        }
    }

    private async Task StopLoopAsync(CancellationToken cancellationToken)
    {
        Task? runTask;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_isRunning) return;
            _isRunning = false;
            cancellation = _runCancellation;
            runTask = _runTask;
            _runCancellation = null;
            _runTask = null;
        }

        if (cancellation != null) await cancellation.CancelAsync();
        if (runTask != null)
        {
            try
            {
                await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await UpdateAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan interval;
            lock (_sync) interval = _intervalTime;
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
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
                var globalStatus = await GlobalStatusService.SyncAndGetOne(dbContextFactory, installLocator, log: false);
                var activeSteamId = globalStatus?.ActiveUserSteamId;

                if (activeSteamId != null)
                {
                    await SteamAppService.SyncDb(dbContextFactory, installLocator, log: false);
                    await SteamAppService.UpdateAppRunningStatus(added, isRunning: true, dbContextFactory: dbContextFactory);
                    await SteamAppService.UpdateAppRunningStatus(removed, isRunning: false, dbContextFactory: dbContextFactory);

                    foreach (var appId in added)
                        await UseAppRecordService.StartRecord(activeSteamId, appId, dbContextFactory);
                    foreach (var appId in removed)
                        await UseAppRecordService.StopRecord(activeSteamId, appId, dbContextFactory);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _hostStopping.CancelAsync();
        await StopLoopAsync(CancellationToken.None).ConfigureAwait(false);
        _hostStopping.Dispose();
    }
}
