using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Apps.Contracts;

namespace ElectronNet.Services;

public sealed class SteamAppMetadataService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamAppCatalogGateway appCatalogGateway,
    ILogger<SteamAppMetadataService> logger) : IAppNameResolver, IAppMetadataWriter, IDisposable
{
    private int _disposed;

    public string? GetCachedName(uint appId)
    {
        if (appId == 0 || Volatile.Read(ref _disposed) != 0) return null;

        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.SteamAppTable.AsNoTracking()
                .Where(app => app.AppId == (int)appId)
                .Select(app => app.Name)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read cached app name for {AppId}", appId);
            return null;
        }
    }

    public async Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (appId == 0) return null;

        var result = await appCatalogGateway.GetAppAsync(appId, string.Empty, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null) return GetCachedName(appId);
        await UpsertAsync(result.Value, cancellationToken).ConfigureAwait(false);
        return result.Value.Name;
    }

    public async Task EnsureCachedAsync(IEnumerable<AppMetadata> apps, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(apps);

        try
        {
            var appList = apps.Where(app => app.AppId != 0 && !string.IsNullOrEmpty(app.Name)).ToList();
            if (appList.Count == 0) return;

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var appIds = appList.Select(app => (int)app.AppId).ToList();
            var existingIds = await db.SteamAppTable.AsNoTracking()
                .Where(app => appIds.Contains(app.AppId))
                .Select(app => app.AppId)
                .ToHashSetAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var app in appList)
            {
                if (existingIds.Contains((int)app.AppId)) continue;
                db.SteamAppTable.Add(new SteamApp
                {
                    AppId = (int)app.AppId,
                    Name = app.Name!,
                    NameLocalizedJson = "{}",
                    Installed = false,
                    IsRunning = false
                });
                existingIds.Add((int)app.AppId);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to cache app metadata");
        }
    }

    private async Task UpsertAsync(SteamAppMetadataSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.SteamAppTable
                .FirstOrDefaultAsync(app => app.AppId == (int)snapshot.AppId, cancellationToken)
                .ConfigureAwait(false);
            if (existing == null)
            {
                db.SteamAppTable.Add(new SteamApp
                {
                    AppId = (int)snapshot.AppId,
                    Name = snapshot.Name,
                    NameLocalizedJson = "{}",
                    Installed = false,
                    Type = snapshot.Type,
                    IsFreeApp = snapshot.IsFree,
                    IsRunning = false
                });
            }
            else
            {
                if (string.IsNullOrEmpty(existing.Name)) existing.Name = snapshot.Name;
                if (string.IsNullOrEmpty(existing.Type)) existing.Type = snapshot.Type;
                existing.IsFreeApp ??= snapshot.IsFree;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to update app metadata projection for {AppId}", snapshot.AppId);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
