using System.Security.Cryptography;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Features.Sync;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Sync;

namespace ElectronNet.Services;

internal sealed record SyncProgressChanged(SyncStatusDto Status);

/// <summary>
/// 多设备同步（自带存储，端到端加密）。
/// <para>远端布局：vault.json（口令包裹的主密钥）+ devices/{设备}/seg-{序号}.ssec（加密的变更段）+ devices/{设备}/info.ssec。</para>
/// <para>
/// 每台设备只写自己的目录，所以不需要锁或条件写入，任何「按前缀列出 / 读 / 写 / 删」的存储都能用。
/// 一次同步先拉取（应用其他设备的新段），再推送（本机 outbox → 新段；首次或恢复后上传全量快照）。
/// 本机段超过 <see cref="CompactThreshold"/> 个时写一份快照并删除旧段。Steam 凭证永远不参与同步。
/// </para>
/// </summary>
internal sealed class SyncService(
    IAppPaths appPaths,
    SyncConfigStore configStore,
    IHttpClientFactory httpClientFactory,
    DeviceService deviceService,
    AppEnvironment environment,
    IMainWindowAccessor mainWindowAccessor,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<SyncService> logger) : IEventHandler<UsageSessionEnded>, IDisposable
{
    internal const string HttpClientName = "Sync";
    internal const int CompactThreshold = 200;
    private const int MinPassphraseLength = 8;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionEndDelay = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Lock _statusLock = new();
    private string _state = "idle";
    private string? _phase;
    private int _done;
    private int _total;
    private long _dueAt;
    private ITimer? _timer;
    private CancellationTokenSource? _runCancellation;
    private Task _publishChain = Task.CompletedTask;

    /// <summary>连接成功后立即在后台同步一次（测试中关闭以便按步骤驱动）。</summary>
    internal bool RunAfterSetup { get; set; } = true;

    /// <summary>测试用：直接替换存储后端。</summary>
    internal Func<SyncConfigFile, SyncSecrets, ISyncStorage>? StorageOverride { get; set; }

    #region 生命周期

    /// <summary>启动时（迁移之后）安装触发器并开始定时同步。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            // 上次运行若在应用远端变更时崩溃，applying 不会残留（事务回滚），这里再保险一次
            await SqliteSchema.ExecuteAsync(connection, "UPDATE sync_control SET applying = 0 WHERE id = 1 AND applying = 1", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            logger.LogWarning(exception, "Failed to prepare sync triggers");
        }
        _timer ??= timeProvider.CreateTimer(_ => OnTick(), null, InitialDelay, TickInterval);
    }

    private void OnTick()
    {
        try
        {
            var config = configStore.Read();
            if (config == null || !config.AutoSync) return;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var due = Interlocked.Read(ref _dueAt);
            var last = ReadLastSyncAt();
            if ((due > 0 && now >= due) || last == null || now - last >= config.IntervalMinutes * 60L)
            {
                Interlocked.Exchange(ref _dueAt, 0);
                _ = RunInBackgroundAsync("schedule");
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Sync scheduling failed");
        }
    }

    /// <summary>游戏会话结束 2 分钟后同步一次，让其他设备尽快看到。</summary>
    public Task HandleAsync(UsageSessionEnded message, CancellationToken cancellationToken)
    {
        var due = timeProvider.GetUtcNow().Add(SessionEndDelay).ToUnixTimeSeconds();
        Interlocked.Exchange(ref _dueAt, due);
        return Task.CompletedTask;
    }

    private long? ReadLastSyncAt()
    {
        try
        {
            using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_sync_at FROM sync_control WHERE id = 1 AND enabled = 1";
            return command.ExecuteScalar() as long?;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    #endregion

    #region 状态

    public async Task<SyncStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = configStore.Read();
        var secrets = configStore.ReadSecrets(config);
        var control = new ControlState(false, null, 0, false, null, null);
        var pending = 0;
        var devices = new List<SyncDeviceDto>();
        var deviceId = deviceService.CurrentDeviceId;
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            control = await ReadControlAsync(connection, cancellationToken).ConfigureAwait(false);
            pending = (int)await SqliteSchema.ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM (SELECT DISTINCT table_name, row_id FROM sync_outbox)", cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT device_id, last_seq, device_name, app_version, platform, remote_updated_at FROM sync_cursor ORDER BY remote_updated_at DESC";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                devices.Add(new SyncDeviceDto
                {
                    DeviceId = reader.GetString(0),
                    LastSeq = reader.GetInt64(1),
                    Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    AppVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Platform = reader.IsDBNull(4) ? null : reader.GetString(4),
                    RemoteUpdatedAt = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    IsCurrent = false
                });
            }
        }
        catch (SqliteException exception)
        {
            logger.LogWarning(exception, "Failed to read sync status");
        }

        if (control.Enabled && deviceId != null)
        {
            devices.Insert(0, new SyncDeviceDto
            {
                DeviceId = deviceId,
                Name = Environment.MachineName,
                AppVersion = environment.AppVersion,
                Platform = PlatformRuntime.PlatformName,
                LastSeq = control.DeviceSeq,
                RemoteUpdatedAt = control.LastSyncAt,
                IsCurrent = true
            });
        }

        string state;
        string? phase;
        int done, total;
        lock (_statusLock)
        {
            state = _state;
            phase = _phase;
            done = _done;
            total = _total;
        }
        return new SyncStatusDto
        {
            Configured = config != null && control.Enabled,
            Enabled = control.Enabled,
            Locked = config != null && control.Enabled && secrets.MasterKey == null,
            CanPersistSecrets = configStore.CanPersistSecrets,
            Config = config == null ? null : SyncConfigStore.ToDto(config),
            HasWebDavPassword = !string.IsNullOrEmpty(secrets.WebDavPassword),
            HasS3Secret = !string.IsNullOrEmpty(secrets.S3SecretAccessKey),
            State = state == "running" ? state : control.LastError != null ? "error" : "idle",
            Phase = phase,
            Done = done,
            Total = total,
            LastSyncAt = control.LastSyncAt,
            LastError = control.LastError,
            PendingChanges = pending + (control.Enabled && !control.FullExportDone ? 1 : 0),
            VaultId = control.VaultId,
            DeviceId = deviceId,
            Devices = devices
        };
    }

    private void SetProgress(string state, string? phase, int done, int total)
    {
        lock (_statusLock)
        {
            _state = state;
            _phase = phase;
            _done = done;
            _total = total;
        }
        Publish();
    }

    private void Publish()
    {
        lock (_statusLock)
        {
            _publishChain = _publishChain.ContinueWith(async _ =>
            {
                try
                {
                    await eventBus.PublishAsync(new SyncProgressChanged(await GetStatusAsync().ConfigureAwait(false))).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Failed to publish sync progress");
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    #endregion

    #region 连接与设置

    public async Task<SyncTestResultDto> TestAsync(SyncConnectRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var (config, secrets) = PrepareConnection(request);
            using var http = httpClientFactory.CreateClient(HttpClientName);
            var storage = CreateStorage(config, secrets, http);
            var vault = await storage.GetAsync(SyncVaultFile.Key, cancellationToken).ConfigureAwait(false);
            var devices = (await storage.ListAsync("devices/", cancellationToken).ConfigureAwait(false))
                .Select(item => SyncSegment.ParseObjectKey(item.Key)?.DeviceId ?? (item.Key.Split('/') is { Length: 3 } parts ? parts[1] : null))
                .Where(id => id != null)
                .Distinct(StringComparer.Ordinal)
                .Count();
            return new SyncTestResultDto { Success = true, VaultExists = vault != null, DeviceCount = devices };
        }
        catch (Exception exception) when (MapError(exception) is { } code)
        {
            logger.LogInformation(exception, "Sync connection test failed ({Code})", code);
            return new SyncTestResultDto { Success = false, Error = code, VaultExists = false, DeviceCount = 0 };
        }
    }

    /// <summary>
    /// 连接（或修改设置）：远端没有保险库时用口令创建；已有时用口令解锁（加入已有同步）。
    /// 只修改设置时，若远端保险库与本机记录一致，可以不再输入口令。
    /// </summary>
    public async Task<SyncSetupResultDto> SetupAsync(SyncSetupRequest request, CancellationToken cancellationToken = default)
    {
        string? passphrase = null;
        if (!string.IsNullOrEmpty(request.Passphrase))
        {
            passphrase = IpcSecret.Decode(request.Passphrase);
            if (passphrase == null) return SetupFailed("invalid");
        }

        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = configStore.Read();
            var existingSecrets = configStore.ReadSecrets(existing);
            var (config, secrets) = PrepareConnection(request.Connection);
            using var http = httpClientFactory.CreateClient(HttpClientName);
            var storage = CreateStorage(config, secrets, http);

            var created = false;
            byte[] masterKey;
            var vaultBytes = await storage.GetAsync(SyncVaultFile.Key, cancellationToken).ConfigureAwait(false);
            SyncVaultFile vault;
            if (vaultBytes == null)
            {
                if (passphrase == null) return SetupFailed("passphraseRequired");
                if (passphrase.Length < MinPassphraseLength) return SetupFailed("weakPassphrase");
                (vault, masterKey) = await SyncVault.CreateAsync(passphrase, timeProvider.GetUtcNow().ToUnixTimeSeconds(), cancellationToken).ConfigureAwait(false);
                await storage.PutAsync(SyncVaultFile.Key, SyncVault.Serialize(vault), cancellationToken).ConfigureAwait(false);
                created = true;
            }
            else
            {
                vault = SyncVault.Parse(vaultBytes) ?? throw new InvalidDataException("vaultCorrupt");
                if (passphrase != null)
                {
                    try
                    {
                        masterKey = await SyncVault.UnlockAsync(vault, passphrase, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CryptographicException)
                    {
                        return SetupFailed("wrongPassphrase");
                    }
                }
                else if (existingSecrets.MasterKey != null && existing?.VaultId == vault.VaultId)
                {
                    masterKey = existingSecrets.MasterKey;
                }
                else
                {
                    return SetupFailed("passphraseRequired");
                }
            }

            config.VaultId = vault.VaultId;
            configStore.Save(config, secrets with { MasterKey = masterKey });

            await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                var control = await ReadControlAsync(connection, cancellationToken).ConfigureAwait(false);
                if (!control.Enabled || control.VaultId != vault.VaultId)
                {
                    // 新连接（或换了保险库）：本机像新设备一样先拉取远端，再上传全量快照
                    await ResetStateAsync(connection, vault.VaultId, cancellationToken).ConfigureAwait(false);
                }
            }
            logger.LogInformation("Sync connected to vault {VaultId} via {Backend} (created: {Created})", vault.VaultId, config.Backend, created);
            if (RunAfterSetup) _ = RunInBackgroundAsync("setup");
            return new SyncSetupResultDto { Success = true, CreatedVault = created };
        }
        catch (Exception exception) when (exception is InvalidDataException { Message: "vaultCorrupt" })
        {
            return SetupFailed("vaultCorrupt");
        }
        catch (Exception exception) when (MapError(exception) is { } code)
        {
            logger.LogWarning(exception, "Sync setup failed ({Code})", code);
            return SetupFailed(code);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>断开同步：停止记录变更、清空本机同步状态（远端数据保留）；可选删除保存的密码与密钥。</summary>
    public async Task<bool> DisableAsync(SyncDisableRequest request, CancellationToken cancellationToken = default)
    {
        _runCancellation?.Cancel();
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteSchema.ExecuteAsync(connection, """
                UPDATE sync_control SET enabled = 0, applying = 0, vault_id = NULL, full_export_done = 0, last_error = NULL WHERE id = 1;
                DELETE FROM sync_outbox;
                DELETE FROM sync_cursor;
                DELETE FROM sync_version;
                """, cancellationToken).ConfigureAwait(false);
            if (request.ForgetCredentials) configStore.Delete();
            logger.LogInformation("Sync disconnected (forget credentials: {Forget})", request.ForgetCredentials);
            return true;
        }
        finally
        {
            _runGate.Release();
            SetProgress("idle", null, 0, 0);
        }
    }

    /// <summary>修改同步口令：用新口令重新包裹同一把主密钥，其他设备不需要重新输入。</summary>
    public async Task<bool> ChangePassphraseAsync(SyncPassphraseChangeRequest request, CancellationToken cancellationToken = default)
    {
        var passphrase = IpcSecret.Decode(request.Passphrase);
        if (passphrase is not { Length: >= MinPassphraseLength }) return false;
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = configStore.Read();
            var secrets = configStore.ReadSecrets(config);
            if (config == null || secrets.MasterKey == null) return false;
            using var http = httpClientFactory.CreateClient(HttpClientName);
            var storage = CreateStorage(config, secrets, http);
            var vault = SyncVault.Parse(await storage.GetAsync(SyncVaultFile.Key, cancellationToken).ConfigureAwait(false) ?? []);
            if (vault == null || vault.VaultId != config.VaultId) return false;
            var rewrapped = await SyncVault.RewrapAsync(vault, secrets.MasterKey, passphrase, cancellationToken).ConfigureAwait(false);
            await storage.PutAsync(SyncVaultFile.Key, SyncVault.Serialize(rewrapped), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (MapError(exception) is not null)
        {
            logger.LogWarning(exception, "Failed to change the sync passphrase");
            return false;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task<string?> PickFolderAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        try
        {
            var result = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
            {
                Properties = [OpenDialogProperty.openDirectory, OpenDialogProperty.createDirectory]
            }).ConfigureAwait(false);
            return result is { Length: > 0 } ? result[0] : null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the sync folder picker");
            return null;
        }
    }

    /// <summary>合并请求与已保存的设置：密码留空表示沿用已保存的（仅限同一后端）。</summary>
    private (SyncConfigFile Config, SyncSecrets Secrets) PrepareConnection(SyncConnectRequest request)
    {
        var existing = configStore.Read();
        var existingSecrets = configStore.ReadSecrets(existing);
        var sameBackend = existing?.Backend == request.Config.Backend;
        var config = SyncConfigStore.FromDto(request.Config, existing);
        string? Decode(string? encoded, string? saved)
        {
            if (string.IsNullOrEmpty(encoded)) return sameBackend ? saved : null;
            return IpcSecret.Decode(encoded) ?? throw new SyncStorageException(SyncStorageErrorKind.Configuration, "invalid secret");
        }
        var secrets = new SyncSecrets(
            Decode(request.WebDavPassword, existingSecrets.WebDavPassword),
            Decode(request.S3SecretAccessKey, existingSecrets.S3SecretAccessKey),
            existingSecrets.MasterKey);
        return (config, secrets);
    }

    private ISyncStorage CreateStorage(SyncConfigFile config, SyncSecrets secrets, HttpClient http)
        => StorageOverride?.Invoke(config, secrets) ?? SyncConfigStore.CreateStorage(config, secrets, http);

    #endregion

    #region 同步

    /// <summary>手动「立即同步」。已在同步时返回 false。</summary>
    public bool RunNow()
    {
        if (_runGate.CurrentCount == 0) return false;
        _ = RunInBackgroundAsync("manual");
        return true;
    }

    private async Task RunInBackgroundAsync(string trigger)
    {
        try
        {
            await RunAsync(trigger).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Sync run crashed");
        }
    }

    /// <summary>执行一次完整同步，返回错误码（成功时为 null）。</summary>
    internal async Task<string?> RunAsync(string trigger, CancellationToken cancellationToken = default)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return "busy";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCancellation = cancellation;
        string? error = null;
        try
        {
            SetProgress("running", "pull", 0, 0);
            error = await RunCoreAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error = "cancelled";
        }
        catch (Exception exception) when (MapError(exception) is { } code)
        {
            logger.LogWarning(exception, "Sync failed ({Code})", code);
            error = code;
        }
        finally
        {
            try
            {
                await using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = error == null
                    ? "UPDATE sync_control SET last_sync_at = $now, last_error = NULL WHERE id = 1"
                    : "UPDATE sync_control SET last_error = $error WHERE id = 1 AND enabled = 1";
                command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException exception)
            {
                logger.LogWarning(exception, "Failed to record the sync result");
            }
            _runCancellation = null;
            _runGate.Release();
            SetProgress("idle", null, 0, 0);
        }
        if (error is null or "notConfigured") logger.LogInformation("Sync finished ({Trigger})", trigger);
        return error;
    }

    private async Task<string?> RunCoreAsync(CancellationToken cancellationToken)
    {
        var config = configStore.Read();
        if (config == null) return "notConfigured";
        var secrets = configStore.ReadSecrets(config);
        if (secrets.MasterKey == null) return "locked";
        var masterKey = secrets.MasterKey;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var control = await ReadControlAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!control.Enabled) return "notConfigured";

        using var http = httpClientFactory.CreateClient(HttpClientName);
        var storage = CreateStorage(config, secrets, http);
        var vault = SyncVault.Parse(await storage.GetAsync(SyncVaultFile.Key, cancellationToken).ConfigureAwait(false) ?? []);
        if (vault == null) return "vaultMissing";
        if (vault.VaultId != control.VaultId) return "vaultMismatch";

        var me = await deviceService.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var engine = new SyncEngine(me, timeProvider);
        var listing = await storage.ListAsync("devices/", cancellationToken).ConfigureAwait(false);
        var segments = listing
            .Select(item => (item.Key, Parsed: SyncSegment.ParseObjectKey(item.Key)))
            .Where(item => item.Parsed != null)
            .Select(item => (item.Key, item.Parsed!.Value.DeviceId, item.Parsed.Value.Seq))
            .ToList();

        // ---------------- 拉取
        var cursors = await ReadCursorsAsync(connection, cancellationToken).ConfigureAwait(false);
        var toPull = segments
            .Where(item => item.DeviceId != me && item.Seq > cursors.GetValueOrDefault(item.DeviceId))
            .OrderBy(item => item.DeviceId, StringComparer.Ordinal).ThenBy(item => item.Seq)
            .ToList();
        var pulled = 0;
        var blockedDevices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in toPull)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blockedDevices.Contains(segment.DeviceId)) continue;
            SetProgress("running", "pull", pulled, toPull.Count);
            var content = await storage.GetAsync(segment.Key, cancellationToken).ConfigureAwait(false);
            pulled++;
            if (content == null) continue; // 在列出之后被压缩删除，快照会覆盖它
            var decoded = await SyncSegment.DecodeAsync<SyncSegment>(content, masterKey, cancellationToken).ConfigureAwait(false);
            if (decoded == null || decoded.DeviceId != segment.DeviceId || decoded.Seq != segment.Seq)
                throw new InvalidDataException("A sync segment does not match its name.");
            if (decoded.Version > SyncSegment.CurrentVersion)
            {
                // 更新版本的应用写出的段：停在这里，等本机升级后再继续这台设备
                blockedDevices.Add(segment.DeviceId);
                continue;
            }
            await engine.ApplyAsync(connection, decoded.Changes, transaction => SaveCursorAsync(connection, transaction, segment.DeviceId, segment.Seq, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        await RefreshDeviceInfoAsync(connection, storage, masterKey, segments.Select(item => item.DeviceId).Where(id => id != me).Distinct(), cancellationToken).ConfigureAwait(false);

        // ---------------- 推送
        SetProgress("running", "push", 0, 0);
        var own = segments.Where(item => item.DeviceId == me).Select(item => item.Seq).ToList();
        var seq = Math.Max(control.DeviceSeq, own.Count == 0 ? 0 : own.Max());
        var written = new List<long>();
        long? snapshotStart = null;

        async Task WriteSegmentAsync(string kind, List<SyncChange> changes)
        {
            seq++;
            var segment = new SyncSegment { DeviceId = me, Seq = seq, Kind = kind, CreatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(), Changes = changes };
            await storage.PutAsync(SyncSegment.ObjectKey(me, seq), await SyncSegment.EncodeAsync(segment, masterKey, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
            await SyncEngine.RecordVersionsAsync(connection, changes, cancellationToken).ConfigureAwait(false);
            await SetDeviceSeqAsync(connection, seq, cancellationToken).ConfigureAwait(false);
            written.Add(seq);
            SetProgress("running", "push", written.Count, 0);
        }

        async Task WriteSnapshotAsync()
        {
            var outboxAtStart = await SqliteSchema.ScalarLongAsync(connection, "SELECT IFNULL(MAX(seq), 0) FROM sync_outbox", cancellationToken).ConfigureAwait(false);
            snapshotStart = seq + 1;
            var any = false;
            await foreach (var chunk in engine.ReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                await WriteSegmentAsync("snapshot", chunk).ConfigureAwait(false);
                any = true;
            }
            if (!any) await WriteSegmentAsync("snapshot", []).ConfigureAwait(false);
            await SyncEngine.ClearOutboxAsync(connection, outboxAtStart, cancellationToken).ConfigureAwait(false);
            await SqliteSchema.ExecuteAsync(connection, "UPDATE sync_control SET full_export_done = 1 WHERE id = 1", cancellationToken).ConfigureAwait(false);
        }

        if (!control.FullExportDone) await WriteSnapshotAsync().ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (changes, maxSeq) = await engine.ReadOutboxAsync(connection, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (maxSeq == 0) break;
            if (changes.Count > 0) await WriteSegmentAsync("changes", changes).ConfigureAwait(false);
            await SyncEngine.ClearOutboxAsync(connection, maxSeq, cancellationToken).ConfigureAwait(false);
        }

        // ---------------- 压缩：本机段太多时写一份快照，删除之前的段
        if (snapshotStart == null && own.Count + written.Count > CompactThreshold)
        {
            SetProgress("running", "compact", 0, 0);
            await WriteSnapshotAsync().ConfigureAwait(false);
        }
        if (snapshotStart != null)
        {
            foreach (var old in own.Concat(written).Where(value => value < snapshotStart.Value))
            {
                await storage.DeleteAsync(SyncSegment.ObjectKey(me, old), cancellationToken).ConfigureAwait(false);
            }
        }

        var info = new SyncDeviceInfo
        {
            DeviceId = me,
            Name = Environment.MachineName,
            Platform = PlatformRuntime.PlatformName,
            AppVersion = environment.AppVersion,
            LastSeq = seq,
            UpdatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
        };
        await storage.PutAsync(SyncSegment.InfoKey(me), await SyncSegment.EncodeAsync(info, masterKey, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        return blockedDevices.Count > 0 ? "newerFormat" : null;
    }

    private async Task RefreshDeviceInfoAsync(SqliteConnection connection, ISyncStorage storage, byte[] masterKey, IEnumerable<string> deviceIds, CancellationToken cancellationToken)
    {
        foreach (var deviceId in deviceIds)
        {
            try
            {
                var content = await storage.GetAsync(SyncSegment.InfoKey(deviceId), cancellationToken).ConfigureAwait(false);
                if (content == null) continue;
                var info = await SyncSegment.DecodeAsync<SyncDeviceInfo>(content, masterKey, cancellationToken).ConfigureAwait(false);
                if (info == null || info.DeviceId != deviceId) continue;
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO sync_cursor (device_id, last_seq, device_name, app_version, platform, remote_updated_at)
                    VALUES ($id, 0, $name, $version, $platform, $updated)
                    ON CONFLICT (device_id) DO UPDATE SET device_name = excluded.device_name, app_version = excluded.app_version,
                      platform = excluded.platform, remote_updated_at = excluded.remote_updated_at
                    """;
                command.Parameters.AddWithValue("$id", deviceId);
                command.Parameters.AddWithValue("$name", Truncate(info.Name, 128));
                command.Parameters.AddWithValue("$version", (object?)Truncate(info.AppVersion, 32) ?? DBNull.Value);
                command.Parameters.AddWithValue("$platform", (object?)Truncate(info.Platform, 32) ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", info.UpdatedAt);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidDataException or System.Text.Json.JsonException)
            {
                logger.LogDebug(exception, "Ignoring unreadable device info for {DeviceId}", deviceId);
            }
        }
    }

    #endregion

    #region 本机状态

    private sealed record ControlState(bool Enabled, string? VaultId, long DeviceSeq, bool FullExportDone, long? LastSyncAt, string? LastError);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SyncSchema.EnsureTriggersAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<ControlState> ReadControlAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await SqliteSchema.ExecuteAsync(connection,
            "INSERT OR IGNORE INTO sync_control (id, enabled, applying, device_seq, full_export_done) VALUES (1, 0, 0, 0, 0)", cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, vault_id, device_seq, full_export_done, last_sync_at, last_error FROM sync_control WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new ControlState(false, null, 0, false, null, null);
        return new ControlState(
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    /// <summary>连接到（新的）保险库：清掉旧的进度与版本，本机像新设备一样先拉取再上传全量快照。</summary>
    private static async Task ResetStateAsync(SqliteConnection connection, string vaultId, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM sync_outbox;
                DELETE FROM sync_cursor;
                DELETE FROM sync_version;
                UPDATE sync_control SET enabled = 1, applying = 0, vault_id = $vault, full_export_done = 0, last_error = NULL WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$vault", vaultId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, long>> ReadCursorsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, last_seq FROM sync_cursor";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) cursors[reader.GetString(0)] = reader.GetInt64(1);
        return cursors;
    }

    private async Task SaveCursorAsync(SqliteConnection connection, SqliteTransaction transaction, string deviceId, long seq, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_cursor (device_id, last_seq, applied_at) VALUES ($id, $seq, $now)
            ON CONFLICT (device_id) DO UPDATE SET last_seq = MAX(sync_cursor.last_seq, excluded.last_seq), applied_at = excluded.applied_at
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetDeviceSeqAsync(SqliteConnection connection, long seq, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync_control SET device_seq = MAX(device_seq, $seq) WHERE id = 1";
        command.Parameters.AddWithValue("$seq", seq);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    /// <summary>异常 → 错误码；不认识的异常返回 null（交给上层记录为崩溃）。</summary>
    internal static string? MapError(Exception exception) => exception switch
    {
        SyncStorageException storage => storage.Kind switch
        {
            SyncStorageErrorKind.Unauthorized => "unauthorized",
            SyncStorageErrorKind.Network => "network",
            SyncStorageErrorKind.Configuration => "configuration",
            SyncStorageErrorKind.ClockSkew => "clockSkew",
            _ => "protocol"
        },
        CryptographicException => "decryptFailed",
        InvalidDataException or System.Text.Json.JsonException => "corrupt",
        SqliteException => "database",
        IOException or UnauthorizedAccessException => "io",
        ArgumentException => "configuration",
        InvalidOperationException => "failed",
        _ => null
    };

    private static SyncSetupResultDto SetupFailed(string error) => new() { Success = false, Error = error, CreatedVault = false };

    private static string? Truncate(string? value, int length) => value == null ? null : value.Length <= length ? value : value[..length];

    public void Dispose()
    {
        _timer?.Dispose();
        _runCancellation?.Cancel();
        Task chain;
        lock (_statusLock) chain = _publishChain;
        try
        {
            // 进度推送会打开数据库连接读取状态，退出前等它结束
            chain.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _runGate.Dispose();
    }
}
