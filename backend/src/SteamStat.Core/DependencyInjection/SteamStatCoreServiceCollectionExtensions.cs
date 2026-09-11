using System.Net;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Http;
using SteamStat.Core.Settings;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Steam.Session;

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
