namespace SteamStat.Core.Features.Library.Contracts;

public interface IOwnedGameCatalog
{
    Task<IReadOnlyList<OwnedGameCatalogItem>> GetCachedAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}

public sealed record OwnedGameCatalogItem(
    uint AppId,
    string Name,
    string LocalizedName,
    int PlaytimeForever,
    long LastPlayedAt);
