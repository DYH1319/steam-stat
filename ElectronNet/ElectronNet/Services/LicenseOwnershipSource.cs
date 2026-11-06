using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;

namespace ElectronNet.Services;

/// <summary>
/// 成就集的 DLC 所有权：当前有效的 package（登录会话给的，退款后就没有了）
/// 映射到 library_acquisition 里已解析过的 package → 应用。没解析过的 package 先解析一次。
/// </summary>
internal sealed class LicenseOwnershipSource(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamLicenseGateway licenseGateway,
    LibraryArchiveService libraryArchive,
    TimeProvider timeProvider,
    ILogger<LicenseOwnershipSource> logger) : ISteamAppOwnershipSource, IDisposable
{
    /// <summary>有 package 解析不出来时（PICS 没返回）隔一段时间再试，不要每次打开成就都请求。</summary>
    private static readonly TimeSpan ResolveRetry = TimeSpan.FromMinutes(30);

    private sealed record Entry(IReadOnlySet<uint> Packages, SteamAppOwnership Ownership, DateTimeOffset ResolvedAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    public async Task<SteamAppOwnership?> GetAsync(string accountName, CancellationToken cancellationToken = default)
    {
        var packages = licenseGateway.GetCurrentPackageIds(accountName);
        if (packages == null) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_cache.TryGetValue(accountName, out var cached)
                && cached.Packages.SetEquals(packages)
                && (cached.Ownership.IsComplete || now - cached.ResolvedAt < ResolveRetry))
                return cached.Ownership;

            var resolved = await LoadAsync(accountName, cancellationToken).ConfigureAwait(false);
            if (!packages.All(resolved.ContainsKey))
            {
                await libraryArchive.RefreshAcquisitionsAsync(accountName, cancellationToken).ConfigureAwait(false);
                resolved = await LoadAsync(accountName, cancellationToken).ConfigureAwait(false);
            }
            var ownership = new SteamAppOwnership(
                packages.Where(resolved.ContainsKey).SelectMany(package => resolved[package]).ToHashSet(),
                packages.All(resolved.ContainsKey));
            _cache[accountName] = new Entry(packages, ownership, now);
            return ownership;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to resolve owned apps of {AccountName}", accountName);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<uint, List<uint>>> LoadAsync(string accountName, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.LibraryAcquisitionTable.AsNoTracking()
            .Where(row => row.AccountName == accountName && row.PackageId != 0)
            .Select(row => new { row.PackageId, row.AppId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .GroupBy(row => (uint)row.PackageId)
            .ToDictionary(group => group.Key, group => group.Where(row => row.AppId != 0).Select(row => (uint)row.AppId).ToList());
    }

    public void Dispose() => _gate.Dispose();
}
