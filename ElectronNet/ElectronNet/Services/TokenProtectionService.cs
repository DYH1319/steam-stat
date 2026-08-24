using System.Security.Cryptography;
using System.Text;
using ElectronNet.Constants;

namespace ElectronNet.Services;

/// <summary>
/// 登录凭证（Token / GuardData）静态加密服务
/// 使用 Windows DPAPI（CurrentUser 作用域）加密后再持久化到数据库，
/// 确保数据库文件被拷贝到其他机器 / 其他用户后无法解出凭证。
/// 兼容历史明文数据：解密时遇到无前缀的值将原样返回。
/// </summary>
public static class TokenProtectionService
{
    // 加密后字符串的前缀标识（含版本号，便于将来升级加密方案）
    private const string ENCRYPTION_PREFIX = "dpapi:v1:";

    // 附加熵，防止其他 DPAPI 使用者直接解密本应用的数据
    private static readonly byte[] _entropy = "steam-stat.login-token.v1"u8.ToArray();

    /// <summary>
    /// 加密字符串。null 或空字符串原样返回。
    /// </summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || IsProtected(plainText))
        {
            return plainText;
        }

        try
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), _entropy, DataProtectionScope.CurrentUser);
            return ENCRYPTION_PREFIX + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} TokenProtection Protect failed: {ex.Message}");
            return plainText;
        }
    }

    /// <summary>
    /// 解密字符串。无加密前缀的值（历史明文数据）原样返回。
    /// </summary>
    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !IsProtected(protectedText))
        {
            return protectedText;
        }

        try
        {
            var encrypted = Convert.FromBase64String(protectedText[ENCRYPTION_PREFIX.Length..]);
            var decrypted = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} TokenProtection Unprotect failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 判断字符串是否已被本服务加密
    /// </summary>
    public static bool IsProtected(string? value)
    {
        return value != null && value.StartsWith(ENCRYPTION_PREFIX, StringComparison.Ordinal);
    }
}
