using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Http;
using SteamStat.Core.PublicData;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 公共数据层客户端：从 GitHub Raw / jsDelivr（国内网络优化时优先 jsDelivr）下载清单，
/// 只下载本机用到的游戏所在的分片，逐个校验 SHA-256 与大小上限后缓存在 {UserData}/PublicData。
/// <list type="bullet">
/// <item>统计：为应用补充类型、标签（以及 CM 没给的开发商 / 发行商）</item>
/// <item>成就：CM 不可用且没有缓存时，作为 schema 的回退来源</item>
/// </list>
/// 请求只暴露分片号（appId % 256），不上传任何用户数据。可以在设置里关闭。
/// </summary>
public sealed class PublicDataService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    IHttpClientFactory httpClientFactory,
    ISettingsStore settingsStore,
    ILanguageProvider languageProvider,
    TimeProvider timeProvider,
    ILogger<PublicDataService> logger) : IPublicAchievementSchemaSource, IPublicDataSwitch, IDisposable
{
    internal const string MirrorVariable = "STEAM_STAT_PUBLIC_DATA";
    private const string Repository = "DYH1319/steam-stat-public-data";
    private static readonly string[] DefaultMirrors =
    [
        $"https://raw.githubusercontent.com/{Repository}/data",
        $"https://cdn.jsdelivr.net/gh/{Repository}@data"
    ];
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _cacheLock = new();
    private Dictionary<int, PublicAppRecord>? _apps;
    private PublicDataManifest? _manifest;
    private ITimer? _timer;
    private volatile bool _running;

    private string Root => Path.Combine(appPaths.UserDataDirectory, "PublicData");
    private string ManifestFile => Path.Combine(Root, "manifest.json");
    private string StateFile => Path.Combine(Root, "state.json");

    private sealed class State
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("lastRefreshAt")] public long? LastRefreshAt { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("lastError")] public string? LastError { get; set; }
    }

    #region 调度与状态

    public void StartSchedule()
    {
        _timer ??= timeProvider.CreateTimer(_ => _ = RefreshIfDueAsync(), null, InitialDelay, TickInterval);
    }

    private async Task RefreshIfDueAsync()
    {
        var state = ReadState();
        if (!state.Enabled) return;
        var last = state.LastRefreshAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : DateTimeOffset.MinValue;
        if (timeProvider.GetUtcNow() - last < RefreshAfter) return;
        await RefreshAsync().ConfigureAwait(false);
    }

    public PublicDataStatusDto GetStatus()
    {
        var state = ReadState();
        var manifest = LoadManifest();
        var cachedShards = 0;
        long bytes = 0;
        foreach (var dataset in new[] { PublicDataFormat.AppsDataset, PublicDataFormat.AchievementsDataset })
        {
            var directory = Path.Combine(Root, dataset);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                cachedShards++;
                bytes += new FileInfo(file).Length;
            }
        }
        var achievementApps = manifest == null
            ? 0
            : PublicDataFormat.Shards(manifest, PublicDataFormat.AchievementsDataset)
                .Where(shard => File.Exists(ShardPath(shard))).Sum(shard => shard.Count);
        return new PublicDataStatusDto
        {
            Enabled = state.Enabled,
            State = _running ? "running" : "idle",
            GeneratedAt = manifest?.GeneratedAt,
            LastRefreshAt = state.LastRefreshAt,
            Source = state.Source,
            TotalApps = manifest == null ? 0 : PublicDataFormat.Shards(manifest, PublicDataFormat.AppsDataset).Sum(shard => shard.Count),
            CachedApps = LoadApps().Count,
            CachedAchievementApps = achievementApps,
            CachedShards = cachedShards,
            CacheBytes = bytes,
            LastError = state.LastError
        };
    }

    public async Task<PublicDataStatusDto> SetEnabledAsync(bool enabled)
    {
        var state = ReadState();
        state.Enabled = enabled;
        WriteState(state);
        if (enabled) await RefreshAsync().ConfigureAwait(false);
        return GetStatus();
    }

    #endregion

    #region 下载

    /// <summary>刷新清单，并下载本机用到的游戏所在、尚未缓存（或已变化）的分片。</summary>
    public async Task<PublicDataStatusDto> RefreshAsync(
        IProgress<TaskProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!ReadState().Enabled) return GetStatus();
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return GetStatus();
        _running = true;
        try
        {
            progress?.Report(new TaskProgressUpdate("manifest"));
            var (manifest, source, bytes) = await FetchManifestAsync(cancellationToken).ConfigureAwait(false);
            var state = ReadState();
            if (manifest == null || bytes == null)
            {
                state.LastError = "unreachable";
                WriteState(state);
                return GetStatus();
            }
            // CDN 可能还缓存着旧清单：比本地的旧就继续用本地的
            var current = LoadManifest();
            if (current != null && current.GeneratedAt > manifest.GeneratedAt)
            {
                manifest = current;
            }
            else
            {
                Directory.CreateDirectory(Root);
                await File.WriteAllBytesAsync(ManifestFile, bytes, cancellationToken).ConfigureAwait(false);
            }

            var needed = await NeededShardsAsync(cancellationToken).ConfigureAwait(false);
            var failed = 0;
            var datasets = new[] { PublicDataFormat.AppsDataset, PublicDataFormat.AchievementsDataset };
            var pending = datasets.Sum(dataset => PublicDataFormat.Shards(manifest, dataset)
                .Count(shard => needed.Contains(shard.Id) && !File.Exists(ShardPath(shard))));
            var downloaded = 0;
            foreach (var dataset in datasets)
            {
                var shards = PublicDataFormat.Shards(manifest, dataset);
                foreach (var shard in shards.Where(shard => needed.Contains(shard.Id)))
                {
                    if (File.Exists(ShardPath(shard))) continue;
                    progress?.Report(new TaskProgressUpdate("shards", downloaded++, pending));
                    if (!await DownloadShardAsync(shard, source, cancellationToken).ConfigureAwait(false)) failed++;
                }
                PruneDataset(dataset, shards);
            }

            lock (_cacheLock)
            {
                _manifest = manifest;
                _apps = null;
            }
            state.LastRefreshAt = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            state.Source = source != null && !Directory.Exists(source) && Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : "local";
            state.LastError = failed > 0 ? "invalid" : null;
            WriteState(state);
            logger.LogInformation("Public data refreshed from {Source} (generated at {GeneratedAt}, {Failed} shards failed)", state.Source, manifest.GeneratedAt, failed);
            return GetStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to update the public data cache");
            var state = ReadState();
            state.LastError = "io";
            WriteState(state);
            return GetStatus();
        }
        finally
        {
            _running = false;
            _refreshGate.Release();
        }
    }

    /// <summary>本机知道的所有应用（已安装、会话、游戏库、成就、时长快照）所在的分片。</summary>
    private async Task<HashSet<string>> NeededShardsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<long>();
        ids.UnionWith(await db.SteamAppTable.AsNoTracking().Select(row => (long)row.AppId).ToListAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(await db.UseAppRecordTable.AsNoTracking().Select(row => (long)row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(await db.LibraryAcquisitionTable.AsNoTracking().Select(row => row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(await db.AchievementGameArchiveTable.AsNoTracking().Select(row => row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(await db.PlaytimeSnapshotTable.AsNoTracking().Select(row => (long)row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        return ids.Where(id => id > 0).Select(PublicDataFormat.ShardOf).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>测试用：替换镜像列表（URL 或本地目录）。</summary>
    internal IReadOnlyList<string>? MirrorsOverride { get; set; }

    private IReadOnlyList<string> Mirrors()
    {
        if (MirrorsOverride != null) return MirrorsOverride;
        if (Environment.GetEnvironmentVariable(MirrorVariable) is { Length: > 0 } custom) return [custom.TrimEnd('/', '\\')];
        return settingsStore.GetSettings().Network?.CnOptimized == true ? DefaultMirrors.Reverse().ToArray() : DefaultMirrors;
    }

    private async Task<(PublicDataManifest? Manifest, string? Source, byte[]? Bytes)> FetchManifestAsync(CancellationToken cancellationToken)
    {
        foreach (var mirror in Mirrors())
        {
            var bytes = await FetchAsync(mirror, "manifest.json", PublicDataFormat.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
            if (bytes == null) continue;
            var manifest = Parse<PublicDataManifest>(bytes);
            if (PublicDataFormat.IsValid(manifest)) return (manifest, mirror, bytes);
            logger.LogWarning("Public data mirror {Mirror} returned an invalid manifest", mirror);
        }
        return (null, null, null);
    }

    /// <summary>先用拿到清单的镜像下载，失败再试其他镜像；SHA-256 或大小不符的内容一律丢弃。</summary>
    private async Task<bool> DownloadShardAsync(PublicDataShard shard, string? preferred, CancellationToken cancellationToken)
    {
        var mirrors = Mirrors().OrderBy(mirror => mirror == preferred ? 0 : 1).ToList();
        foreach (var mirror in mirrors)
        {
            var bytes = await FetchAsync(mirror, shard.Path, PublicDataFormat.MaxShardBytes, cancellationToken).ConfigureAwait(false);
            if (bytes == null || bytes.LongLength != shard.Size) continue;
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), shard.Sha256, StringComparison.Ordinal)) continue;
            var target = ShardPath(shard);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        logger.LogInformation("Public data shard {Path} could not be downloaded or verified", shard.Path);
        return false;
    }

    private async Task<byte[]?> FetchAsync(string mirror, string relative, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(mirror))
            {
                var file = Path.GetFullPath(Path.Combine(mirror, relative.Replace('/', Path.DirectorySeparatorChar)));
                return File.Exists(file) && new FileInfo(file).Length <= maxBytes ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false) : null;
            }
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.Download);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{mirror}/{relative}");
            SteamHttpRequestOptions.SetOperation(request, "public-data");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or UriFormatException)
        {
            logger.LogInformation("Public data mirror {Mirror} unavailable with {ExceptionType}", mirror, exception.GetType().Name);
            return null;
        }
    }

    private void PruneDataset(string dataset, IReadOnlyList<PublicDataShard> shards)
    {
        var directory = Path.Combine(Root, dataset);
        if (!Directory.Exists(directory)) return;
        var keep = shards.Select(shard => Path.GetFileName(shard.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
        }
    }

    #endregion

    #region 读取

    /// <summary>
    /// 统计用的类型 / 标签 / 开发商 / 发行商。类型名称按界面语言（中文界面用 schinese）。
    /// 没有缓存的应用不返回。
    /// </summary>
    public IReadOnlyDictionary<int, (string? Genres, string? Tags, string? Developers, string? Publishers)> GetAppMeta(IEnumerable<int> appIds)
    {
        var apps = LoadApps();
        if (apps.Count == 0) return new Dictionary<int, (string?, string?, string?, string?)>();
        var manifest = LoadManifest();
        var language = languageProvider.GetSteamLanguage() == "schinese" ? "schinese" : "english";
        var result = new Dictionary<int, (string?, string?, string?, string?)>();
        foreach (var appId in appIds)
        {
            if (!apps.TryGetValue(appId, out var record)) continue;
            var genres = record.Genres?
                .Select(id => manifest?.Genres?.GetValueOrDefault(id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Select(names => names?.GetValueOrDefault(language) ?? names?.GetValueOrDefault("english"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            result[appId] = (Join(genres), Join(record.Tags), Join(record.Developers), Join(record.Publishers));
        }
        return result;
    }

    /// <summary>公共数据里的评测和拥有者数量。只覆盖热门游戏，冷门游戏靠商店状态。</summary>
    public IReadOnlyDictionary<int, PublicAppPopularity> GetAppPopularity(IEnumerable<int> appIds)
    {
        var apps = LoadApps();
        var result = new Dictionary<int, PublicAppPopularity>();
        foreach (var appId in appIds)
        {
            if (!apps.TryGetValue(appId, out var record)) continue;
            var reviews = (record.Positive ?? 0) + (record.Negative ?? 0);
            if (reviews == 0 && record.Owners == null && record.Price == null) continue;
            result[appId] = new PublicAppPopularity(
                reviews > 0 ? reviews : null,
                reviews > 0 ? (int)Math.Round(100.0 * (record.Positive ?? 0) / reviews) : null,
                record.Owners,
                record.Price);
        }
        return result;
    }

    /// <summary>公共数据里的发行日期（英文写法），解析不出来时返回 null。</summary>
    public IReadOnlyDictionary<int, long> GetReleaseDates(IEnumerable<int> appIds)
    {
        var apps = LoadApps();
        var result = new Dictionary<int, long>();
        foreach (var appId in appIds)
        {
            if (apps.TryGetValue(appId, out var record)
                && DateTimeOffset.TryParse(record.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                result[appId] = date.ToUnixTimeSeconds();
        }
        return result;
    }

    /// <summary>用户可以关闭公共数据层。关闭时不主动访问这类公开数据。</summary>
    public bool Enabled => ReadState().Enabled;

    public async Task<SteamAchievementSchemaSnapshot?> TryGetSchemaAsync(uint appId, string language, CancellationToken cancellationToken = default)
    {
        if (!ReadState().Enabled) return null;
        var manifest = LoadManifest();
        var shard = manifest == null
            ? null
            : PublicDataFormat.Shards(manifest, PublicDataFormat.AchievementsDataset).FirstOrDefault(item => item.Id == PublicDataFormat.ShardOf(appId));
        if (shard == null) return null;
        if (!File.Exists(ShardPath(shard)) && !await DownloadShardAsync(shard, null, cancellationToken).ConfigureAwait(false)) return null;
        var content = ReadShard<PublicAchievementRecord>(shard);
        var record = content?.Apps?.GetValueOrDefault(appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return record == null ? null : PublicDataMapper.ToSnapshot(appId, language, record);
    }

    private Dictionary<int, PublicAppRecord> LoadApps()
    {
        lock (_cacheLock)
        {
            if (_apps != null) return _apps;
        }
        var apps = new Dictionary<int, PublicAppRecord>();
        var manifest = LoadManifest();
        if (manifest != null)
        {
            foreach (var shard in PublicDataFormat.Shards(manifest, PublicDataFormat.AppsDataset))
            {
                if (!File.Exists(ShardPath(shard))) continue;
                foreach (var (key, record) in ReadShard<PublicAppRecord>(shard)?.Apps ?? [])
                {
                    if (int.TryParse(key, out var appId) && PublicDataFormat.ShardOf(appId) == shard.Id) apps[appId] = record;
                }
            }
        }
        lock (_cacheLock) _apps = apps;
        return apps;
    }

    private PublicDataManifest? LoadManifest()
    {
        lock (_cacheLock)
        {
            if (_manifest != null) return _manifest;
        }
        try
        {
            if (!File.Exists(ManifestFile)) return null;
            var manifest = Parse<PublicDataManifest>(File.ReadAllBytes(ManifestFile));
            if (!PublicDataFormat.IsValid(manifest)) return null;
            lock (_cacheLock) _manifest = manifest;
            return manifest;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>读缓存分片时再校验一次哈希（磁盘上的文件也可能被改动）。</summary>
    private PublicDataShardFile<T>? ReadShard<T>(PublicDataShard shard)
    {
        try
        {
            var bytes = File.ReadAllBytes(ShardPath(shard));
            if (bytes.LongLength != shard.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), shard.Sha256, StringComparison.Ordinal))
            {
                File.Delete(ShardPath(shard));
                return null;
            }
            var content = Parse<PublicDataShardFile<T>>(bytes);
            return content != null && content.Shard == shard.Id && content.Version == PublicDataFormat.Version ? content : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string ShardPath(PublicDataShard shard) => Path.Combine(Root, shard.Path.Replace('/', Path.DirectorySeparatorChar));

    private static T? Parse<T>(byte[] bytes) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Join(IEnumerable<string?>? values)
    {
        var list = values?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list is { Count: > 0 } ? string.Join("; ", list) : null;
    }

    private State ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new State() : new State();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new State();
        }
    }

    private void WriteState(State state)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to save the public data state");
        }
    }

    #endregion

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshGate.Dispose();
    }
}

public sealed record PublicAppPopularity(int? ReviewCount, int? ReviewPercent, long? Owners, int? PriceCents);
