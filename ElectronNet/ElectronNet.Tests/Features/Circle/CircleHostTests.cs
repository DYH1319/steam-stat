using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Features.SteamCache.Persistence;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using ElectronNET.Serialization;
using ElectronNet.Services;
using ElectronNet.Services.Collectors;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives;
using SteamStat.Core.Circle;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Insights;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Tests.Features.Circle;

public abstract class CircleHostFixture : InsightHostFixture
{
    protected const string Pal = "76561198000000009";
    protected const string Buddy = "76561198000000010";

    private protected SteamFeatureSnapshotStore Snapshots = null!;
    private protected FakeProfiles Profiles = null!;
    private protected CircleService Circle = null!;
    private protected ExchangeCardService Cards = null!;

    [SetUp]
    public void SetUpCircle()
    {
        Snapshots = new SteamFeatureSnapshotStore(new EfSteamResourceCacheStore(DbFactory, NullLogger<EfSteamResourceCacheStore>.Instance), Clock, NullLogger<SteamFeatureSnapshotStore>.Instance);
        Profiles = new FakeProfiles();
        Circle = new CircleService(Insights, Semantic, Catalog, DbFactory, Snapshots, Profiles, new Names(), Settings, Clock, NullLogger<CircleService>.Instance);
        Cards = new ExchangeCardService(Circle, DbFactory, Snapshots, null!, new AppEnvironment(false, "zh-CN", false, Paths), Clock, NullLogger<ExchangeCardService>.Instance);
    }

    protected long Now => Clock.GetUtcNow().ToUnixTimeSeconds();

    protected async Task FriendsAsync(params (string SteamId, string Name)[] friends)
    {
        await Snapshots.SaveFriendsAsync(new SteamFriendData
        {
            AccountName = "me",
            CurrentUser = new SteamFriendInfo { SteamId = Me, PersonaName = "Me", Relationship = 3 },
            Friends = friends.Select(friend => new SteamFriendInfo { SteamId = friend.SteamId, PersonaName = friend.Name, Relationship = 3, AvatarHash = "ab" + friend.SteamId[^2..] }).ToList(),
            LastUpdateTime = (int)Now
        }, SteamDataSource.Cm, Clock.GetUtcNow());
    }

    protected async Task TrackAsync(params string[] steamIds)
    {
        await using var db = DbFactory.CreateDbContext();
        db.FriendTrackingTable.AddRange(steamIds.Select(id => new FriendTracking { AccountName = "me", FriendSteamId = id, CreatedAt = Now - 86400L * 365 }));
        await db.SaveChangesAsync();
    }

    protected async Task FriendPlaysAsync(string steamId, int appId, long start, long end)
    {
        await using var db = DbFactory.CreateDbContext();
        db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = steamId, FriendPersonaName = "Pal", ChangeType = "game", CurrentValue = $$"""{"gameId":"{{appId}}"}""", Timestamp = start });
        db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = steamId, FriendPersonaName = "Pal", ChangeType = "game", CurrentValue = """{"gameId":"0"}""", Timestamp = end });
        await db.SaveChangesAsync();
    }

    protected async Task LobbyAsync(string steamId, int appId, string key, long from, long to)
    {
        await using var db = DbFactory.CreateDbContext();
        db.PresenceGroupTable.Add(new PresenceGroupRecord { AccountName = "me", SteamId = steamId, IsSelf = steamId == Me, AppId = appId, Kind = PresenceGroupKinds.Lobby, GroupKey = key, ObservedAt = from });
        db.PresenceGroupTable.Add(new PresenceGroupRecord { AccountName = "me", SteamId = steamId, IsSelf = steamId == Me, AppId = appId, Kind = PresenceGroupKinds.Lobby, GroupKey = string.Empty, ObservedAt = to });
        await db.SaveChangesAsync();
    }

    protected async Task<long> TogetherSecondsAsync(string friend)
    {
        await using var db = DbFactory.CreateDbContext();
        return await db.TogetherRecordTable.Where(row => row.FriendSteamId == friend).SumAsync(row => row.Seconds);
    }

    private protected sealed class Names : IAppNameResolver
    {
        public string? GetCachedName(uint appId) => $"Game {appId}";
        public Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default) => Task.FromResult<string?>($"Game {appId}");
    }

    private protected sealed class FakeProfiles : ISteamFriendProfileGateway
    {
        public Dictionary<ulong, SteamGatewayResult<SteamFriendLibrarySnapshot>> Libraries { get; } = [];
        public int Calls { get; private set; }

        public Task<SteamGatewayResult<SteamFriendLibrarySnapshot>> GetLibraryAsync(string accountName, ulong friendSteamId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Libraries.TryGetValue(friendSteamId, out var result)
                ? result
                : SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(SteamFailureKind.Forbidden, "private"));
        }

        public Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetAchievementUnlocksAsync(string accountName, ulong friendSteamId, uint appId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public static SteamGatewayResult<SteamFriendLibrarySnapshot> Library(ulong steamId, params (int AppId, int Minutes)[] games)
            => SteamGatewayResult<SteamFriendLibrarySnapshot>.Succeeded(
                new SteamFriendLibrarySnapshot(steamId, games.Select(game => new SteamFriendOwnedGame(game.AppId, $"Game {game.AppId}", game.Minutes, 0, 0, false, 0, 0, 0)).ToList()),
                SteamDataSource.Cm, SteamFreshness.Fresh, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }
}

