using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElectronNet.Models;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;
using SteamStat.Core.Features;
using SteamStat.Core.PublicData;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.PublicData;

[TestFixture]
public sealed class PublicDataServiceTests
{
    private string _root = null!;
    private string _mirror = null!;
    private AppPaths _paths = null!;
    private TestDbContextFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-public-data-tests", Guid.NewGuid().ToString("N"));
        _mirror = Path.Combine(_root, "mirror");
        _paths = new AppPaths(Path.Combine(_root, "UserData"));
        Directory.CreateDirectory(_paths.DatabaseDirectory);
        _factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.SteamAppTable.Add(new SteamApp { AppId = 570, Name = "Dota 2", NameLocalizedJson = "{}" });
        db.UseAppRecordTable.Add(new UseAppRecord { AppId = 250900, SteamId = "765", StartTime = 1, EndTime = 2, Duration = 1 });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public async Task Refresh_DownloadsOnlyNeededVerifiedShardsAndServesMetadata()
    {
        WriteMirror(generatedAt: 100);
        using var service = CreateService("schinese");

        var status = await service.RefreshAsync();

        status.LastError.Should().BeNull();
        status.GeneratedAt.Should().Be(100);
        status.TotalApps.Should().Be(3);
        status.CachedApps.Should().Be(2, "the shard of app 99999 is not used on this device");
        status.Source.Should().Be("local");

        var meta = service.GetAppMeta([570, 250900, 99999]);
        meta[570].Genres.Should().Be("动作; 策略");
        meta[570].Tags.Should().Be("MOBA; Free to Play");
        meta[250900].Developers.Should().Be("Nicalis, Inc.; Edmund McMillen");
        meta.ContainsKey(99999).Should().BeFalse();

        var schema = await service.TryGetSchemaAsync(570, "schinese");
        schema!.Definitions.Select(definition => definition.LocalizedName).Should().Equal("胜利");
        (await service.TryGetSchemaAsync(250900, "schinese")).Should().BeNull();
    }

    [Test]
    public async Task Refresh_RejectsTamperedShardsAndKeepsANewerLocalManifest()
    {
        WriteMirror(generatedAt: 200);
        using var service = CreateService("english");
        await service.RefreshAsync();

        // CDN 返回较旧的清单：继续使用本地较新的
        WriteMirror(generatedAt: 150, dotaName: "Old");
        var status = await service.RefreshAsync();
        status.GeneratedAt.Should().Be(200);

        // 分片内容被篡改（哈希不符）：丢弃
        WriteMirror(generatedAt: 300, dotaName: "Dota 2 Reborn", tamper: true);
        status = await service.RefreshAsync();
        status.GeneratedAt.Should().Be(300);
        status.LastError.Should().Be("invalid");
        service.GetAppMeta([570]).Should().NotContainKey(570);
    }

    [Test]
    public async Task Disabled_DoesNothing()
    {
        WriteMirror(generatedAt: 100);
        using var service = CreateService("english");
        (await service.SetEnabledAsync(false)).Enabled.Should().BeFalse();
        (await service.RefreshAsync()).GeneratedAt.Should().BeNull();
        (await service.TryGetSchemaAsync(570, "english")).Should().BeNull();
    }

    /// <summary>手动运行：dotnet test --filter Category=RealNetwork，验证线上 data 分支与客户端格式一致。</summary>
    [Test, Explicit, Category("RealNetwork")]
    public async Task LiveMirror_ServesAManifestAndShardsTheClientAccepts()
    {
        using var service = new PublicDataService(_factory, _paths, new RealHttp(), new FakeSettings(), new FixedLanguage("schinese"), TimeProvider.System, NullLogger<PublicDataService>.Instance);
        var status = await service.RefreshAsync();
        status.LastError.Should().BeNull();
        status.TotalApps.Should().BeGreaterThan(0);
        status.CachedApps.Should().BeGreaterThan(0, "Dota 2 (570) is in the seed list");
        service.GetAppMeta([570])[570].Genres.Should().NotBeNullOrEmpty();
    }

