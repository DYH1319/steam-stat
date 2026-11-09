using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Http;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Archives;

/// <summary>Steam 按平台记录的时长。时间都是 Unix 秒，0 表示没有。</summary>
public sealed record SteamPlaytimeDetail(
    int AppId,
    int ForeverMinutes,
    int WindowsMinutes,
    int MacMinutes,
    int LinuxMinutes,
    int DeckMinutes,
    int DisconnectedMinutes,
    long FirstPlayed,
    long LastPlayed,
    long FirstWindows,
    long FirstMac,
    long FirstLinux,
    long FirstDeck,
    long LastWindows,
    long LastMac,
    long LastLinux,
    long LastDeck);

public sealed record SteamFamilyMember(ulong SteamId, string? Name, int Role, long JoinedAt, bool Former);

public sealed record SteamFamilyPlaytime(ulong SteamId, int AppId, long FirstPlayed, long LatestPlayed, long Seconds, bool ByOwner);

public sealed record SteamFamilySnapshot(ulong GroupId, string Name, IReadOnlyList<SteamFamilyMember> Members, IReadOnlyList<SteamFamilyPlaytime> Playtime);

public sealed record SteamWishlistReply(IReadOnlyList<WishlistEntry> Items, bool Authenticated);

public sealed record SteamStoreSearchHit(int AppId, string Name, string? Image);

/// <summary>年度回顾的回复。Payload 是原始 protobuf，Steam 拒绝时为空（Result 是 EResult 的名字）。</summary>
public sealed record SteamYearInReviewReply(string Result, byte[]? Payload);

/// <summary>档案采集用到的 Steam 接口。需要 CM 会话的在没有会话时返回 null，由调用方下次再试。</summary>
public interface ISteamArchiveGateway
{
    /// <summary>账号已登录时，换一个网页用的访问令牌。结果在内存里缓存几小时。</summary>
    Task<SteamWebSession?> GetWebSessionAsync(string accountName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SteamPlaytimeDetail>?> GetPlaytimeDetailsAsync(string accountName, CancellationToken cancellationToken = default);

    /// <summary>没有加入家庭组时返回空组（GroupId = 0）。</summary>
    Task<SteamFamilySnapshot?> GetFamilyAsync(string accountName, CancellationToken cancellationToken = default);

    /// <summary>账号的商店地区。钱包区决定标价，没有钱包时用账号所在国家。取不到时返回 null。</summary>
    Task<string?> GetStoreCountryAsync(string accountName, CancellationToken cancellationToken = default);

    /// <summary>带令牌时能读到私密的愿望单。取不到时返回 null。</summary>
    Task<SteamWishlistReply?> GetWishlistAsync(ulong steamId, SteamWebSession? session, CancellationToken cancellationToken = default);

    /// <summary>当前在线人数。这款游戏没有人数数据时返回 -1，网络失败时返回 null。</summary>
    Task<int?> GetPlayerCountAsync(int appId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NewsItem>?> GetNewsAsync(int appId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SteamStoreSearchHit>> SearchStoreAsync(string term, string language, string countryCode, CancellationToken cancellationToken = default);

    /// <summary>Steam 年度回顾（SaleFeature.GetUserYearInReview），只读自己的。没有会话或网络失败时返回 null。</summary>
    Task<SteamYearInReviewReply?> GetYearInReviewAsync(string accountName, int year, CancellationToken cancellationToken = default);
}

internal sealed class SteamArchiveGateway(
    ISteamSessionAccessor sessionAccessor,
    SteamCredentialStore credentialStore,
    ISteamCmOperationScheduler scheduler,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<SteamArchiveGateway> logger) : ISteamArchiveGateway
{
    /// <summary>访问令牌大约一天过期，提前换。</summary>
    private static readonly TimeSpan WebSessionLifetime = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (SteamWebSession Session, DateTimeOffset Expires)> _webSessions = new(StringComparer.Ordinal);

    public async Task<SteamWebSession?> GetWebSessionAsync(string accountName, CancellationToken cancellationToken = default)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session) || session.Client.SteamID is not { } steamId)
        {
            _webSessions.TryRemove(accountName, out _);
            return null;
        }
        var now = timeProvider.GetUtcNow();
        if (_webSessions.TryGetValue(accountName, out var cached) && cached.Expires > now) return cached.Session;
        var credential = credentialStore.FindByAccountName(accountName);
        if (credential == null) return null;
        try
        {
            // 不续期刷新令牌：这里只是读网页，换掉用户保存的令牌得不偿失
            var result = await session.Client.Authentication
                .GenerateAccessTokenForAppAsync(steamId, credential.RefreshToken, allowRenewal: false)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(result.AccessToken)) return null;
            var web = new SteamWebSession(steamId.ConvertToUInt64(), result.AccessToken, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
            _webSessions[accountName] = (web, now + WebSessionLifetime);
            return web;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AuthenticationException or TaskCanceledException or TimeoutException or AsyncJobFailedException)
        {
            logger.LogInformation("Could not create a web session with {ExceptionType}", exception.GetType().Name);
            return null;
        }
    }

