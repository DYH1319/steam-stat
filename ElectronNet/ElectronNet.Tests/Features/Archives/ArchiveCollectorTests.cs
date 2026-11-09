using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ElectronNet.Features.DataTransfer;
using ElectronNet.Models;
using ElectronNet.Services;
using ElectronNet.Services.Collectors;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SteamKit2;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Recordings;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Features;
using SteamStat.Core.Insights;
using SteamStat.Core.PublicData;
using SteamStat.Core.Sessions;

namespace ElectronNet.Tests.Features.Archives;

/// <summary>真实 SQLite，假的只有 Steam：HTTP 处理器按地址返回页面，能看到每一次请求的头和内容。</summary>
public abstract class ArchiveFixture : InsightHostFixture
{
    protected const string MeAccount = "me";
    protected const ulong MeId = 76561198000000001;

    private protected FakeGateway Gateway = null!;
    private protected FakeSteam Steam = null!;
    private protected WebArchiveStore WebStore = null!;
    private protected BillingArchive Billing = null!;
    private protected CommunityArchive Community = null!;

    [SetUp]
    public void SetUpArchives()
    {
        Gateway = new FakeGateway();
        Steam = new FakeSteam();
        WebStore = new WebArchiveStore(DbFactory, Paths, Clock);
        Billing = new BillingArchive(DbFactory, Catalog, Clock);
        Community = new CommunityArchive(DbFactory, Clock);
    }

    private protected SteamWebFetcher Fetcher => new(new Clients(Steam), NullLogger<SteamWebFetcher>.Instance);

    private protected static LoggedIn Session => new(MeAccount, MeId);

    private protected BillingCollector BillingCollector() => new(DbFactory, Gateway, Fetcher, WebStore, Billing, Session, Session, Settings, Clock) { Spacing = TimeSpan.Zero };

    private protected CommunityCollector CommunityCollector() => new(Gateway, Fetcher, WebStore, Community, Session, Session, Settings, Clock) { Spacing = TimeSpan.Zero };

    private protected sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private protected sealed class FakeSteam : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Cookie, string? Form)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, request.Headers.TryGetValues("Cookie", out var values) ? string.Join(';', values) : null, form));
            return Respond(request);
        }
    }

    private protected static HttpResponseMessage Page(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private protected static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private protected sealed class LoggedIn(string account, ulong steamId) : ISteamSessionAccessor, ISteamAccountIdentityProvider
    {
        public IReadOnlyList<string> GetLoggedInUsers() => [account];

        public bool TryGetSession(string accountName, out ISteamSession session) => throw new NotSupportedException();

        public bool TryGetSteamId(string accountName, out ulong value)
        {
            value = steamId;
            return accountName == account;
        }
    }

    private protected sealed class FakeGateway : ISteamArchiveGateway
    {
        public SteamWebSession? Web { get; set; } = new(MeId, "token-value", "abc123");
        public Func<ulong, SteamWebSession?, SteamWishlistReply?> Wishlist { get; set; } = (_, session) => new SteamWishlistReply([], session != null);
        public string? Country { get; set; }
        public int CountryCalls { get; private set; }
        public Dictionary<int, int?> Players { get; } = [];
        public List<int> AskedPlayers { get; } = [];

        public Task<SteamWebSession?> GetWebSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult(Web);

        public Task<string?> GetStoreCountryAsync(string accountName, CancellationToken cancellationToken = default)
        {
            CountryCalls++;
            return Task.FromResult(Country);
        }

        public Task<IReadOnlyList<SteamPlaytimeDetail>?> GetPlaytimeDetailsAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SteamPlaytimeDetail>?>([]);
        public Task<SteamFamilySnapshot?> GetFamilyAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<SteamFamilySnapshot?>(null);
        public Task<SteamWishlistReply?> GetWishlistAsync(ulong steamId, SteamWebSession? session, CancellationToken cancellationToken = default) => Task.FromResult(Wishlist(steamId, session));

        public Task<int?> GetPlayerCountAsync(int appId, CancellationToken cancellationToken = default)
        {
            AskedPlayers.Add(appId);
            return Task.FromResult(Players.GetValueOrDefault(appId, 10));
        }

        public Task<IReadOnlyList<NewsItem>?> GetNewsAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NewsItem>?>([]);
        public Task<IReadOnlyList<SteamStoreSearchHit>> SearchStoreAsync(string term, string language, string countryCode, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SteamStoreSearchHit>>([]);

        public Func<int, SteamYearInReviewReply?> YearInReview { get; set; } = _ => new SteamYearInReviewReply("NoMatch", null);
        public List<int> AskedYears { get; } = [];

        public Task<SteamYearInReviewReply?> GetYearInReviewAsync(string accountName, int year, CancellationToken cancellationToken = default)
        {
            AskedYears.Add(year);
            return Task.FromResult(YearInReview(year));
        }
    }
}

[TestFixture]
public sealed class ArchiveCollectionServiceTests : ArchiveFixture
{
    private sealed class Probe(string id, Func<CollectorOutcome> run) : IArchiveCollector
    {
        public int Runs { get; private set; }
        public string Id => id;
        public TimeSpan Interval => TimeSpan.FromDays(7);

        public Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(run());
        }
    }

    private ArchiveCollectionService Service(params IArchiveCollector[] collectors)
        => new(collectors, new WebArchiveReparser(DbFactory, WebStore, Billing, Community, NullLogger<WebArchiveReparser>.Instance), DbFactory, Clock, NullLogger<ArchiveCollectionService>.Instance);

    [Test]
    public async Task RunsOnlyWhatIsDue()
    {
        var probe = new Probe("a", () => CollectorOutcome.Ok(3));
        using var service = Service(probe);

        await service.RunDueAsync();
        await service.RunDueAsync();
        probe.Runs.Should().Be(1);

        Clock.Now += TimeSpan.FromDays(8);
        await service.RunDueAsync();
        probe.Runs.Should().Be(2);
    }

    [Test]
    public async Task AFailureRetriesSoonerThanTheFullInterval()
    {
        var probe = new Probe("a", () => CollectorOutcome.Failed(CollectorErrors.Network));
        using var service = Service(probe);

        await service.RunDueAsync();
        Clock.Now += TimeSpan.FromHours(7);
        await service.RunDueAsync();

        probe.Runs.Should().Be(2);
        (await service.GetStatusAsync()).Single().LastError.Should().Be(CollectorErrors.Network);
    }

    [Test]
    public async Task SkippingDoesNotCountAsARun()
    {
        var probe = new Probe("a", () => CollectorOutcome.Skipped);
        using var service = Service(probe);

        await service.RunDueAsync();
        await service.RunDueAsync();

        probe.Runs.Should().Be(2, "没登录时下次调度要再试");
        (await service.GetStatusAsync()).Single().LastRunAt.Should().BeNull();
    }

    [Test]
    public async Task OneCrashDoesNotStopTheRest()
    {
        var bad = new Probe("bad", () => throw new InvalidOperationException("boom"));
        var good = new Probe("good", () => CollectorOutcome.Ok(1));
        using var service = Service(bad, good);

        await service.RunDueAsync();

        good.Runs.Should().Be(1);
        (await service.GetStatusAsync()).Single(item => item.Id == "bad").LastError.Should().Be(CollectorErrors.Read);
    }

    [Test]
    public async Task ManualRunIgnoresTheInterval()
    {
        var probe = new Probe("a", () => CollectorOutcome.Ok(2));
        using var service = Service(probe);
        await service.RunDueAsync();

        var status = await service.RunNowAsync("a");

        probe.Runs.Should().Be(2);
        status!.LastItems.Should().Be(2);
        (await service.RunNowAsync("missing")).Should().BeNull();
    }
}

