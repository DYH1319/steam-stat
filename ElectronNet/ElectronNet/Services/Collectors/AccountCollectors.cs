using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Sessions;

namespace ElectronNet.Services.Collectors;

/// <summary>
/// 平台时长档案：Windows / Linux / Deck 各玩了多久、各平台首末次游玩。
/// ClientGetLastPlayedTimes 一次返回全部，值变化才写一条，所以也是平台时长的历史。
/// </summary>
internal sealed class PlatformPlaytimeCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    TimeProvider timeProvider) : IArchiveCollector
{
    public string Id => "platform";

    public TimeSpan Interval => TimeSpan.FromHours(12);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;
        var written = 0;
        foreach (var account in accounts)
        {
            var details = await gateway.GetPlaytimeDetailsAsync(account.AccountName, cancellationToken).ConfigureAwait(false);
            if (details == null) return CollectorOutcome.Failed(CollectorErrors.Network);
            written += await SaveAsync(account.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture), details, cancellationToken).ConfigureAwait(false);
        }
        return CollectorOutcome.Ok(written);
    }

    internal async Task<int> SaveAsync(string steamId, IReadOnlyList<SteamPlaytimeDetail> details, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var latest = (await db.PlatformPlaytimeTable.AsNoTracking().Where(row => row.SteamId == steamId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.AppId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.ObservedAt).ThenByDescending(row => row.Id).First());
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var written = 0;
        foreach (var detail in details.Where(item => item.AppId > 0))
        {
            var row = ToRecord(steamId, detail, now);
            if (latest.TryGetValue(detail.AppId, out var previous) && Same(previous, row)) continue;
            db.PlatformPlaytimeTable.Add(row);
            written++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private static PlatformPlaytimeRecord ToRecord(string steamId, SteamPlaytimeDetail detail, long now) => new()
    {
        SteamId = steamId,
        AppId = detail.AppId,
        ObservedAt = now,
        WindowsMinutes = detail.WindowsMinutes,
        MacMinutes = detail.MacMinutes,
        LinuxMinutes = detail.LinuxMinutes,
        DeckMinutes = detail.DeckMinutes,
        DisconnectedMinutes = detail.DisconnectedMinutes,
        FirstPlayed = Time(detail.FirstPlayed),
        LastPlayed = Time(detail.LastPlayed),
        FirstWindows = Time(detail.FirstWindows),
        FirstMac = Time(detail.FirstMac),
        FirstLinux = Time(detail.FirstLinux),
        FirstDeck = Time(detail.FirstDeck),
        LastWindows = Time(detail.LastWindows),
        LastMac = Time(detail.LastMac),
        LastLinux = Time(detail.LastLinux),
        LastDeck = Time(detail.LastDeck)
    };

    private static long? Time(long value) => value > 0 ? value : null;

    private static bool Same(PlatformPlaytimeRecord left, PlatformPlaytimeRecord right)
        => left.WindowsMinutes == right.WindowsMinutes && left.MacMinutes == right.MacMinutes
           && left.LinuxMinutes == right.LinuxMinutes && left.DeckMinutes == right.DeckMinutes
           && left.DisconnectedMinutes == right.DisconnectedMinutes && left.FirstPlayed == right.FirstPlayed
           && left.LastPlayed == right.LastPlayed;
}

/// <summary>
/// 家庭共享档案：GetPlaytimeSummary 给出成员在共享库里的时长。
/// 接口只有当前的累计值，按家庭组 + 成员 + 应用覆盖。
/// </summary>
internal sealed class FamilyCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    TimeProvider timeProvider,
    ISteamProfileGateway? profiles = null) : IArchiveCollector
{
    /// <summary>每次采集最多为这么多个成员查公开昵称：家庭最多 6 人，留一点余量给已退出的成员</summary>
    private const int NameLookupLimit = 8;

    public string Id => "family";

    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;
        var written = 0;
        foreach (var account in accounts)
        {
            var family = await gateway.GetFamilyAsync(account.AccountName, cancellationToken).ConfigureAwait(false);
            if (family == null) return CollectorOutcome.Failed(CollectorErrors.Network);
            family = await FillMissingNamesAsync(family, cancellationToken).ConfigureAwait(false);
            written += await SaveAsync(family, cancellationToken).ConfigureAwait(false);
        }
        return CollectorOutcome.Ok(written);
    }

    /// <summary>
    /// 家庭接口不一定给成员昵称（不是好友的成员常常为空），档案里也还没有名字时用公开 miniprofile 补。
    /// 取不到就保持为空，界面显示「成员·后四位」
    /// </summary>
    internal async Task<SteamFamilySnapshot> FillMissingNamesAsync(SteamFamilySnapshot family, CancellationToken cancellationToken = default)
    {
        if (profiles == null || family.Members.All(member => !string.IsNullOrWhiteSpace(member.Name))) return family;
        HashSet<string> named;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            named = (await db.FamilyMemberTable.AsNoTracking()
                .Where(row => row.Name != null && row.Name != "")
                .Select(row => row.SteamId)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        }
        var lookups = 0;
        var members = new List<SteamFamilyMember>(family.Members.Count);
        foreach (var member in family.Members)
        {
            var steamId = member.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(member.Name) || named.Contains(steamId) || lookups >= NameLookupLimit)
            {
                members.Add(member);
                continue;
            }
            lookups++;
            var profile = await profiles.GetPublicProfileAsync(member.SteamId, cancellationToken).ConfigureAwait(false);
            members.Add(profile is { IsSuccess: true, Value.PersonaName: { } name } ? member with { Name = name } : member);
        }
        return family with { Members = members };
    }

    internal async Task<int> SaveAsync(SteamFamilySnapshot family, CancellationToken cancellationToken = default)
    {
        if (family.GroupId == 0) return 0;
        var groupId = family.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var members = await db.FamilyMemberTable.Where(row => row.FamilyGroupId == groupId).ToDictionaryAsync(row => row.SteamId, cancellationToken).ConfigureAwait(false);
        foreach (var member in family.Members)
        {
            var steamId = member.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!members.TryGetValue(steamId, out var row))
            {
                row = new FamilyMemberRecord { FamilyGroupId = groupId, SteamId = steamId };
                db.FamilyMemberTable.Add(row);
                members[steamId] = row;
            }
            row.Name = member.Name ?? row.Name;
            row.Role = member.Role;
            row.JoinedAt = member.JoinedAt > 0 ? member.JoinedAt : row.JoinedAt;
            row.Former = member.Former;
            row.UpdatedAt = now;
        }

        var existing = (await db.FamilyPlaytimeTable.Where(row => row.FamilyGroupId == groupId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => (row.SteamId, row.AppId, row.ByOwner));
        foreach (var entry in family.Playtime.Where(item => item.AppId > 0))
        {
            var steamId = entry.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var key = (steamId, entry.AppId, entry.ByOwner);
            if (!existing.TryGetValue(key, out var row))
            {
                row = new FamilyPlaytimeRecord { FamilyGroupId = groupId, SteamId = steamId, AppId = entry.AppId, ByOwner = entry.ByOwner };
                db.FamilyPlaytimeTable.Add(row);
                existing[key] = row;
            }
            row.FirstPlayed = entry.FirstPlayed > 0 ? entry.FirstPlayed : row.FirstPlayed;
            row.LatestPlayed = entry.LatestPlayed > 0 ? entry.LatestPlayed : row.LatestPlayed;
            // 累计值不会变小，变小说明接口这次只给了一部分
            row.Seconds = Math.Max(row.Seconds, entry.Seconds);
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return family.Playtime.Count;
    }
}

