using ElectronNet.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Jobs;

/// <summary>
/// 定期采集官方累计时长快照（启动 60 秒后执行一次，此后每 6 小时一次）
/// </summary>
public sealed class PlaytimeSnapshotJob(
    PlaytimeHistoryService playtimeHistoryService,
    ILogger<PlaytimeSnapshotJob> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await playtimeHistoryService.RunSnapshotAsync(
                    "all", refreshCm: false, stoppingToken);
                logger.LogInformation(
                    "Playtime snapshot run: inserted {Inserted}, updated {Updated}, baselines {Baselines}, skipped {Skipped}, errors {Errors}",
                    result.Inserted, result.Updated, result.BaselinesCreated,
                    result.SkippedRunning, result.Errors.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Playtime snapshot run failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
