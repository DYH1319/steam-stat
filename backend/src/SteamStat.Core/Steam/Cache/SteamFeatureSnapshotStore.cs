using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Steam.Cache;

public sealed record SteamCachedSnapshot<T>(
    string AccountName,
    T Value,
    SteamFreshness Freshness,
    DateTimeOffset LastSuccessfulUpdate);

public sealed record SteamResourceStatus(
    string ResourceKind,
    string AccountName,
    SteamDataSource? Source,
    SteamFreshness? Freshness,
    DateTimeOffset? LastSuccessfulUpdate,
    SteamFailureKind? Failure = null,
    string? DiagnosticCode = null);

public sealed class SteamFeatureSnapshotStore(
    ISteamResourceCacheStore cacheStore,
    TimeProvider timeProvider,
    ILogger<SteamFeatureSnapshotStore> logger)
{
    public const string LibraryResourceKind = "library-snapshot";
    public const string FriendsResourceKind = "friends-snapshot";
    private const string PayloadFormat = "json-v1";
    private const string ResourceId = "snapshot";
    private const int SchemaVersion = 1;
    private readonly ConcurrentDictionary<(string ResourceKind, string AccountName), RuntimeStatus> _runtimeStatuses = new();

    public Task SaveLibraryAsync(
        string accountName,
        ulong steamId,
        IReadOnlyList<SteamOwnedGame> games,
        SteamDataSource source,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken = default)
        => SaveAsync(
            LibraryResourceKind,
            SteamResourcePolicies.LibrarySnapshot,
            accountName,
            steamId,
            new LibrarySnapshotDocument(accountName, games),
            source,
            fetchedAt,
            cancellationToken);

    public Task SaveFriendsAsync(
        SteamFriendData data,
        SteamDataSource source,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(data.CurrentUser.SteamId, out var steamId) || steamId == 0)
            throw new ArgumentException("Friends snapshot requires a valid current-user Steam ID.", nameof(data));
        return SaveAsync(
            FriendsResourceKind,
            SteamResourcePolicies.FriendsSnapshot,
            data.AccountName,
            steamId,
            new FriendsSnapshotDocument(data),
            source,
            fetchedAt,
            cancellationToken);
    }

    public async Task<SteamCachedSnapshot<IReadOnlyList<SteamOwnedGame>>?> GetLibraryAsync(
        string accountName,
        CancellationToken cancellationToken = default)
        => (await GetLibrariesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(snapshot => string.Equals(snapshot.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<SteamCachedSnapshot<IReadOnlyList<SteamOwnedGame>>>> GetLibrariesAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await ReadEntriesAsync(
            LibraryResourceKind, SteamResourcePolicies.LibrarySnapshot, cancellationToken).ConfigureAwait(false);
        var snapshots = new List<SteamCachedSnapshot<IReadOnlyList<SteamOwnedGame>>>();
        foreach (var entry in entries)
        {
            var document = Deserialize<LibrarySnapshotDocument>(entry.Entry, SteamResourcePolicies.LibrarySnapshot);
            if (document?.AccountName is not { Length: > 0 }) continue;
            snapshots.Add(new SteamCachedSnapshot<IReadOnlyList<SteamOwnedGame>>(
                document.AccountName,
                document.Games,
                entry.Freshness,
                entry.Entry.FetchedAt));
        }
        return snapshots;
    }

    public async Task<SteamCachedSnapshot<SteamFriendData>?> GetFriendsAsync(
        string accountName,
        CancellationToken cancellationToken = default)
        => (await GetFriendsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(snapshot => string.Equals(snapshot.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<SteamCachedSnapshot<SteamFriendData>>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await ReadEntriesAsync(
            FriendsResourceKind, SteamResourcePolicies.FriendsSnapshot, cancellationToken).ConfigureAwait(false);
        var snapshots = new List<SteamCachedSnapshot<SteamFriendData>>();
        foreach (var entry in entries)
        {
            var document = Deserialize<FriendsSnapshotDocument>(entry.Entry, SteamResourcePolicies.FriendsSnapshot);
            if (document?.Data.AccountName is not { Length: > 0 }) continue;
            snapshots.Add(new SteamCachedSnapshot<SteamFriendData>(
                document.Data.AccountName,
                document.Data,
                entry.Freshness,
                entry.Entry.FetchedAt));
        }
        return snapshots;
    }

    public void ReportResult(
        string resourceKind,
        string accountName,
        SteamDataSource source,
        SteamFreshness freshness,
        DateTimeOffset lastSuccessfulUpdate,
        SteamFailureKind? failure = null,
        string? diagnosticCode = null)
        => _runtimeStatuses[(resourceKind, accountName)] = new RuntimeStatus(
            source, freshness, lastSuccessfulUpdate, failure, diagnosticCode);

    public void ReportFailure(
        string resourceKind,
        string accountName,
        SteamFailureKind failure,
        string diagnosticCode)
        => _runtimeStatuses[(resourceKind, accountName)] = new RuntimeStatus(
            null, null, null, failure, diagnosticCode);

    public async Task<IReadOnlyList<SteamResourceStatus>> GetResourceStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = new Dictionary<(string ResourceKind, string AccountName), SteamResourceStatus>();
        await AddPersistedStatusesAsync(
            statuses, LibraryResourceKind, SteamResourcePolicies.LibrarySnapshot, cancellationToken).ConfigureAwait(false);
        await AddPersistedStatusesAsync(
            statuses, FriendsResourceKind, SteamResourcePolicies.FriendsSnapshot, cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in _runtimeStatuses)
            statuses[key] = new SteamResourceStatus(
                key.ResourceKind,
                key.AccountName,
                value.Source,
                GetRuntimeFreshness(key.ResourceKind, value),
                value.LastSuccessfulUpdate,
                value.Failure,
                value.DiagnosticCode);
        return statuses.Values
            .OrderBy(status => status.ResourceKind, StringComparer.Ordinal)
            .ThenBy(status => status.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SteamFreshness? GetRuntimeFreshness(string resourceKind, RuntimeStatus status)
    {
        if (status.Failure.HasValue || !status.LastSuccessfulUpdate.HasValue) return status.Freshness;
        var policy = resourceKind == LibraryResourceKind
            ? SteamResourcePolicies.LibrarySnapshot
            : SteamResourcePolicies.FriendsSnapshot;
        var age = timeProvider.GetUtcNow() - status.LastSuccessfulUpdate.Value;
        if (age < policy.RefreshInterval) return SteamFreshness.Fresh;
        return age < policy.StaleInterval ? SteamFreshness.Stale : SteamFreshness.Expired;
    }

    private async Task SaveAsync<T>(
        string resourceKind,
        SteamCachePolicy policy,
        string accountName,
        ulong steamId,
        T document,
        SteamDataSource source,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
        var payload = JsonSerializer.Serialize(document);
        if (Encoding.UTF8.GetByteCount(payload) > policy.MaximumPayloadBytes)
            throw new InvalidDataException("Steam feature snapshot exceeds the maximum payload size.");
        var key = SteamCacheKey.Create(
            resourceKind, steamId.ToString(), ResourceId, schemaVersion: SchemaVersion);
        var entry = policy.CreateEntry(key, payload, source, fetchedAt, PayloadFormat);
        await cacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        ReportResult(resourceKind, accountName, source, SteamFreshness.Fresh, fetchedAt);
    }

    private async Task AddPersistedStatusesAsync(
        Dictionary<(string ResourceKind, string AccountName), SteamResourceStatus> statuses,
        string resourceKind,
        SteamCachePolicy policy,
        CancellationToken cancellationToken)
    {
        var entries = await ReadEntriesAsync(resourceKind, policy, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            string? accountName = resourceKind == LibraryResourceKind
                ? Deserialize<LibrarySnapshotDocument>(entry.Entry, policy)?.AccountName
                : Deserialize<FriendsSnapshotDocument>(entry.Entry, policy)?.Data.AccountName;
            if (string.IsNullOrWhiteSpace(accountName)) continue;
            statuses[(resourceKind, accountName)] = new SteamResourceStatus(
                resourceKind,
                accountName,
                SteamDataSource.Sqlite,
                entry.Freshness,
                entry.Entry.FetchedAt);
        }
    }

    private async Task<IReadOnlyList<CachedEntry>> ReadEntriesAsync(
        string resourceKind,
        SteamCachePolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var entries = await cacheStore.GetByResourceKindAsync(
                resourceKind, SchemaVersion, 100, cancellationToken).ConfigureAwait(false);
            return entries.Where(entry => entry.PayloadFormat == PayloadFormat && policy.ShouldRetain(entry, now))
                .Select(entry => new CachedEntry(entry, policy.GetFreshness(entry, now)))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read persisted Steam {ResourceKind}", resourceKind);
            return [];
        }
    }

    private T? Deserialize<T>(SteamResourceCacheEntry entry, SteamCachePolicy policy)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(entry.Payload) > policy.MaximumPayloadBytes) return default;
            return JsonSerializer.Deserialize<T>(entry.Payload);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "Ignored malformed Steam {ResourceKind}", entry.Key.ResourceKind);
            return default;
        }
    }

    private sealed record CachedEntry(SteamResourceCacheEntry Entry, SteamFreshness Freshness);
    private sealed record RuntimeStatus(
        SteamDataSource? Source,
        SteamFreshness? Freshness,
        DateTimeOffset? LastSuccessfulUpdate,
        SteamFailureKind? Failure,
        string? DiagnosticCode);
    private sealed record LibrarySnapshotDocument(string AccountName, IReadOnlyList<SteamOwnedGame> Games);
    private sealed record FriendsSnapshotDocument(SteamFriendData Data);
}
