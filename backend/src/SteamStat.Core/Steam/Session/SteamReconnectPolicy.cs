using SteamKit2;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Steam.Session;

internal sealed class SteamReconnectPolicy(
    SteamResultClassifier classifier,
    int maxAttempts = 10,
    TimeSpan? baseDelay = null,
    TimeSpan? maximumDelay = null,
    TimeSpan? rateLimitedDelay = null)
{
    private readonly TimeSpan _baseDelay = baseDelay ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maximumDelay = maximumDelay ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _rateLimitedDelay = rateLimitedDelay ?? TimeSpan.FromMinutes(2);

    public int MaxAttempts { get; } = maxAttempts;

    public bool IsTerminal(EResult result)
        => classifier.Classify(result, SteamResultContext.Authentication).Kind == SteamFailureKind.AuthenticationRequired;

    public bool CanRetry(int attemptsConsumed) => attemptsConsumed < MaxAttempts;

    public TimeSpan GetDelay(int nextAttempt, EResult? previousResult, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nextAttempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0.8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1.2);
        var delay = previousResult == EResult.RateLimitExceeded
            ? _rateLimitedDelay
            : TimeSpan.FromTicks(Math.Min(
                _baseDelay.Ticks * (long)Math.Pow(2, Math.Min(nextAttempt - 1, 30)),
                _maximumDelay.Ticks));
        return TimeSpan.FromTicks((long)(delay.Ticks * jitter));
    }
}
