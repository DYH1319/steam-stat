using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamPresenceLocalizationSource
{
    Task<SteamGatewayResult<IReadOnlyDictionary<string, string>>> GetAsync(
        string accountName,
        uint appId,
        string language,
        CancellationToken cancellationToken);
}

internal sealed class CmPresenceLocalizationSource(
    ISteamSessionAccessor sessionAccessor,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmPresenceLocalizationSource> logger) : ISteamPresenceLocalizationSource
{
    public async Task<SteamGatewayResult<IReadOnlyDictionary<string, string>>> GetAsync(
        string accountName,
        uint appId,
        string language,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                SteamFailureKind.AuthenticationRequired, "presence_session_unavailable");
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unifiedMessages == null)
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                SteamFailureKind.Protocol, "presence_handler_unavailable");
        try
        {
            var response = await scheduler.RunAsync(
                accountName,
                "rich-presence-localization",
                session.Generation,
                async _ => await unifiedMessages.SendMessage<
                    CCommunityGetAppRichPresenceLocalizationRequest,
                    CCommunityGetAppRichPresenceLocalizationResponse>(
                    "Community.GetAppRichPresenceLocalization#1",
                    new CCommunityGetAppRichPresenceLocalizationRequest
                    {
                        appid = appId,
                        language = language
                    }),
                cancellationToken).ConfigureAwait(false);
            if (response.Result != EResult.OK)
                return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                    classifier.Classify(response.Result));
            var tokenLists = response.Body?.token_lists ?? [];
            var tokenList = tokenLists.FirstOrDefault(list => list.language == language)
                            ?? tokenLists.FirstOrDefault();
            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tokenList != null)
                foreach (var token in tokenList.tokens)
                    if (!string.IsNullOrEmpty(token.name))
                        tokens[token.name] = token.value ?? string.Empty;
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Succeeded(
                tokens,
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + SteamResourcePolicies.RichPresenceLocalization.RefreshInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch rich presence localization for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                classifier.Classify(exception, cancellationToken));
        }
    }
}
