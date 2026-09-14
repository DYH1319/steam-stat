using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal sealed class CmAppCatalogSource(
    ISteamSessionAccessor sessionAccessor,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmAppCatalogSource> logger) : ISteamAppMetadataSource
{
    public async Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
    {
        if (!TryGetSession(preferredAccountName, out var accountName, out var session))
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, "pics_session_unavailable");
        var apps = session.Client.GetHandler<SteamApps>();
        if (apps == null)
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.Protocol, "pics_handler_unavailable");
        try
        {
            var tokens = await scheduler.RunAsync(
                accountName,
                "pics-access-tokens",
                session.Generation,
                async _ => await apps.PICSGetAccessTokens([appId], []),
                cancellationToken).ConfigureAwait(false);
            var tokenOutcome = PicsResponseSemantics.ClassifyAccessToken(
                appId, tokens.AppTokens, tokens.AppTokensDenied.ToHashSet());
            if (tokenOutcome == PicsAppOutcome.AccessDenied)
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.Forbidden, "pics_access_denied");
            if (tokenOutcome != PicsAppOutcome.Success)
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.Protocol, "pics_token_incomplete");

            var products = await scheduler.RunAsync(
                accountName,
                "pics-product-info",
                session.Generation,
                async _ => await apps.PICSGetProductInfo(
                    [new SteamApps.PICSRequest(appId, tokens.AppTokens[appId])], [], false),
                cancellationToken).ConfigureAwait(false);
            var results = products.Results ?? [];
            var batches = results.Select(result => new PicsProductInfoBatch(
                result.ResponsePending,
                result.Apps.ToDictionary(pair => pair.Key, pair => pair.Value.MissingToken),
                result.UnknownApps.ToHashSet())).ToArray();
            var outcome = PicsResponseSemantics.ClassifyProductInfo(
                appId, new PicsProductInfoResultSet(products.Complete, products.Failed, batches));
            if (outcome == PicsAppOutcome.AccessDenied)
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.Forbidden, "pics_access_denied");
            if (outcome == PicsAppOutcome.NotFound)
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.NotFound, "pics_app_not_found");
            if (outcome != PicsAppOutcome.Success)
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.Protocol, "pics_product_incomplete");

            var product = results.SelectMany(result => result.Apps)
                .FirstOrDefault(pair => pair.Key == appId).Value;
            var common = product?.KeyValues?["common"];
            var name = common?["name"].AsString();
            if (string.IsNullOrWhiteSpace(name))
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.InvalidData, "pics_invalid_payload");
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
                new SteamAppMetadataSnapshot(
                    appId,
                    name,
                    common?["type"].AsString(),
                    common?["is_free"].AsBoolean(false) ?? false),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + SteamResourcePolicies.AppMetadata.RefreshInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch PICS app metadata for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                classifier.Classify(exception, cancellationToken));
        }
    }

    private bool TryGetSession(
        string? preferredAccountName,
        out string accountName,
        out ISteamSession session)
    {
        if (!string.IsNullOrWhiteSpace(preferredAccountName)
            && sessionAccessor.TryGetSession(preferredAccountName, out session))
        {
            accountName = preferredAccountName;
            return true;
        }
        foreach (var candidate in sessionAccessor.GetLoggedInUsers())
        {
            if (!sessionAccessor.TryGetSession(candidate, out session)) continue;
            accountName = candidate;
            return true;
        }
        accountName = string.Empty;
        session = null!;
        return false;
    }
}
