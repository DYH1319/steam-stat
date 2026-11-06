using System.Text.Json;
using SteamStat.Core.Archives.Store;

namespace SteamStat.Core.Insights;

/// <summary>一款游戏在商店上的公开状态。批量检查用美区标价，所有用户比较时口径一致；购前模拟按用户所在的区查。</summary>
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
    long? ReleaseDate,
    StorePageFacts? Page = null,
    IReadOnlyList<int>? TagIds = null,
    IReadOnlyList<int>? CategoryIds = null,
    int? FinalPriceCents = null,
    string? PriceText = null)
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

    public static string BuildRequest(IEnumerable<int> appIds, string countryCode = StoreRegions.Default)
        => JsonSerializer.Serialize(new
        {
            ids = appIds.Select(appId => new { appid = appId }),
            context = new { language = "english", country_code = countryCode, steam_realm = 1 },
            data_request = new
            {
                include_basic_info = true,
                include_reviews = true,
                include_release = true,
                include_assets = true,
                include_screenshots = true,
                include_tag_count = 20
            }
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
            int? finalPrice = null;
            string? priceText = null;
            var purchasable = false;
            if (item.TryGetProperty("best_purchase_option", out var option) && option.ValueKind == JsonValueKind.Object)
            {
                purchasable = true;
                if (TryInt(option, "final_price_in_cents", out var final)) finalPrice = final;
                price = TryInt(option, "original_price_in_cents", out var original) ? original : finalPrice;
                priceText = option.TryGetProperty("formatted_final_price", out var formatted) ? formatted.GetString() : null;
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

            var facts = ReadFacts(item, franchise, releaseDate);
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
                releaseDate,
                facts,
                ReadIntArray(item, "tagids"),
                ReadCategories(item),
                finalPrice,
                priceText));
        }
        return list;
    }

    /// <summary>下架的游戏只返回一个空壳，没有内容可存，返回 null。</summary>
    private static StorePageFacts? ReadFacts(JsonElement item, string? franchise, long? releaseDate)
    {
        var name = item.TryGetProperty("name", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        if (name.Length == 0) return null;
        string? description = null;
        IReadOnlyList<string> developers = [];
        IReadOnlyList<string> publishers = [];
        if (item.TryGetProperty("basic_info", out var basic) && basic.ValueKind == JsonValueKind.Object)
        {
            description = basic.TryGetProperty("short_description", out var text) ? text.GetString() : null;
            developers = Names(basic, "developers");
            publishers = Names(basic, "publishers");
        }
        string? header = null, capsule = null;
        if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object)
        {
            header = assets.TryGetProperty("header", out var file) ? file.GetString() : null;
            capsule = assets.TryGetProperty("main_capsule", out file) ? file.GetString() : null;
        }
        return new StorePageFacts(
            name,
            description,
            developers,
            publishers,
            franchise,
            releaseDate,
            header,
            capsule,
            ReadScreenshots(item),
            item.TryGetProperty("store_url_path", out var path) ? path.GetString() : null);
    }

    /// <summary>文件名带 ?t= 时间戳，每次改动商店页都会变，比较内容时要去掉。</summary>
    private static IReadOnlyList<string> ReadScreenshots(JsonElement item)
    {
        if (!item.TryGetProperty("screenshots", out var screenshots) || screenshots.ValueKind != JsonValueKind.Object
            || !screenshots.TryGetProperty("all_ages_screenshots", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray()
            .Select(entry => entry.TryGetProperty("filename", out var file) ? file.GetString() : null)
            .Where(file => !string.IsNullOrEmpty(file))
            .Select(file => file!.Split('?')[0])
            .ToArray();
    }

    private static IReadOnlyList<string> Names(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Select(entry => entry.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Number).Select(entry => entry.GetInt32()).ToArray();
    }

    private static IReadOnlyList<int> ReadCategories(JsonElement item)
    {
        if (!item.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Object) return [];
        return ReadIntArray(categories, "supported_player_categoryids")
            .Concat(ReadIntArray(categories, "feature_categoryids"))
            .Distinct()
            .ToArray();
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
