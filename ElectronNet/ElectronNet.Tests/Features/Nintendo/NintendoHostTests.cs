using System.Net;
using System.Text;
using ElectronNet.Features.DataTransfer;
using ElectronNet.Features.Nintendo;
using ElectronNet.Features.Sync;
using ElectronNet.Services;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Nintendo;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.Nintendo;

[TestFixture]
public sealed class NintendoHostTests : InsightHostFixture
{
    private const string NaId = "abcdef0123456789";
    private const string Zelda = "0100f2c0115b6000";

    private FakeNintendo _nintendo = null!;
    private FakeWindow _window = null!;
    private FakeSecrets _secrets = null!;
    private NintendoService _service = null!;

    private long Now => Clock.Now.ToUnixTimeSeconds();

    [SetUp]
    public void SetUpNintendo()
    {
        Settings.Current.Nintendo = new NintendoSettings { Enabled = true, RiskAcknowledged = true, PollMinutes = 15, HiddenTitleIds = [] };
        _nintendo = new FakeNintendo();
        _window = new FakeWindow();
        _secrets = new FakeSecrets();
        _service = new NintendoService(DbFactory, new NintendoClient(new Clients(_nintendo)), _secrets, Settings, _window, Bus, Clock, NullLogger<NintendoService>.Instance);
    }

    [TearDown]
    public void TearDownNintendo() => _service.Dispose();

    private async Task<NintendoLoginResultDto> LinkAsync()
    {
        var begin = await _service.BeginLoginAsync();
        begin.WindowOpened.Should().BeTrue();
        var state = Uri.UnescapeDataString(_window.Url!.Query.Split('&').Single(part => part.TrimStart('?').StartsWith("state=")).Split('=')[1]);
        return await _service.CompleteLoginAsync(new NintendoLoginCompleteRequest
        {
            LoginId = begin.LoginId,
            CallbackUrl = $"npf5c38e31cd085304b://auth#session_token_code=the-code&state={Uri.EscapeDataString(state)}&session_state=x"
        });
    }

    [Test]
    public async Task Linking_StoresTheTokenProtectedAndMakesTheFirstAccountMine()
    {
        var result = await LinkAsync();

        result.Status.Should().Be("linked");
        result.Account!.Mine.Should().BeTrue();
        _window.Closed.Should().BeTrue();
        await using var db = DbFactory.CreateDbContext();
        var credential = await db.NintendoCredentialTable.SingleAsync();
        credential.SessionToken.Should().Be("protected:session-token");
        (await db.NintendoAccountTable.SingleAsync()).Nickname.Should().Be("Link");
        Bus.Events.OfType<NintendoStatusChanged>().Should().NotBeEmpty();
    }

    [Test]
    public async Task ACallbackIsOnlyAcceptedOnceAndOnlyWithItsState()
    {
        var begin = await _service.BeginLoginAsync();
        (await _service.CompleteLoginAsync(new NintendoLoginCompleteRequest { LoginId = begin.LoginId, CallbackUrl = "npf5c38e31cd085304b://auth#session_token_code=x&state=forged" }))
            .Status.Should().Be("stateMismatch");
        (await _service.CompleteLoginAsync(new NintendoLoginCompleteRequest { LoginId = "someone-else", CallbackUrl = "npf5c38e31cd085304b://auth#session_token_code=x&state=x" }))
            .Status.Should().Be("expired");

        var linked = await LinkAsync();
        linked.Status.Should().Be("linked");
        (await _service.CompleteLoginAsync(new NintendoLoginCompleteRequest { LoginId = linked.LoginId, CallbackUrl = "npf5c38e31cd085304b://auth#session_token_code=x&state=x" }))
            .Status.Should().Be("expired");
    }

    [Test]
    public async Task NothingIsRequestedUntilTheRiskNoticeIsAcknowledged()
    {
        Settings.Current.Nintendo!.RiskAcknowledged = false;

        (await _service.BeginLoginAsync()).WindowOpened.Should().BeFalse();
        (await _service.SyncAsync(null, null)).Status.Should().Be("inactive");
        _nintendo.Requests.Should().BeEmpty();
        _window.Url.Should().BeNull();
    }

