using ElectronNet.Features.Diagnostics;
using ElectronNet.Features.Login.Persistence;
using ElectronNet.Features.SteamCache.Persistence;
using ElectronNet.Helpers;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Jobs;
using ElectronNet.Persistence;
using ElectronNet.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Steam.Session;
using SteamStat.Core.Settings;
using SteamStat.Core.Steam.Cache;

namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatElectronServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatElectron(this IServiceCollection services, AppEnvironment appEnvironment, AppDistribution? distribution = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appEnvironment);

        services.AddSingleton(appEnvironment);
        services.AddSingleton(distribution ?? (appEnvironment.IsDevelopment ? AppDistribution.Development : AppDistribution.FromKind("installer", null, null)));
        services.AddSingleton(appEnvironment.Paths);
        Directory.CreateDirectory(appEnvironment.Paths.DatabaseDirectory);
        services.AddDbContextFactory<ElectronNet.AppDbContext>((provider, options) =>
        {
            var appPaths = provider.GetRequiredService<IAppPaths>();
            options.UseSqlite(SqliteConnectionStrings.Create(appPaths.DatabaseFile));
        });
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<DatabaseSnapshotter>();
        services.AddSingleton<PendingRestoreApplier>();
        services.AddSingleton<CredentialStorageScrubber>();
        services.AddSingleton<ISteamResourceCacheStore, EfSteamResourceCacheStore>();
        services.AddSingleton<MainWindowAccessor>();
        services.AddSingleton<IMainWindowAccessor>(provider => provider.GetRequiredService<MainWindowAccessor>());
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<ElectronIpcEventForwarder>();
        services.AddSingleton<IEventHandler<LoginUsersChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<SteamLoginProgressChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<FriendsChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<UpdaterStateChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<AchievementArchiveSyncChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<TrayCommandIssued>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IEventHandler<SaveBackupProgressChanged>>(provider => provider.GetRequiredService<ElectronIpcEventForwarder>());
        services.AddSingleton<IAutoStartManager, ElectronAutoStartManager>();
        services.AddSingleton<IWindowPreferences, ElectronWindowPreferences>();
        services.AddSingleton<IElectronAutoUpdater, ElectronAutoUpdater>();
        services.AddSingleton<IShellLauncher, ElectronShellLauncher>();
        services.AddSingleton<IReleaseFeedClient, GitHubReleaseFeedClient>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdaterController, ElectronUpdaterController>();
        services.AddSingleton<LocalFileService>();
        services.AddSingleton<LocalDataSourceService>();
        services.AddSingleton<FileHelper>();
        services.AddSingleton<GlobalStatusService>();
        services.AddSingleton<SteamAppService>();
        services.AddSingleton<UseAppRecordService>();
        services.AddSingleton<DeviceService>();
        services.AddSingleton<SessionManagementService>();
        services.AddSingleton<PlaytimeHistoryService>();
        services.AddSingleton<SessionImportService>();
        services.AddSingleton<StatsDatasetService>();
        services.AddSingleton<ElectronPowerEventsBridge>();
        services.AddSingleton<SteamService>();
        services.AddSingleton<SteamUserService>();
        services.AddSingleton<SteamAppMetadataService>();
        services.AddSingleton<IAppNameResolver>(provider => provider.GetRequiredService<SteamAppMetadataService>());
        services.AddSingleton<IAppMetadataWriter>(provider => provider.GetRequiredService<SteamAppMetadataService>());
        services.AddSingleton<SteamLanguageProvider>();
        services.AddSingleton<ILanguageProvider>(provider => provider.GetRequiredService<SteamLanguageProvider>());
        services.AddSingleton<SteamRichPresenceResolver>();
        services.AddSingleton<IRichPresenceResolver>(provider => provider.GetRequiredService<SteamRichPresenceResolver>());
        services.AddSingleton<FriendStatusRecordService>();
        services.AddSingleton<IFriendStatusRecorder>(provider => provider.GetRequiredService<FriendStatusRecordService>());
        services.AddSingleton<AchievementArchiveService>();
        services.AddSingleton<LibraryArchiveService>();
        services.AddSingleton<ScreenshotArchiveService>();
        services.AddSingleton<SaveBackupService>();
        services.AddSingleton<SaveAnalysisService>();
        services.AddSingleton<ProfileHistoryService>();
        services.AddSingleton<RevealRegistry>();
        services.AddSingleton<DataExportService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<IEventHandler<UsageSessionEnded>>(provider => provider.GetRequiredService<SaveBackupService>());
        services.AddSingleton<IEventHandler<UsageSessionEnded>>(provider => provider.GetRequiredService<ScreenshotArchiveService>());
        services.AddSingleton<IEventHandler<FriendsChanged>>(provider => provider.GetRequiredService<ProfileHistoryService>());
        services.AddSingleton<IEventHandler<SteamSessionReady>>(provider => provider.GetRequiredService<ProfileHistoryService>());
        services.AddSingleton<SteamAppImagesService>();
        services.AddSingleton<ISteamLoginTokenStore, SteamLoginTokenStore>();
        services.AddSingleton<ApplicationCleanupService>(provider => new ApplicationCleanupService(
            () => ElectronNet.Program.Cleanup(
                provider.GetRequiredService<SteamLoginService>(),
                provider.GetRequiredService<ILogger<ElectronNet.Program>>())));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ApplicationCleanupService>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<UpdateService>());
        services.AddSingleton<UpdateAppRunningStatusJob>();
        services.AddSingleton<IAppRunningStatusJobController>(provider => provider.GetRequiredService<UpdateAppRunningStatusJob>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<UpdateAppRunningStatusJob>());
        services.AddSingleton<TrayService>();
        services.AddSingleton<DesktopNotificationService>();
        services.AddSingleton<DiagnosticsRedactionProvider>();
        services.AddSingleton<ICrashReportSink, GitHubIssueCrashReportSink>();
        // 自动上报只走用户配置的代理，不经 Steam 的配额 / 连通性观测管道
        services.AddHttpClient(SentryCrashReportUploader.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(provider => new System.Net.Http.SocketsHttpHandler
            {
                Proxy = provider.GetRequiredService<SteamStat.Core.Http.SteamNetworkProxy>(),
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddSingleton<ICrashReportUploader, SentryCrashReportUploader>();
        services.AddSingleton<CrashReportService>();
        services.AddSingleton<ICrashReportController>(provider => provider.GetRequiredService<CrashReportService>());
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<IpcRequestBinder>();
        services.AddSingleton<ShellIpcPolicy>();
        services.AddSingleton<IpcMainService>();
        services.AddSingleton<RendererProxySync>();
        services.AddSingleton<ApplicationStartupCoordinator>();
        return services;
    }
}
