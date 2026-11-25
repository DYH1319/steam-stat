using System.Net;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Nintendo;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Tests.Nintendo;

[TestFixture]
public sealed class NintendoLoginTests
{
    [Test]
    public void TheLoginUrl_CarriesAnS256ChallengeOfTheVerifier()
    {
        var login = NintendoLoginRequest.Create();
        var query = login.Url.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('='))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

        login.Url.GetLeftPart(UriPartial.Path).Should().Be(NintendoEndpoints.Authorize.AbsoluteUri);
        query["client_id"].Should().Be(NintendoEndpoints.ClientId);
        query["redirect_uri"].Should().Be("npf5c38e31cd085304b://auth");
        query["response_type"].Should().Be("session_token_code");
        query["state"].Should().Be(login.State);
        query["session_token_code_challenge_method"].Should().Be("S256");
        query["session_token_code_challenge"].Should().Be(NintendoLoginRequest.Challenge(login.CodeVerifier));
        login.CodeVerifier.Should().NotContainAny("=", "+", "/");
        NintendoLoginRequest.Create().State.Should().NotBe(login.State);
    }

    [Test]
    public void TheCallback_IsReadFromTheFragmentAndChecksTheState()
    {
        NintendoCallback.Parse("npf5c38e31cd085304b://auth#session_state=x&session_token_code=abc.def&state=S1", "S1")
            .Should().Be(new NintendoCallback("abc.def", null));
        NintendoCallback.Parse("  NPF5C38E31CD085304B://auth#state=S1&session_token_code=abc  ", "S1").Code.Should().Be("abc");
        NintendoCallback.Parse("npf5c38e31cd085304b://auth#session_token_code=abc&state=other", "S1").Error.Should().Be(NintendoCallback.StateMismatch);
        NintendoCallback.Parse("npf5c38e31cd085304b://auth#error=access_denied&state=S1", "S1").Error.Should().Be(NintendoCallback.Denied);
        NintendoCallback.Parse("https://accounts.nintendo.com/connect/1.0.0/authorize?state=S1", "S1").Error.Should().Be(NintendoCallback.NotCallback);
        NintendoCallback.Parse("npf5c38e31cd085304b://auth#state=S1", "S1").Error.Should().Be(NintendoCallback.NotCallback);
        NintendoCallback.Parse(null, "S1").Error.Should().Be(NintendoCallback.NotCallback);
    }

    [Test]
    public void OnlyTheListedEndpointsMayCarryTokens()
    {
        NintendoEndpoints.IsAllowed(NintendoEndpoints.PlayHistories).Should().BeTrue();
        NintendoEndpoints.IsAllowed(NintendoEndpoints.Token).Should().BeTrue();
        NintendoEndpoints.IsAllowed(new Uri("https://app-api.znej.nintendo.com/api/v2.0/users/me/play_histories?x=1")).Should().BeFalse();
        NintendoEndpoints.IsAllowed(new Uri("http://app-api.znej.nintendo.com/api/v2.0/users/me/play_histories")).Should().BeFalse();
        NintendoEndpoints.IsAllowed(new Uri("https://example.com/api/v2.0/users/me/play_histories")).Should().BeFalse();
    }

    [Test]
    public void ChineseTriesChineseTitlesFirst()
    {
        NintendoEndpoints.LocalesFor("zh-CN").Should().Equal("zh-CN", "en-US");
        NintendoEndpoints.LocalesFor("en-US").Should().Equal("en-US");
        NintendoEndpoints.LocalesFor(null).Should().Equal("en-US");
    }
}

