namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatCoreServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient();
        return services;
    }
}
