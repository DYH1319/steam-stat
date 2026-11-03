using ElectronNet.Features.Diagnostics;
using ElectronNet.Hosting;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.Diagnostics;

[TestFixture]
public sealed class CrashReportServiceTests
{
    private string _userData = null!;

    [SetUp]
    public void SetUp() => _userData = Path.Combine(Path.GetTempPath(), "steam-stat-crash-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_userData)) Directory.Delete(_userData, recursive: true);
    }

    [Test]
    public void Disabled_WritesNothing()
    {
        var service = CreateService(enabledInSettings: false);

        service.RecordHostException(new InvalidOperationException("boom"), "unhandled").Should().BeNull();
        Directory.Exists(service.Directory).Should().BeFalse();
    }

    [Test]
    public void BeforeSettingsInitialize_ReadsTheSettingsFile()
    {
        var service = CreateService(enabledInSettings: true);

        service.RecordHostException(new InvalidOperationException("boom"), "fatal").Should().NotBeNull();
    }

    [Test]
    public void Record_RedactsAndMergesTheSameFingerprint()
    {
        var service = CreateService();
        service.SetEnabled(true);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var first = service.RecordRenderer(new RendererCrashReportRequest
        {
            Kind = "rendererError",
            ExceptionType = "TypeError",
            Message = $"Cannot read {home}/secret for 76561198000000001",
            StackTrace = "at render (http://localhost:9000/src/a.ts:1:2)"
        })!;
        var second = service.RecordRenderer(new RendererCrashReportRequest
        {
            Kind = "rendererError",
            ExceptionType = "TypeError",
            Message = "other message",
            StackTrace = "at render (file:///opt/dist/a.js:9:9)"
        })!;

        first.Message.Should().Be("Cannot read ~/secret for [steamid]");
        first.Id.Should().StartWith("crash-");
        second.Id.Should().Be(first.Id);
        service.List().Should().ContainSingle().Which.Count.Should().Be(2);
    }

    [Test]
    public void Record_IsRateLimitedPerProcess()
    {
        var service = CreateService();
        service.SetEnabled(true);

        for (var i = 0; i < CrashReportService.MaxRecordsPerProcess + 10; i++)
            service.RecordHostException(new InvalidOperationException($"boom {i}"), "unobservedTask");

        service.List().Sum(report => report.Count).Should().Be(CrashReportService.MaxRecordsPerProcess);
    }

    [Test]
    public async Task SubmitAndDismiss_UpdateStatus()
    {
        var sink = new RecordingSink();
        var service = CreateService(sink: sink);
        service.SetEnabled(true);
        var submitted = service.RecordHostException(new InvalidOperationException("a"), "unhandled")!;
        var dismissed = service.RecordHostException(new ArgumentException("b"), "unhandled")!;

        (await service.SubmitAsync(submitted.Id, CancellationToken.None)).Success.Should().BeTrue();
        service.Dismiss(dismissed.Id).Should().BeTrue();

        sink.Submitted.Should().ContainSingle().Which.Id.Should().Be(submitted.Id);
        var reports = service.List().ToDictionary(report => report.Id);
        reports[submitted.Id].Status.Should().Be(CrashReportStatuses.Submitted);
        reports[submitted.Id].SubmittedAt.Should().NotBeNull();
        reports[dismissed.Id].Status.Should().Be(CrashReportStatuses.Dismissed);

        // 已提交 / 已忽略的报告不再合并：同一异常再次出现会生成新的待处理记录
        service.RecordHostException(new InvalidOperationException("a"), "unhandled")!.Id.Should().NotBe(submitted.Id);
    }

    [Test]
    public void AutomaticSink_SubmitsNewReportsWithoutUserAction()
    {
        var sink = new RecordingSink { RequiresUserAction = false };
        var service = CreateService(sink: sink);
        service.SetEnabled(true);

        service.RecordHostException(new InvalidOperationException("auto"), "unhandled");

        SpinWait.SpinUntil(() => sink.Submitted.Count == 1, TimeSpan.FromSeconds(2)).Should().BeTrue();
    }