    private sealed class RealHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(30) };
    }

    [Test]
    public void Manifest_ValidationRejectsUnexpectedPaths()
    {
        var shard = new PublicDataShard { Id = "3a", Path = "apps/3a.0123456789abcdef.json", Sha256 = "0123456789abcdef" + new string('0', 48), Size = 10, Count = 1 };
        PublicDataFormat.IsValid(Manifest(shard)).Should().BeTrue();
        PublicDataFormat.IsValid(Manifest(new PublicDataShard { Id = "3a", Path = "../evil.json", Sha256 = shard.Sha256, Size = 10, Count = 1 })).Should().BeFalse();
        PublicDataFormat.IsValid(Manifest(new PublicDataShard { Id = "3a", Path = shard.Path, Sha256 = shard.Sha256, Size = PublicDataFormat.MaxShardBytes + 1, Count = 1 })).Should().BeFalse();

        static PublicDataManifest Manifest(PublicDataShard shard) => new()
        {
            Format = PublicDataFormat.Format, Version = 1, GeneratedAt = 1,
            Datasets = new() { ["apps"] = new PublicDataset { Shards = [shard] } }
        };
    }

    private void WriteMirror(long generatedAt, string dotaName = "Dota 2", bool tamper = false)
    {
        if (Directory.Exists(_mirror)) Directory.Delete(_mirror, true);
        var apps = new Dictionary<string, Dictionary<string, object>>
        {
            ["3a"] = new() { ["570"] = new { name = dotaName, genres = new[] { 1, 2 }, tags = new[] { "MOBA", "Free to Play" }, developers = new[] { "Valve" } } },
            ["14"] = new() { ["250900"] = new { name = "Isaac", developers = new[] { "Nicalis, Inc.", "Edmund McMillen" } } },
            ["9f"] = new() { ["99999"] = new { name = "Unused" } }
        };
        var achievements = new Dictionary<string, Dictionary<string, object>>
        {
            ["3a"] = new() { ["570"] = new { fetchedAt = 1, languages = new Dictionary<string, object> { ["schinese"] = new[] { new { name = "WIN", displayName = "胜利", description = "", icon = "", iconGray = "", hidden = false } } } } }
        };
        var datasets = new Dictionary<string, object>
        {
            ["apps"] = new { shards = WriteShards("apps", apps, tamper) },
            ["achievements"] = new { shards = WriteShards("achievements", achievements, false) }
        };
        var manifest = new
        {
            format = PublicDataFormat.Format, version = 1, generatedAt,
            genres = new Dictionary<string, object> { ["1"] = new { english = "Action", schinese = "动作" }, ["2"] = new { english = "Strategy", schinese = "策略" } },
            datasets
        };
        File.WriteAllText(Path.Combine(_mirror, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private List<object> WriteShards(string dataset, Dictionary<string, Dictionary<string, object>> shards, bool tamper)
    {
        var list = new List<object>();
        Directory.CreateDirectory(Path.Combine(_mirror, dataset));
        foreach (var (id, apps) in shards)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = $"steam-stat-public-data/{dataset}", version = 1, shard = id, apps }));
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var path = $"{dataset}/{id}.{sha[..16]}.json";
            File.WriteAllBytes(Path.Combine(_mirror, path.Replace('/', Path.DirectorySeparatorChar)), tamper ? bytes.Select(b => b == (byte)'D' ? (byte)'d' : b).ToArray() : bytes);
            list.Add(new { id, path, sha256 = sha, size = bytes.Length, count = apps.Count });
        }
        return list;
    }

    private PublicDataService CreateService(string language)
        => new(_factory, _paths, new NoHttp(), new FakeSettings(), new FixedLanguage(language), TimeProvider.System, NullLogger<PublicDataService>.Instance)
        {
            MirrorsOverride = [_mirror]
        };

    private sealed class FixedLanguage(string language) : ILanguageProvider
    {
        public string GetSteamLanguage() => language;
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in tests");
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public AppSettings GetSettings() => new();
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
