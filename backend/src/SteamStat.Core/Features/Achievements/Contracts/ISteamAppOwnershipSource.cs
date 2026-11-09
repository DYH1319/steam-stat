namespace SteamStat.Core.Features.Achievements.Contracts;

/// <summary>
/// 账号拥有的应用（含 DLC）。IsComplete 为 false 时还有许可证没解析完：
/// 不在列表里不代表没有，要回退到「解锁过该集任一成就才计入」。
/// </summary>
public sealed record SteamAppOwnership(IReadOnlySet<uint> AppIds, bool IsComplete);

/// <summary>成就集用它判断 DLC 所有权。拿不到授权数据时返回 null。</summary>
public interface ISteamAppOwnershipSource
{
    Task<SteamAppOwnership?> GetAsync(string accountName, CancellationToken cancellationToken = default);
}

public sealed class NullSteamAppOwnershipSource : ISteamAppOwnershipSource
{
    public Task<SteamAppOwnership?> GetAsync(string accountName, CancellationToken cancellationToken = default)
        => Task.FromResult<SteamAppOwnership?>(null);
}
