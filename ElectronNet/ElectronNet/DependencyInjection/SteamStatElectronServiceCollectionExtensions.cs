using ElectronNet.Features.Login.Persistence;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Jobs;
using ElectronNet.Persistence;
using ElectronNet.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Sessions;
using SteamStat.Core.Settings;

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
        services.AddSingleton<IAutoStartManager, ElectronAutoStartManager>();
        services.AddSingleton<IWindowPreferences, ElectronWindowPreferences>();
        services.AddSingleton<IUpdaterController, ElectronUpdaterController>();
        services.AddSingleton<UpdateAppRunningStatusJob>();
        services.AddSingleton<IAppRunningStatusJobController>(provider => provider.GetRequiredService<UpdateAppRunningStatusJob>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<UpdateAppRunningStatusJob>());
        services.AddSingleton<SteamAppMetadataService>();
        services.AddSingleton<IAppNameResolver>(provider => provider.GetRequiredService<SteamAppMetadataService>());
        services.AddSingleton<IAppMetadataWriter>(provider => provider.GetRequiredService<SteamAppMetadataService>());
        services.AddSingleton<SteamLanguageProvider>();
        services.AddSingleton<ILanguageProvider>(provider => provider.GetRequiredService<SteamLanguageProvider>());
        services.AddSingleton<SteamRichPresenceResolver>();
        services.AddSingleton<IRichPresenceResolver>(provider => provider.GetRequiredService<SteamRichPresenceResolver>());
        services.AddSingleton<FriendStatusRecordService>();
        services.AddSingleton<IFriendStatusRecorder>(provider => provider.GetRequiredService<FriendStatusRecordService>());
        services.AddSingleton<ISteamLoginTokenStore, SteamLoginTokenStore>();
        services.AddSingleton<SteamLoginService>();
        services.AddSingleton<ISteamSessionAccessor>(provider => provider.GetRequiredService<SteamLoginService>());
        services.AddSingleton<SteamLibraryService>();
        services.AddSingleton<IEventHandler<SteamSessionEnded>>(provider => provider.GetRequiredService<SteamLibraryService>());
        services.AddSingleton<SteamFriendsService>();
        services.AddSingleton<IEventHandler<SteamSessionReady>>(provider => provider.GetRequiredService<SteamFriendsService>());
        services.AddSingleton<IEventHandler<SteamSessionEnded>>(provider => provider.GetRequiredService<SteamFriendsService>());
        services.AddSingleton<IpcRequestBinder>();
        services.AddSingleton<ShellIpcPolicy>();
        services.AddSingleton<IpcMainService>();
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton(provider => new ApplicationCleanupService(
            () => ElectronNet.Program.Cleanup(provider.GetRequiredService<SteamLoginService>())));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ApplicationCleanupService>());
        return services;
    }
}
