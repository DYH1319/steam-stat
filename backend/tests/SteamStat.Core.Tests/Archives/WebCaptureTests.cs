using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Archives.Web;

namespace SteamStat.Core.Tests.Archives;

[TestFixture]
public sealed class SteamWebAllowlistTests
{
    private const ulong Me = 76561198000000001;

    [Test]
    public void EveryBuiltAddressIsAllowed()
    {
        SteamWebAllowlist.IsAllowed(HttpMethod.Get, SteamWebAllowlist.BillingHistory()).Should().BeTrue();
        SteamWebAllowlist.IsAllowed(HttpMethod.Post, SteamWebAllowlist.BillingMore()).Should().BeTrue();
        SteamWebAllowlist.IsAllowed(HttpMethod.Get, SteamWebAllowlist.Reviews(Me, 2)).Should().BeTrue();
        SteamWebAllowlist.IsAllowed(HttpMethod.Get, SteamWebAllowlist.Guides(Me, 1)).Should().BeTrue();
        SteamWebAllowlist.IsAllowed(HttpMethod.Get, SteamWebAllowlist.Favorites(Me, 1)).Should().BeTrue();
        SteamWebAllowlist.IsAllowed(HttpMethod.Get, SteamWebAllowlist.Comments(Me, 0, 50)).Should().BeTrue();
    }

    [TestCase("GET", "https://store.steampowered.com/account/")]
    [TestCase("GET", "https://store.steampowered.com/account/AjaxLoadMoreHistory/")]
    [TestCase("POST", "https://store.steampowered.com/account/history/")]
    [TestCase("POST", "https://store.steampowered.com/checkout/")]
    [TestCase("GET", "http://store.steampowered.com/account/history/")]
    [TestCase("GET", "https://store.steampowered.com:8443/account/history/")]
    [TestCase("GET", "https://user@store.steampowered.com/account/history/")]
    [TestCase("GET", "https://store.steampowered.com.evil.example/account/history/")]
    [TestCase("GET", "https://evil.example/account/history/")]
    [TestCase("GET", "https://steamcommunity.com/login/home/")]
    [TestCase("GET", "https://steamcommunity.com/profiles/76561198000000001/inventory/")]
    [TestCase("POST", "https://steamcommunity.com/profiles/76561198000000001/recommended/")]
    [TestCase("GET", "https://steamcommunity.com/profiles/1/recommended/")]
    [TestCase("GET", "https://steamcommunity.com/id/a/../../login/recommended/")]
    public void EverythingElseIsRefused(string method, string url)
        => SteamWebAllowlist.IsAllowed(new HttpMethod(method), new Uri(url)).Should().BeFalse();

    [Test]
    public void FollowsTheVanityRedirectOnTheSameHost()
    {
        var target = SteamWebAllowlist.ResolveRedirect(SteamWebAllowlist.Reviews(Me, 1), "https://steamcommunity.com/id/somebody/recommended/?p=1", HttpMethod.Get);
        target.Should().NotBeNull();
        target!.AbsolutePath.Should().Be("/id/somebody/recommended/");
    }

    [Test]
    public void AResolvesRelativeLocations()
        => SteamWebAllowlist.ResolveRedirect(SteamWebAllowlist.BillingHistory(), "/account/history", HttpMethod.Get).Should().NotBeNull();

    [TestCase("https://steamcommunity.com/login/home/?goto=x")]
    [TestCase("https://evil.example/profiles/76561198000000001/recommended/")]
    [TestCase("https://store.steampowered.com/account/history/")]
    [TestCase("")]
    public void DoesNotFollowRedirectsOffTheList(string location)
        => SteamWebAllowlist.ResolveRedirect(SteamWebAllowlist.Reviews(Me, 1), location, HttpMethod.Get).Should().BeNull();
}

[TestFixture]
public sealed class SteamWebRedactorTests
{
    private const string Jwt = "eyJhbGciOiJFZERTQSJ9.eyJpc3MiOiJyOjE4NTYifQ.abcdefghijklmnop";

    [Test]
    public void RemovesTheTokensEmbeddedInAPage()
    {
        var page = $$"""
            <div data-store_user_config="{&quot;webapi_token&quot;:&quot;{{Jwt}}&quot;}"></div>
            <script>g_sessionID = "0123456789abcdef01234567"; var c = {"webapi_token":"plain-value"};</script>
            <span data-x="{{Jwt}}"></span>
            """;

        var output = SteamWebRedactor.Redact(page);

        output.Should().NotContain(Jwt).And.NotContain("0123456789abcdef01234567").And.NotContain("plain-value");
        output.Should().Contain("g_sessionID = \"REDACTED\"");
    }

