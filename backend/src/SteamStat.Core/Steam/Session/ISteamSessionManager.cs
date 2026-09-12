using SteamStat.Core.Sessions;

namespace SteamStat.Core.Steam.Session;

public interface ISteamGuardInteraction
{
    Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect);
    Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect);
    Task<bool> AcceptDeviceConfirmationAsync();
}

public sealed record SteamAuthenticationResult(
    string AccountName,
    string AccessToken,
    string RefreshToken,
    string? GuardData);

public sealed record SteamSessionStartResult(bool Success, string? ErrorCode = null);

public sealed record SteamSessionStatusSnapshot(
    string AccountName,
    SteamSessionState State,
    long Generation,
    int ReconnectAttempt,
    string? ErrorCode = null);

public interface ISteamSessionStatusProvider
{
    IReadOnlyList<SteamSessionStatusSnapshot> GetSessionStatuses();
}

public interface ISteamSessionManager : ISteamSessionStatusProvider, IAsyncDisposable
{
    Task<SteamAuthenticationResult> AuthenticateWithCredentialsAsync(
        string username,
        string password,
        bool persistent,
        string? guardData,
        ISteamGuardInteraction guardInteraction,
        CancellationToken cancellationToken = default);
    Task<SteamAuthenticationResult> AuthenticateWithQrAsync(
        bool persistent,
        Action<string> challengeChanged,
        CancellationToken cancellationToken = default);
    Task<SteamSessionStartResult> StartSessionAsync(
        string accountName,
        string refreshToken,
        string? guardData,
        bool rememberPassword,
        CancellationToken cancellationToken = default);
    Task CancelLoginAsync();
    IReadOnlyList<string> GetLoggedInUsers();
    Task<bool> LogoutUserAsync(string accountName);
    Task LogoutAllUsersAsync();
    bool SetUserPersonaState(string accountName, int personaState);
    Task ShutdownAsync();
}
