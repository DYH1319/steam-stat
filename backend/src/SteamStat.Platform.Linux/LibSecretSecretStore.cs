using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Linux;

/// <summary>保存凭证主密钥的系统密钥环（生产实现为 libsecret / Secret Service）。</summary>
internal interface ICredentialKeyring
{
    /// <summary>读取主密钥；不存在时返回 null。密钥环不可用时抛出 <see cref="KeyringUnavailableException"/>。</summary>
    byte[]? Load();

    void Store(byte[] key);
}

internal sealed class KeyringUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Linux 凭证保护：随机 256 位主密钥存放在 Secret Service（GNOME Keyring / KWallet / KeePassXC 等），
/// 每个凭证用 AES-256-GCM 加密后存数据库。效果与 Windows DPAPI 相同：数据库文件被拷走也无法解密。
/// 密钥环不可用时退回进程内随机密钥（前缀 session:v1:），<see cref="IsProtected"/> 对其返回 false，
/// 因此凭证只在本次运行有效、不会写入数据库，也绝不会以明文落盘。
/// </summary>
internal sealed class LibSecretSecretStore(
    ICredentialKeyring keyring,
    ILogger<LibSecretSecretStore> logger) : ISecretStore
{
    private const string EncryptionPrefix = "libsecret:v1:";
    private const string SessionPrefix = "session:v1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = "steam-stat.login-token.v1"u8.ToArray();

    private readonly object _sync = new();
    private readonly byte[] _sessionKey = RandomNumberGenerator.GetBytes(KeySize);
    private byte[]? _key;
    private bool _unavailableLogged;

    public bool CanPersist => GetKey(create: false) != null || KeyringReachable();

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || IsProtected(plainText) || plainText.StartsWith(SessionPrefix, StringComparison.Ordinal))
            return plainText;
        var key = GetKey(create: true);
        return key == null
            ? Encrypt(plainText, _sessionKey, SessionPrefix)
            : Encrypt(plainText, key, EncryptionPrefix);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return protectedText;
        if (protectedText.StartsWith(SessionPrefix, StringComparison.Ordinal))
            return Decrypt(protectedText[SessionPrefix.Length..], _sessionKey);
        if (!IsProtected(protectedText)) return protectedText;
        var key = GetKey(create: false);
        return key == null ? null : Decrypt(protectedText[EncryptionPrefix.Length..], key);
    }

    public bool IsProtected(string? value)
        => value != null && value.StartsWith(EncryptionPrefix, StringComparison.Ordinal);

    private string? Encrypt(string plainText, byte[] key, string prefix)
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes(plainText);
            var payload = new byte[NonceSize + plain.Length + TagSize];
            var nonce = payload.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, payload.AsSpan(NonceSize, plain.Length), payload.AsSpan(NonceSize + plain.Length), AssociatedData);
            return prefix + Convert.ToBase64String(payload);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to protect a Steam login secret");
            return null;
        }
    }

    private string? Decrypt(string encoded, byte[] key)
    {
        try
        {
            var payload = Convert.FromBase64String(encoded);
            if (payload.Length < NonceSize + TagSize) return null;
            var cipherLength = payload.Length - NonceSize - TagSize;
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize, cipherLength), payload.AsSpan(NonceSize + cipherLength), plain, AssociatedData);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to unprotect a Steam login secret");
            return null;
        }
    }

    private bool KeyringReachable()
    {
        try
        {
            keyring.Load();
            return true;
        }
        catch (KeyringUnavailableException)
        {
            return false;
        }
    }

    private byte[]? GetKey(bool create)
    {
        lock (_sync)
        {
            if (_key != null) return _key;
            try
            {
                var key = keyring.Load();
                if (key is { Length: KeySize })
                {
                    _key = key;
                    return _key;
                }
                if (!create) return null;

                key = RandomNumberGenerator.GetBytes(KeySize);
                keyring.Store(key);
                _key = key;
                logger.LogInformation("Created the Steam Stat credential key in the system keyring");
                return _key;
            }
            catch (KeyringUnavailableException exception)
            {
                if (!_unavailableLogged)
                {
                    logger.LogWarning(exception, "The system keyring (Secret Service) is unavailable; saved Steam logins will not persist");
                    _unavailableLogged = true;
                }
                return null;
            }
        }
    }
}
