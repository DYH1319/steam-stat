using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamPublicProfileSource
{
    Task<SteamGatewayResult<SteamPublicProfileSnapshot>> GetAsync(ulong steamId, CancellationToken cancellationToken);
}

/// <summary>
/// 公开的 miniprofile：不需要登录，只提供等级、头像框和动态头像这类装饰信息。
/// steamcommunity.com 不通时改用 steam-chat.com。两个域名在部分网络下都可能不通，
/// 核心资料不能依赖它，失败时调用方保持现有数据即可。
/// </summary>
internal sealed class HttpPublicProfileSource(
    IHttpClientFactory httpClientFactory,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<HttpPublicProfileSource> logger) : ISteamPublicProfileSource
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex AlternateCdn = new(
        @"^https://([a-z0-9-]+)\.(?:fastly|cloudflare)\.steamstatic\.com/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private volatile string _preferredClient = SteamStatHttpClients.SteamCommunity;

    public async Task<SteamGatewayResult<SteamPublicProfileSnapshot>> GetAsync(
        ulong steamId,
        CancellationToken cancellationToken)
    {
        var preferred = _preferredClient;
        var fallback = preferred == SteamStatHttpClients.SteamCommunity
            ? SteamStatHttpClients.SteamChat
            : SteamStatHttpClients.SteamCommunity;
        var result = await RequestAsync(preferred, steamId, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) return result;

        var retried = await RequestAsync(fallback, steamId, cancellationToken).ConfigureAwait(false);
        if (!retried.IsSuccess) return result;
        // 记住能用的域名，后面的账号不用再等一次超时
        _preferredClient = fallback;
        return retried;
    }

    private async Task<SteamGatewayResult<SteamPublicProfileSnapshot>> RequestAsync(
        string clientName,
        ulong steamId,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(RequestTimeout);
        try
        {
            using var client = httpClientFactory.CreateClient(clientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"miniprofile/{(uint)steamId}/json");
            SteamHttpRequestOptions.SetOperation(request, "miniprofile");
            using var response = await client.SendAsync(request, attempt.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SteamGatewayResult<SteamPublicProfileSnapshot>.Failed(classifier.Classify(response.StatusCode));
            var json = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamPublicProfileSnapshot>.Succeeded(
                Parse(json),
                SteamDataSource.Http,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + RefreshInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                "Public Steam profile is unavailable from {Client}: {ExceptionType}",
                clientName,
                exception.GetType().Name);
            return SteamGatewayResult<SteamPublicProfileSnapshot>.Failed(
                classifier.Classify(exception, cancellationToken));
        }
    }

    internal static SteamPublicProfileSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int? level = root.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsed)
            ? parsed
            : null;
        return new SteamPublicProfileSnapshot(
            level,
            ReadString(root, "level_class"),
            ToSteamAssetUrl(ReadString(root, "avatar_frame")),
            ToSteamAssetUrl(ReadString(root, "animated_avatar")));
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 地址来自响应内容，只接受 Steam 自己的静态资源。这些常是几 MB 的动画，
    /// Fastly / Cloudflare 节点在国内很慢，同路径的文件换到 Akamai 下载。
    /// </summary>
    private static string? ToSteamAssetUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            ? AlternateCdn.Replace(value!, "https://$1.akamai.steamstatic.com/")
            : null;
}