[TestFixture]
public sealed class BillingCollectorTests : ArchiveFixture
{
    private static string Row(string id, string item, string total = "¥ 48.00", string type = "Purchase", string extraClass = "") => $"""
        <tr class="wallet_table_row" onclick="location.href='https://help.steampowered.com/x?transid={id}'">
          <td class="wht_date">3 Nov, 2026</td><td class="wht_items">{item}</td>
          <td class="wht_type {extraClass}"><div>{type}</div></td><td class="wht_total">{total}</td>
        </tr>
        """;

    private static string First(string rows, bool more = true) => $$"""
        <html><head><title>Purchase History</title></head><body data-x="{&quot;webapi_token&quot;:&quot;eyJhbGciOiJFZERTQSJ9.eyJpc3MiOiJyOjE4NTYifQ.abcdefghijklmnop&quot;}">
        <table class="wallet_history_table"><tbody>{{rows}}</tbody></table>
        <script>g_sessionID = "secret-session"; {{(more ? """var g_historyCursor = {"wallet_txnid":"9","timestamp_newest":5,"balance":"0","currency":23};""" : string.Empty)}}</script>
        </body></html>
        """;

    private static string More(string rows) => JsonSerializer.Serialize(new { html = rows, cursor = (object?)null });

    private async Task<List<PurchaseRecord>> RowsAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return await db.PurchaseRecordTable.AsNoTracking().OrderBy(row => row.RowKey).ToListAsync();
    }

    [Test]
    public async Task TheFirstRunPagesToTheEndWithTheCursorAndSession()
    {
        Steam.Respond = request => request.Method == HttpMethod.Post ? Page(More(Row("2", "Older"))) : Page(First(Row("1", "Newer")));

        var outcome = await BillingCollector().CollectAsync(default);

        outcome.Result.Should().Be(CollectorResult.Ok);
        outcome.Items.Should().Be(2);
        (await RowsAsync()).Select(row => row.RowKey).Should().Equal("txn:1", "txn:2");
        Steam.Requests.Should().HaveCount(2);
        Steam.Requests[0].Cookie.Should().Contain($"steamLoginSecure={MeId}%7C%7Ctoken-value").And.Contain("Steam_Language=english");
        var form = Steam.Requests[1].Form!;
        form.Should().Contain("cursor%5Bwallet_txnid%5D=9").And.Contain("cursor%5Btimestamp_newest%5D=5").And.Contain("sessionid=abc123");
    }

    [Test]
    public async Task LaterRunsStopAtRowsAlreadySeen()
    {
        Steam.Respond = request => request.Method == HttpMethod.Post ? Page(More(Row("2", "Older"))) : Page(First(Row("1", "Newer")));
        await BillingCollector().CollectAsync(default);
        Steam.Requests.Clear();

        await BillingCollector().CollectAsync(default);

        Steam.Requests.Should().ContainSingle("第一页全是见过的行，不用再翻");
    }

    [Test]
    public async Task ARefundUpdatesTheExistingRow()
    {
        Steam.Respond = _ => Page(First(Row("1", "Game"), more: false));
        await BillingCollector().CollectAsync(default);

        Steam.Respond = _ => Page(First(Row("1", "Game", type: "Refund", extraClass: "wht_refunded"), more: false));
        await BillingCollector().CollectAsync(default);

        var row = (await RowsAsync()).Single();
        row.Refunded.Should().BeTrue();
        row.Kind.Should().Be(BillingKinds.Refund);
    }

    [Test]
    public async Task PurchasesAreMatchedToOwnedGames()
    {
        Catalog.Owned.Add(new InsightOwnedGame(620, "Portal 2", 60, 0));
        Steam.Respond = _ => Page(First(Row("1", "Portal 2™", "¥ 35.00"), more: false));

        await BillingCollector().CollectAsync(default);

        var item = BillingArchive.Parse((await RowsAsync()).Single().Items).Single();
        item.AppId.Should().Be(620);
        item.AllocatedCents.Should().Be(3500);
    }

    [Test]
    public async Task RawPagesAreSavedWithoutTokens()
    {
        Steam.Respond = _ => Page(First(Row("1", "Game"), more: false));

        await BillingCollector().CollectAsync(default);

        await using var db = DbFactory.CreateDbContext();
        var capture = await db.WebCaptureTable.SingleAsync();
        capture.Kind.Should().Be(WebCaptureKinds.Billing);
        var content = await WebStore.ReadAsync(capture);
        content.Should().Contain("wallet_table_row").And.NotContain("eyJhbGci").And.NotContain("secret-session");
        // 文件本身也不能有，备份会原样打包
        await using var file = File.OpenRead(Path.Combine(WebStore.Root, capture.File));
        await using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(zip, Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be(content);
        BackupFormat.IsArchiveFile($"{BackupFormat.WebArchivePrefix}{capture.File}").Should().BeTrue("备份要能带上它");
    }

    [Test]
    public async Task AnExpiredLoginIsReportedAndNothingIsSaved()
    {
        Steam.Respond = _ => Redirect("https://store.steampowered.com/login/?redir=account");

        var outcome = await BillingCollector().CollectAsync(default);

        outcome.Error.Should().Be(CollectorErrors.SignedOut);
        await using var db = DbFactory.CreateDbContext();
        (await db.WebCaptureTable.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task ARedirectOffTheListIsNotFollowed()
    {
        Steam.Respond = _ => Redirect("https://evil.example/account/history/");

        var outcome = await BillingCollector().CollectAsync(default);

        outcome.Result.Should().Be(CollectorResult.Failed);
        Steam.Requests.Should().ContainSingle("登录 Cookie 不能跟着跳转出去");
    }

    [Test]
    public async Task ALayoutChangeKeepsThePageAndCanBeReparsedLater()
    {
        Steam.Respond = _ => Page("<title>History</title><main>brand new design</main>");

        var outcome = await BillingCollector().CollectAsync(default);

        outcome.Error.Should().Be(CollectorErrors.Layout);
        await using var db = DbFactory.CreateDbContext();
        var capture = await db.WebCaptureTable.SingleAsync();
        capture.State.Should().Be("layout");

        // 修好解析器：用新内容代替旧文件，版本号退回去，模拟解析器升级
        var path = Path.Combine(WebStore.Root, capture.File);
        await using (var file = File.Create(path))
        await using (var zip = new GZipStream(file, CompressionLevel.Fastest))
            await zip.WriteAsync(Encoding.UTF8.GetBytes(First(Row("7", "Recovered"), more: false)));
        capture.ParserVersion = 0;
        await db.SaveChangesAsync();

        var reparser = new WebArchiveReparser(DbFactory, WebStore, Billing, Community, NullLogger<WebArchiveReparser>.Instance);
        (await reparser.RunIfNeededAsync()).Should().Be(1);
        (await RowsAsync()).Single().RowKey.Should().Be("txn:7");
        (await reparser.RunIfNeededAsync()).Should().Be(0, "解析过的页面不再解析");
    }

    [Test]
    public async Task TheSwitchTurnsItOff()
    {
        Settings.Current.Archive = new SteamStat.Core.Settings.ArchiveSettings { WebCaptureEnabled = false };

        (await BillingCollector().CollectAsync(default)).Result.Should().Be(CollectorResult.Skipped);
        Steam.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task NoWebSessionMeansNoRequests()
    {
        Gateway.Web = null;

        await BillingCollector().CollectAsync(default);

        Steam.Requests.Should().BeEmpty();
    }
}

[TestFixture]
public sealed class CommunityCollectorTests : ArchiveFixture
{
    private static string Review(string id, int appId) => $"""
        <div class="review_box"><div class="header"></div><div class="review_box_content">
          <div class="leftcol"><a href="https://steamcommunity.com/app/{appId}"></a></div>
          <div class="rightcol"><div class="thumb"><img src="x/icon_thumbsUp.png"></div>
          <div class="hours">1.0 hrs on record</div><div class="content">text {id}</div><div class="posted">Posted 1 March, 2020.</div>
          <a id="RecommendationVoteUpBtn{id}"></a></div></div></div>
        """;

    private static string Reviews(int total, params string[] boxes)
        => $"""<title>x</title><div id="leftContents"><div class="workshopBrowsePagingInfo">Showing 1-10 of {total} entries</div>{string.Concat(boxes)}</div>""";

    private const string EmptyProfile = """<title>x</title><div class="profile_small_header_bg"></div>""";

    private static string Comments(int total, params (string Id, long At)[] comments)
    {
        var html = string.Concat(comments.Select(comment => $"""
            <div class="commentthread_comment" id="comment_{comment.Id}"><a class="commentthread_author_link" data-miniprofile="1"><bdi>A</bdi></a>
            <div class="commentthread_comment_timestamp" data-timestamp="{comment.At}"></div><div class="commentthread_comment_text">hi</div></div>
            """));
        return JsonSerializer.Serialize(new { success = true, total_count = total, comments_html = html });
    }

    private void Serve(Func<HttpRequestMessage, HttpResponseMessage?> reviews, string comments = "{\"success\":true,\"total_count\":0,\"comments_html\":\"\"}")
        => Steam.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/comment/", StringComparison.Ordinal)) return Page(comments);
            if (path.Contains("myworkshopfiles", StringComparison.Ordinal)) return Page("""<div class="workshopBrowsePaging"></div>""");
            return reviews(request) ?? Page(EmptyProfile);
        };

    private async Task<List<CommunityReviewRecord>> ReviewsAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return await db.CommunityReviewTable.AsNoTracking().OrderBy(row => row.ReviewId).ToListAsync();
    }

    [Test]
    public async Task FollowsTheVanityRedirectAndReadsEveryPage()
    {
        Serve(request => request.RequestUri!.AbsolutePath.StartsWith("/profiles", StringComparison.Ordinal)
            ? Redirect($"https://steamcommunity.com/id/someone/recommended/{request.RequestUri.Query}")
            : Page(request.RequestUri.Query.Contains("p=2", StringComparison.Ordinal) ? Reviews(11, Review("11", 2)) : Reviews(11, Review("1", 1))));

        var outcome = await CommunityCollector().CollectAsync(default);

        outcome.Result.Should().Be(CollectorResult.Ok);
        (await ReviewsAsync()).Select(row => row.ReviewId).Should().Equal("1", "11");
        Steam.Requests.Where(request => request.Uri.AbsolutePath.StartsWith("/id/", StringComparison.Ordinal)).Should().OnlyContain(request => request.Cookie != null);
    }

    [Test]
    public async Task AReviewGoneFromAFullSweepIsMarkedNotDeleted()
    {
        Serve(_ => Page(Reviews(2, Review("1", 1), Review("2", 2))));
        await CommunityCollector().CollectAsync(default);
        Clock.Now += TimeSpan.FromDays(7);

        Serve(_ => Page(Reviews(1, Review("1", 1))));
        await CommunityCollector().CollectAsync(default);

        var rows = await ReviewsAsync();
        rows.Should().HaveCount(2);
        rows[0].MissingSince.Should().BeNull();
        rows[1].MissingSince.Should().NotBeNull();
        rows[1].Text.Should().Be("text 2", "这份档案的价值就是留住它");
    }

    [Test]
    public async Task AReviewThatComesBackIsNoLongerMissing()
    {
        Serve(_ => Page(Reviews(2, Review("1", 1), Review("2", 2))));
        await CommunityCollector().CollectAsync(default);
        Clock.Now += TimeSpan.FromDays(7);
        Serve(_ => Page(Reviews(1, Review("1", 1))));
        await CommunityCollector().CollectAsync(default);
        Clock.Now += TimeSpan.FromDays(7);

        Serve(_ => Page(Reviews(2, Review("1", 1), Review("2", 2))));
        await CommunityCollector().CollectAsync(default);

        (await ReviewsAsync()).Should().OnlyContain(row => row.MissingSince == null);
    }

    [Test]
    public async Task AnEmptySweepProvesNothing()
    {
        Serve(_ => Page(Reviews(1, Review("1", 1))));
        await CommunityCollector().CollectAsync(default);
        Clock.Now += TimeSpan.FromDays(7);

        // 资料转为私密或登录失效时页面也是空的
        Serve(_ => Page(EmptyProfile));
        await CommunityCollector().CollectAsync(default);

        (await ReviewsAsync()).Single().MissingSince.Should().BeNull();
    }

    [Test]
    public async Task AFailureHalfwayMarksNothing()
    {
        Serve(_ => Page(Reviews(11, Review("1", 1), Review("2", 2))));
        Steam.Respond = request => request.RequestUri!.Query.Contains("p=2", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : request.RequestUri.AbsolutePath.Contains("recommended", StringComparison.Ordinal) ? Page(Reviews(11, Review("1", 1), Review("2", 2))) : Page("""<div class="workshopBrowsePaging"></div>""");
        await CommunityCollector().CollectAsync(default);
        Clock.Now += TimeSpan.FromDays(7);

        Steam.Respond = request => request.RequestUri!.Query.Contains("p=2", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : request.RequestUri.AbsolutePath.Contains("recommended", StringComparison.Ordinal) ? Page(Reviews(11, Review("1", 1))) : Page("""<div class="workshopBrowsePaging"></div>""");
        var outcome = await CommunityCollector().CollectAsync(default);

        outcome.Result.Should().Be(CollectorResult.Failed);
        (await ReviewsAsync()).Should().OnlyContain(row => row.MissingSince == null);
    }

    [Test]
    public async Task OnlyCommentsInTheRangeReadCanGoMissing()
    {
        var total = 40 * 50 + 1;
        await using (var db = DbFactory.CreateDbContext())
        {
            db.CommunityCommentTable.Add(new CommunityCommentRecord { SteamId = Me, CommentId = "ancient", AuthorName = "A", PostedAt = 1, FirstSeenAt = 1, LastSeenAt = 1 });
            await db.SaveChangesAsync();
        }
        Serve(_ => null, Comments(total, ("a", 500), ("b", 400)));

        await CommunityCollector().CollectAsync(default);

        await using var read = DbFactory.CreateDbContext();
        (await read.CommunityCommentTable.SingleAsync(row => row.CommentId == "ancient")).MissingSince.Should().BeNull("这么早的留言这次没翻到，不能算消失");
    }

    [Test]
    public async Task ALayoutChangeInOneKindStillReadsTheOthers()
    {
        Steam.Respond = request => request.RequestUri!.AbsolutePath.Contains("recommended", StringComparison.Ordinal)
            ? Page("<title>x</title><main>new</main>")
            : request.RequestUri.AbsolutePath.Contains("/comment/", StringComparison.Ordinal) ? Page(Comments(1, ("a", 500))) : Page("""<div class="workshopBrowsePaging"></div>""");

        var outcome = await CommunityCollector().CollectAsync(default);

        outcome.Error.Should().Be(CollectorErrors.Layout);
        await using var db = DbFactory.CreateDbContext();
        (await db.CommunityCommentTable.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task OldCapturesArePrunedAndFilesRemoved()
    {
        Serve(_ => Page(Reviews(1, Review("1", 1))));
        for (var run = 0; run < 7; run++)
        {
            await CommunityCollector().CollectAsync(default);
            Clock.Now += TimeSpan.FromDays(7);
        }

        await using var db = DbFactory.CreateDbContext();
        var kept = await db.WebCaptureTable.Where(row => row.Kind == WebCaptureKinds.Reviews).ToListAsync();
        kept.Should().HaveCount(4);
        Directory.GetFiles(Path.Combine(WebStore.Root, WebCaptureKinds.Reviews), "*.gz", SearchOption.AllDirectories).Should().HaveCount(4);
    }
}

[TestFixture]
public sealed class WishlistCollectorTests : ArchiveFixture
{
    private WishlistCollector Collector() => new(DbFactory, Gateway, Session, Session, Clock);

    private async Task<List<WishlistRecord>> RowsAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return await db.WishlistRecordTable.AsNoTracking().Where(row => row.SteamId == Me).OrderBy(row => row.Id).ToListAsync();
    }

    [Test]
    public async Task ThePublicReplyIsTriedFirstAndTheTokenOnlyWhenEmpty()
    {
        var tokens = new List<bool>();
        Gateway.Wishlist = (_, session) =>
        {
            tokens.Add(session != null);
            return new SteamWishlistReply([new WishlistEntry(1, 100, 0)], session != null);
        };

        await Collector().CollectAsync(default);

        tokens.Should().NotContain(true);
        (await RowsAsync()).Single().AddedAt.Should().Be(100);
    }

    [Test]
    public async Task APrivateWishlistIsReadWithTheToken()
    {
        Gateway.Wishlist = (steamId, session) => session != null && steamId == MeId
            ? new SteamWishlistReply([new WishlistEntry(1, 100, 0)], true)
            : new SteamWishlistReply([], session != null);

        await Collector().CollectAsync(default);

        (await RowsAsync()).Should().ContainSingle();
    }

    [Test]
    public async Task EmptyRepliesRemoveNothingUntilConfirmed()
    {
        Gateway.Wishlist = (_, session) => new SteamWishlistReply([new WishlistEntry(1, 100, 0)], session != null);
        await Collector().CollectAsync(default);

        Gateway.Wishlist = (_, session) => new SteamWishlistReply([], session != null);
        await Collector().CollectAsync(default);
        (await RowsAsync()).Single().RemovedAt.Should().BeNull("一次空结果不算数");

        await Collector().CollectAsync(default);
        (await RowsAsync()).Single().RemovedAt.Should().NotBeNull();
    }

    [Test]
    public async Task AFailedRequestChangesNothing()
    {
        Gateway.Wishlist = (_, session) => new SteamWishlistReply([new WishlistEntry(1, 100, 0)], session != null);
        await Collector().CollectAsync(default);

        Gateway.Wishlist = (_, _) => null;
        var outcome = await Collector().CollectAsync(default);

        outcome.Error.Should().Be(CollectorErrors.Network);
        (await RowsAsync()).Single().RemovedAt.Should().BeNull();
    }

    [Test]
    public async Task ReaddingStartsANewStretch()
    {
        Gateway.Wishlist = (_, session) => new SteamWishlistReply([new WishlistEntry(1, 100, 0)], session != null);
        await Collector().CollectAsync(default);
        Gateway.Wishlist = (_, session) => new SteamWishlistReply([new WishlistEntry(1, 900, 0)], session != null);
        await Collector().CollectAsync(default);

        var rows = await RowsAsync();
        rows.Should().HaveCount(2);
        rows[0].RemovedAt.Should().NotBeNull();
        rows[1].RemovedAt.Should().BeNull();
    }

    [Test]
    public async Task TheQueryWorksOutWhatHappenedToEachItem()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.WishlistRecordTable.AddRange(
                new WishlistRecord { SteamId = Me, AppId = 1, AddedAt = Clock.GetUtcNow().AddDays(-30).ToUnixTimeSeconds(), FirstSeenAt = 1, RemovedAt = Clock.GetUtcNow().AddDays(-3).ToUnixTimeSeconds() },
                new WishlistRecord { SteamId = Me, AppId = 2, AddedAt = Clock.GetUtcNow().AddDays(-20).ToUnixTimeSeconds(), FirstSeenAt = 1, RemovedAt = Clock.GetUtcNow().AddDays(-2).ToUnixTimeSeconds() },
                new WishlistRecord { SteamId = Me, AppId = 3, AddedAt = Clock.GetUtcNow().AddDays(-10).ToUnixTimeSeconds(), FirstSeenAt = 1 });
            db.LibraryAcquisitionTable.Add(new LibraryAcquisition { AccountName = MeAccount, AppId = 1, PackageId = 5, AcquiredAt = Clock.GetUtcNow().AddDays(-3).ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
        }

        var items = (await Query().GetWishlistAsync(new ArchiveScopeRequest())).Items.ToDictionary(item => item.AppId);

        items[1].Outcome.Should().Be("acquired");
        items[1].WaitDays.Should().Be(27);
        items[2].Outcome.Should().Be("removed");
        items[3].Outcome.Should().Be("waiting");
        items[3].WaitDays.Should().Be(10);
    }

    private ArchiveQueryService Query() => QueryFactory.Create(DbFactory, Paths, Settings, Clock);
}

internal static class QueryFactory
{
    private sealed class NoNames : IAppNameResolver
    {
        public string? GetCachedName(uint appId) => null;
        public Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    internal sealed class NoLanguage : ILanguageProvider
    {
        public string GetSteamLanguage() => "english";
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    internal static PublicDataService PublicData(IDbContextFactory<AppDbContext> factory, SteamStat.Core.Environment.IAppPaths paths, SteamStat.Core.Settings.ISettingsStore settings, TimeProvider clock, IHttpClientFactory? clients = null)
        => new(factory, paths, clients ?? new NoClients(), settings, new NoLanguage(), clock, NullLogger<PublicDataService>.Instance);

    internal static ArchiveQueryService Create(IDbContextFactory<AppDbContext> factory, SteamStat.Core.Environment.IAppPaths paths, SteamStat.Core.Settings.ISettingsStore settings, TimeProvider clock)
    {
        var publicData = PublicData(factory, paths, settings, clock);
        publicData.HistoryMirrorsOverride = [];
        var history = new PlayerHistoryService(factory, publicData, paths, clock, NullLogger<PlayerHistoryService>.Instance);
        var store = new StoreArchiveService(factory, new NoClients(), paths, settings, NullLogger<StoreArchiveService>.Instance);
        return new ArchiveQueryService(factory, new NoNames(), history, publicData, store, clock);
    }
}

[TestFixture]
public sealed class StoreArchiveTests : ArchiveFixture
{
    private StoreArchiveService Archive() => new(DbFactory, new Clients(Steam), Paths, Settings, NullLogger<StoreArchiveService>.Instance);

    private static StorePageFacts Facts(string name = "Game", string header = "a/header.jpg")
        => new(name, "desc", ["Dev"], ["Pub"], null, null, header, null, [], null);

    private static StoreItemStatus Item(StorePageFacts? page, bool visible = true, bool purchasable = true)
        => new(1, visible, purchasable, false, false, 999, null, null, null, null, page);

    private async Task Record(StoreItemStatus item, long? previousDelisted, bool known = true)
    {
        await using var db = DbFactory.CreateDbContext();
        var previous = known ? new Dictionary<int, long?> { [1] = previousDelisted } : new Dictionary<int, long?>();
        await Archive().RecordAsync(db, [item], previous, Clock.GetUtcNow().ToUnixTimeSeconds());
        await db.SaveChangesAsync();
    }

    private async Task<(List<StorePageSnapshot> Snapshots, List<AppArtifactEvent> Events)> ReadAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return (await db.StorePageSnapshotTable.AsNoTracking().ToListAsync(), await db.AppArtifactEventTable.AsNoTracking().ToListAsync());
    }

    [Test]
    public async Task TheFirstSightingIsOnlyABaseline()
    {
        await Record(Item(Facts()), null, known: false);

        var (snapshots, events) = await ReadAsync();
        snapshots.Should().ContainSingle();
        events.Should().BeEmpty();
    }

    [Test]
    public async Task ADelistedGameSeenForTheFirstTimeIsNotAnEvent()
    {
        await Record(Item(null, visible: false), null, known: false);

        (await ReadAsync()).Events.Should().BeEmpty();
    }

    [Test]
    public async Task NoChangeMeansNoNewSnapshot()
    {
        await Record(Item(Facts()), null, known: false);
        Clock.Now += TimeSpan.FromDays(7);
        await Record(Item(Facts()), null);

        (await ReadAsync()).Snapshots.Should().ContainSingle();
    }

    [Test]
    public async Task ARenameIsRecordedWithBothNames()
    {
        await Record(Item(Facts()), null, known: false);
        Clock.Now += TimeSpan.FromDays(7);
        await Record(Item(Facts("New Name")), null);

        var (snapshots, events) = await ReadAsync();
        snapshots.Should().HaveCount(2);
        events.Should().ContainSingle().Which.Should().Match<AppArtifactEvent>(row => row.Kind == "renamed" && row.Before == "Game" && row.After == "New Name");
    }

    [Test]
    public async Task DelistingAndRelistingAreEvents()
    {
        await Record(Item(Facts()), null, known: false);
        await Record(Item(null, purchasable: false), null);
        await Record(Item(Facts()), 123);

        (await ReadAsync()).Events.Select(row => row.Kind).Should().Equal("delisted", "relisted");
    }

    [Test]
    public async Task CoversAreKeptPerVersionForPlayedGamesOnly()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.UseAppRecordTable.Add(new UseAppRecord { AppId = 1, SteamId = Me, StartTime = 1, EndTime = 2, Duration = 1 });
            db.StorePageSnapshotTable.AddRange(
                new StorePageSnapshot { AppId = 1, CapturedAt = 1, Fingerprint = "a", Facts = Facts().ToJson() },
                new StorePageSnapshot { AppId = 1, CapturedAt = 2, Fingerprint = "b", Facts = Facts(header: "b/header.jpg").ToJson() },
                new StorePageSnapshot { AppId = 2, CapturedAt = 1, Fingerprint = "c", Facts = Facts().ToJson() });
            await db.SaveChangesAsync();
        }
        Steam.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

        (await Archive().DownloadCoversAsync()).Should().Be(2);

        Directory.GetFiles(Archive().CoverDirectory).Should().HaveCount(2);
        Steam.Requests.Should().OnlyContain(request => request.Cookie == null);
    }

    [Test]
    public async Task TheCoverSwitchTurnsDownloadsOff()
    {
        Settings.Current.Archive = new SteamStat.Core.Settings.ArchiveSettings { StoreCoversEnabled = false };

        (await Archive().DownloadCoversAsync()).Should().Be(0);
        Steam.Requests.Should().BeEmpty();
    }
}

[TestFixture]
public sealed class RecordingCollectorTests : ArchiveFixture
{
    private string _root = null!;

    [SetUp]
    public void SetUpRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-recording-tests", Guid.NewGuid().ToString("N"), "gamerecordings");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void RemoveRoot() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    private RecordingCollector Collector() => new(DbFactory, null!, Clock, NullLogger<RecordingCollector>.Instance);

    private void AddBackground(string name, int chunks)
    {
        var folder = Path.Combine(_root, "video", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "session.mpd"), "x");
        for (var index = 1; index <= chunks; index++) File.WriteAllBytes(Path.Combine(folder, $"chunk-stream0-{index:D5}.m4s"), new byte[10]);
    }

    private void AddTimeline(string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "timelines"));
        File.WriteAllText(Path.Combine(_root, "timelines", $"{name}.json"), json);
    }

    private RecordingScan Scan() => GameRecordingScanner.ScanAccount(_root, 40, Me);

    [Test]
    public async Task IndexesAVideoFolderAndItsTimeline()
    {
        AddBackground("bg_730_20260327_050839", 4);
        AddTimeline("timeline_73020260327_050839", """{"endtime":"90000","entries":[{"time":"1000","type":"usermarker","title":"Ace"}]}""");

        await Collector().SaveAsync(Scan());

        await using var db = DbFactory.CreateDbContext();
        var media = await db.RecordingMediaTable.SingleAsync();
        media.AppId.Should().Be(730);
        media.DurationMs.Should().Be(12000, "没有索引时按每片 3 秒估算");
        media.SizeBytes.Should().Be(41);
        media.StartedAt.Should().Be(new DateTimeOffset(2026, 3, 27, 5, 8, 39, TimeSpan.Zero).ToUnixTimeSeconds());
        var timeline = await db.RecordingTimelineTable.SingleAsync();
        timeline.DurationMs.Should().Be(90000);
        RecordingCollector.ParseMarkers(timeline.Markers).Should().ContainSingle().Which.Title.Should().Be("Ace");
    }

    [Test]
    public async Task VideoRemovedBySteamKeepsItsRecord()
    {
        AddBackground("bg_730_20260327_050839", 1);
        await Collector().SaveAsync(Scan());

        Directory.Delete(Path.Combine(_root, "video"), recursive: true);
        await Collector().SaveAsync(Scan());

        await using var db = DbFactory.CreateDbContext();
        (await db.RecordingMediaTable.SingleAsync()).MissingSince.Should().NotBeNull();
    }

    [Test]
    public async Task MarkersSurviveTheFileGoingAway()
    {
        AddTimeline("timeline_73020260327_050839", """{"entries":[{"time":"1000","type":"usermarker","title":"Ace"}]}""");
        await Collector().SaveAsync(Scan());

        File.WriteAllText(Path.Combine(_root, "timelines", "timeline_73020260327_050839.json"), """{"entries":[{"time":"2000","type":"usermarker","title":"Later"}]}""");
        await Collector().SaveAsync(Scan());

        await using var db = DbFactory.CreateDbContext();
        RecordingCollector.ParseMarkers((await db.RecordingTimelineTable.SingleAsync()).Markers).Select(marker => marker.Title).Should().Equal("Ace", "Later");
    }

    [Test]
    public void ABrokenTimelineIsSkippedNotFatal()
    {
        AddTimeline("timeline_73020260327_050839", "{ not json");
        var errors = new List<Exception>();

        var scan = GameRecordingScanner.ScanAccount(_root, 40, Me, errors.Add);

        scan.Timelines.Should().BeEmpty();
        errors.Should().ContainSingle();
    }

    [Test]
    public void ReadsACustomFolderFromLocalConfig()
    {
        var user = Path.Combine(Path.GetDirectoryName(_root)!, "user");
        Directory.CreateDirectory(Path.Combine(user, "config"));
        var custom = Path.Combine(Path.GetDirectoryName(_root)!, "elsewhere");
        Directory.CreateDirectory(Path.Combine(custom, "gamerecordings", "video"));
        File.WriteAllText(Path.Combine(user, "config", "localconfig.vdf"), $"\"Root\"\n{{\n\t\"GameRecording\"\n\t{{\n\t\t\"BackgroundRecordPath\"\t\t\"{custom}\"\n\t}}\n}}\n");

        GameRecordingScanner.ResolveRoot(user).Should().Be(Path.Combine(custom, "gamerecordings"));
    }

    [Test]
    public void FallsBackToTheDefaultFolder()
    {
        var user = Path.GetDirectoryName(_root)!;

        GameRecordingScanner.ResolveRoot(user).Should().Be(_root);
    }
}

