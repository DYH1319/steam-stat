using ElectronNet.Models;
using ElectronNet.Services;
using ElectronNet.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Platform;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SteamAppServiceTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void BuildInstallDirPath_UsesThePlatformSeparator()
    {
        var library = Path.Combine(_tempDir, "Steam");
        SteamAppService.BuildInstallDirPath(library, "Balatro")
            .Should().Be(Path.Combine(library, "steamapps", "common", "Balatro"));
        SteamAppService.BuildInstallDirPath(null, "Balatro").Should().BeNull();
        SteamAppService.BuildInstallDirPath(library, "").Should().BeNull();
    }

    [Test]
    public async Task SyncDb_StoresNativePathsAndCountsOnlyNewlyRemovedApps()
    {
        using var layout = new TempSteamLayout();
        var library = layout.WithLibrary("Library", "appmanifest_570.acf");
        WriteLibraryFoldersVdf(layout.SteamPath, library);

        var factory = new TestDbContextFactory(Path.Combine(_tempDir, "test.db"));
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.SteamAppTable.AddRange(
                new SteamApp { AppId = 570, Name = "Dota 2", NameLocalizedJson = "{}", Installed = true, InstallDirPath = @"C:\old\path" },
                new SteamApp { AppId = 440, Name = "TF2", NameLocalizedJson = "{}", Installed = true, AppOnDisk = 10 },
                new SteamApp { AppId = 10, Name = "Counter-Strike", NameLocalizedJson = "{}", Installed = false });
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger();
        var locator = new FakeInstallLocator(layout.SteamPath);
        var localFiles = new LocalFileService(NullLogger<LocalFileService>.Instance);
        var service = new SteamAppService(factory, locator, localFiles,
            new GlobalStatusService(factory, locator, localFiles, TimeProvider.System, NullLogger<GlobalStatusService>.Instance), logger);

        await service.SyncDb();

        await using (var db = await factory.CreateDbContextAsync())
        {
            var apps = await db.SteamAppTable.ToDictionaryAsync(app => app.AppId);
            apps[570].InstallDirPath.Should().Be(Path.Combine(library, "steamapps", "common", "dota 2 beta"));
            apps[440].Installed.Should().BeFalse();
            apps[440].AppOnDisk.Should().Be(0);
        }
        logger.Messages.Should().Contain("Synchronized 2 Steam apps: 0 inserted, 1 updated, 1 removed");

        // 再同步一次：已标记为未安装的应用不再计入 removed
        logger.Messages.Clear();
        await service.SyncDb();
        logger.Messages.Should().Contain("Synchronized 1 Steam apps: 0 inserted, 1 updated, 0 removed");
    }

    private static void WriteLibraryFoldersVdf(string steamPath, string libraryPath)
    {
        var path = Path.Combine(steamPath, "config", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{libraryPath.Replace("\\", "\\\\")}}"
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

    private sealed class FakeInstallLocator(string steamPath) : ISteamInstallLocator
    {
        public SteamRegistrySnapshot ReadSteamRegistry() => new() { SteamPath = steamPath };
        public SteamActiveProcessSnapshot ReadActiveProcess() => new(0, 0, "", "", "");
        public IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry() => new Dictionary<int, SteamAppRegistrySnapshot>();
        public bool HasRegistryKey(SteamRegistryKey key) => true;
        public void SetAutoLoginUser(string accountName, bool rememberPassword) { }
    }

    private sealed class CapturingLogger : ILogger<SteamAppService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ElectronNet.Persistence.SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
