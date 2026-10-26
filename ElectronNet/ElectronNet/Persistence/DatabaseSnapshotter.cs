using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;

namespace ElectronNet.Persistence;

/// <summary>
/// 启动时的数据库滚动快照，与迁移前的 steam-stat.bak 互为补充，用于数据库损坏时的自救。
/// 两份快照至少间隔 <see cref="MinInterval"/>，最多保留最近 <see cref="MaxSnapshots"/> 份。
/// 使用 SQLite 在线备份 API，WAL 模式下与应用并发读写互不阻塞。
/// </summary>
internal sealed class DatabaseSnapshotter(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ILogger<DatabaseSnapshotter> logger)
{
    private const int MaxSnapshots = 7;
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);

    internal async Task SnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var backupDirectory = Path.Combine(appPaths.DatabaseDirectory, "backups");
            var existing = Directory.Exists(backupDirectory)
                ? Directory.GetFiles(backupDirectory, "steam-stat-*.bak").OrderDescending().ToList()
                : [];
            if (existing.Count > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(existing[0]) < MinInterval)
            {
                return;
            }

            Directory.CreateDirectory(backupDirectory);
            var targetFile = Path.Combine(backupDirectory, $"steam-stat-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak");
            var temporaryFile = $"{targetFile}.tmp";

            try
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                await db.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    var sourceConnection = (SqliteConnection)db.Database.GetDbConnection();
                    await using var backupConnection = new SqliteConnection(SqliteConnectionStrings.Create(temporaryFile, pooling: false));
                    await backupConnection.OpenAsync(cancellationToken);
                    sourceConnection.BackupDatabase(backupConnection);
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryFile, targetFile);
                logger.LogInformation("Created database snapshot {SnapshotFile}", targetFile);

                foreach (var stale in Directory.GetFiles(backupDirectory, "steam-stat-*.bak").OrderDescending().Skip(MaxSnapshots))
                {
                    File.Delete(stale);
                }
            }
            finally
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to create database snapshot");
        }
    }
}
