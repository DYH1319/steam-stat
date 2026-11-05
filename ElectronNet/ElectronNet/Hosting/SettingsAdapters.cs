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
    AppDistribution distribution,
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

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var file = LinuxAutoStart.AutostartFile(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                LinuxAutoStart.Apply(file, LinuxAutoStart.LaunchCommand(distribution, Environment.GetEnvironmentVariable, Environment.ProcessPath), autoStart, silentStart, LinuxAutoStart.IconFor(distribution));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to update the XDG autostart entry");
            }
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

internal sealed class ElectronWindowPreferences(MainWindowAccessor mainWindowAccessor, PopoutWindowService popouts) : IWindowPreferences
{
    public void SetZoomFactor(double zoomFactor)
    {
        mainWindowAccessor.Window?.WebContents.SetZoomFactor(zoomFactor);
        popouts.SetZoomFactor(zoomFactor);
    }

    public void SetWindowMaterial(string material)
    {
        if (mainWindowAccessor.Window is { } window) WindowMaterials.Apply(window, material);
        popouts.SetWindowMaterial(material);
    }
}

internal sealed class ElectronUpdaterController(UpdateService updateService) : IUpdaterController
{
    public void SetAutoUpdateEnabled(bool enabled) => updateService.SetAutoUpdateEnabled(enabled);
    public void SetUpdateChannel(string channel) => updateService.SetChannel(channel);
}
