using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SteamStat.Core.Features.Achievements;

namespace SteamStat.Core.PublicData;

/// <summary>
/// 公共数据层（DYH1319/steam-stat-public-data 的 data 分支）的 v1 格式。
/// 清单列出各数据集的分片（appId % 256），分片文件名带内容哈希，客户端逐个校验 SHA-256 与大小上限，
/// 只从白名单镜像下载。这里只有公开的商店 / 社区数据，不包含任何用户数据。
/// </summary>
public static partial class PublicDataFormat
{
    public const string Format = "steam-stat-public-data";
    public const int Version = 1;
    public const string AppsDataset = "apps";
    public const string AchievementsDataset = "achievements";
    public const long MaxManifestBytes = 1024 * 1024;
    public const long MaxShardBytes = 4 * 1024 * 1024;

    public static string ShardOf(long appId) => (appId % 256).ToString("x2", CultureInfo.InvariantCulture);

    /// <summary>清单格式、版本与每个分片条目都合法时返回 true（不合法的清单整体丢弃）。</summary>
    public static bool IsValid(PublicDataManifest? manifest)
    {
        if (manifest is not { Format: Format, Version: Version } || manifest.GeneratedAt <= 0) return false;
        foreach (var (dataset, value) in manifest.Datasets ?? [])
        {
            if (dataset is not (AppsDataset or AchievementsDataset)) continue;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shard in value.Shards ?? [])
            {
                if (!ShardIdPattern().IsMatch(shard.Id) || !ids.Add(shard.Id)) return false;
                if (!Sha256Pattern().IsMatch(shard.Sha256) || shard.Size is <= 0 or > MaxShardBytes || shard.Count < 0) return false;
                if (shard.Path != $"{dataset}/{shard.Id}.{shard.Sha256[..16]}.json") return false;
            }
        }
        return true;
    }

    public static IReadOnlyList<PublicDataShard> Shards(PublicDataManifest manifest, string dataset)
        => manifest.Datasets?.GetValueOrDefault(dataset)?.Shards ?? [];

    [GeneratedRegex("^[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShardIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}

public sealed class PublicDataManifest
{
    [JsonPropertyName("format")] public string Format { get; init; } = string.Empty;
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("generatedAt")] public long GeneratedAt { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    /// <summary>类型 ID → 语言（english / schinese）→ 名称。</summary>
    [JsonPropertyName("genres")] public Dictionary<string, Dictionary<string, string>>? Genres { get; init; }
    [JsonPropertyName("datasets")] public Dictionary<string, PublicDataset>? Datasets { get; init; }
}

public sealed class PublicDataset
{
    [JsonPropertyName("shards")] public List<PublicDataShard>? Shards { get; init; }
}

public sealed class PublicDataShard
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
}

public sealed class PublicDataShardFile<T>
{
    [JsonPropertyName("format")] public string Format { get; init; } = string.Empty;
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("shard")] public string Shard { get; init; } = string.Empty;
    [JsonPropertyName("apps")] public Dictionary<string, T>? Apps { get; init; }
}

public sealed class PublicAppRecord
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("genres")] public List<int>? Genres { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("developers")] public List<string>? Developers { get; init; }
    [JsonPropertyName("publishers")] public List<string>? Publishers { get; init; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("achievements")] public int? Achievements { get; init; }
    [JsonPropertyName("fetchedAt")] public long FetchedAt { get; init; }
}

public sealed class PublicAchievementRecord
{
    [JsonPropertyName("fetchedAt")] public long FetchedAt { get; init; }
    /// <summary>Steam 语言名（english / schinese）→ 成就列表。</summary>
    [JsonPropertyName("languages")] public Dictionary<string, List<PublicAchievementItem>>? Languages { get; init; }
}

public sealed class PublicAchievementItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;
    [JsonPropertyName("iconGray")] public string IconGray { get; init; } = string.Empty;
    [JsonPropertyName("hidden")] public bool Hidden { get; init; }
}

/// <summary>
/// 成就 schema 的公共数据回退：CM 不可用且本地没有缓存时，由 <see cref="Steam.Gateway.SteamAchievementSchemaGateway"/> 调用。
/// 默认实现什么都不返回；Host 注册真正的实现（下载并校验公共数据分片）。
/// </summary>
public interface IPublicAchievementSchemaSource
{
    Task<SteamAchievementSchemaSnapshot?> TryGetSchemaAsync(uint appId, string language, CancellationToken cancellationToken = default);
}

public sealed class NullPublicAchievementSchemaSource : IPublicAchievementSchemaSource
{
    public Task<SteamAchievementSchemaSnapshot?> TryGetSchemaAsync(uint appId, string language, CancellationToken cancellationToken = default)
        => Task.FromResult<SteamAchievementSchemaSnapshot?>(null);
}

public static class PublicDataMapper
{
    /// <summary>公共数据的成就记录 → schema 快照；所需语言缺失时退回 english。没有可用条目时返回 null。</summary>
    public static SteamAchievementSchemaSnapshot? ToSnapshot(uint appId, string language, PublicAchievementRecord record)
    {
        var languages = record.Languages;
        if (languages == null) return null;
        var items = languages.GetValueOrDefault(language) ?? languages.GetValueOrDefault("english");
        if (items is not { Count: > 0 }) return null;
        var definitions = items
            .Where(item => item.Name.Length is > 0 and <= 128)
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(item => new SteamAchievementDefinition(
                InternalKey: null,
                InternalName: item.Name,
                LocalizedName: Clip(item.DisplayName.Length > 0 ? item.DisplayName : item.Name),
                LocalizedDescription: Clip(item.Description),
                Icon: SafeIcon(item.Icon),
                IconGray: SafeIcon(item.IconGray),
                Hidden: item.Hidden,
                GlobalUnlockedPercent: null,
                GroupId: null,
                Archived: false,
                ProgressType: SteamAchievementProgressType.None,
                MinProgress: null,
                MaxProgress: null))
            .ToList();
        return definitions.Count == 0 ? null : new SteamAchievementSchemaSnapshot(appId, language, 0, 0, definitions, []);
    }

    /// <summary>只接受 Steam CDN 的 https 图标地址。</summary>
    internal static string SafeIcon(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && value.Length <= 512
           && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase))
            ? value
            : string.Empty;

    private static string Clip(string value) => value.Length <= 512 ? value : value[..512];
}