[TestFixture]
public sealed class TogetherRecordTests : CircleHostFixture
{
    [Test]
    public async Task Retag_WritesTogetherRecordsAndMarksConfirmedParties()
    {
        var start = Unix(2026, 2, 2, 20);
        await PlayAsync(1, start, 3600);
        await FriendPlaysAsync(Pal, 1, start, start + 3600);
        await FriendPlaysAsync(Buddy, 1, start, start + 3600);
        await LobbyAsync(Me, 1, "same", start, start + 3600);
        await LobbyAsync(Pal, 1, "same", start + 60, start + 3600);
        await LobbyAsync(Buddy, 1, "other", start, start + 3600);

        await Tags.RetagAsync();

        (await ScalarAsync("SELECT auto_tags FROM v_session")).Should().Be(((int)(SessionTags.Coop | SessionTags.CoopConfirmed)).ToString());
        (await TogetherSecondsAsync(Pal)).Should().Be(3600);
        (await TogetherSecondsAsync(Buddy)).Should().Be(0, "同一款游戏、不同的大厅：不在一起");
        (await ScalarAsync("SELECT confirmed_seconds FROM v_together")).Should().Be("3540");
    }

    [Test]
    public async Task Retag_BackfillsOldSessionsOnceAndKeepsTheirExistingCoop()
    {
        var old = Unix(2025, 3, 1, 20);
        await PlayAsync(1, old, 3600);
        await PlayAsync(2, Unix(2025, 3, 2, 20), 3600);
        await FriendPlaysAsync(Pal, 1, old, old + 3600);
        await using (var db = DbFactory.CreateDbContext())
            await db.UseAppRecordTable.ExecuteUpdateAsync(setters => setters.SetProperty(record => record.AutoTags, (int)SessionTags.Coop));

        await Tags.RetagAsync();

        (await TogetherSecondsAsync(Pal)).Should().Be(3600, "第一次运行要补算历史");
        (await ScalarAsync("SELECT COUNT(*) FROM v_session WHERE auto_tags & 2 != 0")).Should().Be("2", "好友记录可能已经清掉了，已有的开黑标签保留");

        await using (var db = DbFactory.CreateDbContext())
            await db.TogetherRecordTable.ExecuteDeleteAsync();
        await Tags.RetagAsync();
        (await TogetherSecondsAsync(Pal)).Should().Be(0, "补算只做一次，之后只看最近几天");
    }

    [Test]
    public async Task HiddenGames_DoNotShowUpInTheView()
    {
        var start = Unix(2026, 2, 2, 20);
        await PlayAsync(1, start, 3600);
        await FriendPlaysAsync(Pal, 1, start, start + 3600);
        await Tags.RetagAsync();
        Settings.Current.Analytics = new SteamStat.Core.Settings.AnalyticsSettings { HiddenAppIds = [1] };
        await ApplySettingsAsync();

        (await ScalarAsync("SELECT COUNT(*) FROM v_together")).Should().Be("0");
    }
}

