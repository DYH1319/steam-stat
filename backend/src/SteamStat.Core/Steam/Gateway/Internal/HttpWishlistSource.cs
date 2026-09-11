using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamWishlistSource
{
    Task<SteamGatewayResult<IReadOnlyList<uint>>> GetAsync(
        ulong steamId,
        CancellationToken cancellationToken);
}

internal sealed class HttpWishlistSource(
    IHttpClientFactory httpClientFactory,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<HttpWishlistSource> logger) : ISteamWishlistSource
{
    public async Task<SteamGatewayResult<IReadOnlyList<uint>>> GetAsync(
        ulong steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamWebApi);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"IWishlistService/GetWishlist/v1/?steamid={steamId}");
            SteamHttpRequestOptions.SetOperation(request, "wishlist");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SteamGatewayResult<IReadOnlyList<uint>>.Failed(classifier.Classify(response.StatusCode));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            IReadOnlyList<uint> appIds = [];
            if (document.RootElement.TryGetProperty("response", out var root)
                && root.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
                appIds = items.EnumerateArray()
                    .Where(item => item.TryGetProperty("appid", out var appId)
                                   && appId.TryGetUInt32(out _))
                    .Select(item => item.GetProperty("appid").GetUInt32())
                    .Distinct()
                    .ToArray();
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
                appIds,
                SteamDataSource.Http,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + SteamResourcePolicies.Wishlist.RefreshInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch Steam wishlist with {ExceptionType}",
                exception.GetType().Name);
            return SteamGatewayResult<IReadOnlyList<uint>>.Failed(
                classifier.Classify(exception, cancellationToken));
        }
    }
}
