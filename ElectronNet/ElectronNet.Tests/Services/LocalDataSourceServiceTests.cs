using ElectronNet.Services;
using ElectronNet.Tests.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class LocalDataSourceServiceTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-datasource-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public async Task GetAsync_WhenEverythingPresent_ReportsOkSources()
    {
        using var layout = new TempSteamLayout()
            .WithConfigFile("loginusers.vdf");
        var library = layout.WithLibrary("Steam", "appmanifest_570.acf");
        WriteLibraryFoldersVdf(layout.SteamPath, library);
        WriteFile(Path.Combine(layout.SteamPath, "config", "config.vdf"), "\"root\"\n{\n\t\"key\"\t\t\"value\"\n}\n");
        WriteFile(Path.Combine(layout.SteamPath, "userdata", "123", "config", "localconfig.vdf"), "x");

        var service = CreateService(
            new FakeInstallLocator(layout.SteamPath, hasSteamKey: true, hasActiveProcess: true, hasApps: true, appCount: 3));

        var result = await service.GetAsync();

        result.CheckedAt.Should().BeGreaterThan(0);
        var sources = result.Sources.ToDictionary(s => s.Id);
        sources.Should().HaveCount(9);
        sources["registry.steam"].Status.Should().Be("ok");
        sources["registry.steam"].Path.Should().Be(@"HKCU\Software\Valve\Steam");
        sources["registry.activeProcess"].Status.Should().Be("ok");
        sources["registry.apps"].Status.Should().Be("ok");
        sources["registry.apps"].ItemCount.Should().Be(3);
        sources["file.loginUsersVdf"].Status.Should().Be("ok");
        sources["file.loginUsersVdf"].ItemCount.Should().Be(2);
        sources["file.libraryFoldersVdf"].Status.Should().Be("ok");
        sources["file.libraryFoldersVdf"].ItemCount.Should().Be(1);
        sources["file.appManifests"].Status.Should().Be("ok");
        sources["file.appManifests"].ItemCount.Should().Be(1);
        sources["file.appManifests"].FailedCount.Should().Be(0);
        sources["file.configVdf"].Status.Should().Be("ok");
        sources["file.localConfigVdf"].Status.Should().Be("ok");
        sources["file.localConfigVdf"].ItemCount.Should().Be(1);
        sources["job.appRunningStatus"].Status.Should().Be("ok");
    }

    [Test]
    public async Task GetAsync_WhenNothingPresent_ReportsMissingSources()
    {
        using var layout = new TempSteamLayout();
        var service = CreateService(new FakeInstallLocator(
            layout.SteamPath, hasSteamKey: false, hasActiveProcess: false, hasApps: false),
            jobEnabled: false);

        var result = await service.GetAsync();

        result.Sources.Should().OnlyContain(s => s.Status == "missing");
        result.Sources.Single(s => s.Id == "job.appRunningStatus").ItemCount.Should().Be(0);
    }

    [Test]
    public async Task GetAsync_WhenSteamKeyExistsButPathEmpty_ReportsParseFailed()
    {
        var service = CreateService(new FakeInstallLocator(
            steamPath: "", hasSteamKey: true, hasActiveProcess: false, hasApps: false));

        var result = await service.GetAsync();

        var source = result.Sources.Single(s => s.Id == "registry.steam");
        source.Status.Should().Be("parseFailed");
        source.Error.Should().Be("SteamPath is empty");
        result.Sources.Where(s => s.Kind == "file").Should().OnlyContain(s => s.Path == null);
    }

    [Test]
    public async Task GetAsync_WhenVdfCorrupt_ReportsParseFailed()
    {
        using var layout = new TempSteamLayout();
        WriteFile(Path.Combine(layout.SteamPath, "config", "loginusers.vdf"), "\"users\"\n{\n\t\"broken\"\n");

        var service = CreateService(new FakeInstallLocator(
            layout.SteamPath, hasSteamKey: true, hasActiveProcess: false, hasApps: false));

        var result = await service.GetAsync();

        var source = result.Sources.Single(s => s.Id == "file.loginUsersVdf");
        source.Status.Should().Be("parseFailed");
        source.Error.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task GetAsync_WhenManifestBroken_ReportsParseFailedWithCounts()
    {
        using var layout = new TempSteamLayout().WithConfigFile("loginusers.vdf");
        var library = layout.WithLibrary("Steam", "appmanifest_570.acf", "appmanifest_9999.acf");
        WriteLibraryFoldersVdf(layout.SteamPath, library);

        var service = CreateService(new FakeInstallLocator(
            layout.SteamPath, hasSteamKey: true, hasActiveProcess: false, hasApps: false));

        var result = await service.GetAsync();

        var source = result.Sources.Single(s => s.Id == "file.appManifests");
        source.Status.Should().Be("parseFailed");
        source.ItemCount.Should().Be(2);
        source.FailedCount.Should().Be(1);
    }

    [Test]
    public async Task GetAsync_WhenJobDisabledAndNoRecords_ReportsMissingJob()
    {
        using var layout = new TempSteamLayout();
        var service = CreateService(
            new FakeInstallLocator(layout.SteamPath, hasSteamKey: false, hasActiveProcess: false, hasApps: false),
            jobEnabled: false);

        var result = await service.GetAsync();

        result.Sources.Single(s => s.Id == "job.appRunningStatus").Status.Should().Be("missing");
    }

    private LocalDataSourceService CreateService(FakeInstallLocator locator, bool jobEnabled = true)
        => new(
            locator,
            new LocalFileService(NullLogger<LocalFileService>.Instance),
            new TestDbContextFactory(Path.Combine(_tempDir, "test.db")),
            new FakeSettingsStore(jobEnabled),
            TimeProvider.System,
            NullLogger<LocalDataSourceService>.Instance);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteLibraryFoldersVdf(string steamPath, string libraryPath)
    {
        var escaped = libraryPath.Replace("\\", "\\\\");
        WriteFile(
            Path.Combine(steamPath, "config", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{escaped}}"
            		"label"		""
            		"contentid"		"123"
            		"totalsize"		"0"
            		"update_clean_bytes_tally"		"0"
            		"time_last_update_verified"		"0"
            		"apps"
            		{
            		}
            	}
            }
            """);
    }

    private sealed class FakeInstallLocator(
        string steamPath, bool hasSteamKey, bool hasActiveProcess, bool hasApps, int appCount = 0)
        : ISteamInstallLocator
    {
        public SteamRegistrySnapshot ReadSteamRegistry() => new() { SteamPath = steamPath };
        public SteamActiveProcessSnapshot ReadActiveProcess() => new(0, 0, "", "", "");
        public IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry()
            => Enumerable.Range(0, appCount)
                .ToDictionary(i => i, i => new SteamAppRegistrySnapshot(i, null, null, null, null, null, null, null));
        public bool HasRegistryKey(SteamRegistryKey key) => key switch
        {
            SteamRegistryKey.Steam => hasSteamKey,
            SteamRegistryKey.ActiveProcess => hasActiveProcess,
            SteamRegistryKey.Apps => hasApps,
            _ => false
        };
        public void SetAutoLoginUser(string accountName, bool rememberPassword) { }
    }

    private sealed class FakeSettingsStore(bool jobEnabled) : ISettingsStore
    {
        public AppSettings GetSettings() => new()
        {
            UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings { Enabled = jobEnabled }
        };
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ElectronNet.Persistence.SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext()
        {
            var context = new AppDbContext(_options);
            context.Database.EnsureCreated();
            return context;
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