    [Test]
    public void GitHubIssueSink_BuildsALinkWithTheRedactedStack()
    {
        var url = GitHubIssueCrashReportSink.BuildIssueUrl(new CrashReport
        {
            Id = "crash-1-abc",
            Fingerprint = "0123456789abcdef",
            Source = "host",
            Kind = "unhandled",
            ExceptionType = "System.InvalidOperationException",
            Message = "boom\nsecond line",
            StackTrace = string.Concat(Enumerable.Repeat("   at ElectronNet.Services.Foo.Bar()\n", 2000)),
            AppVersion = "4.1.0",
            Os = "Arch Linux",
            Distribution = "appimage",
            FirstSeenAt = 1,
            LastSeenAt = 1,
            Count = 3,
            Status = CrashReportStatuses.Pending
        });

        url.Length.Should().BeLessThanOrEqualTo(GitHubIssueLink.MaxUrlLength);
        var decoded = Uri.UnescapeDataString(url);
        decoded.Should().Contain("[Crash] System.InvalidOperationException: boom&").And.Contain("`0123456789abcdef`").And.Contain("labels=crash");
    }

    private CrashReportService CreateService(bool enabledInSettings = false, ICrashReportSink? sink = null)
    {
        var environment = new AppEnvironment(false, "en-US", false, new AppPaths(_userData), "4.1.0");
        var settings = new StaticSettingsStore(new AppSettings { Diagnostics = new DiagnosticsSettings { CrashReportsEnabled = enabledInSettings } });
        return new CrashReportService(
            environment,
            AppDistribution.FromKind("installer", null, null),
            sink ?? new RecordingSink(),
            new DiagnosticsRedactionProvider(new NoDatabaseFactory(), NullLogger<DiagnosticsRedactionProvider>.Instance),
            settings,
            TimeProvider.System,
            NullLogger<CrashReportService>.Instance);
    }

    private sealed class RecordingSink : ICrashReportSink
    {
        public List<CrashReport> Submitted { get; } = [];
        public string Id => "test";
        public bool RequiresUserAction { get; init; } = true;

        public Task<CrashReportSubmission> SubmitAsync(CrashReport report, CancellationToken cancellationToken)
        {
            lock (Submitted) Submitted.Add(report);
            return Task.FromResult(new CrashReportSubmission(true, "https://example.invalid"));
        }
    }

    private sealed class StaticSettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings GetSettings() => settings;
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class NoDatabaseFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnection("Data Source=:memory:")).Options);
    }
}

[TestFixture]
public sealed class FeedbackIssueTests
{
    private static readonly AppInfoDto Info = new()
    {
        Version = "4.1.0",
        Platform = "linux",
        Arch = "x64",
        OsDescription = "SteamOS",
        Distribution = "appimage",
        UpdateMode = "auto",
        PortableData = false,
        DataDirectory = "/home/deck/.config/steam-stat",
        SecureCredentialStorage = true,
        SessionType = "x11",
        RepositoryUrl = ProjectLinks.RepositoryUrl
    };

    [Test]
    public void BugReport_IncludesEnvironmentAndDiagnosticsHint()
    {
        var (title, body, labels) = DiagnosticsService.BuildIssue(
            new FeedbackIssueRequest { Kind = "bug", Title = "启动后白屏", Description = "打开就白屏" }, Info, english: false);

        title.Should().Be("[Bug] 启动后白屏");
        labels.Should().Equal("bug");
        body.Should().Contain("### 问题描述\n\n打开就白屏").And.Contain("- Steam Stat: 4.1.0 (appimage, auto)")
            .And.Contain("- Platform: linux x64 (x11)").And.Contain("### 诊断包");
        body.Should().NotContain("\r", "the issue body must not depend on the platform newline");
    }

    [Test]
    public void FeatureRequest_InEnglish_WithoutSystemInfo()
    {
        var (title, body, labels) = DiagnosticsService.BuildIssue(new FeedbackIssueRequest { Kind = "feature" }, null, english: true);

        title.Should().Be("[Feature] ");
        labels.Should().Equal("enhancement");
        body.Should().Contain("### What would you like?").And.NotContain("Environment").And.NotContain("Diagnostics");
    }
}
