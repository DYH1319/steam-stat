using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Features.SaveAnalysis;
using ElectronNet.Infrastructure;
using ElectronNet.Services;
using ElectronNET.Serialization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Services;

[TestFixture]
[NonParallelizable]
public sealed class SaveAnalysisTests
{
    private const string Manifest = """
        {
          "id": "demo-game",
          "version": "1.2.0",
          "apiVersion": 1,
          "name": { "zh-CN": "演示", "en-US": "Demo" },
          "appIds": [570],
          "entry": "parser.js",
          "data": ["data/names.json"],
          "imageHosts": ["static.example.com"],
          "locations": [
            { "id": "cloud", "root": "steamUserdata", "path": "570/remote", "files": ["slot?.sav"] },
            { "id": "docs", "label": "Docs", "root": "documents", "path": "My Games/Demo", "files": ["*.sav"] }
          ],
          "detect": { "files": ["*.sav"], "magic": "DEMO" }
        }
        """;

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(SaveAnalysisService.RepositoryVariable, null);
        Environment.SetEnvironmentVariable(SaveAnalysisService.DevDirectoryVariable, null);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void Manifest_ParsesLocalizedTextAndRejectsUnsafePaths()
    {
        var manifest = SaveAnalysisManifestLoader.Parse(Manifest);
        manifest.Name.ZhCn.Should().Be("演示");
        manifest.Locations[1].Label!.EnUs.Should().Be("Docs");
        manifest.EffectiveMaxFileSize.Should().Be(SaveAnalysisManifest.DefaultMaxFileSize);

        Action escape = () => SaveAnalysisManifestLoader.Parse(Manifest.Replace("\"parser.js\"", "\"../evil.js\""));
        escape.Should().Throw<InvalidDataException>().WithMessage("invalidEntry");
        Action location = () => SaveAnalysisManifestLoader.Parse(Manifest.Replace("My Games/Demo", "../../Windows"));
        location.Should().Throw<InvalidDataException>().WithMessage("invalidLocation");
        Action id = () => SaveAnalysisManifestLoader.Parse(Manifest.Replace("demo-game", "Demo Game"));
        id.Should().Throw<InvalidDataException>().WithMessage("invalidId");
        Action root = () => SaveAnalysisManifestLoader.Parse(Manifest.Replace("\"documents\"", "\"system32\""));
        root.Should().Throw<InvalidDataException>().WithMessage("invalidLocation");
    }

    [Test]
    public void Patterns_AreAnchoredAndVersionsCompareNumerically()
    {
        SaveAnalysisManifestLoader.MatchesPattern("rep+persistentgamedata1.dat", "rep+persistentgamedata?.dat").Should().BeTrue();
        SaveAnalysisManifestLoader.MatchesPattern("20260805.rep_persistentgamedata1.dat", "rep_persistentgamedata?.dat").Should().BeFalse();
        SaveAnalysisManifestLoader.MatchesPattern("20260805.rep_persistentgamedata1.dat", "*.rep_persistentgamedata?.dat").Should().BeTrue();
        SaveAnalysisManifestLoader.CompareVersions("1.10.0", "1.9.3").Should().BePositive();
        SaveAnalysisManifestLoader.CompareVersions("2.0.0-beta", "2.0.0").Should().BeNegative();
        SaveAnalysisManifestLoader.CompareVersions("1.0", "1.0.0").Should().Be(0);
    }

