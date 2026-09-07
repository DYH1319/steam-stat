using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SettingsStorageTests
{
    private string _directory = null!;
    private AppPaths _paths = null!;
    private JsonSettingsStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-core-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_directory);
        var environment = new AppEnvironment(false, "zh-CN", false, _paths);
        _store = new JsonSettingsStore(_paths, new AppSettingsFactory(environment), NullLogger<JsonSettingsStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _store.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void MissingValues_AreMergedWithLocaleDefaults()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.SettingsFile, """{"colorScheme":"dark","updateAppRunningStatusJob":{"intervalSeconds":30}}""");

        var settings = _store.GetSettings();

        settings.Language.Should().Be("zh-CN");
        settings.ColorScheme.Should().Be("dark");
        settings.AutoUpdate.Should().BeTrue();
        settings.UpdateAppRunningStatusJob!.Enabled.Should().BeTrue();
        settings.UpdateAppRunningStatusJob.IntervalSeconds.Should().Be(30);
    }

    [Test]
    public async Task Update_UsesAtomicTemporaryFileAndLeavesValidJson()
    {
        var updates = Enumerable.Range(1, 8)
            .Select(index => _store.UpdateAsync(new AppSettings { ZoomFactor = index / 10d }))
            .ToArray();

        (await Task.WhenAll(updates)).Should().OnlyContain(result => result);
        File.Exists(_paths.SettingsFile + ".tmp").Should().BeFalse();
        var settings = _store.GetSettings();
        settings.ZoomFactor.Should().BeInRange(0.1, 0.8);
        File.ReadAllText(_paths.SettingsFile).Should().Contain("\"zoomFactor\"");
    }
}