    [Test]
    public void KeepsWhatTheParsersNeed()
    {
        const string Page = """<tr class="wallet_table_row" onclick="location.href='https://x/?transid=123'"><td class="wht_total">¥ 48.00</td></tr>""";
        SteamWebRedactor.Redact(Page).Should().Be(Page);
    }
}

[TestFixture]
public sealed class MoneyTextTests
{
    [TestCase("¥ 48.00", 4800, "CNY")]
    [TestCase("$19.99", 1999, "USD")]
    [TestCase("19,99€", 1999, "EUR")]
    [TestCase("12,--€", 1200, "EUR")]
    [TestCase("CDN$ 12.34", 1234, "CAD")]
    [TestCase("1 234,56 pуб.", 123456, "RUB")]
    [TestCase("R$ 1.234,56", 123456, "BRL")]
    [TestCase("ARS$ 12.345,67", 1234567, "ARS")]
    [TestCase("₩ 12,000", 1200000, "KRW")]
    [TestCase("-¥ 12.00", -1200, "CNY")]
    [TestCase("−$5.00", -500, "USD")]
    [TestCase("R 99.00", 9900, "ZAR")]
    [TestCase("₱ 450.00", 45000, "PHP")]
    [TestCase("Credit: ¥ 5.00", 500, "CNY")]
    public void ParsesTheFormatsSteamUses(string text, long cents, string currency)
    {
        var amount = MoneyText.Parse(text);
        amount.Should().NotBeNull();
        amount!.Cents.Should().Be(cents);
        amount.Currency.Should().Be(currency);
    }

    [Test]
    public void ThousandsSeparatorIsNotADecimalPoint()
        => MoneyText.Parse("¥ 1,200", "JPY")!.Cents.Should().Be(120000);

    [Test]
    public void AmbiguousSymbolsTrustTheWalletCurrency()
    {
        MoneyText.Parse("¥ 1,200", "JPY")!.Currency.Should().Be("JPY");
        MoneyText.Parse("$ 19.99", "MXN")!.Currency.Should().Be("MXN");
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase("Free")]
    [TestCase(null)]
    public void NoAmountIsNull(string? text) => MoneyText.Parse(text).Should().BeNull();

    [TestCase(23, "CNY")]
    [TestCase(1, "USD")]
    [TestCase(999, null)]
    public void MapsTheWalletCurrencyCode(int code, string? currency) => MoneyText.CurrencyOf(code).Should().Be(currency);
}

[TestFixture]
public sealed class BillingParserTests
{
    /// <summary>行结构来自 ASFEnhance、steam-user 的解析代码和公开脚本，内容是编的。</summary>
    private const string Page = """
        <html><head><title>Purchase History</title></head><body>
        <table class="wallet_history_table"><tbody>
        <tr class="wallet_table_row wallet_table_row_amt_change" onclick="location.href='https://help.steampowered.com/en/wizard/HelpWithPurchase?transid=111'">
          <td class="wht_date">3 Nov, 2026</td>
          <td class="wht_items"><div>Example Game</div><div>Example Game - Soundtrack</div></td>
          <td class="wht_type"><div>Purchase</div><div class="wth_payment">Alipay</div></td>
          <td class="wht_total">¥ 98.00</td>
          <td class="wht_wallet_change wallet_column"></td>
          <td class="wht_wallet_balance wallet_column"></td>
        </tr>
        <tr class="wallet_table_row" onclick="location.href='https://help.steampowered.com/en/wizard/HelpWithPurchase?transid=112'">
          <td class="wht_date">Nov 1, 2026</td>
          <td class="wht_items">Regret Simulator</td>
          <td class="wht_type wht_refunded"><div>Refund</div><div class="wth_payment">Steam Wallet</div></td>
          <td class="wht_total"><div class="wht_refunded">¥ 20.00</div></td>
          <td class="wht_wallet_change wallet_column">+¥ 20.00</td>
          <td class="wht_wallet_balance wallet_column">¥ 21.50</td>
        </tr>
        <tr class="wallet_table_row">
          <td class="wht_date">28 Oct, 2026</td>
          <td class="wht_items"><div>Steam Community Market</div><div>2 Market Transactions</div></td>
          <td class="wht_type"><div>Market Transaction</div></td>
          <td class="wht_total">¥ 1.50</td>
          <td class="wht_wallet_change wallet_column">-¥ 1.50</td>
          <td class="wht_wallet_balance wallet_column">¥ 1.50</td>
        </tr>
        <tr class="wallet_table_row">
          <td class="wht_date">not a date</td>
          <td class="wht_items">Broken</td>
          <td class="wht_type"><div>Purchase</div></td>
          <td class="wht_total">¥ 1.00</td>
        </tr>
        </tbody></table>
        <script>var g_historyCursor = {"wallet_txnid":"9000","timestamp_newest":1793000000,"balance":"150","currency":23};</script>
        </body></html>
        """;