[TestFixture]
public sealed class CircleServiceTests : CircleHostFixture
{
    [Test]
    public async Task Friendship_CombinesSnapshotsSinceAndTogether()
    {
        var start = Unix(2026, 1, 20, 20);
        await FriendsAsync((Pal, "Pal"), (Buddy, "Buddy"));
        await TrackAsync(Pal);
        await PlayAsync(1, start, 3600);
        await FriendPlaysAsync(Pal, 1, start, start + 3600);
        await LobbyAsync(Me, 1, "k", start, start + 3600);
        await LobbyAsync(Pal, 1, "k", start, start + 3600);
        await Tags.RetagAsync();
        await using (var db = DbFactory.CreateDbContext())
        {
            db.FriendSinceTable.Add(new FriendSinceRecord { AccountName = "me", FriendSteamId = Buddy, FriendSince = Unix(2019, 2, 10), ObservedAt = Now });
            await db.SaveChangesAsync();
        }

        var report = await Circle.GetFriendshipAsync(new ArchiveScopeRequest());

        var pal = report.Bonds.Single(bond => bond.SteamId == Pal);
        pal.Tracked.Should().BeTrue();
        pal.AvatarHash.Should().Be("ab09");
        pal.TogetherSeconds.Should().Be(3600);
        pal.FirstTogetherConfirmed.Should().BeTrue();
        pal.Status.Should().Be("active");
        var buddy = report.Bonds.Single(bond => bond.SteamId == Buddy);
        buddy.SinceEvidence.Should().Be("official");
        buddy.NextAnniversary.Should().Be(Unix(2026, 2, 10));
        report.OfficialSince.Should().BeTrue();
        report.Events.Should().Contain(item => item.Kind == "firstTogether" && item.SteamId == Pal && item.Confirmed);
        report.Names.Should().ContainSingle(name => name.AppId == 1);
    }

    [Test]
    public async Task Together_ReportsWhenGroupDataStarted()
    {
        var start = Unix(2026, 1, 20, 20);
        await TrackAsync(Pal);
        await PlayAsync(1, start, 3600);
        await FriendPlaysAsync(Pal, 1, start, start + 3600);
        await LobbyAsync(Pal, 1, "k", start + 100, start + 3600);
        await Tags.RetagAsync();

        var together = await Circle.GetTogetherAsync(new CircleFriendRequest { FriendSteamId = Pal });

        together.Segments.Should().ContainSingle().Which.ConfirmedSeconds.Should().Be(0, "只有好友一方有大厅信息，按同时在玩算");
        together.GroupDataSince.Should().Be(start + 100);
    }

