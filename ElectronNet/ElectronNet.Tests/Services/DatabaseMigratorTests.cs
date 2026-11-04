using System.Data.Common;
using ElectronNet;
using ElectronNet.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;

namespace ElectronNet.Tests.Services;

[TestFixture]
public class DatabaseMigratorTests
{
    private const string InitialMigration = "20260118024506_Initial";
    private const string FixtureSteamId = "76561198000000000";

    [Test]
    public async Task LegacyFixture_IsBackedUpAndUpgradedWithoutDataLoss()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-migration-tests", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(tempDir);
        Directory.CreateDirectory(appPaths.DatabaseDirectory);
        var dbContextFactory = new TestDbContextFactory(appPaths.DatabaseFile);

        try
        {
            await CreateLegacyFixtureAsync(dbContextFactory);
            await File.WriteAllTextAsync(appPaths.DatabaseBackupFile, "previous backup");

            var migrator = new DatabaseMigrator(
                dbContextFactory,
                appPaths,
                NullLogger<DatabaseMigrator>.Instance);
            await migrator.MigrateAsync();

            File.Exists(appPaths.DatabaseBackupFile).Should().BeTrue();
            File.Exists($"{appPaths.DatabaseBackupFile}.tmp").Should().BeFalse();

            await using (var backup = CreateContext(appPaths.DatabaseBackupFile))
            {
                (await backup.Database.GetAppliedMigrationsAsync()).Should().Equal(InitialMigration);
                (await ExecuteScalarAsync<long>(backup.Database.GetDbConnection(), "SELECT COUNT(*) FROM steam_user;")).Should().Be(1);
                (await ExecuteScalarAsync<long>(backup.Database.GetDbConnection(), "SELECT COUNT(*) FROM use_app_record;")).Should().Be(1);
            }

            await using var upgraded = await dbContextFactory.CreateDbContextAsync();
            (await upgraded.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await upgraded.SteamUserTable.AsNoTracking().SingleAsync()).SteamId.Should().Be(FixtureSteamId);
            (await upgraded.UseAppRecordTable.AsNoTracking().SingleAsync()).SteamId.Should().Be(FixtureSteamId);
            (await upgraded.GlobalStatusTable.AsNoTracking().SingleAsync()).ActiveUserSteamId.Should().Be(FixtureSteamId);
            (await upgraded.SteamAppTable.AsNoTracking().SingleAsync()).Name.Should().Be("Fixture App");
            (await upgraded.SteamResourceCacheTable.AsNoTracking().AnyAsync()).Should().BeFalse();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task NewDatabase_IsBackedUpBeforeInitialMigration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-migration-tests", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(tempDir);
        Directory.CreateDirectory(appPaths.DatabaseDirectory);
        var dbContextFactory = new TestDbContextFactory(appPaths.DatabaseFile);

        try
        {
            var migrator = new DatabaseMigrator(
                dbContextFactory,
                appPaths,
                NullLogger<DatabaseMigrator>.Instance);
            await migrator.MigrateAsync();

            await using var backup = CreateContext(appPaths.DatabaseBackupFile);
            (await backup.Database.GetAppliedMigrationsAsync()).Should().BeEmpty();

            await using var migrated = await dbContextFactory.CreateDbContextAsync();
            (await migrated.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task DrizzleDatabase_IsMovedAsideAndReplacedWithFreshDatabase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-migration-tests", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(tempDir);
        Directory.CreateDirectory(appPaths.DatabaseDirectory);
        var dbContextFactory = new TestDbContextFactory(appPaths.DatabaseFile);

        try
        {
            // v0.1.0（Node + drizzle）留下的数据库：同名表但没有 EF 迁移历史
            await using (var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE __drizzle_migrations (id INTEGER PRIMARY KEY, hash TEXT NOT NULL, created_at NUMERIC);
                    INSERT INTO __drizzle_migrations (hash, created_at) VALUES ('fixture', 1764151441254);
                    CREATE TABLE global_status (id INTEGER PRIMARY KEY, steam_path TEXT);
                    CREATE TABLE steam_user (steam_id BLOB PRIMARY KEY);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var migrator = new DatabaseMigrator(dbContextFactory, appPaths, NullLogger<DatabaseMigrator>.Instance);
            await migrator.MigrateAsync();

            await using (var migrated = await dbContextFactory.CreateDbContextAsync())
            {
                (await migrated.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
                (await ExecuteScalarAsync<long>(migrated.Database.GetDbConnection(),
                    "SELECT COUNT(*) FROM sqlite_master WHERE name = '__drizzle_migrations';")).Should().Be(0);
            }

            var legacyFiles = Directory.GetFiles(Path.Combine(appPaths.DatabaseDirectory, DatabaseMigrator.LegacyDirectoryName), "*.db");
            legacyFiles.Should().ContainSingle();
            await using var legacy = new SqliteConnection(SqliteConnectionStrings.Create(legacyFiles[0], pooling: false));
            (await ExecuteScalarAsync<string>(legacy, "SELECT hash FROM __drizzle_migrations;")).Should().Be("fixture");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task MigratedDatabase_WithStrayDrizzleTable_IsLeftInPlace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-migration-tests", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(tempDir);
        Directory.CreateDirectory(appPaths.DatabaseDirectory);
        var dbContextFactory = new TestDbContextFactory(appPaths.DatabaseFile);

        try
        {
            await CreateLegacyFixtureAsync(dbContextFactory);
            await using (var context = await dbContextFactory.CreateDbContextAsync())
            {
                await context.Database.ExecuteSqlRawAsync("CREATE TABLE __drizzle_migrations (id INTEGER PRIMARY KEY);");
            }

            var migrator = new DatabaseMigrator(dbContextFactory, appPaths, NullLogger<DatabaseMigrator>.Instance);
            await migrator.MigrateAsync();

            Directory.Exists(Path.Combine(appPaths.DatabaseDirectory, DatabaseMigrator.LegacyDirectoryName)).Should().BeFalse();
            await using var upgraded = await dbContextFactory.CreateDbContextAsync();
            (await upgraded.SteamUserTable.AsNoTracking().SingleAsync()).SteamId.Should().Be(FixtureSteamId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static async Task CreateLegacyFixtureAsync(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync();
        await context.Database.MigrateAsync(InitialMigration);
        await context.Database.ExecuteSqlRawAsync($$"""
            INSERT INTO global_status
                (id, steam_path, active_user_steam_id, refresh_time, steam_user_refresh_time)
            VALUES
                (1, 'C:\Fixture\Steam', {{FixtureSteamId}}, 1700000000, 1700000001);

            INSERT INTO steam_user
                (steam_id, account_id, account_name, persona_name)
            VALUES
                ({{FixtureSteamId}}, 39734272, 'fixture_account', 'Fixture User');

            INSERT INTO steam_app
                (app_id, name, name_localized, installed, is_running, refresh_time)
            VALUES
                (730, 'Fixture App', char(123) || char(125), 1, 0, 1700000002);

            INSERT INTO use_app_record
                (app_id, steam_id, start_time, end_time, duration)
            VALUES
                (730, {{FixtureSteamId}}, 1700000003, 1700000063, 60);
            """);
    }

    private static AppDbContext CreateContext(string databaseFile)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<T> ExecuteScalarAsync<T>(DbConnection connection, string commandText)
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var result = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
