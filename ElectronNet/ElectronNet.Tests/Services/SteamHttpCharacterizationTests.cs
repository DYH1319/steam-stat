using System.Net;
using System.Text;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SteamHttpCharacterizationTests
{
    private string _databaseFile = null!;
    private TestDbContextFactory _dbContextFactory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"steam-stat-http-{Guid.NewGuid():N}.db");
        _dbContextFactory = new TestDbContextFactory(_databaseFile);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile)) File.Delete(_databaseFile);
    }

    [Test]
    public async Task AppMetadataHttp_SuccessReturnsAndCachesNameThroughInjectedHandler()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("{\"730\":{\"success\":true,\"data\":{\"name\":\"Counter-Strike 2\",\"type\":\"game\",\"is_free\":true}}}")
        });
        using var service = new SteamAppMetadataService(
            _dbContextFactory, new FakeHttpClientFactory(handler), NullLogger<SteamAppMetadataService>.Instance);

        var result = await service.ResolveNameAsync(730);

        result.Should().Be("Counter-Strike 2");
        handler.RequestUri.Should().Be("https://store.steampowered.com/api/appdetails?appids=730&filters=basic");
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        (await db.SteamAppTable.SingleAsync()).Should().BeEquivalentTo(new
        {
            AppId = 730,
            Name = "Counter-Strike 2",
            Type = "game",
            IsFreeApp = true
        });
    }

    [Test]
    public async Task AppMetadataHttp_SuccessFalseReturnsNullWithoutCaching()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("{\"999999999\":{\"success\":false}}")
        });
        using var service = new SteamAppMetadataService(
            _dbContextFactory, new FakeHttpClientFactory(handler), NullLogger<SteamAppMetadataService>.Instance);

        var result = await service.ResolveNameAsync(999999999);

        result.Should().BeNull();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        (await db.SteamAppTable.AnyAsync()).Should().BeFalse();
    }

    [Test]
    public async Task AppMetadataHttp_FailureReturnsNull()
    {
        using var service = new SteamAppMetadataService(
            _dbContextFactory,
            new FakeHttpClientFactory(new FakeHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
            NullLogger<SteamAppMetadataService>.Instance);

        (await service.ResolveNameAsync(730)).Should().BeNull();
    }

    [Test]
    public async Task ProfileHttp_SuccessParsesCurrentShapeThroughInjectedHandler()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("{\"level\":42,\"level_class\":\"friendPlayerLevel lvl_40\",\"avatar_url\":\"https://cdn.example/avatar_full.jpg\",\"persona_name\":\"Alice\",\"avatar_frame\":\"https://cdn.example/frame.png\",\"animated_avatar\":\"https://cdn.example/animated.gif\"}")
        });
        var service = CreateUserService(handler);

        var result = await service.FetchProfileAsync("76561198000000000", CancellationToken.None);

        result.Should().BeEquivalentTo(new
        {
            Level = (int?)42,
            LevelClass = "friendPlayerLevel lvl_40",
            AvatarUrl = "https://cdn.example/avatar_full.jpg",
            PersonaName = "Alice",
            AvatarFrame = "https://cdn.example/frame.png",
            AnimatedAvatar = "https://cdn.example/animated.gif"
        });
        handler.RequestUri.Should().Be("https://steam-chat.com/miniprofile/39734272/json");
    }

    [Test]
    public async Task ProfileHttp_FailurePropagatesHttpRequestExceptionToCurrentCallerBoundary()
    {
        var service = CreateUserService(new FakeHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var action = () => service.FetchProfileAsync("76561198000000000", CancellationToken.None);

        await action.Should().ThrowAsync<HttpRequestException>();
    }

    private SteamUserService CreateUserService(HttpMessageHandler handler) => new(
        null!, _dbContextFactory, new FakeHttpClientFactory(handler), null!, null!, null!, null!, null!,
        NullLogger<SteamUserService>.Instance);

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(response);
        }
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databaseFile};Pooling=False")
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
