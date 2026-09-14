namespace SteamStat.Core.Http;

public sealed class SteamAccessOptions
{
    public TimeSpan JsonTotalTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan JsonAttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DownloadTotalTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DownloadAttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int RetryCount { get; set; } = 2;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public int HttpConcurrencyLimit { get; set; } = 4;
    public int HttpQueueLimit { get; set; } = 32;
    public int RequestTokenLimit { get; set; } = 20;
    public int RequestTokensPerPeriod { get; set; } = 1;
    public TimeSpan RequestReplenishmentPeriod { get; set; } = TimeSpan.FromMilliseconds(1500);
    public double CircuitFailureRatio { get; set; } = 0.5;
    public int CircuitMinimumThroughput { get; set; } = 5;
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
    public int CdnConcurrencyLimit { get; set; } = 6;
    public int CdnQueueLimit { get; set; } = 32;
    public int MaximumDownloadBytes { get; set; } = 16 * 1024 * 1024;
    public int CmConcurrencyLimit { get; set; } = 2;
    public int CmQueueLimit { get; set; } = 16;
    public TimeSpan CmOperationTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CmShutdownTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan HealthEvidenceLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
