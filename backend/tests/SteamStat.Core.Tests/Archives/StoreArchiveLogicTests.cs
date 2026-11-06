using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Archives.Store;

namespace SteamStat.Core.Tests.Archives;

[TestFixture]
public sealed class ArtifactDiffTests
{
    private static StorePageFacts Facts(string name = "Game", string? header = "abc/header.jpg", string publisher = "Pub")
        => new(name, "desc", ["Dev"], [publisher], null, 1, header, "abc/capsule.jpg", ["ss1.jpg"], "app/1/Game");

    [Test]
    public void TheFirstSnapshotIsOnlyABaseline() => ArtifactDiff.Compare(null, Facts()).Should().BeEmpty();

    [Test]
    public void NothingChanged() => ArtifactDiff.Compare(Facts(), Facts()).Should().BeEmpty();

    [Test]
    public void DetectsARename()
        => ArtifactDiff.Compare(Facts(), Facts("New Game")).Should().ContainSingle()
            .Which.Should().Be(new ArtifactChange(ArtifactEventKinds.Renamed, "Game", "New Game"));

    [Test]
    public void DetectsACoverChange()
        => ArtifactDiff.Compare(Facts(), Facts(header: "def/header.jpg")).Should().ContainSingle().Which.Kind.Should().Be(ArtifactEventKinds.CoverChanged);

    [Test]
    public void DetectsAPublisherChange()
        => ArtifactDiff.Compare(Facts(), Facts(publisher: "Other")).Should().ContainSingle().Which.Kind.Should().Be(ArtifactEventKinds.PublisherChanged);

    [Test]
    public void ADelistedPageWithNoDataIsNotARename()
        => ArtifactDiff.Compare(Facts(), Facts(string.Empty, null)).Should().BeEmpty();

    [Test]
    public void TheFingerprintIgnoresNothingInTheFacts()
    {
        Facts().Fingerprint().Should().Be(Facts().Fingerprint());
        Facts().Fingerprint().Should().NotBe(Facts("Other").Fingerprint());
    }

    [Test]
    public void SurvivesAJsonRoundTrip()
        => StorePageFacts.FromJson(Facts().ToJson())!.Fingerprint().Should().Be(Facts().Fingerprint());

    [Test]
    public void BadJsonIsNull() => StorePageFacts.FromJson("{nope").Should().BeNull();
}

[TestFixture]
public sealed class ServerShutdownDetectorTests
{
    private static readonly int[] OnlineOnly = [1, 36];
    private static readonly int[] WithCampaign = [1, 2, 36];

    private static NewsItem News(string title) => new(title, null, 1793000000, null);

    private static IReadOnlyList<DailyPlayers> Curve(int active, int silent)
        => Enumerable.Range(0, active).Select(day => new DailyPlayers(day * 86400L, 40))
            .Concat(Enumerable.Range(active, silent).Select(day => new DailyPlayers(day * 86400L, 0)))
            .ToArray();

    [TestCase("Important notice: servers will shut down on March 1")]
    [TestCase("Game servers are shutting down")]
    [TestCase("End of Service announcement")]
    [TestCase("Sunsetting the online services")]
    [TestCase("We are shutting down the servers")]
    public void AnOfficialNoticeIsEvidence(string title)
    {
        var evidence = ServerShutdownDetector.Evaluate(OnlineOnly, [News(title)], []);
        evidence.Should().NotBeNull();
        evidence!.Source.Should().Be("news");
        evidence.Detail.Should().Be(title);
    }

    [TestCase("Patch notes 1.2: server stability")]
    [TestCase("New map! Shutdown the reactor to win")]
    [TestCase("Winter update")]
    public void OrdinaryNewsIsNot(string title) => ServerShutdownDetector.Evaluate(OnlineOnly, [News(title)], []).Should().BeNull();

    [Test]
    public void GamesWithASinglePlayerModeAreNeverFlagged()
    {
        ServerShutdownDetector.Evaluate(WithCampaign, [News("Servers will shut down")], Curve(30, 30)).Should().BeNull();
    }

    [Test]
    public void TwoSilentWeeksAfterHavingPlayersIsEvidence()
    {
        var evidence = ServerShutdownDetector.Evaluate(OnlineOnly, [], Curve(10, 14));
        evidence.Should().NotBeNull();
        evidence!.Source.Should().Be("players");
        evidence.At.Should().Be(10 * 86400L);
    }

    [Test]
    public void AShortSilenceIsNot() => ServerShutdownDetector.Evaluate(OnlineOnly, [], Curve(10, 13)).Should().BeNull();

    [Test]
    public void AGameThatNeverHadDataIsNot() => ServerShutdownDetector.Evaluate(OnlineOnly, [], Curve(0, 40)).Should().BeNull();
}

