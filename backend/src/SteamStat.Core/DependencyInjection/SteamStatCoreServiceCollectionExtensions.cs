using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Features.Login;
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
        services.AddSingleton<ISteamConnectionFactory>(provider =>
            new SteamConnectionFactory(provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
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
        services.AddSingleton<ISteamSessionAccessor>(provider => provider.GetRequiredService<SteamSessionManager>());
        services.AddSingleton<SteamConnectivityMonitor>();
        services.AddSingleton<ISteamConnectivityMonitor>(provider => provider.GetRequiredService<SteamConnectivityMonitor>());
        services.AddSingleton<SteamHttpRequestQuota>();
        services.AddSingleton<SteamCmOperationScheduler>();
        services.AddSingleton<ISteamCmOperationScheduler>(provider => provider.GetRequiredService<SteamCmOperationScheduler>());
        services.AddSingleton<SteamRequestCoalescer<SteamCacheKey>>();
        services.AddSingleton<ISteamAppMetadataSource, HttpStoreSource>();
        services.AddSingleton<ISteamAppCatalogGateway, SteamAppCatalogGateway>();

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
            SteamStatHttpClients.SteamCommunity,
            SteamDependency.Community,
            new Uri("https://steam-chat.com/"),
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
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.AutomaticDecompression = DecompressionMethods.All;
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
            options.CircuitBreaker.SamplingDuration = accessOptions.CircuitSamplingDuration;
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
