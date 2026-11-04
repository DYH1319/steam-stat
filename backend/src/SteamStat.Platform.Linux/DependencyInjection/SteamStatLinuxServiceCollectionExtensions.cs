using SteamStat.Core.Platform;
using SteamStat.Platform.Linux;

namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatLinuxServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Linux / Steam Deck 桌面模式的平台实现：registry.vdf + /proc 安装定位、libsecret 凭证保护、
    /// 进程控制与 X11 桌面活动探针。
    /// </summary>
    public static IServiceCollection AddSteamStatLinux(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
#pragma warning disable CA1416
        services.AddSingleton(_ => LinuxSystemContext.Current());
        services.AddSingleton<ICredentialKeyring, LibSecretKeyring>();
        services.AddSingleton<ISecretStore, LibSecretSecretStore>();
        services.AddSingleton<ISteamInstallLocator, LinuxSteamInstallLocator>();
        services.AddSingleton<IProcessController, LinuxProcessController>();
        services.AddSingleton<IDesktopActivityProbe, LinuxDesktopActivityProbe>();
#pragma warning restore CA1416
        return services;
    }
}