    [Test]
    public void ReadsARowsFieldsAndTheCursor()
    {
        var page = BillingParser.ParseDocument(Page);

        page.State.Should().Be(WebPageState.Ok);
        page.Rows.Should().HaveCount(3);
        page.SkippedRows.Should().Be(1);
        page.Cursor.Should().Be(new BillingCursor("9000", 1793000000, "150", 23));

        var purchase = page.Rows[0];
        purchase.Key.Should().Be("txn:111");
        purchase.Kind.Should().Be(BillingKinds.Purchase);
        purchase.Items.Should().Equal("Example Game", "Example Game - Soundtrack");
        purchase.TotalCents.Should().Be(9800);
        purchase.Currency.Should().Be("CNY");
        purchase.PaymentMethod.Should().Be("Alipay");
        purchase.Refunded.Should().BeFalse();
        purchase.Date.Should().Be(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Test]
    public void RefundsAndMarketRowsAreClassified()
    {
        var page = BillingParser.ParseDocument(Page);

        page.Rows[1].Kind.Should().Be(BillingKinds.Refund);
        page.Rows[1].Refunded.Should().BeTrue();
        page.Rows[1].WalletChangeCents.Should().Be(2000);
        page.Rows[1].WalletBalanceCents.Should().Be(2150);
        page.Rows[2].Kind.Should().Be(BillingKinds.Market);
        page.Rows[2].TransactionId.Should().BeNull();
        page.Rows[2].Key.Should().StartWith("row:");
    }

    [Test]
    public void KeysAreStableAndIdenticalRowsStayApart()
    {
        const string Row = """<tr class="wallet_table_row"><td class="wht_date">28 Oct, 2026</td><td class="wht_items">Steam Community Market</td><td class="wht_type"><div>Market Transaction</div></td><td class="wht_total">¥ 1.50</td></tr>""";
        var html = $"<title>x</title><table class=\"wallet_history_table\"><tbody>{Row}{Row}</tbody></table>";

        var first = BillingParser.ParseDocument(html);
        var second = BillingParser.ParseDocument(html);

        first.Rows.Select(row => row.Key).Should().OnlyHaveUniqueItems();
        first.Rows.Select(row => row.Key).Should().Equal(second.Rows.Select(row => row.Key));
    }

    [Test]
    public void LoadMoreIsAFragmentWithItsOwnCursor()
    {
        var html = """<tr class="wallet_table_row"><td class="wht_date">Jan 2, 2020</td><td class="wht_items">Old Game</td><td class="wht_type"><div>Gift Purchase</div></td><td class="wht_total">$4.99</td></tr>""";
        var json = System.Text.Json.JsonSerializer.Serialize(new { html, cursor = new { wallet_txnid = "1", timestamp_newest = 5, balance = "0", currency = 1 } });

        var page = BillingParser.ParseMore(json, "CNY");

        page.State.Should().Be(WebPageState.Ok);
        page.Rows.Should().ContainSingle().Which.Kind.Should().Be(BillingKinds.Gift);
        page.Rows[0].Currency.Should().Be("USD");
        page.Cursor!.WalletTxnId.Should().Be("1");
    }

    [Test]
    public void TheLastPageHasNoCursor()
    {
        var page = BillingParser.ParseMore("""{"html":"","cursor":null}""", "CNY");
        page.State.Should().Be(WebPageState.Ok);
        page.Rows.Should().BeEmpty();
        page.Cursor.Should().BeNull();
    }

    [Test]
    public void TheSignInPageIsNotALayoutChange()
        => BillingParser.ParseDocument("<html><head><title>Sign In</title></head><body></body></html>").State.Should().Be(WebPageState.SignedOut);

    [Test]
    public void AnEmptyHistoryIsNotALayoutChange()
    {
        var page = BillingParser.ParseDocument("""<title>History</title><table class="wallet_history_table"><tbody></tbody></table>""");
        page.State.Should().Be(WebPageState.Ok);
        page.Rows.Should().BeEmpty();
    }

    [TestCase("<title>History</title><div>completely new page</div>")]
    [TestCase("")]
    public void AnUnknownPageIsALayoutChange(string html)
        => BillingParser.ParseDocument(html).State.Should().Be(WebPageState.LayoutChanged);

    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("""{"html":"<div>new</div>","cursor":null}""")]
    public void AnUnknownLoadMoreIsALayoutChange(string json)
        => BillingParser.ParseMore(json, "CNY").State.Should().Be(WebPageState.LayoutChanged);
}

