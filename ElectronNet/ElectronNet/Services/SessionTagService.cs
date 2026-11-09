using ElectronNet.Hosting;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ElectronNet.Services.Collectors;
using SteamStat.Core.Circle;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 给会话打自动标签，结果存在 use_app_record.auto_tags。存下来是因为开黑要读好友记录，
/// 十年档案有上百万条，不能每次查询都算；其余标签便宜，每次都重算全部，规则改了也能跟上。
/// 这一列每台设备自己算，不参与同步：好友记录本来就不同步。
/// <para>
/// 开黑判定的同时写 together_record（每局和每位好友在一起多久、其中多少确认同组），友情时间线、契合度、
/// 双人回顾都读它，不用各自再读一遍好友记录。第一次运行时补算全部历史，之后只算最近几天。
/// </para>
/// </summary>
internal sealed class SessionTagService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ISettingsStore settingsStore,
    IInsightCatalog catalog,
    TimeProvider timeProvider,
    ILogger<SessionTagService> logger) : IEventHandler<UsageSessionEnded>, IEventHandler<AppSettingsChanged>, IDisposable
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SessionEndDelay = TimeSpan.FromSeconds(20);
    /// <summary>好友记录可能晚于会话到达，这么多天以内的会话每次重新判断开黑。</summary>
    private const int CoopRecheckDays = 3;
    private const long FriendWindowSeconds = 86400;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _timerLock = new();
    private ITimer? _timer;
    private int? _pauseDays;
    private int _disposed;

    public void StartSchedule() => Schedule(StartDelay);

    public Task HandleAsync(UsageSessionEnded message, CancellationToken cancellationToken)
    {
        Schedule(SessionEndDelay);
        return Task.CompletedTask;
    }

    /// <summary>回坑的判定线是「暂停」天数，改了要重算。</summary>
    public Task HandleAsync(AppSettingsChanged message, CancellationToken cancellationToken)
    {
        if (_pauseDays != null && _pauseDays != message.Settings.Analytics.PauseDays) Schedule(SessionEndDelay);
        return Task.CompletedTask;
    }

    private void Schedule(TimeSpan delay)
    {
        lock (_timerLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _timer?.Dispose();
            _timer = timeProvider.CreateTimer(_ => _ = RunGuardedAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunGuardedAsync()
    {
        try
        {
            await RetagAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Session tagging failed");
        }
    }

    /// <summary>返回改动的记录数。周刊出刊之前也调用，保证标签是新的。</summary>
    internal async Task<int> RetagAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = settingsStore.GetSettings();
            var pauseDays = settings.Analytics?.PauseDays ?? 30;
            _pauseDays = pauseDays;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.UseAppRecordTable.AsNoTracking()
                .Where(record => record.EndTime != null && record.Duration > 0)
                .Select(record => new { record.Id, record.SteamId, record.AppId, record.StartTime, EndTime = record.EndTime!.Value, Duration = record.Duration!.Value, record.IdleDuration, record.AutoTags, record.BuildId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (rows.Count == 0) return 0;

            var candidates = rows
                .Select(row => new TagCandidate(row.Id, row.SteamId, row.AppId, row.StartTime, row.EndTime, row.Duration, row.IdleDuration, row.BuildId))
                .ToList();
            var tags = SessionTagger.Tag(candidates, new SessionTagRules(pauseDays, timeProvider.LocalTimeZone));

            var recheckFrom = now - CoopRecheckDays * 86400L;
            var backfilled = (await CollectorData.ReadAsync<TogetherState>(db, TogetherStateId, cancellationToken).ConfigureAwait(false)).Version >= TogetherVersion;
            bool Recheck(int? autoTags, long startTime) => autoTags == null || startTime >= recheckFrom;
            // 补算历史时旧局也要算一遍 together_record，但已有的开黑标签保留：好友记录可能已经被清掉了
            var pending = rows.Where(row => !backfilled || Recheck(row.AutoTags, row.StartTime)).ToList();
            var from = pending.Count == 0 ? now : pending.Min(row => row.StartTime) - FriendWindowSeconds;
            var friends = pending.Count == 0 ? InsightFriendData.Empty : await LoadFriendPlaysAsync(db, from, cancellationToken).ConfigureAwait(false);
            var groups = pending.Count == 0 ? [] : await LoadGroupsAsync(db, from, cancellationToken).ConfigureAwait(false);
            var appFacts = catalog.GetAppFacts(pending.Select(row => row.AppId).ToHashSet());
            var pendingIds = pending.Select(row => row.Id).ToHashSet();
            var together = TogetherDetector.Detect(
                pending.Select(row => new TogetherSession(row.Id, row.SteamId, row.AppId, row.StartTime, row.EndTime)).ToList(),
                friends,
                groups,
                appId => SessionTagger.CanPlayTogether(appFacts.GetValueOrDefault(appId)?.Tags));

            const int coopBits = (int)(SessionTags.Coop | SessionTags.CoopConfirmed);
            var changes = new List<(int Id, int Tags)>();
            foreach (var row in rows)
            {
                var value = (int)tags[row.Id];
                if (pendingIds.Contains(row.Id))
                {
                    if (!Recheck(row.AutoTags, row.StartTime)) value |= (row.AutoTags ?? 0) & coopBits;
                    if (together.CoopSeconds.GetValueOrDefault(row.Id) >= SessionTagger.MinCoopSeconds) value |= (int)SessionTags.Coop;
                    if (together.ConfirmedSeconds.GetValueOrDefault(row.Id) >= SessionTagger.MinCoopSeconds) value |= (int)(SessionTags.Coop | SessionTags.CoopConfirmed);
                }
                else
                {
                    value |= (row.AutoTags ?? 0) & coopBits;
                }
                if (value != row.AutoTags) changes.Add((row.Id, value));
            }

            await WriteAsync(changes, cancellationToken).ConfigureAwait(false);
            await WriteTogetherAsync(pendingIds, together.Segments, cancellationToken).ConfigureAwait(false);
            if (!backfilled)
            {
                await CollectorData.WriteAsync(db, TogetherStateId, new TogetherState { Version = TogetherVersion }, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Backfilled {Count} together segments", together.Segments.Count);
            }
            if (changes.Count > 0) logger.LogInformation("Tagged {Count} sessions", changes.Count);
            return changes.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<InsightFriendData> LoadFriendPlaysAsync(AppDbContext db, long from, CancellationToken cancellationToken)
    {
        var events = await db.FriendStatusRecordTable.AsNoTracking()
            .Where(record => record.Timestamp >= from && (record.ChangeType == "game" || record.ChangeType == "state"))
            .OrderBy(record => record.Timestamp)
            .Select(record => new { record.FriendSteamId, record.FriendPersonaName, record.ChangeType, record.CurrentValue, record.Timestamp })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return FriendTimeline.Build(events.Select(item => new FriendEventRow(item.FriendSteamId, item.FriendPersonaName, item.ChangeType, null, item.CurrentValue, item.Timestamp)));
    }

    /// <summary>
    /// 大厅、服务器和 Rich Presence 组。RP 记录没有 steam_player_group 时也要读：那说明离开了组，
    /// 不读的话上一个组会一直延续到 12 小时上限。
    /// </summary>
    private static async Task<IReadOnlyList<GroupSpan>> LoadGroupsAsync(AppDbContext db, long from, CancellationToken cancellationToken)
    {
        var observations = await db.PresenceGroupTable.AsNoTracking()
            .Where(row => row.ObservedAt >= from)
            .Select(row => new GroupObservation(row.SteamId, row.AppId, row.Kind, row.GroupKey, row.ObservedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var presence = await db.RichPresenceRecordTable.AsNoTracking()
            .Where(row => row.ObservedAt >= from)
            .Select(row => new { row.SteamId, row.AppId, row.RawValues, row.ObservedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in presence)
        {
            var values = row.RawValues.Contains(PresenceGroupKinds.RichPresenceKey, StringComparison.OrdinalIgnoreCase)
                ? ParseValues(row.RawValues)
                : EmptyValues;
            observations.Add(GroupTimeline.FromRichPresence(row.SteamId, row.AppId, values, row.ObservedAt));
        }
        return GroupTimeline.Build(observations);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyValues = new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> ParseValues(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return EmptyValues;
        }
    }

    /// <summary>先删掉这次重算的局的旧结果，再写新的。</summary>
    private async Task WriteTogetherAsync(IReadOnlySet<int> sessionIds, IReadOnlyList<TogetherSegment> segments, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0) return;
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM together_record WHERE session_id = $id";
            var id = delete.Parameters.Add("$id", SqliteType.Integer);
            foreach (var sessionId in sessionIds)
            {
                id.Value = sessionId;
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO together_record (session_id, steam_id, friend_steam_id, app_id, start_time, end_time, seconds, confirmed_seconds)
                VALUES ($session, $steam, $friend, $app, $start, $end, $seconds, $confirmed)
                """;
            var session = insert.Parameters.Add("$session", SqliteType.Integer);
            var steam = insert.Parameters.Add("$steam", SqliteType.Text);
            var friend = insert.Parameters.Add("$friend", SqliteType.Text);
            var app = insert.Parameters.Add("$app", SqliteType.Integer);
            var start = insert.Parameters.Add("$start", SqliteType.Integer);
            var end = insert.Parameters.Add("$end", SqliteType.Integer);
            var seconds = insert.Parameters.Add("$seconds", SqliteType.Integer);
            var confirmed = insert.Parameters.Add("$confirmed", SqliteType.Integer);
            foreach (var segment in segments)
            {
                session.Value = segment.SessionId;
                steam.Value = segment.SteamId;
                friend.Value = segment.FriendSteamId;
                app.Value = segment.AppId;
                start.Value = segment.Start;
                end.Value = segment.End;
                seconds.Value = segment.Seconds;
                confirmed.Value = segment.ConfirmedSeconds;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>together_record 补算到哪个版本了。判定规则改了就加一，下次启动重算全部历史。</summary>
    internal const string TogetherStateId = "togetherRecords";
    internal const int TogetherVersion = 1;

    internal sealed class TogetherState
    {
        public int Version { get; set; }
    }

    /// <summary>直接写列：不经 EF 跟踪几万个实体，也不碰 updated_at，同步触发器不会把它当成用户修改。</summary>
    private async Task WriteAsync(IReadOnlyList<(int Id, int Tags)> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE use_app_record SET auto_tags = $tags WHERE id = $id";
        var tags = command.Parameters.Add("$tags", SqliteType.Integer);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var change in changes)
        {
            tags.Value = change.Tags;
            id.Value = change.Id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_timerLock) _timer?.Dispose();
        _gate.Dispose();
    }
}
