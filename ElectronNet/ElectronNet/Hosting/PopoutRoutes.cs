using System.Text.RegularExpressions;

namespace ElectronNet.Hosting;

/// <param name="Kind">窗口类型，决定默认大小和记住的窗口状态</param>
/// <param name="Key">同一 Key 只开一个窗口：同一游戏的不同标签页算同一个窗口</param>
/// <param name="Path">窗口打开的路由</param>
internal sealed record PopoutRoute(string Kind, string Key, string Path);

/// <summary>可以弹出独立窗口的路由。查询串只放行不会改变地址结构的字符。</summary>
internal static partial class PopoutRoutes
{
    public static PopoutRoute? Parse(string? path)
    {
        var match = RoutePattern().Match(path ?? string.Empty);
        if (!match.Success) return null;

        var key = match.Groups["page"].Value;
        var kind = key.StartsWith("/game/", StringComparison.Ordinal) ? "game"
            : key.StartsWith("/friend/", StringComparison.Ordinal) ? "friend"
            : "report";
        return new PopoutRoute(kind, kind == "report" ? path! : key, path!);
    }

    [GeneratedRegex(@"^(?<page>/game/\d{1,10}|/friend/\d{1,20}|/reports)(?:\?[\w%.~=&-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex RoutePattern();
}
