using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Features.DataTransfer;
using ElectronNet.Infrastructure;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Security;

namespace ElectronNet.Services;

/// <summary>
/// 一键手动备份与恢复（.ssbackup）。
/// <para>备份：SQLite 在线备份出一份副本，删除凭证与资源缓存后打包，可以用口令整体加密。</para>
/// <para>
/// 恢复：选择文件 →（加密时）输入口令 → 校验清单与每个文件的 SHA-256、迁移版本 → 解到 Restore/pending 暂存，
/// 下次启动时由 <see cref="PendingRestoreApplier"/> 在打开数据库之前替换。替换前当前数据库会另存为
/// pre-restore 快照，本机的登录凭证与设备身份会保留下来。
/// </para>
/// </summary>
internal sealed class BackupService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    IMainWindowAccessor mainWindowAccessor,
    RevealRegistry revealRegistry,
    AppEnvironment environment,
    DeviceService deviceService,
    TimeProvider timeProvider,
    ILogger<BackupService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stagedLock = new();
    private StagedBackup? _staged;

    private sealed class StagedBackup
    {
        public required string Token { get; init; }
        public required string SourcePath { get; init; }
        public required bool Encrypted { get; init; }
        /// <summary>可读的 zip：未加密时就是源文件，加密时是解密到临时目录的副本。</summary>
        public string? ZipPath { get; set; }
        public bool OwnsZip { get; set; }
        public BackupManifest? Manifest { get; set; }
    }

    #region 备份

    public async Task<BackupCreateResultDto> CreateAsync(BackupCreateRequest request)
    {
        var target = await PickSaveTargetAsync().ConfigureAwait(false);
        if (target == null) return new BackupCreateResultDto { Status = "cancelled", SizeBytes = 0, Encrypted = false };
        return await CreateToAsync(request, target).ConfigureAwait(false);
    }

    internal async Task<BackupCreateResultDto> CreateToAsync(BackupCreateRequest request, string target, CancellationToken cancellationToken = default)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(request.Password))
        {
            password = IpcSecret.Decode(request.Password);
            if (string.IsNullOrEmpty(password)) return CreateFailed("invalid");
        }
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return CreateFailed("busy");

        var work = Path.Combine(appPaths.TempDirectory, "backup-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        try
        {
            Directory.CreateDirectory(work);
            var databaseCopy = Path.Combine(work, BackupFormat.DatabaseEntry);
            await SnapshotDatabaseAsync(databaseCopy, cancellationToken).ConfigureAwait(false);
            var (migration, counts) = await StripAndCountAsync(databaseCopy, cancellationToken).ConfigureAwait(false);

            var deviceId = deviceService.CurrentDeviceId ?? await deviceService.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            var zipPath = Path.Combine(work, "backup.zip");
            BackupManifest manifest;
            await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var files = new List<BackupFileEntry>();
                await using (var database = File.OpenRead(databaseCopy))
                    files.Add(await BackupFormat.AddEntryAsync(zip, BackupFormat.DatabaseEntry, database, cancellationToken).ConfigureAwait(false));
                if (File.Exists(appPaths.SettingsFile))
                {
                    await using var settings = new FileStream(appPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    files.Add(await BackupFormat.AddEntryAsync(zip, BackupFormat.SettingsEntry, settings, cancellationToken).ConfigureAwait(false));
                }
                if (!string.IsNullOrWhiteSpace(request.RendererState) && IsJsonObject(request.RendererState))
                {
                    using var renderer = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.RendererState));
                    files.Add(await BackupFormat.AddEntryAsync(zip, BackupFormat.RendererStateEntry, renderer, cancellationToken).ConfigureAwait(false));
                }
                var badgeNames = BadgeNamesPath(appPaths);
                if (File.Exists(badgeNames))
                {
                    await using var badges = new FileStream(badgeNames, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    files.Add(await BackupFormat.AddEntryAsync(zip, BackupFormat.BadgeNamesEntry, badges, cancellationToken).ConfigureAwait(false));
                }
                if (request.IncludeSaveBackups)
                {
                    foreach (var (name, path) in EnumerateSaveObjects(appPaths))
                    {
                        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        files.Add(await BackupFormat.AddEntryAsync(zip, name, source, cancellationToken).ConfigureAwait(false));
                    }
                }

                manifest = new BackupManifest
                {
                    AppVersion = environment.AppVersion ?? "0.0.0",
                    CreatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                    Migration = migration,
                    DeviceId = deviceId,
                    DeviceName = Environment.MachineName,
                    Counts = counts,
                    Files = files
                };
                var manifestEntry = zip.CreateEntry(BackupFormat.ManifestEntry, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, BackupFormat.JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = $"{target}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
            try
            {
                if (password != null)
                {
                    await using var plain = File.OpenRead(zipPath);
                    await using var encrypted = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await ChunkedEncryption.EncryptAsync(plain, encrypted, new EncryptionKeySource.Passphrase(password), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    File.Move(zipPath, temporary);
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            logger.LogInformation("Created a backup ({Size} bytes, encrypted: {Encrypted})", new FileInfo(target).Length, password != null);
            return new BackupCreateResultDto
            {
                Status = "completed",
                ExportId = revealRegistry.Register(target),
                Path = target,
                SizeBytes = new FileInfo(target).Length,
                Encrypted = password != null,
                Manifest = ToDto(manifest, deviceId)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to write the backup");
            return CreateFailed("io");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Backup failed");
            return CreateFailed("failed");
        }
        finally
        {
            TryDeleteDirectory(work);
            _gate.Release();
        }
    }

    /// <summary>SQLite 在线备份 API：WAL 模式下与应用的并发读写互不阻塞，得到一致的单文件副本。</summary>
    private async Task SnapshotDatabaseAsync(string target, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = (SqliteConnection)db.Database.GetDbConnection();
            await using var backup = new SqliteConnection(SqliteConnectionStrings.Create(target, pooling: false));
            await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(backup);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>在副本上清空凭证与缓存、统计数据量并 VACUUM（被删除的凭证不会残留在空闲页里）。</summary>
    internal static async Task<(string? Migration, Dictionary<string, long> Counts)> StripAndCountAsync(string databaseFile, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in BackupFormat.StrippedTables)
        {
            if (await SqliteSchema.TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
                await SqliteSchema.ExecuteAsync(connection, $"DELETE FROM \"{table}\"", cancellationToken).ConfigureAwait(false);
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, sql) in CountQueries)
        {
            var table = sql.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
            if (!await SqliteSchema.TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false)) continue;
            counts[key] = await SqliteSchema.ScalarLongAsync(connection, sql, cancellationToken).ConfigureAwait(false);
        }

        string? migration = null;
        if (await SqliteSchema.TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId DESC LIMIT 1";
            migration = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        await SqliteSchema.ExecuteAsync(connection, "PRAGMA journal_mode = DELETE", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "VACUUM", cancellationToken).ConfigureAwait(false);
        return (migration, counts);
    }

    private static readonly (string Key, string Sql)[] CountQueries =
    [
        ("sessions", "SELECT COUNT(*) FROM use_app_record WHERE end_time IS NOT NULL"),
        ("achievementUnlocks", "SELECT COUNT(*) FROM achievement_unlock_archive"),
        ("friendRecords", "SELECT COUNT(*) FROM friend_status_record"),
        ("profileHistory", "SELECT COUNT(*) FROM profile_history"),
        ("library", "SELECT COUNT(*) FROM library_game_meta"),
        ("screenshots", "SELECT COUNT(*) FROM screenshot_archive"),
        ("saveSnapshots", "SELECT COUNT(*) FROM save_backup_snapshot")
    ];

    private async Task<string?> PickSaveTargetAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        var name = $"steam-stat-backup-{timeProvider.GetLocalNow():yyyyMMdd-HHmmss}.{BackupFormat.Extension}";
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            var path = await Electron.Dialog.ShowSaveDialogAsync(snapshot.Window, new SaveDialogOptions
            {
                DefaultPath = string.IsNullOrEmpty(documents) ? name : Path.Combine(documents, name),
                Filters = [new FileFilter { Name = "Steam Stat Backup", Extensions = [BackupFormat.Extension] }]
            }).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.HasExtension(path) ? path : $"{path}.{BackupFormat.Extension}";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the backup save dialog");
            return null;
        }
    }

    #endregion

    #region 恢复

    public async Task<BackupInspectResultDto> PickRestoreAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return InspectCancelled();
        string[]? files;
        try
        {
            files = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
            {
                DefaultPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Properties = [OpenDialogProperty.openFile],
                Filters = [new FileFilter { Name = "Steam Stat Backup", Extensions = [BackupFormat.Extension, "zip"] }]
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the backup open dialog");
            return InspectCancelled();
        }
        return files is { Length: > 0 } ? await InspectAsync(files[0]).ConfigureAwait(false) : InspectCancelled();
    }

    internal async Task<BackupInspectResultDto> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        ClearStaged();
        try
        {
            var size = new FileInfo(path).Length;
            var prefix = new byte[4];
            await using (var stream = File.OpenRead(path))
            {
                _ = await stream.ReadAtLeastAsync(prefix, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            }

            var staged = new StagedBackup
            {
                Token = "rst_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)),
                SourcePath = path,
                Encrypted = ChunkedEncryption.HasMagic(prefix)
            };
            if (!staged.Encrypted)
            {
                var manifest = ReadManifestFrom(path);
                if (manifest == null) return InspectFailed("invalid", path, size, false);
                staged.ZipPath = path;
                staged.Manifest = manifest;
            }
            lock (_stagedLock) _staged = staged;
            return new BackupInspectResultDto
            {
                Status = staged.Encrypted ? "locked" : "ready",
                Token = staged.Token,
                FileName = Path.GetFileName(path),
                SizeBytes = size,
                Encrypted = staged.Encrypted,
                Manifest = staged.Manifest == null ? null : ToDto(staged.Manifest, deviceService.CurrentDeviceId)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to read the backup file");
            return InspectFailed("io", path, 0, false);
        }
    }

    public async Task<BackupInspectResultDto> UnlockAsync(BackupUnlockRequest request, CancellationToken cancellationToken = default)
    {
        StagedBackup? staged;
        lock (_stagedLock) staged = _staged?.Token == request.Token ? _staged : null;
        if (staged is not { Encrypted: true }) return InspectFailed("invalid", null, 0, true);
        var password = IpcSecret.Decode(request.Password);
        if (string.IsNullOrEmpty(password)) return InspectFailed("wrongPassword", staged.SourcePath, 0, true);

        var size = new FileInfo(staged.SourcePath).Length;
        Directory.CreateDirectory(appPaths.TempDirectory);
        var decrypted = Path.Combine(appPaths.TempDirectory, $"restore-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.zip");
        try
        {
            await using (var source = File.OpenRead(staged.SourcePath))
            await using (var target = new FileStream(decrypted, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await ChunkedEncryption.DecryptAsync(source, target, password, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            var manifest = ReadManifestFrom(decrypted);
            if (manifest == null)
            {
                File.Delete(decrypted);
                return InspectFailed("invalid", staged.SourcePath, size, true);
            }
            lock (_stagedLock)
            {
                if (staged.OwnsZip && staged.ZipPath != null) TryDeleteFile(staged.ZipPath);
                staged.ZipPath = decrypted;
                staged.OwnsZip = true;
                staged.Manifest = manifest;
            }
            return new BackupInspectResultDto
            {
                Status = "ready",
                Token = staged.Token,
                FileName = Path.GetFileName(staged.SourcePath),
                SizeBytes = size,
                Encrypted = true,
                Manifest = ToDto(manifest, deviceService.CurrentDeviceId)
            };
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            TryDeleteFile(decrypted);
            return InspectFailed("wrongPassword", staged.SourcePath, size, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(decrypted);
            logger.LogWarning(exception, "Failed to decrypt the backup");
            return InspectFailed("io", staged.SourcePath, size, true);
        }
    }

    /// <summary>校验并解到 Restore/pending，写入 pending.json；下次启动时应用。</summary>
    public async Task<BackupRestoreResultDto> StageRestoreAsync(BackupRestoreRequest request, CancellationToken cancellationToken = default)
    {
        StagedBackup? staged;
        lock (_stagedLock) staged = _staged?.Token == request.Token ? _staged : null;
        if (staged?.ZipPath == null || staged.Manifest == null) return RestoreFailed("invalid");
        var manifest = staged.Manifest;

        if (manifest.Migration != null)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!db.Database.GetMigrations().Contains(manifest.Migration, StringComparer.Ordinal)) return RestoreFailed("newerVersion");
        }

        var paths = new RestorePaths(appPaths);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return RestoreFailed("busy");
        try
        {
            CancelPendingCore(paths);
            Directory.CreateDirectory(paths.PendingDirectory);
            using (var zip = ZipFile.OpenRead(staged.ZipPath))
            {
                foreach (var file in manifest.Files)
                {
                    var skip = file.Name switch
                    {
                        BackupFormat.SettingsEntry => !request.RestoreSettings,
                        BackupFormat.RendererStateEntry => !request.RestoreRendererState,
                        _ => BackupFormat.IsSaveObject(file.Name) && !request.RestoreSaveBackups
                    };
                    if (skip) continue;
                    var destination = Path.Combine(paths.PendingDirectory, file.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (!await BackupFormat.ExtractVerifiedAsync(zip, file, destination, cancellationToken).ConfigureAwait(false))
                    {
                        CancelPendingCore(paths);
                        return RestoreFailed("corrupt");
                    }
                }
            }

            if (!await IntegrityCheckAsync(Path.Combine(paths.PendingDirectory, BackupFormat.DatabaseEntry), cancellationToken).ConfigureAwait(false))
            {
                CancelPendingCore(paths);
                return RestoreFailed("corrupt");
            }

            var pending = new PendingRestore
            {
                StagedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                SourceName = Path.GetFileName(staged.SourcePath),
                BackupCreatedAt = manifest.CreatedAt,
                BackupAppVersion = manifest.AppVersion
            };
            await File.WriteAllTextAsync(paths.PendingFile, JsonSerializer.Serialize(pending, BackupFormat.JsonOptions), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Staged a restore from a backup created at {CreatedAt}; it is applied on the next start", manifest.CreatedAt);
            ClearStaged();
            return new BackupRestoreResultDto { Success = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(exception, "Failed to stage the restore");
            CancelPendingCore(paths);
            return RestoreFailed("io");
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool CancelPendingRestore()
    {
        var paths = new RestorePaths(appPaths);
        var had = File.Exists(paths.PendingFile);
        CancelPendingCore(paths);
        return had;
    }

    public BackupRestoreStatusDto GetRestoreStatus()
    {
        var paths = new RestorePaths(appPaths);
        var pending = ReadJson<PendingRestore>(paths.PendingFile);
        var last = ReadJson<RestoreOutcome>(paths.LastRestoreFile);
        return new BackupRestoreStatusDto
        {
            Pending = pending != null,
            PendingSource = pending?.SourceName,
            LastAppliedAt = last?.AppliedAt,
            LastSource = last?.SourceName,
            LastSuccess = last?.Success,
            LastError = last?.Error
        };
    }

    /// <summary>恢复后的首次启动：把备份里的渲染层偏好交给渲染层（只交一次）。</summary>
    public BackupRendererStateDto TakeRendererState()
    {
        var file = new RestorePaths(appPaths).RendererStateFile;
        try
        {
            if (!File.Exists(file)) return new BackupRendererStateDto();
            var state = File.ReadAllText(file);
            File.Delete(file);
            return new BackupRendererStateDto { State = IsJsonObject(state) ? state : null };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to read the restored renderer state");
            return new BackupRendererStateDto();
        }
    }

    private static void CancelPendingCore(RestorePaths paths)
    {
        TryDeleteFile(paths.PendingFile);
        TryDeleteDirectory(paths.PendingDirectory);
    }

    private static BackupManifest? ReadManifestFrom(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return BackupFormat.ReadManifest(zip);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<bool> IntegrityCheckAsync(string databaseFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(databaseFile)) return false;
        try
        {
            await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false, readOnly: true));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void ClearStaged()
    {
        lock (_stagedLock)
        {
            if (_staged is { OwnsZip: true, ZipPath: not null }) TryDeleteFile(_staged.ZipPath);
            _staged = null;
        }
    }

    #endregion

    internal static string BadgeNamesPath(IAppPaths paths) => Path.Combine(paths.UserDataDirectory, "ProfileHistory", "badge-names.json");

    internal static string SaveObjectsDirectory(IAppPaths paths) => Path.Combine(paths.UserDataDirectory, "SaveBackups", "objects");

    private static IEnumerable<(string Name, string Path)> EnumerateSaveObjects(IAppPaths paths)
    {
        var root = SaveObjectsDirectory(paths);
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var name = BackupFormat.SaveObjectsPrefix + relative;
            if (BackupFormat.IsSaveObject(name)) yield return (name, file);
        }
    }

    private static BackupManifestDto ToDto(BackupManifest manifest, string? currentDeviceId)
    {
        var saveObjects = manifest.Files.Where(file => BackupFormat.IsSaveObject(file.Name)).ToList();
        return new BackupManifestDto
        {
            AppVersion = manifest.AppVersion,
            CreatedAt = manifest.CreatedAt,
            DeviceName = manifest.DeviceName,
            SameDevice = manifest.DeviceId != null && manifest.DeviceId == currentDeviceId,
            DatabaseBytes = manifest.Files.FirstOrDefault(file => file.Name == BackupFormat.DatabaseEntry)?.Size ?? 0,
            HasSettings = manifest.Files.Any(file => file.Name == BackupFormat.SettingsEntry),
            HasRendererState = manifest.Files.Any(file => file.Name == BackupFormat.RendererStateEntry),
            SaveObjectCount = saveObjects.Count,
            SaveObjectBytes = saveObjects.Sum(file => file.Size),
            Counts = manifest.Counts.Select(pair => new BackupCountDto { Key = pair.Key, Count = pair.Value }).ToList()
        };
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogWarning(exception, "Failed to read {File}", Path.GetFileName(path));
            return null;
        }
    }

    private static BackupCreateResultDto CreateFailed(string error)
        => new() { Status = "failed", SizeBytes = 0, Encrypted = false, Error = error };

    private static BackupInspectResultDto InspectCancelled()
        => new() { Status = "cancelled", SizeBytes = 0, Encrypted = false };

    private static BackupInspectResultDto InspectFailed(string error, string? path, long size, bool encrypted)
        => new() { Status = "failed", FileName = path == null ? null : Path.GetFileName(path), SizeBytes = size, Encrypted = encrypted, Error = error };

    private static BackupRestoreResultDto RestoreFailed(string error) => new() { Success = false, Error = error };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        ClearStaged();
        _gate.Dispose();
    }
}

/// <summary>恢复相关文件都在 {UserData}/Restore 下。</summary>
internal sealed class RestorePaths(IAppPaths appPaths)
{
    internal string Root => Path.Combine(appPaths.UserDataDirectory, "Restore");
    internal string PendingDirectory => Path.Combine(Root, "pending");
    internal string PendingFile => Path.Combine(Root, "pending.json");
    internal string RendererStateFile => Path.Combine(Root, "renderer-state.json");
    internal string LastRestoreFile => Path.Combine(Root, "last-restore.json");
}
