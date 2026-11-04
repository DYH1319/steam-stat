using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ElectronNet.Features.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Environment;

namespace ElectronNet.Tests.Features.Diagnostics;

[TestFixture]
public sealed class SentryCrashReportUploaderTests
{
    private const string Dsn = "https://publickey123@o1.ingest.us.sentry.io/42";

    [Test]
    public void Dsn_ParsesTheEnvelopeEndpoint()
    {
        var dsn = SentryDsn.TryParse(Dsn)!;
        dsn.PublicKey.Should().Be("publickey123");
        dsn.EnvelopeUri.Should().Be(new Uri("https://o1.ingest.us.sentry.io/api/42/envelope/"));

        SentryDsn.TryParse("https://key@self.hosted/sentry/7")!.EnvelopeUri.Should().Be(new Uri("https://self.hosted/sentry/api/7/envelope/"));
        SentryDsn.TryParse("").Should().BeNull();
        SentryDsn.TryParse("https://o1.ingest.sentry.io/42").Should().BeNull("缺少公钥");
        SentryDsn.TryParse("https://key@o1.ingest.sentry.io/project").Should().BeNull();
        SentryDsn.TryParse("ftp://key@host/1").Should().BeNull();
    }

    [Test]
    public void Event_ContainsOnlyTheRedactedReportAndDisablesIpInference()
    {
        var report = Report("host", "unhandled", """
            System.InvalidOperationException: boom
               at SteamStat.Core.Foo.Bar() in /src/Foo.cs:line 12
               at System.Threading.Tasks.Task.Run()
               at ElectronNet.Program.Main(String[] args)
            """);

        var json = SentryCrashReportUploader.BuildEvent(report, "abc", Environment());

        json["platform"]!.GetValue<string>().Should().Be("csharp");
        json["level"]!.GetValue<string>().Should().Be("fatal");
        json["release"]!.GetValue<string>().Should().Be("steam-stat@4.1.0-beta.1");
        json["environment"]!.GetValue<string>().Should().Be("beta");
        json["fingerprint"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal("0123456789abcdef");
        json["sdk"]!["settings"]!["infer_ip"]!.GetValue<string>().Should().Be("never");
        json.ContainsKey("user").Should().BeFalse();
        json.ContainsKey("server_name").Should().BeFalse();

        var frames = json["exception"]!["values"]![0]!["stacktrace"]!["frames"]!.AsArray();
        frames.Select(frame => frame!["function"]!.GetValue<string>()).Should().Equal(
            "ElectronNet.Program.Main(String[] args)", "System.Threading.Tasks.Task.Run()", "SteamStat.Core.Foo.Bar()");
        frames[2]!["lineno"]!.GetValue<int>().Should().Be(12);
        frames[2]!["in_app"]!.GetValue<bool>().Should().BeTrue();
        frames[1]!["in_app"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public void Event_ParsesChromiumFrames()
    {
        var report = Report("renderer", "rendererError", """
            TypeError: x is undefined
                at render (file:///app/dist/assets/index-1.js:10:5)
                at file:///app/node_modules/vue/dist/vue.js:3:7
            """);

        var json = SentryCrashReportUploader.BuildEvent(report, "abc", Environment());

        json["platform"]!.GetValue<string>().Should().Be("javascript");
        json["level"]!.GetValue<string>().Should().Be("error");
        var frames = json["exception"]!["values"]![0]!["stacktrace"]!["frames"]!.AsArray();
        frames.Should().HaveCount(2);
        frames[1]!["function"]!.GetValue<string>().Should().Be("render");
        frames[1]!["colno"]!.GetValue<int>().Should().Be(5);
        frames[1]!["in_app"]!.GetValue<bool>().Should().BeTrue();
        frames[0]!["in_app"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public void Envelope_HasHeaderItemHeaderAndPayload()
    {
        var bytes = SentryCrashReportUploader.BuildEnvelope(Report("host", "fatal", "x"), "abc", DateTimeOffset.UnixEpoch, SentryDsn.TryParse(Dsn)!, Environment());
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');

        JsonNode.Parse(lines[0])!["event_id"]!.GetValue<string>().Should().Be("abc");
        var item = JsonNode.Parse(lines[1])!;
        item["type"]!.GetValue<string>().Should().Be("event");
        item["length"]!.GetValue<int>().Should().Be(Encoding.UTF8.GetByteCount(lines[2]));
        JsonNode.Parse(lines[2])!["event_id"]!.GetValue<string>().Should().Be("abc");
    }

    [Test]
    public async Task Upload_PostsToTheEnvelopeEndpointWithAuth()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var uploader = new SentryCrashReportUploader(new SingleClientFactory(handler), Environment(), TimeProvider.System,
            NullLogger<SentryCrashReportUploader>.Instance, SentryDsn.TryParse(Dsn));

        var id = await uploader.UploadAsync(Report("host", "fatal", "x"), CancellationToken.None);

        id.Should().NotBeNullOrEmpty();
        handler.Request!.RequestUri.Should().Be(new Uri("https://o1.ingest.us.sentry.io/api/42/envelope/"));
        handler.Request.Headers.GetValues("X-Sentry-Auth").Single().Should().Contain("sentry_key=publickey123");

        handler.Status = HttpStatusCode.TooManyRequests;
        (await uploader.UploadAsync(Report("host", "fatal", "x"), CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task Upload_WithoutDsnSendsNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var uploader = new SentryCrashReportUploader(new SingleClientFactory(handler), Environment(), TimeProvider.System,
            NullLogger<SentryCrashReportUploader>.Instance, null);

        uploader.IsConfigured.Should().BeFalse();
        (await uploader.UploadAsync(Report("host", "fatal", "x"), CancellationToken.None)).Should().BeNull();
        handler.Request.Should().BeNull();
    }

    /// <summary>手动验证：STEAM_STAT_SENTRY_DSN=... dotnet test --filter Category=RealSentry，会向该项目发送一条测试事件。</summary>
    [Test, Explicit, Category("RealSentry")]
    public async Task RealSentry_AcceptsTheEnvelope()
    {
        var dsn = SentryDsn.TryParse(System.Environment.GetEnvironmentVariable(SentryDsn.EnvironmentVariable));
        if (dsn == null) Assert.Ignore($"{SentryDsn.EnvironmentVariable} is not set");
        var uploader = new SentryCrashReportUploader(new SingleClientFactory(new HttpClientHandler()), Environment(), TimeProvider.System,
            NullLogger<SentryCrashReportUploader>.Instance, dsn);

        var report = Report("host", "unhandled", """
            System.InvalidOperationException: Steam Stat Sentry integration test
               at SteamStat.Core.Diagnostics.SentryIntegration.Verify() in /src/Diagnostics.cs:line 1
            """) with { Message = "Steam Stat Sentry integration test", FirstSeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        (await uploader.UploadAsync(report, CancellationToken.None)).Should().NotBeNull();
    }

    private static AppEnvironment Environment()
        => new(false, "en-US", false, new AppPaths(Path.Combine(Path.GetTempPath(), "steam-stat-sentry")), "4.1.0-beta.1");

    private static CrashReport Report(string source, string kind, string stack) => new()
    {
        Id = "crash-1-0123456789abcdef-aaaaaa",
        Fingerprint = "0123456789abcdef",
        Source = source,
        Kind = kind,
        ExceptionType = "System.InvalidOperationException",
        Message = "boom",
        StackTrace = stack,
        AppVersion = "4.1.0-beta.1",
        Os = "Arch Linux",
        Distribution = "appimage",
        FirstSeenAt = 1,
        LastSeenAt = 1,
        Count = 1,
        Status = CrashReportStatuses.Pending
    };

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
