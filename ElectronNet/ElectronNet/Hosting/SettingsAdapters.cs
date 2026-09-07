using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Infrastructure;
using ElectronNet.Services;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Hosting;

internal sealed class ElectronAutoStartManager(
    AppEnvironment environment,
    ILogger<ElectronAutoStartManager> logger) : IAutoStartManager
{
    public async Task ApplyAsync(bool autoStart, bool silentStart, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (environment.IsDevelopment)
        {
            logger.LogWarning("Skip setting auto start because the application is not packed");
            return;
        }

        Electron.App.SetLoginItemSettings(new LoginSettings
        {
            OpenAtLogin = autoStart,
            Path = (await Electron.App.GetPathAsync(PathName.Exe)).Replace(@"\electron", ""),
            Args = silentStart ? ["--silent-start"] : []
        });
    }
}

internal sealed class ElectronWindowPreferences(MainWindowAccessor mainWindowAccessor) : IWindowPreferences
{
    public void SetZoomFactor(double zoomFactor)
        => mainWindowAccessor.Window?.WebContents.SetZoomFactor(zoomFactor);
}

internal sealed class ElectronUpdaterController(UpdateService updateService) : IUpdaterController
{
    public void SetAutoUpdateEnabled(bool enabled) => updateService.SetAutoUpdateEnabled(enabled);
}
