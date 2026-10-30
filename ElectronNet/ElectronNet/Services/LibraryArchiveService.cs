using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Services;

/// <summary>
/// 游戏库档案视角：入库时间（Steam 许可证 / 首次观测回退）与用户录入的游戏状态、评分、价格。
/// </summary>
internal sealed class LibraryArchiveService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamLibraryService libraryService,
    ISteamLicenseGateway licenseGateway,
    TimeProvider timeProvider,
    ILogger<LibraryArchiveService> logger) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<LibraryAcquisitionRefreshResultDto> RefreshAcquisitionsAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            // 1. 首次观测：本地快照中出现、尚未记录的自有游戏
            var page = await libraryService.GetLibraryPageAsync(accountName, 0, int.MaxValue, cancellationToken)
                .ConfigureAwait(false);
            var ownedIds = page.Items.Where(game => game.IsOwned).Select(game => (long)game.AppId).ToHashSet();
            var observedIds = (await db.LibraryAcquisitionTable.AsNoTracking()
                    .Where(row => row.AccountName == accountName && row.PackageId == 0)
                    .Select(row => row.AppId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet();
            var newlyObserved = ownedIds.Where(appId => !observedIds.Contains(appId)).ToArray();
            db.LibraryAcquisitionTable.AddRange(newlyObserved.Select(appId => new LibraryAcquisition
            {
                AccountName = accountName,
                AppId = appId,
                PackageId = 0,
                AcquiredAt = now
            }));

            // 2. 许可证：只解析尚未记录过的 package
            var knownPackages = (await db.LibraryAcquisitionTable.AsNoTracking()
                    .Where(row => row.AccountName == accountName && row.PackageId != 0)
                    .Select(row => row.PackageId)
                    .Distinct()
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(id => (uint)id)
                .ToHashSet();
            var licenses = await licenseGateway.GetLicensedPackagesAsync(accountName, knownPackages, cancellationToken)
                .ConfigureAwait(false);
            var packages = 0;
            if (licenses is { IsSuccess: true, Value: { } value })
            {
                foreach (var package in value)
                {
                    var acquiredAt = package.TimeCreated.ToUnixTimeSeconds();
                    var appIds = package.AppIds.Count == 0 ? [0u] : package.AppIds;
                    db.LibraryAcquisitionTable.AddRange(appIds.Distinct().Select(appId => new LibraryAcquisition
                    {
                        AccountName = accountName,
                        AppId = appId,
                        PackageId = package.PackageId,
                        AcquiredAt = acquiredAt
                    }));
                    packages++;
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var failure = licenses.Failure;
            return new LibraryAcquisitionRefreshResultDto
            {
                Status = failure == null ? "success" : licenses.IsSuccess || newlyObserved.Length > 0 || ownedIds.Count > 0 ? "partial" : "failure",
                Observed = newlyObserved.Length,
                Packages = packages,
                Failure = failure == null ? null : ToWire(failure.Value),
                DiagnosticCode = licenses.DiagnosticCode
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to refresh library acquisitions for {AccountName}", accountName);
            return new LibraryAcquisitionRefreshResultDto
            {
                Status = "failure",
                Observed = 0,
                Packages = 0,
                Failure = "unknown",
                DiagnosticCode = "library_acquisition_failed"
            };
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// 每个应用取最早的许可证时间；没有许可证记录时回退到首次观测时间。
    /// </summary>
    public LibraryAcquisitionPageDto GetAcquisitions(LibraryAcquisitionPageRequest request)
    {
        using var db = dbContextFactory.CreateDbContext();
        var rows = db.LibraryAcquisitionTable.AsNoTracking()
            .Where(row => row.AccountName == request.AccountName && row.AppId != 0)
            .Select(row => new { row.AppId, row.PackageId, row.AcquiredAt })
            .ToList();
        var items = rows.GroupBy(row => row.AppId)
            .Select(group =>
            {
                var license = group.Where(row => row.PackageId != 0).MinBy(row => row.AcquiredAt);
                var pick = license ?? group.MinBy(row => row.AcquiredAt)!;
                return new LibraryAcquisitionDto
                {
                    AppId = (int)group.Key,
                    AcquiredAt = pick.AcquiredAt,
                    Source = license != null ? "license" : "observed",
                    PackageId = license != null ? (uint)license.PackageId : null
                };
            })
            .OrderBy(item => item.AppId)
            .ToArray();
        var pageItems = items.Skip(request.Offset).Take(request.Limit).ToArray();
        var nextOffset = request.Offset + pageItems.Length;
        return new LibraryAcquisitionPageDto
        {
            Total = items.Length,
            Items = pageItems,
            NextOffset = nextOffset < items.Length ? nextOffset : null
        };
    }

    public IReadOnlyList<LibraryGameMetaDto> GetGameMeta(string accountName, int offset = 0, int limit = 100)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.LibraryGameMetaTable.AsNoTracking()
            .Where(row => row.AccountName == accountName)
            .OrderBy(row => row.AppId)
            .Skip(offset)
            .Take(limit)
            .AsEnumerable()
            .Select(row => new LibraryGameMetaDto
            {
                AppId = (int)row.AppId,
                Status = row.Status,
                CompletedAt = row.CompletedAt,
                Rating = row.Rating,
                Price = row.Price,
                Currency = row.Currency,
                Note = row.Note,
                UpdatedAt = row.UpdatedAt
            })
            .ToArray();
    }

    public async Task<int> UpdateGameMetaAsync(
        LibraryGameMetaUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0) return 0;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var appIds = request.Items.Select(item => (long)item.AppId).Distinct().ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.LibraryGameMetaTable
            .Where(row => row.AccountName == request.AccountName && appIds.Contains(row.AppId))
            .ToDictionaryAsync(row => row.AppId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in request.Items)
        {
            var appId = (long)item.AppId;
            var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim();
            var currency = string.IsNullOrWhiteSpace(item.Currency) ? null : item.Currency.Trim().ToUpperInvariant();
            var empty = item.Status == null && item.CompletedAt == null && item.Rating == null
                && item.Price == null && note == null;
            existing.TryGetValue(appId, out var row);
            if (empty)
            {
                if (row != null)
                {
                    db.LibraryGameMetaTable.Remove(row);
                    existing.Remove(appId);
                }
                continue;
            }
            if (row == null)
            {
                row = new LibraryGameMeta { AccountName = request.AccountName, AppId = appId };
                db.LibraryGameMetaTable.Add(row);
                existing[appId] = row;
            }
            row.Status = item.Status;
            row.CompletedAt = item.CompletedAt;
            row.Rating = item.Rating;
            row.Price = item.Price;
            row.Currency = item.Price == null ? null : currency;
            row.Note = note;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return request.Items.Count;
    }

    private static string ToWire(SteamFailureKind failure) => failure switch
    {
        SteamFailureKind.Offline => "offline",
        SteamFailureKind.AuthenticationRequired => "authenticationRequired",
        SteamFailureKind.Forbidden => "forbidden",
        SteamFailureKind.NotFound => "notFound",
        SteamFailureKind.RateLimited => "rateLimited",
        SteamFailureKind.Transient => "transient",
        SteamFailureKind.Timeout => "timeout",
        SteamFailureKind.Protocol => "protocol",
        SteamFailureKind.InvalidData => "invalidData",
        _ => "unknown"
    };

    public void Dispose() => _refreshLock.Dispose();
}
