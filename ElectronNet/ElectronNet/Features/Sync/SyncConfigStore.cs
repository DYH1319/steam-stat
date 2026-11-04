using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Platform;
using SteamStat.Core.Sync;

namespace ElectronNet.Features.Sync;

/// <summary>{UserData}/Sync/config.json：后端设置（明文）与密钥（经 DPAPI / libsecret 保护）。</summary>
internal sealed class SyncConfigFile
{
    [JsonPropertyName("backend")] public string Backend { get; set; } = "folder";
    [JsonPropertyName("folderPath")] public string? FolderPath { get; set; }
    [JsonPropertyName("webDavUrl")] public string? WebDavUrl { get; set; }
    [JsonPropertyName("webDavUser")] public string? WebDavUser { get; set; }
    [JsonPropertyName("s3Endpoint")] public string? S3Endpoint { get; set; }
    [JsonPropertyName("s3Region")] public string? S3Region { get; set; }
    [JsonPropertyName("s3Bucket")] public string? S3Bucket { get; set; }
    [JsonPropertyName("s3AccessKeyId")] public string? S3AccessKeyId { get; set; }
    [JsonPropertyName("s3PathStyle")] public bool S3PathStyle { get; set; } = true;
    [JsonPropertyName("remoteRoot")] public string RemoteRoot { get; set; } = SyncConfigStore.DefaultRemoteRoot;
    [JsonPropertyName("intervalMinutes")] public int IntervalMinutes { get; set; } = 15;
    [JsonPropertyName("autoSync")] public bool AutoSync { get; set; } = true;
    [JsonPropertyName("vaultId")] public string? VaultId { get; set; }
    [JsonPropertyName("webDavPassword")] public string? ProtectedWebDavPassword { get; set; }
    [JsonPropertyName("s3SecretAccessKey")] public string? ProtectedS3Secret { get; set; }
    [JsonPropertyName("masterKey")] public string? ProtectedMasterKey { get; set; }
}

/// <summary>解密后的密钥，只在内存中使用。</summary>
internal sealed record SyncSecrets(string? WebDavPassword, string? S3SecretAccessKey, byte[]? MasterKey);

/// <summary>
/// 同步配置与密钥。密钥写盘前一律经 <see cref="ISecretStore"/> 保护；不能安全保存（Linux 没有密钥环）时
/// 只保存在本次运行的内存中。配置文件不进入手动备份（换电脑恢复后需要重新连接同步）。
/// </summary>
internal sealed class SyncConfigStore(IAppPaths appPaths, ISecretStore secretStore, ILogger<SyncConfigStore> logger)
{
    internal const string DefaultRemoteRoot = "steam-stat-sync";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private SyncSecrets? _memorySecrets;

    internal string Directory => Path.Combine(appPaths.UserDataDirectory, "Sync");
    private string FilePath => Path.Combine(Directory, "config.json");

    internal bool CanPersistSecrets => secretStore.CanPersist;

