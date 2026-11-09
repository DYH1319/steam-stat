using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ElectronNet.Features.DataTransfer;

/// <summary>
/// 手动备份包（.ssbackup）：一个 zip，可以整体用口令加密（见 <see cref="SteamStat.Core.Security.ChunkedEncryption"/>）。
/// <list type="bullet">
/// <item>manifest.json：格式版本、来源版本 / 设备、迁移版本、数据量统计、每个文件的大小与 SHA-256</item>
/// <item>database.db：SQLite 在线备份的副本，已删除 Steam 凭证与资源缓存并 VACUUM</item>
/// <item>settings.json / renderer-state.json / badge-names.json：应用设置、渲染层偏好、徽章名缓存（可选）</item>
/// <item>save-backups/objects/{xx}/{sha256}：存档备份库的内容对象（可选）</item>
/// <item>web-archive/… 与 store-covers/…：抓取的原始网页（令牌已抹掉）和商店封面。下架之后这些只有这里有</item>
/// </list>
/// </summary>
internal static partial class BackupFormat
{
    internal const string Format = "steam-stat-backup";
    internal const int Version = 1;
    internal const string Extension = "ssbackup";

    internal const string ManifestEntry = "manifest.json";
    internal const string DatabaseEntry = "database.db";
    internal const string SettingsEntry = "settings.json";
    internal const string RendererStateEntry = "renderer-state.json";
    internal const string BadgeNamesEntry = "badge-names.json";
    internal const string SaveObjectsPrefix = "save-backups/objects/";
    internal const string WebArchivePrefix = "web-archive/";
    internal const string StoreCoversPrefix = "store-covers/";

    /// <summary>备份副本中清空的表：凭证永远不离开本机，资源缓存可以重新获取。</summary>
    internal static readonly IReadOnlyList<string> StrippedTables = ["steam_login_token", "steam_resource_cache"];

    private const long MaxManifestBytes = 4 * 1024 * 1024;

    internal static bool IsAllowedEntry(string name)
        => name is DatabaseEntry or SettingsEntry or RendererStateEntry or BadgeNamesEntry
           || SaveObjectPattern().IsMatch(name) || ArchiveFilePattern().IsMatch(name);

    internal static bool IsArchiveFile(string name) => ArchiveFilePattern().IsMatch(name);

    internal static bool IsSaveObject(string name) => SaveObjectPattern().IsMatch(name);

    [GeneratedRegex("^save-backups/objects/[0-9a-f]{2}/[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SaveObjectPattern();

    [GeneratedRegex(@"^(?:web-archive/(?:billing|reviews|guides|favorites|comments)/\d{17}/\d{8}-\d{6}-\d{4}\.gz|store-covers/\d+-[0-9a-f]{16}\.jpg)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveFilePattern();

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>读取并校验 manifest；格式不对时返回 null。</summary>
    internal static BackupManifest? ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry);
        if (entry == null || entry.Length > MaxManifestBytes) return null;
        try
        {
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions);
            if (manifest is not { Format: Format } || manifest.Version is < 1 or > Version) return null;
            if (manifest.Files.Count == 0 || manifest.Files.All(file => file.Name != DatabaseEntry)) return null;
            if (manifest.Files.Any(file => !IsAllowedEntry(file.Name) || file.Size < 0 || file.Sha256.Length != 64)) return null;
            if (manifest.Files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count) return null;
            return manifest;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>写入 zip 时同时计算 SHA-256 与大小。</summary>
    internal static async Task<BackupFileEntry> AddEntryAsync(ZipArchive zip, string name, Stream source, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, name.StartsWith(SaveObjectsPrefix, StringComparison.Ordinal) ? CompressionLevel.Fastest : CompressionLevel.Optimal);
        await using var target = entry.Open();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            size += read;
        }
        return new BackupFileEntry { Name = name, Size = size, Sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset()) };
    }

    /// <summary>解出一个条目并核对大小与 SHA-256；不一致时删除输出文件并返回 false。</summary>
    internal static async Task<bool> ExtractVerifiedAsync(ZipArchive zip, BackupFileEntry file, string destination, CancellationToken cancellationToken)
    {
        var entry = zip.GetEntry(file.Name);
        if (entry == null || entry.Length != file.Size) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string actual;
        long size = 0;
        await using (var source = entry.Open())
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                size += read;
                if (size > file.Size) break;
                hasher.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        if (size == file.Size && string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        File.Delete(destination);
        return false;
    }
}

internal sealed class BackupManifest
{
    [JsonPropertyName("format")] public string Format { get; init; } = BackupFormat.Format;
    [JsonPropertyName("version")] public int Version { get; init; } = BackupFormat.Version;
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "0.0.0";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
    [JsonPropertyName("migration")] public string? Migration { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }
    [JsonPropertyName("counts")] public Dictionary<string, long> Counts { get; init; } = [];
    [JsonPropertyName("files")] public List<BackupFileEntry> Files { get; init; } = [];
}

internal sealed class BackupFileEntry
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
}

/// <summary>Restore/pending.json：下次启动时由 <see cref="Persistence.PendingRestoreApplier"/> 应用。</summary>
internal sealed class PendingRestore
{
    [JsonPropertyName("stagedAt")] public long StagedAt { get; init; }
    [JsonPropertyName("sourceName")] public string SourceName { get; init; } = string.Empty;
    [JsonPropertyName("backupCreatedAt")] public long BackupCreatedAt { get; init; }
    [JsonPropertyName("backupAppVersion")] public string BackupAppVersion { get; init; } = string.Empty;
}

/// <summary>Restore/last-backup.json：最近一次成功的手动备份，供档案体检判断「多久没备份」。</summary>
internal sealed class LastBackupRecord
{
    [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
}

/// <summary>Restore/last-restore.json：最近一次恢复的结果，供界面提示。</summary>
internal sealed class RestoreOutcome
{
    [JsonPropertyName("appliedAt")] public long AppliedAt { get; init; }
    [JsonPropertyName("sourceName")] public string SourceName { get; init; } = string.Empty;
    [JsonPropertyName("backupCreatedAt")] public long BackupCreatedAt { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("preRestoreSnapshot")] public string? PreRestoreSnapshot { get; init; }
}
