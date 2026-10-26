using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Cache;

namespace SteamStat.Core.Features.Library;

public sealed class SteamOwnedGameCatalog(SteamFeatureSnapshotStore snapshotStore) : IOwnedGameCatalog
{
    public async Task<IReadOnlyList<OwnedGameCatalogItem>> GetCachedAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var snapshot = await snapshotStore.GetLibraryAsync(accountName, cancellationToken).ConfigureAwait(false);
        if (snapshot == null) return [];
        return snapshot.Value
            .Where(game => game.IsOwned || game.IsFamilyShared)
            .Select(game => new OwnedGameCatalogItem(
                (uint)game.AppId,
                game.Name,
                game.NameLocalized,
                game.PlaytimeForever,
                game.RtimeLastPlayed))
            .ToArray();
    }
}
