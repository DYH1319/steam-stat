using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamStat.Core.Security;

namespace SteamStat.Core.Sync;

/// <summary>
/// 远端根目录下的 vault.json：保存用同步口令包裹的随机主密钥（256 位）。
/// 口令只在设置同步时使用一次；之后各设备用本机安全存储（DPAPI / libsecret）保存主密钥。
/// 远端只有口令派生密钥加密后的主密钥，服务端（包括将来的托管云）无法解密任何数据。
/// </summary>
public sealed class SyncVaultFile
{
    public const string Key = "vault.json";
    public const string FormatName = "steam-stat-sync";
    public const int CurrentVersion = 1;

    [JsonPropertyName("format")] public string Format { get; init; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; init; } = CurrentVersion;
    [JsonPropertyName("vaultId")] public string VaultId { get; init; } = string.Empty;
    [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
    /// <summary>Base64：ChunkedEncryption（口令 → PBKDF2）加密的主密钥。</summary>
    [JsonPropertyName("wrappedKey")] public string WrappedKey { get; init; } = string.Empty;
}

public static class SyncVault
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<(SyncVaultFile File, byte[] MasterKey)> CreateAsync(string passphrase, long now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        var masterKey = RandomNumberGenerator.GetBytes(ChunkedEncryption.KeySize);
        var wrapped = await ChunkedEncryption.EncryptBytesAsync(masterKey, new EncryptionKeySource.Passphrase(passphrase), cancellationToken).ConfigureAwait(false);
        var file = new SyncVaultFile
        {
            VaultId = "v_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            CreatedAt = now,
            WrappedKey = Convert.ToBase64String(wrapped)
        };
        return (file, masterKey);
    }

    /// <summary>口令错误时抛出 <see cref="CryptographicException"/>。</summary>
    public static async Task<byte[]> UnlockAsync(SyncVaultFile file, string passphrase, CancellationToken cancellationToken = default)
    {
        byte[] wrapped;
        try
        {
            wrapped = Convert.FromBase64String(file.WrappedKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The sync vault is damaged.", exception);
        }
        var key = await ChunkedEncryption.DecryptBytesAsync(wrapped, passphrase, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (key.Length != ChunkedEncryption.KeySize) throw new InvalidDataException("The sync vault is damaged.");
        return key;
    }

    /// <summary>修改口令：用新口令重新包裹同一把主密钥（已有数据不需要重新加密）。</summary>
    public static async Task<SyncVaultFile> RewrapAsync(SyncVaultFile file, byte[] masterKey, string newPassphrase, CancellationToken cancellationToken = default)
    {
        var wrapped = await ChunkedEncryption.EncryptBytesAsync(masterKey, new EncryptionKeySource.Passphrase(newPassphrase), cancellationToken).ConfigureAwait(false);
        return new SyncVaultFile { VaultId = file.VaultId, CreatedAt = file.CreatedAt, WrappedKey = Convert.ToBase64String(wrapped) };
    }

    public static byte[] Serialize(SyncVaultFile file) => JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);

    public static SyncVaultFile? Parse(byte[] content)
    {
        try
        {
            var file = JsonSerializer.Deserialize<SyncVaultFile>(content);
            return file is { Format: SyncVaultFile.FormatName, Version: >= 1 and <= SyncVaultFile.CurrentVersion } && file.VaultId.Length > 0 && file.WrappedKey.Length > 0
                ? file
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>一条行变更。Key 为同步键（uid 或自然键的 JSON 数组文本），At / Dev 为版本（毫秒时间戳 + 作者设备）。</summary>
public sealed class SyncChange
{
    [JsonPropertyName("t")] public string Table { get; init; } = string.Empty;
    /// <summary>upsert | delete</summary>
    [JsonPropertyName("op")] public string Op { get; init; } = "upsert";
    [JsonPropertyName("k")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("at")] public long At { get; init; }
    [JsonPropertyName("dev")] public string Device { get; init; } = string.Empty;
    /// <summary>写出时为 long / double / string / null；读回时各值为 <see cref="JsonElement"/>。</summary>
    [JsonPropertyName("row")] public Dictionary<string, object?>? Row { get; init; }
}

/// <summary>
/// 一个同步段：某台设备按序号写出的一批变更（changes），或它眼中的全量状态（snapshot，用于首次上传与压缩）。
/// 存储格式：JSON → gzip → ChunkedEncryption（主密钥）。
/// </summary>
public sealed class SyncSegment
{
    public const int CurrentVersion = 1;
    private const long MaxPlainBytes = 512L * 1024 * 1024;

    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;
    [JsonPropertyName("device")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("seq")] public long Seq { get; init; }
    /// <summary>changes | snapshot</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "changes";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
    [JsonPropertyName("changes")] public List<SyncChange> Changes { get; init; } = [];

    public static string ObjectKey(string deviceId, long seq) => $"devices/{deviceId}/seg-{seq:D12}.ssec";

    public static string InfoKey(string deviceId) => $"devices/{deviceId}/info.ssec";

    /// <summary>从对象名解析 (设备, 序号)；不是段文件时返回 null。</summary>
    public static (string DeviceId, long Seq)? ParseObjectKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 3 || parts[0] != "devices" || !parts[2].StartsWith("seg-", StringComparison.Ordinal) || !parts[2].EndsWith(".ssec", StringComparison.Ordinal))
            return null;
        return long.TryParse(parts[2].AsSpan(4, parts[2].Length - 9), out var seq) && seq > 0 ? (parts[1], seq) : null;
    }

    public static async Task<byte[]> EncodeAsync<T>(T value, ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default)
    {
        using var plain = new MemoryStream();
        await using (var gzip = new GZipStream(plain, CompressionLevel.Optimal, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        plain.Position = 0;
        using var encrypted = new MemoryStream();
        await ChunkedEncryption.EncryptAsync(plain, encrypted, new EncryptionKeySource.RawKey(masterKey), cancellationToken).ConfigureAwait(false);
        return encrypted.ToArray();
    }

    /// <summary>解密并解压；密钥不符或被篡改时抛出 <see cref="CryptographicException"/>，解压后超过上限时抛出 <see cref="InvalidDataException"/>。</summary>
    public static async Task<T?> DecodeAsync<T>(byte[] content, ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default)
    {
        using var compressed = new MemoryStream();
        using (var input = new MemoryStream(content, writable: false))
        {
            await ChunkedEncryption.DecryptAsync(input, compressed, null, masterKey, cancellationToken).ConfigureAwait(false);
        }
        compressed.Position = 0;
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        await using var limited = new LimitedReadStream(gzip, MaxPlainBytes);
        return await JsonSerializer.DeserializeAsync<T>(limited, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>防压缩炸弹：读超过上限即失败。</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Count(int read)
        {
            _read += read;
            if (_read > limit) throw new InvalidDataException("The sync segment is too large.");
            return read;
        }
    }
}

/// <summary>devices/{id}/info.ssec：设备名、版本与最新序号（加密，远端看不到设备名）。</summary>
public sealed class SyncDeviceInfo
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; init; }
    [JsonPropertyName("lastSeq")] public long LastSeq { get; init; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; init; }
}