[TestFixture]
public sealed class PlayerHistoryTests : ArchiveFixture
{
    private string _mirror = null!;
    private PublicDataService _publicData = null!;

    [SetUp]
    public void SetUpMirror()
    {
        _mirror = Path.Combine(Path.GetTempPath(), "steam-stat-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_mirror, "players", "2026-01"));
        _publicData = QueryFactory.PublicData(DbFactory, Paths, Settings, Clock);
        _publicData.HistoryMirrorsOverride = [_mirror];
    }

    [TearDown]
    public void RemoveMirror() => Directory.Delete(_mirror, recursive: true);

    private PlayerHistoryService History() => new(DbFactory, _publicData, Paths, Clock, NullLogger<PlayerHistoryService>.Instance);

    private PlayerCountCollector Collector() => new(DbFactory, Gateway, new AlwaysOn(), History(), Catalog, Settings, Clock) { Spacing = TimeSpan.Zero };

    private sealed class AlwaysOn : IPublicDataSwitch
    {
        public bool Enabled => true;
    }

    private void Publish(int appId, int?[] days, bool tamper = false)
    {
        var shard = PlayerHistoryFormat.ShardOf(appId);
        var content = JsonSerializer.SerializeToUtf8Bytes(new { format = PlayerHistoryFormat.ShardFormat, version = 1, month = "2026-01", shard, apps = new Dictionary<string, int?[]> { [appId.ToString()] = days } });
        File.WriteAllBytes(Path.Combine(_mirror, "players", "2026-01", $"{shard}.json"), content);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tamper ? [1] : content));
        File.WriteAllText(Path.Combine(_mirror, "index.json"), JsonSerializer.Serialize(new
        {
            format = PlayerHistoryFormat.IndexFormat,
            version = 1,
            updatedAt = 100,
            apps = new[] { appId },
            months = new Dictionary<string, object[]> { ["2026-01"] = [new { shard, sha256 = sha, size = content.Length }] }
        }));
    }

    [Test]
    public async Task MergesTheBranchWithLocalSamples()
    {
        Publish(570, [100, null, 300]);
        await using (var db = DbFactory.CreateDbContext())
        {
            db.AppPlayerCountTable.AddRange(
                new AppPlayerCount { AppId = 570, Day = "2026-01-03", Peak = 999, Low = 1, Samples = 2 },
                new AppPlayerCount { AppId = 570, Day = "2026-01-04", Peak = 400, Low = 1, Samples = 1 },
                new AppPlayerCount { AppId = 570, Day = "2026-01-05", Peak = 0, Low = 0, Samples = 0 });
            await db.SaveChangesAsync();
        }

        var curve = await History().GetCurveAsync(570, 12);

        curve.Select(point => (point.Players, point.Source)).Should().Equal((100, "public"), (300, "public"), (400, "local"));
    }

    [Test]
    public async Task ATamperedShardIsIgnored()
    {
        Publish(570, [100], tamper: true);

        (await History().GetCurveAsync(570, 12)).Should().BeEmpty();
    }

    [Test]
    public async Task AMissingBranchIsNotAnError()
    {
        File.Delete(Path.Combine(_mirror, "index.json"));

        (await History().GetCoverageAsync()).Should().BeEmpty();
        (await History().GetCurveAsync(570, 12)).Should().BeEmpty();
    }

    [Test]
    public async Task ALocalMirrorThatDoesNotExistIsNotAnError()
    {
        // 开发时 STEAM_STAT_PUBLIC_DATA 指向的目录可能没有 history 子目录，路径不能当成地址用
        _publicData.HistoryMirrorsOverride = [Path.Combine(_mirror, "nothing-here")];

        (await History().GetCurveAsync(570, 12)).Should().BeEmpty();
    }

    [Test]
    public async Task AnOpenCircuitIsNotAnError()
    {
        var publicData = QueryFactory.PublicData(DbFactory, Paths, Settings, Clock, new ThrowingClients(new Polly.CircuitBreaker.BrokenCircuitException()));
        publicData.HistoryMirrorsOverride = ["https://example.invalid/history"];
        var history = new PlayerHistoryService(DbFactory, publicData, Paths, Clock, NullLogger<PlayerHistoryService>.Instance);

        (await history.GetCurveAsync(570, 12)).Should().BeEmpty();
    }

    private sealed class ThrowingClients(Exception exception) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Thrower(exception));

        private sealed class Thrower(Exception exception) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
        }
    }

    [Test]
    public async Task OnlyGamesTheBranchDoesNotCoverAreSampled()
    {
        Publish(570, [100]);
        Catalog.Owned.AddRange([new InsightOwnedGame(570, "Hot", 60, 5), new InsightOwnedGame(9, "Cold", 60, 5)]);

        await Collector().CollectAsync(default);

        Gateway.AskedPlayers.Should().Equal(9);
    }

    [Test]
    public async Task EachGameIsSampledOnceADay()
    {
        Catalog.Owned.Add(new InsightOwnedGame(9, "Cold", 60, 5));
        var collector = Collector();

        await collector.CollectAsync(default);
        await collector.CollectAsync(default);

        Gateway.AskedPlayers.Should().Equal(9);
    }

    [Test]
    public async Task GamesWithNoPlayerDataAreLeftAloneForAWhile()
    {
        Catalog.Owned.Add(new InsightOwnedGame(9, "Cold", 60, 5));
        Gateway.Players[9] = -1;
        var collector = Collector();
        for (var day = 0; day < 3; day++)
        {
            await collector.CollectAsync(default);
            Clock.Now += TimeSpan.FromDays(1);
        }
        Gateway.AskedPlayers.Clear();

        await collector.CollectAsync(default);

        Gateway.AskedPlayers.Should().BeEmpty();
    }

    [Test]
    public async Task RepeatedFailuresEndThePass()
    {
        Catalog.Owned.AddRange(Enumerable.Range(1, 20).Select(id => new InsightOwnedGame(id, "G", 60, 5)));
        foreach (var id in Enumerable.Range(1, 20)) Gateway.Players[id] = null;

        var outcome = await Collector().CollectAsync(default);

        outcome.Error.Should().Be(CollectorErrors.Network);
        Gateway.AskedPlayers.Should().HaveCount(5);
    }
}

