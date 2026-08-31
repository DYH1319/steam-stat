using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Persistence;
using ElectronNet.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;

namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatElectronServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatElectron(this IServiceCollection services, AppEnvironment appEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appEnvironment);

        services.AddSingleton(appEnvironment);
        services.AddSingleton(appEnvironment.Paths);
        Directory.CreateDirectory(appEnvironment.Paths.DatabaseDirectory);
        services.AddDbContextFactory<ElectronNet.AppDbContext>((provider, options) =>
        {
            var appPaths = provider.GetRequiredService<IAppPaths>();
            options.UseSqlite(SqliteConnectionStrings.Create(appPaths.DatabaseFile));
        });
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<MainWindowAccessor>();
        services.AddSingleton<IMainWindowAccessor>(provider => provider.GetRequiredService<MainWindowAccessor>());
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<ElectronIpcEventForwarder>();
        services.AddSingleton<IEventHandler<LoginUsersChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<SteamLoginProgressChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<FriendsChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<UpdaterStateChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<FriendsSessionEventHandler>();
        services.AddSingleton<IEventHandler<SteamSessionDisconnected>>(provider => provider.GetRequiredService<FriendsSessionEventHandler>());
        services.AddSingleton<IEventHandler<SteamSessionReconnected>>(provider => provider.GetRequiredService<FriendsSessionEventHandler>());
        services.AddSingleton<IpcMainService>();
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton(_ => new ApplicationCleanupService(ElectronNet.Program.Cleanup));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ApplicationCleanupService>());
        return services;
    }
}
