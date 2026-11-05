using System.Text.RegularExpressions;

namespace SteamStat.Core.Sync;

/// <summary>远端存储中的一个对象。Key 使用「/」分隔，不以「/」开头，相对于同步根目录。</summary>
public sealed record SyncObjectInfo(string Key, long Size);

public enum SyncStorageErrorKind
{
    /// <summary>认证失败或没有权限。</summary>
    Unauthorized,
    /// <summary>网络不可达、超时或服务端 5xx。</summary>
    Network,
    /// <summary>配置无效（地址、桶、目录不存在等）。</summary>
    Configuration,
    /// <summary>本机与服务端时间相差过大（S3 签名要求）。</summary>
    ClockSkew,
    /// <summary>服务端返回了无法理解的响应。</summary>
    Protocol
}

public sealed class SyncStorageException(SyncStorageErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public SyncStorageErrorKind Kind { get; } = kind;
}

/// <summary>
/// 同步后端只需要「按前缀列出 / 读 / 写 / 删」四个操作；每台设备只写自己的目录，不需要锁或条件写入。
/// 内容在交给后端之前已经端到端加密，后端看到的只有密文和对象名。
/// </summary>
public interface ISyncStorage
{
    /// <summary>递归列出前缀下的所有对象；前缀不存在时返回空列表。</summary>
    Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>读取对象；不存在时返回 null。</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>写入（覆盖）对象，必要时创建上级目录。</summary>
    Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>删除对象；不存在时不报错。</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public static partial class SyncKeys
{
    /// <summary>对象名只允许 [A-Za-z0-9._-] 组成的段，用「/」连接；不允许空段、「.」与「..」。</summary>
    public static bool IsValid(string key)
        => key.Length is > 0 and <= 512 && KeyPattern().IsMatch(key) && key.Split('/').All(segment => segment is not ("." or ".."));

    public static void Validate(string key)
    {
        if (!IsValid(key)) throw new ArgumentException($"Invalid sync object key: {key}", nameof(key));
    }

    /// <summary>前缀可以为空，或以「/」结尾的合法路径。</summary>
    public static void ValidatePrefix(string prefix)
    {
        if (prefix.Length == 0) return;
        if (!prefix.EndsWith('/') || !IsValid(prefix.TrimEnd('/'))) throw new ArgumentException($"Invalid sync prefix: {prefix}", nameof(prefix));
    }

    /// <summary>远端根目录（用户可配置）：去掉首尾「/」后每段必须合法；空表示存储根。</summary>
    public static string NormalizeRoot(string? root)
    {
        var trimmed = (root ?? string.Empty).Trim().Trim('/');
        if (trimmed.Length == 0) return string.Empty;
        if (!IsValid(trimmed)) throw new ArgumentException("The remote folder may only contain letters, digits, '.', '_', '-' and '/'.", nameof(root));
        return trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
