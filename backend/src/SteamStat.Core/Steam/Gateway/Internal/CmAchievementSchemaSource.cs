using Microsoft.Extensions.Logging;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface IAchievementSchemaSource
{
    Task<SteamGatewayResult<uint>> GetHashAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken);
    Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetFullAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken);
}

internal sealed class CmAchievementSchemaSource(
    ISteamSessionAccessor sessionAccessor,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmAchievementSchemaSource> logger) : IAchievementSchemaSource
{
    public Task<SteamGatewayResult<uint>> GetHashAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            appId,
            language,
            preferredAccountName,
            true,
            "achievement-schema-hash",
            body => body.schema_hash ?? 0,
            cancellationToken);

    public Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetFullAsync(
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            appId,
            language,
            preferredAccountName,
            false,
            "achievement-schema-full",
            body => AchievementProtocolMapper.MapSchema(appId, language, body),
            cancellationToken);

    private async Task<SteamGatewayResult<T>> ExecuteAsync<T>(
        uint appId,
        string language,
        string? preferredAccountName,
        bool hashOnly,
        string operation,
        Func<AchievementSchemaResponse, T> map,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetSession(preferredAccountName, out var accountName, out var session))
            return SteamGatewayResult<T>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.SchemaSessionUnavailable);
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unifiedMessages == null)
            return SteamGatewayResult<T>.Failed(
                SteamFailureKind.Protocol,
                SteamAchievementDiagnosticCodes.SchemaHandlerUnavailable);
        var generation = session.Generation;
        try
        {
            var player = unifiedMessages.CreateService<Player>();
            var request = new CPlayer_GetGameAchievements_Request
            {
                appid = appId,
                language = language
            };
            if (hashOnly) Extensible.AppendValue(request, 3, true);
            var response = await scheduler.RunAsync(
                accountName,
                operation,
                generation,
                async _ => await player.GetGameAchievements(request),
                cancellationToken).ConfigureAwait(false);
            if (response.Result != EResult.OK)
                return SteamGatewayResult<T>.Failed(classifier.Classify(response.Result));
            if (response.Body == null)
                return SteamGatewayResult<T>.Failed(
                    SteamFailureKind.InvalidData,
                    SteamAchievementDiagnosticCodes.SchemaInvalidPayload);
            T value;
            try
            {
                var body = ProtobufReshape.To<AchievementSchemaResponse>(response.Body);
                value = map(body);
            }
            catch (Exception exception) when (exception is InvalidDataException or ProtoException)
            {
                return SteamGatewayResult<T>.Failed(
                    SteamFailureKind.InvalidData,
                    SteamAchievementDiagnosticCodes.SchemaInvalidPayload);
            }
            if (!sessionAccessor.TryGetSession(accountName, out var current)
                || current.Generation != generation)
                return SteamGatewayResult<T>.Failed(
                    SteamFailureKind.Transient,
                    SteamAchievementDiagnosticCodes.SchemaStaleSessionGeneration);
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<T>.Succeeded(
                value,
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + SteamResourcePolicies.AchievementSchema.RefreshInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch achievement schema for generation {SessionGeneration} with {ExceptionType}",
                generation, exception.GetType().Name);
            return SteamGatewayResult<T>.Failed(classifier.Classify(exception, cancellationToken));
        }
    }

    private bool TryGetSession(
        string? preferredAccountName,
        out string accountName,
        out ISteamSession session)
    {
        if (!string.IsNullOrWhiteSpace(preferredAccountName)
            && sessionAccessor.TryGetSession(preferredAccountName, out session))
        {
            accountName = preferredAccountName;
            return true;
        }
        foreach (var candidate in sessionAccessor.GetLoggedInUsers())
        {
            if (!sessionAccessor.TryGetSession(candidate, out session)) continue;
            accountName = candidate;
            return true;
        }
        accountName = string.Empty;
        session = null!;
        return false;
    }
}
