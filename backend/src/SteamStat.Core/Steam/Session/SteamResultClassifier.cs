using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using SteamKit2;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Steam.Session;

internal enum SteamResultContext
{
    Resource,
    Authentication
}

internal sealed class SteamResultClassifier
{
    public SteamFailure Classify(EResult result, SteamResultContext context = SteamResultContext.Resource)
    {
        if (context == SteamResultContext.Authentication && result is
            EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked
            or EResult.InvalidSignature or EResult.AccountDisabled or EResult.AccountLockedDown
            or EResult.AccountLogonDenied or EResult.AccountLoginDeniedNeedTwoFactor
            or EResult.Banned or EResult.AccountNotFound)
            return new SteamFailure(SteamFailureKind.AuthenticationRequired, "steam_authentication_required");
        return result switch
        {
            EResult.InvalidPassword or EResult.Expired or EResult.Revoked or EResult.InvalidSignature
                or EResult.AccountDisabled or EResult.AccountLockedDown or EResult.AccountLogonDenied
                or EResult.AccountLoginDeniedNeedTwoFactor or EResult.Banned
                => new(SteamFailureKind.AuthenticationRequired, "steam_authentication_required"),
            EResult.AccessDenied => new(SteamFailureKind.Forbidden, "steam_access_denied"),
            EResult.AccountNotFound => new(SteamFailureKind.NotFound, "steam_not_found"),
            EResult.RateLimitExceeded => new(SteamFailureKind.RateLimited, "steam_rate_limited"),
            EResult.Busy or EResult.ServiceUnavailable or EResult.TryAnotherCM
                => new(SteamFailureKind.Transient, "steam_transient"),
            EResult.Timeout => new(SteamFailureKind.Timeout, "steam_timeout"),
            EResult.InvalidProtocolVer => new(SteamFailureKind.Protocol, "steam_protocol_error"),
            _ => new(SteamFailureKind.Unknown, "steam_unknown")
        };
    }

    public SteamFailure Classify(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        if (statusCode == HttpStatusCode.RequestTimeout) return new(SteamFailureKind.Timeout, "http_timeout");
        if (statusCode == HttpStatusCode.Unauthorized) return new(SteamFailureKind.AuthenticationRequired, "http_authentication_required");
        if (statusCode == HttpStatusCode.Forbidden) return new(SteamFailureKind.Forbidden, "http_forbidden");
        if (statusCode == HttpStatusCode.NotFound) return new(SteamFailureKind.NotFound, "http_not_found");
        if (status == 429) return new(SteamFailureKind.RateLimited, "http_rate_limited");
        if (status >= 500) return new(SteamFailureKind.Transient, "http_server_error");
        if (status >= 400) return new(SteamFailureKind.Protocol, "http_client_error");
        return new(SteamFailureKind.Unknown, "http_unexpected_status");
    }

    public SteamFailure Classify(Exception exception, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            callerToken.ThrowIfCancellationRequested();
            return new SteamFailure(SteamFailureKind.Timeout, "operation_timeout");
        }
        if (exception is TimeoutException) return new SteamFailure(SteamFailureKind.Timeout, "operation_timeout");
        if (exception is JsonException or InvalidDataException or FormatException)
            return new SteamFailure(SteamFailureKind.InvalidData, "invalid_payload");
        if (Find<SocketException>(exception) != null)
            return new SteamFailure(SteamFailureKind.Offline, "http_connection_error");
        if (Find<AuthenticationException>(exception) != null)
            return new SteamFailure(SteamFailureKind.Protocol, "tls_error");
        if (exception is HttpRequestException httpException)
        {
            if (httpException.StatusCode is { } statusCode) return Classify(statusCode);
            return httpException.HttpRequestError.ToString() switch
            {
                "NameResolutionError" => new(SteamFailureKind.Offline, "dns_error"),
                "ConnectionError" or "ProxyTunnelError" => new(SteamFailureKind.Offline, "http_connection_error"),
                "SecureConnectionError" => new(SteamFailureKind.Protocol, "tls_error"),
                "UserAuthenticationError" => new(SteamFailureKind.AuthenticationRequired, "http_authentication_required"),
                "ResponseEnded" => new(SteamFailureKind.Transient, "http_response_ended"),
                _ => new(SteamFailureKind.Transient, "http_request_error")
            };
        }

        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        if (typeName.EndsWith("RateLimiterRejectedException", StringComparison.Ordinal)
            || typeName.EndsWith("CmSchedulerRejectedException", StringComparison.Ordinal))
            return new SteamFailure(SteamFailureKind.RateLimited, "rate_limiter_rejected");
        if (typeName.EndsWith("BrokenCircuitException", StringComparison.Ordinal))
            return new SteamFailure(SteamFailureKind.Transient, "circuit_open");
        if (typeName.EndsWith("TimeoutRejectedException", StringComparison.Ordinal))
            return new SteamFailure(SteamFailureKind.Timeout, "resilience_timeout");
        return new SteamFailure(SteamFailureKind.Unknown, "unknown_error");
    }

    private static TException? Find<TException>(Exception exception) where TException : Exception
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is TException match) return match;
        return null;
    }
}
