using System.Net;
using ElectronNet.Helpers;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Gateway;

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
        using var service = new SteamAppMetadataService(
            _dbContextFactory,
            new FakeAppCatalogGateway(SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
                new SteamAppMetadataSnapshot(730, "Counter-Strike 2", "game", true),
                SteamDataSource.Http,
                SteamFreshness.Fresh,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddDays(7))),
            NullLogger<SteamAppMetadataService>.Instance);

        var result = await service.ResolveNameAsync(730);

        result.Should().Be("Counter-Strike 2");
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
        using var service = new SteamAppMetadataService(
            _dbContextFactory,
            new FakeAppCatalogGateway(SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.NotFound, "store_app_not_found")),
            NullLogger<SteamAppMetadataService>.Instance);

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
            new FakeAppCatalogGateway(SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.Transient, "http_server_error")),
            NullLogger<SteamAppMetadataService>.Instance);

        (await service.ResolveNameAsync(730)).Should().BeNull();
    }

    [Test]
    public async Task ProfileAvatarSource_BuildsOfficialCdnUrisWithoutCommunityHttpClient()
    {
        await using var provider = new ServiceCollection().AddSteamStatCore().BuildServiceProvider();
        var source = provider.GetRequiredService<ISteamAvatarUriProvider>();
        const string hash = "0123456789abcdef0123456789abcdef01234567";

        source.GetAvatarUri(hash, SteamAvatarSize.Full).Should()
            .Be("https://avatars.akamai.steamstatic.com/0123456789abcdef0123456789abcdef01234567_full.jpg");
        source.GetAvatarUri(hash, SteamAvatarSize.Medium).Should()
            .Be("https://avatars.akamai.steamstatic.com/0123456789abcdef0123456789abcdef01234567_medium.jpg");
        source.GetAvatarUri(hash, SteamAvatarSize.Small).Should()
            .Be("https://avatars.akamai.steamstatic.com/0123456789abcdef0123456789abcdef01234567.jpg");
        typeof(SteamStatHttpClients).GetField("SteamCommunity").Should().BeNull();
    }

    [Test]
    public async Task ProfileAvatarSource_RejectsInvalidAvatarHash()
    {
        await using var provider = new ServiceCollection().AddSteamStatCore().BuildServiceProvider();
        var source = provider.GetRequiredService<ISteamAvatarUriProvider>();

        source.GetAvatarUri("not-a-sha1", SteamAvatarSize.Full).Should().BeNull();
        source.GetDefaultAvatarUri(SteamAvatarSize.Small).Should()
            .Be("https://avatars.akamai.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg");
    }

    [Test]
    public async Task DownloadHttp_OversizedResponseKeepsExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"steam-stat-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var existingPath = Path.Combine(directory, "avatar.jpg");
        await File.WriteAllTextAsync(existingPath, "old");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        var helper = new FileHelper(
            new FakeHttpClientFactory(new FakeHandler(response)),
            new SteamAccessOptions { MaximumDownloadBytes = 2 },
            NullLogger<FileHelper>.Instance);
        try
        {
            var result = await helper.DownloadFileAsync(
                "https://avatars.akamai.steamstatic.com/avatar.jpg",
                directory,
                "avatar",
                CancellationToken.None);

            result.Should().BeEmpty();
            (await File.ReadAllTextAsync(existingPath)).Should().Be("old");

            var replacementResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5])
            };
            replacementResponse.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            var replacement = new FileHelper(
                new FakeHttpClientFactory(new FakeHandler(replacementResponse)),
                new SteamAccessOptions { MaximumDownloadBytes = 2 },
                NullLogger<FileHelper>.Instance);
            var replacedPath = await replacement.DownloadFileAsync(
                "https://avatars.akamai.steamstatic.com/avatar.jpg",
                directory,
                "avatar",
                CancellationToken.None);

            replacedPath.Should().Be(existingPath);
            (await File.ReadAllBytesAsync(existingPath)).Should().Equal(4, 5);
            Directory.GetFiles(directory, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeAppCatalogGateway(SteamGatewayResult<SteamAppMetadataSnapshot> result) : ISteamAppCatalogGateway
    {
        public Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAppAsync(
            uint appId,
            string language,
            string? preferredAccountName = null,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

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
