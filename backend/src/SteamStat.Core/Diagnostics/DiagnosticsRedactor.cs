using System.Text.RegularExpressions;

namespace SteamStat.Core.Diagnostics;

/// <summary>脱敏所需的本机已知身份信息。</summary>
/// <param name="HomeDirectory">当前用户的家目录（替换为 ~）。</param>
/// <param name="UserName">操作系统用户名。</param>
/// <param name="AccountNames">本机出现过的 Steam 账号名。</param>
/// <param name="PersonaNames">本机出现过的 Steam 昵称。</param>
public sealed record DiagnosticsRedactionContext(
    string? HomeDirectory,
    string? UserName,
    IReadOnlyCollection<string> AccountNames,
    IReadOnlyCollection<string> PersonaNames)
{
    public static DiagnosticsRedactionContext Empty { get; } = new(null, null, [], []);
}

/// <summary>
/// 诊断包与崩溃报告的统一脱敏：家目录 / 用户名、Steam 账号名与昵称、SteamID64、userdata 下的 AccountId、
/// 邮箱、令牌（URL 参数 / Bearer / JWT）、代理地址中的账号密码。只做替换，不删除行，便于对照日志时间线。
/// </summary>
public sealed partial class DiagnosticsRedactor
{
    private const int MinimumNameLength = 3;
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _identityRules;

    public DiagnosticsRedactor(DiagnosticsRedactionContext context)
    {
        var rules = new List<(Regex, string)>();
        if (!string.IsNullOrWhiteSpace(context.HomeDirectory) && context.HomeDirectory.Length > 1)
        {
            var home = context.HomeDirectory.TrimEnd('/', '\\');
            // 同时匹配正反斜杠写法（日志里两种都可能出现）
            var pattern = string.Join(@"[\\/]", home.Split('/', '\\').Select(Regex.Escape));
            rules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "~"));
        }

        AddLiteralRule(rules, context.AccountNames, "[account]");
        AddLiteralRule(rules, context.PersonaNames, "[persona]");
        if (!string.IsNullOrWhiteSpace(context.UserName) && context.UserName.Length >= MinimumNameLength)
            AddLiteralRule(rules, [context.UserName], "[user]");
        _identityRules = rules;
    }

    public string Redact(string? text) => Redact(text, out _);

    /// <summary>脱敏并返回替换次数（诊断包据此告诉用户处理了多少处）。</summary>
    public string Redact(string? text, out int replacements)
    {
        replacements = 0;
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var count = 0;
        string Replace(Regex pattern, string input, string replacement)
            => pattern.Replace(input, match =>
            {
                count++;
                return match.Result(replacement);
            });

        var result = text;
        foreach (var (pattern, replacement) in _identityRules)
            result = Replace(pattern, result, replacement);

        result = Replace(UserProfilePathPattern(), result, "${prefix}[user]");
        result = Replace(ProxyCredentialsPattern(), result, "${scheme}[redacted]@");
        result = Replace(JwtPattern(), result, "[jwt]");
        result = Replace(BearerPattern(), result, "Bearer [redacted]");
        result = Replace(SecretParameterPattern(), result, "${name}${separator}[redacted]");
        result = Replace(EmailPattern(), result, "[email]");
        result = Replace(SteamId64Pattern(), result, "[steamid]");
        result = Replace(UserDataAccountPattern(), result, "${prefix}[accountid]");
        replacements = count;
        return result;
    }

    private static void AddLiteralRule(List<(Regex, string)> rules, IEnumerable<string> values, string replacement)
    {
        var names = values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= MinimumNameLength)
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // 长名称优先，避免较短名称先替换掉长名称的一部分
            .OrderByDescending(value => value.Length)
            .Select(value => Regex.Escape(value))
            .ToArray();
        if (names.Length == 0) return;
        // 名称两侧不能紧挨字母数字，避免把 "bob" 替换进 "bobcat"
        rules.Add((new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', names)})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement));
    }

    [GeneratedRegex(@"(?<prefix>(?:[A-Za-z]:[\\/]Users|/home|/Users)[\\/])(?!\[user\])[^\\/\s""':;]+", RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePathPattern();

    [GeneratedRegex(@"(?<scheme>\b(?:https?|socks[45]h?)://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProxyCredentialsPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<name>\b(?:access_token|refresh_token|token|webapi_key|api_key|key|password|guard_data)\b)(?<separator>\s*[=:]\s*""?)[^&\s"",;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParameterPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b7656119\d{10}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SteamId64Pattern();

    [GeneratedRegex(@"(?<prefix>userdata[\\/])\d{1,10}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserDataAccountPattern();
}