    [Test]
    public async Task Leaderboard_MergesMeSnapshotsAndCards()
    {
        await FriendsAsync((Pal, "Pal"));
        await TrackAsync(Pal);
        await PlayAsync(1, Now - 86400L * 2, 3600);
        await using (var db = DbFactory.CreateDbContext())
        {
            db.FriendPlaytimeSnapshotTable.AddRange(
                new FriendPlaytimeSnapshot { SteamId = Pal, AppId = 1, PlaytimeMinutes = 100, ObservedAt = Now - 86400L * 40 },
                new FriendPlaytimeSnapshot { SteamId = Pal, AppId = 1, PlaytimeMinutes = 400, ObservedAt = Now - 86400L * 1 });
            await db.SaveChangesAsync();
        }
        var card = new CardPayload { SteamId = Buddy, Name = "Buddy", GeneratedAt = Now - 3600, Year = 2026, Totals = new CardTotals(5000, 100, 30, 90, 10, 8, 1, 50, 5), Games = [new CardGame(1, "Game 1", 700, 50, 0, 0, 0)] };
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(ExchangeCardCodec.Encode(card, null)) })).Status.Should().Be("ok");

        var board = await Circle.GetLeaderboardAsync(new LeaderboardRequest());

        board.Entries.Select(entry => (entry.SteamId, entry.Source)).Should().BeEquivalentTo([(Me, "me"), (Pal, "snapshot"), (Buddy, "card")]);
        board.Entries.Single(entry => entry.SteamId == Pal).Last30.Should().Be(300);
        board.Entries.Single(entry => entry.SteamId == Me).Last7.Should().Be(60);
        board.Entries.Single(entry => entry.SteamId == Buddy).Name.Should().Be("Buddy");
        board.AppId.Should().Be(1);
        board.Game.Select(entry => (entry.SteamId, entry.Minutes)).Should().Equal((Buddy, 700), (Pal, 400), (Me, 60));
        board.Cards.Should().Be(1);
    }

    [Test]
    public async Task LibraryEvents_SkipTheBaselineAndShowAdditionsAndRemovals()
    {
        await TrackAsync(Pal);
        var collector = new FriendPlaytimeCollector(DbFactory, Profiles, null!, null!, Clock);
        await collector.SaveLibraryAsync(Pal, [Owned(1), Owned(2)]);
        Clock.Now = Clock.Now.AddDays(1);
        await collector.SaveLibraryAsync(Pal, [Owned(1), Owned(2), Owned(3, achievements: (2, 10))]);
        Clock.Now = Clock.Now.AddDays(1);
        await collector.SaveLibraryAsync(Pal, [Owned(1), Owned(3, achievements: (4, 10))]);

        var library = await Circle.GetLibraryEventsAsync(new CircleFilterRequest { FriendSteamId = Pal });

        library.BaselineGames.Should().Be(2);
        library.Events.Select(item => (item.AppId, item.Kind)).Should().Equal((2, "removed"), (3, "added"));
        await using var db = DbFactory.CreateDbContext();
        (await db.FriendAchievementProgressTable.Where(row => row.SteamId == Pal).Select(row => row.Unlocked).ToListAsync()).Should().Equal(2, 4);
    }

    [Test]
    public async Task LibraryCollector_DoesNotTreatAShortReplyAsRemovals()
    {
        var collector = new FriendPlaytimeCollector(DbFactory, Profiles, null!, null!, Clock);
        await collector.SaveLibraryAsync(Pal, Enumerable.Range(1, 10).Select(id => Owned(id)).ToList());
        await collector.SaveLibraryAsync(Pal, [Owned(1)]);
        await using var db = DbFactory.CreateDbContext();
        (await db.FriendLibraryItemTable.CountAsync(row => row.RemovedAt != null)).Should().Be(0);
    }

    [Test]
    public async Task AchievementRace_FindsWhenTheFriendOvertook()
    {
        await TrackAsync(Pal);
        await using (var db = DbFactory.CreateDbContext())
        {
            for (var index = 0; index < 3; index++)
                db.AchievementUnlockArchiveTable.Add(new AchievementUnlockArchive { AccountName = "me", SteamId = Me, AppId = 1, InternalName = $"a{index}", UnlockTime = Now - 86400L * (50 - index), RecordedAt = Now });
            db.AchievementGameArchiveTable.Add(new AchievementGameArchive { AccountName = "me", SteamId = Me, AppId = 1, AppName = "Game 1", Total = 10, Unlocked = 3, SyncedAt = Now });
            db.FriendAchievementProgressTable.AddRange(
                new FriendAchievementProgress { SteamId = Pal, AppId = 1, Unlocked = 2, Total = 10, ObservedAt = Now - 86400L * 40 },
                new FriendAchievementProgress { SteamId = Pal, AppId = 1, Unlocked = 5, Total = 10, ObservedAt = Now - 86400L * 10 });
            await db.SaveChangesAsync();
        }

        var race = await Circle.GetAchievementRaceAsync(new CircleFriendRequest { FriendSteamId = Pal });

        var row = race.Rows.Should().ContainSingle().Subject;
        row.Leader.Should().Be("them");
        row.OvertakenAt.Should().Be(Now - 86400L * 10);
        row.TheirRecent.Should().Be(3);
        race.TheirLeads.Should().Be(1);
    }

    [Test]
    public async Task PartyPlan_UsesLiveLibrariesAndFallsBackToStoredOnes()
    {
        await FriendsAsync((Pal, "Pal"), (Buddy, "Buddy"));
        await TrackAsync(Buddy);
        Catalog.Owned.Add(new InsightOwnedGame(1, "Game 1", 600, Now));
        Catalog.Owned.Add(new InsightOwnedGame(2, "Game 2", 60, 0));
        Catalog.Facts[1] = new InsightAppFacts(1) { Tags = ["Co-op"] };
        Catalog.Facts[2] = new InsightAppFacts(2) { Tags = ["Co-op"] };
        Profiles.Libraries[ulong.Parse(Pal)] = FakeProfiles.Library(ulong.Parse(Pal), (1, 300), (2, 10));
        var collector = new FriendPlaytimeCollector(DbFactory, Profiles, null!, null!, Clock);
        await collector.SaveLibraryAsync(Buddy, [Owned(1)]);

        var plan = await Circle.PlanPartyAsync(new PartyPlanRequest { FriendSteamIds = [Pal, Buddy, Me] });

        plan.Members.Select(member => (member.SteamId, member.Library)).Should().Equal((Me, "ok"), (Pal, "ok"), (Buddy, "ok"));
        plan.Common.Should().ContainSingle().Which.AppId.Should().Be(1);
        plan.NearMiss.Should().ContainSingle().Which.Missing.Should().Equal(Buddy);
        plan.Together.Should().HaveCount(168);

        await Circle.PlanPartyAsync(new PartyPlanRequest { FriendSteamIds = [Pal] });
        Profiles.Calls.Should().Be(2, "游戏库缓存十分钟");
    }

    [Test]
    public async Task Observation_WindowsOpenExtendAndReconnect()
    {
        var service = new FriendObservationService(DbFactory, Clock, NullLogger<FriendObservationService>.Instance);
        await service.HandleAsync(new SteamSessionReady("me"), CancellationToken.None);
        Clock.Now = Clock.Now.AddMinutes(5);
        await service.BeatAsync();
        Clock.Now = Clock.Now.AddMinutes(1);
        await service.HandleAsync(new SteamSessionEnded("me"), CancellationToken.None);
        Clock.Now = Clock.Now.AddMinutes(4);
        await service.HandleAsync(new SteamSessionReady("me"), CancellationToken.None);
        Clock.Now = Clock.Now.AddHours(1);
        await service.HandleAsync(new SteamSessionEnded("me"), CancellationToken.None);
        await service.DisposeAsync();

        await using var db = DbFactory.CreateDbContext();
        var windows = await db.FriendObservationWindowTable.ToListAsync();
        windows.Should().ContainSingle("十分钟内重连接着上一段");
        (windows[0].EndedAt - windows[0].StartedAt).Should().Be(70 * 60);
    }

    [Test]
    public async Task FriendSince_KeepsTheLatestTimeAndIgnoresZero()
    {
        var collector = new FriendSinceCollector(DbFactory, null!, null!, null!, Clock);
        await collector.SaveAsync("me", [new SteamFriendSince(ulong.Parse(Pal), 100), new SteamFriendSince(ulong.Parse(Buddy), 0)]);
        await collector.SaveAsync("me", [new SteamFriendSince(ulong.Parse(Pal), 200)]);

        await using var db = DbFactory.CreateDbContext();
        (await db.FriendSinceTable.Select(row => new { row.FriendSteamId, row.FriendSince }).ToListAsync()).Should().BeEquivalentTo([new { FriendSteamId = Pal, FriendSince = 200L }]);
    }

    private static SteamFriendOwnedGame Owned(int appId, int minutes = 10, (int Unlocked, int Total)? achievements = null)
        => new(appId, $"Game {appId}", minutes, 0, 0, achievements != null, achievements?.Total ?? 0, achievements?.Unlocked ?? 0, 0);
}

