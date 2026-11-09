using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ElectronNet.Features.Archive;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Archives;
using SteamStat.Core.Helpers;
using SteamStat.Core.Platform;
using SteamStat.Core.Sessions;
using SteamStat.Core.Settings;
using ValveKeyValue;

namespace ElectronNet.Services.Collectors;

/// <summary>
/// Steam 年度回顾：月度时长、单游戏时长、连续天数和官方百分位，回填安装之前的历史。
/// 往年的回顾不会再变，读到一次就不再读；Steam 拒绝的年份（还没生成、账号太新）一个月后再试。
/// 回顾从 2022 年开始有；当年的要到 12 月才生成。
/// </summary>
internal sealed class YearInReviewCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    ISettingsStore settingsStore,
    TimeProvider timeProvider) : IArchiveCollector
{
    internal const int FirstYear = 2022;
    private const int MaxRequestsPerRun = 4;
    private static readonly TimeSpan RetryDenied = TimeSpan.FromDays(30);

    internal TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(2);

    public string Id => "yearInReview";

    public TimeSpan Interval => TimeSpan.FromDays(7);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        if (settingsStore.GetSettings().Memoir?.YearInReviewEnabled == false) return CollectorOutcome.Skipped;
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;
        var now = timeProvider.GetUtcNow();
        var lastYear = now.Month == 12 ? now.Year : now.Year - 1;
        if (lastYear < FirstYear) return CollectorOutcome.Ok(0);

        var written = 0;
        var requests = 0;
        foreach (var account in accounts)
        {
            var steamId = account.SteamId.ToString(CultureInfo.InvariantCulture);
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.YearInReviewTable.Where(row => row.SteamId == steamId).ToDictionaryAsync(row => row.Year, cancellationToken).ConfigureAwait(false);
            for (var year = lastYear; year >= FirstYear; year--)
            {
                if (existing.TryGetValue(year, out var row) && !Due(row, year, now)) continue;
                if (requests >= MaxRequestsPerRun) return CollectorOutcome.Ok(written);
                if (requests > 0) await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
                requests++;
                var reply = await gateway.GetYearInReviewAsync(account.AccountName, year, cancellationToken).ConfigureAwait(false);
                if (reply == null) return written > 0 ? CollectorOutcome.Ok(written) : CollectorOutcome.Failed(CollectorErrors.Network);
                if (row == null)
                {
                    row = new YearInReviewRecord { SteamId = steamId, Year = year };
                    db.YearInReviewTable.Add(row);
                    existing[year] = row;
                }
                // 拒绝不覆盖已经读到的内容
                if (reply.Payload != null || row.Payload == null)
                {
                    row.Result = reply.Result;
                    row.Payload = reply.Payload;
                }
                row.FetchedAt = now.ToUnixTimeSeconds();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (reply.Payload != null) written++;
            }
        }
        return CollectorOutcome.Ok(written);
    }

    /// <summary>读到过的往年不再读；12 月的当年回顾每次都更新；拒绝过的一个月后再试。</summary>
    private static bool Due(YearInReviewRecord row, int year, DateTimeOffset now)
    {
        if (row.Payload != null) return year == now.Year;
        return now - DateTimeOffset.FromUnixTimeSeconds(row.FetchedAt) >= RetryDenied;
    }
}

/// <summary>
/// 云存档写入时间（userdata/{accountId}/{appId}/remotecache.vdf）。每个文件只保留最后一次写入的时间，
/// 所以每次看到新的时间都记一条：第一次运行时读到的那些就是安装之前的游玩痕迹。只读，不记路径。
/// </summary>
internal sealed class CloudSaveTraceCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    TimeProvider timeProvider,
    ILogger<CloudSaveTraceCollector> logger) : IArchiveCollector
{
    public string Id => "cloudTrace";

    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var steamPath = installLocator.ReadSteamRegistry().SteamPath;
        if (string.IsNullOrWhiteSpace(steamPath)) return CollectorOutcome.Skipped;
        var failures = 0;
        var traces = await Task.Run(() => Scan(steamPath, timeProvider.GetUtcNow().ToUnixTimeSeconds(), exception =>
        {
            failures++;
            logger.LogInformation("Skipped an unreadable remote cache with {ExceptionType}", exception.GetType().Name);
        }), cancellationToken).ConfigureAwait(false);
        var written = await SaveAsync(traces, cancellationToken).ConfigureAwait(false);
        return failures > 0 && traces.Count == 0 ? CollectorOutcome.Failed(CollectorErrors.Read) : CollectorOutcome.Ok(written);
    }

    internal readonly record struct Trace(string SteamId, int AppId, string FileKey, long WrittenAt);

    internal static IReadOnlyList<Trace> Scan(string steamPath, long now, Action<Exception> onError)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];
        var result = new List<Trace>();
        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            if (!long.TryParse(Path.GetFileName(userDir), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                || accountId is <= 0 or > int.MaxValue
                || SteamIdHelper.AccountIdToSteamId((int)accountId) is not { } steamId) continue;
            foreach (var appDir in Directory.EnumerateDirectories(userDir))
            {
                if (!int.TryParse(Path.GetFileName(appDir), NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                    || appId <= 0 || SaveLocationDiscovery.ExcludedAppIds.Contains(appId)) continue;
                var cache = Path.Combine(appDir, "remotecache.vdf");
                if (!File.Exists(cache)) continue;
                try
                {
                    result.AddRange(Parse(VdfHelper.Read(cache), steamId, appId, now));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyValueException or InvalidDataException or FormatException)
                {
                    onError(exception);
                }
            }
        }
        return result;
    }

    internal static IEnumerable<Trace> Parse(KVObject root, string steamId, int appId, long now)
    {
        foreach (var file in root.Children)
        {
            if (!file.Children.Any()) continue;
            var time = Value(file, "time") ?? Value(file, "remotetime") ?? Value(file, "localtime");
            // 远在未来的时间是坏数据（时钟错了）
            if (time is not > 0 || time > now + 86400) continue;
            yield return new Trace(steamId, appId, Key(file.Name), time.Value);
        }
    }

    private static long? Value(KVObject file, string name)
    {
        var text = file.Children.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Value?.ToString(CultureInfo.InvariantCulture);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>路径可能含存档名、角色名，不存原文。</summary>
    internal static string Key(string relativePath)
        => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant())))[..16];

    internal async Task<int> SaveAsync(IReadOnlyList<Trace> traces, CancellationToken cancellationToken = default)
    {
        if (traces.Count == 0) return 0;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var group in traces.GroupBy(trace => trace.SteamId))
        {
            var seen = (await db.CloudSaveTraceTable.AsNoTracking().Where(row => row.SteamId == group.Key)
                    .Select(row => new { row.AppId, row.FileKey, row.WrittenAt }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(row => (row.AppId, row.FileKey, row.WrittenAt))
                .ToHashSet();
            foreach (var trace in group)
            {
                if (!seen.Add((trace.AppId, trace.FileKey, trace.WrittenAt))) continue;
                db.CloudSaveTraceTable.Add(new CloudSaveTrace { SteamId = trace.SteamId, AppId = trace.AppId, FileKey = trace.FileKey, WrittenAt = trace.WrittenAt, ObservedAt = now });
                written++;
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }
}
