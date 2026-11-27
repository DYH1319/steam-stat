using System.Globalization;
using System.Text.Json;

namespace SteamStat.Core.Nintendo;

/// <summary>主机。同一款游戏在两代主机上玩过会是两条记录。</summary>
public static class NintendoPlatforms
{
    public const string Switch = "switch";
    public const string Switch2 = "switch2";
    public const string Other = "other";

    /// <summary>接口给的是展示名（「Nintendo Switch 2」），旧版本叫 deviceType，也可能是机型代号。</summary>
    public static string Normalize(string? label)
    {
        var text = (label ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ", StringComparison.Ordinal);
        if (text.Length == 0) return Other;
        if (text.Contains("switch 2", StringComparison.Ordinal) || text.Contains("switch2", StringComparison.Ordinal)
            || text is "bee" or "ounce" or "nx2") return Switch2;
        if (text.Contains("switch", StringComparison.Ordinal) || text is "hac" or "nx") return Switch;
        return Other;
    }
}

public sealed record NintendoPlayedTitle(
    string TitleId,
    string Platform,
    string Name,
    string? ImageUrl,
    int TotalMinutes,
    int TotalDays,
    long? FirstPlayedAt,
    long? LastPlayedAt,
    long? LastUpdatedAt);

public sealed record NintendoDayTitle(string TitleId, string Platform, string Name, string? ImageUrl, int Minutes);

/// <summary>PlayedDate 是接口给的日期（yyyy-MM-dd），按任天堂那边的日期划分，不跟随应用的逻辑日分界点。</summary>
public sealed record NintendoDay(string PlayedDate, IReadOnlyList<NintendoDayTitle> Titles);

public sealed record NintendoPlayHistory(IReadOnlyList<NintendoPlayedTitle> Titles, IReadOnlyList<NintendoDay> Recent, long? LastUpdatedAt);

/// <summary>
/// 解析 play_histories。接口没有文档，字段随时可能变：认不出的字段忽略，单条记录缺关键字段就跳过这一条，
/// 不让一条坏数据毁掉整次采集。
/// </summary>
public static class PlayHistoryParser
{
    public const int MaxTitles = 5000;
    public const int MaxDays = 400;

    public static NintendoPlayHistory Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("play_histories is not an object");

        var titles = new Dictionary<(string, string), NintendoPlayedTitle>();
        foreach (var item in Array(root, "playHistories").Take(MaxTitles))
        {
            var id = TitleId(item);
            if (id == null) continue;
            var platform = NintendoPlatforms.Normalize(Text(item, "platform") ?? Text(item, "deviceType"));
            var total = new NintendoPlayedTitle(
                id,
                platform,
                Name(item, id),
                Image(item),
                Math.Max(0, Int(item, "totalPlayedMinutes")),
                Math.Max(0, Int(item, "totalPlayedDays")),
                Time(Text(item, "firstPlayedAt")),
                Time(Text(item, "lastPlayedAt")),
                Time(Text(item, "lastUpdatedAt")));
            // 同一款游戏同一台主机重复出现时取时长大的那条
            if (!titles.TryGetValue((id, platform), out var existing) || existing.TotalMinutes < total.TotalMinutes)
                titles[(id, platform)] = total;
        }

        var days = new Dictionary<string, Dictionary<(string, string), NintendoDayTitle>>(StringComparer.Ordinal);
        foreach (var day in Array(root, "recentPlayHistories").Take(MaxDays))
        {
            var date = Date(Text(day, "playedDate"));
            if (date == null) continue;
            if (!days.TryGetValue(date, out var entries)) days[date] = entries = [];
            foreach (var item in Array(day, "dailyPlayHistories"))
            {
                var id = TitleId(item);
                if (id == null) continue;
                var platform = NintendoPlatforms.Normalize(Text(item, "platform") ?? Text(item, "deviceType"));
                var minutes = Math.Max(0, Int(item, "totalPlayedMinutes"));
                entries[(id, platform)] = entries.TryGetValue((id, platform), out var previous)
                    ? previous with { Minutes = previous.Minutes + minutes }
                    : new NintendoDayTitle(id, platform, Name(item, id), Image(item), minutes);
            }
        }

        return new NintendoPlayHistory(
            titles.Values.OrderByDescending(title => title.LastPlayedAt ?? 0).ThenBy(title => title.TitleId, StringComparer.Ordinal).ToList(),
            days.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new NintendoDay(pair.Key, pair.Value.Values.OrderBy(title => title.TitleId, StringComparer.Ordinal).ToList()))
                .ToList(),
            Time(Text(root, "lastUpdatedAt")));
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray()
            : [];

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => (int)Math.Clamp(number, 0, int.MaxValue),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (int)Math.Clamp(parsed, 0, int.MaxValue),
            _ => 0
        };
    }

    /// <summary>标题 ID 是 16 位十六进制。统一成小写，太长或含其他字符的不要。</summary>
    private static string? TitleId(JsonElement item)
    {
        var id = Text(item, "titleId")?.Trim().ToLowerInvariant();
        return id is { Length: > 0 and <= 32 } && id.All(char.IsAsciiLetterOrDigit) ? id : null;
    }

    private static string Name(JsonElement item, string id)
    {
        var name = Text(item, "titleName")?.Trim();
        return string.IsNullOrEmpty(name) ? id : name.Length > 200 ? name[..200] : name;
    }

    /// <summary>封面只接受 https，渲染层直接加载。</summary>
    private static string? Image(JsonElement item)
        => Text(item, "imageUrl") is { Length: <= 1000 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;

    /// <summary>时间是 RFC 3339，也见过不带时区的写法（按 UTC）。</summary>
    internal static long? Time(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToUnixTimeSeconds();
        return null;
    }

    private static string? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 10) text = text[..10];
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }
}
