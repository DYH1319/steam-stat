using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class HttpPublicProfileSourceTests
{
    private const ulong SteamId = 76561198000000001;

    [Test]
    public void Parse_ReadsLevelFrameAndAnimatedAvatar()
    {
        var profile = HttpPublicProfileSource.Parse(
            """{"level":36,"level_class":"friendPlayerLevel lvl_30","avatar_frame":"https://shared.fastly.steamstatic.com/frame.png","animated_avatar":"https://shared.cloudflare.steamstatic.com/avatar.gif"}""");

        profile.Level.Should().Be(36);
        profile.LevelClass.Should().Be("friendPlayerLevel lvl_30");
        profile.AvatarFrameUrl.Should().Be("https://shared.akamai.steamstatic.com/frame.png");
        profile.AnimatedAvatarUrl.Should().Be("https://shared.akamai.steamstatic.com/avatar.gif");
    }

    [Test]
    public void Parse_TreatsMissingFieldsAsAbsent()
    {
        var profile = HttpPublicProfileSource.Parse("""{"persona_name":"Bob"}""");

        profile.Level.Should().BeNull();
        profile.LevelClass.Should().BeNull();
        profile.AvatarFrameUrl.Should().BeNull();
        profile.AnimatedAvatarUrl.Should().BeNull();
        profile.PersonaName.Should().Be("Bob");
    }

    [TestCase("http://shared.fastly.steamstatic.com/frame.png")]
    [TestCase("https://example.com/frame.png")]
    [TestCase("https://steamstatic.com.example.com/frame.png")]
    [TestCase("not a url")]
    public void Parse_RejectsAssetsOutsideSteam(string url)
    {
        var profile = HttpPublicProfileSource.Parse($$"""{"avatar_frame":"{{url}}","animated_avatar":"{{url}}"}""");

        profile.AvatarFrameUrl.Should().BeNull();
        profile.AnimatedAvatarUrl.Should().BeNull();
    }

    [Test]
    public async Task GetAsync_UsesSteamCommunityFirst()
    {
        var clients = new FakeClients { [SteamStatHttpClients.SteamCommunity] = Ok, [SteamStatHttpClients.SteamChat] = Fail };

        var result = await CreateSource(clients).GetAsync(SteamId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        clients.Calls.Should().Equal(SteamStatHttpClients.SteamCommunity);
    }

    [Test]
    public async Task GetAsync_FallsBackToSteamChatAndRemembersIt()
    {
        var clients = new FakeClients { [SteamStatHttpClients.SteamCommunity] = Fail, [SteamStatHttpClients.SteamChat] = Ok };
        var source = CreateSource(clients);

        (await source.GetAsync(SteamId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await source.GetAsync(SteamId, CancellationToken.None)).IsSuccess.Should().BeTrue();

        clients.Calls.Should().Equal(
            SteamStatHttpClients.SteamCommunity,
            SteamStatHttpClients.SteamChat,
            SteamStatHttpClients.SteamChat);
    }

    [Test]
    public async Task GetAsync_FallsBackWhenTheRequestThrows()
    {
        var clients = new FakeClients
        {
            [SteamStatHttpClients.SteamCommunity] = () => throw new HttpRequestException("blocked"),
            [SteamStatHttpClients.SteamChat] = Ok
        };

        (await CreateSource(clients).GetAsync(SteamId, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Test]
    public async Task GetAsync_FailsWhenBothDomainsAreUnavailable()
    {
        var clients = new FakeClients { [SteamStatHttpClients.SteamCommunity] = Fail, [SteamStatHttpClients.SteamChat] = Fail };

        (await CreateSource(clients).GetAsync(SteamId, CancellationToken.None)).IsSuccess.Should().BeFalse();
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("""{"level":1}""") };
    private static HttpResponseMessage Fail() => new(HttpStatusCode.ServiceUnavailable);

    private static HttpPublicProfileSource CreateSource(FakeClients clients) => new(
        clients, new SteamResultClassifier(), TimeProvider.System, NullLogger<HttpPublicProfileSource>.Instance);

    private sealed class FakeClients : Dictionary<string, Func<HttpResponseMessage>>, IHttpClientFactory
    {
        public List<string> Calls { get; } = [];

        public HttpClient CreateClient(string name)
            => new(new Handler(() =>
            {
                Calls.Add(name);
                return this[name]();
            }))
            { BaseAddress = new Uri("https://example.test/") };
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }
}