[TestFixture]
public sealed class ArchiveQueryTests : ArchiveFixture
{
    private ArchiveQueryService Query() => QueryFactory.Create(DbFactory, Paths, Settings, Clock);

    [Test]
    public async Task PurchasesAreNewestFirstAndPaged()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.PurchaseRecordTable.AddRange(Enumerable.Range(1, 3).Select(index => new PurchaseRecord { SteamId = Me, RowKey = $"txn:{index}", Date = index, Kind = "purchase", TypeText = "Purchase", TotalCents = 100, FirstSeenAt = 1 }));
            await db.SaveChangesAsync();
        }

        var first = await Query().GetPurchasesAsync(new PurchasePageRequest { Offset = 0, Limit = 2 });
        var last = await Query().GetPurchasesAsync(new PurchasePageRequest { Offset = first.NextOffset!.Value, Limit = 2 });

        first.Total.Should().Be(3);
        first.Items.Select(item => item.Date).Should().Equal(3, 2);
        last.NextOffset.Should().BeNull();
    }

    [Test]
    public async Task HiddenGamesDoNotLeakThroughThePurchaseView()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.PurchaseRecordTable.AddRange(
                new PurchaseRecord { SteamId = Me, RowKey = "a", Date = 1, Kind = "purchase", TypeText = "p", FirstSeenAt = 1, Items = """[{"name":"Secret","appId":620,"source":"exact","allocatedCents":1,"shared":false}]""" },
                new PurchaseRecord { SteamId = Me, RowKey = "b", Date = 1, Kind = "purchase", TypeText = "p", FirstSeenAt = 1, Items = """[{"name":"Other","appId":6200,"source":"exact","allocatedCents":1,"shared":false}]""" },
                new PurchaseRecord { SteamId = Me, RowKey = "c", Date = 1, Kind = "market", TypeText = "m", FirstSeenAt = 1, Items = """[{"name":"Market","appId":null,"source":"none","allocatedCents":1,"shared":false}]""" });
            await db.SaveChangesAsync();
        }
        Settings.Current.Analytics!.HiddenAppIds = [620];
        var semantic = new SemanticLayerService(Paths, Settings, NullLogger<SemanticLayerService>.Instance);
        await semantic.InstallAsync();

        var result = await semantic.QueryForIpcAsync(new SemanticQueryRequest { Sql = "SELECT items FROM v_purchase" });

        result.Status.Should().Be("ok");
        result.RowsJson.Should().NotContain("Secret").And.Contain("Other").And.Contain("Market");
    }

    [Test]
    public async Task TheScopeFiltersByAccount()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.PurchaseRecordTable.AddRange(
                new PurchaseRecord { SteamId = Me, RowKey = "a", Date = 1, Kind = "purchase", TypeText = "p", FirstSeenAt = 1 },
                new PurchaseRecord { SteamId = Other, RowKey = "a", Date = 1, Kind = "purchase", TypeText = "p", FirstSeenAt = 1 });
            await db.SaveChangesAsync();
        }

        (await Query().GetPurchasesAsync(new PurchasePageRequest { SteamIds = [Other], Offset = 0, Limit = 10 })).Items.Should().ContainSingle().Which.SteamId.Should().Be(Other);
    }

    [Test]
    public async Task FriendDeltasCompareWithTheLastValueBeforeTheWindow()
    {
        const string Friend = "76561198000000099";
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        await using (var db = DbFactory.CreateDbContext())
        {
            db.FriendPlaytimeSnapshotTable.AddRange(
                new FriendPlaytimeSnapshot { SteamId = Friend, AppId = 1, PlaytimeMinutes = 100, ObservedAt = now - 86400L * 60 },
                new FriendPlaytimeSnapshot { SteamId = Friend, AppId = 1, PlaytimeMinutes = 160, ObservedAt = now - 86400L * 5 },
                new FriendPlaytimeSnapshot { SteamId = Friend, AppId = 2, PlaytimeMinutes = 50, ObservedAt = now - 86400L * 2 });
            await db.SaveChangesAsync();
        }

        var result = await Query().GetFriendPlaytimeAsync(new FriendPlaytimeRequest { SteamId = Friend, Days = 30 });

        result.Games.First(game => game.AppId == 1).DeltaMinutes.Should().Be(60);
        result.Games.First(game => game.AppId == 2).DeltaMinutes.Should().Be(0, "第一次观测是基线，不知道这段时间玩了多少");
    }

    [Test]
    public async Task PalettesAreSavedAndPendingShrinks()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.ScreenshotArchiveTable.AddRange(
                new ScreenshotArchive { AccountId = 1, AppId = 1, FileName = "a", FilePath = "/a.jpg", TakenAt = 10 },
                new ScreenshotArchive { AccountId = 1, AppId = 1, FileName = "b", FilePath = "/b.jpg", TakenAt = 20 },
                new ScreenshotArchive { AccountId = 1, AppId = 1, FileName = "c", FilePath = "/c.jpg", TakenAt = 30, MissingSince = 5 });
            await db.SaveChangesAsync();
        }
        var pending = await Query().GetPalettePendingAsync(new ScreenshotPaletteRequest { Limit = 10 });
        pending.Should().HaveCount(2, "文件不在的不用分析");

        await Query().SavePalettesAsync(new ScreenshotPaletteSaveRequest { Items = [new ScreenshotPaletteItem { Id = pending[0], Palette = "#112233" }, new ScreenshotPaletteItem { Id = pending[1], Palette = string.Empty }] });

        var spectrum = await Query().GetSpectrumAsync();
        spectrum.Colors.Should().Equal("#112233");
        spectrum.Pending.Should().Be(0, "读不出来的不再重试");
    }

    [Test]
    public async Task ThePaletteImageIsSentAsADataUrlAndMissingFilesAreNot()
    {
        var file = Path.Combine(Paths.UserDataDirectory, "shot.jpg");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        int id;
        await using (var db = DbFactory.CreateDbContext())
        {
            var row = new ScreenshotArchive { AccountId = 1, AppId = 1, FileName = "a", FilePath = file };
            db.ScreenshotArchiveTable.Add(row);
            await db.SaveChangesAsync();
            id = row.Id;
        }

        (await Query().GetPaletteImageAsync(id)).Should().Be("data:image/jpeg;base64,AQID");
        (await Query().GetPaletteImageAsync(id + 1)).Should().BeNull();
    }

    [Test]
    public async Task RichPresenceDefaultsToYourOwnRecords()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.RichPresenceRecordTable.AddRange(
                new RichPresenceRecord { AccountName = MeAccount, SteamId = Me, IsSelf = true, AppId = 1, RawValues = """{"map":"Mine"}""", ObservedAt = Clock.GetUtcNow().ToUnixTimeSeconds() - 100 },
                new RichPresenceRecord { AccountName = MeAccount, SteamId = "7", IsSelf = false, AppId = 1, RawValues = """{"map":"Theirs"}""", ObservedAt = Clock.GetUtcNow().ToUnixTimeSeconds() - 100 });
            await db.SaveChangesAsync();
        }

        var mine = await Query().GetRichPresenceAsync(new RichPresenceMiningRequest { AppId = 1 });

        mine.Dimensions.Single().Values.Single().Value.Should().Be("Mine");
    }

    [Test]
    public async Task PioneerUsesTheFirstObservation()
    {
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        await using (var db = DbFactory.CreateDbContext())
        {
            db.AchievementUnlockArchiveTable.AddRange(Enumerable.Range(0, 5).Select(index => new AchievementUnlockArchive
            {
                AccountName = MeAccount,
                SteamId = Me,
                AppId = 1,
                InternalName = $"A{index}",
                UnlockTime = now - 86400L * 100,
                RecordedAt = now - 86400L * 100,
                FirstGlobalPercent = 1,
                FirstPercentAt = now - 86400L * 100,
                GlobalPercent = 10
            }));
            await db.SaveChangesAsync();
        }

        (await Query().GetPioneerAsync(new ArchiveScopeRequest())).Score.Should().Be(90);
    }
}

