using FluentAssertions;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAchievementsServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Test]
    public async Task GetOverviewAsync_UsesPreferCacheAndOneBulkSummaryCall()
    {
        var catalog = new FakeCatalog(
        [
            new OwnedGameCatalogItem(10, "Alpha", "Alpha Localized", 120, 1699999999),
            new OwnedGameCatalogItem(20, "Beta", "Beta Localized", 30, 1699999950)
        ]);
        var progress = new FakeProgressGateway
        {
            SummariesResult = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
                .Succeeded(
                    [new SteamAchievementAppProgressSnapshot(10, 8, 4, 50.0)],
                    SteamDataSource.Cm,
                    SteamFreshness.Fresh,
                    Now,
                    Now)
        };
        var schema = new FakeSchemaGateway();
        var service = CreateService(catalog, schema, progress);

        var result = await service.GetOverviewAsync("acct");

        catalog.Calls.Should().Be(1);
        progress.SummaryCalls.Should().Be(1);
        progress.LastSummaryRefreshMode.Should().Be(SteamRefreshMode.PreferCache);
        progress.LastSummaryAppIds.Should().Equal(10u, 20u);
        progress.UnlockCalls.Should().Be(0);
        schema.Calls.Should().Be(0);
        result.AccountName.Should().Be("acct");
        result.Games.Should().HaveCount(2);
        result.Games[0].Progress.Should().Be(new SteamAchievementAppProgressSnapshot(10, 8, 4, 50.0));
    }

    [Test]
    public async Task GetGameAsync_AppNotInCatalog_ReadsCatalogOnceAndSkipsGateways()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var schema = new FakeSchemaGateway();
        var progress = new FakeProgressGateway();
        var service = CreateService(catalog, schema, progress);

        var result = await service.GetGameAsync("acct", 999);

        catalog.Calls.Should().Be(1);
        schema.Calls.Should().Be(0);
        progress.UnlockCalls.Should().Be(0);
        progress.SummaryCalls.Should().Be(0);
        result.IsSuccess.Should().BeFalse();
        result.AppId.Should().Be(999u);
        result.AppName.Should().BeEmpty();
        result.Achievements.Should().BeEmpty();
        result.SchemaState.Failure.Should().Be(SteamFailureKind.NotFound);
        result.SchemaState.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.AppUnavailable);
        result.ProgressState.Failure.Should().Be(SteamFailureKind.NotFound);
        result.ProgressState.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.AppUnavailable);
    }

    [Test]
    public async Task RefreshGameAsync_AppNotInCatalog_AlsoSkipsGateways()
    {
        var catalog = new FakeCatalog([]);
        var schema = new FakeSchemaGateway();
        var progress = new FakeProgressGateway();
        var service = CreateService(catalog, schema, progress);

        var result = await service.RefreshGameAsync("acct", 999);

        catalog.Calls.Should().Be(1);
        schema.Calls.Should().Be(0);
        progress.UnlockCalls.Should().Be(0);
        result.SchemaState.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.AppUnavailable);
    }

    [Test]
    public async Task GetGameAsync_AppNamePrefersNonEmptyLocalizedName()
    {
        var catalog = new FakeCatalog(
        [
            new OwnedGameCatalogItem(10, "Alpha", "Alpha Localized", 1, 1),
            new OwnedGameCatalogItem(20, "Beta", "", 2, 2)
        ]);
        var schema = new FakeSchemaGateway
        {
            Result = SchemaResult(new SteamAchievementSchemaSnapshot(10, "schinese", 1, 42, [], []))
        };
        var progress = new FakeProgressGateway();
        var service = CreateService(catalog, schema, progress);

        var localized = await service.GetGameAsync("acct", 10);
        var fallback = await service.GetGameAsync("acct", 20);

        localized.AppName.Should().Be("Alpha Localized");
        fallback.AppName.Should().Be("Beta");
        schema.LastLanguage.Should().Be("schinese");
    }

    [Test]
    public async Task GameMethods_PassAccountAppIdLanguageAndExpectedRefreshMode()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var schema = new FakeSchemaGateway
        {
            Result = SchemaResult(new SteamAchievementSchemaSnapshot(10, "schinese", 1, 42, [], []))
        };
        var progress = new FakeProgressGateway();
        var service = CreateService(catalog, schema, progress);

        await service.GetGameAsync("acct", 10);

        schema.LastRefreshMode.Should().Be(SteamRefreshMode.PreferCache);
        schema.LastAppId.Should().Be(10u);
        schema.LastLanguage.Should().Be("schinese");
        schema.LastAccountName.Should().Be("acct");
        progress.LastUnlockRefreshMode.Should().Be(SteamRefreshMode.PreferCache);
        progress.LastUnlockAppId.Should().Be(10u);
        progress.LastUnlockAccountName.Should().Be("acct");

        await service.RefreshGameAsync("acct", 10);

        schema.LastRefreshMode.Should().Be(SteamRefreshMode.RequireRefresh);
        progress.LastUnlockRefreshMode.Should().Be(SteamRefreshMode.RequireRefresh);
    }

    [Test]
    public async Task GetGameAsync_StartsSchemaAndUnlockCallsBeforeAwaitingEither()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var schema = new FakeSchemaGateway
        {
            WaitForGate = true,
            Result = SchemaResult(new SteamAchievementSchemaSnapshot(10, "schinese", 1, 42, [], []))
        };
        var progress = new FakeProgressGateway { WaitForUnlockGate = true };
        var service = CreateService(catalog, schema, progress);

        var pending = service.GetGameAsync("acct", 10);
        await Task.WhenAll(schema.Entered.Task, progress.UnlockEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(30));

        schema.Calls.Should().Be(1);
        progress.UnlockCalls.Should().Be(1);
        schema.Gate.SetResult();
        progress.UnlockGate.SetResult();
        var result = await pending;
        result.IsSuccess.Should().BeTrue();
    }

    [Test]
    public async Task GetGameAsync_SchemaSuccessProgressFailure_ReturnsPartialDefinitions()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var schema = new FakeSchemaGateway
        {
            Result = SchemaResult(new SteamAchievementSchemaSnapshot(
                10,
                "schinese",
                1,
                42,
                [Def("ACH_A", 1), Def("ACH_B", 2)],
                []))
        };
        var progress = new FakeProgressGateway
        {
            UnlocksResult = SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable)
        };
        var service = CreateService(catalog, schema, progress);

        var result = await service.GetGameAsync("acct", 10);

        result.IsSuccess.Should().BeTrue();
        result.IsPartial.Should().BeTrue();
        result.AppName.Should().Be("Alpha");
        result.Achievements.Should().HaveCount(2);
        result.Achievements.Should().OnlyContain(entry => entry.IsUnlocked == null);
        result.Summary.Unknown.Should().Be(2);
        result.ProgressState.Failure.Should().Be(SteamFailureKind.AuthenticationRequired);
        result.ProgressState.DiagnosticCode.Should()
            .Be(SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
    }

    [Test]
    public async Task GetGameAsync_RejectsBlankAccountAndZeroAppId()
    {
        var service = CreateService(new FakeCatalog([]), new FakeSchemaGateway(), new FakeProgressGateway());

        await ((Func<Task>)(() => service.GetGameAsync("  ", 10))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => service.RefreshGameAsync(null!, 10))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => service.GetGameAsync("acct", 0))).Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
        await ((Func<Task>)(() => service.RefreshGameAsync("acct", 0))).Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GameMethods_PropagateCancellation()
    {
        var catalog = new FakeCatalog([new OwnedGameCatalogItem(10, "Alpha", "Alpha", 1, 1)]);
        var service = CreateService(catalog, new FakeSchemaGateway(), new FakeProgressGateway());
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await ((Func<Task>)(() => service.GetGameAsync("acct", 10, source.Token))).Should()
            .ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => service.RefreshGameAsync("acct", 10, source.Token))).Should()
            .ThrowAsync<OperationCanceledException>();
    }

    private static SteamAchievementsService CreateService(
        FakeCatalog catalog,
        FakeSchemaGateway schema,
        FakeProgressGateway progress)
        => new(
            new SteamAchievementOverviewQuery(catalog, progress),
            catalog,
            schema,
            progress,
            new FakeLanguageProvider("schinese"));

    private static SteamAchievementDefinition Def(string internalName, uint internalKey)
        => new(
            internalKey,
            internalName,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            null,
            null,
            false,
            SteamAchievementProgressType.None,
            null,
            null);

    private static SteamGatewayResult<SteamAchievementSchemaSnapshot> SchemaResult(
        SteamAchievementSchemaSnapshot snapshot)
        => SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
            snapshot, SteamDataSource.Cm, SteamFreshness.Fresh, Now, Now);

    private sealed class FakeLanguageProvider(string language) : ILanguageProvider
    {
        public string GetSteamLanguage() => language;
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

    private sealed class FakeSchemaGateway : ISteamAchievementSchemaGateway
    {
        public SteamGatewayResult<SteamAchievementSchemaSnapshot> Result { get; set; }
            = SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.Offline, "offline");

        public int Calls { get; private set; }

        public uint LastAppId { get; private set; }

        public string? LastLanguage { get; private set; }

        public string? LastAccountName { get; private set; }

        public SteamRefreshMode LastRefreshMode { get; private set; }

        public bool WaitForGate { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetSchemaAsync(
            uint appId,
            string language,
            string? preferredAccountName = null,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAppId = appId;
            LastLanguage = language;
            LastAccountName = preferredAccountName;
            LastRefreshMode = refreshMode;
            Entered.TrySetResult();
            if (WaitForGate) await Gate.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Result;
        }
    }

    private sealed class FakeProgressGateway : ISteamAchievementProgressGateway
    {
        public SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>> SummariesResult
        { get; set; } = SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>
            .Failed(SteamFailureKind.Offline, "offline");

        public SteamGatewayResult<SteamAchievementUnlockSnapshot> UnlocksResult { get; set; }
            = SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.Offline, "offline");

        public int SummaryCalls { get; private set; }

        public int UnlockCalls { get; private set; }

        public IReadOnlyList<uint>? LastSummaryAppIds { get; private set; }

        public SteamRefreshMode LastSummaryRefreshMode { get; private set; }

        public string? LastUnlockAccountName { get; private set; }

        public uint LastUnlockAppId { get; private set; }

        public SteamRefreshMode LastUnlockRefreshMode { get; private set; }

        public bool WaitForUnlockGate { get; set; }

        public TaskCompletionSource UnlockEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource UnlockGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>>
            GetSummariesAsync(
                string accountName,
                IReadOnlyList<uint> appIds,
                SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryCalls++;
            LastSummaryAppIds = appIds;
            LastSummaryRefreshMode = refreshMode;
            return Task.FromResult(SummariesResult);
        }

        public async Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
            string accountName,
            uint appId,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
        {
            UnlockCalls++;
            LastUnlockAccountName = accountName;
            LastUnlockAppId = appId;
            LastUnlockRefreshMode = refreshMode;
            UnlockEntered.TrySetResult();
            if (WaitForUnlockGate) await UnlockGate.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return UnlocksResult;
        }
    }
}
