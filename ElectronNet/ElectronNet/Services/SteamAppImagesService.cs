using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Services;

public sealed class SteamAppImagesService(ISteamAppCatalogGateway catalog)
{
    private readonly SemaphoreSlim _concurrency = new(4);

    public async Task<IReadOnlyList<SteamAppImagesDto>> GetAsync(SteamAppImagesRequest request)
    {
        if (request.AppIds.Count > 20 || request.AppIds.Any(id => id == 0))
            throw new ArgumentException("Expected at most 20 valid Steam app ids.");
        var language = request.Language ?? "english";
        return await Task.WhenAll(request.AppIds.Distinct().Select(async id =>
        {
            await _concurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await catalog.GetAppAsync((uint)id, language, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                // Old name-only cache payloads are valid, but cannot satisfy an artwork request.
                if (Incomplete(result.Value?.Artwork) && result.Source == SteamDataSource.Sqlite)
                    result = await catalog.GetAppAsync((uint)id, language,
                        refreshMode: SteamRefreshMode.RequireRefresh, cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                var images = result.Value?.Artwork;
                return new SteamAppImagesDto(id, images?.Capsule ?? [], images?.Header ?? [],
                    images?.Cover ?? [], images?.Hero ?? [], Incomplete(images));
            }
            catch (OperationCanceledException)
            {
                return new SteamAppImagesDto(id, [], [], [], [], true);
            }
            finally
            {
                _concurrency.Release();
            }
        })).ConfigureAwait(false);
    }

    private static bool Incomplete(SteamAppArtwork? images)
        => images == null || images.Cover.Count == 0 || images.Hero.Count == 0;
}
