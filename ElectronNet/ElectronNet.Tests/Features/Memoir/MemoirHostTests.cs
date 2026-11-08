using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using ElectronNET.Serialization;
using ElectronNet.Services;
using ElectronNet.Services.Collectors;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Features;
using SteamStat.Core.Insights;
using SteamStat.Core.Memoir;
using SteamStat.Core.Sessions;
using ValveKeyValue;

namespace ElectronNet.Tests.Features.Memoir;

public abstract class MemoirHostFixture : InsightHostFixture
{
    private protected FakeMemoirCatalog MemoirCatalog = null!;
    private protected MemoirService Memoir = null!;
    private protected MemoirJournalService Journal = null!;

    [SetUp]
    public void SetUpMemoir()
    {
        MemoirCatalog = new FakeMemoirCatalog();
        Memoir = new MemoirService(Semantic, Insights, MemoirCatalog, DbFactory, Clock, NullLogger<MemoirService>.Instance);
        Journal = new MemoirJournalService(DbFactory, Memoir, new Names(), Settings, Bus, Clock, NullLogger<MemoirJournalService>.Instance);
    }

    [TearDown]
    public void TearDownMemoir() => Journal.Dispose();

    protected static int Day(int year, int month, int day) => MemoirDays.ToEpoch(new DateOnly(year, month, day));

    private protected sealed class FakeMemoirCatalog : IMemoirCatalog
    {
        public List<MemoirFriend> Friends { get; } = [];
        public List<YearReview> Reviews { get; } = [];
        public List<MemoirFirstPlay> FirstPlays { get; } = [];

        public Task<IReadOnlyList<MemoirFriend>> GetFriendsAsync(InsightScope scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoirFriend>>(Friends);
        public Task<IReadOnlyList<YearReview>> GetYearReviewsAsync(InsightScope scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<YearReview>>(Reviews);
        public Task<IReadOnlyList<MemoirFirstPlay>> GetLibraryFirstPlaysAsync(InsightScope scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoirFirstPlay>>(FirstPlays);
        public IReadOnlyDictionary<int, string> GetNames(IEnumerable<int> appIds) => appIds.ToDictionary(appId => appId, appId => $"Game {appId}");
    }

    private sealed class Names : IAppNameResolver
    {
        public string? GetCachedName(uint appId) => $"Game {appId}";
        public Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default) => Task.FromResult<string?>($"Game {appId}");
    }
}

[TestFixture]
public sealed class TimeCapsuleHostTests : MemoirHostFixture
{
    private long Now => Clock.Now.ToUnixTimeSeconds();

    [Test]
    public async Task TheMessage_IsNeverSentBeforeTheCapsuleIsDue()
    {
        var capsule = await Journal.CreateCapsuleAsync(new TimeCapsuleCreateRequest { Label = "Next year", Message = "Hello future", OpenAt = Now + 30 * 86400L });

        capsule.State.Should().Be("sealed");
        capsule.Message.Should().BeNull();
        (await Journal.GetCapsulesAsync()).Single().Message.Should().BeNull();
        (await Journal.OpenCapsuleAsync(capsule.Id)).Should().BeNull();

        Clock.Now = Clock.Now.AddDays(31);
        var listed = (await Journal.GetCapsulesAsync()).Single();
        listed.State.Should().Be("ready");
        listed.Message.Should().Be("Hello future");
        Bus.Events.OfType<TimeCapsuleReady>().Should().ContainSingle();

        var opened = await Journal.OpenCapsuleAsync(capsule.Id);
        opened!.State.Should().Be("opened");
        opened.Context.Should().NotBeNull();
    }

    [Test]
    public async Task AGameBoundCapsule_WaitsForALaunchAfterTheDate()
    {
        await PlayAsync(10, Now - 86400, 3600);
        var capsule = await Journal.CreateCapsuleAsync(new TimeCapsuleCreateRequest { Message = "Remember the boss", OpenAt = Now + 2 * 86400L, AppId = 10 });
        capsule.Context.Should().BeNull("the context is only sent once the capsule is ready");

        Clock.Now = Clock.Now.AddDays(5);
        (await Journal.CheckCapsulesAsync(null, announce: false)).Should().BeEmpty();

        await PlayAsync(10, Now - 3600, 600);
        (await Journal.CheckCapsulesAsync(null, announce: false)).Should().Equal(capsule.Id);
    }

