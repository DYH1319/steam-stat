using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;

namespace ElectronNet.Persistence;

internal sealed class DatabaseMigrator(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ILogger<DatabaseMigrator> logger)
{
    internal const string LegacyDirectoryName = "legacy";

    internal async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await QuarantineDrizzleDatabaseAsync(cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnableWalModeAsync(db, cancellationToken);
        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pendingMigrations.Length == 0)
        {
            logger.LogInformation("Database is up to date");
            return;
        }

        logger.LogInformation(
            "Detected {MigrationCount} pending database migrations: {MigrationIds}",
            pendingMigrations.Length,
            (object)pendingMigrations);

        var stopwatch = Stopwatch.StartNew();
        await BackupAsync(db, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        stopwatch.Stop();

        logger.LogInformation(
            "Applied database migrations {MigrationIds} in {ElapsedMilliseconds} ms",
            (object)pendingMigrations,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// v0.1.0 早期版本（Node + drizzle）与当前版本共用 Database/steam-stat.db，但表结构不同且没有 EF 迁移历史，
    /// 直接执行 Initial 迁移会因 "table already exists" 失败导致启动即退出。
    /// 检测到这种数据库时把它（连同 -wal / -shm）原样移到 Database/legacy 下，再以全新数据库启动。
    /// </summary>
    private async Task QuarantineDrizzleDatabaseAsync(CancellationToken cancellationToken)
    {
        var databaseFile = appPaths.DatabaseFile;
        if (!File.Exists(databaseFile)) return;

        bool isDrizzleDatabase;
        await using (var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false)))
        {
            await connection.OpenAsync(cancellationToken);
            isDrizzleDatabase = await TableExistsAsync(connection, "__drizzle_migrations", cancellationToken)
                && !await HasEfMigrationHistoryAsync(connection, cancellationToken);
        }

        if (!isDrizzleDatabase) return;

        // 确保没有池化连接仍持有文件句柄（Windows 上否则无法移动）
        SqliteConnection.ClearAllPools();

        var legacyDirectory = Path.Combine(appPaths.DatabaseDirectory, LegacyDirectoryName);
        Directory.CreateDirectory(legacyDirectory);
        var target = Path.Combine(legacyDirectory, $"steam-stat-drizzle-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        for (var suffix = 1; File.Exists(target); suffix++)
        {
            target = Path.Combine(legacyDirectory, $"steam-stat-drizzle-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}.db");
        }

        File.Move(databaseFile, target);
        foreach (var sidecar in new[] { "-wal", "-shm" })
        {
            if (File.Exists(databaseFile + sidecar)) File.Move(databaseFile + sidecar, target + sidecar);
        }

        logger.LogWarning(
            "Found a database from an early (drizzle-based) Steam Stat build that cannot be migrated; moved it to {LegacyDatabaseFile} and starting with a new database",
            target);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> HasEfMigrationHistoryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken)) return false;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>
    /// 切换数据库到 WAL 日志模式（设置持久化在数据库文件中）。
    /// WAL 允许读操作与唯一的写操作并发执行，避免读写互相阻塞导致的
    /// "SQLite Error 5: database is locked"。
    /// </summary>
    private async Task EnableWalModeAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode = WAL";
                var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
                if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("SQLite WAL journal mode is enabled");
                }
                else
                {
                    logger.LogWarning("SQLite journal mode is {JournalMode}; expected wal", mode);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to enable SQLite WAL journal mode");
        }
    }

    private async Task BackupAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appPaths.DatabaseDirectory);
        var temporaryBackupFile = $"{appPaths.DatabaseBackupFile}.tmp";

        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var sourceConnection = (SqliteConnection)db.Database.GetDbConnection();
                await using var backupConnection = new SqliteConnection(SqliteConnectionStrings.Create(temporaryBackupFile, pooling: false));
                await backupConnection.OpenAsync(cancellationToken);
                sourceConnection.BackupDatabase(backupConnection);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(appPaths.DatabaseBackupFile))
            {
                File.Replace(temporaryBackupFile, appPaths.DatabaseBackupFile, null);
            }
            else
            {
                File.Move(temporaryBackupFile, appPaths.DatabaseBackupFile);
            }

            logger.LogInformation("Database backup completed before migration");
        }
        finally
        {
            if (File.Exists(temporaryBackupFile)) File.Delete(temporaryBackupFile);
        }
    }
}
