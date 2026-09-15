using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements;

public sealed class SteamAchievementsService(
    SteamAchievementOverviewQuery overviewQuery,
    IOwnedGameCatalog catalog,
    ISteamAchievementSchemaGateway schemaGateway,
    ISteamAchievementProgressGateway progressGateway,
    ILanguageProvider languageProvider)
{
    public Task<SteamAchievementOverviewResult> GetOverviewAsync(
        string accountName,
        CancellationToken cancellationToken = default)
        => overviewQuery.GetAsync(accountName, SteamRefreshMode.PreferCache, cancellationToken);

    public Task<SteamAchievementGameResult> GetGameAsync(
        string accountName,
        uint appId,
        CancellationToken cancellationToken = default)
        => GetGameCoreAsync(accountName, appId, SteamRefreshMode.PreferCache, cancellationToken);

    public Task<SteamAchievementGameResult> RefreshGameAsync(
        string accountName,
        uint appId,
        CancellationToken cancellationToken = default)
        => GetGameCoreAsync(accountName, appId, SteamRefreshMode.RequireRefresh, cancellationToken);

    private async Task<SteamAchievementGameResult> GetGameCoreAsync(
        string accountName,
        uint appId,
        SteamRefreshMode refreshMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentOutOfRangeException.ThrowIfZero(appId);
        var games = await catalog.GetCachedAsync(accountName, cancellationToken).ConfigureAwait(false);
        var language = languageProvider.GetSteamLanguage();
        var game = games.FirstOrDefault(item => item.AppId == appId);
        if (game == null)
        {
            return SteamAchievementMerge.Compose(
                appId,
                language,
                SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                    SteamFailureKind.NotFound,
                    SteamAchievementDiagnosticCodes.AppUnavailable),
                SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.NotFound,
                    SteamAchievementDiagnosticCodes.AppUnavailable));
        }

        var appName = !string.IsNullOrEmpty(game.LocalizedName) ? game.LocalizedName : game.Name;
        var schemaTask = schemaGateway.GetSchemaAsync(
            appId, language, accountName, refreshMode, cancellationToken);
        var unlocksTask = progressGateway.GetUnlocksAsync(
            accountName, appId, refreshMode, cancellationToken);
        await Task.WhenAll(schemaTask, unlocksTask).ConfigureAwait(false);
        var schema = await schemaTask.ConfigureAwait(false);
        var unlocks = await unlocksTask.ConfigureAwait(false);
        return SteamAchievementMerge.Compose(appId, language, schema, unlocks)
            with { AppName = appName };
    }
}
