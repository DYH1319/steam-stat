using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Library.Contracts;

/// <summary>
/// 账号自有许可证（package）：创建时间即入库时间，AppIds 为该 package 授予的应用。
/// </summary>
public sealed record SteamLicensePackage(
    uint PackageId,
    DateTimeOffset TimeCreated,
    IReadOnlyList<uint> AppIds);

/// <summary>
/// 读取登录会话收到的许可证列表，并经 PICS 解析 package 包含的应用。
/// 已解析过的 package 由调用方通过 knownPackageIds 跳过，避免重复请求。
/// </summary>
public interface ISteamLicenseGateway
{
    Task<SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>> GetLicensedPackagesAsync(
        string accountName,
        IReadOnlySet<uint> knownPackageIds,
        CancellationToken cancellationToken = default);
}
