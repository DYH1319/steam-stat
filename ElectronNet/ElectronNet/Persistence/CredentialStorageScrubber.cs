using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.Platform;

namespace ElectronNet.Persistence;

/// <summary>
/// Purges lingering plaintext credential copies: compacts the main database once
/// and rewrites or removes unprotected rows inside the pre-migration backup file.
/// </summary>
internal sealed class CredentialStorageScrubber(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ISecretStore secretStore,
    ILogger<CredentialStorageScrubber> logger)
{
    private const int ScrubbedUserVersion = 1;

    internal async Task ScrubAsync(int upgradedTokenCount, CancellationToken cancellationToken = default)
    {
        await ScrubMainDatabaseAsync(upgradedTokenCount, cancellationToken).ConfigureAwait(false);
        await ScrubBackupDatabaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ScrubMainDatabaseAsync(int upgradedTokenCount, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            var userVersion = await ReadUserVersionAsync(connection, cancellationToken);
            if (userVersion >= ScrubbedUserVersion && upgradedTokenCount <= 0) return;
            await ExecuteNonQueryAsync(connection, "VACUUM", cancellationToken);
            await ExecuteNonQueryAsync(connection, $"PRAGMA user_version = {ScrubbedUserVersion}", cancellationToken);
            logger.LogInformation("Vacuumed the main database to purge legacy plaintext credential copies");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to scrub the main credential database");
        }
    }

    private async Task ScrubBackupDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var backupFile = appPaths.DatabaseBackupFile;
            if (!File.Exists(backupFile)) return;

            await using var connection = new SqliteConnection(
                SqliteConnectionStrings.Create(backupFile, pooling: false));
            await connection.OpenAsync(cancellationToken);

            var changed = await TableExistsAsync(connection, "steam_login_token", cancellationToken)
                && await ProtectBackupTokensAsync(connection, cancellationToken);
            var userVersion = await ReadUserVersionAsync(connection, cancellationToken);
            if (!changed && userVersion >= ScrubbedUserVersion) return;
            await ExecuteNonQueryAsync(connection, "VACUUM", cancellationToken);
            await ExecuteNonQueryAsync(connection, $"PRAGMA user_version = {ScrubbedUserVersion}", cancellationToken);
            logger.LogInformation("Vacuumed the database backup to purge legacy plaintext credential copies");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to scrub the credential database backup");
        }
    }

    private async Task<bool> ProtectBackupTokensAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<(long Id, string? AccessToken, string? RefreshToken, string? GuardData)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, access_token, refresh_token, guard_data FROM steam_login_token";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var changed = false;
        foreach (var row in rows)
        {
            if (IsFullyProtected(row.AccessToken, row.RefreshToken, row.GuardData)) continue;

            var accessToken = secretStore.Protect(row.AccessToken);
            var refreshToken = secretStore.Protect(row.RefreshToken);
            var guardData = secretStore.Protect(row.GuardData);
            var fullyProtected = IsProtectedOrNothingToProtect(row.AccessToken, accessToken)
                && IsProtectedOrNothingToProtect(row.RefreshToken, refreshToken)
                && IsProtectedOrNothingToProtect(row.GuardData, guardData);
            if (!fullyProtected)
            {
                await using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM steam_login_token WHERE id = $id";
                delete.Parameters.AddWithValue("$id", row.Id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
                logger.LogWarning(
                    "Deleted a backup Steam login token row that could not be fully protected (id {TokenId})",
                    row.Id);
            }
            else
            {
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE steam_login_token SET access_token = $accessToken, refresh_token = $refreshToken, guard_data = $guardData WHERE id = $id";
                update.Parameters.AddWithValue("$accessToken", accessToken!);
                update.Parameters.AddWithValue("$refreshToken", refreshToken!);
                update.Parameters.AddWithValue("$guardData", (object?)guardData ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", row.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            changed = true;
        }
        return changed;
    }

    private bool IsFullyProtected(string? accessToken, string? refreshToken, string? guardData)
        => IsProtected(accessToken) && IsProtected(refreshToken) && IsProtected(guardData);

    private bool IsProtected(string? value)
        => string.IsNullOrEmpty(value) || secretStore.IsProtected(value);

    private bool IsProtectedOrNothingToProtect(string? original, string? protectedValue)
        => string.IsNullOrEmpty(original) || secretStore.IsProtected(protectedValue);

    private static async Task<long> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count && count > 0;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
