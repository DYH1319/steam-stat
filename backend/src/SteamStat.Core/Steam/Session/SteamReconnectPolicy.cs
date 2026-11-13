using SteamKit2;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Steam.Session;

internal sealed class SteamReconnectPolicy(
    SteamResultClassifier classifier,
    int maxAttempts = 10,
    TimeSpan? baseDelay = null,
    TimeSpan? maximumDelay = null,
    TimeSpan? rateLimitedDelay = null,
    TimeSpan? stableAfter = null,
    int maxQuickDrops = 4,
    int logOnRetries = 2,
    TimeSpan? logOnRetryDelay = null)
{
    private readonly TimeSpan _baseDelay = baseDelay ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maximumDelay = maximumDelay ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _rateLimitedDelay = rateLimitedDelay ?? TimeSpan.FromMinutes(2);
    private readonly TimeSpan _logOnRetryDelay = logOnRetryDelay ?? TimeSpan.FromSeconds(3);

    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>
    /// 会话保持这么久才算稳定，退避计数在这之后才清零。
    /// 登录成功就清零的话，「登录成功、几秒后被断开」会以最短间隔一直循环下去
    /// （同一个账号在另一处用同样的方式登录时，两边互相把对方挤掉，15 分钟里能向 Steam 登录 60 次）。
    /// </summary>
    public TimeSpan StableAfter { get; } = stableAfter ?? TimeSpan.FromSeconds(60);

    /// <summary>连续这么多次「登录成功后没撑到稳定就断开」就停止自动重连，交给用户处理。</summary>
    public int MaxQuickDrops { get; } = maxQuickDrops;

    public bool IsStable(TimeSpan uptime) => uptime >= StableAfter;

    public bool IsTerminal(EResult result)
        => classifier.Classify(result, SteamResultContext.Authentication).Kind == SteamFailureKind.AuthenticationRequired;

    public bool CanRetry(int attemptsConsumed) => attemptsConsumed < MaxAttempts;

    /// <summary>
    /// 首次登录遇到 Steam 侧的临时错误（AM 不可用、CM 忙）时，在返回失败之前再试几次。
    /// 这时没有会话可以交给后台重连，不重试的话用户只能自己再点一次。
    /// </summary>
    public int LogOnRetries { get; } = logOnRetries;

    public bool IsTransient(EResult result)
        => classifier.Classify(result, SteamResultContext.Authentication).Kind == SteamFailureKind.Transient;

    /// <summary>第 n 次重试前等多久：3 秒、6 秒，用户在等，不按后台重连那样指数退避</summary>
    public TimeSpan GetLogOnRetryDelay(int retry) => TimeSpan.FromTicks(_logOnRetryDelay.Ticks * retry);

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
