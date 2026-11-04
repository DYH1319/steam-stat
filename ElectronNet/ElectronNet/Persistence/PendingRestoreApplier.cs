using System.Text.Json;
using ElectronNet.Features.DataTransfer;
using ElectronNet.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;

namespace ElectronNet.Persistence;

/// <summary>
/// 启动时（打开数据库之前）应用已暂存的恢复（Restore/pending）：
/// <list type="number">
/// <item>当前数据库用在线备份 API 另存为 Database/backups/pre-restore-*.bak，失败时据此回滚</item>
/// <item>用备份中的数据库替换当前数据库；保留本机的登录凭证（备份中本来就没有）与当前设备身份</item>
/// <item>按暂存内容替换设置、徽章名缓存，补齐存档备份库对象，渲染层偏好留给渲染层领取</item>
/// </list>
/// 之后照常执行数据库迁移（旧版本的备份会被升级）。
/// </summary>
internal sealed class PendingRestoreApplier(IAppPaths appPaths, TimeProvider timeProvider, ILogger<PendingRestoreApplier> logger)
{
    internal async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var paths = new RestorePaths(appPaths);
        if (!File.Exists(paths.PendingFile)) return false;

        PendingRestore? pending = null;
        string? preRestore = null;
        var replaced = false;
        try
        {
            pending = JsonSerializer.Deserialize<PendingRestore>(await File.ReadAllTextAsync(paths.PendingFile, cancellationToken).ConfigureAwait(false));
            var restoredDatabase = Path.Combine(paths.PendingDirectory, BackupFormat.DatabaseEntry);
            if (pending == null || !File.Exists(restoredDatabase)) throw new InvalidDataException("The staged restore is incomplete.");

            SqliteConnection.ClearAllPools();
            var databaseFile = appPaths.DatabaseFile;
            if (File.Exists(databaseFile))
            {
                var backupDirectory = Path.Combine(appPaths.DatabaseDirectory, "backups");
                Directory.CreateDirectory(backupDirectory);
                preRestore = Path.Combine(backupDirectory, $"pre-restore-{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.bak");
                await using (var current = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false)))
                await using (var copy = new SqliteConnection(SqliteConnectionStrings.Create(preRestore, pooling: false)))
                {
                    await current.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
                    current.BackupDatabase(copy);
                }
                SqliteConnection.ClearAllPools();
            }

            DeleteDatabaseFiles(databaseFile);
            replaced = true;
            Directory.CreateDirectory(appPaths.DatabaseDirectory);
            File.Copy(restoredDatabase, databaseFile);
            if (preRestore != null) await CarryOverLocalStateAsync(databaseFile, preRestore, cancellationToken).ConfigureAwait(false);
            else await ResetCurrentDeviceAsync(databaseFile, cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();

            ApplyFiles(paths);

            await WriteOutcomeAsync(paths, new RestoreOutcome
            {
                AppliedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                SourceName = pending.SourceName,
                BackupCreatedAt = pending.BackupCreatedAt,
                Success = true,
                PreRestoreSnapshot = preRestore == null ? null : Path.GetFileName(preRestore)
            }).ConfigureAwait(false);
            logger.LogInformation("Restored data from backup {Source}; the previous database was kept as {Snapshot}", pending.SourceName, preRestore);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to apply the staged restore");
            if (replaced) RollBack(preRestore);
            await WriteOutcomeAsync(paths, new RestoreOutcome
            {
                AppliedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                SourceName = pending?.SourceName ?? string.Empty,
                BackupCreatedAt = pending?.BackupCreatedAt ?? 0,
                Success = false,
                Error = "failed"
            }).ConfigureAwait(false);
            return false;
        }
        finally
        {
            // 无论成功与否都清掉暂存，避免每次启动反复尝试
            TryDelete(() => File.Delete(paths.PendingFile));
            TryDelete(() => Directory.Delete(paths.PendingDirectory, true));
        }
    }

    /// <summary>
    /// 把本机状态带进恢复后的数据库：当前设备身份（恢复的设备一律不再是「当前设备」）与登录凭证。
    /// 两边表结构可能不同（旧版本的备份），只复制共有的列。
    /// </summary>
    private static async Task CarryOverLocalStateAsync(string databaseFile, string priorFile, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS prior";
            attach.Parameters.AddWithValue("$path", priorFile);
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (await SqliteSchema.TableExistsAsync(connection, "device", cancellationToken).ConfigureAwait(false))
            {
                await SqliteSchema.ExecuteAsync(connection, "UPDATE main.device SET is_current = 0", cancellationToken).ConfigureAwait(false);
                if (await SqliteSchema.TableExistsAsync(connection, "device", cancellationToken, "prior").ConfigureAwait(false))
                {
                    await SqliteSchema.ExecuteAsync(connection,
                        "INSERT OR REPLACE INTO main.device (id, name, is_current, created_at) SELECT id, name, 1, created_at FROM prior.device WHERE is_current = 1",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            const string tokens = "steam_login_token";
            if (await SqliteSchema.TableExistsAsync(connection, tokens, cancellationToken).ConfigureAwait(false)
                && await SqliteSchema.TableExistsAsync(connection, tokens, cancellationToken, "prior").ConfigureAwait(false))
            {
                var mainColumns = await SqliteSchema.ColumnsAsync(connection, tokens, cancellationToken).ConfigureAwait(false);
                var priorColumns = await SqliteSchema.ColumnsAsync(connection, tokens, cancellationToken, "prior").ConfigureAwait(false);
                var common = mainColumns.Intersect(priorColumns, StringComparer.Ordinal).Select(SqliteSchema.Quote).ToList();
                if (common.Count > 0)
                {
                    var list = string.Join(", ", common);
                    await SqliteSchema.ExecuteAsync(connection, $"DELETE FROM main.{tokens}", cancellationToken).ConfigureAwait(false);
                    await SqliteSchema.ExecuteAsync(connection, $"INSERT INTO main.{tokens} ({list}) SELECT {list} FROM prior.{tokens}", cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await SqliteSchema.ExecuteAsync(connection, "DETACH DATABASE prior", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ResetCurrentDeviceAsync(string databaseFile, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await SqliteSchema.TableExistsAsync(connection, "device", cancellationToken).ConfigureAwait(false))
            await SqliteSchema.ExecuteAsync(connection, "UPDATE device SET is_current = 0", cancellationToken).ConfigureAwait(false);
    }

    private void ApplyFiles(RestorePaths paths)
    {
        var settings = Path.Combine(paths.PendingDirectory, BackupFormat.SettingsEntry);
        if (File.Exists(settings))
        {
            Directory.CreateDirectory(appPaths.SettingsDirectory);
            if (File.Exists(appPaths.SettingsFile))
                File.Copy(appPaths.SettingsFile, Path.Combine(appPaths.SettingsDirectory, "settings.pre-restore.json"), overwrite: true);
            File.Copy(settings, appPaths.SettingsFile, overwrite: true);
        }

        var badges = Path.Combine(paths.PendingDirectory, BackupFormat.BadgeNamesEntry);
        if (File.Exists(badges))
        {
            var target = BackupService.BadgeNamesPath(appPaths);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(badges, target, overwrite: true);
        }

        var renderer = Path.Combine(paths.PendingDirectory, BackupFormat.RendererStateEntry);
        if (File.Exists(renderer)) File.Copy(renderer, paths.RendererStateFile, overwrite: true);

        // 存档备份库是内容寻址的：只补齐缺少的对象，已有对象不覆盖
        var objects = Path.Combine(paths.PendingDirectory, "save-backups", "objects");
        if (Directory.Exists(objects))
        {
            var targetRoot = BackupService.SaveObjectsDirectory(appPaths);
            foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(targetRoot, Path.GetRelativePath(objects, file));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    private void RollBack(string? preRestore)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(appPaths.DatabaseFile);
            if (preRestore != null && File.Exists(preRestore)) File.Copy(preRestore, appPaths.DatabaseFile);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to roll back the restore; the previous database is kept at {Snapshot}", preRestore);
        }
    }

    private static void DeleteDatabaseFiles(string databaseFile)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(databaseFile + suffix)) File.Delete(databaseFile + suffix);
        }
    }

    private async Task WriteOutcomeAsync(RestorePaths paths, RestoreOutcome outcome)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            await File.WriteAllTextAsync(paths.LastRestoreFile, JsonSerializer.Serialize(outcome, BackupFormat.JsonOptions)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to record the restore outcome");
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
