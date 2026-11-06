using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Http;

namespace SteamStat.Core.Archives.Web;

/// <summary>一次网页会话。令牌只在内存里，不写库，不进日志。</summary>
public sealed record SteamWebSession(ulong SteamId, string AccessToken, string SessionId);

public enum WebFetchStatus
{
    Ok,
    /// <summary>被跳转到登录页或收到 401 / 403：令牌失效。</summary>
    SignedOut,
    /// <summary>跳转目标不在白名单上，没有跟过去。</summary>
    Blocked,
    RateLimited,
    Failed
}

public sealed record WebFetchResult(WebFetchStatus Status, string? Body, Uri FinalUri);

/// <summary>
/// 带登录状态读 Steam 网页。这条 HttpClient 不开 CookieContainer、不自动跳转：
/// 自动跳转会把手写的 Cookie 头原样带去任何主机，共用 CookieContainer 会让账号之间串 Cookie。
/// 所以跳转在这里手动处理，每一跳都重新过白名单。
/// </summary>
public sealed class SteamWebFetcher(IHttpClientFactory httpClientFactory, ILogger<SteamWebFetcher> logger)
{
    private const int MaxRedirects = 3;
    private const long MaxBodyBytes = 16 * 1024 * 1024;

    public Task<WebFetchResult> GetAsync(SteamWebSession session, Uri uri, CancellationToken cancellationToken = default)
        => SendAsync(session, HttpMethod.Get, uri, null, cancellationToken);

    public Task<WebFetchResult> PostAsync(SteamWebSession session, Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken = default)
        => SendAsync(session, HttpMethod.Post, uri, form, cancellationToken);

    private async Task<WebFetchResult> SendAsync(
        SteamWebSession session,
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!SteamWebAllowlist.IsAllowed(method, current)) return new WebFetchResult(WebFetchStatus.Blocked, null, current);
            try
            {
                using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamWeb);
                using var request = new HttpRequestMessage(method, current);
                SteamHttpRequestOptions.SetOperation(request, "web-capture");
                request.Headers.TryAddWithoutValidation("Cookie", CookieHeader(session));
                request.Headers.Referrer = new Uri($"https://{current.Host}/");
                if (form != null) request.Content = new FormUrlEncodedContent(form.Append(new KeyValuePair<string, string>("sessionid", session.SessionId)));

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var next = SteamWebAllowlist.ResolveRedirect(current, response.Headers.Location?.OriginalString, method);
                    if (next != null)
                    {
                        current = next;
                        continue;
                    }
                    // 登录失效时跳到 /login，它不在名单上
                    var location = response.Headers.Location?.OriginalString ?? string.Empty;
                    return new WebFetchResult(
                        location.Contains("login", StringComparison.OrdinalIgnoreCase) ? WebFetchStatus.SignedOut : WebFetchStatus.Blocked,
                        null,
                        current);
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new WebFetchResult(WebFetchStatus.SignedOut, null, current);
                if (response.StatusCode == HttpStatusCode.TooManyRequests) return new WebFetchResult(WebFetchStatus.RateLimited, null, current);
                if (!response.IsSuccessStatusCode) return new WebFetchResult(WebFetchStatus.Failed, null, current);
                if (response.Content.Headers.ContentLength > MaxBodyBytes) return new WebFetchResult(WebFetchStatus.Failed, null, current);
                var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
                return new WebFetchResult(body == null ? WebFetchStatus.Failed : WebFetchStatus.Ok, body, current);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or SteamRateLimiterRejectedException or Polly.ExecutionRejectedException)
            {
                // 异常消息里可能有地址，这里只记类型
                logger.LogInformation("Web capture request failed with {ExceptionType}", exception.GetType().Name);
                return new WebFetchResult(WebFetchStatus.Failed, null, current);
            }
        }
        return new WebFetchResult(WebFetchStatus.Blocked, null, current);
    }

    /// <summary>固定用英文：解析器按英文文字判断类型。%7C%7C 是 Cookie 里的「||」。</summary>
    private static string CookieHeader(SteamWebSession session)
        => string.Create(CultureInfo.InvariantCulture, $"steamLoginSecure={session.SteamId}%7C%7C{session.AccessToken}; sessionid={session.SessionId}; Steam_Language=english; timezoneOffset=0,0");

    private static async Task<string?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
