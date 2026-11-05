using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Http;
using SteamStat.Core.Settings;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Session;
using SteamStat.Core.Steam.Session.Internal;

namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatCoreServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatCore(
        this IServiceCollection services,
        Action<SteamAccessOptions>? configureAccess = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var accessOptions = new SteamAccessOptions();
        configureAccess?.Invoke(accessOptions);
        services.AddSingleton(accessOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppSettingsFactory, AppSettingsFactory>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<SettingsCoordinator>();
        services.AddSingleton<SteamResultClassifier>();
        services.AddSingleton(provider => new SteamReconnectPolicy(provider.GetRequiredService<SteamResultClassifier>()));
        services.AddSingleton<SteamCredentialStore>();
        services.AddSingleton<SteamNetworkProxy>();
        services.AddSingleton<SteamNetworkController>();
        services.AddSingleton<ISteamNetworkController>(provider => provider.GetRequiredService<SteamNetworkController>());
        services.AddSingleton<SteamNetworkDiagnostics>();
        services.AddSingleton<ISteamConnectionFactory>(provider =>
            new SteamConnectionFactory(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                provider.GetRequiredService<SteamNetworkProxy>(),
                provider.GetRequiredService<SteamAccessOptions>()));
        services.AddSingleton<INetworkAvailability>(_ => new SystemNetworkAvailability());
        services.AddSingleton(provider => new SteamSessionManager(
            provider.GetRequiredService<SteamStat.Core.Events.IEventBus>(),
            provider.GetRequiredService<SteamCredentialStore>(),
            provider.GetRequiredService<ISteamConnectionFactory>(),
            provider.GetRequiredService<INetworkAvailability>(),
            provider.GetRequiredService<SteamReconnectPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SteamSessionManager>>()));
        services.AddSingleton<ISteamSessionManager>(provider => provider.GetRequiredService<SteamSessionManager>());
        services.AddSingleton<ISteamSessionStatusProvider>(provider => provider.GetRequiredService<SteamSessionManager>());
        services.AddSingleton<ISteamSessionAccessor>(provider => provider.GetRequiredService<SteamSessionManager>());
        services.AddSingleton<ISteamAccountIdentityProvider, SteamAccountIdentityProvider>();
        services.AddSingleton<SteamConnectivityMonitor>();
        services.AddSingleton<ISteamConnectivityMonitor>(provider => provider.GetRequiredService<SteamConnectivityMonitor>());
        services.AddSingleton<SteamHttpRequestQuota>();
        services.AddSingleton<SteamCmOperationScheduler>();
        services.AddSingleton<ISteamCmOperationScheduler>(provider => provider.GetRequiredService<SteamCmOperationScheduler>());
        services.AddSingleton<SteamRequestCoalescer<SteamCacheKey>>();
        services.AddSingleton<SteamFeatureSnapshotStore>();
        services.AddSingleton<SteamStat.Core.Steam.SteamOperationalStatusService>();
        services.AddSingleton<CmAppCatalogSource>();
        services.AddSingleton<HttpStoreSource>();
        services.AddSingleton<ISteamAppMetadataSource, SteamAppMetadataSourceChain>();
        services.AddSingleton<ISteamAppCatalogGateway, SteamAppCatalogGateway>();
        services.AddSingleton<ISteamProfileSource, CmProfileSource>();
        services.AddSingleton<ISteamPublicProfileSource, HttpPublicProfileSource>();
        services.AddSingleton<ISteamProfileGateway, SteamProfileGateway>();
        services.AddSingleton<ISteamAvatarUriProvider, SteamAvatarUriProvider>();
        services.AddSingleton<ISteamWishlistSource, HttpWishlistSource>();
        services.AddSingleton<ISteamWishlistGateway, SteamWishlistGateway>();
        services.AddSingleton<ISteamLibrarySource, CmLibrarySource>();
        services.AddSingleton<ISteamLibraryGateway, SteamLibraryGateway>();
        services.AddSingleton<ISteamLicenseGateway, CmLicenseSource>();
        services.AddSingleton<ISteamPresenceLocalizationSource, CmPresenceLocalizationSource>();
        services.AddSingleton<ISteamPresenceLocalizationGateway, SteamPresenceLocalizationGateway>();
        services.AddSingleton<IAchievementSchemaSource, CmAchievementSchemaSource>();
        services.TryAddSingleton<SteamStat.Core.PublicData.IPublicAchievementSchemaSource, SteamStat.Core.PublicData.NullPublicAchievementSchemaSource>();
        services.AddSingleton<ISteamAchievementSchemaGateway, SteamAchievementSchemaGateway>();
        services.AddSingleton<IAchievementProgressSource, CmAchievementProgressSource>();
        services.AddSingleton<ISteamAchievementProgressGateway, SteamAchievementProgressGateway>();
        services.AddSingleton<SteamAchievementOverviewQuery>();
        services.AddSingleton<SteamAchievementsService>();
        services.AddSingleton<ISteamPresenceFeed, SteamKitPresenceFeed>();
        services.AddSingleton<ISteamFriendProfileGateway, CmFriendProfileSource>();
        services.AddSingleton<SteamFriendCompareService>();
        services.AddSingleton<SteamLoginService>();
        services.AddSingleton<SteamLibraryService>();
        services.AddSingleton<IOwnedGameCatalog, SteamOwnedGameCatalog>();
        services.AddSingleton<SteamFriendsService>();
        services.AddSingleton<SteamStat.Core.Events.IEventHandler<SteamStat.Core.Events.SteamSessionReady>>(
            provider => provider.GetRequiredService<SteamFriendsService>());
        services.AddSingleton<SteamStat.Core.Events.IEventHandler<SteamStat.Core.Events.SteamSessionEnded>>(
            provider => provider.GetRequiredService<SteamFriendsService>());

        ConfigureClient(
            services,
            SteamStatHttpClients.SteamStore,
            SteamDependency.Store,
            new Uri("https://store.steampowered.com/"),
            accessOptions,
            retryDownloads: true);
        ConfigureClient(
            services,
            SteamStatHttpClients.SteamWebApi,
            SteamDependency.SteamWebApi,
            new Uri("https://api.steampowered.com/"),
            accessOptions,
            retryDownloads: true);
        ConfigureClient(
            services,
            SteamStatHttpClients.SteamCdn,
            SteamDependency.Cdn,
            new Uri("https://avatars.akamai.steamstatic.com/"),
            accessOptions,
            retryDownloads: false);
        ConfigureClient(
            services,
            SteamStatHttpClients.SteamCommunity,
            SteamDependency.Community,
            new Uri("https://steamcommunity.com/"),
            accessOptions,
            retryDownloads: false);
        // 同一份公开资料的备用域名。单独一条管道，主域名熔断时不受影响
        ConfigureClient(
            services,
            SteamStatHttpClients.SteamChat,
            SteamDependency.Community,
            new Uri("https://steam-chat.com/"),
            accessOptions,
            retryDownloads: false);
        ConfigureClient(
            services,
            SteamStatHttpClients.Download,
            SteamDependency.Download,
            null,
            accessOptions,
            retryDownloads: false);
        return services;
    }

    private static void ConfigureClient(
        IServiceCollection services,
        string name,
        SteamDependency dependency,
        Uri? baseAddress,
        SteamAccessOptions accessOptions,
        bool retryDownloads)
    {
        var builder = services.AddHttpClient(name, client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStat/1.4");
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    dependency is SteamDependency.Cdn or SteamDependency.Download ? "*/*" : "application/json"));
            })
            .UseSocketsHttpHandler((handler, provider) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.AutomaticDecompression = DecompressionMethods.All;
                // 动态代理：设置页修改代理后对新请求立即生效（连接池按代理地址分区）
                handler.Proxy = provider.GetRequiredService<SteamNetworkProxy>();
                handler.UseProxy = true;
            })
            .AddHttpMessageHandler(provider => new SteamHttpQuotaHandler(
                dependency,
                provider.GetRequiredService<SteamHttpRequestQuota>(),
                provider.GetRequiredService<ISteamConnectivityMonitor>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SteamHttpQuotaHandler>>()))
            .AddHttpMessageHandler(provider => new SteamHttpObservationHandler(
                dependency,
                provider.GetRequiredService<SteamResultClassifier>(),
                provider.GetRequiredService<ISteamConnectivityMonitor>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SteamHttpObservationHandler>>()));

        builder.AddStandardResilienceHandler(options =>
        {
            var downloads = dependency is SteamDependency.Cdn or SteamDependency.Download;
            options.TotalRequestTimeout.Timeout = downloads
                ? accessOptions.DownloadTotalTimeout
                : accessOptions.JsonTotalTimeout;
            options.AttemptTimeout.Timeout = downloads
                ? accessOptions.DownloadAttemptTimeout
                : accessOptions.JsonAttemptTimeout;
            options.RateLimiter.DefaultRateLimiterOptions.PermitLimit = downloads
                ? accessOptions.CdnConcurrencyLimit
                : accessOptions.HttpConcurrencyLimit;
            options.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 0;
            options.CircuitBreaker.FailureRatio = accessOptions.CircuitFailureRatio;
            options.CircuitBreaker.MinimumThroughput = accessOptions.CircuitMinimumThroughput;
            // 采样窗口不得小于单次尝试超时的两倍（Polly 选项校验要求），调大超时时自动放宽
            var minimumSampling = options.AttemptTimeout.Timeout * 2;
            options.CircuitBreaker.SamplingDuration = accessOptions.CircuitSamplingDuration < minimumSampling
                ? minimumSampling
                : accessOptions.CircuitSamplingDuration;
            options.CircuitBreaker.BreakDuration = accessOptions.CircuitBreakDuration;
            options.Retry.MaxRetryAttempts = accessOptions.RetryCount;
            options.Retry.Delay = accessOptions.RetryDelay;
            options.Retry.ShouldRetryAfterHeader = true;
            options.Retry.DisableForUnsafeHttpMethods();
            options.Retry.OnRetry = _ =>
            {
                SteamTelemetry.RecordRetry(dependency);
                return default;
            };
            if (!retryDownloads)
                options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
        });
    }
}