[TestFixture]
public sealed class PlayHistoryParserTests
{
    internal const string Sample = """
        {
          "playHistories": [
            {"titleId":"0100F2C0115B6000","titleName":"Zelda: Tears of the Kingdom","platform":"Nintendo Switch","imageUrl":"https://img.example.test/totk.jpg",
             "firstPlayedAt":"2023-05-12T09:00:00+09:00","lastPlayedAt":"2026-11-20T22:15:00Z","lastUpdatedAt":"2026-11-20T22:30:00Z","totalPlayedDays":120,"totalPlayedMinutes":18000},
            {"titleId":"0100F2C0115B6000","titleName":"Zelda: Tears of the Kingdom","platform":"Nintendo Switch 2","imageUrl":"https://img.example.test/totk.jpg",
             "firstPlayedAt":"2026-06-05T18:00:00Z","lastPlayedAt":"2026-11-19T23:30:00Z","totalPlayedDays":22,"totalPlayedMinutes":2400},
            {"titleId":"01006A800016E000","titleName":"Smash","deviceType":"HAC","imageUrl":"http://insecure.example.test/a.jpg",
             "firstPlayedAt":"2019-01-01T00:00:00","lastPlayedAt":"2020-01-01","totalPlayedDays":"30","totalPlayedMinutes":"900"},
            {"titleName":"No id"},
            {"titleId":"bad id!","totalPlayedMinutes":1},
            "not an object"
          ],
          "recentPlayHistories": [
            {"playedDate":"2026-11-20","dailyPlayHistories":[
              {"titleId":"0100F2C0115B6000","titleName":"Zelda: Tears of the Kingdom","platform":"Nintendo Switch","totalPlayedMinutes":95},
              {"titleId":"0100F2C0115B6000","titleName":"Zelda: Tears of the Kingdom","platform":"Nintendo Switch","totalPlayedMinutes":5}]},
            {"playedDate":"2026-11-19","dailyPlayHistories":[
              {"titleId":"0100F2C0115B6000","titleName":"Zelda: Tears of the Kingdom","platform":"Nintendo Switch 2","totalPlayedMinutes":60}]},
            {"playedDate":"not a date","dailyPlayHistories":[]}
          ],
          "hiddenTitleList": [],
          "someNewField": {"x": 1}
        }
        """;

