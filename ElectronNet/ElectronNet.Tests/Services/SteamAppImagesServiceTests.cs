using ElectronNet.Services;
using FluentAssertions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SteamAppImagesServiceTests
{
    [Test]
    public async Task OldNameOnlyCache_IsRefreshedWithoutRemovingExistingMetadata()
    {
        var artwork = new SteamAppArtwork([], ["https://shared.fastly.steamstatic.com/header.jpg"],
            ["https://shared.fastly.steamstatic.com/cover.jpg"], ["https://shared.fastly.steamstatic.com/hero.jpg"]);
        var gateway = new FakeCatalog(mode => new SteamAppMetadataSnapshot(
            3527290, "PEAK", "game", false, mode == SteamRefreshMode.RequireRefresh ? artwork : null));
        var result = await new SteamAppImagesService(gateway).GetAsync(new SteamAppImagesRequest
        {
            AppIds = [3527290, 3527290], Language = "schinese"
        });

        result.Should().ContainSingle();
        result[0].Header.Should().Equal(artwork.Header);
        result[0].Retryable.Should().BeFalse();
        gateway.Modes.Should().Equal(SteamRefreshMode.PreferCache, SteamRefreshMode.RequireRefresh);
    }

    [Test]
    public async Task OfflineNameOnlyCache_IsRetryableAndDoesNotInventArtworkUrls()
    {
        var gateway = new FakeCatalog(_ => new SteamAppMetadataSnapshot(3527290, "PEAK", "game", false));
        var result = await new SteamAppImagesService(gateway).GetAsync(new SteamAppImagesRequest { AppIds = [3527290] });
        result[0].Retryable.Should().BeTrue();
        result[0].Header.Should().BeEmpty();
    }

    [Test]
    public async Task OversizedBatch_IsRejectedBeforeQueryingGateway()
    {
        var gateway = new FakeCatalog(_ => null);
        var action = () => new SteamAppImagesService(gateway).GetAsync(new SteamAppImagesRequest
        {
            AppIds = Enumerable.Range(1, 21).Select(id => (uint)id).ToArray()
        });
        await action.Should().ThrowAsync<ArgumentException>();
        gateway.Modes.Should().BeEmpty();
    }

    private sealed class FakeCatalog(Func<SteamRefreshMode, SteamAppMetadataSnapshot?> value) : ISteamAppCatalogGateway
    {
        public List<SteamRefreshMode> Modes { get; } = [];

        public Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAppAsync(
            uint appId, string language, string? preferredAccountName = null,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache, CancellationToken cancellationToken = default)
        {
            Modes.Add(refreshMode);
            return Task.FromResult(SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
                value(refreshMode), SteamDataSource.Sqlite, SteamFreshness.Fresh,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));
        }
    }
}
