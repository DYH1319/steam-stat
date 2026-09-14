using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Features.Login;

public sealed class SteamCredentialStore(
    ISteamLoginTokenStore tokenStore,
    ISecretStore secretStore,
    TimeProvider timeProvider,
    ILogger<SteamCredentialStore> logger)
{
    private readonly ConcurrentDictionary<string, ProtectedCredential> _sessionCredentials = new();

    internal SteamCredentialMaterial? FindByAccountName(string accountName)
    {
        if (_sessionCredentials.TryGetValue(accountName, out var session))
            return Unprotect(accountName, session.RefreshToken, session.GuardData);
        var saved = tokenStore.FindByAccountName(accountName);
        return saved == null ? null : Unprotect(saved.AccountName, saved.RefreshToken, saved.GuardData);
    }

    internal async Task<SteamCredentialLookup> LoadByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var saved = await tokenStore.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return saved == null
            ? new SteamCredentialLookup(false, null)
            : new SteamCredentialLookup(true, Unprotect(saved.AccountName, saved.RefreshToken, saved.GuardData));
    }

    internal string? LoadGuardData(string accountName)
        => FindByAccountName(accountName)?.GuardData;

    internal void RememberForSession(string accountName, string refreshToken, string? guardData)
    {
        var protectedToken = secretStore.Protect(refreshToken);
        if (string.IsNullOrEmpty(protectedToken)) throw new InvalidOperationException("Steam refresh token could not be protected.");
        _sessionCredentials[accountName] = new ProtectedCredential(protectedToken, secretStore.Protect(guardData));
    }

    internal async Task SaveAsync(SteamAuthenticationResult result, CancellationToken cancellationToken = default)
    {
        try
        {
            await tokenStore.UpsertAsync(new SteamLoginTokenWrite(
                result.AccountName,
                secretStore.Protect(result.AccessToken) ?? string.Empty,
                secretStore.Protect(result.RefreshToken) ?? string.Empty,
                secretStore.Protect(result.GuardData),
                (int)timeProvider.GetUtcNow().ToUnixTimeSeconds()), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Saved Steam login token for {AccountName}", result.AccountName);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save Steam login token");
        }
    }

    public IReadOnlyList<SteamLoginTokenSummary> List()
        => tokenStore.List().Select(token => new SteamLoginTokenSummary(
            token.Id, token.AccountName, token.CreatedAt, GetJwtExpiry(secretStore.Unprotect(token.RefreshToken)))).ToList();

    public async Task EncryptLegacyAsync(CancellationToken cancellationToken = default)
    {
        var upgraded = await tokenStore.EncryptLegacyAsync(secretStore, cancellationToken).ConfigureAwait(false);
        if (upgraded > 0) logger.LogInformation("Encrypted {Count} legacy plaintext Steam token(s)", upgraded);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var token = await tokenStore.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (token == null) return false;
        _sessionCredentials.TryRemove(token.AccountName, out _);
        logger.LogInformation("Deleted saved Steam token for {AccountName}", token.AccountName);
        return true;
    }

    internal void ForgetSession(string accountName) => _sessionCredentials.TryRemove(accountName, out _);

    private SteamCredentialMaterial? Unprotect(string accountName, string protectedToken, string? protectedGuardData)
    {
        var refreshToken = secretStore.Unprotect(protectedToken);
        return string.IsNullOrEmpty(refreshToken)
            ? null
            : new SteamCredentialMaterial(accountName, refreshToken, secretStore.Unprotect(protectedGuardData));
    }

    private static long? GetJwtExpiry(string? jwt)
    {
        try
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var expiry) ? expiry.GetInt64() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ProtectedCredential(string RefreshToken, string? GuardData);
}

internal sealed record SteamCredentialMaterial(string AccountName, string RefreshToken, string? GuardData);
internal sealed record SteamCredentialLookup(bool Found, SteamCredentialMaterial? Credential);
