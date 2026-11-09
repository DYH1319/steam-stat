using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamStat.Core.Archives.Store;

/// <summary>
/// 商店页上值得留下的内容。不含价格和评测：它们每天都在变，单独存在商店状态里，
/// 也不含标签：标签按投票排序，边缘的标签每周都换，算进哈希会产生大量没有意义的快照。
/// </summary>
public sealed record StorePageFacts(
    string Name,
    string? ShortDescription,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    string? Franchise,
    long? ReleaseDate,
    string? HeaderAsset,
    string? CapsuleAsset,
    IReadOnlyList<string> Screenshots,
    string? StorePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static StorePageFacts? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StorePageFacts>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>内容指纹，只有它变了才产生新快照。</summary>
    public string Fingerprint()
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())))[..32];
}

public static class ArtifactEventKinds
{
    public const string Delisted = "delisted";
    public const string Relisted = "relisted";
    public const string Renamed = "renamed";
    public const string CoverChanged = "coverChanged";
    public const string PublisherChanged = "publisherChanged";
    public const string ServerShutdown = "serverShutdown";
}

public sealed record ArtifactChange(string Kind, string? Before, string? After);

/// <summary>两次快照之间发生的、值得记一笔的事。第一次看到一款游戏只是基线，不算变化。</summary>
public static class ArtifactDiff
{
    public static IReadOnlyList<ArtifactChange> Compare(StorePageFacts? previous, StorePageFacts next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (previous == null) return [];
        var changes = new List<ArtifactChange>();
        // 下架的游戏有时只返回空名字，这时不算改名
        if (next.Name.Length > 0 && previous.Name.Length > 0 && !string.Equals(previous.Name, next.Name, StringComparison.Ordinal))
            changes.Add(new ArtifactChange(ArtifactEventKinds.Renamed, previous.Name, next.Name));
        if (next.HeaderAsset != null && previous.HeaderAsset != null && !string.Equals(previous.HeaderAsset, next.HeaderAsset, StringComparison.Ordinal))
            changes.Add(new ArtifactChange(ArtifactEventKinds.CoverChanged, previous.HeaderAsset, next.HeaderAsset));
        var before = string.Join(", ", previous.Publishers);
        var after = string.Join(", ", next.Publishers);
        if (before.Length > 0 && after.Length > 0 && !string.Equals(before, after, StringComparison.Ordinal))
            changes.Add(new ArtifactChange(ArtifactEventKinds.PublisherChanged, before, after));
        return changes;
    }
}

public sealed record NewsItem(string Title, string? Contents, long Date, string? Url);

public sealed record DailyPlayers(long Day, int Peak);

/// <summary>为什么怀疑一款游戏关服了。</summary>
public sealed record ShutdownEvidence(string Source, string Detail, long? At);

/// <summary>
/// 判断联机游戏是否关服。Steam 没有这样的字段，所以两条证据都只算「疑似」：
/// 官方公告里出现停服字样，或者纯联机游戏连续两周采样都是 0 人。
/// </summary>
public static partial class ServerShutdownDetector
{
    private const int MultiPlayer = 1;
    private const int SinglePlayer = 2;
    private const int OnlineCoop = 38;
    private const int OnlinePvp = 36;
    private const int MinSilentDays = 14;

    public static bool IsOnlineOnly(IReadOnlyCollection<int> categories)
        => !categories.Contains(SinglePlayer)
           && (categories.Contains(MultiPlayer) || categories.Contains(OnlineCoop) || categories.Contains(OnlinePvp));

    public static ShutdownEvidence? Evaluate(
        IReadOnlyCollection<int> categories,
        IReadOnlyList<NewsItem> news,
        IReadOnlyList<DailyPlayers> players)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(news);
        ArgumentNullException.ThrowIfNull(players);
        if (!IsOnlineOnly(categories)) return null;

        foreach (var item in news.OrderByDescending(entry => entry.Date))
        {
            if (Shutdown().IsMatch(item.Title)) return new ShutdownEvidence("news", item.Title, item.Date);
        }

        var ordered = players.OrderBy(sample => sample.Day).ToArray();
        var silent = 0;
        for (var index = ordered.Length - 1; index >= 0 && ordered[index].Peak == 0; index--) silent++;
        // 一直是 0 说明这款游戏根本没有人数数据，不说明关服
        var hadPlayers = ordered.Take(ordered.Length - silent).Any(sample => sample.Peak > 0);
        return silent >= MinSilentDays && hadPlayers
            ? new ShutdownEvidence("players", silent.ToString(System.Globalization.CultureInfo.InvariantCulture), ordered[^silent].Day)
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\b(?:servers?|online services?|service)\b.{0,40}\b(?:shut(?:ting)?[\s-]?down|sunset(?:ting)?|closure|closing|discontinu\w*|going offline|end of life)\b)|(?:\b(?:shut(?:ting)?[\s-]?down|sunset(?:ting)?)\b.{0,40}\b(?:servers?|online services?)\b)|\bend of service\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Shutdown();
}
