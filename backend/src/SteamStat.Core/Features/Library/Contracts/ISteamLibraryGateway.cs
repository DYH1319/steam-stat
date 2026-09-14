using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Library.Contracts;

public sealed record SteamLibraryGameSnapshot(
    int AppId,
    string Name,
    string NameLocalized,
    int PlaytimeForever,
    int Playtime2Weeks,
    int RtimeLastPlayed,
    string ImgIconUrl,
    bool HasCommunityVisibleStats,
    IReadOnlyList<int> ContentDescriptorIds,
    bool IsOwned,
    bool IsFamilyShared,
    IReadOnlyList<string> OwnerSteamIds,
    IReadOnlyList<string> OwnerNames,
    int AchievementTotal,
    int AchievementUnlocked,
    double AchievementPercentage);

public sealed record SteamLibrarySnapshot(
    ulong SteamId,
    long SessionGeneration,
    IReadOnlyList<SteamLibraryGameSnapshot> OwnedGames,
    IReadOnlyList<SteamLibraryGameSnapshot> FamilySharedGames,
    IReadOnlyDictionary<uint, IReadOnlyList<string>> FamilyOwners);

public interface ISteamLibraryGateway
{
    Task<SteamGatewayResult<SteamLibrarySnapshot>> GetLibraryAsync(
        string accountName,
        bool includeFamilyShared,
        CancellationToken cancellationToken = default);
}
