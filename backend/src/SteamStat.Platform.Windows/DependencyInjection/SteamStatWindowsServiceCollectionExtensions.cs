namespace Microsoft.Extensions.DependencyInjection;

public static class SteamStatWindowsServiceCollectionExtensions
{
    public static IServiceCollection AddSteamStatWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