    /// <summary>用户写的文字编码后经过真实的桥接转换器：「12:30」「2024-05-01」不会被改写成时间。</summary>
    [Test]
    public async Task UserText_SurvivesTheElectronBridge()
    {
        var bridge = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new JsonToBoxedPrimitivesConverter() }
        };
        var binder = new IpcRequestBinder(NullLogger<IpcRequestBinder>.Instance);
        T Bind<T>(object args, IIpcEndpointDescriptor endpoint) where T : class
            => binder.Bind<T>(JsonSerializer.Deserialize<object[]>(JsonSerializer.Serialize(new object?[] { null, args }), bridge)![1], endpoint);

        var capsuleRequest = Bind<TimeCapsuleCreateRequest>(new { message = IpcSecret.Encode("12:30"), label = IpcSecret.Encode("2024-05-01"), openAt = Now + 2 * 86400L }, MemoirIpc.CreateCapsule);
        var eventRequest = Bind<LifeEventSaveRequest>(new { title = IpcSecret.Encode("2024-05-01"), kind = "move", startDay = 19000, boundary = false }, MemoirIpc.SaveLifeEvent);

        (await Journal.CreateCapsuleAsync(capsuleRequest)).Label.Should().Be("2024-05-01");
        (await Journal.SaveLifeEventAsync(eventRequest)).Title.Should().Be("2024-05-01");
        await using var db = DbFactory.CreateDbContext();
        (await db.TimeCapsuleTable.SingleAsync()).Message.Should().Be("12:30");
    }

