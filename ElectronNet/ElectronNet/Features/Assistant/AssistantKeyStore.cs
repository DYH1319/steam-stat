using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.Platform;

namespace ElectronNet.Features.Assistant;

/// <summary>
/// 模型服务的 Key，按服务商各存一份，写盘前一律经 <see cref="ISecretStore"/> 保护（Windows DPAPI / Linux 密钥环）。
/// 不能安全保存时只留在内存里，本次运行有效。不在设置文件里，所以不进备份和同步，也不会回传给渲染层。
/// </summary>
internal sealed class AssistantKeyStore(IAppPaths appPaths, ISecretStore secretStore, ILogger<AssistantKeyStore> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);

    private string File => Path.Combine(appPaths.UserDataDirectory, "assistant-keys.json");

    public bool CanPersist => secretStore.CanPersist;

    public string? Get(string provider)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(provider, out var cached)) return cached;
            var stored = Read().GetValueOrDefault(provider);
            var key = string.IsNullOrEmpty(stored) ? null : secretStore.Unprotect(stored);
            if (key != null) _memory[provider] = key;
            return key;
        }
    }

    public bool Has(string provider) => !string.IsNullOrEmpty(Get(provider));

    public void Set(string provider, string? key)
    {
        lock (_lock)
        {
            var all = Read();
            if (string.IsNullOrWhiteSpace(key))
            {
                _memory.Remove(provider);
                all.Remove(provider);
            }
            else
            {
                _memory[provider] = key.Trim();
                if (secretStore.CanPersist && secretStore.Protect(key.Trim()) is { } protectedKey) all[provider] = protectedKey;
                else all.Remove(provider);
            }
            Write(all);
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to read the stored assistant keys");
            return new(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, string> all)
    {
        try
        {
            if (all.Count == 0)
            {
                if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
                return;
            }
            Directory.CreateDirectory(appPaths.UserDataDirectory);
            var temporary = File + ".tmp";
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(all));
            LocalFiles.RestrictToCurrentUser(temporary);
            System.IO.File.Move(temporary, File, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to save the assistant keys");
        }
    }
}

internal static class LocalFiles
{
    /// <summary>Linux 上把文件权限收紧到 600。Windows 的用户目录本来就只有本人能读。</summary>
    public static void RestrictToCurrentUser(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 权限收不紧不影响功能；文件仍在用户自己的数据目录里
        }
    }
}