[TestFixture]
public sealed class CommunityParserTests
{
    private static readonly DateTimeOffset Captured = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    private static string Review(string id, string thumb, string hours, string posted, string header = "") => $"""
        <div class="review_box">
          <div class="header">{header}</div>
          <div class="review_box_content">
            <div class="leftcol"><a href="https://steamcommunity.com/app/620" class="game_capsule_ctn"><img class="game_capsule" src="x.jpg"></a></div>
            <div class="rightcol">
              <div class="vote_header">
                <div class="thumb"><a href="#"><img src="https://x/userreviews/{thumb}.png"></a></div>
                <div class="title"><a href="#">Recommended</a></div>
                <div class="hours">{hours}</div>
              </div>
              <div class="content">First line<br>Second &amp; line</div>
              <div class="posted">{posted}</div>
              <div class="control_block"><a id="RecommendationVoteUpBtn{id}"></a></div>
            </div>
          </div>
        </div>
        """;

    private static string Reviews(params string[] boxes) => $"""
        <title>Steam Community :: Someone :: Recommended</title>
        <div id="leftContents"><div class="workshopBrowsePaging"><div class="workshopBrowsePagingInfo"> Showing 1-10 of 1,012 entries </div></div>
        {string.Join("\n", boxes)}</div>
        """;