    [Test]
    public async Task Capsules_RejectMessagesThatOpenToday()
    {
        var act = () => Journal.CreateCapsuleAsync(new TimeCapsuleCreateRequest { Message = "Too soon", OpenAt = Now + 60 });

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

[TestFixture]
public sealed class PreviouslyOnHostTests : MemoirHostFixture
{
    [Test]
    public async Task ALaunchAfterALongBreak_PublishesTheRecap()
    {
        var last = Unix(2025, 9, 1, 20);
        await PlayAsync(10, last - 10 * 86400, 7200);
        await PlayAsync(10, last, 3600);
        await using (var db = DbFactory.CreateDbContext())
        {
            var record = await db.UseAppRecordTable.OrderBy(row => row.Id).LastAsync();
            record.Note = "Stuck at the lighthouse";
            db.ScreenshotArchiveTable.Add(new ScreenshotArchive { AccountId = 1, SteamId = Me, AppId = 10, FileName = "a.jpg", FilePath = "/x/a.jpg", TakenAt = last + 600, IndexedAt = last });
            db.AchievementUnlockArchiveTable.Add(new AchievementUnlockArchive { AccountName = "me", SteamId = Me, AppId = 10, InternalName = "ACT1", Name = "Act One", UnlockTime = last + 1200, RecordedAt = last });
            db.AppBuildTable.Add(new AppBuildRecord { AppId = 10, BuildId = 2, ObservedAt = last + 30 * 86400L });
            await db.SaveChangesAsync();
        }
        var start = (int)Unix(2026, 2, 1, 20);
        await PlayAsync(10, start, 60);
        int recordId;
        await using (var db = DbFactory.CreateDbContext()) recordId = (await db.UseAppRecordTable.OrderBy(row => row.Id).LastAsync()).Id;

        await Journal.OnStartedAsync(new UsageSessionStarted(recordId, Me, 10, start));

        var recap = Bus.Events.OfType<PreviouslyOnReady>().Single().Recap;
        recap.GapDays.Should().Be(152);
        recap.Live.Should().BeTrue();
        recap.Notes.Should().ContainSingle(note => note.Text == "Stuck at the lighthouse");
        recap.Screenshots.Should().ContainSingle();
        recap.Unlocks.Should().ContainSingle(unlock => unlock.Name == "Act One");
        recap.Since.Should().ContainSingle(change => change.Kind == "update" && change.Count == 1);
        recap.PeriodSessions.Should().Be(2);
        recap.TotalSeconds.Should().Be(10800);
    }

    [Test]
    public async Task AShortBreak_OrTheSettingOff_StaysQuiet()
    {
        await PlayAsync(10, Unix(2026, 1, 20), 3600);
        var start = (int)Unix(2026, 2, 1);
        await PlayAsync(10, start, 60);

        await Journal.OnStartedAsync(new UsageSessionStarted(2, Me, 10, start));
        Settings.Current.Memoir = new SteamStat.Core.Settings.MemoirSettings { PreviouslyOnEnabled = false };
        await Journal.OnStartedAsync(new UsageSessionStarted(2, Me, 10, start + 200 * 86400));

        Bus.Events.OfType<PreviouslyOnReady>().Should().BeEmpty();
    }

    [Test]
    public async Task TheDetailPage_CanAlwaysAskForTheRecap()
    {
        (await Journal.GetPreviouslyOnAsync(new PreviouslyOnRequest { AppId = 10 })).Should().BeNull();
        await PlayAsync(10, Unix(2026, 1, 30, 8), 3600);

        var recap = await Journal.GetPreviouslyOnAsync(new PreviouslyOnRequest { AppId = 10 });

        recap!.Live.Should().BeFalse();
        recap.GapDays.Should().Be(4);
    }
}

[TestFixture]
public sealed class MemoirServiceHostTests : MemoirHostFixture
{
    [Test]
    public async Task TheTimeMachine_ReadsTheRealViews()
    {
        await PlayAsync(10, Unix(2026, 1, 10, 20), 7200);
        await using (var db = DbFactory.CreateDbContext())
        {
            db.CloudSaveTraceTable.Add(new CloudSaveTrace { SteamId = Me, AppId = 10, FileKey = "abc", WrittenAt = Unix(2024, 5, 1), ObservedAt = Unix(2026, 1, 1) });
            db.PlaytimeSnapshotTable.Add(new PlaytimeSnapshot { SteamId = Me, AppId = 10, PlaytimeMinutes = 600, FirstObservedAt = (int)Unix(2026, 1, 1), LastObservedAt = (int)Unix(2026, 1, 1) });
            await db.SaveChangesAsync();
        }
        MemoirCatalog.FirstPlays.Add(new MemoirFirstPlay(Me, 10, Unix(2024, 1, 1)));

        var day = await Memoir.GetTimeMachineAsync(new TimeMachineRequest { Day = Day(2026, 1, 10) });
        var before = await Memoir.GetTimeMachineAsync(new TimeMachineRequest { Day = Day(2024, 5, 1) });
        var prehistory = await Memoir.GetPrehistoryAsync(new ArchiveScopeRequest());

        day.Summary.DaySeconds.Should().BeEquivalentTo(new MemoirValueDto { Value = 7200, Evidence = "recorded" });
        day.Summary.TotalSeconds.Value.Should().Be(36000 + 7200);
        before.Summary.Traces.Should().ContainSingle(trace => trace.Kind == "cloudSave");
        before.Summary.TotalSeconds.Evidence.Should().Be("inferred");
        before.FirstDay.Should().Be("2024-01-01");
        prehistory.Untracked.Should().ContainSingle(item => item.AppId == 10 && item.UntrackedSeconds == 36000 && item.Evidence == "official");
        prehistory.Calendar.Should().Contain(item => item.Day == "2024-05-01");
    }

    [Test]
    public async Task LifeEvents_ReshapeTheChapters()
    {
        for (var month = 1; month <= 12; month++) await PlayAsync(month <= 6 ? 10 : 20, Unix(2025, month, 10), 30 * 3600);

        var plain = await Memoir.GetChaptersAsync(new ArchiveScopeRequest());
        var saved = await Journal.SaveLifeEventAsync(new LifeEventSaveRequest { Title = "Moved", Kind = "move", StartDay = Day(2025, 4, 1), Boundary = true });
        var reshaped = await Memoir.GetChaptersAsync(new ArchiveScopeRequest());

        plain.Chapters.Should().HaveCountGreaterThanOrEqualTo(2);
        reshaped.Chapters.Should().Contain(chapter => chapter.FromMonth == "2025-04" && chapter.StartEventId == saved.Id);
        reshaped.Events.Should().ContainSingle(item => item.Title == "Moved" && item.Start == "2025-04-01");
        (await Journal.DeleteLifeEventAsync(saved.Id)).Should().BeTrue();
        (await Journal.GetLifeEventsAsync()).Should().BeEmpty();
    }
}

[TestFixture]
public sealed class MemoirCollectorTests : MemoirHostFixture
{
    [Test]
    public void RemoteCache_YieldsOneTracePerFile_WithoutPaths()
    {
        var text = """
            "730"
            {
                "ChangeNumber" "106"
                "cfg/my_secret_save.txt"
                {
                    "root" "0"
                    "time" "1699795699"
                    "localtime" "1699795742"
                }
                "future.txt"
                {
                    "time" "4102444800"
                }
            }
            """;
        var root = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));

        var traces = CloudSaveTraceCollector.Parse(root, Me, 730, 1_800_000_000).ToList();

        traces.Should().ContainSingle();
        traces[0].WrittenAt.Should().Be(1699795699);
        traces[0].FileKey.Should().HaveLength(16).And.NotContain("secret");
    }