    [Test]
    public async Task Syncing_WritesTotalsDaysAndPlateaus_AndOnlyChangesAreRewritten()
    {
        await LinkAsync();
        (await _service.SyncAsync(null, null)).Status.Should().Be("ok");

        await using (var db = DbFactory.CreateDbContext())
        {
            (await db.NintendoTitleTotalTable.CountAsync()).Should().Be(2);
            (await db.NintendoDailyPlaytimeTable.SumAsync(row => row.Minutes)).Should().Be(160);
            (await db.NintendoPlaytimeSnapshotTable.CountAsync()).Should().Be(2);
            (await db.NintendoTitleTable.SingleAsync(row => row.TitleId == Zelda && row.Platform == "switch")).Name.Should().Be("Zelda");
        }

        Clock.Now = Clock.Now.AddMinutes(15);
        var firstPoll = Now;
        await _service.SyncAsync(null, null);
        Clock.Now = Clock.Now.AddMinutes(15);
        _nintendo.SwitchMinutes = 1010;
        await _service.SyncAsync(null, null);

        await using (var db = DbFactory.CreateDbContext())
        {
            var segments = await db.NintendoPlaytimeSnapshotTable.Where(row => row.Platform == "switch").OrderBy(row => row.TotalMinutes).ToListAsync();
            segments.Select(row => row.TotalMinutes).Should().Equal(1000, 1010);
            segments[0].LastObservedAt.Should().Be(firstPoll, "the poll that still saw the old value closes the window");
            segments[1].FirstObservedAt.Should().Be(Now);
        }

        var dataset = await _service.GetDatasetAsync(new NintendoScopeRequest());
        dataset.Accounts.Should().Equal(NaId);
        dataset.Titles.Should().HaveCount(2);
        dataset.Totals.Single(total => dataset.Titles[total.Title].Platform == "switch").TotalMinutes.Should().Be(1010);
        dataset.Daily.Should().HaveCount(3 * 4);
        dataset.Intervals.Should().HaveCount(5);
        dataset.Intervals[2].Should().Be((int)Now - 600);
        dataset.Intervals[3].Should().Be((int)Now);
        dataset.Intervals[4].Should().Be(10);
        dataset.PlacedMinutes.Should().Be(10);
    }

    [Test]
    public async Task TheRevision_ChangesOnlyWhenTheDataDoes()
    {
        await LinkAsync();
        await _service.SyncAsync(null, null);
        var first = (await _service.GetRevisionAsync()).Revision;
        Clock.Now = Clock.Now.AddMinutes(15);
        await _service.SyncAsync(null, null);
        (await _service.GetRevisionAsync()).Revision.Should().Be(first);

        _nintendo.SwitchMinutes = 1100;
        Clock.Now = Clock.Now.AddMinutes(15);
        await _service.SyncAsync(null, null);
        (await _service.GetRevisionAsync()).Revision.Should().NotBe(first);
    }

