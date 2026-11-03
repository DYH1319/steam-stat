using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Sessions;

namespace ElectronNet.Services;

/// <summary>
/// 自己的资料变化：昵称、头像、等级与展示徽章的字段级历史。
/// 来源：登录会话就绪后经 CM 采集、好友快照中的当前用户（实时）、本机 loginusers.vdf（离线）。
/// 值与上一条不同时才写入；首次观测作为基线。头像会另存一份本地副本，避免 CDN 失效后丢失。
/// </summary>
internal sealed class ProfileHistoryService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamProfileGateway profileGateway,
    ISteamAvatarUriProvider avatarUriProvider,
    ISteamSessionAccessor sessionAccessor,
    ISteamAccountIdentityProvider identityProvider,
    FileHelper fileHelper,
    IAppPaths appPaths,
    TimeProvider timeProvider,
    ILogger<ProfileHistoryService> logger,
    IAppNameResolver? appNameResolver = null,
    ILanguageProvider? languageProvider = null) : IEventHandler<FriendsChanged>, IEventHandler<SteamSessionReady>, IDisposable
{
    public const string PersonaName = "personaName";
    public const string Avatar = "avatar";
    public const string Level = "level";
    public const string FavoriteBadge = "favoriteBadge";

    private static readonly TimeSpan SessionReadyDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LocalInterval = TimeSpan.FromHours(1);
    /// <summary>查不到名称的徽章过一段时间再试，避免每次采集都请求。</summary>
    private static readonly TimeSpan BadgeNameRetryInterval = TimeSpan.FromDays(3);
    private const int BadgeNameLookupLimit = 20;

    private readonly Lock _badgeNameLock = new();
    private Dictionary<string, BadgeNameEntry>? _badgeNames;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _timerLock = new();
    private readonly Dictionary<string, ITimer> _readyTimers = new(StringComparer.Ordinal);
    private Dictionary<(string SteamId, string Field), string>? _latest;
    private ITimer? _timer;

    /// <summary>定期把本机账号（loginusers.vdf / 已同步资料）的昵称写入历史。</summary>
    public void StartSchedule()
    {
        if (_timer != null) return;
        _timer = timeProvider.CreateTimer(_ => _ = ObserveLocalUsersAsync(), null, TimeSpan.FromSeconds(60), LocalInterval);
    }

    public Task HandleAsync(FriendsChanged message, CancellationToken cancellationToken)
    {
        var user = message.Data.CurrentUser;
        if (string.IsNullOrWhiteSpace(user.SteamId)) return Task.CompletedTask;
        // 好友快照变化非常频繁：交给后台串行比较，不阻塞事件总线
        _ = ObserveAsync(user.SteamId, "friends", NormalizeName(user.PersonaName), NormalizeAvatar(user.AvatarHash), user.Level, null);
        return Task.CompletedTask;
    }

    public Task HandleAsync(SteamSessionReady message, CancellationToken cancellationToken)
    {
        // 会话刚就绪时自己的头像 / 昵称可能尚未下发：延迟采集
        lock (_timerLock)
        {
            if (_readyTimers.Remove(message.AccountName, out var previous)) previous.Dispose();
            _readyTimers[message.AccountName] = timeProvider.CreateTimer(_ =>
            {
                lock (_timerLock)
                {
                    if (_readyTimers.Remove(message.AccountName, out var timer)) timer.Dispose();
                }
                _ = CaptureAccountAsync(message.AccountName, CancellationToken.None);
            }, null, SessionReadyDelay, Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    /// <summary>立即为所有已登录账号采集一次（CM），并补记本机账号的昵称。</summary>
    public async Task<ProfileHistoryCaptureResultDto> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var recorded = await ObserveLocalUsersAsync().ConfigureAwait(false);
        var accounts = sessionAccessor.GetLoggedInUsers();
        foreach (var accountName in accounts)
            recorded += await CaptureAccountAsync(accountName, cancellationToken).ConfigureAwait(false);
        return new ProfileHistoryCaptureResultDto
        {
            Accounts = accounts.Count,
            Recorded = recorded,
            Error = accounts.Count == 0 ? "notLoggedIn" : null
        };
    }

    private async Task<int> CaptureAccountAsync(string accountName, CancellationToken cancellationToken)
    {
        try
        {
            if (!identityProvider.TryGetSteamId(accountName, out var steamId)) return 0;
            var profile = await profileGateway.GetProfileAsync(accountName, steamId, cancellationToken).ConfigureAwait(false);
            var badge = await profileGateway.GetFavoriteBadgeAsync(accountName, steamId, cancellationToken).ConfigureAwait(false);
            if (!profile.IsSuccess && !badge.IsSuccess) return 0;
            // 游戏徽章 / 活动徽章（如季节性促销）的应用不一定在本机库中：采集时顺带解析名称
            string? badgeAppName = null;
            if (badge is { IsSuccess: true, Value: { HasBadge: true, AppId: > 0 } badgeValue } && appNameResolver != null)
            {
                try
                {
                    badgeAppName = await appNameResolver.ResolveNameAsync(badgeValue.AppId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug("Failed to resolve the favorite badge app name with {ExceptionType}", exception.GetType().Name);
                }
            }
            var recorded = await ObserveAsync(
                steamId.ToString(CultureInfo.InvariantCulture),
                "cm",
                profile.IsSuccess ? NormalizeName(profile.Value?.PersonaName) : null,
                profile.IsSuccess ? NormalizeAvatar(profile.Value?.AvatarHash) : null,
                profile.IsSuccess ? profile.Value?.Level : null,
                badge is { IsSuccess: true, Value: { } value } ? SerializeBadge(value, badgeAppName) : null).ConfigureAwait(false);
            if (badge.IsSuccess)
                await ResolveBadgeNamesAsync(accountName, steamId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return recorded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Failed to capture profile history with {ExceptionType}", exception.GetType().Name);
            return 0;
        }
    }

    private async Task<int> ObserveLocalUsersAsync()
    {
        try
        {
            List<(string SteamId, string? PersonaName)> users;
            await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
            {
                users = (await db.SteamUserTable.AsNoTracking()
                        .Select(user => new { user.SteamId, user.PersonaName })
                        .ToListAsync().ConfigureAwait(false))
                    .Select(user => (user.SteamId, user.PersonaName))
                    .ToList();
            }
            var recorded = 0;
            foreach (var (steamId, personaName) in users)
                recorded += await ObserveAsync(steamId, "local", NormalizeName(personaName), null, null, null).ConfigureAwait(false);
            return recorded;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to observe local Steam users for profile history");
            return 0;
        }
    }

    /// <summary>与每个字段的最新值比较，变化时追加一条记录；返回写入条数。</summary>
    internal async Task<int> ObserveAsync(
        string steamId,
        string source,
        string? personaName,
        string? avatarHash,
        int? level,
        string? favoriteBadge)
    {
        if (string.IsNullOrWhiteSpace(steamId) || !ulong.TryParse(steamId, out _)) return 0;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_latest == null)
            {
                await using var loader = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                _latest = await LoadLatestAsync(loader).ConfigureAwait(false);
            }
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var added = new List<ProfileHistoryEntry>();
            void Track(string field, string? value)
            {
                if (value == null) return;
                _latest!.TryGetValue((steamId, field), out var previous);
                if (previous == value || (field == FavoriteBadge && BadgeKey(previous) == BadgeKey(value))) return;
                added.Add(new ProfileHistoryEntry
                {
                    SteamId = steamId,
                    Field = field,
                    Value = value.Length > 1024 ? value[..1024] : value,
                    PreviousValue = previous,
                    ObservedAt = now,
                    Source = source
                });
                _latest[(steamId, field)] = value;
            }
            Track(PersonaName, personaName);
            Track(Avatar, avatarHash);
            Track(Level, level?.ToString(CultureInfo.InvariantCulture));
            Track(FavoriteBadge, favoriteBadge);
            // 好友快照变化非常频繁：只有确实有变化时才打开数据库
            if (added.Count == 0) return 0;
            await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            db.ProfileHistoryTable.AddRange(added);
            await db.SaveChangesAsync().ConfigureAwait(false);

            // 头像另存本地副本（失败不影响记录，渲染层回退到 CDN）
            var avatar = added.FirstOrDefault(entry => entry.Field == Avatar);
            if (avatar != null)
            {
                var path = await DownloadAvatarAsync(avatar.Value).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path))
                {
                    avatar.LocalPath = path;
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            return added.Count;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to record profile history");
            // 缓存可能与数据库不一致：下次重新加载
            _latest = null;
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ProfileHistoryEntryDto> Get(ProfileHistoryQueryRequest request)
    {
        using var db = dbContextFactory.CreateDbContext();
        var query = db.ProfileHistoryTable.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.SteamId)) query = query.Where(row => row.SteamId == request.SteamId);
        return query.OrderByDescending(row => row.ObservedAt).ThenByDescending(row => row.Id)
            .Take(5000)
            .AsEnumerable()
            .Select(row => new ProfileHistoryEntryDto
            {
                Id = row.Id,
                SteamId = row.SteamId,
                Field = row.Field,
                Value = row.Field == FavoriteBadge ? EnrichBadge(row.Value) : row.Value,
                PreviousValue = row.Field == FavoriteBadge && row.PreviousValue != null ? EnrichBadge(row.PreviousValue) : row.PreviousValue,
                ObservedAt = row.ObservedAt,
                Source = row.Source,
                LocalPath = row.LocalPath != null && File.Exists(row.LocalPath) ? row.LocalPath : null
            })
            .ToArray();
    }

    private static async Task<Dictionary<(string SteamId, string Field), string>> LoadLatestAsync(AppDbContext db)
    {
        var rows = await db.ProfileHistoryTable.AsNoTracking()
            .Select(row => new { row.Id, row.SteamId, row.Field, row.Value, row.ObservedAt })
            .ToListAsync().ConfigureAwait(false);
        return rows
            .GroupBy(row => (row.SteamId, row.Field))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(row => row.ObservedAt).ThenByDescending(row => row.Id).First().Value);
    }

    private async Task<string?> DownloadAvatarAsync(string hash)
    {
        var uri = avatarUriProvider.GetAvatarUri(hash, SteamAvatarSize.Full);
        if (uri == null) return null;
        var directory = Path.Combine(appPaths.UserDataDirectory, "ProfileHistory", "avatars");
        var existing = Directory.Exists(directory) ? Directory.GetFiles(directory, hash + ".*").FirstOrDefault() : null;
        if (existing != null) return existing;
        return await fileHelper.DownloadFileAsync(uri.ToString(), directory, hash).ConfigureAwait(false);
    }

    /// <summary>值比较只用徽章本身的字段；应用名单独存放，读取时缺失再从缓存补齐。</summary>
    internal static string SerializeBadge(SteamFavoriteBadgeSnapshot badge, string? appName = null)
        => badge.HasBadge
            ? JsonSerializer.Serialize(new
            {
                badgeId = badge.BadgeId,
                level = badge.Level,
                appId = badge.AppId,
                communityItemId = badge.CommunityItemId.ToString(CultureInfo.InvariantCulture),
                borderColor = badge.BorderColor,
                appName = string.IsNullOrWhiteSpace(appName) ? null : appName
            })
            : "none";

    /// <summary>徽章比较键：忽略只用于显示的应用名。</summary>
    internal static string? BadgeKey(string? value)
    {
        if (value == null || value == "none") return value;
        try
        {
            if (JsonNode.Parse(value) is not JsonObject node) return value;
            node.Remove("appName");
            node.Remove("badgeName");
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    /// <summary>读取时补齐显示名：徽章名来自徽章名缓存，应用名优先用记录里的，其次本地应用表，再次徽章名缓存。</summary>
    private string EnrichBadge(string value)
    {
        if (value == "none") return value;
        try
        {
            if (JsonNode.Parse(value) is not JsonObject node) return value;
            var appId = node["appId"]?.GetValue<uint>() ?? 0;
            if (appId == 0) return value;
            var cached = FindBadgeName(appId);
            var changed = false;
            if (!string.IsNullOrWhiteSpace(cached?.BadgeName))
            {
                node["badgeName"] = cached.BadgeName;
                changed = true;
            }
            if (!(node["appName"] is JsonValue existing && existing.TryGetValue<string>(out var known) && !string.IsNullOrWhiteSpace(known)))
            {
                var name = appNameResolver?.GetCachedName(appId);
                if (string.IsNullOrWhiteSpace(name)) name = cached?.AppName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    node["appName"] = name;
                    changed = true;
                }
            }
            return changed ? node.ToJsonString() : value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return value;
        }
    }

    internal static string? NormalizeName(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "[unknown]" ? null : value;

    /// <summary>空哈希 / 全零哈希视为未知（会话尚未下发头像），不记录。</summary>
    internal static string? NormalizeAvatar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 40 || value.All(character => character == '0')) return null;
        return value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    #region 徽章名称

    private string CurrentSteamLanguage()
    {
        try
        {
            return languageProvider?.GetSteamLanguage() ?? "english";
        }
        catch
        {
            return "english";
        }
    }

    private string BadgeNamePath => Path.Combine(appPaths.UserDataDirectory, "ProfileHistory", "badge-names.json");

    private static string BadgeNameKey(uint appId, string language) => $"{appId.ToString(CultureInfo.InvariantCulture)}:{language}";

    private Dictionary<string, BadgeNameEntry> BadgeNames()
    {
        if (_badgeNames != null) return _badgeNames;
        try
        {
            if (File.Exists(BadgeNamePath))
            {
                _badgeNames = JsonSerializer.Deserialize<Dictionary<string, BadgeNameEntry>>(File.ReadAllText(BadgeNamePath));
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug("Failed to read the badge name cache with {ExceptionType}", exception.GetType().Name);
        }
        return _badgeNames ??= new Dictionary<string, BadgeNameEntry>(StringComparer.Ordinal);
    }

    /// <summary>当前界面语言优先；没有时用任意语言已解析到的名称。</summary>
    internal BadgeNameEntry? FindBadgeName(uint appId)
    {
        lock (_badgeNameLock)
        {
            var names = BadgeNames();
            if (names.TryGetValue(BadgeNameKey(appId, CurrentSteamLanguage()), out var entry) && entry.HasName) return entry;
            var prefix = appId.ToString(CultureInfo.InvariantCulture) + ":";
            return names.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.HasName)
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }
    }

    internal void StoreBadgeName(uint appId, string language, string? badgeName, string? appName)
    {
        lock (_badgeNameLock)
        {
            BadgeNames()[BadgeNameKey(appId, language)] = new BadgeNameEntry(badgeName, appName, timeProvider.GetUtcNow().ToUnixTimeSeconds());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BadgeNamePath)!);
                File.WriteAllText(BadgeNamePath, JsonSerializer.Serialize(_badgeNames));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Failed to write the badge name cache with {ExceptionType}", exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 为该账号历史中出现过的游戏 / 活动徽章解析名称（含已被替换的旧徽章），写入缓存后读取时补齐。
    /// 徽章名不参与值比较，所以不改动已有记录。
    /// </summary>
    private async Task<int> ResolveBadgeNamesAsync(string accountName, string steamId, CancellationToken cancellationToken)
    {
        try
        {
            List<string> values;
            await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                values = await db.ProfileHistoryTable.AsNoTracking()
                    .Where(row => row.SteamId == steamId && row.Field == FavoriteBadge)
                    .Select(row => row.Value)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            var language = CurrentSteamLanguage();
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var pending = values
                .Select(BadgeAppId)
                .Where(appId => appId > 0)
                .Distinct()
                .Where(appId =>
                {
                    lock (_badgeNameLock)
                    {
                        if (!BadgeNames().TryGetValue(BadgeNameKey(appId, language), out var entry)) return true;
                        return !entry.HasName && now - entry.ResolvedAt > BadgeNameRetryInterval.TotalSeconds;
                    }
                })
                .Take(BadgeNameLookupLimit)
                .ToList();
            var resolved = 0;
            foreach (var appId in pending)
            {
                var result = await profileGateway.GetBadgeNameAsync(accountName, appId, language, cancellationToken).ConfigureAwait(false);
                if (result.Failure == SteamStat.Core.Steam.Gateway.SteamFailureKind.AuthenticationRequired) break;
                StoreBadgeName(appId, language, result.Value?.BadgeName, result.Value?.AppName);
                if (result.IsSuccess) resolved++;
            }
            return resolved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug("Failed to resolve favorite badge names with {ExceptionType}", exception.GetType().Name);
            return 0;
        }
    }

    internal static uint BadgeAppId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "none") return 0;
        try
        {
            return JsonNode.Parse(value) is JsonObject node && node["appId"] is JsonValue appId && appId.TryGetValue<uint>(out var id) ? id : 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    internal sealed record BadgeNameEntry(string? BadgeName, string? AppName, long ResolvedAt)
    {
        public bool HasName => !string.IsNullOrWhiteSpace(BadgeName) || !string.IsNullOrWhiteSpace(AppName);
    }

    #endregion

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_timerLock)
        {
            foreach (var timer in _readyTimers.Values) timer.Dispose();
            _readyTimers.Clear();
        }
        _gate.Dispose();
    }
}
