using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAchievementSchemaGatewayTests
{
    private static readonly SteamCachePolicy Policy = SteamResourcePolicies.AchievementSchema;
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Test]
    public async Task FreshPersistedSchema_SurvivesGatewayRestartWithoutCmCalls()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var snapshot = Snapshot(730, "english", 305419896);
        var onlineSource = new FakeSchemaSource { FullResult = Success(snapshot, time) };
        using (var first = CreateGateway(store, onlineSource, time))
            (await first.GetSchemaAsync(730, "english")).IsSuccess.Should().BeTrue();

        var offlineSource = new FakeSchemaSource();
        using var restarted = CreateGateway(store, offlineSource, time);
        var result = await restarted.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(snapshot);
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        offlineSource.HashCalls.Should().Be(0);
        offlineSource.FullCalls.Should().Be(0);
    }

    [Test]
    public async Task StaleSchema_SameHash_RevalidatesWithoutFullFetch()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var snapshot = Snapshot(730, "english", 42);
        var seeded = SeedPositive(store, snapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));

        var source = new FakeSchemaSource { HashResult = HashSuccess(42, time) };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(snapshot);
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        source.HashCalls.Should().Be(1);
        source.FullCalls.Should().Be(0);

        var entry = store.Get(seeded.Key)!;
        entry.Payload.Should().Be(seeded.Payload);
        entry.ContentHash.Should().Be("42");
        entry.Source.Should().Be(seeded.Source);
        entry.FetchedAt.Should().Be(time.Now);
        entry.RefreshAfter.Should().Be(time.Now + Policy.RefreshInterval);
        entry.RetainUntil.Should().Be(time.Now + Policy.RetentionInterval);
        entry.LastAccessedAt.Should().Be(time.Now);
    }

    [Test]
    public async Task StaleSchema_ChangedHash_FetchesFullAndReplacesEntry()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var oldSnapshot = Snapshot(730, "english", 42, "ACH_OLD");
        var seeded = SeedPositive(store, oldSnapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        var newSnapshot = Snapshot(730, "english", 77, "ACH_NEW");
        var source = new FakeSchemaSource
        {
            HashResult = HashSuccess(77, time),
            FullResult = Success(newSnapshot, time)
        };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(newSnapshot);
        result.Source.Should().Be(SteamDataSource.Cm);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        source.HashCalls.Should().Be(1);
        source.FullCalls.Should().Be(1);

        var entry = store.Get(seeded.Key)!;
        entry.Payload.Should().NotBe(seeded.Payload);
        entry.Payload.Should().Contain("ACH_NEW");
        entry.ContentHash.Should().Be(77u.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task StaleSchema_ChangedHashFullFailure_KeepsOldEntryAndReturnsStaleSuccess()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var oldSnapshot = Snapshot(730, "english", 42, "ACH_OLD");
        var seeded = SeedPositive(store, oldSnapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        var source = new FakeSchemaSource
        {
            HashResult = HashSuccess(77, time),
            FullResult = SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.Transient, "cm_operation_failed")
        };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(oldSnapshot);
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Stale);
        result.Failure.Should().Be(SteamFailureKind.Transient);
        result.DiagnosticCode.Should().Be("cm_operation_failed");
        source.HashCalls.Should().Be(1);
        source.FullCalls.Should().Be(1);

        var entry = store.Get(seeded.Key)!;
        entry.Payload.Should().Be(seeded.Payload);
        entry.ContentHash.Should().Be("42");
        entry.FetchedAt.Should().Be(seeded.FetchedAt);
    }

    [Test]
    public async Task NoCache_FetchesFullWithoutHashRequest()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var snapshot = Snapshot(730, "english", 42);
        var source = new FakeSchemaSource { FullResult = Success(snapshot, time) };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english", "preferred-account");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(snapshot);
        result.Source.Should().Be(SteamDataSource.Cm);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        source.HashCalls.Should().Be(0);
        source.FullCalls.Should().Be(1);

        var entry = store.Get(SteamAchievementCacheKeys.Schema(730, "english"))!;
        entry.Source.Should().Be(SteamDataSource.Cm);
        entry.ContentHash.Should().Be(42u.ToString(CultureInfo.InvariantCulture));
        entry.Payload.Should().NotContain("preferred-account");
        entry.Payload.Should().NotContain("Session");
        entry.Payload.Should().NotContain("Generation");
    }

    [Test]
    public async Task ConcurrentCalls_SamePublicKeyCoalesceAcrossAccounts()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSchemaSource
        {
            FullFactory = (appId, language) => Success(Snapshot(appId, language, 5), time),
            BeforeFull = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        using var gateway = CreateGateway(store, source, time);

        var first = gateway.GetSchemaAsync(730, "english", "account-a");
        await started.Task;
        var second = gateway.GetSchemaAsync(730, "english", "account-b");
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.IsSuccess);
        source.FullCalls.Should().Be(1);
        source.HashCalls.Should().Be(0);
    }

    [Test]
    public async Task ConcurrentCalls_CallerCancellationOnlyCancelsOwnWait()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSchemaSource
        {
            FullFactory = (appId, language) => Success(Snapshot(appId, language, 5), time),
            BeforeFull = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        using var gateway = CreateGateway(store, source, time);
        using var firstCancellation = new CancellationTokenSource();

        var first = gateway.GetSchemaAsync(730, "english", cancellationToken: firstCancellation.Token);
        await started.Task;
        var second = gateway.GetSchemaAsync(730, "english");
        await firstCancellation.CancelAsync();

        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        var secondResult = await second;

        secondResult.IsSuccess.Should().BeTrue();
        secondResult.Source.Should().Be(SteamDataSource.Cm);
        source.FullCalls.Should().Be(1);
        store.Get(SteamAchievementCacheKeys.Schema(730, "english")).Should().NotBeNull();
    }

    [Test]
    public async Task Language_IsNormalizedAndIsolatesCacheKeys()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeSchemaSource
        {
            FullFactory = (appId, language) => Success(Snapshot(appId, language, 1), time)
        };
        using var gateway = CreateGateway(store, source, time);

        var first = await gateway.GetSchemaAsync(730, "  ENGLISH ");
        first.IsSuccess.Should().BeTrue();
        first.Value!.Language.Should().Be("english");
        store.Get(SteamAchievementCacheKeys.Schema(730, "english")).Should().NotBeNull();
        source.FullCalls.Should().Be(1);

        var normalized = await gateway.GetSchemaAsync(730, "english");
        normalized.IsSuccess.Should().BeTrue();
        normalized.Source.Should().Be(SteamDataSource.Sqlite);
        source.FullCalls.Should().Be(1);

        var isolated = await gateway.GetSchemaAsync(730, "Schinese");
        isolated.IsSuccess.Should().BeTrue();
        isolated.Value!.Language.Should().Be("schinese");
        source.FullCalls.Should().Be(2);
    }

    [Test]
    public async Task MalformedCache_IsSafeMissAndOfflineSourceFails()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var key = SteamAchievementCacheKeys.Schema(730, "english");
        store.Put(Policy.CreateEntry(key, "{ malformed", SteamDataSource.Cm, time.Now, "json-v1"));
        var source = new FakeSchemaSource();
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Offline);
        source.HashCalls.Should().Be(0);
        source.FullCalls.Should().Be(1);
    }

    [Test]
    public async Task SemanticMismatchCache_IsSafeMissAndOfflineSourceFails()
    {
        var time = new MutableTimeProvider(BaseTime);
        var key = SteamAchievementCacheKeys.Schema(730, "english");
        var source = new FakeSchemaSource();

        var wrongAppStore = new MemoryCacheStore();
        var wrongAppPayload = JsonSerializer.Serialize(
            new TestCachePayload(Snapshot(999, "english", 42), null, null));
        wrongAppStore.Put(Policy.CreateEntry(
            key, wrongAppPayload, SteamDataSource.Cm, time.Now, "json-v1", contentHash: "42"));
        using (var wrongAppGateway = CreateGateway(wrongAppStore, source, time))
        {
            var wrongAppResult = await wrongAppGateway.GetSchemaAsync(730, "english");
            wrongAppResult.IsSuccess.Should().BeFalse();
            wrongAppResult.Failure.Should().Be(SteamFailureKind.Offline);
        }

        var wrongHashStore = new MemoryCacheStore();
        var validPayload = JsonSerializer.Serialize(
            new TestCachePayload(Snapshot(730, "english", 42), null, null));
        wrongHashStore.Put(Policy.CreateEntry(
            key, validPayload, SteamDataSource.Cm, time.Now, "json-v1", contentHash: "999"));
        using var wrongHashGateway = CreateGateway(wrongHashStore, source, time);
        var wrongHashResult = await wrongHashGateway.GetSchemaAsync(730, "english");

        wrongHashResult.IsSuccess.Should().BeFalse();
        wrongHashResult.Failure.Should().Be(SteamFailureKind.Offline);
        source.FullCalls.Should().Be(2);
        source.HashCalls.Should().Be(0);
    }

    [Test]
    public async Task OversizedCacheEntry_IsSafeMissAndOfflineSourceFails()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var key = SteamAchievementCacheKeys.Schema(730, "english");
        var oversized = new string('x', Policy.MaximumPayloadBytes + 1);
        store.Put(Policy.CreateEntry(key, oversized, SteamDataSource.Cm, time.Now, "json-v1"));
        var source = new FakeSchemaSource();
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Offline);
        source.FullCalls.Should().Be(1);
    }

    [Test]
    public async Task PayloadAtUtf8Limit_IsAccepted_OverLimit_IsSafeMiss()
    {
        var time = new MutableTimeProvider(BaseTime);
        var key = SteamAchievementCacheKeys.Schema(730, "english");
        var payload = JsonSerializer.Serialize(
            new TestCachePayload(Snapshot(730, "english", 42), null, null));
        var padding = Policy.MaximumPayloadBytes - Encoding.UTF8.GetByteCount(payload);
        padding.Should().BePositive();

        var atLimitStore = new MemoryCacheStore();
        atLimitStore.Put(Policy.CreateEntry(
            key, payload + new string(' ', padding), SteamDataSource.Cm, time.Now, "json-v1",
            contentHash: "42"));
        var source = new FakeSchemaSource();
        using (var atLimitGateway = CreateGateway(atLimitStore, source, time))
        {
            var atLimit = await atLimitGateway.GetSchemaAsync(730, "english");
            atLimit.IsSuccess.Should().BeTrue();
            atLimit.Source.Should().Be(SteamDataSource.Sqlite);
        }
        source.FullCalls.Should().Be(0);

        var overLimitStore = new MemoryCacheStore();
        overLimitStore.Put(Policy.CreateEntry(
            key, payload + new string(' ', padding + 1), SteamDataSource.Cm, time.Now, "json-v1",
            contentHash: "42"));
        using var overLimitGateway = CreateGateway(overLimitStore, source, time);
        var overLimit = await overLimitGateway.GetSchemaAsync(730, "english");

        overLimit.IsSuccess.Should().BeFalse();
        overLimit.Failure.Should().Be(SteamFailureKind.Offline);
        source.FullCalls.Should().Be(1);
    }

    [Test]
    public async Task FullPayloadAboveUtf8Limit_ReturnsTooLargeAndPreservesOldEntry()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var oldSnapshot = Snapshot(730, "english", 42, "ACH_OLD");
        var seeded = SeedPositive(store, oldSnapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        var source = new FakeSchemaSource
        {
            HashResult = HashSuccess(77, time),
            FullFactory = (appId, language) => Success(LargeSnapshot(appId, language, 77), time)
        };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(oldSnapshot);
        result.Freshness.Should().Be(SteamFreshness.Stale);
        result.Failure.Should().Be(SteamFailureKind.InvalidData);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaPayloadTooLarge);
        store.Get(seeded.Key)!.Payload.Should().Be(seeded.Payload);
        store.Get(seeded.Key)!.ContentHash.Should().Be("42");

        var emptyStore = new MemoryCacheStore();
        using var emptyGateway = CreateGateway(emptyStore, source, time);
        var noCache = await emptyGateway.GetSchemaAsync(731, "english");

        noCache.IsSuccess.Should().BeFalse();
        noCache.Failure.Should().Be(SteamFailureKind.InvalidData);
        noCache.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaPayloadTooLarge);
        emptyStore.Get(SteamAchievementCacheKeys.Schema(731, "english")).Should().BeNull();
    }

    [Test]
    public async Task FullFetchWriteFailure_PreservesOldEntryAndStillReturnsSuccess()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var oldSnapshot = Snapshot(730, "english", 42, "ACH_OLD");
        var seeded = SeedPositive(store, oldSnapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        var newSnapshot = Snapshot(730, "english", 77, "ACH_NEW");
        store.FailWrites = true;
        var source = new FakeSchemaSource
        {
            HashResult = HashSuccess(77, time),
            FullResult = Success(newSnapshot, time)
        };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(newSnapshot);
        result.Source.Should().Be(SteamDataSource.Cm);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        store.Get(seeded.Key)!.Payload.Should().Be(seeded.Payload);
        store.Get(seeded.Key)!.ContentHash.Should().Be("42");
    }

    [Test]
    public async Task HashRevalidationWriteFailure_ReturnsOldValueFresh()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var oldSnapshot = Snapshot(730, "english", 42);
        var seeded = SeedPositive(store, oldSnapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        store.FailWrites = true;
        var source = new FakeSchemaSource { HashResult = HashSuccess(42, time) };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(oldSnapshot);
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        source.FullCalls.Should().Be(0);
        store.Get(seeded.Key)!.Payload.Should().Be(seeded.Payload);
    }

    [Test]
    public async Task CacheOnly_ReturnsActualFreshnessWithoutSourceCalls()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var snapshot = Snapshot(730, "english", 42);
        SeedPositive(store, snapshot, time.Now);
        var source = new FakeSchemaSource();
        using var gateway = CreateGateway(store, source, time);

        var fresh = await gateway.GetSchemaAsync(730, "english", refreshMode: SteamRefreshMode.CacheOnly);
        fresh.IsSuccess.Should().BeTrue();
        fresh.Source.Should().Be(SteamDataSource.Sqlite);
        fresh.Freshness.Should().Be(SteamFreshness.Fresh);

        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        var stale = await gateway.GetSchemaAsync(730, "english", refreshMode: SteamRefreshMode.CacheOnly);
        stale.IsSuccess.Should().BeTrue();
        stale.Source.Should().Be(SteamDataSource.Sqlite);
        stale.Freshness.Should().Be(SteamFreshness.Stale);

        var miss = await gateway.GetSchemaAsync(999, "english", refreshMode: SteamRefreshMode.CacheOnly);
        miss.IsSuccess.Should().BeFalse();
        miss.Failure.Should().Be(SteamFailureKind.NotFound);
        miss.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaCacheMiss);

        source.HashCalls.Should().Be(0);
        source.FullCalls.Should().Be(0);
    }

    [Test]
    public async Task StaleSessionGenerationFailure_NeverWritesCache()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeSchemaSource
        {
            FullResult = SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.Transient, SteamAchievementDiagnosticCodes.SchemaStaleSessionGeneration)
        };
        using var gateway = CreateGateway(store, source, time);

        var miss = await gateway.GetSchemaAsync(730, "english");

        miss.IsSuccess.Should().BeFalse();
        miss.Failure.Should().Be(SteamFailureKind.Transient);
        miss.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaStaleSessionGeneration);
        store.Get(SteamAchievementCacheKeys.Schema(730, "english")).Should().BeNull();

        var oldSnapshot = Snapshot(731, "english", 42);
        var seeded = SeedPositive(store, oldSnapshot, time.Now);
        time.Advance(Policy.RefreshInterval + TimeSpan.FromHours(1));
        source.HashResult = HashSuccess(77, time);

        var stale = await gateway.GetSchemaAsync(731, "english");

        stale.IsSuccess.Should().BeTrue();
        stale.Value.Should().BeEquivalentTo(oldSnapshot);
        stale.Freshness.Should().Be(SteamFreshness.Stale);
        stale.Failure.Should().Be(SteamFailureKind.Transient);
        stale.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaStaleSessionGeneration);
        var entry = store.Get(seeded.Key)!;
        entry.Payload.Should().Be(seeded.Payload);
        entry.ContentHash.Should().Be("42");
        entry.FetchedAt.Should().Be(seeded.FetchedAt);
    }

    [Test]
    public async Task SemanticallyInvalidFullSnapshot_IsNotWritten()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var duplicate = new SteamAchievementSchemaSnapshot(
            730,
            "english",
            7,
            42,
            [
                new SteamAchievementDefinition(
                    11, "ACH_DUP", "Alpha", "a", "icon.jpg", "icon_gray.jpg",
                    false, null, null, false, SteamAchievementProgressType.None, null, null),
                new SteamAchievementDefinition(
                    12, "ACH_DUP", "Beta", "b", "icon.jpg", "icon_gray.jpg",
                    false, null, null, false, SteamAchievementProgressType.None, null, null)
            ],
            []);
        var source = new FakeSchemaSource { FullResult = Success(duplicate, time) };
        using var gateway = CreateGateway(store, source, time);
        var result = await gateway.GetSchemaAsync(730, "english");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.InvalidData);
        result.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaInvalidPayload);
        source.FullCalls.Should().Be(1);
        store.Get(SteamAchievementCacheKeys.Schema(730, "english")).Should().BeNull();
    }

    [Test]
    public async Task NotFound_IsNegativeCached_OtherFailuresAreNot()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeSchemaSource
        {
            FullResult = SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.NotFound, "steam_not_found")
        };
        using var gateway = CreateGateway(store, source, time);

        var first = await gateway.GetSchemaAsync(731, "english");
        var second = await gateway.GetSchemaAsync(731, "english");

        first.IsSuccess.Should().BeFalse();
        second.IsSuccess.Should().BeFalse();
        second.Failure.Should().Be(SteamFailureKind.NotFound);
        second.Source.Should().Be(SteamDataSource.Sqlite);
        source.FullCalls.Should().Be(1);

        var negativeEntry = store.Get(SteamAchievementCacheKeys.Schema(731, "english"))!;
        negativeEntry.Source.Should().Be(SteamDataSource.Cm);
        using (var document = JsonDocument.Parse(negativeEntry.Payload))
        {
            document.RootElement.GetProperty("Value").ValueKind.Should().Be(JsonValueKind.Null);
            document.RootElement.GetProperty("Failure")
                .GetInt32().Should().Be((int)SteamFailureKind.NotFound);
        }

        var failures = new[]
        {
            SteamFailureKind.AuthenticationRequired,
            SteamFailureKind.Timeout,
            SteamFailureKind.RateLimited
        };
        for (var index = 0; index < failures.Length; index++)
        {
            var appId = (uint)(1000 + index);
            source.FullResult = SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                failures[index], "upstream_failure");
            (await gateway.GetSchemaAsync(appId, "english")).IsSuccess.Should().BeFalse();
            (await gateway.GetSchemaAsync(appId, "english")).IsSuccess.Should().BeFalse();
            store.Get(SteamAchievementCacheKeys.Schema(appId, "english")).Should().BeNull();
        }
        source.FullCalls.Should().Be(1 + failures.Length * 2);
        source.HashCalls.Should().Be(0);
    }

    [Test]
    public async Task InvalidArguments_ReturnTypedFailureOrThrow()
    {
        var time = new MutableTimeProvider(BaseTime);
        var store = new MemoryCacheStore();
        var source = new FakeSchemaSource();
        using var gateway = CreateGateway(store, source, time);

        var zeroApp = await gateway.GetSchemaAsync(0, "english");
        zeroApp.IsSuccess.Should().BeFalse();
        zeroApp.Failure.Should().Be(SteamFailureKind.NotFound);
        zeroApp.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaInvalidPayload);

        var act = () => gateway.GetSchemaAsync(1, "   ");
        await act.Should().ThrowAsync<ArgumentException>();

        source.HashCalls.Should().Be(0);
        source.FullCalls.Should().Be(0);
    }

    private static SteamAchievementSchemaSnapshot Snapshot(
        uint appId, string language, uint hash, string internalName = "ACH_SYNTH")
        => new(
            appId,
            language,
            7,
            hash,
            [
                new SteamAchievementDefinition(
                    11, internalName, "Synthetic", "Synthetic description",
                    "icon.jpg", "icon_gray.jpg", false, 12.5, 1, false,
                    SteamAchievementProgressType.Int, 0, 10)
            ],
            [new SteamAchievementGroup(1, "Synthetic Group", null, false, false, true, 0)]);

    private static SteamAchievementSchemaSnapshot LargeSnapshot(uint appId, string language, uint hash)
    {
        var localizedName = new string('成', 256);
        var definitions = Enumerable.Range(0, 1500)
            .Select(index => new SteamAchievementDefinition(
                (uint)index + 1,
                $"ACH_LARGE_{index}",
                localizedName,
                "大量描述文本",
                "icon.jpg",
                "icon_gray.jpg",
                false,
                null,
                null,
                false,
                SteamAchievementProgressType.None,
                null,
                null))
            .ToArray();
        return new SteamAchievementSchemaSnapshot(appId, language, 7, hash, definitions, []);
    }

    private static SteamResourceCacheEntry SeedPositive(
        MemoryCacheStore store,
        SteamAchievementSchemaSnapshot snapshot,
        DateTimeOffset fetchedAt)
    {
        var key = SteamAchievementCacheKeys.Schema(snapshot.AppId, snapshot.Language);
        var payload = JsonSerializer.Serialize(new TestCachePayload(snapshot, null, null));
        var entry = Policy.CreateEntry(
            key,
            payload,
            SteamDataSource.Cm,
            fetchedAt,
            "json-v1",
            contentHash: snapshot.SchemaHash.ToString(CultureInfo.InvariantCulture));
        store.Put(entry);
        return entry;
    }

    private static SteamGatewayResult<SteamAchievementSchemaSnapshot> Success(
        SteamAchievementSchemaSnapshot snapshot, MutableTimeProvider time)
        => SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
            snapshot,
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            time.Now,
            time.Now + Policy.RefreshInterval);

    private static SteamGatewayResult<uint> HashSuccess(uint hash, MutableTimeProvider time)
        => SteamGatewayResult<uint>.Succeeded(
            hash,
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            time.Now,
            time.Now + Policy.RefreshInterval);

    private static SteamAchievementSchemaGateway CreateGateway(
        ISteamResourceCacheStore store,
        IAchievementSchemaSource source,
        TimeProvider timeProvider)
        => new(
            store,
            source,
            new SteamRequestCoalescer<SteamCacheKey>(),
            timeProvider,
            NullLogger<SteamAchievementSchemaGateway>.Instance);

    private sealed record TestCachePayload(
        SteamAchievementSchemaSnapshot? Value,
        SteamFailureKind? Failure,
        string? DiagnosticCode);

    private sealed class FakeSchemaSource : IAchievementSchemaSource
    {
        public SteamGatewayResult<uint> HashResult { get; set; } =
            SteamGatewayResult<uint>.Failed(SteamFailureKind.Offline, "offline");

        public SteamGatewayResult<SteamAchievementSchemaSnapshot> FullResult { get; set; } =
            SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(SteamFailureKind.Offline, "offline");

        public Func<uint, string, SteamGatewayResult<uint>>? HashFactory { get; set; }

        public Func<uint, string, SteamGatewayResult<SteamAchievementSchemaSnapshot>>? FullFactory { get; set; }

        public Func<Task>? BeforeHash { get; set; }

        public Func<Task>? BeforeFull { get; set; }

        public int HashCalls { get; private set; }

        public int FullCalls { get; private set; }

        public async Task<SteamGatewayResult<uint>> GetHashAsync(
            uint appId,
            string language,
            string? preferredAccountName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashCalls++;
            if (BeforeHash != null) await BeforeHash();
            return HashFactory?.Invoke(appId, language) ?? HashResult;
        }

        public async Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetFullAsync(
            uint appId,
            string language,
            string? preferredAccountName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullCalls++;
            if (BeforeFull != null) await BeforeFull();
            return FullFactory?.Invoke(appId, language) ?? FullResult;
        }
    }

    private sealed class MemoryCacheStore : ISteamResourceCacheStore
    {
        private readonly Dictionary<SteamCacheKey, SteamResourceCacheEntry> _entries = new();

        public bool FailWrites { get; set; }

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
            if (FailWrites) throw new InvalidOperationException("cache write failed");
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
