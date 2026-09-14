using FluentAssertions;
using SteamStat.Core.Features.Library;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class LibraryCharacterizationTests
{
    [Test]
    public void MergeOwnedAndFamilyGames_PreservesFlagsAddsOwnersAndSortsByPlaytime()
    {
        var owned = new List<SteamOwnedGame>
        {
            new() { AppId = 10, Name = "Owned", IsOwned = true, PlaytimeForever = 20 }
        };
        var shared = new List<SteamOwnedGame>
        {
            new() { AppId = 20, Name = "Shared", IsFamilyShared = true, PlaytimeForever = 50 }
        };

        var merged = SteamLibraryService.MergeOwnedAndFamilyGames(
            owned, shared, new Dictionary<uint, List<string>> { [10] = ["76561198000000001"] });

        merged.Select(game => game.AppId).Should().Equal(20, 10);
        merged[0].IsFamilyShared.Should().BeTrue();
        merged[1].IsOwned.Should().BeTrue();
        merged[1].OwnerSteamIds.Should().Equal("76561198000000001");
        merged[1].Should().BeSameAs(owned[0]);
    }

    [Test]
    public async Task MergeWishlist_MarksExistingAndAppendsMissingAfterSortedLibrary()
    {
        var games = new List<SteamOwnedGame>
        {
            new() { AppId = 10, IsOwned = true, PlaytimeForever = 100 },
            new() { AppId = 20, IsFamilyShared = true, PlaytimeForever = 50 }
        };
        var resolved = new List<uint>();

        var missingCount = await SteamLibraryService.MergeWishlistAsync(games, [20, 30], appId =>
        {
            resolved.Add(appId);
            return Task.FromResult<string?>("Wishlist only");
        });

        missingCount.Should().Be(1);
        games.Select(game => game.AppId).Should().Equal(10, 20, 30);
        games[1].IsInWishlist.Should().BeTrue();
        games[2].Should().BeEquivalentTo(new
        {
            AppId = 30,
            Name = "Wishlist only",
            NameLocalized = "Wishlist only",
            IsInWishlist = true,
            IsOwned = false,
            IsFamilyShared = false
        });
        resolved.Should().Equal(30u);
    }

    [Test]
    public async Task MergeWishlist_EmptyResponseLeavesLibraryUnchanged()
    {
        var games = new List<SteamOwnedGame> { new() { AppId = 10, IsOwned = true } };

        var missingCount = await SteamLibraryService.MergeWishlistAsync(
            games, [], _ => throw new AssertionException("Resolver should not be called"));

        missingCount.Should().Be(0);
        games.Should().ContainSingle().Which.AppId.Should().Be(10);
    }
}
