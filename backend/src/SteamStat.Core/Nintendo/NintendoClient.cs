using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SteamStat.Core.Http;

namespace SteamStat.Core.Nintendo;

/// <summary>session token 失效、被撤销或换不出令牌：只能重新登录。</summary>
public sealed class NintendoAuthException(string message) : Exception(message);

/// <summary>任天堂的回复不是预期的样子（接口改了）。</summary>
public sealed class NintendoFormatException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record NintendoTokens(string AccessToken, string? IdToken);

public sealed record NintendoUser(string Id, string Nickname, string? IconUrl, string? Country);

/// <summary>
/// 任天堂账号和游玩记录的客户端。只往 <see cref="NintendoEndpoints"/> 列出的地址发请求，令牌不进日志。
/// </summary>
public sealed class NintendoClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = SteamStatHttpClients.Nintendo;
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>用一次性的 session_token_code 换长期的 session token（约两年有效）。</summary>
    public async Task<string> ExchangeSessionTokenAsync(string code, string verifier, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, NintendoEndpoints.SessionToken, NintendoEndpoints.AccountsUserAgent);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = NintendoEndpoints.ClientId,
            ["session_token_code"] = code,
            ["session_token_code_verifier"] = verifier
        });
        using var document = await SendAsync(request, authEndpoint: true, cancellationToken).ConfigureAwait(false);
        return Required(document.RootElement, "session_token");
    }

    public async Task<NintendoTokens> GetTokensAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, NintendoEndpoints.Token, NintendoEndpoints.AccountsUserAgent);
        request.Content = JsonContent.Create(new Dictionary<string, string>
        {
            ["client_id"] = NintendoEndpoints.ClientId,
            ["session_token"] = sessionToken,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer-session-token"
        });
        using var document = await SendAsync(request, authEndpoint: true, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new NintendoTokens(Required(root, "access_token"), Optional(root, "id_token"));
    }

    public async Task<NintendoUser> GetUserAsync(NintendoTokens tokens, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, NintendoEndpoints.Me, NintendoEndpoints.AccountsUserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var document = await SendAsync(request, authEndpoint: false, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var id = Required(root, "id").Trim().ToLowerInvariant();
        if (id.Length is 0 or > 32 || !id.All(char.IsAsciiLetterOrDigit)) throw new NintendoFormatException("unexpected account id");
        var nickname = Optional(root, "nickname")?.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, 64)] : id;
        var icon = Optional(root, "iconUri") is { } iconUri && Uri.TryCreate(iconUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
        var country = Optional(root, "country") is { Length: <= 8 } code ? code : null;
        return new NintendoUser(id, nickname, icon, country);
    }

    /// <summary>
    /// 读游玩记录。网关有时只认 id_token，所以 access_token 被拒时再试一次 id_token；
    /// 语言不被接受（400）时换下一个语言。
    /// </summary>
    public async Task<NintendoPlayHistory> GetPlayHistoryAsync(NintendoTokens tokens, IReadOnlyList<string> locales, CancellationToken cancellationToken = default)
    {
        var bearers = tokens.IdToken is { Length: > 0 } idToken && idToken != tokens.AccessToken ? new[] { tokens.AccessToken, idToken } : [tokens.AccessToken];
        Exception? last = null;
        foreach (var locale in locales.Count == 0 ? ["en-US"] : locales)
        {
            foreach (var bearer in bearers)
            {
                using var request = Request(HttpMethod.Get, NintendoEndpoints.PlayHistories, NintendoEndpoints.AppUserAgent);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                request.Headers.TryAddWithoutValidation("gentry-locale", locale);
                try
                {
                    var body = await SendRawAsync(request, authEndpoint: false, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return PlayHistoryParser.Parse(body);
                    }
                    catch (JsonException exception)
                    {
                        throw new NintendoFormatException("play history is not readable", exception);
                    }
                }
                catch (NintendoAuthException exception)
                {
                    last = exception;
                }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
                {
                    last = exception;
                    break;
                }
            }
        }
        throw last ?? new NintendoAuthException("play history was refused");
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string userAgent)
    {
        if (!NintendoEndpoints.IsAllowed(uri)) throw new InvalidOperationException("Nintendo endpoint is not allowed");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, bool authEndpoint, CancellationToken cancellationToken)
    {
        var body = await SendRawAsync(request, authEndpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new NintendoFormatException("response is not JSON", exception);
        }
    }

    /// <summary>
    /// 账号接口对过期或撤销的 session token 回 400 + OAuth 错误码（invalid_grant 等），不是 401；
    /// 其余接口令牌无效时回 401 / 403。错误正文可能带令牌，不放进异常信息。
    /// </summary>
    private async Task<string> SendRawAsync(HttpRequestMessage request, bool authEndpoint, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new NintendoFormatException("response is too large");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > MaxResponseBytes) throw new NintendoFormatException("response is too large");
        if (response.IsSuccessStatusCode) return body;

        var status = response.StatusCode;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new NintendoAuthException($"refused with {(int)status}");
        if (authEndpoint && status == HttpStatusCode.BadRequest && OAuthError(body) is { } error)
            throw new NintendoAuthException($"refused: {error}");
        throw new HttpRequestException($"Nintendo answered {(int)status}", null, status);
    }

    private static string? OAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = Optional(document.RootElement, "error");
            return error is { Length: <= 64 } && error.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? error : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Required(JsonElement element, string name)
        => Optional(element, name) is { Length: > 0 } value ? value : throw new NintendoFormatException($"missing {name}");

    private static string? Optional(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
