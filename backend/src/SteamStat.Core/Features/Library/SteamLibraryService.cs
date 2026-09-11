using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Sessions;

namespace SteamStat.Core.Features.Library;

public sealed class SteamLibraryService(
    ISteamSessionAccessor sessionAccessor,
    ISteamLibraryGateway libraryGateway,
    ISteamWishlistGateway wishlistGateway,
    IAppNameResolver appNameResolver,
    IAppMetadataWriter appMetadataWriter,
    TimeProvider timeProvider,
    ILogger<SteamLibraryService> logger) : IEventHandler<SteamSessionEnded>, IDisposable
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<SteamOwnedGame>> _userLibraryCache = new();
    private int _disposed;

    public async Task<List<SteamOwnedGame>> GetLibraryForUserAsync(
        string accountName,
        bool includeFamilyShared = true,
        CancellationToken cancellationToken = default)
    {
        var refreshStartedAt = timeProvider.GetUtcNow();
        try
        {
            var result = await libraryGateway.GetLibraryAsync(
                accountName, includeFamilyShared, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value == null)
            {
                logger.LogWarning(
                    "Failed to get Steam library for {AccountName}: {DiagnosticCode}",
                    accountName, result.DiagnosticCode);
                return [];
            }
            var snapshot = result.Value;
            var ownedGames = snapshot.OwnedGames.Select(ToOwnedGame).ToList();
            var familySharedGames = snapshot.FamilySharedGames.Select(ToOwnedGame).ToList();
            var familyOwners = snapshot.FamilyOwners.ToDictionary(
                pair => pair.Key, pair => pair.Value.ToList());
            var merged = MergeOwnedAndFamilyGames(ownedGames, familySharedGames, familyOwners);
            await ApplyWishlistAsync(merged, snapshot.SteamId, cancellationToken).ConfigureAwait(false);
            _userLibraryCache[accountName] = CloneGames(merged);
            await appMetadataWriter.EnsureCachedAsync(
                merged.Where(game => !string.IsNullOrEmpty(game.Name))
                    .Select(game => new AppMetadata((uint)game.AppId, game.Name)),
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Got {OwnedCount} owned and {SharedCount} family-shared games for {AccountName}",
                ownedGames.Count, familySharedGames.Count, accountName);
            logger.LogDebug(
                "Refreshed Steam library for {AccountName} in {Elapsed}",
                accountName, timeProvider.GetUtcNow() - refreshStartedAt);
            return CloneGames(merged).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to get Steam library for {AccountName}", accountName);
            return [];
        }
    }

    internal static List<SteamOwnedGame> MergeOwnedAndFamilyGames(
        List<SteamOwnedGame> ownedGames,
        List<SteamOwnedGame> familySharedGames,
        IReadOnlyDictionary<uint, List<string>> familyOwnersMap)
    {
        foreach (var game in ownedGames)
            if (familyOwnersMap.TryGetValue((uint)game.AppId, out var owners)) game.OwnerSteamIds = owners;
        return ownedGames.Concat(familySharedGames).OrderByDescending(game => game.PlaytimeForever).ToList();
    }

    private async Task ApplyWishlistAsync(
        List<SteamOwnedGame> games,
        ulong steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await wishlistGateway.GetWishlistAsync(
                steamId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value == null)
            {
                logger.LogWarning("Steam wishlist is degraded: {DiagnosticCode}", result.DiagnosticCode);
                return;
            }
            var missingCount = await MergeWishlistAsync(
                games,
                result.Value.Select(appId => checked((int)appId)).ToArray(),
                appId => appNameResolver.ResolveNameAsync(appId, cancellationToken)).ConfigureAwait(false);
            if (result.Value.Count > 0)
                logger.LogDebug(
                    "Applied {Count} Steam wishlist items ({MissingCount} not owned)",
                    result.Value.Count, missingCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to apply Steam wishlist");
        }
    }

    internal static async Task<int> MergeWishlistAsync(
        List<SteamOwnedGame> games,
        IReadOnlyList<int> wishlist,
        Func<uint, Task<string?>> resolveNameAsync)
    {
        if (wishlist.Count == 0) return 0;
        var byId = games.ToDictionary(game => game.AppId);
        var missing = new List<int>();
        foreach (var appId in wishlist)
        {
            if (byId.TryGetValue(appId, out var game)) game.IsInWishlist = true;
            else missing.Add(appId);
        }
        foreach (var appId in missing)
        {
            var name = await resolveNameAsync((uint)appId).ConfigureAwait(false) ?? string.Empty;
            games.Add(new SteamOwnedGame
            {
                AppId = appId,
                Name = name,
                NameLocalized = name,
                IsInWishlist = true
            });
        }
        return missing.Count;
    }

    public async Task<Dictionary<string, List<SteamOwnedGame>>> GetLibraryForAllUsersAsync(
        bool includeFamilyShared = true,
        CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(sessionAccessor.GetLoggedInUsers()
            .Select(async user => (user, Games: await GetLibraryForUserAsync(
                user, includeFamilyShared, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        return results.ToDictionary(result => result.user, result => result.Games);
    }

    public async Task<bool> SyncLibraryForUserAsync(
        string accountName,
        bool includeFamilyShared = true,
        CancellationToken cancellationToken = default)
        => (await GetLibraryForUserAsync(
            accountName, includeFamilyShared, cancellationToken).ConfigureAwait(false)).Count > 0;

    public async Task<Dictionary<string, bool>> SyncLibraryForAllUsersAsync(
        bool includeFamilyShared = true,
        CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(sessionAccessor.GetLoggedInUsers()
            .Select(async user => (user, Result: await SyncLibraryForUserAsync(
                user, includeFamilyShared, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        return results.ToDictionary(result => result.user, result => result.Result);
    }

    private static SteamOwnedGame ToOwnedGame(SteamLibraryGameSnapshot game) => new()
    {
        AppId = game.AppId,
        Name = game.Name,
        NameLocalized = game.NameLocalized,
        PlaytimeForever = game.PlaytimeForever,
        Playtime2Weeks = game.Playtime2Weeks,
        RtimeLastPlayed = game.RtimeLastPlayed,
        ImgIconUrl = game.ImgIconUrl,
        HasCommunityVisibleStats = game.HasCommunityVisibleStats,
        ContentDescriptorIds = game.ContentDescriptorIds.ToList(),
        IsOwned = game.IsOwned,
        IsFamilyShared = game.IsFamilyShared,
        OwnerSteamIds = game.OwnerSteamIds.ToList(),
        OwnerNames = game.OwnerNames.ToList(),
        AchievementTotal = game.AchievementTotal,
        AchievementUnlocked = game.AchievementUnlocked,
        AchievementPercentage = game.AchievementPercentage
    };

    private static IReadOnlyList<SteamOwnedGame> CloneGames(IEnumerable<SteamOwnedGame> games)
        => games.Select(game => new SteamOwnedGame
        {
            AppId = game.AppId,
            Name = game.Name,
            NameLocalized = game.NameLocalized,
            PlaytimeForever = game.PlaytimeForever,
            Playtime2Weeks = game.Playtime2Weeks,
            RtimeLastPlayed = game.RtimeLastPlayed,
            ImgIconUrl = game.ImgIconUrl,
            HasCommunityVisibleStats = game.HasCommunityVisibleStats,
            ContentDescriptorIds = game.ContentDescriptorIds.ToList(),
            IsOwned = game.IsOwned,
            IsFamilyShared = game.IsFamilyShared,
            IsInWishlist = game.IsInWishlist,
            OwnerSteamIds = game.OwnerSteamIds.ToList(),
            OwnerNames = game.OwnerNames.ToList(),
            AchievementTotal = game.AchievementTotal,
            AchievementUnlocked = game.AchievementUnlocked,
            AchievementPercentage = game.AchievementPercentage
        }).ToArray();

    public void ClearLibraryForAccount(string accountName) => _userLibraryCache.TryRemove(accountName, out _);

    public Task HandleAsync(SteamSessionEnded message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearLibraryForAccount(message.AccountName);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _userLibraryCache.Clear();
    }
}

public sealed class SteamOwnedGame
{
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NameLocalized { get; set; } = string.Empty;
    public int PlaytimeForever { get; set; }
    public int Playtime2Weeks { get; set; }
    public int RtimeLastPlayed { get; set; }
    public string ImgIconUrl { get; set; } = string.Empty;
    public bool HasCommunityVisibleStats { get; set; }
    public List<int> ContentDescriptorIds { get; set; } = [];
    public bool IsOwned { get; set; }
    public bool IsFamilyShared { get; set; }
    public bool IsInWishlist { get; set; }
    public List<string> OwnerSteamIds { get; set; } = [];
    public List<string> OwnerNames { get; set; } = [];
    public int AchievementTotal { get; set; }
    public int AchievementUnlocked { get; set; }
    public double AchievementPercentage { get; set; }
}