    [Test]
    public void ReadsAReview()
    {
        var page = CommunityParsers.ParseReviews(Reviews(Review("1000001", "icon_thumbsUp", "20.3 hrs on record (14.5 hrs at review time)", "Posted 24 November, 2018.", "31 people found this review helpful<br>2 people found this review funny")), Captured);

        page.State.Should().Be(WebPageState.Ok);
        page.Total.Should().Be(1012);
        var review = page.Reviews.Should().ContainSingle().Subject;
        review.ReviewId.Should().Be("1000001");
        review.AppId.Should().Be(620);
        review.Recommended.Should().BeTrue();
        review.HoursTotal.Should().Be(20.3);
        review.HoursAtReview.Should().Be(14.5);
        review.PostedAt.Should().Be(new DateTimeOffset(2018, 11, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        review.EditedAt.Should().BeNull();
        review.Helpful.Should().Be(31);
        review.Funny.Should().Be(2);
        review.Text.Should().Be("First line\nSecond & line");
    }

    [Test]
    public void ThumbsDownComesFromTheIconNotTheWords()
        => CommunityParsers.ParseReviews(Reviews(Review("1", "icon_thumbsDown", "1,204.5 hrs on record", "Posted 1 March, 2020.")), Captured)
            .Reviews[0].Recommended.Should().BeFalse();

    [Test]
    public void ThisYearsDatesHaveNoYear()
    {
        var review = CommunityParsers.ParseReviews(Reviews(Review("1", "icon_thumbsUp", "1.0 hrs on record", "Posted 24 February. Last edited 3 March.")), Captured).Reviews[0];

        review.PostedAt.Should().Be(new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        review.EditedAt.Should().Be(new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Test]
    public void LargeHourCountsKeepTheirValue()
        => CommunityParsers.ParseReviews(Reviews(Review("1", "icon_thumbsUp", "1,204.5 hrs on record", "Posted 1 March, 2020.")), Captured)
            .Reviews[0].HoursTotal.Should().Be(1204.5);

    [Test]
    public void NoReviewsYetIsNotALayoutChange()
        => CommunityParsers.ParseReviews("""<title>x</title><div class="profile_small_header_bg"></div>""", Captured).State.Should().Be(WebPageState.Ok);

    [Test]
    public void AnUnknownPageIsALayoutChange()
        => CommunityParsers.ParseReviews("<title>x</title><main>new design</main>", Captured).State.Should().Be(WebPageState.LayoutChanged);

    [Test]
    public void TheSignInPageIsReported()
        => CommunityParsers.ParseReviews("<title>Sign In</title>", Captured).State.Should().Be(WebPageState.SignedOut);

    [Test]
    public void FavoritesKeepTheirTitleOutsideTheLink()
    {
        const string Html = """
            <div class="workshopBrowsePagingInfo">Showing 1-2 of 5 entries</div>
            <div class="workshopItem">
              <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=232" class="ugc" data-appid="220700" data-publishedfileid="232">
                <div class="workshopItemPreviewHolder"><img class="workshopItemPreviewImage" src="https://cdn/ugc/1.jpg"></div>
              </a>
              <a href="#" class="item_link"><div class="workshopItemTitle">Example Item</div></a>
            </div>
            """;

        var page = CommunityParsers.ParseFiles(Html);

        page.Total.Should().Be(5);
        var file = page.Files.Should().ContainSingle().Subject;
        file.Should().Be(new CommunityFile("232", 220700, "Example Item", null, null, "https://cdn/ugc/1.jpg"));
    }

    [Test]
    public void GuidesKeepTheirTitleInsideTheLink()
    {
        const string Html = """
            <div class="workshopBrowsePagingInfo">Showing 1-3 of 3 entries</div>
            <div class="workshopItemCollectionContainer">
              <a class="workshopItemCollection ugc" href="#" data-appid="581610" data-publishedfileid="358">
                <div class="workshopItem"><div class="workshopItemPreviewHolder"><img class="workshopItemPreviewImage" src="https://cdn/p.jpg"></div></div>
                <div class="workshopItemDetails">
                  <div class="workshopItemApp guide"></div>
                  <div class="workshopItemTitle"> How to do it </div>
                  <div class="workshopItemAuthorLine">by&nbsp;<span class="workshopItemAuthorName">Someone</span></div>
                  <div class="workshopItemShortDesc">Short text</div>
                </div>
              </a>
            </div>
            """;

        CommunityParsers.ParseFiles(Html).Files.Should().ContainSingle()
            .Which.Should().Be(new CommunityFile("358", 581610, "How to do it", "Short text", "Someone", "https://cdn/p.jpg"));
    }

    [Test]
    public void AnEmptyFileListIsNotALayoutChange()
        => CommunityParsers.ParseFiles("""<div class="workshopBrowsePaging"></div>""").State.Should().Be(WebPageState.Ok);

    [Test]
    public void ReadsComments()
    {
        const string Html = """
            <div class="commentthread_comment" id="comment_564">
              <a class="hoverunderline commentthread_author_link" href="#" data-miniprofile="40000001"><bdi>Someone</bdi></a>
              <div class="commentthread_comment_timestamp"></div>
              <div class="commentthread_comment_timestamp" title="x" data-timestamp="1793737796">3 Nov</div>
              <div class="commentthread_comment_text" id="comment_content_564">Hello<br>there</div>
            </div>
            """;
        var json = System.Text.Json.JsonSerializer.Serialize(new { success = true, total_count = 16296, comments_html = Html });

        var page = CommunityParsers.ParseComments(json);

        page.Total.Should().Be(16296);
        page.Comments.Should().ContainSingle().Which.Should().Be(new CommunityComment("564", "76561198000265729", "Someone", 1793737796, "Hello\nthere"));
    }

    [Test]
    public void NoCommentsIsFine()
        => CommunityParsers.ParseComments("""{"success":true,"total_count":0,"comments_html":""}""").State.Should().Be(WebPageState.Ok);

    [TestCase("""{"success":false}""")]
    [TestCase("nope")]
    [TestCase("""{"success":true,"total_count":3,"comments_html":"<div>changed</div>"}""")]
    public void UnknownCommentRepliesAreALayoutChange(string json)
        => CommunityParsers.ParseComments(json).State.Should().Be(WebPageState.LayoutChanged);
}