    [Test]
    public void TitlesAreKeptPerPlatformAndBadEntriesAreSkipped()
    {
        var history = PlayHistoryParser.Parse(Sample);

        history.Titles.Should().HaveCount(3);
        var onSwitch = history.Titles.Single(title => title.TitleId == "0100f2c0115b6000" && title.Platform == NintendoPlatforms.Switch);
        onSwitch.TotalMinutes.Should().Be(18000);
        onSwitch.TotalDays.Should().Be(120);
        onSwitch.FirstPlayedAt.Should().Be(new DateTimeOffset(2023, 5, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        onSwitch.ImageUrl.Should().Be("https://img.example.test/totk.jpg");
        history.Titles.Single(title => title.Platform == NintendoPlatforms.Switch2).TotalMinutes.Should().Be(2400);

        var smash = history.Titles.Single(title => title.TitleId == "01006a800016e000");
        smash.Platform.Should().Be(NintendoPlatforms.Switch, "the old revision calls it deviceType");
        smash.TotalMinutes.Should().Be(900);
        smash.ImageUrl.Should().BeNull("only https covers are loaded");
        smash.FirstPlayedAt.Should().Be(new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Test]
    public void DaysAreSortedAndDuplicateEntriesAreAdded()
    {
        var history = PlayHistoryParser.Parse(Sample);

        history.Recent.Select(day => day.PlayedDate).Should().Equal("2026-11-19", "2026-11-20");
        history.Recent[1].Titles.Should().ContainSingle().Which.Minutes.Should().Be(100);
        history.Recent[0].Titles.Single().Platform.Should().Be(NintendoPlatforms.Switch2);
    }

    [Test]
    public void AnEmptyOrUnexpectedReplyDoesNotThrowForMissingLists()
    {
        PlayHistoryParser.Parse("{}").Titles.Should().BeEmpty();
        PlayHistoryParser.Parse("""{"playHistories": {"x":1}}""").Titles.Should().BeEmpty();
        FluentActions.Invoking(() => PlayHistoryParser.Parse("[]")).Should().Throw<System.Text.Json.JsonException>();
    }

    [TestCase("Nintendo Switch", NintendoPlatforms.Switch)]
    [TestCase("Nintendo Switch 2", NintendoPlatforms.Switch2)]
    [TestCase("NINTENDO_SWITCH_2", NintendoPlatforms.Switch2)]
    [TestCase("HAC", NintendoPlatforms.Switch)]
    [TestCase("BEE", NintendoPlatforms.Switch2)]
    [TestCase("Wii U", NintendoPlatforms.Other)]
    [TestCase(null, NintendoPlatforms.Other)]
    public void PlatformsAreNormalized(string? label, string expected)
        => NintendoPlatforms.Normalize(label).Should().Be(expected);
}

[TestFixture]
public sealed class NintendoInferenceTests
{
    private const long T0 = 1_800_000_000;

    private static NintendoSegment Segment(int minutes, long first, long last, long? played = null, string title = "a")
        => new(title, NintendoPlatforms.Switch, minutes, first, last, played);

    [Test]
    public void ADeltaInsideAShortWindow_IsPlacedBeforeTheReading()
    {
        var result = NintendoPlaytimeInference.Infer([Segment(100, T0 - 86400, T0), Segment(130, T0 + 3600, T0 + 3600)]);

        result.Intervals.Should().ContainSingle().Which.Should().Be(new NintendoInterval("a", NintendoPlatforms.Switch, T0 + 1800, T0 + 3600, 30));
        result.PlacedMinutes.Should().Be(30);
        result.UnplacedMinutes.Should().Be(0);
    }

    [Test]
    public void TheLastPlayedTime_AnchorsTheEndOrTheStart()
    {
        NintendoPlaytimeInference.Place(T0, T0 + 3600, 10, T0 + 1800).Should().Be((T0 + 1200, T0 + 1800));
        NintendoPlaytimeInference.Place(T0, T0 + 3600, 50, T0 + 600).Should().Be((T0 + 600, T0 + 3600));
        NintendoPlaytimeInference.Place(T0, T0 + 3600, 10, T0 - 600).Should().Be((T0 + 3000, T0 + 3600), "a stale last-played time is ignored");
    }

    [Test]
    public void LongWindowsAndOversizedDeltas_AreNotPlaced()
    {
        var result = NintendoPlaytimeInference.Infer([
            Segment(100, T0, T0),
            Segment(130, T0 + 5 * 3600, T0 + 5 * 3600),
            Segment(200, T0 + 5 * 3600 + 900, T0 + 5 * 3600 + 900)
        ]);

        result.Intervals.Should().BeEmpty();
        result.UnplacedMinutes.Should().Be(100);
    }

    [Test]
    public void AShrinkingTotal_StartsOverWithoutANegativeInterval()
    {
        var result = NintendoPlaytimeInference.Infer([Segment(100, T0, T0), Segment(90, T0 + 600, T0 + 600), Segment(100, T0 + 1200, T0 + 1200)]);

        result.Intervals.Should().ContainSingle().Which.Minutes.Should().Be(10);
    }

    [Test]
    public void NeighbouringReadings_MergeIntoOneSession()
    {
        var result = NintendoPlaytimeInference.Infer([
            Segment(0, T0, T0),
            Segment(15, T0 + 900, T0 + 900),
            Segment(30, T0 + 1800, T0 + 1800),
            Segment(40, T0 + 2700, T0 + 2700),
            Segment(10, T0, T0, title: "b"),
            Segment(20, T0 + 900, T0 + 900, title: "b")
        ]);

        result.Intervals.Should().HaveCount(2);
        var merged = result.Intervals.Single(interval => interval.TitleId == "a");
        merged.Start.Should().Be(T0);
        merged.End.Should().Be(T0 + 2700);
        merged.Minutes.Should().Be(40);
    }
}

[TestFixture]
public sealed class NintendoClientTests
{
    private static readonly NintendoTokens Tokens = new("access", "id");

    [Test]
    public async Task ARevokedSessionToken_IsAnAuthFailureNotANetworkFailure()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"The provided grant is invalid"}"""));
        var client = new NintendoClient(new Clients(handler));

        await FluentActions.Awaiting(() => client.GetTokensAsync("session")).Should().ThrowAsync<NintendoAuthException>();
        handler.Requests.Single().Headers.UserAgent.ToString().Should().Be(NintendoEndpoints.AccountsUserAgent);
    }

    [Test]
    public async Task TheSessionTokenExchange_PostsTheVerifier()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"session_token":"long-lived","code":"x"}"""));
        var token = await new NintendoClient(new Clients(handler)).ExchangeSessionTokenAsync("code", "verifier");

