using System.Collections.Concurrent;
using System.Text.Json;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features;
using SteamStat.Core.Http;

namespace ElectronNet.Services;

public sealed class SteamAppMetadataService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<SteamAppMetadataService> logger) : IAppNameResolver, IAppMetadataWriter, IDisposable
{
    private readonly ConcurrentDictionary<uint, Task<string?>> _inflightFetches = new();
    private readonly CancellationTokenSource _lifetime = new();
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

        var cachedName = GetCachedName(appId);
        if (!string.IsNullOrEmpty(cachedName)) return cachedName;

        var task = _inflightFetches.GetOrAdd(appId, FetchFromStoreAsync);
        _ = task.ContinueWith(
            (_, state) =>
            {
                var (owner, id, completedTask) = ((SteamAppMetadataService, uint, Task<string?>))state!;
                ((ICollection<KeyValuePair<uint, Task<string?>>>)owner._inflightFetches)
                    .Remove(new KeyValuePair<uint, Task<string?>>(id, completedTask));
            },
            (this, appId, task),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to cache app metadata");
        }
    }

    private async Task<string?> FetchFromStoreAsync(uint appId)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(SteamStatHttpClients.SteamApi)
                .GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", _lifetime.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: _lifetime.Token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty(appId.ToString(), out var appElement)
                || !appElement.TryGetProperty("success", out var successElement)
                || !successElement.GetBoolean()
                || !appElement.TryGetProperty("data", out var dataElement))
                return null;

            var name = dataElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrEmpty(name)) return null;
            var type = dataElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var isFree = dataElement.TryGetProperty("is_free", out var isFreeElement) && isFreeElement.GetBoolean();

            await UpsertAsync(appId, name, type, isFree, _lifetime.Token).ConfigureAwait(false);
            return name;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch app metadata for {AppId}", appId);
            return null;
        }
    }

    private async Task UpsertAsync(uint appId, string name, string? type, bool isFree, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.SteamAppTable.FirstOrDefaultAsync(app => app.AppId == (int)appId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            db.SteamAppTable.Add(new SteamApp
            {
                AppId = (int)appId,
                Name = name,
                NameLocalizedJson = "{}",
                Installed = false,
                Type = type,
                IsFreeApp = isFree,
                IsRunning = false
            });
        }
        else
        {
            if (string.IsNullOrEmpty(existing.Name)) existing.Name = name;
            if (string.IsNullOrEmpty(existing.Type)) existing.Type = type;
            existing.IsFreeApp ??= isFree;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _inflightFetches.Clear();
        _lifetime.Dispose();
    }
}
