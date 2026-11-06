using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamStat.Core.PublicData;

/// <summary>
/// 公共数据仓 history 分支的在线人数历史。和 data 分支不同，这里只追加：
/// 每天采样一次，按月分片，月份结束后文件就不再变，CDN 和本地缓存都可以永久留着。
/// 按 appId % 16 分片：一个月的文件有几百款游戏，看过几款之后，同一批文件就都在本地了。
/// 格式改动要同时改公共数据仓的 tools/history.mjs 和 tools/validate-history.mjs。
/// </summary>
public static partial class PlayerHistoryFormat
{
    public const string IndexFormat = "steam-stat-public-data/players-index";
    public const string ShardFormat = "steam-stat-public-data/players";
    public const int Version = 1;
    public const int ShardCount = 16;
    public const long MaxIndexBytes = 2 * 1024 * 1024;
    public const long MaxShardBytes = 4 * 1024 * 1024;

    public static string ShardOf(int appId) => (appId % ShardCount).ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    public static string PathOf(string month, string shard) => $"players/{month}/{shard}.json";

    public static bool IsValid(PlayerHistoryIndex? index)
    {
        if (index is not { Format: IndexFormat, Version: Version } || index.UpdatedAt <= 0) return false;
        foreach (var (month, files) in index.Months ?? [])
        {
            if (!Month().IsMatch(month)) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (!Shard().IsMatch(file.Shard) || !seen.Add(file.Shard)) return false;
                if (!Sha256().IsMatch(file.Sha256) || file.Size is <= 0 or > MaxShardBytes) return false;
            }
        }
        return true;
    }

    [GeneratedRegex("^[0-9]{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant)]
    private static partial Regex Month();

    [GeneratedRegex("^[0-9a-f]$", RegexOptions.CultureInvariant)]
    private static partial Regex Shard();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}

public sealed class PlayerHistoryIndex
{
    [JsonPropertyName("format")] public string Format { get; init; } = string.Empty;
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; init; }
    /// <summary>覆盖的应用。客户端只给这之外的游戏自己采样。</summary>
    [JsonPropertyName("apps")] public List<int>? Apps { get; init; }
    /// <summary>yyyy-MM → 这个月的分片。</summary>
    [JsonPropertyName("months")] public Dictionary<string, List<PlayerHistoryFile>>? Months { get; init; }
}

public sealed class PlayerHistoryFile
{
    [JsonPropertyName("shard")] public string Shard { get; init; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
}

public sealed class PlayerHistoryShard
{
    [JsonPropertyName("format")] public string Format { get; init; } = string.Empty;
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("month")] public string Month { get; init; } = string.Empty;
    [JsonPropertyName("shard")] public string Shard { get; init; } = string.Empty;
    /// <summary>应用 ID → 这个月每天的人数，下标是日期减一，没有采到是 null。</summary>
    [JsonPropertyName("apps")] public Dictionary<string, List<int?>>? Apps { get; init; }
}