    [Test]
    public async Task ARevokedLogin_IsDroppedAndTheAccountAsksToSignInAgain()
    {
        await LinkAsync();
        _nintendo.Revoked = true;

        (await _service.SyncAsync(null, null)).Status.Should().Be("failed");

        var status = await _service.GetStatusAsync();
        status.Accounts.Single().State.Should().Be("signedOut");
        await using var db = DbFactory.CreateDbContext();
        (await db.NintendoCredentialTable.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task AChangedReply_IsReportedAsALayoutProblem()
    {
        await LinkAsync();
        _nintendo.Broken = true;

        await _service.SyncAsync(null, null);

        (await _service.GetStatusAsync()).Accounts.Single().State.Should().Be("layout");
    }

    [Test]
    public async Task WithoutAKeyring_TheLoginOnlyLivesInMemory()
    {
        _secrets.CanPersist = false;
        await LinkAsync();

        await using (var db = DbFactory.CreateDbContext())
            (await db.NintendoCredentialTable.CountAsync()).Should().Be(0);
        (await _service.SyncAsync(null, null)).Status.Should().Be("ok");
    }

    [Test]
    public async Task TheWeeklySection_CountsMineOnlyAndSkipsHiddenTitles()
    {
        await LinkAsync();
        await _service.SyncAsync(null, null);

        var week = await _service.SummarizeAsync("2026-11-16", "2026-11-22");
        week!.Seconds.Should().Be(160 * 60);
        week.Games.Should().Be(2);
        week.ActiveDays.Should().Be(2);
        week.Top[0].Name.Should().Be("Zelda");

        Settings.Current.Nintendo!.HiddenTitleIds = [Zelda];
        (await _service.SummarizeAsync("2026-11-16", "2026-11-22")).Should().BeNull();

        Settings.Current.Nintendo.HiddenTitleIds = [];
        await _service.UpdateAccountAsync(new NintendoAccountUpdateRequest { NaId = NaId, Mine = false });
        (await _service.SummarizeAsync("2026-11-16", "2026-11-22"))!.Seconds.Should().Be(160 * 60, "nobody marked as mine means every account");
        (await _service.SummarizeAsync("2026-11-01", "2026-11-07")).Should().BeNull();
    }

    [Test]
    public async Task Unlinking_DeletesTheLoginAndOptionallyTheData()
    {
        await LinkAsync();
        await _service.SyncAsync(null, null);

        (await _service.UnlinkAccountAsync(new NintendoAccountUnlinkRequest { NaId = NaId, DeleteData = false })).Accounts.Single().State.Should().Be("signedOut");
        await using (var db = DbFactory.CreateDbContext())
            (await db.NintendoDailyPlaytimeTable.CountAsync()).Should().BeGreaterThan(0);

        (await _service.UnlinkAccountAsync(new NintendoAccountUnlinkRequest { NaId = NaId, DeleteData = true })).Accounts.Should().BeEmpty();
        await using (var db = DbFactory.CreateDbContext())
        {
            (await db.NintendoDailyPlaytimeTable.CountAsync()).Should().Be(0);
            (await db.NintendoTitleTotalTable.CountAsync()).Should().Be(0);
        }
    }

    [Test]
    public void CredentialsNeverLeaveTheDevice()
    {
        BackupFormat.StrippedTables.Should().Contain("nintendo_credential");
        DatabaseJsonExporter.ExcludedTables.Should().Contain("nintendo_credential");
        SyncSchema.ByName.Should().NotContainKey("nintendo_credential");
        SyncSchema.ByName.Should().ContainKeys("nintendo_account", "nintendo_title", "nintendo_title_total", "nintendo_playtime_snapshot", "nintendo_daily_playtime");
        SyncSchema.ByName["nintendo_account"].Columns.Should().NotContain(["last_sync_at", "last_success_at", "last_error"]);
    }

    [Test]
    public async Task TheWeeklyIssue_CarriesTheNintendoSection()
    {
        await LinkAsync();
        await _service.SyncAsync(null, null);
        Clock.Now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        await PlayAsync(10, Unix(2026, 11, 18), 3600);
        using var weekly = new WeeklyService(DbFactory, Insights, Tags, Settings, Bus, Clock, NullLogger<WeeklyService>.Instance, _service);

        await weekly.RunOnceAsync();

        await using var db = DbFactory.CreateDbContext();
        var payload = (await db.WeeklyIssueTable.SingleAsync(issue => issue.Kind == "weekly")).Payload;
        payload.Should().Contain("\"nintendo\":{\"seconds\":9600");
    }

    private sealed class FakeNintendo : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int SwitchMinutes { get; set; } = 1000;
        public bool Revoked { get; set; }
        public bool Broken { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            return Task.FromResult(path switch
            {
                "/connect/1.0.0/api/session_token" => Json("""{"session_token":"session-token","code":"c"}"""),
                "/connect/1.0.0/api/token" when Revoked => Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest),
                "/connect/1.0.0/api/token" => Json("""{"access_token":"access","id_token":"id","expires_in":900}"""),
                "/2.0.0/users/me" => Json($$"""{"id":"{{NaId.ToUpperInvariant()}}","nickname":"Link","country":"JP"}"""),
                "/api/v2.0/users/me/play_histories" when Broken => Json("<html>maintenance</html>"),
                "/api/v2.0/users/me/play_histories" => Json($$"""
                    {"playHistories":[
                      {"titleId":"{{Zelda}}","titleName":"Zelda","platform":"Nintendo Switch","totalPlayedMinutes":{{SwitchMinutes}},"totalPlayedDays":40,"lastPlayedAt":"2026-11-20T22:00:00Z"},
                      {"titleId":"{{Zelda}}","titleName":"Zelda","platform":"Nintendo Switch 2","totalPlayedMinutes":300,"totalPlayedDays":5}
                    ],
                    "recentPlayHistories":[
                      {"playedDate":"2026-11-18","dailyPlayHistories":[{"titleId":"{{Zelda}}","titleName":"Zelda","platform":"Nintendo Switch","totalPlayedMinutes":40}]},
                      {"playedDate":"2026-11-19","dailyPlayHistories":[
                        {"titleId":"{{Zelda}}","titleName":"Zelda","platform":"Nintendo Switch","totalPlayedMinutes":90},
                        {"titleId":"{{Zelda}}","titleName":"Zelda","platform":"Nintendo Switch 2","totalPlayedMinutes":30}]}
                    ]}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeWindow : INintendoLoginWindow
    {
        public Uri? Url { get; private set; }
        public bool Closed { get; private set; }

        public Task<bool> OpenAsync(Uri url, Action<string> onCallback, Action onClosed, CancellationToken cancellationToken = default)
        {
            Url = url;
            Closed = false;
            return Task.FromResult(true);
        }

        public void Close() => Closed = true;
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public bool CanPersist { get; set; } = true;
        public string? Protect(string? plainText) => plainText == null ? null : "protected:" + plainText;
        public string? Unprotect(string? protectedText) => protectedText?.StartsWith("protected:") == true ? protectedText["protected:".Length..] : null;
        public bool IsProtected(string? value) => value?.StartsWith("protected:") == true;
    }
}
