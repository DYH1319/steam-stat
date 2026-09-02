using SteamStat.Core.Platform;
using SteamStat.Platform.Windows;

namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatWindowsServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
#pragma warning disable CA1416
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<ISteamInstallLocator, SteamInstallLocator>();
        services.AddSingleton<IProcessController, WindowsProcessController>();
#pragma warning restore CA1416
        return services;
    }
}
