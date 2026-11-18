using ElectronNet;
using ElectronNet.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Tests.Services;

[TestFixture]
public class AppDbContextTests
{
    [Test]
    public async Task Migrate_CreatesCurrentSchemaWithPatchedSqliteRuntime()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-tests", Guid.NewGuid().ToString("N"));
        var databaseFile = Path.Combine(tempDir, "steam-stat.db");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var context = CreateContext(databaseFile);
            await context.Database.MigrateAsync();

            var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
            appliedMigrations.Should().Equal(
                "20260118024506_Initial",
                "20260121041547_ChangeBlobToIntegerInSteamUser",
                "20260121044203_ChangeBlobToIntegerInSteamUserFix",
                "20260129164432_ChangeBlobToInteger",
                "20260129164604_ChangeBlobToIntegerFix",
                "20260205044537_FixNoIndexBug",
                "20260206080543_RefactorSteamAppRefreshTime",
                "20260210065212_EnsureChangeBlobToInteger",
                "20260216053809_ChangeSteamIdTypeFromIntegerToText",
                "20260218151829_EnsureChangeSteamIdFromIntegerToText",
                "20260330132723_AddSteamLoginToken",
                "20260418090053_AddFriendStatusRecord",
                "20260908085358_AddSteamResourceCache",
                "20261024043056_AddSessionTracking",
                "20261025073747_AddPlaytimeHistory",
                "20261029025256_AddFriendTracking",
                "20261029080012_AddSteamLoginTokenOptions",
                "20261030065547_AddAchievementAndLibraryArchive",
                "20261102081706_AddAchievementArchiveProgress",
                "20261103022824_AddArchiveExtensions",
                "20261104085641_AddSync",
                "20261106015020_AddRichPresenceRecord",
                "20261106035016_AddAchievementSets",
                "20261106070155_AddInsightsAndWeekly",
                "20261106110407_AddNewArchives",
                "20261108053219_AddMemoir",
                "20261109033911_AddFriendCircle",
                "20261111015614_DeduplicateFriendStatusRecords"
            );

            await context.Database.OpenConnectionAsync();
            var tables = new List<string>();
            await using (var command = context.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT sqlite_version();";
                (await command.ExecuteScalarAsync()).Should().Be("3.53.3");

                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            await context.Database.CloseConnectionAsync();
            tables.Should().Equal(
                "__EFMigrationsHistory",
                "__EFMigrationsLock",
                "achievement_game_archive",
                "achievement_schema_change",
                "achievement_schema_state",
                "achievement_set_archive",
                "achievement_target",
                "achievement_unlock_archive",
                "app_artifact_event",
                "app_build_history",
                "app_install_event",
                "app_player_count",
                "app_store_status",
                "cloud_save_trace",
                "collector_state",
                "community_comment",
                "community_file",
                "community_review",
                "device",
                "exchange_card",
                "family_member",
                "family_playtime",
                "friend_achievement_progress",
                "friend_library_item",
                "friend_observation_window",
                "friend_playtime_snapshot",
                "friend_since",
                "friend_status_record",
                "friend_tracking",
                "global_status",
                "library_acquisition",
                "library_game_meta",
                "life_event",
                "platform_playtime",
                "playtime_snapshot",
                "presence_group_record",
                "profile_history",
                "purchase_record",
                "recording_media",
                "recording_timeline",
                "rich_presence_record",
                "save_backup_file",
                "save_backup_snapshot",
                "save_backup_target",
                "screenshot_archive",
                "session_import_batch",
                "steam_app",
                "steam_login_token",
                "steam_resource_cache",
                "steam_user",
                "store_page_snapshot",
                "sync_control",
                "sync_cursor",
                "sync_outbox",
                "sync_version",
                "time_capsule",
                "together_record",
                "use_app_record",
                "web_capture",
                "weekly_issue",
                "wishlist_record",
                "year_in_review"
            );
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void DesignTimeFactory_UsesExplicitDatabaseOutsideUserData()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-design-tests", Guid.NewGuid().ToString("N"));
        var databaseFile = Path.Combine(tempDir, "design.db");

        try
        {
            using var context = new AppDbContextDesignTimeFactory().CreateDbContext(["--database", databaseFile]);

            context.Database.GetDbConnection().DataSource.Should().Be(Path.GetFullPath(databaseFile));
            Directory.Exists(tempDir).Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static AppDbContext CreateContext(string databaseFile)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;
        return new AppDbContext(options);
    }
}