    public async Task<IReadOnlyList<SteamPlaytimeDetail>?> GetPlaytimeDetailsAsync(string accountName, CancellationToken cancellationToken = default)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session)) return null;
        var unified = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unified == null) return null;
        var player = unified.CreateService<Player>();
        var response = await scheduler.RunAsync(
            accountName,
            "archive-playtime",
            session.Generation,
            async _ => await player.ClientGetLastPlayedTimes(new CPlayer_GetLastPlayedTimes_Request { min_last_played = 0 }),
            cancellationToken).ConfigureAwait(false);
        if (response.Result != EResult.OK) return null;
        return (response.Body?.games ?? []).Select(game => new SteamPlaytimeDetail(
            game.appid,
            game.playtime_forever,
            game.playtime_windows_forever,
            game.playtime_mac_forever,
            game.playtime_linux_forever,
            game.playtime_deck_forever,
            (int)game.playtime_disconnected,
            game.first_playtime,
            game.last_playtime,
            game.first_windows_playtime,
            game.first_mac_playtime,
            game.first_linux_playtime,
            game.first_deck_playtime,
            game.last_windows_playtime,
            game.last_mac_playtime,
            game.last_linux_playtime,
            game.last_deck_playtime)).ToArray();
    }

    public async Task<SteamFamilySnapshot?> GetFamilyAsync(string accountName, CancellationToken cancellationToken = default)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session) || session.Client.SteamID is not { } steamId) return null;
        var unified = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unified == null) return null;
        var family = unified.CreateService<FamilyGroups>();
        var group = await scheduler.RunAsync(
            accountName,
            "archive-family-group",
            session.Generation,
            async _ => await family.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request
            {
                steamid = steamId.ConvertToUInt64(),
                include_family_group_response = true
            }),
            cancellationToken).ConfigureAwait(false);
        if (group.Result != EResult.OK) return null;
        var body = group.Body;
        if (body == null || body.family_groupid == 0) return new SteamFamilySnapshot(0, string.Empty, [], []);

        var groupId = body.family_groupid;
        var summary = await scheduler.RunAsync(
            accountName,
            "archive-family-playtime",
            session.Generation,
            async _ => await family.GetPlaytimeSummary(new CFamilyGroups_GetPlaytimeSummary_Request { family_groupid = groupId }),
            cancellationToken).ConfigureAwait(false);
        if (summary.Result != EResult.OK) return null;

        var friends = session.Client.GetHandler<SteamFriends>();
        var members = new List<SteamFamilyMember>();
        foreach (var member in body.family_group?.members ?? [])
            members.Add(new SteamFamilyMember(member.steamid, PersonaName(friends, steamId, member.steamid), (int)member.role, member.time_joined, false));
        foreach (var member in body.family_group?.former_members ?? [])
            members.Add(new SteamFamilyMember(member.steamid, PersonaName(friends, steamId, member.steamid), 0, 0, true));

        var entries = (summary.Body?.entries ?? []).Select(entry => ToPlaytime(entry, false))
            .Concat((summary.Body?.entries_by_owner ?? []).Select(entry => ToPlaytime(entry, true)))
            .ToArray();
        return new SteamFamilySnapshot(groupId, body.family_group?.name ?? string.Empty, members, entries);
    }

    private static SteamFamilyPlaytime ToPlaytime(CFamilyGroups_PlaytimeEntry entry, bool byOwner)
        => new(entry.steamid, (int)entry.appid, entry.first_played, entry.latest_played, entry.seconds_played, byOwner);

    private static string? PersonaName(SteamFriends? friends, SteamID self, ulong steamId)
    {
        if (friends == null) return null;
        var name = steamId == self.ConvertToUInt64() ? friends.GetPersonaName() : friends.GetFriendPersonaName(new SteamID(steamId));
        return string.IsNullOrWhiteSpace(name) || name == "[unknown]" ? null : name;
    }

    public async Task<string?> GetStoreCountryAsync(string accountName, CancellationToken cancellationToken = default)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session) || session.Client.SteamID is not { } steamId) return null;
        var unified = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unified == null) return null;
        var account = unified.CreateService<UserAccount>();
        try
        {
            var wallet = await scheduler.RunAsync(
                accountName,
                "archive-wallet-region",
                session.Generation,
                async _ => await account.GetClientWalletDetails(new CUserAccount_GetClientWalletDetails_Request()),
                cancellationToken).ConfigureAwait(false);
            if (wallet.Result == EResult.OK
                && (StoreRegions.Normalize(wallet.Body?.wallet_country_code) ?? StoreRegions.Normalize(wallet.Body?.user_country_code)) is { } region)
                return region;
            var country = await scheduler.RunAsync(
                accountName,
                "archive-user-country",
                session.Generation,
                async _ => await account.GetUserCountry(new CUserAccount_GetUserCountry_Request { steamid = steamId.ConvertToUInt64() }),
                cancellationToken).ConfigureAwait(false);
            return country.Result == EResult.OK ? StoreRegions.Normalize(country.Body?.country) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TaskCanceledException or TimeoutException or AsyncJobFailedException or InvalidOperationException)
        {
            // 没有地区只是退回到账单或语言，不影响模拟
            logger.LogInformation("Could not read the store country with {ExceptionType}", exception.GetType().Name);
            return null;
        }
    }

    public async Task<SteamWishlistReply?> GetWishlistAsync(ulong steamId, SteamWebSession? session, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"IWishlistService/GetWishlist/v1/?steamid={steamId}");
        var authenticated = session != null && session.SteamId == steamId;
        if (authenticated) path += $"&access_token={Uri.EscapeDataString(session!.AccessToken)}";
        var body = await GetJsonAsync(path, "archive-wishlist", cancellationToken).ConfigureAwait(false);
        if (body == null) return null;
        try
        {
            return new SteamWishlistReply(ParseWishlist(body), authenticated);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<WishlistEntry> ParseWishlist(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var root)
            || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return [];
        var output = new List<WishlistEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("appid", out var id) || !id.TryGetInt32(out var appId) || appId <= 0) continue;
            long? addedAt = item.TryGetProperty("date_added", out var date) && date.TryGetInt64(out var unix) && unix > 0 ? unix : null;
            int? priority = item.TryGetProperty("priority", out var order) && order.TryGetInt32(out var value) ? value : null;
            output.Add(new WishlistEntry(appId, addedAt, priority));
        }
        return output;
    }

    public async Task<int?> GetPlayerCountAsync(int appId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamWebApi);
            using var request = new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}"));
            SteamHttpRequestOptions.SetOperation(request, "archive-players");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // 线上实测：没有人数数据的应用返回 404 和 {"response":{"result":42}}
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return -1;
            if (!response.IsSuccessStatusCode) return null;
            return ParsePlayerCount(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or SteamRateLimiterRejectedException or Polly.ExecutionRejectedException)
        {
            logger.LogInformation("Player count unavailable with {ExceptionType}", exception.GetType().Name);
            return null;
        }
    }

    internal static int? ParsePlayerCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response)) return null;
        var result = response.TryGetProperty("result", out var value) && value.TryGetInt32(out var code) ? code : 0;
        if (result != 1) return -1;
        return response.TryGetProperty("player_count", out var count) && count.TryGetInt32(out var players) ? players : null;
    }

    public async Task<IReadOnlyList<NewsItem>?> GetNewsAsync(int appId, CancellationToken cancellationToken = default)
    {
        var body = await GetJsonAsync(string.Create(CultureInfo.InvariantCulture, $"ISteamNews/GetNewsForApp/v2/?appid={appId}&count=10&maxlength=200&feeds=steam_community_announcements"), "archive-news", cancellationToken).ConfigureAwait(false);
        if (body == null) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("appnews", out var root)
                || !root.TryGetProperty("newsitems", out var items)
                || items.ValueKind != JsonValueKind.Array) return [];
            return items.EnumerateArray()
                .Select(item => new NewsItem(
                    item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("contents", out var contents) ? contents.GetString() : null,
                    item.TryGetProperty("date", out var date) && date.TryGetInt64(out var unix) ? unix : 0,
                    item.TryGetProperty("url", out var url) ? url.GetString() : null))
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SteamStoreSearchHit>> SearchStoreAsync(string term, string language, string countryCode, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamStore);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/storesearch/?term={Uri.EscapeDataString(term)}&l={Uri.EscapeDataString(language)}&cc={Uri.EscapeDataString(countryCode)}");
            SteamHttpRequestOptions.SetOperation(request, "archive-store-search");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            return items.EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "app")
                .Select(item => new SteamStoreSearchHit(
                    item.GetProperty("id").GetInt32(),
                    item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("tiny_image", out var image) ? image.GetString() : null))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or SteamRateLimiterRejectedException or Polly.ExecutionRejectedException)
        {
            logger.LogInformation("Store search unavailable with {ExceptionType}", exception.GetType().Name);
            return [];
        }
    }

    public async Task<SteamYearInReviewReply?> GetYearInReviewAsync(string accountName, int year, CancellationToken cancellationToken = default)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session) || session.Client.SteamID is not { } steamId) return null;
        var unified = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unified == null) return null;
        try
        {
            unified.CreateService<SaleFeatureUnifiedService>();
            var response = await scheduler.RunAsync(
                accountName,
                "archive-year-in-review",
                session.Generation,
                async _ => await unified.SendMessage<CSaleFeature_GetUserYearInReview_Request, CSaleFeature_GetUserYearInReview_Response>(
                    YearInReviewProtocol.GetUserYearInReview,
                    new CSaleFeature_GetUserYearInReview_Request { steamid = steamId.ConvertToUInt64(), year = (uint)year }),
                cancellationToken).ConfigureAwait(false);
            if (response.Result != EResult.OK || response.Body == null) return new SteamYearInReviewReply(response.Result.ToString(), null);
            using var stream = new MemoryStream();
            ProtoBuf.Serializer.Serialize(stream, response.Body);
            return new SteamYearInReviewReply(EResult.OK.ToString(), stream.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TaskCanceledException or TimeoutException or AsyncJobFailedException or InvalidOperationException)
        {
            logger.LogInformation("Year in review unavailable with {ExceptionType}", exception.GetType().Name);
            return null;
        }
    }

    private async Task<string?> GetJsonAsync(string path, string operation, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamWebApi);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            SteamHttpRequestOptions.SetOperation(request, operation);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or SteamRateLimiterRejectedException or Polly.ExecutionRejectedException)
        {
            logger.LogInformation("Archive request {Operation} failed with {ExceptionType}", operation, exception.GetType().Name);
            return null;
        }
    }
}
