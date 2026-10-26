using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAchievementProgressGatewayTests
{
    private static readonly SteamCachePolicy SummaryPolicy = SteamResourcePolicies.AchievementProgressSummary;
    private static readonly SteamCachePolicy UnlockPolicy = SteamResourcePolicies.AchievementUnlocks;
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private const ulong SteamIdA = 76561198000000001UL;
    private const ulong SteamIdB = 76561198000000002UL;

    [Test]
    public async Task FreshSummaryCache_ServesWithoutSourceCalls()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        SeedSummaries(store, SteamIdA, [1u, 2u], [Summary(1), Summary(2)], time.Now);
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 2u]);

        result.IsSuccess.Should().BeTrue();
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        result.Value.Should().Equal(Summary(1), Summary(2));
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task CacheOnlySummary_HitWhenCovered_MissWhenNot()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        SeedSummaries(store, SteamIdA, [1u], [Summary(1)], time.Now);
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var hit = await gateway.GetSummariesAsync("acct", [1u], SteamRefreshMode.CacheOnly);
        hit.IsSuccess.Should().BeTrue();
        hit.Source.Should().Be(SteamDataSource.Sqlite);

        var coveredEmpty = await gateway.GetSummariesAsync(
            "acct", [1u, 2u], SteamRefreshMode.CacheOnly);
        coveredEmpty.IsSuccess.Should().BeFalse();
        coveredEmpty.Failure.Should().Be(SteamFailureKind.NotFound);
        coveredEmpty.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressCacheMiss);

        var miss = await gateway.GetSummariesAsync("acct", [9u], SteamRefreshMode.CacheOnly);
        miss.IsSuccess.Should().BeFalse();
        miss.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressCacheMiss);
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task SuccessEmptyCoverage_CountsAsCoveredWithoutValue()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        SeedSummaries(store, SteamIdA, [1u, 7u], [Summary(1)], time.Now);
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 7u], SteamRefreshMode.CacheOnly);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(Summary(1));
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task NoIdentity_ReturnsAuthenticationRequiredBeforeCacheAccess()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        SeedSummaries(store, SteamIdA, [1u], [Summary(1)], time.Now);
        var source = new FakeProgressSource(identity: null);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u]);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.AuthenticationRequired);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task EmptyAppIds_ReturnsSuccessEmptyWithoutSourceCalls()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", []);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        result.Source.Should().Be(SteamDataSource.Memory);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task ZeroAppId_IsRejectedAsInvalidData()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 0u]);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.InvalidData);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task PersonalCache_IsolatesAccountsBySteamId()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        SeedSummaries(store, SteamIdA, [10u], [Summary(10)], time.Now);
        var source = new FakeProgressSource(SteamIdB, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdB, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time)
        };
        using var gateway = CreateGateway(store, source, time);

        var cacheOnly = await gateway.GetSummariesAsync("acct-b", [10u], SteamRefreshMode.CacheOnly);
        cacheOnly.IsSuccess.Should().BeFalse();
        cacheOnly.Failure.Should().Be(SteamFailureKind.NotFound);

        var fetched = await gateway.GetSummariesAsync("acct-b", [10u]);
        fetched.IsSuccess.Should().BeTrue();
        fetched.Source.Should().Be(SteamDataSource.Cm);
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA)).Should().NotBeNull();
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdB)).Should().NotBeNull();
    }

    [Test]
    public async Task MalformedSummaryCache_IsSafeMissAndFetches()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var key = SteamAchievementCacheKeys.ProgressSummary(SteamIdA);
        store.Put(SummaryPolicy.CreateEntry(key, "{ malformed", SteamDataSource.Cm, time.Now, "json-v1"));
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [4u]);

        result.IsSuccess.Should().BeTrue();
        result.Source.Should().Be(SteamDataSource.Cm);
        source.SummaryCalls.Should().Be(1);
    }

    [Test]
    public async Task SemanticMismatchSummaryCache_IsSafeMiss()
    {
        var time = new MutableTimeProvider(BaseTime);
        var key = SteamAchievementCacheKeys.ProgressSummary(SteamIdA);
        var wrongAccountStore = new MemoryCacheStore();
        wrongAccountStore.Put(SummaryPolicy.CreateEntry(
            key,
            JsonSerializer.Serialize(new AchievementProgressSummaryCachePayload(
                SteamIdB, [1u], [Summary(1)])),
            SteamDataSource.Cm,
            time.Now,
            "json-v1"));
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(wrongAccountStore, source, time);

        var miss = await gateway.GetSummariesAsync("acct", [1u], SteamRefreshMode.CacheOnly);
        miss.IsSuccess.Should().BeFalse();
        miss.Failure.Should().Be(SteamFailureKind.NotFound);

        var uncoveredValueStore = new MemoryCacheStore();
        uncoveredValueStore.Put(SummaryPolicy.CreateEntry(
            key,
            JsonSerializer.Serialize(new AchievementProgressSummaryCachePayload(
                SteamIdA, [1u], [Summary(1), Summary(9)])),
            SteamDataSource.Cm,
            time.Now,
            "json-v1"));
        using var secondGateway = CreateGateway(uncoveredValueStore, source, time);
        var secondMiss = await secondGateway.GetSummariesAsync("acct", [1u], SteamRefreshMode.CacheOnly);
        secondMiss.IsSuccess.Should().BeFalse();
        source.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task SummaryRestartRoundtrip_PersistsAcrossGatewayInstances()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var onlineSource = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time)
        };
        using (var first = CreateGateway(store, onlineSource, time))
            (await first.GetSummariesAsync("acct", [5u, 6u])).IsSuccess.Should().BeTrue();

        var offlineSource = new FakeProgressSource(SteamIdA, generation: 3);
        using var restarted = CreateGateway(store, offlineSource, time);
        var result = await restarted.GetSummariesAsync("acct", [5u, 6u]);

        result.IsSuccess.Should().BeTrue();
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        result.Value.Should().Equal(Summary(5), Summary(6));
        offlineSource.SummaryCalls.Should().Be(0);
    }

    [Test]
    public async Task TotalSourceFailure_FallsBackToRetainedStaleCache()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var seeded = SeedSummaries(store, SteamIdA, [1u, 2u], [Summary(1), Summary(2)], time.Now);
        time.Advance(SummaryPolicy.RefreshInterval + TimeSpan.FromMinutes(1));
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 2u]);

        result.IsSuccess.Should().BeTrue();
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Stale);
        result.Value.Should().Equal(Summary(1), Summary(2));
        result.Failure.Should().Be(SteamFailureKind.Offline);
        result.DiagnosticCode.Should().Be("offline");
        store.Get(seeded.Key)!.Payload.Should().Be(seeded.Payload);
    }

    [Test]
    public async Task TotalSourceFailure_WithoutFullRetainedCoverage_Fails()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        SeedSummaries(store, SteamIdA, [1u], [Summary(1)], time.Now);
        time.Advance(SummaryPolicy.RefreshInterval + TimeSpan.FromMinutes(1));
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 2u]);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Offline);
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA)).Should().NotBeNull();
    }

    [Test]
    public async Task StaleSourceIdentity_NeverWritesCache()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        source.SummariesFactory = (_, ids) =>
        {
            source.Identity = new AchievementProgressSessionIdentity(SteamIdA, 4);
            return BatchSuccess(SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time);
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u]);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Transient);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA)).Should().BeNull();
    }

    [Test]
    public async Task PartialSourceResult_MergesRetainedAndFreshValues()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var seeded = SeedSummaries(store, SteamIdA, [1u], [Summary(1, total: 5)], time.Now);
        time.Advance(SummaryPolicy.RefreshInterval + TimeSpan.FromMinutes(1));
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => new SteamGatewayResult<AchievementProgressBatchSnapshot>(
                true,
                new AchievementProgressBatchSnapshot(
                    SteamIdA, 3, ids, [2u], [Summary(2, total: 8)]),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now,
                SteamFailureKind.Transient,
                SteamAchievementDiagnosticCodes.ProgressPartial)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 2u]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(Summary(1, total: 5), Summary(2, total: 8));
        result.Freshness.Should().Be(SteamFreshness.Stale);
        result.Failure.Should().Be(SteamFailureKind.Transient);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressPartial);
        source.SummaryCalls.Should().Be(1);

        var persisted = ReadSummaryPayload(store.Get(seeded.Key)!);
        persisted.SteamId.Should().Be(SteamIdA);
        persisted.CoveredAppIds.Should().Equal(1u, 2u);
        persisted.Values.Should().Equal(Summary(1, total: 5), Summary(2, total: 8));
    }

    [Test]
    public async Task OwnedPartialResult_WithFullCoverage_StillReportsPartial()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => new SteamGatewayResult<AchievementProgressBatchSnapshot>(
                true,
                new AchievementProgressBatchSnapshot(
                    SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray()),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now,
                SteamFailureKind.Transient,
                SteamAchievementDiagnosticCodes.ProgressPartial)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 2u]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(Summary(1), Summary(2));
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        result.Failure.Should().Be(SteamFailureKind.Transient);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressPartial);
        source.SummaryCalls.Should().Be(1);
    }

    [Test]
    public async Task InvalidSourceSnapshot_IsRejectedWithoutWrite()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdA, 3, ids, [9u], [Summary(9)], time)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [1u, 2u]);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.InvalidData);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA)).Should().BeNull();
        source.SummaryCalls.Should().Be(1);
    }

    [Test]
    public async Task SuccessEmptyRefresh_RemovesRetainedSummaryButKeepsCoverage()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var seeded = SeedSummaries(store, SteamIdA, [1u, 2u], [Summary(1), Summary(2)], time.Now);
        time.Advance(SummaryPolicy.RefreshInterval + TimeSpan.FromMinutes(1));
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(SteamIdA, 3, ids, ids, [], time)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [2u]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        result.Freshness.Should().Be(SteamFreshness.Fresh);

        var persisted = ReadSummaryPayload(store.Get(seeded.Key)!);
        persisted.CoveredAppIds.Should().Equal(1u, 2u);
        persisted.Values.Should().Equal(Summary(1));
    }

    [Test]
    public async Task SummaryWrite_PersistsDeterministicOrdering()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSummariesAsync("acct", [9u, 1u, 5u]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(Summary(9), Summary(1), Summary(5));
        var persisted = ReadSummaryPayload(
            store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA))!);
        persisted.CoveredAppIds.Should().Equal(1u, 5u, 9u);
        persisted.Values.Should().Equal(Summary(1), Summary(5), Summary(9));
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA))!.Payload
            .Should().NotContain("SessionGeneration");
    }

    [Test]
    public async Task CoalescedSubsetCall_FetchesOnlyMissingIdsInFollowUp()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time),
            BeforeSummaries = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        using var gateway = CreateGateway(store, source, time);

        var first = gateway.GetSummariesAsync("acct", [1u]);
        await started.Task;
        var second = gateway.GetSummariesAsync("acct", [1u, 2u]);
        await Task.Delay(50);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.IsSuccess);
        results[1].Value.Should().Equal(Summary(1), Summary(2));
        source.SummaryCalls.Should().Be(2);
        source.SummaryRequests[0].Should().Equal(1u);
        source.SummaryRequests[1].Should().Equal(2u);
    }

    [Test]
    public async Task CallerCancellation_DoesNotCancelSharedSummaryOperation()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            SummariesFactory = (_, ids) => BatchSuccess(
                SteamIdA, 3, ids, ids, ids.Select(id => Summary(id)).ToArray(), time),
            BeforeSummaries = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        using var gateway = CreateGateway(store, source, time);
        using var firstCancellation = new CancellationTokenSource();

        var first = gateway.GetSummariesAsync("acct", [1u], cancellationToken: firstCancellation.Token);
        await started.Task;
        var second = gateway.GetSummariesAsync("acct", [1u]);
        await firstCancellation.CancelAsync();

        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        var secondResult = await second;

        secondResult.IsSuccess.Should().BeTrue();
        source.SummaryCalls.Should().Be(1);
        store.Get(SteamAchievementCacheKeys.ProgressSummary(SteamIdA)).Should().NotBeNull();
    }

    [Test]
    public async Task UnlockFetch_PersistsKeylessEntriesWithoutSessionGeneration()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var entries = new SteamAchievementUnlock[]
        {
            new(null, "ACH_ALPHA", true, DateTimeOffset.FromUnixTimeSeconds(1700000000)),
            new(null, "ACH_BETA", false, null)
        };
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            UnlocksFactory = (_, appId) => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(SteamIdA, appId, 3, entries),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetUnlocksAsync("acct", 730);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SessionGeneration.Should().Be(3);
        result.Value.Entries.Should().Equal(entries);
        var entry = store.Get(SteamAchievementCacheKeys.Unlocks(SteamIdA, 730))!;
        entry.Payload.Should().NotContain("SessionGeneration");
        entry.Payload.Should().NotContain("acct");

        source.Identity = new AchievementProgressSessionIdentity(SteamIdA, 9);
        var cached = await gateway.GetUnlocksAsync("acct", 730, SteamRefreshMode.CacheOnly);
        cached.IsSuccess.Should().BeTrue();
        cached.Source.Should().Be(SteamDataSource.Sqlite);
        cached.Value!.SessionGeneration.Should().Be(9);
        cached.Value.Entries.Should().Equal(entries);
        source.UnlockCalls.Should().Be(1);
    }

    [Test]
    public async Task UnlockFetch_KeyedEntries_RoundtripThroughCache()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var entries = new SteamAchievementUnlock[]
        {
            new(11, string.Empty, true, DateTimeOffset.FromUnixTimeSeconds(1700000000)),
            new(12, "ACH_NAMED", false, null)
        };
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            UnlocksFactory = (_, appId) => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(SteamIdA, appId, 3, entries),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now)
        };
        using var gateway = CreateGateway(store, source, time);

        var fetched = await gateway.GetUnlocksAsync("acct", 730);
        fetched.IsSuccess.Should().BeTrue();
        var cached = await gateway.GetUnlocksAsync("acct", 730, SteamRefreshMode.CacheOnly);
        cached.IsSuccess.Should().BeTrue();
        cached.Source.Should().Be(SteamDataSource.Sqlite);
        cached.Value!.Entries.Should().Equal(entries);
        source.UnlockCalls.Should().Be(1);
    }

    [Test]
    public async Task UnlockCache_IsolatesAccountsBySteamId()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var entriesA = new SteamAchievementUnlock[]
        {
            new(null, "ACH_OWNER_A", true, DateTimeOffset.FromUnixTimeSeconds(1700000000))
        };
        var payloadA = JsonSerializer.Serialize(
            new AchievementUnlockCachePayload(SteamIdA, 730, entriesA));
        store.Put(UnlockPolicy.CreateEntry(
            SteamAchievementCacheKeys.Unlocks(SteamIdA, 730),
            payloadA, SteamDataSource.Cm, time.Now, "json-v1"));
        var source = new FakeProgressSource(SteamIdB, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var cacheOnly = await gateway.GetUnlocksAsync("acct-b", 730, SteamRefreshMode.CacheOnly);
        cacheOnly.IsSuccess.Should().BeFalse();
        cacheOnly.Failure.Should().Be(SteamFailureKind.NotFound);
        store.Get(SteamAchievementCacheKeys.Unlocks(SteamIdA, 730))!.Payload.Should().Be(payloadA);
        source.UnlockCalls.Should().Be(0);
    }

    [Test]
    public async Task UnlockSourceFailure_FallsBackToRetainedStaleCache()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var entries = new SteamAchievementUnlock[]
        {
            new(null, "ACH_KEEP", true, DateTimeOffset.FromUnixTimeSeconds(1700000000))
        };
        var key = SteamAchievementCacheKeys.Unlocks(SteamIdA, 730);
        var payload = JsonSerializer.Serialize(new AchievementUnlockCachePayload(SteamIdA, 730, entries));
        var seeded = UnlockPolicy.CreateEntry(key, payload, SteamDataSource.Cm, time.Now, "json-v1");
        store.Put(seeded);
        time.Advance(UnlockPolicy.RefreshInterval + TimeSpan.FromMinutes(1));
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetUnlocksAsync("acct", 730);

        result.IsSuccess.Should().BeTrue();
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Stale);
        result.Failure.Should().Be(SteamFailureKind.Offline);
        result.Value!.Entries.Should().Equal(entries);
        result.Value.SessionGeneration.Should().Be(3);
        store.Get(key)!.Payload.Should().Be(seeded.Payload);
    }

    [Test]
    public async Task UnlockCacheMiss_CacheOnlyFailsTyped()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3);
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetUnlocksAsync("acct", 730, SteamRefreshMode.CacheOnly);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.NotFound);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.UnlocksCacheMiss);
        source.UnlockCalls.Should().Be(0);
    }

    [Test]
    public async Task UnlockMalformedCache_IsSafeMissAndFetches()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var key = SteamAchievementCacheKeys.Unlocks(SteamIdA, 730);
        var duplicateNames = JsonSerializer.Serialize(new AchievementUnlockCachePayload(
            SteamIdA,
            730,
            [
                new SteamAchievementUnlock(null, "ACH_DUP", true, null),
                new SteamAchievementUnlock(null, "ACH_DUP", false, null)
            ]));
        store.Put(UnlockPolicy.CreateEntry(key, duplicateNames, SteamDataSource.Cm, time.Now, "json-v1"));
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            UnlocksFactory = (_, appId) => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(
                    SteamIdA, appId, 3, [new SteamAchievementUnlock(null, "ACH_VALID", true, null)]),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now)
        };
        using var gateway = CreateGateway(store, source, time);

        var miss = await gateway.GetUnlocksAsync("acct", 730, SteamRefreshMode.CacheOnly);
        miss.IsSuccess.Should().BeFalse();

        var result = await gateway.GetUnlocksAsync("acct", 730);
        result.IsSuccess.Should().BeTrue();
        source.UnlockCalls.Should().Be(1);
        result.Value!.Entries.Should().ContainSingle();
    }

    [Test]
    public async Task UnlockStaleIdentity_DoesNotWrite()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            UnlocksFactory = (_, appId) => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(SteamIdB, appId, 3, []),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetUnlocksAsync("acct", 730);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Transient);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
        store.Get(SteamAchievementCacheKeys.Unlocks(SteamIdA, 730)).Should().BeNull();
    }

    [Test]
    public async Task UnlockUnmatchedDiagnostic_IsPreservedOnSuccess()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var entries = new SteamAchievementUnlock[]
        {
            new(null, "ACH_KNOWN", true, DateTimeOffset.FromUnixTimeSeconds(1700000000))
        };
        var source = new FakeProgressSource(SteamIdA, generation: 3)
        {
            UnlocksFactory = (_, appId) => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(SteamIdA, appId, 3, entries),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                time.Now,
                time.Now,
                null,
                SteamAchievementDiagnosticCodes.UnlocksUnmatched)
        };
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetUnlocksAsync("acct", 730);

        result.IsSuccess.Should().BeTrue();
        result.Failure.Should().BeNull();
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.UnlocksUnmatched);
        result.Value!.Entries.Should().Equal(entries);
    }

    private static SteamAchievementAppProgressSnapshot Summary(uint appId, int total = 4)
        => new(appId, total, 2, 50.0);

    private static SteamGatewayResult<AchievementProgressBatchSnapshot> BatchSuccess(
        ulong steamId,
        long generation,
        IReadOnlyList<uint> requested,
        IReadOnlyList<uint> covered,
        IReadOnlyList<SteamAchievementAppProgressSnapshot> summaries,
        MutableTimeProvider time)
        => SteamGatewayResult<AchievementProgressBatchSnapshot>.Succeeded(
            new AchievementProgressBatchSnapshot(steamId, generation, requested, covered, summaries),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            time.Now,
            time.Now);

    private static SteamResourceCacheEntry SeedSummaries(
        MemoryCacheStore store,
        ulong steamId,
        uint[] covered,
        SteamAchievementAppProgressSnapshot[] values,
        DateTimeOffset fetchedAt)
    {
        var key = SteamAchievementCacheKeys.ProgressSummary(steamId);
        var payload = JsonSerializer.Serialize(
            new AchievementProgressSummaryCachePayload(steamId, covered, values));
        var entry = SummaryPolicy.CreateEntry(key, payload, SteamDataSource.Cm, fetchedAt, "json-v1");
        store.Put(entry);
        return entry;
    }

    private static AchievementProgressSummaryCachePayload ReadSummaryPayload(SteamResourceCacheEntry entry)
        => JsonSerializer.Deserialize<AchievementProgressSummaryCachePayload>(entry.Payload)!;

    private static SteamAchievementProgressGateway CreateGateway(
        MemoryCacheStore store,
        FakeProgressSource source,
        TimeProvider timeProvider)
        => new(
            store,
            source,
            new SteamRequestCoalescer<SteamCacheKey>(),
            timeProvider,
            NullLogger<SteamAchievementProgressGateway>.Instance);

    private sealed class FakeProgressSource : IAchievementProgressSource
    {
        public FakeProgressSource(ulong steamId, long generation)
            => Identity = new AchievementProgressSessionIdentity(steamId, generation);

        public FakeProgressSource(AchievementProgressSessionIdentity? identity)
            => Identity = identity;

        public AchievementProgressSessionIdentity? Identity { get; set; }

        public Func<string, IReadOnlyList<uint>, SteamGatewayResult<AchievementProgressBatchSnapshot>>?
            SummariesFactory { get; set; }

        public Func<string, uint, SteamGatewayResult<SteamAchievementUnlockSnapshot>>?
            UnlocksFactory { get; set; }

        public Func<Task>? BeforeSummaries { get; set; }

        public int SummaryCalls { get; private set; }

        public int UnlockCalls { get; private set; }

        public List<IReadOnlyList<uint>> SummaryRequests { get; } = [];

        public bool TryGetIdentity(string accountName, out AchievementProgressSessionIdentity identity)
        {
            identity = Identity ?? null!;
            return Identity != null;
        }

        public async Task<SteamGatewayResult<AchievementProgressBatchSnapshot>> GetSummariesAsync(
            string accountName,
            IReadOnlyList<uint> appIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryCalls++;
            SummaryRequests.Add(appIds);
            if (BeforeSummaries != null) await BeforeSummaries();
            return SummariesFactory?.Invoke(accountName, appIds)
                ?? SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                    SteamFailureKind.Offline, "offline");
        }

        public Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
            string accountName,
            uint appId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnlockCalls++;
            return Task.FromResult(
                UnlocksFactory?.Invoke(accountName, appId)
                ?? SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.Offline, "offline"));
        }
    }

    private sealed class MemoryCacheStore : ISteamResourceCacheStore
    {
        private readonly Dictionary<SteamCacheKey, SteamResourceCacheEntry> _entries = new();

        public void Put(SteamResourceCacheEntry entry) => _entries[entry.Key] = entry;

        public SteamResourceCacheEntry? Get(SteamCacheKey key)
            => _entries.TryGetValue(key, out var entry) ? entry : null;

        public Task<SteamResourceCacheEntry?> GetAsync(
            SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Get(key));
        }

        public Task<IReadOnlyList<SteamResourceCacheEntry>> GetByResourceKindAsync(
            string resourceKind,
            int schemaVersion,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SteamResourceCacheEntry> result = _entries.Values
                .Where(entry => entry.Key.ResourceKind == resourceKind
                    && entry.Key.SchemaVersion == schemaVersion)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task UpsertAsync(SteamResourceCacheEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[entry.Key] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpiredAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expired = _entries.Values
                .Where(entry => entry.RetainUntil is { } retainUntil && retainUntil <= now)
                .Take(batchSize)
                .ToArray();
            foreach (var entry in expired) _entries.Remove(entry.Key);
            return Task.FromResult(expired.Length);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;

        public void Advance(TimeSpan delta) => Now += delta;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
