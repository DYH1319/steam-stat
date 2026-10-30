using System.Text.Json;
using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAppArtworkMapperTests
{
    [TestCase(499170u, "8e4b79f6886648fe2b9b4fd36824146559b047ec/header_schinese.jpg",
        "3c644b8edbbe401083a520e3cfc8c7821cf720a8/library_capsule_schinese.jpg")]
    [TestCase(3280350u, "6270c77b0729e2df0a17d660286eeddfd9169386/header.jpg",
        "9b523fd411eeaefe80f238489745325a1cd2317f/library_capsule.jpg")]
    [TestCase(3527290u, "31bac6b2eccf09b368f5e95ce510bae2baf3cfcd/header.jpg",
        "480bd879ac737921bfa2529a6fea15961267ad21/library_600x900.jpg")]
    public void RealPicsPaths_KeepPerAssetHashAndLocalizedFilename(uint appId, string header, string cover)
    {
        var common = Node("common",
            Node("header_image", Text("english", "header.jpg"), Text("schinese", header)),
            Node("small_capsule", Text("english", "capsule_231x87.jpg")),
            Node("library_assets_full", Node("library_capsule",
                Node("image", Text("english", cover)))));

        var images = SteamAppArtworkMapper.Map(appId, "schinese", common);

        images.Header[0].Should().Be(Url(appId, header));
        images.Header[1].Should().Be(Url(appId, "header.jpg"));
        images.Capsule.Should().Equal(Url(appId, "capsule_231x87.jpg"));
        images.Cover.Should().Equal(Url(appId, cover));
        images.Hero.Should().BeEmpty();
    }

    [Test]
    public void Artwork_RejectsTraversalAndNonSteamHosts()
    {
        var images = SteamAppArtworkMapper.Map(3527290, "english",
            Node("common", Node("header_image", Text("english", "../header.jpg")),
                Node("small_capsule", Text("english", "https://evil.example/a.jpg"))));
        images.Header.Should().BeEmpty();
        images.Capsule.Should().BeEmpty();
        SteamAppArtworkMapper.Map(1, "english", null).Cover.Should().BeEmpty();
    }

    [Test]
    public void StoreFallback_KeepsCompleteUrlsAndDoesNotGuessCoverOrHero()
    {
        using var json = JsonDocument.Parse("""
            {"header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3527290/31bac6b2eccf09b368f5e95ce510bae2baf3cfcd/header.jpg?t=1792684718"}
            """);
        var images = SteamAppArtworkMapper.FromStore(3527290, json.RootElement);
        images.Should().NotBeNull();
        images!.Header[0].Should().EndWith("/header.jpg?t=1792684718");
        images.Cover.Should().BeEmpty();
        images.Hero.Should().BeEmpty();
    }

    private static string Url(uint appId, string path)
        => $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/{path}";

    private static KeyValue Text(string name, string value) => new(name) { Value = value };

    private static KeyValue Node(string name, params KeyValue[] children)
    {
        var node = new KeyValue(name);
        node.Children.AddRange(children);
        return node;
    }
}
