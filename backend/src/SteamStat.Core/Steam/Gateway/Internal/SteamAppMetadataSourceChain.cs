namespace SteamStat.Core.Steam.Gateway.Internal;

internal sealed class SteamAppMetadataSourceChain(
    CmAppCatalogSource cmSource,
    HttpStoreSource storeSource) : ISteamAppMetadataSource
{
    public async Task<SteamGatewayResult<Features.Apps.Contracts.SteamAppMetadataSnapshot>> GetAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
    {
        var primary = await cmSource.GetAsync(
            appId, language, preferredAccountName, cancellationToken).ConfigureAwait(false);
        if (primary.IsSuccess) return primary;
        var fallback = await storeSource.GetAsync(
            appId, language, preferredAccountName, cancellationToken).ConfigureAwait(false);
        return fallback.IsSuccess || fallback.Failure == SteamFailureKind.NotFound
            ? fallback
            : primary;
    }
}
