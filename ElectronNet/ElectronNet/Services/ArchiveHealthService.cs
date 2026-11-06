using System.Globalization;
using ElectronNet.Features.Reliability;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 档案体检：把「这份档案还靠得住吗」拆成几项可以回答的问题，每项都给出修复入口。
/// 只读，随时可以跑；完整性检查的耗时和数据库大小成正比，所以只在用户打开体检页时执行。
/// </summary>
internal sealed class ArchiveHealthService(
    IAppPaths appPaths,
    PlaytimeHistoryService playtimeHistory,
    BackupService backupService,
    ArchiveSealService sealService,
    DatabaseSnapshotter snapshotter,
    SteamFriendsService friendsService,
    ISettingsStore settingsStore,
    TimeProvider timeProvider,
    ILogger<ArchiveHealthService> logger)
{
    public async Task<ArchiveHealthReportDto> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var overview = playtimeHistory.GetOverview(new PlaytimeHistoryQueryRequest());
        var hidden = (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();
        var discrepancies = overview.Discrepancies
            .Where(item => !hidden.Contains(item.AppId))
            .Select(item => new PlaytimeGap(item.From, item.To, Math.Max(0, item.OfficialSeconds - item.LocalSeconds)))
            .ToList();
        var boundary = settingsStore.GetSettings().Analytics?.DayBoundaryHour ?? 4;
        var gaps = ArchiveHealthRules.MergeGaps(discrepancies, boundary, timeProvider.LocalTimeZone);

        var seals = await sealService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        var coverage = friendsService.GetRichPresenceAccounts()
            .Select(friendsService.GetRichPresenceCoverage)
            .OfType<RichPresenceCoverage>()
            .ToList();
        var games = overview.Games.Where(game => !hidden.Contains(game.AppId)).ToList();

        var checks = new List<ArchiveHealthCheckDto>
        {
            await CheckIntegrityAsync(cancellationToken).ConfigureAwait(false),
            ArchiveHealthRules.Snapshots(snapshotter.List(), now),
            ArchiveHealthRules.Backup(backupService.GetLastBackupAt(), ReadLastSyncAt(), now),
            ArchiveHealthRules.Gaps(gaps),
            ArchiveHealthRules.Official(games.Count, games.Count(game => game.Status == "untracked"), games.Count(game => game.Status == "overcounted"), overview.LastCapturedAt, now),
            await CheckRecoveredSessionsAsync(cancellationToken).ConfigureAwait(false),
            ArchiveHealthRules.RichPresence(coverage),
            await CheckAchievementScopeAsync(cancellationToken).ConfigureAwait(false),
            ArchiveHealthRules.Seals(seals)
        };
        return new ArchiveHealthReportDto { CheckedAt = now, Checks = checks, Gaps = gaps };
    }

    private async Task<ArchiveHealthCheckDto> CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        var size = File.Exists(appPaths.DatabaseFile) ? new FileInfo(appPaths.DatabaseFile).Length : 0;
        var healthy = await DatabaseSnapshotter.IsHealthyAsync(appPaths.DatabaseFile, cancellationToken).ConfigureAwait(false);
        return ArchiveHealthRules.Integrity(healthy, size);
    }

    private long? ReadLastSyncAt()
    {
        try
        {
            using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false, readOnly: true));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_sync_at FROM sync_control WHERE id = 1 AND enabled = 1";
            var value = command.ExecuteScalar();
            // last_sync_at 是毫秒
            return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture) / 1000;
        }
        catch (SqliteException exception)
        {
            logger.LogDebug(exception, "Failed to read the last sync time");
            return null;
        }
    }

    private async Task<ArchiveHealthCheckDto> CheckAchievementScopeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false, readOnly: true));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string Compared = "FROM achievement_game_archive WHERE all_total IS NOT NULL AND progress_total IS NOT NULL";
        return ArchiveHealthRules.AchievementScope(
            await SqliteSchema.ScalarLongAsync(connection, $"SELECT COUNT(*) {Compared}", cancellationToken).ConfigureAwait(false),
            await SqliteSchema.ScalarLongAsync(connection, $"SELECT COUNT(*) {Compared} AND progress_total <> total", cancellationToken).ConfigureAwait(false),
            await SqliteSchema.ScalarLongAsync(connection, "SELECT COUNT(*) FROM achievement_game_archive WHERE all_total IS NULL", cancellationToken).ConfigureAwait(false));
    }

    private async Task<ArchiveHealthCheckDto> CheckRecoveredSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false, readOnly: true));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var total = await SqliteSchema.ScalarLongAsync(connection, "SELECT COUNT(*) FROM use_app_record WHERE duration > 0", cancellationToken).ConfigureAwait(false);
        var recovered = await SqliteSchema.ScalarLongAsync(connection, "SELECT COUNT(*) FROM use_app_record WHERE duration > 0 AND end_reason = 'recovered'", cancellationToken).ConfigureAwait(false);
        return ArchiveHealthRules.Recovered(total, recovered);
    }
}
