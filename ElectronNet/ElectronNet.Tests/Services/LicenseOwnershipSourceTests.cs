using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class LicenseOwnershipSourceTests
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _factory = null!;
    private FakeLicenseGateway _licenses = null!;
    private LicenseOwnershipSource _source = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.LibraryAcquisitionTable.AddRange(
            new LibraryAcquisition { AccountName = "alice", AppId = 100, PackageId = 1, AcquiredAt = 1 },
            new LibraryAcquisition { AccountName = "alice", AppId = 101, PackageId = 1, AcquiredAt = 1 },
            new LibraryAcquisition { AccountName = "alice", AppId = 200, PackageId = 2, AcquiredAt = 1 },
            // 首次观测的记录不是授权，不能算拥有
            new LibraryAcquisition { AccountName = "alice", AppId = 300, PackageId = 0, AcquiredAt = 1 });
        db.SaveChanges();
        _licenses = new FakeLicenseGateway();
        _source = new LicenseOwnershipSource(
            _factory, _licenses, null!, TimeProvider.System, NullLogger<LicenseOwnershipSource>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _source.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task WithoutASession_OwnershipIsUnknown()
    {
        _licenses.Current = null;

        (await _source.GetAsync("alice")).Should().BeNull();
    }

    [Test]
    public async Task OwnsTheAppsOfCurrentPackagesOnly()
    {
        _licenses.Current = new HashSet<uint> { 1 };

        var ownership = await _source.GetAsync("alice");

        ownership!.IsComplete.Should().BeTrue();
        // package 2 还在 library_acquisition 里（入库记录不删），但已经退款
        ownership.AppIds.Should().BeEquivalentTo([100u, 101u]);
    }

    [Test]
    public async Task ANewPackageInvalidatesTheCache()
    {
        _licenses.Current = new HashSet<uint> { 1 };
        await _source.GetAsync("alice");

        _licenses.Current = new HashSet<uint> { 1, 2 };

        (await _source.GetAsync("alice"))!.AppIds.Should().Contain(200u);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class FakeLicenseGateway : ISteamLicenseGateway
    {
        public IReadOnlySet<uint>? Current { get; set; }

        public IReadOnlySet<uint>? GetCurrentPackageIds(string accountName) => Current;

        public Task<SteamGatewayResult<IReadOnlyList<SteamLicensePackage>>> GetLicensedPackagesAsync(
            string accountName, IReadOnlySet<uint> knownPackageIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