[TestFixture]
public sealed class ExchangeCardHostTests : CircleHostFixture
{
    private CardPayload Card(long generatedAt, int year = 2026) => new()
    {
        SteamId = Pal,
        Name = "Pal",
        GeneratedAt = generatedAt,
        Year = year,
        Totals = new CardTotals(1000, 300, 10, 40, 5, 4, 1, 20, 3),
        Months = Enumerable.Range(0, 12).Select(month => month == 0 ? 300 : 0).ToList(),
        Heatmap = Enumerable.Range(0, 168).Select(cell => cell == 21 ? 255 : 0).ToList(),
        Games = [new CardGame(1, "Game 1", 900, 300, 1, 2, 0)]
    };

    [Test]
    public async Task Import_StoresCardsAndTellsAboutOlderOnes()
    {
        var first = await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode("看看 " + ExchangeCardCodec.Encode(Card(Now - 7200), null)) });
        first.Status.Should().Be("ok");
        first.Updated.Should().BeFalse();
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(ExchangeCardCodec.Encode(Card(Now - 7200), null)) })).Updated.Should().BeFalse("同一张卡再导一次不算更新");
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(ExchangeCardCodec.Encode(Card(Now), null)) })).Updated.Should().BeTrue();

        var cards = await Cards.GetCardsAsync();
        cards.Select(card => card.GeneratedAt).Should().Equal(Now, Now - 7200);
        (await Cards.DeleteAsync(new ExchangeCardIdRequest { Id = cards[0].Id!.Value })).Should().BeTrue();
        (await Cards.GetCardsAsync()).Should().ContainSingle();
    }

    [Test]
    public async Task Import_RejectsMyOwnCardsAndReportsPassphraseProblems()
    {
        var mine = Card(Now) with { SteamId = Me };
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(ExchangeCardCodec.Encode(mine, null)) })).Status.Should().Be("self");
        var locked = ExchangeCardCodec.Encode(Card(Now), "secret");
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(locked) })).Status.Should().Be("needsPassphrase");
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(locked), Passphrase = IpcSecret.Encode("nope") })).Status.Should().Be("wrongPassphrase");
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(locked), Passphrase = IpcSecret.Encode("secret") })).Status.Should().Be("ok");
        (await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode("hello") })).Status.Should().Be("invalid");
    }

    [Test]
    public async Task Export_BuildsACardFromTheArchiveThatDecodes()
    {
        await PlayAsync(1, Unix(2026, 1, 12, 21), 3600);
        var export = await Cards.ExportAsync(new ExchangeCardExportRequest { Year = 2026, Name = IpcSecret.Encode("显示名"), Passphrase = IpcSecret.Encode("pw") });

        export.Protected.Should().BeTrue();
        export.Card.Name.Should().Be("显示名");
        export.Card.YearMinutes.Should().Be(60);
        var decoded = ExchangeCardCodec.Decode(export.Code, "pw");
        decoded.Status.Should().Be(CardDecodeStatus.Ok);
        decoded.Card!.SteamId.Should().Be(Me);
    }

    [Test]
    public async Task DuoReview_UsesTheFriendsCardAndMyTogetherTime()
    {
        var start = Unix(2026, 1, 12, 21);
        await TrackAsync(Pal);
        await PlayAsync(1, start, 3600);
        await FriendPlaysAsync(Pal, 1, start, start + 3600);
        await Tags.RetagAsync();
        (await Cards.DuoReviewAsyncOrNull(Pal)).Should().BeNull("没有对方的卡就没有双人回顾");
        await Cards.ImportAsync(new ExchangeCardImportRequest { Text = IpcSecret.Encode(ExchangeCardCodec.Encode(Card(Now, 2025), null)) });

        var duo = (await Cards.DuoReviewAsyncOrNull(Pal))!;

        duo.TheirYear.Should().Be(2025, "没有同一年的卡时用最新的一张");
        duo.Shared.Should().ContainSingle().Which.AppId.Should().Be(1);
        duo.TogetherSeconds.Should().Be(3600);
        duo.Schedule.Should().BeApproximately(1, 1e-6);
        duo.MyMinutes.Should().Be(60);
    }

    [Test]
    public void CardCode_SurvivesTheElectronBridge()
    {
        var code = ExchangeCardCodec.Encode(Card(Now), null);
        var bridge = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new JsonToBoxedPrimitivesConverter() }
        };
        var payload = JsonSerializer.Serialize(new object?[] { null, new { code, year = 2026 } });
        var boxed = JsonSerializer.Deserialize<object[]>(payload, bridge)![1];
        var request = new IpcRequestBinder(NullLogger<IpcRequestBinder>.Instance).Bind<ExchangeCardSaveRequest>(boxed, CircleIpc.SaveCardFile);
        request.Code.Should().Be(code);
    }
}

internal static class ExchangeCardServiceTestExtensions
{
    public static Task<DuoReviewDto?> DuoReviewAsyncOrNull(this ExchangeCardService service, string friend)
        => service.GetDuoReviewAsync(new DuoReviewRequest { FriendSteamId = friend, Year = 2026 });
}