[TestFixture]
public sealed class StoreRegionServiceTests : ArchiveFixture
{
    private sealed class NoSessions : ISteamSessionAccessor, ISteamAccountIdentityProvider
    {
        public IReadOnlyList<string> GetLoggedInUsers() => [];
        public bool TryGetSession(string accountName, out ISteamSession session) => throw new NotSupportedException();

        public bool TryGetSteamId(string accountName, out ulong steamId)
        {
            steamId = 0;
            return false;
        }
    }

    private StoreRegionService Service() => new(DbFactory, Gateway, Session, Session, Settings, new QueryFactory.NoLanguage(), Clock);

    private StoreRegionService LoggedOut()
    {
        var none = new NoSessions();
        return new StoreRegionService(DbFactory, Gateway, none, none, Settings, new QueryFactory.NoLanguage(), Clock);
    }

    private async Task AddPurchases(string currency, int count)
    {
        await using var db = DbFactory.CreateDbContext();
        db.PurchaseRecordTable.AddRange(Enumerable.Range(0, count).Select(index => new PurchaseRecord { SteamId = Me, RowKey = $"{currency}{index}", Date = 1, Kind = "purchase", TypeText = "p", Currency = currency, FirstSeenAt = 1 }));
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task TheAccountsWalletRegionComesFirst()
    {
        Gateway.Country = "CN";
        await AddPurchases("USD", 5);

        var region = await Service().ResolveAsync(null);

        region.Should().Match<StoreRegionDto>(item => item.Region == "CN" && item.Source == "account" && item.Detected == "CN");
    }

    [Test]
    public async Task TheUserCanOverrideIt()
    {
        Gateway.Country = "CN";
        Settings.Current.Archive = new SteamStat.Core.Settings.ArchiveSettings { StoreRegion = "jp" };

        var region = await Service().ResolveAsync(null);

        region.Region.Should().Be("JP");
        region.Source.Should().Be("manual");
        region.Detected.Should().Be("CN", "设置页要能显示切回自动之后是哪个区");
    }

    [Test]
    public async Task ABadOverrideIsIgnored()
    {
        Gateway.Country = "CN";
        Settings.Current.Archive = new SteamStat.Core.Settings.ArchiveSettings { StoreRegion = "??" };

        (await Service().ResolveAsync(null)).Source.Should().Be("account");
    }

    [Test]
    public async Task TheRegionIsRememberedWhenLoggedOut()
    {
        Gateway.Country = "CN";
        await Service().ResolveAsync(null);

        (await LoggedOut().ResolveAsync(null)).Region.Should().Be("CN");
    }

    [Test]
    public async Task SteamIsAskedAtMostOnceAWeek()
    {
        Gateway.Country = "CN";
        var service = Service();

        await service.ResolveAsync(null);
        await service.ResolveAsync(null);
        Gateway.CountryCalls.Should().Be(1);

        Clock.Now += TimeSpan.FromDays(8);
        Gateway.Country = "TR";
        (await service.ResolveAsync(null)).Region.Should().Be("TR", "换区要能跟上");
    }

    [Test]
    public async Task AFailedLookupKeepsTheOldRegion()
    {
        Gateway.Country = "CN";
        await Service().ResolveAsync(null);
        Clock.Now += TimeSpan.FromDays(8);
        Gateway.Country = null;

        (await Service().ResolveAsync(null)).Region.Should().Be("CN");
    }

    [Test]
    public async Task BillingIsTheFallback()
    {
        await AddPurchases("JPY", 3);
        await AddPurchases("USD", 1);

        (await Service().ResolveAsync(null)).Should().Match<StoreRegionDto>(item => item.Region == "JP" && item.Source == "billing");
    }

    [Test]
    public async Task TheLanguageIsTheLastResort()
        => (await Service().ResolveAsync(null)).Should().Match<StoreRegionDto>(item => item.Region == "US" && item.Source == "language");

    [Test]
    public async Task TheSelectedAccountDecidesBetweenRegions()
    {
        await using (var db = DbFactory.CreateDbContext())
            await CollectorData.WriteAsync(db, "storeRegion", new Dictionary<string, object> { [Me] = new { Region = "CN", At = Clock.GetUtcNow().ToUnixTimeSeconds() }, [Other] = new { Region = "TR", At = Clock.GetUtcNow().ToUnixTimeSeconds() - 1 } }, default);

        (await Service().ResolveAsync([Other])).Region.Should().Be("TR");
    }
}

[TestFixture]
public sealed class SimulatorServiceTests : ArchiveFixture
{
    private sealed class AlwaysOn : IPublicDataSwitch
    {
        public bool Enabled => true;
    }

