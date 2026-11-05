using ElectronNet.Services;
using FluentAssertions;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SteamLocalAvatarCacheTests
{
    private const string SteamId = "76561198000000001";
    private string _root = null!;
    private string _steam = null!;
    private string _temp = null!;
    private string _source = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("avatar-cache-").FullName;
        _steam = Path.Combine(_root, "Steam");
        _temp = Path.Combine(_root, "Temp");
        _source = Path.Combine(_steam, "config", "avatarcache", $"{SteamId}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private string WriteCurrent(string name, DateTime time)
    {
        var path = Path.Combine(_temp, "AvatarFull", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "current");
        File.SetLastWriteTimeUtc(path, time);
        return path;
    }

    [Test]
    public void Import_ReplacesTheDefaultAvatar()
    {
        File.WriteAllText(_source, "cached");
        var current = WriteCurrent("default.jpg", DateTime.UtcNow.AddDays(1));

        var imported = SteamLocalAvatarCache.Import(_steam, SteamId, _temp, current);

        imported.Should().Be(Path.Combine(_temp, "AvatarFull", $"{SteamId}.png"));
        File.ReadAllText(imported!).Should().Be("cached");
    }

    [Test]
    public void Import_ReturnsNullWhenSteamHasNoCachedAvatar()
    {
        SteamLocalAvatarCache.Import(_steam, SteamId, _temp, null).Should().BeNull();
        SteamLocalAvatarCache.Import(null, SteamId, _temp, null).Should().BeNull();
    }

    [Test]
    public void Import_KeepsANewerAvatarFromCm()
    {
        File.WriteAllText(_source, "cached");
        File.SetLastWriteTimeUtc(_source, DateTime.UtcNow.AddDays(-1));
        var current = WriteCurrent($"{SteamId}.jpg", DateTime.UtcNow);

        SteamLocalAvatarCache.Import(_steam, SteamId, _temp, current).Should().BeNull();
    }

    [Test]
    public void Import_PicksUpANewerCachedAvatar()
    {
        File.WriteAllText(_source, "cached");
        var current = WriteCurrent($"{SteamId}.jpg", DateTime.UtcNow.AddDays(-1));

        SteamLocalAvatarCache.Import(_steam, SteamId, _temp, current).Should().NotBeNull();
    }

    [Test]
    public void Import_RecoversWhenTheCurrentFileIsGone()
    {
        File.WriteAllText(_source, "cached");

        SteamLocalAvatarCache.Import(_steam, SteamId, _temp, Path.Combine(_temp, "missing.jpg")).Should().NotBeNull();
    }
}
