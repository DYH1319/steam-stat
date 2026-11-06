using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Archives.Web;

/// <summary>网页抓取的数据种类。</summary>
public static class WebCaptureKinds
{
    public const string Billing = "billing";
    public const string Reviews = "reviews";
    public const string Guides = "guides";
    public const string Favorites = "favorites";
    public const string Comments = "comments";

    public static readonly IReadOnlyList<string> All = [Billing, Reviews, Guides, Favorites, Comments];
}

/// <summary>
/// 登录 Cookie 只会发给这里列出的地址。这是网页接入唯一的安全边界：
/// 每一次请求和每一次跳转都要先过这里，不在名单上就中止，所以新增抓取时只能在这里加。
/// 都是只读页面。AjaxLoadMoreHistory 是 POST，但它只是账单的翻页。
/// </summary>
public static partial class SteamWebAllowlist
{
    private const string Store = "https://store.steampowered.com";
    private const string Community = "https://steamcommunity.com";

    public static Uri BillingHistory() => new($"{Store}/account/history/?l=english");

    public static Uri BillingMore() => new($"{Store}/account/AjaxLoadMoreHistory/?l=english");

    public static Uri Reviews(ulong steamId, int page)
        => new(string.Create(CultureInfo.InvariantCulture, $"{Community}/profiles/{steamId}/recommended/?p={page}&l=english"));

    public static Uri Guides(ulong steamId, int page)
        => new(string.Create(CultureInfo.InvariantCulture, $"{Community}/profiles/{steamId}/myworkshopfiles/?section=guides&p={page}&numperpage=30&l=english"));

    public static Uri Favorites(ulong steamId, int page)
        => new(string.Create(CultureInfo.InvariantCulture, $"{Community}/profiles/{steamId}/myworkshopfiles/?browsefilter=myfavorites&browsesort=myfavorites&p={page}&numperpage=30&l=english"));

    public static Uri Comments(ulong steamId, int start, int count)
        => new(string.Create(CultureInfo.InvariantCulture, $"{Community}/comment/Profile/render/{steamId}/-1/?start={start}&count={count}&l=english"));

    public static bool IsAllowed(HttpMethod method, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        var path = uri.AbsolutePath;
        return uri.Host switch
        {
            "store.steampowered.com" when method == HttpMethod.Get => path is "/account/history/" or "/account/history",
            "store.steampowered.com" when method == HttpMethod.Post => path == "/account/AjaxLoadMoreHistory/",
            "steamcommunity.com" when method == HttpMethod.Get => CommunityPath().IsMatch(path),
            _ => false
        };
    }

    /// <summary>
    /// 跳转只接受同一主机上同样在名单里的地址。社区页会从 /profiles/ID 跳到 /id/自定义地址，
    /// 登录失效时跳到 /login，后者不在名单上，正好当成「登录失效」处理。
    /// </summary>
    public static Uri? ResolveRedirect(Uri current, string? location, HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(current, location, out var target)) return null;
        return string.Equals(target.Host, current.Host, StringComparison.OrdinalIgnoreCase) && IsAllowed(method, target) ? target : null;
    }

    [GeneratedRegex(@"^/(?:(?:profiles/\d{17}|id/[A-Za-z0-9_-]{1,64})/(?:recommended|myworkshopfiles)/?|comment/Profile/render/\d{17}/-1/?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CommunityPath();
}
