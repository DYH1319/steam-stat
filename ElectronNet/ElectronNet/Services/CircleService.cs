using System.Globalization;
using ElectronNet.Features.Insights;
using ElectronNet.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Circle;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Services;

/// <summary>
/// 好友与圈子的读取入口。读一次档案算完，缓存两分钟：几个页面和好友档案页都读同一份。
/// <para>
/// 一起玩的结果由 <see cref="SessionTagService"/> 写进 together_record，这里只读；
/// 隐藏游戏从视图（v_together）和这里的过滤两头剔除。
/// </para>
/// </summary>
internal sealed class CircleService(
    InsightService insights,
    SemanticLayerService semanticLayer,
    IInsightCatalog catalog,
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamFeatureSnapshotStore snapshotStore,
    ISteamFriendProfileGateway friendProfiles,
    IAppNameResolver appNames,
    ISettingsStore settingsStore,
    TimeProvider timeProvider,
    ILogger<CircleService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LibraryCacheLifetime = TimeSpan.FromMinutes(10);
    /// <summary>作息和契合度看最近一年，开黑预测看最近八周。</summary>
    internal const int YearDays = 365;
    internal const int ForecastWeeks = 8;
    private const long FriendWindowDays = 400;

    private readonly SemanticInsightDataSource _data = new(semanticLayer);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, SteamGatewayResult<SteamFriendLibrarySnapshot> Result)> _libraries = new(StringComparer.Ordinal);
    private Loaded? _cached;

    internal sealed record Person(string SteamId, string Name, string? AvatarHash, bool IsFriend, bool Online, long LastLogOff, IReadOnlyList<string> AccountNames);

    internal sealed record Loaded(
        string Key,
        DateTimeOffset At,
        InsightArchive Archive,
        /// <summary>视角里的账号（SteamID），第一个是玩得最多的。</summary>
        IReadOnlyList<string> Mine,
        IReadOnlyList<string> AccountNames,
        IReadOnlyDictionary<string, Person> People,
        IReadOnlySet<string> Tracked,
        IReadOnlyDictionary<string, long> OfficialSince,
        IReadOnlyList<InsightFriendChange> Relationship,
        IReadOnlyList<TogetherSegment> Together,
        IReadOnlyList<InsightFriendPlay> Plays,
        IReadOnlyList<InsightFriendOnline> Online,
        IReadOnlySet<int> Hidden,
        long? RecordsSince)
    {
        public string Primary => Mine.Count > 0 ? Mine[0] : string.Empty;
        public bool IsMine(string steamId) => Mine.Contains(steamId);
        public string NameOf(string steamId) => People.TryGetValue(steamId, out var person) && person.Name.Length > 0 ? person.Name : steamId;
        public string? AvatarOf(string steamId) => People.GetValueOrDefault(steamId)?.AvatarHash;
    }

    /// <summary>好友追踪变了、交换卡导入了：下次重新读。</summary>
    public void Invalidate() => _cached = null;

    private IReadOnlyList<string>? ScopeIds(IReadOnlyList<string>? steamIds)
    {
        if (steamIds is { Count: > 0 }) return steamIds;
        var mine = AccountSettings.MineOf(settingsStore.GetSettings());
        return mine.Count > 0 ? mine : null;
    }

    internal async Task<Loaded> LoadAsync(IReadOnlyList<string>? requested, CancellationToken cancellationToken = default)
    {
        var steamIds = ScopeIds(requested);
        var key = string.Join(',', (steamIds ?? []).Order(StringComparer.Ordinal));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_cached is { } cached && cached.Key == key && now - cached.At < CacheLifetime) return cached;
            var started = timeProvider.GetTimestamp();
            var scope = InsightScope.Of(steamIds);
            var nowSeconds = now.ToUnixTimeSeconds();
            var archive = await insights.LoadAsync(scope, nowSeconds - FriendWindowDays * 86400, cancellationToken).ConfigureAwait(false);
            var hidden = (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var users = await db.SteamUserTable.AsNoTracking().Select(user => new { user.SteamId, user.AccountName }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var snapshots = await snapshotStore.GetFriendsAsync(cancellationToken).ConfigureAwait(false);
            var accountToSteamId = users.Where(user => user.AccountName.Length > 0).GroupBy(user => user.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().SteamId, StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in snapshots)
                if (snapshot.Value.CurrentUser.SteamId.Length > 0) accountToSteamId[snapshot.AccountName] = snapshot.Value.CurrentUser.SteamId;
            var accountNames = accountToSteamId.Where(pair => scope.Includes(pair.Value)).Select(pair => pair.Key).ToList();
            var mine = accountNames.Select(name => accountToSteamId[name]).Distinct()
                .OrderByDescending(id => archive.Sessions.Count(session => session.SteamId == id))
                .ThenBy(id => id, StringComparer.Ordinal)
                .ToList();

            var people = new Dictionary<string, Person>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots.Where(snapshot => accountNames.Contains(snapshot.AccountName, StringComparer.OrdinalIgnoreCase)))
                foreach (var friend in snapshot.Value.Friends.Where(friend => friend.Relationship == 3 && friend.SteamId.Length > 0))
                {
                    var existing = people.GetValueOrDefault(friend.SteamId);
                    people[friend.SteamId] = new Person(
                        friend.SteamId,
                        friend.PersonaName.Length > 0 ? friend.PersonaName : existing?.Name ?? string.Empty,
                        friend.AvatarHash.Length > 0 ? friend.AvatarHash : existing?.AvatarHash,
                        true,
                        friend.PersonaState != 0 || (existing?.Online ?? false),
                        Math.Max(friend.LastLogOff, existing?.LastLogOff ?? 0),
                        [.. existing?.AccountNames ?? [], snapshot.AccountName]);
                }

            var tracked = (await db.FriendTrackingTable.AsNoTracking().Select(row => new { row.AccountName, row.FriendSteamId }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(row => accountNames.Contains(row.AccountName, StringComparer.OrdinalIgnoreCase))
                .Select(row => row.FriendSteamId)
                .ToHashSet(StringComparer.Ordinal);
            var since = (await db.FriendSinceTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(row => accountNames.Contains(row.AccountName, StringComparer.OrdinalIgnoreCase))
                .GroupBy(row => row.FriendSteamId)
                .ToDictionary(group => group.Key, group => group.Min(row => row.FriendSince), StringComparer.Ordinal);

            // 好友增删记录很少，全部读；在线和游戏区间只读最近 400 天（洞察档案里已经有了）
            var relationshipRows = await db.FriendStatusRecordTable.AsNoTracking()
                .Where(row => row.ChangeType == SteamFriendsService.RelationshipChangeType && accountNames.Contains(row.AccountName))
                .OrderBy(row => row.Timestamp)
                .Select(row => new { row.FriendSteamId, row.FriendPersonaName, row.ChangeType, row.PreviousValue, row.CurrentValue, row.Timestamp })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var relationship = FriendTimeline.Build(relationshipRows.Select(row => new FriendEventRow(row.FriendSteamId, row.FriendPersonaName, row.ChangeType, row.PreviousValue, row.CurrentValue, row.Timestamp))).Changes;
            var recordsSince = await db.FriendStatusRecordTable.AsNoTracking()
                .Where(row => accountNames.Contains(row.AccountName))
                .Select(row => (long?)row.Timestamp)
                .MinAsync(cancellationToken).ConfigureAwait(false);

            var together = (await _data.QueryAsync(
                    "SELECT session_id, steam_id, friend_steam_id, app_id, start_time, end_time, seconds, confirmed_seconds FROM v_together ORDER BY start_time",
                    new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false))
                .Where(row => scope.Includes(Convert.ToString(row[1], CultureInfo.InvariantCulture)))
                .Select(row => new TogetherSegment(
                    Convert.ToInt64(row[0], CultureInfo.InvariantCulture),
                    Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToInt32(row[3], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row[4], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row[5], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row[6], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row[7], CultureInfo.InvariantCulture)))
                .ToList();

            // 不在好友快照里的人：有快照时不在快照里就是不再是好友；一份快照都没有（没登录过）时不知道，
            // 按记录里最后一次增删判断，追踪过的按还是好友处理。名字用记录里最后一次的
            var haveSnapshots = snapshots.Any(snapshot => accountNames.Contains(snapshot.AccountName, StringComparer.OrdinalIgnoreCase));
            foreach (var group in relationship.Where(change => !people.ContainsKey(change.SteamId)).GroupBy(change => change.SteamId))
            {
                var last = group.MaxBy(change => change.Time)!;
                people[group.Key] = new Person(group.Key, last.Name, null, !haveSnapshots && last.Kind == "added", false, 0, []);
            }
            var recordedNames = archive.Friends.Plays.Select(play => (play.SteamId, play.Name, Time: play.End))
                .Concat(archive.Friends.Online.Select(item => (item.SteamId, item.Name, Time: item.End)))
                .Where(item => item.Name.Length > 0)
                .GroupBy(item => item.SteamId)
                .ToDictionary(group => group.Key, group => group.MaxBy(item => item.Time).Name, StringComparer.Ordinal);
            foreach (var steamId in tracked.Concat(recordedNames.Keys).Distinct().Where(id => !people.ContainsKey(id)))
                people[steamId] = new Person(steamId, recordedNames.GetValueOrDefault(steamId) ?? string.Empty, null, !haveSnapshots, false, 0, []);

            var plays = archive.Friends.Plays.Where(play => !hidden.Contains(play.AppId)).ToList();
            logger.LogInformation("Loaded the circle archive ({People} people, {Together} together segments) in {Elapsed} ms",
                people.Count, together.Count, (int)timeProvider.GetElapsedTime(started).TotalMilliseconds);
            _cached = new Loaded(key, now, archive, mine, accountNames, people, tracked, since, relationship, together, plays, archive.Friends.Online, hidden, recordsSince);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string AppName(Loaded loaded, int appId)
    {
        var owned = loaded.Archive.Owned.FirstOrDefault(game => game.AppId == appId)?.Name;
        if (!string.IsNullOrWhiteSpace(owned) && !AppNameHelper.IsPlaceholder(owned)) return owned;
        var session = loaded.Archive.Sessions.LastOrDefault(item => item.AppId == appId)?.Name;
        if (!string.IsNullOrWhiteSpace(session) && !AppNameHelper.IsPlaceholder(session)) return session;
        var cached = appNames.GetCachedName((uint)appId);
        return AppNameHelper.IsPlaceholder(cached) ? string.Create(CultureInfo.InvariantCulture, $"App {appId}") : cached!;
    }

    private IReadOnlyList<CircleAppNameDto> Names(Loaded loaded, IEnumerable<int> appIds)
        => appIds.Distinct().Select(appId => new CircleAppNameDto { AppId = appId, Name = AppName(loaded, appId) }).ToList();

    #region 一起玩

    public async Task<CircleTogetherDto> GetTogetherAsync(CircleFriendRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var segments = loaded.Together.Where(segment => segment.FriendSteamId == request.FriendSteamId).ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var lobby = await db.PresenceGroupTable.AsNoTracking().Where(row => row.SteamId == request.FriendSteamId).Select(row => (long?)row.ObservedAt).MinAsync(cancellationToken).ConfigureAwait(false);
        var group = await db.RichPresenceRecordTable.AsNoTracking()
            .Where(row => row.SteamId == request.FriendSteamId && row.RawValues.Contains(PresenceGroupKinds.RichPresenceKey))
            .Select(row => (long?)row.ObservedAt).MinAsync(cancellationToken).ConfigureAwait(false);
        return new CircleTogetherDto
        {
            Segments = segments.Select(segment => new CircleTogetherSegmentDto
            {
                SessionId = segment.SessionId,
                FriendSteamId = segment.FriendSteamId,
                AppId = segment.AppId,
                Name = AppName(loaded, segment.AppId),
                Start = segment.Start,
                End = segment.End,
                Seconds = segment.Seconds,
                ConfirmedSeconds = segment.ConfirmedSeconds
            }).ToList(),
            GroupDataSince = new[] { lobby, group }.Where(value => value != null).Min()
        };
    }

    #endregion

    #region 友情时间线

    public async Task<FriendshipDto> GetFriendshipAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var report = Friendship.Build(
            loaded.People.Values.Select(person => new CircleFriend(person.SteamId, person.Name, person.IsFriend, loaded.Tracked.Contains(person.SteamId), person.LastLogOff, person.Online)).ToList(),
            loaded.OfficialSince,
            loaded.Relationship,
            loaded.Together,
            loaded.Archive.Options,
            loaded.Archive.Now);
        var appIds = report.Bonds.Select(bond => bond.FirstTogether?.AppId).Concat(report.Events.Select(item => item.AppId)).OfType<int>().Concat(report.Bonds.SelectMany(bond => bond.TopApps));
        return new FriendshipDto
        {
            Bonds = report.Bonds.Select(bond => new FriendBondDto
            {
                SteamId = bond.SteamId,
                Name = bond.Name.Length > 0 ? bond.Name : loaded.NameOf(bond.SteamId),
                AvatarHash = loaded.AvatarOf(bond.SteamId),
                IsFriend = bond.IsFriend,
                Tracked = bond.Tracked,
                Since = bond.Since,
                SinceEvidence = bond.SinceEvidence,
                RemovedAt = bond.RemovedAt,
                TogetherSeconds = bond.TogetherSeconds,
                ConfirmedSeconds = bond.ConfirmedSeconds,
                TogetherCount = bond.TogetherCount,
                FirstTogetherAt = bond.FirstTogether?.Time,
                FirstTogetherAppId = bond.FirstTogether?.AppId,
                FirstTogetherConfirmed = bond.FirstTogether?.Confirmed ?? false,
                LastTogether = bond.LastTogether,
                Years = bond.Years.Select(year => new FriendBondYearDto { Year = year.Year, Seconds = year.Seconds }).ToList(),
                PeakYear = bond.PeakYear,
                NextAnniversary = bond.NextAnniversary,
                NextAnniversaryYears = bond.NextAnniversaryYears,
                LastLogOff = bond.LastLogOff,
                Status = bond.Status,
                TopApps = bond.TopApps
            }).ToList(),
            Events = report.Events.Select(item => new FriendshipEventDto
            {
                Kind = item.Kind,
                SteamId = item.SteamId,
                Name = item.Name.Length > 0 ? item.Name : loaded.NameOf(item.SteamId),
                Time = item.Time,
                AppId = item.AppId,
                Value = item.Value,
                Confirmed = item.Confirmed,
                Evidence = item.Evidence
            }).ToList(),
            Names = Names(loaded, appIds),
            OfficialSince = loaded.OfficialSince.Count > 0,
            RecordsSince = loaded.RecordsSince
        };
    }

    #endregion

    #region 契合度

    internal double[] MySchedule(Loaded loaded, long from, long to)
        => WeekHours.From(loaded.Archive.Sessions.Select(session => new Span(session.Start, session.End)), loaded.Archive.Options, from, to);

    internal async Task<Dictionary<string, Dictionary<int, int>>> FriendMinutesAsync(Loaded loaded, IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = steamIds.ToList();
        var rows = await db.FriendPlaytimeSnapshotTable.AsNoTracking().Where(row => ids.Contains(row.SteamId))
            .Select(row => new PlaytimeObservation(row.SteamId, row.AppId, row.PlaytimeMinutes, row.ObservedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(row => !loaded.Hidden.Contains(row.AppId)).GroupBy(row => row.SteamId)
            .ToDictionary(group => group.Key, group => Leaderboard.Latest(group).ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
    }

    internal static Dictionary<int, int> MyMinutes(Loaded loaded)
    {
        var minutes = loaded.Archive.Owned.GroupBy(game => game.AppId).ToDictionary(group => group.Key, group => group.Max(game => game.PlaytimeMinutes));
        foreach (var group in loaded.Archive.Sessions.GroupBy(session => session.AppId))
            minutes[group.Key] = Math.Max(minutes.GetValueOrDefault(group.Key), (int)(group.Sum(session => session.Seconds) / 60));
        return minutes;
    }

    public async Task<CompatibilityReportDto> GetCompatibilityAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var now = loaded.Archive.Now;
        var from = now - YearDays * 86400L;
        var mySchedule = MySchedule(loaded, from, now);
        var myMinutes = MyMinutes(loaded);
        var friends = loaded.Tracked.Where(id => !loaded.IsMine(id)).ToList();
        var theirMinutes = await FriendMinutesAsync(loaded, friends, cancellationToken).ConfigureAwait(false);
        var appIds = myMinutes.Keys.Concat(theirMinutes.Values.SelectMany(map => map.Keys)).ToHashSet();
        var facts = catalog.GetAppFacts(appIds);
        var results = new List<CompatibilityDto>();
        foreach (var friend in friends)
        {
            var schedule = WeekHours.From(loaded.Plays.Where(play => play.SteamId == friend).Select(play => new Span(play.Start, play.End)), loaded.Archive.Options, from, now);
            var together = loaded.Together.Where(segment => segment.FriendSteamId == friend && segment.End >= from).Sum(segment => segment.Seconds);
            var score = Compatibility.Score(new CompatibilityInput(mySchedule, schedule, myMinutes, theirMinutes.GetValueOrDefault(friend) ?? [], facts, together));
            results.Add(new CompatibilityDto
            {
                SteamId = friend,
                Name = loaded.NameOf(friend),
                AvatarHash = loaded.AvatarOf(friend),
                Overall = Round(score.Overall),
                Schedule = Round(score.Schedule),
                Taste = Round(score.Taste),
                Together = Round(score.Together),
                MyHours = score.MyHours,
                TheirHours = score.TheirHours,
                MyGames = score.MyGames,
                TheirGames = score.TheirGames,
                SharedGames = score.SharedGames,
                TopShared = score.TopShared,
                BestCell = score.BestCell,
                TogetherSeconds = together
            });
        }
        return new CompatibilityReportDto
        {
            Friends = results.OrderByDescending(item => item.Overall ?? -1).ThenByDescending(item => item.TogetherSeconds).ToList(),
            Names = Names(loaded, results.SelectMany(item => item.TopShared)),
            WeekStart = loaded.Archive.Options.WeekStart
        };
    }

    private static double? Round(double? value) => value is { } number ? Math.Round(number, 3) : null;

    #endregion

    #region 开黑助手

    /// <summary>
    /// 应用能看到好友状态的时间段。这张表是这次才加的，之前的时间按「这个账号那天有好友记录」算整天都在观测：
    /// 应用开着才会有好友记录，反过来那天没开就没有。
    /// </summary>
    internal async Task<IReadOnlyList<Span>> ObservedAsync(Loaded loaded, long from, long to, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var names = loaded.AccountNames.ToList();
        var windows = await db.FriendObservationWindowTable.AsNoTracking()
            .Where(row => names.Contains(row.AccountName) && row.EndedAt >= from && row.StartedAt <= to)
            .Select(row => new { row.StartedAt, row.EndedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var spans = windows.Select(row => new Span(row.StartedAt, row.EndedAt)).ToList();
        var firstWindow = await db.FriendObservationWindowTable.AsNoTracking().Where(row => names.Contains(row.AccountName))
            .Select(row => (long?)row.StartedAt).MinAsync(cancellationToken).ConfigureAwait(false) ?? to;
        if (firstWindow > from)
        {
            var until = Math.Min(firstWindow, to);
            var days = await db.FriendStatusRecordTable.AsNoTracking()
                .Where(row => names.Contains(row.AccountName) && row.Timestamp >= from && row.Timestamp < until)
                .Select(row => row.Timestamp / 86400)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            spans.AddRange(days.Select(day => new Span(Math.Max(from, day * 86400), Math.Min(until, (day + 1) * 86400))));
        }
        return Spans.Union(spans);
    }

    private async Task<SteamGatewayResult<SteamFriendLibrarySnapshot>?> LibraryAsync(Loaded loaded, string steamId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_libraries)
            if (_libraries.TryGetValue(steamId, out var cached) && now - cached.At < LibraryCacheLifetime) return cached.Result;
        var accounts = loaded.People.GetValueOrDefault(steamId)?.AccountNames ?? [];
        if (accounts.Count == 0 || !ulong.TryParse(steamId, out var id)) return null;
        SteamGatewayResult<SteamFriendLibrarySnapshot>? result = null;
        foreach (var account in accounts)
        {
            result = await friendProfiles.GetLibraryAsync(account, id, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess || result.Failure == SteamFailureKind.Forbidden) break;
        }
        if (result != null) lock (_libraries) _libraries[steamId] = (now, result);
        return result;
    }

    public async Task<PartyPlanDto> PlanPartyAsync(PartyPlanRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var options = loaded.Archive.Options;
        var now = loaded.Archive.Now;
        var from = now - ForecastWeeks * 7 * 86400L;
        var friendIds = request.FriendSteamIds.Where(id => id.Length is > 0 and <= 20 && !loaded.IsMine(id)).Distinct().Take(16).ToList();

        var members = new List<PartyMember>();
        var dtos = new List<PartyMemberDto>();

        var myLibrary = loaded.Archive.Owned.GroupBy(game => game.AppId).ToDictionary(group => group.Key, group =>
        {
            var lastPlayed = group.Max(game => game.LastPlayed);
            return new PartyOwnership(group.Max(game => game.PlaytimeMinutes), now - lastPlayed < 14 * 86400 ? 1 : 0, lastPlayed);
        });
        var mySessions = loaded.Archive.Sessions.Select(session => new Span(session.Start, session.End));
        var myForecast = loaded.Archive.Sessions.Any(session => session.End >= from)
            ? PresenceForecast.Build(mySessions, [new Span(from, now)], options, from, now)
            : null;
        members.Add(new PartyMember(loaded.Primary, true, myLibrary.Count > 0 ? myLibrary : null, myForecast));
        dtos.Add(new PartyMemberDto
        {
            SteamId = loaded.Primary,
            Name = await MyNameAsync(loaded, cancellationToken).ConfigureAwait(false),
            IsMe = true,
            Library = myLibrary.Count > 0 ? "ok" : "failed",
            Games = myLibrary.Count,
            HasForecast = myForecast != null,
            Tracked = false
        });

        var observed = await ObservedAsync(loaded, from, now, cancellationToken).ConfigureAwait(false);
        var stored = await StoredLibrariesAsync(loaded, friendIds, cancellationToken).ConfigureAwait(false);
        foreach (var friend in friendIds)
        {
            var result = await LibraryAsync(loaded, friend, cancellationToken).ConfigureAwait(false);
            Dictionary<int, PartyOwnership>? library = null;
            var state = result switch
            {
                null => "signedOut",
                { IsSuccess: true } => "ok",
                { Failure: SteamFailureKind.Forbidden } => "private",
                { Failure: SteamFailureKind.AuthenticationRequired } => "signedOut",
                _ => "failed"
            };
            if (result is { IsSuccess: true, Value: { } snapshot })
                library = snapshot.Games.Where(game => !loaded.Hidden.Contains(game.AppId)).DistinctBy(game => game.AppId)
                    .ToDictionary(game => game.AppId, game => new PartyOwnership(game.PlaytimeForever, game.Playtime2Weeks, game.RtimeLastPlayed));
            // 读不到时用手动追踪留下的游戏库：几天前的总比没有好
            else if (stored.TryGetValue(friend, out var cached) && cached.Count > 0)
                library = cached;

            double?[]? forecast = null;
            if (loaded.Tracked.Contains(friend))
            {
                var online = loaded.Online.Where(item => item.SteamId == friend).Select(item => new Span(item.Start, item.End)).ToList();
                var cells = PresenceForecast.Build(online, observed, options, from, now);
                if (online.Count > 0 && cells.Any(cell => cell != null)) forecast = cells;
            }
            members.Add(new PartyMember(friend, false, library, forecast));
            dtos.Add(new PartyMemberDto
            {
                SteamId = friend,
                Name = loaded.NameOf(friend),
                AvatarHash = loaded.AvatarOf(friend),
                IsMe = false,
                Library = library != null && state != "ok" ? "ok" : state,
                Games = library?.Count ?? 0,
                HasForecast = forecast != null,
                Tracked = loaded.Tracked.Contains(friend)
            });
        }

        var appIds = members.Where(member => member.Library != null).SelectMany(member => member.Library!.Keys).ToHashSet();
        var plan = PartyPlanner.Plan(members, catalog.GetAppFacts(appIds));
        PartyGameDto Map(PartyGame game) => new()
        {
            AppId = game.AppId,
            Name = AppName(loaded, game.AppId),
            Verified = game.Verified,
            Owners = game.Owners,
            Missing = game.Missing,
            Minutes = game.Minutes.Select(pair => new PartyMinutesDto { SteamId = pair.Key, Minutes = pair.Value }).ToList(),
            RecentPlayers = game.RecentPlayers
        };
        return new PartyPlanDto
        {
            Members = dtos,
            Common = plan.Common.Select(Map).ToList(),
            NearMiss = plan.NearMiss.Select(Map).ToList(),
            Slots = plan.Slots.Select(slot => new PartySlotDto { Weekday = slot.Weekday, StartHour = slot.StartHour, EndHour = slot.EndHour, Probability = slot.Probability, Expected = slot.Expected }).ToList(),
            Together = plan.Together.Select(value => value ?? -1).ToList(),
            Expected = plan.Expected,
            WeekStart = options.WeekStart,
            Weeks = ForecastWeeks
        };
    }

    /// <summary>手动追踪的好友的游戏库（入库档案 + 时长快照）。</summary>
    private async Task<Dictionary<string, Dictionary<int, PartyOwnership>>> StoredLibrariesAsync(Loaded loaded, IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = steamIds.Where(loaded.Tracked.Contains).ToList();
        var items = await db.FriendLibraryItemTable.AsNoTracking().Where(row => ids.Contains(row.SteamId) && row.RemovedAt == null)
            .Select(row => new { row.SteamId, row.AppId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var minutes = await FriendMinutesAsync(loaded, ids, cancellationToken).ConfigureAwait(false);
        return items.Where(row => !loaded.Hidden.Contains(row.AppId)).GroupBy(row => row.SteamId).ToDictionary(
            group => group.Key,
            group => group.ToDictionary(row => row.AppId, row => new PartyOwnership(minutes.GetValueOrDefault(group.Key)?.GetValueOrDefault(row.AppId) ?? 0, 0, 0)),
            StringComparer.Ordinal);
    }

    internal async Task<string> MyNameAsync(Loaded loaded, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var users = await db.SteamUserTable.AsNoTracking().Select(user => new { user.SteamId, user.PersonaName, user.AccountName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var user = users.FirstOrDefault(item => item.SteamId == loaded.Primary);
        return user?.PersonaName is { Length: > 0 } name ? name : user?.AccountName ?? loaded.Primary;
    }

    #endregion

    #region 排行榜

    public async Task<LeaderboardDto> GetLeaderboardAsync(LeaderboardRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var now = loaded.Archive.Now;
        var archive = loaded.Archive;
        int Window(long days)
        {
            var from = now - days * 86400;
            return (int)(archive.Sessions.Sum(session => Math.Max(0, Math.Min(session.End, now) - Math.Max(session.Start, from))) / 60);
        }
        var myMinutes = MyMinutes(loaded);
        var me = new BoardEntry(loaded.Primary, BoardSources.Me, now, Window(7), Window(30), myMinutes.Values.Sum(), myMinutes.Count(pair => pair.Value > 0), archive.Unlocks.Count, null);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tracked = loaded.Tracked.Where(id => !loaded.IsMine(id)).ToList();
        var snapshotRows = await db.FriendPlaytimeSnapshotTable.AsNoTracking().Where(row => tracked.Contains(row.SteamId))
            .Select(row => new PlaytimeObservation(row.SteamId, row.AppId, row.PlaytimeMinutes, row.ObservedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = snapshotRows.Where(row => !loaded.Hidden.Contains(row.AppId)).GroupBy(row => row.SteamId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PlaytimeObservation>)group.ToList(), StringComparer.Ordinal);
        var achievements = (await db.FriendAchievementProgressTable.AsNoTracking().Where(row => tracked.Contains(row.SteamId))
                .Select(row => new { row.SteamId, row.AppId, row.Unlocked, row.ObservedAt }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.SteamId)
            .ToDictionary(group => group.Key, group => group.GroupBy(row => row.AppId).Sum(app => app.MaxBy(row => row.ObservedAt)!.Unlocked), StringComparer.Ordinal);
        var cards = (await ExchangeCardService.LoadCardsAsync(db, cancellationToken).ConfigureAwait(false))
            .Where(card => !loaded.IsMine(card.SteamId))
            .Select(card => new CardNumbers(card.SteamId, card.GeneratedAt, card.Totals.Last7Minutes, card.Totals.Last30Minutes, card.Totals.LifetimeMinutes, card.Totals.PlayedGames, card.Totals.Achievements,
                card.Games.Where(game => !loaded.Hidden.Contains(game.AppId)).ToDictionary(game => game.AppId, game => game.Minutes)))
            .ToList();
        var cardNames = cards.Count == 0 ? new Dictionary<string, string>() : (await db.ExchangeCardTable.AsNoTracking().Select(row => new { row.SenderSteamId, row.SenderName, row.GeneratedAt }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.SenderSteamId).ToDictionary(group => group.Key, group => group.MaxBy(row => row.GeneratedAt)!.SenderName);
        string NameOf(string steamId) => loaded.People.ContainsKey(steamId) ? loaded.NameOf(steamId) : cardNames.GetValueOrDefault(steamId) ?? steamId;
        var myName = await MyNameAsync(loaded, cancellationToken).ConfigureAwait(false);

        var entries = Leaderboard.Build(me, snapshots, achievements, cards, now);

        // 至少两个人有时长的游戏才值得比
        var players = new Dictionary<int, int>();
        void Count(IEnumerable<int> appIds)
        {
            foreach (var appId in appIds.Distinct()) players[appId] = players.GetValueOrDefault(appId) + 1;
        }
        Count(myMinutes.Where(pair => pair.Value > 0 && !loaded.Hidden.Contains(pair.Key)).Select(pair => pair.Key));
        foreach (var rows in snapshots.Values) Count(Leaderboard.Latest(rows).Where(pair => pair.Value > 0).Select(pair => pair.Key));
        foreach (var card in cards.Where(card => !snapshots.ContainsKey(card.SteamId))) Count(card.GameMinutes.Where(pair => pair.Value > 0).Select(pair => pair.Key));
        var games = players.Where(pair => pair.Value >= 2).OrderByDescending(pair => pair.Value).ThenByDescending(pair => myMinutes.GetValueOrDefault(pair.Key)).Take(100).Select(pair => pair.Key).ToList();
        var appId = request.AppId is { } selected && games.Contains(selected) ? selected : games.FirstOrDefault();
        IReadOnlyList<GameBoardEntry> game = [];
        if (appId > 0)
        {
            var mine = myMinutes.GetValueOrDefault(appId) is var minutes and > 0
                ? new GameBoardEntry(loaded.Primary, BoardSources.Me, now, minutes, (int)(archive.Sessions.Where(session => session.AppId == appId).Sum(session => Math.Max(0, session.End - Math.Max(session.Start, now - 30 * 86400L))) / 60))
                : null;
            game = Leaderboard.ForGame(appId, mine, snapshots, cards, now);
        }

        return new LeaderboardDto
        {
            Entries = entries.Select(entry => new BoardEntryDto
            {
                SteamId = entry.SteamId,
                Name = entry.Source == BoardSources.Me ? myName : NameOf(entry.SteamId),
                AvatarHash = loaded.AvatarOf(entry.SteamId),
                IsMe = entry.Source == BoardSources.Me,
                Source = entry.Source,
                AsOf = entry.AsOf,
                Last7 = entry.Last7,
                Last30 = entry.Last30,
                Lifetime = entry.Lifetime,
                Games = entry.Games,
                Achievements = entry.Achievements,
                ObservedSince = entry.ObservedSince
            }).ToList(),
            Games = Names(loaded, games),
            AppId = appId > 0 ? appId : null,
            Game = game.Select(entry => new GameBoardEntryDto
            {
                SteamId = entry.SteamId,
                Name = entry.Source == BoardSources.Me ? myName : NameOf(entry.SteamId),
                AvatarHash = loaded.AvatarOf(entry.SteamId),
                IsMe = entry.Source == BoardSources.Me,
                Source = entry.Source,
                AsOf = entry.AsOf,
                Minutes = entry.Minutes,
                Last30 = entry.Last30
            }).ToList(),
            TrackedFriends = tracked.Count,
            Cards = cards.Select(card => card.SteamId).Distinct().Count()
        };
    }

    #endregion

    #region 入坑传播

    public async Task<AdoptionDto> GetAdoptionAsync(AdoptionRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var (evidence, activity) = await AdoptionInputAsync(loaded, cancellationToken).ConfigureAwait(false);
        var me = loaded.Primary;
        var chains = Adoption.Overview(evidence, activity, me);
        AdoptionChain? selected = null;
        if (request.AppId is { } appId && !loaded.Hidden.Contains(appId))
            selected = Adoption.Chain(appId, evidence, activity, me);
        var myName = await MyNameAsync(loaded, cancellationToken).ConfigureAwait(false);
        AdoptionChainDto Map(AdoptionChain chain) => new()
        {
            AppId = chain.AppId,
            Name = AppName(loaded, chain.AppId),
            MyRank = chain.MyRank,
            Adopters = chain.Adopters.Select(adopter => new AdopterDto
            {
                SteamId = adopter.SteamId,
                Name = adopter.SteamId == me ? myName : loaded.NameOf(adopter.SteamId),
                AvatarHash = loaded.AvatarOf(adopter.SteamId),
                IsMe = adopter.SteamId == me,
                Time = adopter.Time,
                Evidence = adopter.Evidence,
                InfluencedBy = adopter.InfluencedBy,
                InfluenceSeconds = adopter.InfluenceSeconds
            }).ToList()
        };
        return new AdoptionDto
        {
            Chains = chains.Select(Map).ToList(),
            Selected = selected is { Count: > 0 } ? Map(selected) : null,
            TrackedFriends = loaded.Tracked.Count(id => !loaded.IsMine(id))
        };
    }

    /// <summary>
    /// 入坑证据。我：第一局，官方时长比记下来的多一小时以上说明开始记录前就玩过。
    /// 好友（手动追踪）：时长快照里第一次出现时长；第一次采集就有的算「早于记录」；
    /// 快照开始之后第一次看到在玩、且之前的快照里没有它，算看到的时间。
    /// </summary>
    internal async Task<(IReadOnlyList<AdoptionEvidence> Evidence, IReadOnlyList<ActivitySpan> Activity)> AdoptionInputAsync(Loaded loaded, CancellationToken cancellationToken)
    {
        var me = loaded.Primary;
        var evidence = new List<AdoptionEvidence>();
        var archive = loaded.Archive;
        var owned = archive.Owned.GroupBy(game => game.AppId).ToDictionary(group => group.Key, group => group.Max(game => game.PlaytimeMinutes));
        foreach (var group in archive.Sessions.GroupBy(session => session.AppId))
        {
            var first = group.Min(session => session.Start);
            var recorded = group.Sum(session => session.Seconds) / 60;
            var before = owned.GetValueOrDefault(group.Key) > recorded + 60;
            evidence.Add(new AdoptionEvidence(me, group.Key, first, before ? AdoptionEvidenceKinds.Before : AdoptionEvidenceKinds.Recorded));
        }

        var tracked = loaded.Tracked.Where(id => !loaded.IsMine(id)).ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.FriendPlaytimeSnapshotTable.AsNoTracking().Where(row => tracked.Contains(row.SteamId) && row.PlaytimeMinutes > 0)
            .Select(row => new { row.SteamId, row.AppId, row.ObservedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var baseline = rows.GroupBy(row => row.SteamId).ToDictionary(group => group.Key, group => group.Min(row => row.ObservedAt));
        var firstSnapshot = rows.GroupBy(row => (row.SteamId, row.AppId)).ToDictionary(group => group.Key, group => group.Min(row => row.ObservedAt));
        foreach (var ((steamId, appId), time) in firstSnapshot)
            evidence.Add(new AdoptionEvidence(steamId, appId, time, time <= baseline[steamId] ? AdoptionEvidenceKinds.Before : AdoptionEvidenceKinds.Inferred));

        var firstPlays = loaded.Plays.Where(play => tracked.Contains(play.SteamId))
            .GroupBy(play => (play.SteamId, play.AppId))
            .Select(group => (group.Key.SteamId, group.Key.AppId, Time: group.Min(play => play.Start)));
        foreach (var (steamId, appId, time) in firstPlays)
        {
            // 快照开始之后、且第一次有时长不早于这次：这一次就是第一次玩
            var known = baseline.TryGetValue(steamId, out var start) && time > start
                        && (!firstSnapshot.TryGetValue((steamId, appId), out var snapshot) || snapshot >= time - 86400);
            evidence.Add(new AdoptionEvidence(steamId, appId, time, known ? AdoptionEvidenceKinds.Recorded : AdoptionEvidenceKinds.Before));
        }

        var activity = loaded.Plays.Where(play => tracked.Contains(play.SteamId)).Select(play => new ActivitySpan(play.SteamId, play.AppId, play.Start, play.End))
            .Concat(archive.Sessions.Select(session => new ActivitySpan(me, session.AppId, session.Start, session.End)))
            .ToList();
        return (evidence.Where(item => !loaded.Hidden.Contains(item.AppId)).ToList(), activity);
    }

    #endregion

    #region 好友入库档案

    public async Task<FriendLibraryDto> GetLibraryEventsAsync(CircleFilterRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var tracked = loaded.Tracked.Where(id => !loaded.IsMine(id) && (request.FriendSteamId == null || id == request.FriendSteamId)).ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var items = (await db.FriendLibraryItemTable.AsNoTracking().Where(row => tracked.Contains(row.SteamId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => !loaded.Hidden.Contains(row.AppId))
            .ToList();
        var minutes = await FriendMinutesAsync(loaded, tracked, cancellationToken).ConfigureAwait(false);
        var owned = loaded.Archive.Owned.Select(game => game.AppId).ToHashSet();
        var events = new List<FriendLibraryEventDto>();
        foreach (var item in items)
        {
            if (!item.IsBaseline) events.Add(Event(item, "added", item.FirstSeenAt));
            if (item.RemovedAt is { } removed) events.Add(Event(item, "removed", removed));
        }
        FriendLibraryEventDto Event(Models.FriendLibraryItem item, string kind, long time) => new()
        {
            SteamId = item.SteamId,
            Name = loaded.NameOf(item.SteamId),
            AppId = item.AppId,
            AppName = AppName(loaded, item.AppId),
            Kind = kind,
            Time = time,
            Owned = owned.Contains(item.AppId),
            Minutes = minutes.GetValueOrDefault(item.SteamId)?.GetValueOrDefault(item.AppId)
        };
        var baselines = items.Where(item => item.IsBaseline).ToList();
        return new FriendLibraryDto
        {
            Events = events.OrderByDescending(item => item.Time).Take(2000).ToList(),
            BaselineAt = baselines.Count == 0 ? null : baselines.Min(item => item.FirstSeenAt),
            BaselineGames = baselines.Count,
            TrackedFriends = tracked.Count
        };
    }

    #endregion

    #region 成就竞速

    public async Task<AchievementRaceDto> GetAchievementRaceAsync(CircleFriendRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var now = loaded.Archive.Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var history = (await db.FriendAchievementProgressTable.AsNoTracking().Where(row => row.SteamId == request.FriendSteamId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => !loaded.Hidden.Contains(row.AppId))
            .ToList();
        var mine = loaded.Archive.Progress.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.MaxBy(item => item.Unlocked)!);
        var myUnlocks = loaded.Archive.Unlocks.ToLookup(unlock => unlock.AppId);
        var rows = new List<AchievementRaceRowDto>();
        foreach (var app in history.GroupBy(row => row.AppId))
        {
            if (!mine.TryGetValue(app.Key, out var my) || my.Total <= 0) continue;
            var ordered = app.OrderBy(row => row.ObservedAt).ToList();
            var latest = ordered[^1];
            var times = myUnlocks[app.Key].Select(unlock => unlock.Time).Order().ToList();
            int MineAt(long time) => times.Count(value => value <= time);

            var windowStart = now - 30 * 86400L;
            var before = ordered.LastOrDefault(row => row.ObservedAt <= windowStart);
            var recent = before != null ? latest.Unlocked - before.Unlocked : ordered[0].ObservedAt <= windowStart ? 0 : latest.Unlocked - ordered[0].Unlocked;

            long? overtaken = null;
            for (var index = 1; index < ordered.Count; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (previous.Unlocked <= MineAt(previous.ObservedAt) && current.Unlocked > MineAt(current.ObservedAt)) overtaken = current.ObservedAt;
            }
            rows.Add(new AchievementRaceRowDto
            {
                AppId = app.Key,
                Name = AppName(loaded, app.Key),
                MyUnlocked = my.Unlocked,
                MyTotal = my.Total,
                TheirUnlocked = latest.Unlocked,
                TheirTotal = latest.Total,
                Leader = my.Unlocked > latest.Unlocked ? "me" : my.Unlocked < latest.Unlocked ? "them" : "tie",
                TheirRecent = Math.Max(0, recent),
                MyRecent = times.Count(value => value >= windowStart),
                TheirChangedAt = ordered.Count > 1 ? latest.ObservedAt : null,
                OvertakenAt = overtaken
            });
        }
        return new AchievementRaceDto
        {
            Rows = rows.OrderByDescending(row => row.OvertakenAt ?? 0).ThenByDescending(row => row.TheirRecent + row.MyRecent).ThenByDescending(row => row.MyUnlocked + row.TheirUnlocked).ToList(),
            MyLeads = rows.Count(row => row.Leader == "me"),
            TheirLeads = rows.Count(row => row.Leader == "them"),
            Ties = rows.Count(row => row.Leader == "tie"),
            ObservedSince = history.Count == 0 ? null : history.Min(row => row.ObservedAt)
        };
    }

    #endregion
}
