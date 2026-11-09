using System.Text.RegularExpressions;

namespace SteamStat.Core.Archives.Web;

/// <summary>
/// 原始页面要长期保存，还会进备份，但 Steam 的页面里嵌着当次登录的令牌：
/// 属性里的 webapi_token（JWT）和脚本里的 g_sessionID。写盘之前全部抹掉，解析用不到它们。
/// </summary>
public static partial class SteamWebRedactor
{
    private const string Mask = "REDACTED";

    public static string Redact(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var output = Jwt().Replace(content, Mask);
        output = Token().Replace(output, match => $"{match.Groups[1].Value}{Mask}{match.Groups[3].Value}");
        return SessionId().Replace(output, match => $"{match.Groups[1].Value}{Mask}{match.Groups[2].Value}");
    }

    /// <summary>JWT 三段都是 base64url，头部总以 eyJ 开头。不认字段名，字段改名时也抹得掉。</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    /// <summary>JSON 里的字段，也可能是 HTML 转义过的（data-* 属性）。</summary>
    [GeneratedRegex(@"((?:webapi_token|access_token|steamLoginSecure|authwgtoken)(?:&quot;|""|')\s*:\s*(?:&quot;|""|'))([^&""']+)((?:&quot;|""|'))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex(@"(g_sessionID\s*=\s*"")[^""]*("")", RegexOptions.CultureInvariant)]
    private static partial Regex SessionId();
}
