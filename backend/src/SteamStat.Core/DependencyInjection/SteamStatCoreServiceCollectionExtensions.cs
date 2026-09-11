using System.Net;
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
    public static IServiceCollection AddSteamStatCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

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
        services.AddSingleton<SteamRequestCoalescer<SteamCacheKey>>();
        services.AddSingleton<ISteamAppMetadataSource, HttpStoreSource>();
        services.AddSingleton<ISteamAppCatalogGateway, SteamAppCatalogGateway>();
        ConfigureClient(services.AddHttpClient(SteamStatHttpClients.Download), TimeSpan.FromSeconds(30));
        ConfigureClient(services.AddHttpClient(SteamStatHttpClients.SteamApi), TimeSpan.FromSeconds(15));
        return services;
    }

    private static void ConfigureClient(IHttpClientBuilder builder, TimeSpan timeout)
    {
        builder.ConfigureHttpClient(client => client.Timeout = timeout)
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.AutomaticDecompression = DecompressionMethods.All;
            });
    }
}