/// <summary>
/// 好友时长快照：只对手动追踪的好友，只记公开时长，值变化才写。
/// 第一条是基线，之后的差值就是这段时间玩的，比从在线记录推算准。
/// </summary>
internal sealed class FriendPlaytimeCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamFriendProfileGateway friendProfiles,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    TimeProvider timeProvider) : IArchiveCollector
{
    internal TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(3);

    public string Id => "friendPlaytime";

    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var names = accounts.Select(account => account.AccountName).ToList();
        var tracked = await db.FriendTrackingTable.AsNoTracking().Where(row => names.Contains(row.AccountName)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var group in tracked.GroupBy(row => row.FriendSteamId))
        {
            if (!ulong.TryParse(group.Key, out var friendId)) continue;
            var result = await friendProfiles.GetLibraryAsync(group.First().AccountName, friendId, cancellationToken).ConfigureAwait(false);
            // 对方游戏库不公开时什么都不记，这不是失败
            if (result is { IsSuccess: true, Value: { } library })
            {
                written += await SaveAsync(group.Key, library.Games.Select(game => (game.AppId, game.PlaytimeForever)).ToArray(), cancellationToken).ConfigureAwait(false);
                written += await SaveLibraryAsync(group.Key, library.Games, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return CollectorOutcome.Ok(written);
    }

    internal async Task<int> SaveAsync(string steamId, IReadOnlyList<(int AppId, int Minutes)> games, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var latest = (await db.FriendPlaytimeSnapshotTable.AsNoTracking().Where(row => row.SteamId == steamId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.AppId)
            .ToDictionary(group => group.Key, group => group.MaxBy(row => row.ObservedAt)!.PlaytimeMinutes);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var written = 0;
        foreach (var (appId, minutes) in games)
        {
            // 从未玩过的不记，否则基线就是整个游戏库
            if (minutes <= 0 && !latest.ContainsKey(appId)) continue;
            if (latest.TryGetValue(appId, out var previous) && previous == minutes) continue;
            db.FriendPlaytimeSnapshotTable.Add(new FriendPlaytimeSnapshot { SteamId = steamId, AppId = appId, PlaytimeMinutes = minutes, ObservedAt = now });
            written++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>
    /// 游戏库和成就进度。第一次看到这位好友时全部是基线：只知道「不晚于现在」入库。
    /// 之后新出现的才算入库，时间精确到采集间隔（一天）。消失的记移除时间，再出现时清掉。
    /// </summary>
    internal async Task<int> SaveLibraryAsync(string steamId, IReadOnlyList<SteamFriendOwnedGame> games, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.FriendLibraryItemTable.Where(row => row.SteamId == steamId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var baseline = items.Count == 0;
        var known = items.ToDictionary(row => row.AppId);
        var current = games.Where(game => game.AppId > 0).DistinctBy(game => game.AppId).ToList();
        var written = 0;
        foreach (var game in current)
        {
            if (known.TryGetValue(game.AppId, out var item))
            {
                if (item.RemovedAt != null)
                {
                    item.RemovedAt = null;
                    written++;
                }
                continue;
            }
            db.FriendLibraryItemTable.Add(new FriendLibraryItem { SteamId = steamId, AppId = game.AppId, FirstSeenAt = now, IsBaseline = baseline });
            written++;
        }
        // 游戏库读到的游戏数明显变少时多半是这次只拿到一部分，不当成移除
        if (current.Count >= items.Count(row => row.RemovedAt == null) / 2)
        {
            var ids = current.Select(game => game.AppId).ToHashSet();
            foreach (var item in items.Where(row => row.RemovedAt == null && !ids.Contains(row.AppId)))
            {
                item.RemovedAt = now;
                written++;
            }
        }

        var progress = (await db.FriendAchievementProgressTable.AsNoTracking().Where(row => row.SteamId == steamId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.AppId)
            .ToDictionary(group => group.Key, group => group.MaxBy(row => row.ObservedAt)!);
        foreach (var game in current.Where(game => game.AchievementTotal > 0))
        {
            if (progress.TryGetValue(game.AppId, out var last) && last.Unlocked == game.AchievementUnlocked && last.Total == game.AchievementTotal) continue;
            // 从来没解锁过的游戏不记，否则基线就是整个游戏库
            if (last == null && game.AchievementUnlocked == 0) continue;
            db.FriendAchievementProgressTable.Add(new FriendAchievementProgress
            {
                SteamId = steamId,
                AppId = game.AppId,
                Unlocked = game.AchievementUnlocked,
                Total = game.AchievementTotal,
                ObservedAt = now
            });
            written++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }
}

/// <summary>
/// 成为好友的时间。CM 的好友列表不带时间，只有 Web API 的 friend_since 有，要访问令牌，只能读自己的。
/// 对全部好友生效：这是自己好友列表的一部分，不是对别人的记录。读不到时不覆盖已有的值。
/// </summary>
internal sealed class FriendSinceCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    TimeProvider timeProvider) : IArchiveCollector
{
    public string Id => "friendSince";

    public TimeSpan Interval => TimeSpan.FromDays(7);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;
        var written = 0;
        var failed = 0;
        foreach (var account in accounts)
        {
            var session = await gateway.GetWebSessionAsync(account.AccountName, cancellationToken).ConfigureAwait(false);
            var friends = session == null ? null : await gateway.GetFriendSinceAsync(session, cancellationToken).ConfigureAwait(false);
            if (friends == null)
            {
                failed++;
                continue;
            }
            written += await SaveAsync(account.AccountName, friends, cancellationToken).ConfigureAwait(false);
        }
        return failed == accounts.Count ? CollectorOutcome.Failed(CollectorErrors.Network) : CollectorOutcome.Ok(written);
    }

    internal async Task<int> SaveAsync(string accountName, IReadOnlyList<SteamFriendSince> friends, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.FriendSinceTable.Where(row => row.AccountName == accountName).ToDictionaryAsync(row => row.FriendSteamId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var written = 0;
        foreach (var friend in friends.Where(friend => friend.Since > 0))
        {
            var steamId = friend.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (existing.TryGetValue(steamId, out var row))
            {
                row.ObservedAt = now;
                if (row.FriendSince == friend.Since) continue;
                // 删了再加回来时 Steam 给的是新的时间
                row.FriendSince = friend.Since;
            }
            else
            {
                db.FriendSinceTable.Add(new FriendSinceRecord { AccountName = accountName, FriendSteamId = steamId, FriendSince = friend.Since, ObservedAt = now });
            }
            written++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }
}

/// <summary>
/// 愿望单历史。先不带令牌请求公开接口；公开结果为空时才用令牌再试一次，私密愿望单也能记录，
/// 令牌只在需要时才出现在请求里。
/// </summary>
internal sealed class WishlistCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    TimeProvider timeProvider) : IArchiveCollector
{
    public string Id => "wishlist";

    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var loggedIn = ArchiveAccounts.LoggedIn(sessions, identity).ToDictionary(account => account.SteamId);
        var steamIds = (await db.SteamUserTable.AsNoTracking().Select(user => user.SteamId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(id => ulong.TryParse(id, out var value) ? value : 0)
            .Where(id => id != 0)
            .Union(loggedIn.Keys)
            .ToArray();
        if (steamIds.Length == 0) return CollectorOutcome.Skipped;

        var streaks = await CollectorData.ReadAsync<Dictionary<string, int>>(db, Id, cancellationToken).ConfigureAwait(false);
        var written = 0;
        var failed = false;
        foreach (var steamId in steamIds)
        {
            var reply = await gateway.GetWishlistAsync(steamId, null, cancellationToken).ConfigureAwait(false);
            if (reply is { Items.Count: 0 } && loggedIn.TryGetValue(steamId, out var account))
            {
                var session = await gateway.GetWebSessionAsync(account.AccountName, cancellationToken).ConfigureAwait(false);
                if (session != null) reply = await gateway.GetWishlistAsync(steamId, session, cancellationToken).ConfigureAwait(false) ?? reply;
            }
            if (reply == null)
            {
                failed = true;
                continue;
            }
            var key = steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var streak = reply.Items.Count == 0 && reply.Authenticated ? streaks.GetValueOrDefault(key) + 1 : 0;
            streaks[key] = streak;
            written += await SaveAsync(key, reply, streak, cancellationToken).ConfigureAwait(false);
        }
        await CollectorData.WriteAsync(db, Id, streaks, cancellationToken).ConfigureAwait(false);
        return failed && written == 0 ? CollectorOutcome.Failed(CollectorErrors.Network) : CollectorOutcome.Ok(written);
    }

    internal async Task<int> SaveAsync(string steamId, SteamWishlistReply reply, int emptyReplies, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.WishlistRecordTable.Where(row => row.SteamId == steamId && row.RemovedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = WishlistDiff.Compute(
            open.Select(row => new ActiveWishlistItem(row.AppId, row.AddedAt)).ToArray(),
            reply.Items,
            reply.Authenticated,
            emptyReplies);
        foreach (var appId in changes.Removed.Distinct())
        {
            foreach (var row in open.Where(item => item.AppId == appId)) row.RemovedAt = now;
        }
        foreach (var item in changes.Updated)
        {
            var row = open.FirstOrDefault(entry => entry.AppId == item.AppId);
            if (row != null) row.Priority = item.Priority ?? row.Priority;
        }
        foreach (var item in changes.Added)
        {
            var addedAt = item.AddedAt ?? now;
            // 同一段可能已经结束过：Steam 把移除又撤销时 date_added 不变
            var closed = await db.WishlistRecordTable.FirstOrDefaultAsync(row => row.SteamId == steamId && row.AppId == item.AppId && row.AddedAt == addedAt, cancellationToken).ConfigureAwait(false);
            if (closed != null)
            {
                closed.RemovedAt = null;
                closed.Priority = item.Priority ?? closed.Priority;
                continue;
            }
            db.WishlistRecordTable.Add(new WishlistRecord { SteamId = steamId, AppId = item.AppId, AddedAt = addedAt, FirstSeenAt = now, Priority = item.Priority });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changes.Added.Count + changes.Removed.Count;
    }
}
