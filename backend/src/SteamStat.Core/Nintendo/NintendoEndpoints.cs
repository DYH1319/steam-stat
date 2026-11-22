using System.Security.Cryptography;
using System.Text;

namespace SteamStat.Core.Nintendo;

/// <summary>
/// 任天堂没有公开 API。这里是 Nintendo Store / Nintendo Switch App 读取自己的「游玩记录」所用的接口
/// （app-api.znej.nintendo.com），用普通的任天堂账号令牌访问，不需要第三方 f 参数服务。
/// 地址全部写死：只有这里列出的地址会带上任天堂账号的令牌。
/// </summary>
public static class NintendoEndpoints
{
    /// <summary>My Nintendo（Nintendo Store App）的客户端 ID，游玩记录只对它开放。</summary>
    public const string ClientId = "5c38e31cd085304b";
    public const string RedirectScheme = "npf" + ClientId;
    public const string RedirectUri = RedirectScheme + "://auth";
    public const string Scope = "openid user user.mii user.email user.links[].id";

    /// <summary>网关拒绝没有 User-Agent 的请求，用 App 自己的。</summary>
    public const string AppUserAgent = "com.nintendo.znej/3.0.3 (iOS/26.0.1)";
    public const string AccountsUserAgent = "NASDKAPI; Android";

    public static readonly Uri Authorize = new("https://accounts.nintendo.com/connect/1.0.0/authorize");
    public static readonly Uri SessionToken = new("https://accounts.nintendo.com/connect/1.0.0/api/session_token");
    public static readonly Uri Token = new("https://accounts.nintendo.com/connect/1.0.0/api/token");
    public static readonly Uri Me = new("https://api.accounts.nintendo.com/2.0.0/users/me");
    public static readonly Uri PlayHistories = new("https://app-api.znej.nintendo.com/api/v2.0/users/me/play_histories");

    private static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        SessionToken.AbsoluteUri, Token.AbsoluteUri, Me.AbsoluteUri, PlayHistories.AbsoluteUri
    };

    /// <summary>带令牌的请求只能发往这些地址（https、精确匹配，不含查询串）。</summary>
    public static bool IsAllowed(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query) && Allowed.Contains(uri.AbsoluteUri);

    /// <summary>
    /// 游玩记录的标题语言。网关要求必须带 gentry-locale，否则回 400；不认识的语言也可能回 400，
    /// 所以中文界面先试 zh-CN，失败再退回 en-US（见 <see cref="NintendoClient"/>）。
    /// </summary>
    public static IReadOnlyList<string> LocalesFor(string? appLanguage)
        => appLanguage?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            ? ["zh-CN", "en-US"]
            : ["en-US"];
}

/// <summary>
/// 一次登录：PKCE 的 verifier 留在 Host，state 防止把别的登录的回调塞进来。
/// 回调是 npf…://auth#session_token_code=… 的深链，浏览器打不开，所以要么由登录窗口截获，要么让用户复制粘贴。
/// </summary>
public sealed record NintendoLoginRequest(string State, string CodeVerifier, Uri Url)
{
    public static NintendoLoginRequest Create()
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(36));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Challenge(verifier);
        var query = string.Join('&', new (string Key, string Value)[]
        {
            ("state", state),
            ("redirect_uri", NintendoEndpoints.RedirectUri),
            ("client_id", NintendoEndpoints.ClientId),
            ("scope", NintendoEndpoints.Scope),
            ("response_type", "session_token_code"),
            ("session_token_code_challenge", challenge),
            ("session_token_code_challenge_method", "S256"),
            ("theme", "login_form")
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new NintendoLoginRequest(state, verifier, new Uri($"{NintendoEndpoints.Authorize.AbsoluteUri}?{query}"));
    }

    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>回调解析的结果。Error 是固定的错误码，渲染层有对应文案。</summary>
public sealed record NintendoCallback(string? Code, string? Error)
{
    public const string NotCallback = "notCallback";
    public const string StateMismatch = "stateMismatch";
    public const string Denied = "denied";

    /// <summary>解析 npf…://auth#session_token_code=…&amp;state=…。参数在 fragment 里，也兼容放在查询串的写法。</summary>
    public static NintendoCallback Parse(string? callbackUrl, string expectedState)
    {
        var text = callbackUrl?.Trim() ?? string.Empty;
        if (!text.StartsWith(NintendoEndpoints.RedirectUri, StringComparison.OrdinalIgnoreCase)) return new(null, NotCallback);
        var rest = text[NintendoEndpoints.RedirectUri.Length..];
        var start = rest.IndexOfAny(['#', '?']);
        if (start < 0) return new(null, NotCallback);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in rest[(start + 1)..].Split(['&', '#', '?'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            parameters[Uri.UnescapeDataString(part[..separator])] = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
        }
        if (parameters.ContainsKey("error")) return new(null, Denied);
        if (!parameters.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            return new(null, StateMismatch);
        return parameters.TryGetValue("session_token_code", out var code) && code.Length > 0 ? new(code, null) : new(null, NotCallback);
    }
}