    private static string Item(string price, string? text = null, bool visible = true) => visible
        ? $$$"""{"response":{"store_items":[{"id":9,"visible":true,"name":"Target","tagids":[1,2,3],"best_purchase_option":{"final_price_in_cents":"{{{price}}}","formatted_final_price":"{{{text}}}"}}]}}"""
        : """{"response":{"store_items":[{"id":9,"visible":false,"success":15}]}}""";

    private SimulatorService Service()
    {
        var clients = new Clients(Steam);
        var archive = new StoreArchiveService(DbFactory, clients, Paths, Settings, NullLogger<StoreArchiveService>.Instance);
        var store = new StoreStatusService(DbFactory, clients, Catalog, new AlwaysOn(), Settings, archive, Clock, NullLogger<StoreStatusService>.Instance);
        var regions = new StoreRegionService(DbFactory, Gateway, Session, Session, Settings, new QueryFactory.NoLanguage(), Clock);
        return new SimulatorService(DbFactory, Gateway, store, regions, Catalog, new AlwaysOn(), new QueryFactory.NoLanguage(), Settings, Clock);
    }

    private static string RegionOf(HttpRequestMessage request)
        => Uri.UnescapeDataString(request.RequestUri!.Query).Contains("\"country_code\":\"CN\"", StringComparison.Ordinal) ? "CN" : "US";

