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
    internal async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
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
