using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements;

public sealed class SteamAchievementOverviewQuery(
    IOwnedGameCatalog catalog,
    ISteamAchievementProgressGateway progressGateway)
{
    public async Task<SteamAchievementOverviewResult> GetAsync(
        string accountName,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var games = await catalog.GetCachedAsync(accountName, cancellationToken).ConfigureAwait(false);
        var appIds = games.Select(game => game.AppId).ToArray();
        var progress = await progressGateway.GetSummariesAsync(
            accountName, appIds, refreshMode, cancellationToken).ConfigureAwait(false);
        var progressByAppId = new Dictionary<uint, SteamAchievementAppProgressSnapshot>();
        if (progress.IsSuccess && progress.Value != null)
            foreach (var summary in progress.Value)
                progressByAppId.TryAdd(summary.AppId, summary);
        var items = games
            .Select(game => new SteamAchievementOverviewItem(
                game.AppId,
                game.Name,
                game.LocalizedName,
                game.PlaytimeForever,
                game.LastPlayedAt,
                progressByAppId.GetValueOrDefault(game.AppId)))
            .ToArray();
        return new SteamAchievementOverviewResult(
            accountName,
            items,
            SteamAchievementResourceState.From(progress));
    }
}