        token.Should().Be("long-lived");
        handler.Bodies.Single().Should().Contain("session_token_code=code").And.Contain("session_token_code_verifier=verifier").And.Contain($"client_id={NintendoEndpoints.ClientId}");
    }

    [Test]
    public async Task PlayHistory_FallsBackToTheIdTokenAndThenToEnglish()
    {
        var handler = new Handler(request =>
        {
            var bearer = request.Headers.Authorization!.Parameter;
            var locale = request.Headers.GetValues("gentry-locale").Single();
            if (locale == "zh-CN") return Json(HttpStatusCode.BadRequest, """{"code":"invalid_locale"}""");
            return bearer == "id" ? Json(HttpStatusCode.OK, PlayHistoryParserTests.Sample) : Json(HttpStatusCode.Unauthorized, "{}");
        });
        var history = await new NintendoClient(new Clients(handler)).GetPlayHistoryAsync(Tokens, ["zh-CN", "en-US"]);

        history.Titles.Should().HaveCount(3);
        handler.Requests.Select(request => request.Headers.GetValues("gentry-locale").Single()).Should().Equal("zh-CN", "en-US", "en-US");
        handler.Requests.Should().OnlyContain(request => request.Headers.UserAgent.ToString() == NintendoEndpoints.AppUserAgent);
    }

    [Test]
    public async Task ARefusedPlayHistory_IsAnAuthFailure()
    {
        var client = new NintendoClient(new Clients(new Handler(_ => Json(HttpStatusCode.Forbidden, "{}"))));
        await FluentActions.Awaiting(() => client.GetPlayHistoryAsync(Tokens, ["en-US"])).Should().ThrowAsync<NintendoAuthException>();
    }

    [Test]
    public async Task AServerError_StaysAnHttpFailure()
    {
        var client = new NintendoClient(new Clients(new Handler(_ => Json(HttpStatusCode.ServiceUnavailable, "down"))));
        await FluentActions.Awaiting(() => client.GetPlayHistoryAsync(Tokens, ["en-US"])).Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task TheUser_IsReadAndSanitized()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"id":"ABCDEF0123456789","nickname":"Link","iconUri":"https://cdn.example.test/mii.png","country":"JP"}"""));
        var user = await new NintendoClient(new Clients(handler)).GetUserAsync(Tokens);

        user.Should().Be(new NintendoUser("abcdef0123456789", "Link", "https://cdn.example.test/mii.png", "JP"));
        handler.Requests.Single().Headers.Authorization!.ToString().Should().Be("Bearer access");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content != null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

[TestFixture]
public sealed class NintendoSettingsAndRedactionTests
{
    [Test]
    public void TheFeatureIsOnlyActiveAfterTheRiskNotice()
    {
        NintendoSettings.IsActive(new AppSettings { Nintendo = new NintendoSettings { Enabled = true, RiskAcknowledged = false } }).Should().BeFalse();
        NintendoSettings.IsActive(new AppSettings { Nintendo = new NintendoSettings { Enabled = true, RiskAcknowledged = true } }).Should().BeTrue();
        NintendoSettings.IsActive(new AppSettings()).Should().BeFalse();
    }

    [Test]
    public void HiddenTitleIdsAreNormalized()
        => NintendoSettings.NormalizeTitleIds([" 0100ABC ", "0100abc", "bad id", ""]).Should().Equal("0100abc");

    [Test]
    public void NintendoTokensAreRedacted()
    {
        var redactor = new DiagnosticsRedactor(DiagnosticsRedactionContext.Empty);
        var text = redactor.Redact("session_token=abc123 session_token_code: xyz id_token=qqq");

        text.Should().NotContainAny("abc123", "xyz", "qqq");
    }
}

/// <summary>
/// 用真实账号核对接口的形状（P0 验证）。需要环境变量 STEAM_STAT_NINTENDO_SESSION_TOKEN：
/// 在开发版里登录一次后，从调试日志之外的渠道取得（例如 nxapi 的 My Nintendo 登录），不要提交到任何地方。
/// 只输出数量、主机、日期范围这些不含身份的信息。
/// </summary>
[TestFixture, Explicit("Talks to Nintendo with a real account"), Category("RealSystem")]
public sealed class NintendoRealAccountSmokeTests
{
    [Test]
    public async Task PlayHistory_HasTheExpectedShape()
    {
        var sessionToken = System.Environment.GetEnvironmentVariable("STEAM_STAT_NINTENDO_SESSION_TOKEN");
        if (string.IsNullOrWhiteSpace(sessionToken)) Assert.Ignore("STEAM_STAT_NINTENDO_SESSION_TOKEN is not set");

        var client = new NintendoClient(new RealClients());
        var tokens = await client.GetTokensAsync(sessionToken!);
        var user = await client.GetUserAsync(tokens);
        var history = await client.GetPlayHistoryAsync(tokens, NintendoEndpoints.LocalesFor(System.Environment.GetEnvironmentVariable("STEAM_STAT_NINTENDO_LANGUAGE") ?? "zh-CN"));

        TestContext.Out.WriteLine($"country={user.Country} titles={history.Titles.Count} recentDays={history.Recent.Count}");
        TestContext.Out.WriteLine($"platforms={string.Join(',', history.Titles.Select(title => title.Platform).Distinct())}");
        TestContext.Out.WriteLine($"days={history.Recent.FirstOrDefault()?.PlayedDate}..{history.Recent.LastOrDefault()?.PlayedDate}");
        TestContext.Out.WriteLine($"withLastPlayed={history.Titles.Count(title => title.LastPlayedAt != null)} withImage={history.Titles.Count(title => title.ImageUrl != null)}");
        history.Titles.Should().NotBeEmpty();
    }

    private sealed class RealClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(30) };
    }
}
