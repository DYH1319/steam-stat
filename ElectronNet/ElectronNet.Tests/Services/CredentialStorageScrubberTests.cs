using ElectronNet.Features.Login.Persistence;
using ElectronNet.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;
using SteamStat.Core.Platform;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class CredentialStorageScrubberTests
{
    private string _tempDir = null!;
    private AppPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-scrubber-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_tempDir);
        Directory.CreateDirectory(_paths.DatabaseDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public async Task EncryptLegacyAsync_SkipsRowsThatCannotBeFullyProtected()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.SteamLoginTokenTable.Add(new ElectronNet.Models.SteamLoginToken
            {
                AccountName = "alice", AccessToken = "plain-access", RefreshToken = "plain-refresh",
                GuardData = "plain-guard", CreatedAt = 1
            });
            db.SteamLoginTokenTable.Add(new ElectronNet.Models.SteamLoginToken
            {
                AccountName = "bob", AccessToken = "fails", RefreshToken = "plain-refresh-2",
                GuardData = null, CreatedAt = 1
            });
            await db.SaveChangesAsync();
        }

        var secrets = new TestSecretStore { FailingInputs = { "fails" } };
        var store = new SteamLoginTokenStore(factory, NullLogger<SteamLoginTokenStore>.Instance);

        var upgraded = await store.EncryptLegacyAsync(secrets);

        upgraded.Should().Be(1);
        await using var verify = await factory.CreateDbContextAsync();
        var alice = await verify.SteamLoginTokenTable.AsNoTracking().SingleAsync(t => t.AccountName == "alice");
        alice.AccessToken.Should().Be("protected:plain-access");
        alice.RefreshToken.Should().Be("protected:plain-refresh");
        alice.GuardData.Should().Be("protected:plain-guard");
        var bob = await verify.SteamLoginTokenTable.AsNoTracking().SingleAsync(t => t.AccountName == "bob");
        bob.AccessToken.Should().Be("fails");
        bob.RefreshToken.Should().Be("plain-refresh-2");
        bob.GuardData.Should().BeNull();
    }

    [Test]
    public async Task EncryptLegacyAsync_WhenNothingToUpgrade_ReturnsZero()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.SteamLoginTokenTable.Add(new ElectronNet.Models.SteamLoginToken
            {
                AccountName = "alice", AccessToken = "protected:a", RefreshToken = "protected:r",
                GuardData = "protected:g", CreatedAt = 1
            });
            await db.SaveChangesAsync();
        }

        var store = new SteamLoginTokenStore(factory, NullLogger<SteamLoginTokenStore>.Instance);

        (await store.EncryptLegacyAsync(new TestSecretStore())).Should().Be(0);
    }

    [Test]
    public async Task EncryptLegacyAsync_TreatsEmptySecretsAsNothingToProtect()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.SteamLoginTokenTable.Add(new ElectronNet.Models.SteamLoginToken
            {
                AccountName = "alice", AccessToken = "", RefreshToken = "plain-refresh",
                GuardData = "", CreatedAt = 1
            });
            await db.SaveChangesAsync();
        }

        var store = new SteamLoginTokenStore(factory, NullLogger<SteamLoginTokenStore>.Instance);

        var upgraded = await store.EncryptLegacyAsync(new TestSecretStore());

        upgraded.Should().Be(1);
        await using var verify = await factory.CreateDbContextAsync();
        var alice = await verify.SteamLoginTokenTable.AsNoTracking().SingleAsync(t => t.AccountName == "alice");
        alice.AccessToken.Should().Be("protected:");
        alice.RefreshToken.Should().Be("protected:plain-refresh");
        alice.GuardData.Should().Be("protected:");
        // 空串视为无需保护，之后启动不会重复跳过/告警
        (await store.EncryptLegacyAsync(new TestSecretStore())).Should().Be(0);
    }

    [Test]
    public async Task ScrubAsync_KeepsBackupRowsWhoseSecretsAreEmpty()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await CreateBackupTokenTableAsync(_paths.DatabaseBackupFile);
        await InsertBackupTokenAsync(1, "alice", "", "plain-refresh", null);

        var scrubber = new CredentialStorageScrubber(
            factory, _paths, new TestSecretStore(), NullLogger<CredentialStorageScrubber>.Instance);

        await scrubber.ScrubAsync(upgradedTokenCount: 0);

        var rows = await ReadBackupTokensAsync();
        rows.Should().ContainSingle();
        rows[0].AccessToken.Should().Be("protected:");
        rows[0].RefreshToken.Should().Be("protected:plain-refresh");
    }

    [Test]
    public async Task ScrubAsync_ProtectsBackupRows_DeletesUnprotectableRows_AndSetsUserVersion()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await CreateBackupTokenTableAsync(_paths.DatabaseBackupFile);
        await InsertBackupTokenAsync(1, "alice", "plain-access", "plain-refresh", "plain-guard");
        await InsertBackupTokenAsync(2, "bob", "fails", "plain-refresh-2", null);

        var secrets = new TestSecretStore { FailingInputs = { "fails" } };
        var scrubber = new CredentialStorageScrubber(
            factory, _paths, secrets, NullLogger<CredentialStorageScrubber>.Instance);

        await scrubber.ScrubAsync(upgradedTokenCount: 0);

        (await ReadUserVersionAsync(_paths.DatabaseFile)).Should().Be(1);
        (await ReadUserVersionAsync(_paths.DatabaseBackupFile)).Should().Be(1);

        var rows = await ReadBackupTokensAsync();
        rows.Should().ContainSingle();
        rows[0].AccountName.Should().Be("alice");
        rows[0].AccessToken.Should().Be("protected:plain-access");
        rows[0].RefreshToken.Should().Be("protected:plain-refresh");
        rows[0].GuardData.Should().Be("protected:plain-guard");
    }

    [Test]
    public async Task ScrubAsync_WithoutBackupFile_StillScrubsMainDatabase()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var scrubber = new CredentialStorageScrubber(
            factory, _paths, new TestSecretStore(), NullLogger<CredentialStorageScrubber>.Instance);

        await scrubber.ScrubAsync(upgradedTokenCount: 0);

        (await ReadUserVersionAsync(_paths.DatabaseFile)).Should().Be(1);
    }

    [Test]
    public async Task ScrubAsync_WhenBackupHasNoTokenTable_OnlySetsUserVersion()
    {
        var factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        await using (var connection = new SqliteConnection(
            SqliteConnectionStrings.Create(_paths.DatabaseBackupFile, pooling: false)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dummy (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var scrubber = new CredentialStorageScrubber(
            factory, _paths, new TestSecretStore(), NullLogger<CredentialStorageScrubber>.Instance);

        await scrubber.ScrubAsync(upgradedTokenCount: 0);

        (await ReadUserVersionAsync(_paths.DatabaseBackupFile)).Should().Be(1);
    }

    private async Task CreateBackupTokenTableAsync(string backupFile)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(backupFile, pooling: false));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE steam_login_token (
                id INTEGER PRIMARY KEY,
                account_name TEXT NOT NULL,
                access_token TEXT NOT NULL,
                refresh_token TEXT NOT NULL,
                guard_data TEXT,
                created_at INTEGER NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertBackupTokenAsync(
        int id, string accountName, string accessToken, string refreshToken, string? guardData)
    {
        await using var connection = new SqliteConnection(
            SqliteConnectionStrings.Create(_paths.DatabaseBackupFile, pooling: false));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO steam_login_token (id, account_name, access_token, refresh_token, guard_data, created_at)
            VALUES ($id, $account, $access, $refresh, $guard, 1)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", accountName);
        command.Parameters.AddWithValue("$access", accessToken);
        command.Parameters.AddWithValue("$refresh", refreshToken);
        command.Parameters.AddWithValue("$guard", (object?)guardData ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<(string AccountName, string AccessToken, string RefreshToken, string? GuardData)>> ReadBackupTokensAsync()
    {
        var rows = new List<(string, string, string, string?)>();
        await using var connection = new SqliteConnection(
            SqliteConnectionStrings.Create(_paths.DatabaseBackupFile, pooling: false));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_name, access_token, refresh_token, guard_data FROM steam_login_token ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    private static async Task<long> ReadUserVersionAsync(string databaseFile)
    {
        await using var connection = new SqliteConnection(
            SqliteConnectionStrings.Create(databaseFile, pooling: false));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class TestSecretStore : ISecretStore
    {
        public HashSet<string> FailingInputs { get; } = new();
        public string? Protect(string? plainText)
        {
            if (plainText == null || IsProtected(plainText)) return plainText;
            return FailingInputs.Contains(plainText) ? null : $"protected:{plainText}";
        }
        public string? Unprotect(string? protectedText) => protectedText;
        public bool IsProtected(string? value)
            => value?.StartsWith("protected:", StringComparison.Ordinal) == true;
    }
}
