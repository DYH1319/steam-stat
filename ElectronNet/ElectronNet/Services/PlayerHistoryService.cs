using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.PublicData;

namespace ElectronNet.Services;

/// <summary>Day 是 UTC 当天 0 点。</summary>
internal readonly record struct PlayerPoint(long Day, int Players, string Source);

/// <summary>
/// 全球在线曲线。公共数据仓的 history 分支给热门游戏，新用户第一天就有曲线；
/// 本机采样补库里的冷门游戏。分支不可用时只有本机采样，什么都不影响。
/// </summary>
internal sealed class PlayerHistoryService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    PublicDataService publicData,
    IAppPaths appPaths,
    TimeProvider timeProvider,
    ILogger<PlayerHistoryService> logger)
{
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private PlayerHistoryIndex? _index;
    private DateTimeOffset _indexCheckedAt = DateTimeOffset.MinValue;

    private string Root => Path.Combine(appPaths.UserDataDirectory, "PublicData", "history");
    private string IndexFile => Path.Combine(Root, "index.json");

    public async Task<IReadOnlySet<int>> GetCoverageAsync(CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        return (index?.Apps ?? []).ToHashSet();
    }

    /// <summary>最近 months 个月的曲线。同一天两边都有时取公共数据：它每天同一时刻采样，可比。</summary>
    public async Task<IReadOnlyList<PlayerPoint>> GetCurveAsync(int appId, int months, CancellationToken cancellationToken = default)
    {
        var points = new Dictionary<long, PlayerPoint>();
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var rows = await db.AppPlayerCountTable.AsNoTracking().Where(row => row.AppId == appId && row.Samples > 0).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!TryDay(row.Day, out var day)) continue;
                points[day] = new PlayerPoint(day, row.Peak, "local");
            }
        }

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index?.Months != null && (index.Apps?.Contains(appId) ?? false))
        {
            var shard = PlayerHistoryFormat.ShardOf(appId);
            foreach (var month in index.Months.Keys.OrderDescending(StringComparer.Ordinal).Take(months))
            {
                var file = index.Months[month].FirstOrDefault(entry => entry.Shard == shard);
                if (file == null) continue;
                var content = await GetShardAsync(month, file, cancellationToken).ConfigureAwait(false);
                if (content?.Apps?.GetValueOrDefault(appId.ToString(CultureInfo.InvariantCulture)) is not { } values) continue;
                for (var offset = 0; offset < values.Count; offset++)
                {
                    if (values[offset] is not { } players) continue;
                    if (!TryDay($"{month}-{offset + 1:D2}", out var day)) continue;
                    points[day] = new PlayerPoint(day, players, "public");
                }
            }
        }

        var cutoff = timeProvider.GetUtcNow().AddMonths(-months).ToUnixTimeSeconds();
        return points.Values.Where(point => point.Day >= cutoff).OrderBy(point => point.Day).ToArray();
    }

    private static bool TryDay(string text, out long unix)
    {
        unix = 0;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return false;
        unix = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeSeconds();
        return true;
    }

    private async Task<PlayerHistoryIndex?> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (!publicData.Enabled) return null;
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (now - _indexCheckedAt < IndexLifetime) return _index;
            _indexCheckedAt = now;
            _index ??= ReadCachedIndex();
            foreach (var mirror in publicData.HistoryMirrors())
            {
                var bytes = await publicData.FetchFileAsync(mirror, "index.json", PlayerHistoryFormat.MaxIndexBytes, cancellationToken).ConfigureAwait(false);
                if (bytes == null) continue;
                var index = Parse<PlayerHistoryIndex>(bytes);
                if (!PlayerHistoryFormat.IsValid(index)) continue;
                // CDN 可能还缓存着旧版本，比本地旧就继续用本地的
                if (_index != null && _index.UpdatedAt > index!.UpdatedAt) break;
                Directory.CreateDirectory(Root);
                await File.WriteAllBytesAsync(IndexFile, bytes, cancellationToken).ConfigureAwait(false);
                _index = index;
                break;
            }
            return _index;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation("Player history index unavailable with {ExceptionType}", exception.GetType().Name);
            return _index;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private PlayerHistoryIndex? ReadCachedIndex()
    {
        if (!File.Exists(IndexFile)) return null;
        var index = Parse<PlayerHistoryIndex>(File.ReadAllBytes(IndexFile));
        return PlayerHistoryFormat.IsValid(index) ? index : null;
    }

    /// <summary>缓存文件的哈希和索引一致才用，当月的文件每天都变，这样会自动重新下载。</summary>
    private async Task<PlayerHistoryShard?> GetShardAsync(string month, PlayerHistoryFile file, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Root, $"{month}-{file.Shard}.json");
        if (File.Exists(target))
        {
            var cached = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
            if (Matches(cached, file)) return ParseShard(cached, month, file);
        }
        foreach (var mirror in publicData.HistoryMirrors())
        {
            var bytes = await publicData.FetchFileAsync(mirror, PlayerHistoryFormat.PathOf(month, file.Shard), PlayerHistoryFormat.MaxShardBytes, cancellationToken).ConfigureAwait(false);
            if (bytes == null || !Matches(bytes, file)) continue;
            Directory.CreateDirectory(Root);
            var temporary = target + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
            return ParseShard(bytes, month, file);
        }
        return null;
    }

    private static bool Matches(byte[] bytes, PlayerHistoryFile file)
        => bytes.LongLength == file.Size && string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal);

    private static PlayerHistoryShard? ParseShard(byte[] bytes, string month, PlayerHistoryFile file)
    {
        var shard = Parse<PlayerHistoryShard>(bytes);
        return shard is { Format: PlayerHistoryFormat.ShardFormat, Version: PlayerHistoryFormat.Version } && shard.Month == month && shard.Shard == file.Shard ? shard : null;
    }

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
}
