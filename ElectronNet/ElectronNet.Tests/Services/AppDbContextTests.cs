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
                "20260418090053_AddFriendStatusRecord"
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
                "friend_status_record",
                "global_status",
                "steam_app",
                "steam_login_token",
                "steam_user",
                "use_app_record"
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
