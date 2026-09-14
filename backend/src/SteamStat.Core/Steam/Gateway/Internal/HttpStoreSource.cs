using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamAppMetadataSource
{
    Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken);
}

internal sealed class HttpStoreSource(
    IHttpClientFactory httpClientFactory,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<HttpStoreSource> logger) : ISteamAppMetadataSource
{
    public async Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamStore);
            var languageQuery = string.IsNullOrWhiteSpace(language)
                ? string.Empty
                : $"&l={Uri.EscapeDataString(language.Trim())}";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/appdetails?appids={appId}&filters=basic{languageQuery}");
            SteamHttpRequestOptions.SetOperation(request, "appdetails");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(classifier.Classify(response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty(appId.ToString(), out var appElement)
                || !appElement.TryGetProperty("success", out var successElement)
                || successElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.InvalidData, "store_invalid_payload");
            if (!successElement.GetBoolean())
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.NotFound, "store_app_not_found");
            if (!appElement.TryGetProperty("data", out var dataElement)
                || !dataElement.TryGetProperty("name", out var nameElement)
                || string.IsNullOrWhiteSpace(nameElement.GetString()))
                return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                    SteamFailureKind.InvalidData, "store_invalid_payload");

            var fetchedAt = timeProvider.GetUtcNow();
            var snapshot = new SteamAppMetadataSnapshot(
                appId,
                nameElement.GetString()!,
                dataElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null,
                dataElement.TryGetProperty("is_free", out var freeElement)
                && freeElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && freeElement.GetBoolean());
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
                snapshot,
                SteamDataSource.Http,
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
                "Failed to fetch app metadata for {AppId} with {ExceptionType}",
                appId, exception.GetType().Name);
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(classifier.Classify(exception, cancellationToken));
        }
    }
}