    [Test]
    public void Locator_FindsSavesPerAccountAndDetectsByMagic()
    {
        var steam = Path.Combine(_root, "Steam");
        var cloud = Path.Combine(steam, "userdata", "123", "570", "remote");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "slot1.sav"), "DEMO1");
        File.WriteAllText(Path.Combine(cloud, "slot10.sav"), "ignored by ?");
        File.WriteAllText(Path.Combine(cloud, "other.txt"), "ignored");
        var docs = Path.Combine(_root, "Docs", "My Games", "Demo");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "manual.sav"), "DEMO2");
        var manifest = SaveAnalysisManifestLoader.Parse(Manifest);

        var found = SaveAnalysisLocator.Locate(manifest, new SaveLocatorEnvironment(
            steam, root => root == "documents" ? [Path.Combine(_root, "Docs"), Path.Combine(_root, "Docs")] : [], _ => null));

        found.Select(item => item.Name).Should().BeEquivalentTo("slot1.sav", "manual.sav");
        found.Single(item => item.Name == "slot1.sav").SteamId.Should().Be("76561197960265851");
        found.Single(item => item.Name == "manual.sav").SteamId.Should().BeNull();
        SaveAnalysisLocator.Detects(manifest, "x.sav", "DEMO!"u8).Should().BeTrue();
        SaveAnalysisLocator.Detects(manifest, "x.sav", "NOPE!"u8).Should().BeFalse();
        SaveAnalysisLocator.Detects(manifest, "x.txt", "DEMO!"u8).Should().BeFalse();
    }

    [Test]
    public async Task Repository_InstallsVerifiedFilesAndRejectsTampering()
    {
        var repository = Path.Combine(_root, "repo");
        var plugin = Path.Combine(repository, "plugins", "demo-game");
        Directory.CreateDirectory(Path.Combine(plugin, "data"));
        File.WriteAllText(Path.Combine(plugin, "manifest.json"), Manifest);
        File.WriteAllText(Path.Combine(plugin, "parser.js"), "SteamStat.register({ apiVersion: 1, analyze: () => ({ sections: [] }) })");
        File.WriteAllText(Path.Combine(plugin, "data", "names.json"), "{\"a\":1}");
        WriteIndex(repository, plugin);
        Environment.SetEnvironmentVariable(SaveAnalysisService.RepositoryVariable, repository);
        var service = CreateService(out _);

        var listing = await service.GetRepositoryAsync();
        listing.Success.Should().BeTrue();
        listing.Plugins.Single().InstalledVersion.Should().BeNull();

        var result = await service.InstallAsync("demo-game");
        result.Success.Should().BeTrue();
        result.Version.Should().Be("1.2.0");
        var installed = service.GetPlugins().Plugins.Single();
        installed.Source.Should().Be("user");
        installed.Enabled.Should().BeTrue();
        service.GetPluginFile(new SaveAnalysisPluginFileRequest { PluginId = "demo-game", Path = "data/names.json" }).Content.Should().Be("{\"a\":1}");
        service.GetPluginFile(new SaveAnalysisPluginFileRequest { PluginId = "demo-game", Path = "manifest.json" }).Error.Should().Be("notDeclared");

        File.WriteAllText(Path.Combine(plugin, "parser.js"), "tampered");
        (await service.InstallAsync("demo-game")).Error.Should().Be("integrity");
        service.GetPlugins().Plugins.Single().Version.Should().Be("1.2.0", "a failed update keeps the installed version");

        service.SetEnabled(new SaveAnalysisPluginEnabledRequest { PluginId = "demo-game", Enabled = false }).Should().BeTrue();
        service.GetPlugins().Plugins.Single().Enabled.Should().BeFalse();
        (await service.UninstallAsync("demo-game")).Should().BeTrue();
        service.GetPlugins().Plugins.Should().BeEmpty();
    }

    [Test]
    public async Task ReadSave_OnlyServesIssuedTokensInChunks()
    {
        var steam = Path.Combine(_root, "Steam");
        var cloud = Path.Combine(steam, "userdata", "123", "570", "remote");
        Directory.CreateDirectory(cloud);
        var content = Enumerable.Range(0, 1000).Select(index => (byte)index).ToArray();
        File.WriteAllBytes(Path.Combine(cloud, "slot1.sav"), content);
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(Path.Combine(dev, "demo-game", "data"));
        File.WriteAllText(Path.Combine(dev, "demo-game", "manifest.json"), Manifest);
        File.WriteAllText(Path.Combine(dev, "demo-game", "parser.js"), "//");
        Environment.SetEnvironmentVariable(SaveAnalysisService.DevDirectoryVariable, dev);
        var service = CreateService(out var connection, steam);
        using var _ = connection;

        service.GetPlugins().Plugins.Single().Source.Should().Be("dev");
        var files = await service.LocateAsync("demo-game");
        var token = files.Single(file => file.Name == "slot1.sav").Token;
        (await service.LocateAsync("demo-game")).Single(file => file.Name == "slot1.sav").Token
            .Should().Be(token, "re-locating reuses the token so earlier lists stay readable");

        var first = await service.ReadAsync(new SaveAnalysisReadRequest { Token = token, Offset = 0, Length = 600 });
        var second = await service.ReadAsync(new SaveAnalysisReadRequest { Token = token, Offset = 600, Length = 600 });
        first.TotalSize.Should().Be(1000);
        Convert.FromBase64String(first.Base64).Concat(Convert.FromBase64String(second.Base64)).Should().Equal(content);
        (await service.ReadAsync(new SaveAnalysisReadRequest { Token = "forged", Offset = 0, Length = 10 })).Error.Should().Be("expired");
        File.ReadAllBytes(Path.Combine(cloud, "slot1.sav")).Should().Equal(content, "the original save is never modified");
        (await service.UninstallAsync("demo-game")).Should().BeFalse("dev plugins are managed by the developer");
    }

    /// <summary>
    /// 回归：Electron.NET 桥会把形似 GUID / 日期 / 时间间隔的字符串参数转成对应类型，
    /// token 必须原样穿过「桥接转换器 → IpcRequestBinder」才能读到存档。
    /// </summary>
    [Test]
    public async Task ReadSave_TokenSurvivesTheElectronBridge()
    {
        var steam = Path.Combine(_root, "Steam");
        var cloud = Path.Combine(steam, "userdata", "123", "570", "remote");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "slot1.sav"), "DEMO-SAVE");
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(Path.Combine(dev, "demo-game"));
        File.WriteAllText(Path.Combine(dev, "demo-game", "manifest.json"), Manifest);
        File.WriteAllText(Path.Combine(dev, "demo-game", "parser.js"), "//");
        Environment.SetEnvironmentVariable(SaveAnalysisService.DevDirectoryVariable, dev);
        var service = CreateService(out var connection, steam);
        using var _ = connection;
        var bridge = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new JsonToBoxedPrimitivesConverter() }
        };
        var binder = new IpcRequestBinder(NullLogger<IpcRequestBinder>.Instance);

        foreach (var file in await service.LocateAsync("demo-game"))
        {
            // 与 Electron.NET IpcMain.FormatArguments 相同：[event, args] → object[] → args
            var payload = JsonSerializer.Serialize(new object?[] { null, new { token = file.Token, offset = 0, length = 64 } });
            var boxed = JsonSerializer.Deserialize<object[]>(payload, bridge)![1];
            var request = binder.Bind<SaveAnalysisReadRequest>(boxed, SaveAnalysisIpc.ReadSave);

            request.Token.Should().Be(file.Token);
            var chunk = await service.ReadAsync(request);
            chunk.Success.Should().BeTrue(chunk.Error);
            Encoding.UTF8.GetString(Convert.FromBase64String(chunk.Base64)).Should().Be("DEMO-SAVE");
        }
    }

    /// <summary>可选：设置 STEAM_STAT_TEST_PLUGINS_DIR 指向插件仓库的 plugins 目录时，校验仓库中的真实插件清单。</summary>
    [Test]
    public void RepositoryPlugins_HaveValidManifests()
    {
        var directory = Environment.GetEnvironmentVariable("STEAM_STAT_TEST_PLUGINS_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) Assert.Ignore("STEAM_STAT_TEST_PLUGINS_DIR not set");
        foreach (var plugin in Directory.GetDirectories(directory))
        {
            SaveAnalysisManifestLoader.TryLoad(plugin, out var manifest, out var error).Should().BeTrue(error);
            manifest!.ApiVersion.Should().Be(SaveAnalysisManifest.SupportedApiVersion);
            foreach (var data in manifest.Data)
                new FileInfo(Path.Combine(plugin, data)).Length.Should().BeLessThan(SaveAnalysisManifest.MaxPluginFileBytes, data);
            if (Environment.GetEnvironmentVariable("STEAM_STAT_LOCATE_STEAM") is { Length: > 0 } steamPath)
                TestContext.Out.WriteLine(string.Join(Environment.NewLine, SaveAnalysisLocator.Locate(manifest, new SaveLocatorEnvironment(steamPath, SaveAnalysisLocator.SystemFolders, _ => null))
                    .Select(item => $"{manifest.Id}: {item.Location.Id} {item.Name} {item.SteamId}")));
        }
    }

    private static void WriteIndex(string repository, string plugin)
    {
        var files = Directory.GetFiles(plugin, "*", SearchOption.AllDirectories).Order().Select(file => new
        {
            path = Path.GetRelativePath(plugin, file).Replace('\\', '/'),
            size = new FileInfo(file).Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))
        });
        var index = new
        {
            schemaVersion = 1,
            plugins = new[]
            {
                new { id = "demo-game", version = "1.2.0", apiVersion = 1, name = new Dictionary<string, string> { ["zh-CN"] = "演示", ["en-US"] = "Demo" }, appIds = new[] { 570 }, path = "plugins/demo-game", files }
            }
        };
        File.WriteAllText(Path.Combine(repository, "index.json"), JsonSerializer.Serialize(index), Encoding.UTF8);
    }

    private SaveAnalysisService CreateService(out SqliteConnection connection, string? steamPath = null)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        return new SaveAnalysisService(
            factory,
            new AppPaths(Path.Combine(_root, "UserData")),
            new FakeLocator(steamPath ?? string.Empty),
            new FakeSettings(),
            null!,
            null!,
            NullLogger<SaveAnalysisService>.Instance);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public AppSettings GetSettings() => new();
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeLocator(string steamPath) : ISteamInstallLocator
    {
        public SteamRegistrySnapshot ReadSteamRegistry() => new() { SteamPath = steamPath };
        public SteamActiveProcessSnapshot ReadActiveProcess() => throw new NotSupportedException();
        public IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry() => throw new NotSupportedException();
        public bool HasRegistryKey(SteamRegistryKey key) => false;
        public void SetAutoLoginUser(string accountName, bool rememberPassword) => throw new NotSupportedException();
    }
}
