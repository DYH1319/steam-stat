using System.Text.Json;

namespace SteamStat.Core.Insights;

/// <summary>一款游戏在商店上的公开状态。美区标价，所有用户比较时口径一致。</summary>
public sealed record StoreItemStatus(
    int AppId,
    bool Visible,
    bool Purchasable,
    bool IsFree,
    bool ComingSoon,
    int? PriceCents,
    int? ReviewCount,
    int? ReviewPercent,
    string? Franchise,
    long? ReleaseDate)
{
    /// <summary>
    /// 看不到商店页，或者看得到但买不了。线上实测：Deadpool 是 visible=false，
    /// Spec Ops: The Line、Forza Horizon 4 商店页还在但没有购买选项。
    /// 被新版替换的旧版（GTA V Legacy）也会落到这里，所以界面写「已下架或不再单独出售」。
    /// </summary>
    public bool Delisted => !Visible || (!Purchasable && !IsFree && !ComingSoon);
}

/// <summary>解析 IStoreBrowseService/GetItems 的响应。这个接口不需要 Key，一次可以查一批。</summary>
public static class StoreBrowseParser
{
    public const string Endpoint = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1";

    public static string BuildRequest(IEnumerable<int> appIds)
        => JsonSerializer.Serialize(new
        {
            ids = appIds.Select(appId => new { appid = appId }),
            context = new { language = "english", country_code = "US", steam_realm = 1 },
            data_request = new { include_basic_info = true, include_reviews = true, include_release = true }
        });

    public static IReadOnlyList<StoreItemStatus> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("store_items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<StoreItemStatus>();
        foreach (var item in items.EnumerateArray())
        {
            if (!TryInt(item, "id", out var appId) || appId <= 0) continue;
            int? price = null;
            var purchasable = false;
            if (item.TryGetProperty("best_purchase_option", out var option) && option.ValueKind == JsonValueKind.Object)
            {
                purchasable = true;
                if (TryInt(option, "original_price_in_cents", out var original)) price = original;
                else if (TryInt(option, "final_price_in_cents", out var final)) price = final;
            }

            int? reviewCount = null, reviewPercent = null;
            if (item.TryGetProperty("reviews", out var reviews)
                && reviews.TryGetProperty("summary_filtered", out var summary))
            {
                if (TryInt(summary, "review_count", out var count)) reviewCount = count;
                if (TryInt(summary, "percent_positive", out var percent)) reviewPercent = percent;
            }

            string? franchise = null;
            if (item.TryGetProperty("basic_info", out var basic)
                && basic.TryGetProperty("franchises", out var franchises)
                && franchises.ValueKind == JsonValueKind.Array)
            {
                franchise = franchises.EnumerateArray()
                    .Select(entry => entry.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            }

            var comingSoon = false;
            long? releaseDate = null;
            if (item.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object)
            {
                comingSoon = release.TryGetProperty("is_coming_soon", out var soon) && soon.ValueKind == JsonValueKind.True;
                if (release.TryGetProperty("steam_release_date", out var date) && date.TryGetInt64(out var unix) && unix > 0) releaseDate = unix;
            }

            list.Add(new StoreItemStatus(
                appId,
                item.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.True,
                purchasable,
                item.TryGetProperty("is_free", out var free) && free.ValueKind == JsonValueKind.True,
                comingSoon,
                price,
                reviewCount,
                reviewPercent,
                franchise,
                releaseDate));
        }
        return list;
    }

    /// <summary>这个接口把一部分数字写成字符串。</summary>
    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
    }
}
