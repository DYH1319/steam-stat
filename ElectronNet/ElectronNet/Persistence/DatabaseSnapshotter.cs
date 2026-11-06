using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Persistence;

/// <summary>
/// 启动时的数据库滚动快照，与迁移前的 steam-stat.bak 互为补充，用于数据库损坏时的自救。
/// 每次启动一份，保留最近 N 份（设置 archive.startupSnapshotKeep）。
/// 使用 SQLite 在线备份 API，WAL 模式下与应用并发读写互不阻塞。
/// <para>
/// 崩溃循环是快照最该发挥作用、也最容易被毁掉的场景：连续启动几次就能把好快照全挤掉。
/// 所以两份至少间隔 <see cref="MinInterval"/>，并且新快照通过完整性检查才会淘汰旧的。
/// </para>
/// </summary>
internal sealed class DatabaseSnapshotter(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ISettingsStore settingsStore,
    ILogger<DatabaseSnapshotter> logger)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

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
                if (!await IsHealthyAsync(temporaryFile, cancellationToken))
                {
                    logger.LogWarning("The database snapshot failed the integrity check; keeping the existing snapshots");
                    return;
                }

                File.Move(temporaryFile, targetFile);
                logger.LogInformation("Created database snapshot {SnapshotFile}", targetFile);

                var keep = settingsStore.GetSettings().Archive?.StartupSnapshotKeep ?? 7;
                foreach (var stale in Directory.GetFiles(backupDirectory, "steam-stat-*.bak").OrderDescending().Skip(keep))
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

    internal static async Task<bool> IsHealthyAsync(string databaseFile, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false, readOnly: true));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1)";
            return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    internal IReadOnlyList<DatabaseSnapshotDto> List()
    {
        var snapshots = new List<DatabaseSnapshotDto>();
        var backupDirectory = Path.Combine(appPaths.DatabaseDirectory, "backups");
        if (Directory.Exists(backupDirectory))
            foreach (var file in Directory.GetFiles(backupDirectory, "*.bak"))
                if (KindOf(Path.GetFileName(file)) is { } kind) snapshots.Add(Describe(file, kind));
        if (File.Exists(appPaths.DatabaseBackupFile)) snapshots.Add(Describe(appPaths.DatabaseBackupFile, "preMigration"));
        return snapshots.OrderByDescending(snapshot => snapshot.CreatedAt).ToList();
    }

    /// <summary>把快照名解析成路径。名字来自渲染层，只接受 <see cref="List"/> 列出来的文件。</summary>
    internal string? Resolve(string name)
    {
        if (name == Path.GetFileName(appPaths.DatabaseBackupFile))
            return File.Exists(appPaths.DatabaseBackupFile) ? appPaths.DatabaseBackupFile : null;
        if (KindOf(name) == null || name != Path.GetFileName(name)) return null;
        var file = Path.Combine(appPaths.DatabaseDirectory, "backups", name);
        return File.Exists(file) ? file : null;
    }

    private static string? KindOf(string fileName)
        => fileName.StartsWith("steam-stat-", StringComparison.Ordinal) && fileName.EndsWith(".bak", StringComparison.Ordinal) ? "startup"
            : fileName.StartsWith("pre-restore-", StringComparison.Ordinal) && fileName.EndsWith(".bak", StringComparison.Ordinal) ? "preRestore"
            : null;

    private static DatabaseSnapshotDto Describe(string file, string kind)
    {
        var info = new FileInfo(file);
        return new DatabaseSnapshotDto
        {
            Name = info.Name,
            CreatedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            SizeBytes = info.Length,
            Kind = kind
        };
    }
}
