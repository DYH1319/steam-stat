using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAchievementOverviewQueryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Test]
    public async Task GetAsync_ReadsCatalogOnceAndFetchesSummariesOnce()
    {
        var catalog = new FakeCatalog(
        [
            new OwnedGameCatalogItem(30, "Gamma", "Gamma Localized", 10, 1699999900),
            new OwnedGameCatalogItem(10, "Alpha", "Alpha Localized", 120, 1699999999),
            new OwnedGameCatalogItem(20, "Beta", "Beta Localized", 30, 1699999950)
        ]);
        var progress = new FakeProgressGateway
        {
            SummariesResult = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
                .Succeeded(
                    [
                        new SteamAchievementAppProgressSnapshot(10, 8, 4, 50.0),
                        new SteamAchievementAppProgressSnapshot(30, 2, 2, 100.0)
                    ],
                    SteamDataSource.Cm,
                    SteamFreshness.Fresh,
                    Now,
                    Now)
        };
        var query = new SteamAchievementOverviewQuery(catalog, progress);

        var result = await query.GetAsync("acct", SteamRefreshMode.RequireRefresh);

        catalog.Calls.Should().Be(1);
        progress.SummaryCalls.Should().Be(1);
        progress.UnlockCalls.Should().Be(0);
        progress.LastAppIds.Should().Equal(30u, 10u, 20u);
        progress.LastRefreshMode.Should().Be(SteamRefreshMode.RequireRefresh);

        result.AccountName.Should().Be("acct");
        result.Games.Should().HaveCount(3);
        result.Games.Select(game => game.AppId).Should().Equal(30u, 10u, 20u);
        result.Games[0].Name.Should().Be("Gamma");
        result.Games[0].LocalizedName.Should().Be("Gamma Localized");
        result.Games[0].PlaytimeForever.Should().Be(10);
        result.Games[0].LastPlayedAt.Should().Be(1699999900L);
        result.Games[0].Progress.Should().Be(new SteamAchievementAppProgressSnapshot(30, 2, 2, 100.0));
        result.Games[1].Progress.Should().Be(new SteamAchievementAppProgressSnapshot(10, 8, 4, 50.0));
        result.Games[2].Progress.Should().BeNull();
        result.ProgressState.Source.Should().Be(SteamDataSource.Cm);
        result.ProgressState.Freshness.Should().Be(SteamFreshness.Fresh);
        result.IsPartial.Should().BeFalse();
    }

    [Test]
    public async Task GetAsync_EmptyCatalog_StillCallsSummaryGatewayOnce()
    {
        var catalog = new FakeCatalog([]);
        var progress = new FakeProgressGateway
        {
            SummariesResult = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
                .Succeeded([], SteamDataSource.Memory, SteamFreshness.Fresh, Now, Now)
        };
        var query = new SteamAchievementOverviewQuery(catalog, progress);

        var result = await query.GetAsync("acct");

        catalog.Calls.Should().Be(1);
        progress.SummaryCalls.Should().Be(1);
        progress.LastAppIds.Should().BeEmpty();
        result.Games.Should().BeEmpty();
    }

    [Test]
    public async Task GetAsync_PartialProgress_PropagatesState()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var progress = new FakeProgressGateway
        {
            SummariesResult = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
                .Succeeded(
                    [new SteamAchievementAppProgressSnapshot(10, 4, 1, 25.0)],
                    SteamDataSource.Sqlite,
                    SteamFreshness.Stale,
                    Now,
                    Now,
                    SteamFailureKind.Transient,
                    SteamAchievementDiagnosticCodes.ProgressPartial)
        };
        var query = new SteamAchievementOverviewQuery(catalog, progress);

        var result = await query.GetAsync("acct");

        result.IsPartial.Should().BeTrue();
        result.ProgressState.Failure.Should().Be(SteamFailureKind.Transient);
        result.ProgressState.Freshness.Should().Be(SteamFreshness.Stale);
        result.Games.Should().ContainSingle().Which.Progress.Should().NotBeNull();
    }

    [Test]
    public async Task GetAsync_ProgressFailure_LeavesProgressNullAndCarriesFailure()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var progress = new FakeProgressGateway
        {
            SummariesResult = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
                .Failed(
                    SteamFailureKind.AuthenticationRequired,
                    SteamAchievementDiagnosticCodes.ProgressSessionUnavailable)
        };
        var query = new SteamAchievementOverviewQuery(catalog, progress);

        var result = await query.GetAsync("acct");

        result.Games.Should().ContainSingle().Which.Progress.Should().BeNull();
        result.ProgressState.HasValue.Should().BeFalse();
        result.ProgressState.Failure.Should().Be(SteamFailureKind.AuthenticationRequired);
        result.IsPartial.Should().BeFalse();
    }

    [Test]
    public async Task GetAsync_RejectsBlankAccountNames()
    {
        var query = new SteamAchievementOverviewQuery(new FakeCatalog([]), new FakeProgressGateway());

        await ((Func<Task>)(() => query.GetAsync("  "))).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeCatalog(IReadOnlyList<OwnedGameCatalogItem> items) : IOwnedGameCatalog
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<OwnedGameCatalogItem>> GetCachedAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(items);
        }
    }

    private sealed class FakeProgressGateway : ISteamAchievementProgressGateway
    {
        public SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>> SummariesResult
        { get; set; } = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
            .Failed(SteamFailureKind.Offline, "offline");

        public int SummaryCalls { get; private set; }

        public int UnlockCalls { get; private set; }

        public IReadOnlyList<uint>? LastAppIds { get; private set; }

        public SteamRefreshMode LastRefreshMode { get; private set; }

        public Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>>
            GetSummariesAsync(
                string accountName,
                IReadOnlyList<uint> appIds,
                SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryCalls++;
            LastAppIds = appIds;
            LastRefreshMode = refreshMode;
            return Task.FromResult(SummariesResult);
        }

        public Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
            string accountName,
            uint appId,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnlockCalls++;
            return Task.FromResult(SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.Offline, "offline"));
        }
    }
}