    [Test]
    public async Task CloudTraces_AreAppendOnly()
    {
        var collector = new CloudSaveTraceCollector(DbFactory, null!, Clock, NullLogger<CloudSaveTraceCollector>.Instance);
        var first = new CloudSaveTraceCollector.Trace(Me, 730, "k1", 100);

        (await collector.SaveAsync([first])).Should().Be(1);
        (await collector.SaveAsync([first, first with { WrittenAt = 200 }])).Should().Be(1);
        await using var db = DbFactory.CreateDbContext();
        (await db.CloudSaveTraceTable.CountAsync()).Should().Be(2);
    }

    [Test]
    public async Task YearInReview_ReadsEachPastYearOnce_AndRetriesDenialsLater()
    {
        var gateway = new Gateway();
        var collector = new YearInReviewCollector(DbFactory, gateway, Session, Session, Settings, Clock) { Spacing = TimeSpan.Zero };

        var outcome = await collector.CollectAsync(CancellationToken.None);

        outcome.Result.Should().Be(CollectorResult.Ok);
        gateway.Asked.Should().Equal(2025, 2024, 2023, 2022);
        gateway.Asked.Clear();
        await collector.CollectAsync(CancellationToken.None);
        gateway.Asked.Should().BeEmpty("2023 was denied less than a month ago");
        Clock.Now = Clock.Now.AddDays(31);
        await collector.CollectAsync(CancellationToken.None);
        gateway.Asked.Should().Equal(2023);
        Settings.Current.Memoir = new SteamStat.Core.Settings.MemoirSettings { YearInReviewEnabled = false };
        (await collector.CollectAsync(CancellationToken.None)).Result.Should().Be(CollectorResult.Skipped);
    }

    private static LoggedIn Session => new("me", 76561198000000001);

    private sealed class LoggedIn(string account, ulong steamId) : ISteamSessionAccessor, ISteamAccountIdentityProvider
    {
        public IReadOnlyList<string> GetLoggedInUsers() => [account];
        public bool TryGetSession(string accountName, out ISteamSession session) => throw new NotSupportedException();
        public bool TryGetSteamId(string accountName, out ulong value)
        {
            value = steamId;
            return accountName == account;
        }
    }

    private sealed class Gateway : ISteamArchiveGateway
    {
        public List<int> Asked { get; } = [];

        public Task<SteamYearInReviewReply?> GetYearInReviewAsync(string accountName, int year, CancellationToken cancellationToken = default)
        {
            Asked.Add(year);
            return Task.FromResult<SteamYearInReviewReply?>(year == 2023 ? new SteamYearInReviewReply("AccessDenied", null) : new SteamYearInReviewReply("OK", [0x0A, 0x00]));
        }

        public Task<SteamWebSession?> GetWebSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<SteamWebSession?>(null);
        public Task<IReadOnlyList<SteamPlaytimeDetail>?> GetPlaytimeDetailsAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SteamPlaytimeDetail>?>([]);
        public Task<SteamFamilySnapshot?> GetFamilyAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<SteamFamilySnapshot?>(null);
        public Task<string?> GetStoreCountryAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<SteamWishlistReply?> GetWishlistAsync(ulong steamId, SteamWebSession? session, CancellationToken cancellationToken = default) => Task.FromResult<SteamWishlistReply?>(null);
        public Task<int?> GetPlayerCountAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<NewsItem>?> GetNewsAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NewsItem>?>([]);
        public Task<IReadOnlyList<SteamStoreSearchHit>> SearchStoreAsync(string term, string language, string countryCode, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SteamStoreSearchHit>>([]);
    }
}
