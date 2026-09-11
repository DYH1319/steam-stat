using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Sessions;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamProfileSource
{
    Task<SteamGatewayResult<SteamProfileSnapshot>> GetAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken);
}

internal sealed class CmProfileSource(
    ISteamSessionAccessor sessionAccessor,
    ISteamCmOperationScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<CmProfileSource> logger) : ISteamProfileSource
{
    public async Task<SteamGatewayResult<SteamProfileSnapshot>> GetAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamProfileSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, "profile_session_unavailable");
        var client = session.Client;
        var friends = client.GetHandler<SteamFriends>();
        if (friends == null)
            return SteamGatewayResult<SteamProfileSnapshot>.Failed(
                SteamFailureKind.Protocol, "profile_persona_unavailable");
        var id = new SteamID(steamId);
        var currentUser = client.SteamID?.ConvertToUInt64() == steamId;
        var personaName = currentUser ? friends.GetPersonaName() : friends.GetFriendPersonaName(id);
        var avatarHash = GetAvatarHash(friends, id);
        var level = await TryGetLevelAsync(accountName, session, id.AccountID, cancellationToken)
            .ConfigureAwait(false);
        var fetchedAt = timeProvider.GetUtcNow();
        return SteamGatewayResult<SteamProfileSnapshot>.Succeeded(
            new SteamProfileSnapshot(steamId, NormalizePersonaName(personaName), level, avatarHash),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            fetchedAt + TimeSpan.FromHours(6));
    }

    private async Task<int?> TryGetLevelAsync(
        string accountName,
        ISteamSession session,
        uint accountId,
        CancellationToken cancellationToken)
    {
        var levels = session.Client.GetHandler<SteamLevelsHandler>();
        if (levels == null) return null;
        try
        {
            return await scheduler.RunAsync(
                accountName,
                "profile-level",
                session.Generation,
                async stoppingToken =>
                {
                    var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var subscription = session.Callbacks.Subscribe<FriendsSteamLevelsCallback>(callback =>
                    {
                        if (callback.Levels.TryGetValue(accountId, out var value)) completion.TrySetResult(value);
                    });
                    levels.RequestFriendLevels([accountId]);
                    return await completion.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch Steam profile level for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            return null;
        }
    }

    private static string? GetAvatarHash(SteamFriends friends, SteamID id)
    {
        try
        {
            var hash = friends.GetFriendAvatar(id);
            return hash is { Length: > 0 }
                ? Convert.ToHexStringLower(hash)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePersonaName(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "[unknown]" ? null : value;
}