    [Test]
    public async Task ThePriceIsTheUsersRegionAndCurrency()
    {
        Gateway.Country = "CN";
        Steam.Respond = request => Page(RegionOf(request) == "CN" ? Item("3500", "¥35.00") : Item("999", "$9.99"));

        var result = await Service().RunAsync(new SimulatorRunRequest { AppId = 9 });

        result.Should().Match<SimulatorResultDto>(item => item.PriceCents == 3500 && item.PriceText == "¥35.00" && item.Currency == "CNY" && item.Region == "CN" && !item.PriceIsManual && !item.RegionUnavailable);
    }

    [Test]
    public async Task ADiscountCountsAtTheDiscountedPrice()
    {
        Gateway.Country = "CN";
        Steam.Respond = _ => Page("""{"response":{"store_items":[{"id":9,"visible":true,"name":"T","tagids":[1],"best_purchase_option":{"final_price_in_cents":"1750","original_price_in_cents":"3500","formatted_final_price":"¥17.50"}}]}}""");

        (await Service().RunAsync(new SimulatorRunRequest { AppId = 9 })).PriceCents.Should().Be(1750);
    }

    [Test]
    public async Task ARegionLockedGameUsesTheGlobalPageWithoutItsPrice()
    {
        Gateway.Country = "CN";
        Steam.Respond = request => Page(RegionOf(request) == "CN" ? Item("0", visible: false) : Item("999", "$9.99"));

        var result = await Service().RunAsync(new SimulatorRunRequest { AppId = 9 });

        result.Status.Should().Be("ok");
        result.RegionUnavailable.Should().BeTrue();
        result.PriceCents.Should().BeNull("美区的价格不是这个区的价格");
        result.PriceText.Should().BeNull();
    }

    [Test]
    public async Task AManualPriceWins()
    {
        Gateway.Country = "CN";
        Steam.Respond = _ => Page(Item("3500", "¥35.00"));

        var result = await Service().RunAsync(new SimulatorRunRequest { AppId = 9, PriceCents = 2000 });

        result.Should().Match<SimulatorResultDto>(item => item.PriceCents == 2000 && item.PriceIsManual && item.PriceText == null && item.Currency == "CNY");
    }

    [Test]
    public async Task TheOverrideDecidesWhichRegionIsAsked()
    {
        Gateway.Country = "US";
        Settings.Current.Archive = new SteamStat.Core.Settings.ArchiveSettings { StoreRegion = "CN" };
        Steam.Respond = request => Page(RegionOf(request) == "CN" ? Item("3500", "¥35.00") : Item("999", "$9.99"));

        (await Service().RunAsync(new SimulatorRunRequest { AppId = 9 })).Region.Should().Be("CN");
    }
}
