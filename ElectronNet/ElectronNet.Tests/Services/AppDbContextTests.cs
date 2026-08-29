using System.Reflection;
using ElectronNet;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Tests.Services;

[TestFixture]
[NonParallelizable]
public class AppDbContextTests
{
    [Test]
    public async Task Migrate_CreatesCurrentSchemaWithPatchedSqliteRuntime()
    {
        var userDataProperty = typeof(Program).GetProperty("UserDataPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalUserDataPath = userDataProperty.GetValue(null);
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-tests", Guid.NewGuid().ToString("N"));
        userDataProperty.SetValue(null, tempDir);
        AppDbContext? context = null;

        try
        {
            context = new AppDbContext();
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
            if (context != null) await context.DisposeAsync();
            SqliteConnection.ClearAllPools();
            userDataProperty.SetValue(null, originalUserDataPath);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
