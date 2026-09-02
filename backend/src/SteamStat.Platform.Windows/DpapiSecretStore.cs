using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore(ILogger<DpapiSecretStore> logger) : ISecretStore
{
    private const string EncryptionPrefix = "dpapi:v1:";
    private static readonly byte[] Entropy = "steam-stat.login-token.v1"u8.ToArray();

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || IsProtected(plainText)) return plainText;
        try
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
            return EncryptionPrefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to protect a Steam login secret");
            return plainText;
        }
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !IsProtected(protectedText)) return protectedText;
        try
        {
            var encrypted = Convert.FromBase64String(protectedText[EncryptionPrefix.Length..]);
            var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to unprotect a Steam login secret");
            return null;
        }
    }

    public bool IsProtected(string? value)
        => value != null && value.StartsWith(EncryptionPrefix, StringComparison.Ordinal);
}
