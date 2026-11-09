using System.Globalization;
using ElectronNet.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Insights;
using SteamStat.Core.Memoir;
using SteamStat.Core.Settings;
using SteamStat.Core.Steam.Cache;

namespace ElectronNet.Features.Memoir;

/// <summary>
/// 语义视图之外的回忆录数据：好友快照、年度回顾、游戏库里的官方首玩时间和游戏名。
/// 隐藏游戏在这里剔除，和语义视图的口径一致。
/// </summary>
internal sealed class HostMemoirCatalog(
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamFeatureSnapshotStore snapshotStore,
    SteamLibraryService libraryService,
    IAppNameResolver appNames,
    ISettingsStore settingsStore,
    ILogger<HostMemoirCatalog> logger) : IMemoirCatalog
{
    private HashSet<int> Hidden => (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();

    public async Task<IReadOnlyList<MemoirFriend>> GetFriendsAsync(InsightScope scope, CancellationToken cancellationToken = default)
    {
        var snapshots = await snapshotStore.GetFriendsAsync(cancellationToken).ConfigureAwait(false);
        var friends = new List<MemoirFriend>();
        foreach (var snapshot in snapshots)
        {
            var owner = snapshot.Value.CurrentUser.SteamId;
            if (string.IsNullOrEmpty(owner) || !scope.Includes(owner)) continue;
            // 3 = 好友。待处理的请求和屏蔽的不算
            friends.AddRange(snapshot.Value.Friends
                .Where(friend => friend.Relationship == 3 && !string.IsNullOrEmpty(friend.SteamId))
                .Select(friend => new MemoirFriend(owner, friend.SteamId, friend.PersonaName)));
        }
        return friends;
    }

    public async Task<IReadOnlyList<YearReview>> GetYearReviewsAsync(InsightScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.YearInReviewTable.AsNoTracking().Where(row => row.Payload != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hidden = Hidden;
        var reviews = new List<YearReview>();
        foreach (var row in rows.Where(row => scope.Includes(row.SteamId)))
        {
            var review = YearReviewParser.Parse(row.SteamId, row.Year, row.FetchedAt, row.Payload!);
            if (review == null)
            {
                logger.LogInformation("Skipped a year in review that could not be parsed");
                continue;
            }
            if (!review.IsEmpty) reviews.Add(hidden.Count == 0 ? review : WithoutHidden(review, hidden));
        }
        return reviews.OrderBy(review => review.Year).ThenBy(review => review.SteamId, StringComparer.Ordinal).ToList();
    }

    /// <summary>隐藏游戏从回顾里拿掉，知道它的时长时从月度合计里也扣掉。</summary>
    internal static YearReview WithoutHidden(YearReview review, IReadOnlySet<int> hidden)
    {
        var months = review.Months.Select(month =>
        {
            var removed = month.Games.Where(game => hidden.Contains(game.AppId)).Sum(game => review.SecondsOf(month.Month, game.AppId) ?? 0);
            return month with
            {
                Seconds = month.Seconds is { } seconds ? Math.Max(0, seconds - removed) : null,
                Games = month.Games.Where(game => !hidden.Contains(game.AppId)).ToList()
            };
        }).ToList();
        var removedTotal = review.Games.Where(game => hidden.Contains(game.AppId)).Sum(game => game.Seconds ?? 0);
        var games = review.Games.Where(game => !hidden.Contains(game.AppId)).ToList();
        return review with
        {
            Seconds = review.Seconds is { } total ? Math.Max(0, total - removedTotal) : null,
            Months = months,
            Games = games,
            GamesPlayed = games.Count,
            NewGames = games.Count(game => game.NewThisYear),
            Streak = review.Streak == null ? null : review.Streak with { AppIds = review.Streak.AppIds.Where(appId => !hidden.Contains(appId)).ToList() }
        };
    }

    public async Task<IReadOnlyList<MemoirFirstPlay>> GetLibraryFirstPlaysAsync(InsightScope scope, CancellationToken cancellationToken = default)
    {
        var snapshot = await libraryService.GetLibrarySnapshotAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.SteamUserTable.AsNoTracking().ToDictionaryAsync(user => user.AccountName, user => user.SteamId, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var hidden = Hidden;
        var result = new List<MemoirFirstPlay>();
        foreach (var (accountName, games) in snapshot.Libraries)
        {
            if (!accounts.TryGetValue(accountName, out var steamId) || !scope.Includes(steamId)) continue;
            result.AddRange(games
                .Where(game => game.RtimeFirstPlayed > 0 && !hidden.Contains(game.AppId))
                .Select(game => new MemoirFirstPlay(steamId, game.AppId, game.RtimeFirstPlayed)));
        }
        return result;
    }

    public IReadOnlyDictionary<int, string> GetNames(IEnumerable<int> appIds)
    {
        var names = new Dictionary<int, string>();
        var missing = new List<int>();
        foreach (var appId in appIds.Distinct())
        {
            var name = appNames.GetCachedName((uint)appId);
            if (AppNameHelper.IsPlaceholder(name)) missing.Add(appId);
            else names[appId] = name!;
        }
        if (missing.Count == 0) return names;
        using var db = dbContextFactory.CreateDbContext();
        foreach (var app in db.SteamAppTable.AsNoTracking().Where(app => missing.Contains(app.AppId)).Select(app => new { app.AppId, app.Name }).ToList())
            if (!string.IsNullOrWhiteSpace(app.Name)) names.TryAdd(app.AppId, app.Name);
        foreach (var appId in missing) names.TryAdd(appId, string.Create(CultureInfo.InvariantCulture, $"App {appId}"));
        return names;
    }
}
