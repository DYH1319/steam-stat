using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Session;
using SteamStat.Core.Steam.Session.Internal;

namespace SteamStat.Core.Steam.Gateway.Internal;

/// <summary>
/// 许可证 → 入库时间：取登录时 CM 推送的 LicenseList（自有、未过期的 package），
/// 再用 PICS 批量解析每个 package 授予的 appids。
/// </summary>
internal sealed class CmLicenseSource(
    ISteamSessionAccessor sessionAccessor,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmLicenseSource> logger) : ISteamLicenseGateway
{
    private const int BatchSize = 200;

    public async Task<SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>> GetLicensedPackagesAsync(
        string accountName,
        IReadOnlySet<uint> knownPackageIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>.Failed(
                SteamFailureKind.AuthenticationRequired, "license_session_unavailable");
        if (session is not ISteamLicenseHolder { Licenses: { } licenses })
            return SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>.Failed(
                SteamFailureKind.Transient, "license_list_unavailable");
        var apps = session.Client.GetHandler<SteamApps>();
        if (apps == null)
            return SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>.Failed(
                SteamFailureKind.Protocol, "license_handler_unavailable");

        var accountId = session.Client.SteamID?.AccountID ?? 0;
        // 只取本账号自有、未过期的 package：家庭共享借用的许可证 OwnerAccountID 不同
        var owned = licenses
            .Where(license => license.PackageID != 0
                && (accountId == 0 || license.OwnerAccountID == 0 || license.OwnerAccountID == accountId)
                && !license.LicenseFlags.HasFlag(ELicenseFlags.Expired))
            .GroupBy(license => license.PackageID)
            .Select(group => group.OrderBy(license => license.TimeCreated).First())
            .ToArray();
        var pending = owned.Where(license => !knownPackageIds.Contains(license.PackageID)).ToArray();
        var output = new List<SteamLicensePackage>(pending.Length);
        var incomplete = false;
        try
        {
            foreach (var batch in pending.Chunk(BatchSize))
            {
                var products = await scheduler.RunAsync(
                    accountName,
                    "pics-license-packages",
                    session.Generation,
                    async _ => await apps.PICSGetProductInfo(
                        [],
                        batch.Select(license => new SteamApps.PICSRequest(license.PackageID, license.AccessToken)),
                        false),
                    cancellationToken).ConfigureAwait(false);
                var packages = (products.Results ?? [])
                    .SelectMany(result => result.Packages)
                    .GroupBy(pair => pair.Key)
                    .ToDictionary(group => group.Key, group => group.First().Value);
                foreach (var license in batch)
                {
                    // Missing PICS data must remain retryable, not become a permanent empty package.
                    if (!packages.TryGetValue(license.PackageID, out var product)
                        || product.KeyValues == null || product.KeyValues == KeyValue.Invalid
                        || license.TimeCreated <= DateTime.UnixEpoch)
                    {
                        incomplete = true;
                        continue;
                    }
                    var appIds = ReadAppIds(product.KeyValues);
                    output.Add(new SteamLicensePackage(
                        license.PackageID,
                        new DateTimeOffset(DateTime.SpecifyKind(license.TimeCreated, DateTimeKind.Utc)),
                        appIds));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to resolve license packages for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            if (output.Count == 0)
                return SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>.Failed(
                    classifier.Classify(exception, cancellationToken));
            var partialAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>.Succeeded(
                output, SteamDataSource.Cm, SteamFreshness.Fresh, partialAt, partialAt,
                SteamFailureKind.Transient, "license_packages_partial");
        }

        var fetchedAt = timeProvider.GetUtcNow();
        return SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>.Succeeded(
            output, SteamDataSource.Cm, SteamFreshness.Fresh, fetchedAt, fetchedAt,
            incomplete ? SteamFailureKind.Transient : null,
            incomplete ? "license_packages_partial" : null);
    }

    private static IReadOnlyList<uint> ReadAppIds(KeyValue? keyValues)
    {
        var node = keyValues?["appids"];
        if (node == null || node == KeyValue.Invalid) return [];
        return node.Children
            .Select(child => child.AsUnsignedInteger())
            .Where(appId => appId != 0)
            .Distinct()
            .ToArray();
    }
}
