using SteamStat.Core.Platform;

namespace SteamStat.Core.Features.Login;

/// <summary>Narrow persistence boundary for remembered Steam login credentials.</summary>
public interface ISteamLoginTokenStore
{
    SteamLoginTokenData? FindByAccountName(string accountName);
    Task<SteamLoginTokenData?> FindByIdAsync(int id, CancellationToken cancellationToken = default);
    IReadOnlyList<SteamLoginTokenData> List();
    Task UpsertAsync(SteamLoginTokenWrite token, CancellationToken cancellationToken = default);
    Task<int> EncryptLegacyAsync(ISecretStore secretStore, CancellationToken cancellationToken = default);
    Task<SteamLoginTokenData?> DeleteAsync(int id, CancellationToken cancellationToken = default);
}

public sealed record SteamLoginTokenData(
    int Id,
    string AccountName,
    string AccessToken,
    string RefreshToken,
    string? GuardData,
    int CreatedAt);

public sealed record SteamLoginTokenWrite(
    string AccountName,
    string AccessToken,
    string RefreshToken,
    string? GuardData,
    int CreatedAt);
