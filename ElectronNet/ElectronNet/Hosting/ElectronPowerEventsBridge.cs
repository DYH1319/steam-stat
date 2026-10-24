using ElectronNET.API;
using ElectronNet.Jobs;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Hosting;

/// <summary>
/// 将 Electron PowerMonitor 的睡眠/唤醒事件桥接到 UpdateAppRunningStatusJob。
/// </summary>
internal sealed class ElectronPowerEventsBridge(
    UpdateAppRunningStatusJob runningStatusJob,
    ILogger<ElectronPowerEventsBridge> logger)
{
    internal void Attach()
    {
        Electron.PowerMonitor.OnSuspend += () =>
        {
            _ = HandleSuspendSafelyAsync();
        };
        Electron.PowerMonitor.OnResume += () =>
        {
            try
            {
                runningStatusJob.HandleResume();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to handle system resume");
            }
        };
    }

    private async Task HandleSuspendSafelyAsync()
    {
        try
        {
            await runningStatusJob.HandleSuspendAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to handle system suspend");
        }
    }
}
