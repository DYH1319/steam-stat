namespace SteamStat.Core.Diagnostics;

/// <summary>
/// 生成预填好的 GitHub「New issue」链接。GitHub 对 URL 长度有上限（约 8 KB），超出时截断正文并注明。
/// </summary>
public static class GitHubIssueLink
{
    public const int MaxUrlLength = 8000;
    private const string TruncatedMarker = "\n\n…(truncated)";

    public static string Build(string title, string body, IReadOnlyCollection<string>? labels = null)
    {
        var prefix = $"{ProjectLinks.NewIssueUrl}?title={Uri.EscapeDataString(Clip(title, 200))}";
        if (labels is { Count: > 0 }) prefix += $"&labels={Uri.EscapeDataString(string.Join(',', labels))}";
        prefix += "&body=";

        var encoded = Uri.EscapeDataString(body);
        if (prefix.Length + encoded.Length <= MaxUrlLength) return prefix + encoded;

        // 按编码后的长度二分出可保留的正文长度（中文等字符编码后膨胀约 9 倍）
        int low = 0, high = body.Length;
        var budget = MaxUrlLength - prefix.Length - Uri.EscapeDataString(TruncatedMarker).Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Uri.EscapeDataString(SafeSubstring(body, middle)).Length <= budget) low = middle;
            else high = middle - 1;
        }
        return prefix + Uri.EscapeDataString(SafeSubstring(body, low) + TruncatedMarker);
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : SafeSubstring(value, length);

    /// <summary>截断时不拆开代理对（否则 EscapeDataString 会抛出异常）。</summary>
    private static string SafeSubstring(string value, int length)
    {
        if (length >= value.Length) return value;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
