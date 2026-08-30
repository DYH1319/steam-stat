using ElectronNet.Hosting;
using ElectronNet.Services;
using Microsoft.Extensions.Hosting;
using SteamStat.Core.Environment;

namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatElectronServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatElectron(this IServiceCollection services, AppEnvironment appEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appEnvironment);

        services.AddSingleton(appEnvironment);
        services.AddSingleton<IAppPaths>(appEnvironment.Paths);
        services.AddSingleton<IpcMainService>();
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton(_ => new ApplicationCleanupService(ElectronNet.Program.Cleanup));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ApplicationCleanupService>());
        return services;
    }
}