    internal SyncConfigFile? Read()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<SyncConfigFile>(File.ReadAllText(FilePath)) : null;
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                logger.LogWarning(exception, "Failed to read the sync configuration");
                return null;
            }
        }
    }

    internal SyncSecrets ReadSecrets(SyncConfigFile? config)
    {
        lock (_lock)
        {
            if (config == null) return new SyncSecrets(null, null, null);
            var memory = _memorySecrets;
            var masterKey = Unprotect(config.ProtectedMasterKey) is { } key ? TryBase64(key) : null;
            return new SyncSecrets(
                Unprotect(config.ProtectedWebDavPassword) ?? memory?.WebDavPassword,
                Unprotect(config.ProtectedS3Secret) ?? memory?.S3SecretAccessKey,
                masterKey ?? memory?.MasterKey);
        }
    }

    internal void Save(SyncConfigFile config, SyncSecrets secrets)
    {
        lock (_lock)
        {
            if (secretStore.CanPersist)
            {
                config.ProtectedWebDavPassword = Protect(secrets.WebDavPassword);
                config.ProtectedS3Secret = Protect(secrets.S3SecretAccessKey);
                config.ProtectedMasterKey = secrets.MasterKey == null ? null : Protect(Convert.ToBase64String(secrets.MasterKey));
                _memorySecrets = null;
            }
            else
            {
                config.ProtectedWebDavPassword = null;
                config.ProtectedS3Secret = null;
                config.ProtectedMasterKey = null;
                _memorySecrets = secrets;
            }
            System.IO.Directory.CreateDirectory(Directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    internal void Delete()
    {
        lock (_lock)
        {
            _memorySecrets = null;
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
    }

    internal static SyncConfigFile FromDto(SyncConfigDto dto, SyncConfigFile? existing)
        => new()
        {
            Backend = dto.Backend,
            FolderPath = Trim(dto.FolderPath),
            WebDavUrl = Trim(dto.WebDavUrl),
            WebDavUser = Trim(dto.WebDavUser),
            S3Endpoint = Trim(dto.S3Endpoint),
            S3Region = Trim(dto.S3Region),
            S3Bucket = Trim(dto.S3Bucket),
            S3AccessKeyId = Trim(dto.S3AccessKeyId),
            S3PathStyle = dto.S3PathStyle ?? true,
            RemoteRoot = SyncKeys.NormalizeRoot(string.IsNullOrWhiteSpace(dto.RemoteRoot) ? DefaultRemoteRoot : dto.RemoteRoot),
            IntervalMinutes = Math.Clamp(dto.IntervalMinutes, 5, 1440),
            AutoSync = dto.AutoSync ?? true,
            VaultId = existing?.VaultId,
            ProtectedWebDavPassword = existing?.ProtectedWebDavPassword,
            ProtectedS3Secret = existing?.ProtectedS3Secret,
            ProtectedMasterKey = existing?.ProtectedMasterKey
        };

    internal static SyncConfigDto ToDto(SyncConfigFile config)
        => new()
        {
            Backend = config.Backend is "folder" or "webdav" or "s3" ? config.Backend : "folder",
            FolderPath = config.FolderPath,
            WebDavUrl = config.WebDavUrl,
            WebDavUser = config.WebDavUser,
            S3Endpoint = config.S3Endpoint,
            S3Region = config.S3Region,
            S3Bucket = config.S3Bucket,
            S3AccessKeyId = config.S3AccessKeyId,
            S3PathStyle = config.S3PathStyle,
            RemoteRoot = config.RemoteRoot,
            IntervalMinutes = config.IntervalMinutes,
            AutoSync = config.AutoSync
        };

    /// <summary>根据配置创建存储后端。配置不完整时抛出 <see cref="SyncStorageException"/>（Configuration）。</summary>
    internal static ISyncStorage CreateStorage(SyncConfigFile config, SyncSecrets secrets, HttpClient http)
        => config.Backend switch
        {
            "folder" => new LocalFolderSyncStorage(config.FolderPath ?? string.Empty, config.RemoteRoot),
            "webdav" => new WebDavSyncStorage(http, config.WebDavUrl ?? string.Empty, config.WebDavUser, secrets.WebDavPassword, config.RemoteRoot),
            "s3" => new S3SyncStorage(http, new S3SyncOptions
            {
                Endpoint = config.S3Endpoint ?? string.Empty,
                Region = config.S3Region ?? string.Empty,
                Bucket = config.S3Bucket ?? string.Empty,
                AccessKeyId = config.S3AccessKeyId ?? string.Empty,
                SecretAccessKey = secrets.S3SecretAccessKey ?? string.Empty,
                PathStyle = config.S3PathStyle,
                RemoteRoot = config.RemoteRoot
            }),
            // 官方托管云（Open Core 的付费部分）尚未推出：以后在这里接入另一个 ISyncStorage 实现
            _ => throw new SyncStorageException(SyncStorageErrorKind.Configuration, "Unsupported sync backend.")
        };

    private string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return secretStore.Protect(value) ?? throw new InvalidOperationException("The credential could not be protected.");
    }

    private string? Unprotect(string? value) => string.IsNullOrEmpty(value) ? null : secretStore.Unprotect(value);

    private static byte[]? TryBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