[TestFixture]
public sealed class WishlistDiffTests
{
    private static ActiveWishlistItem Active(int appId, long addedAt = 100) => new(appId, addedAt);

    private static WishlistEntry Entry(int appId, long? addedAt = 100, int? priority = 1) => new(appId, addedAt, priority);

    [Test]
    public void NewItemsAreAdded()
    {
        var changes = WishlistDiff.Compute([Active(1)], [Entry(1), Entry(2)], authenticated: false, emptyReplies: 0);

        changes.Added.Should().ContainSingle().Which.AppId.Should().Be(2);
        changes.Removed.Should().BeEmpty();
        changes.Updated.Should().ContainSingle().Which.AppId.Should().Be(1);
    }

    [Test]
    public void MissingItemsAreRemoved()
        => WishlistDiff.Compute([Active(1), Active(2)], [Entry(2)], authenticated: false, emptyReplies: 0).Removed.Should().Equal(1);

    [Test]
    public void AnEmptyPublicReplyProvesNothing()
    {
        // 私密资料和空愿望单都返回 {"response":{}}
        var changes = WishlistDiff.Compute([Active(1), Active(2)], [], authenticated: false, emptyReplies: 5);

        changes.Removed.Should().BeEmpty();
    }

    [Test]
    public void OneEmptyAuthenticatedReplyIsNotEnough()
        => WishlistDiff.Compute([Active(1)], [], authenticated: true, emptyReplies: 1).Removed.Should().BeEmpty();

    [Test]
    public void TwoEmptyAuthenticatedRepliesAreTrusted()
        => WishlistDiff.Compute([Active(1)], [], authenticated: true, emptyReplies: 2).Removed.Should().Equal(1);

    [Test]
    public void ReaddingBetweenChecksClosesTheOldStretch()
    {
        var changes = WishlistDiff.Compute([Active(1, 100)], [Entry(1, 200)], authenticated: false, emptyReplies: 0);

        changes.Removed.Should().Equal(1);
        changes.Added.Should().ContainSingle().Which.AddedAt.Should().Be(200);
    }

    [Test]
    public void AMissingDateIsNotAReadd()
        => WishlistDiff.Compute([Active(1, 100)], [Entry(1, null)], authenticated: false, emptyReplies: 0).Removed.Should().BeEmpty();
}

[TestFixture]
public sealed class StoreRegionsTests
{
    [TestCase("CN", "CN")]
    [TestCase("cn", "CN")]
    [TestCase(" jp ", "JP")]
    [TestCase("", null)]
    [TestCase(null, null)]
    [TestCase("CHN", null)]
    [TestCase("C1", null)]
    public void NormalizesCountryCodes(string? value, string? expected) => StoreRegions.Normalize(value).Should().Be(expected);

    [TestCase("CNY", "CN")]
    [TestCase("EUR", "DE")]
    [TestCase("XXX", null)]
    [TestCase(null, null)]
    public void MapsACurrencyToARegion(string? currency, string? region) => StoreRegions.OfCurrency(currency).Should().Be(region);

    [TestCase("schinese", "CN")]
    [TestCase("english", "US")]
    [TestCase("klingon", "US")]
    public void FallsBackToTheLanguage(string language, string region) => StoreRegions.OfLanguage(language).Should().Be(region);

    [Test]
    public void TheRequestCarriesTheRegion()
    {
        SteamStat.Core.Insights.StoreBrowseParser.BuildRequest([1], "CN").Should().Contain("\"country_code\":\"CN\"");
        SteamStat.Core.Insights.StoreBrowseParser.BuildRequest([1]).Should().Contain("\"country_code\":\"US\"");
    }

    [Test]
    public void ADiscountedGameKeepsBothPrices()
    {
        const string Json = """{"response":{"store_items":[{"id":1,"visible":true,"name":"G","best_purchase_option":{"final_price_in_cents":"1750","original_price_in_cents":"3500","formatted_final_price":"¥17.50"}}]}}""";

        var item = SteamStat.Core.Insights.StoreBrowseParser.Parse(Json).Single();

        item.PriceCents.Should().Be(3500, "批量检查要的是标价");
        item.FinalPriceCents.Should().Be(1750, "用户现在要付的是这个");
        item.PriceText.Should().Be("¥17.50");
    }

    [TestCase("¥35.00", "CN", "CNY")]
    [TestCase("¥ 3,400", "JP", "JPY")]
    [TestCase("$9.99", "US", "USD")]
    [TestCase("9,99€", "DE", "EUR")]
    public void TheCurrencyComesFromThePriceText(string text, string region, string currency)
        => SteamStat.Core.Archives.Web.MoneyText.Parse(text, StoreRegions.CurrencyHint(region))!.Currency.Should().Be(currency);
}
