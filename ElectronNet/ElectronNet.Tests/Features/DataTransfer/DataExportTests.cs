using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ElectronNet.Features.DataTransfer;
using ElectronNet.Features.History;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;

namespace ElectronNet.Tests.Features.DataTransfer;

[TestFixture]
public sealed class DataExportTests
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private string _root = null!;
    private TestDbContextFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = new TestDbContextFactory(Path.Combine(_root, "test.db"));
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    #region CSV

    [Test]
    public void Csv_EscapesQuotesAndNeutralizesFormulas()
    {
        CsvTableWriter.FormatCell("a,b", ExportColumnKind.Text, Utc8).Should().Be("\"a,b\"");
        CsvTableWriter.FormatCell("say \"hi\"", ExportColumnKind.Text, Utc8).Should().Be("\"say \"\"hi\"\"\"");
        CsvTableWriter.FormatCell("=HYPERLINK(\"x\")", ExportColumnKind.Text, Utc8).Should().Be("\"'=HYPERLINK(\"\"x\"\")\"");
        CsvTableWriter.FormatCell("@SUM(A1)", ExportColumnKind.Text, Utc8).Should().Be("'@SUM(A1)");
        // 数值列不加引号前缀
        CsvTableWriter.FormatCell(-5L, ExportColumnKind.Integer, Utc8).Should().Be("-5");
        CsvTableWriter.FormatCell(1.5, ExportColumnKind.Real, Utc8).Should().Be("1.5");
        CsvTableWriter.FormatCell(true, ExportColumnKind.Boolean, Utc8).Should().Be("true");
        CsvTableWriter.FormatCell(null, ExportColumnKind.Text, Utc8).Should().BeEmpty();
        // 2026-01-02 00:00:00 UTC = 08:00 UTC+8
        CsvTableWriter.FormatCell(1767312000L, ExportColumnKind.Time, Utc8).Should().Be("2026-01-02 08:00:00");
        CsvTableWriter.FormatCell(0L, ExportColumnKind.Time, Utc8).Should().BeEmpty();
    }

    [Test]
    public async Task Csv_SessionsRoundTripThroughTheImporter()
    {
        await SeedAsync();
        var path = Path.Combine(_root, "sessions.csv");

        var result = await CreateService().ExportToAsync(new DataExportRequest { Format = "csv", Datasets = ["sessions"] }, path);

        result.Status.Should().Be("completed");
        result.Tables.Should().ContainSingle().Which.Rows.Should().Be(2);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);

        var parsed = SessionCsvParser.Parse(Encoding.UTF8.GetString(bytes), Utc8);
        parsed.FatalError.Should().BeNull();
        parsed.Errors.Should().BeEmpty();
        parsed.Rows.Should().HaveCount(2);
        var first = parsed.Rows[0];
        first.AppId.Should().Be(570);
        first.AppName.Should().Be("Dota 2");
        first.SteamId.Should().Be("76561198000000001");
        first.StartTime.Should().Be(1767312000);
        first.EndTime.Should().Be(1767315600);
        first.Duration.Should().Be(3000);
        first.Note.Should().Be("-gg, wp");
        first.Tags.Should().Equal("ranked", "with friends");
        // 进行中的会话（没有结束时间）不导出
        parsed.Rows.Select(row => row.AppId).Should().Equal(570, 730);
    }

    [Test]
    public async Task Csv_MultipleDatasetsWriteOneFilePerDatasetIntoTheFolder()
    {
        await SeedAsync();
        var folder = Path.Combine(_root, "export");

        var result = await CreateService().ExportToAsync(
            new DataExportRequest { Format = "csv", Datasets = ["library", "sessions", "achievementUnlocks"] }, folder);

        result.Status.Should().Be("completed");
        result.Tables.Select(table => table.Id).Should().Equal("sessions", "achievementUnlocks", "library");
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo("sessions.csv", "achievementUnlocks.csv", "library.csv");
        Directory.GetFiles(folder, "*.tmp").Should().BeEmpty();

        var library = await File.ReadAllLinesAsync(Path.Combine(folder, "library.csv"));
        library[0].TrimStart('﻿').Should().StartWith("accountName,appId,appName,acquiredAt");
        library.Should().HaveCount(3); // 表头 + 只有入库时间 + 只有状态
    }

    [Test]
    public void Csv_RejectsUnknownOrEmptyDatasets()
    {
        DataExportService.NormalizeDatasets(new DataExportRequest { Format = "csv", Datasets = [] }).Should().BeNull();
        DataExportService.NormalizeDatasets(new DataExportRequest { Format = "xlsx", Datasets = ["steamLoginTokens"] }).Should().BeNull();
        DataExportService.NormalizeDatasets(new DataExportRequest { Format = "json" }).Should().BeEquivalentTo(ExportDatasets.All);
    }

    [Test]
    public async Task Csv_TimeRangeFiltersTimedDatasets()
    {
        await SeedAsync();
        var path = Path.Combine(_root, "range.csv");

        var result = await CreateService().ExportToAsync(
            new DataExportRequest { Format = "csv", Datasets = ["sessions"], From = 1767400000, To = 1767500000 }, path);

        result.Tables.Single().Rows.Should().Be(1);
    }

    #endregion

    #region xlsx

    [Test]
    public async Task Xlsx_WritesAValidWorkbookWithOneSheetPerDataset()
    {
        await SeedAsync();
        var path = Path.Combine(_root, "export.xlsx");

        var result = await CreateService().ExportToAsync(
            new DataExportRequest { Format = "xlsx", Datasets = ["sessions", "friendRecords"] }, path);

        result.Status.Should().Be("completed");
        using var zip = ZipFile.OpenRead(path);
        zip.Entries.Select(entry => entry.FullName).Should().Contain(
        [
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml",
            "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"
        ]);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            var act = () => XDocument.Load(stream);
            act.Should().NotThrow($"{entry.FullName} must be well-formed XML");
        }

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument workbook;
        using (var stream = zip.GetEntry("xl/workbook.xml")!.Open()) workbook = XDocument.Load(stream);
        workbook.Descendants(ns + "sheet").Select(sheet => (string)sheet.Attribute("name")!).Should().Equal("sessions", "friendRecords");

        XDocument sheet;
        using (var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()) sheet = XDocument.Load(stream);
        var rows = sheet.Descendants(ns + "row").ToList();
        rows.Should().HaveCount(3);
        rows[0].Elements(ns + "c").First().Value.Should().Be("id");
        // 时间列：带日期格式的序列值（2026-01-02 08:00 UTC+8）
        var start = rows[1].Elements(ns + "c").Single(cell => (string)cell.Attribute("r")! == "F2");
        ((string)start.Attribute("s")!).Should().Be("2");
        double.Parse(start.Value, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately(new DateTime(2026, 1, 2, 8, 0, 0).ToOADate(), 1e-6);
        // 文本用 inlineStr，不会被当成公式
        var note = rows[1].Elements(ns + "c").Single(cell => (string)cell.Attribute("r")! == "N2");
        ((string)note.Attribute("t")!).Should().Be("inlineStr");
        note.Value.Should().Be("-gg, wp");
    }

    [Test]
    public void Xlsx_HelpersProduceExcelCompatibleNames()
    {
        XlsxWorkbookWriter.CellRef(0, 1).Should().Be("A1");
        XlsxWorkbookWriter.CellRef(25, 3).Should().Be("Z3");
        XlsxWorkbookWriter.CellRef(26, 3).Should().Be("AA3");
        XlsxWorkbookWriter.CellRef(701, 9).Should().Be("ZZ9");
        XlsxWorkbookWriter.CellRef(702, 9).Should().Be("AAA9");
        XlsxWorkbookWriter.EscapeXml("a<b>&\"\u0001\uD800c😀").Should().Be("a&lt;b&gt;&amp;&quot;c😀");
        XlsxWorkbookWriter.UniqueSheetNames(["a/b", "A:B", new string('x', 40)])
            .Should().Equal("ab", "AB (2)", new string('x', 31));
    }

    #endregion

    #region JSON

    [Test]
    public async Task Json_DumpsArchiveTablesAndNeverIncludesCredentials()
    {
        await SeedAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SteamLoginTokenTable.Add(new SteamLoginToken { AccountName = "alice", AccessToken = "secret-access", RefreshToken = "secret-refresh" });
            await db.SaveChangesAsync();
        }
        var path = Path.Combine(_root, "export.json");

        var result = await CreateService().ExportToAsync(new DataExportRequest { Format = "json" }, path);

        result.Status.Should().Be("completed");
        var text = await File.ReadAllTextAsync(path);
        text.Should().NotContain("secret-access").And.NotContain("secret-refresh");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        root.GetProperty("format").GetString().Should().Be("steam-stat-export");
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("migration").GetString().Should().NotBeNullOrEmpty();
        var tables = root.GetProperty("tables");
        tables.TryGetProperty("steam_login_token", out _).Should().BeFalse();
        tables.TryGetProperty("steam_resource_cache", out _).Should().BeFalse();
        var sessions = tables.GetProperty("use_app_record");
        sessions.GetArrayLength().Should().Be(3);
        sessions[0].GetProperty("app_id").GetInt32().Should().Be(570);
        root.GetProperty("counts").GetProperty("use_app_record").GetInt32().Should().Be(3);
    }

    #endregion

    private async Task SeedAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SteamAppTable.Add(new SteamApp { AppId = 570, Name = "Dota 2", NameLocalizedJson = "{}" });
        db.SteamUserTable.Add(new SteamUser { SteamId = "76561198000000001", AccountId = 39734273, AccountName = "alice" });
        db.UseAppRecordTable.AddRange(
            new UseAppRecord
            {
                AppId = 570, SteamId = "76561198000000001", StartTime = 1767312000, EndTime = 1767315600, Duration = 3000,
                IdleDuration = 120, Note = "-gg, wp", Tags = "[\"ranked\",\"with friends\"]"
            },
            new UseAppRecord { AppId = 730, SteamId = "76561198000000001", StartTime = 1767450000, EndTime = 1767453600, Duration = 3600 },
            new UseAppRecord { AppId = 440, SteamId = "76561198000000001", StartTime = 1767460000 });
        db.FriendStatusRecordTable.Add(new FriendStatusRecord
        {
            AccountName = "alice", FriendSteamId = "76561198000000002", FriendPersonaName = "=cmd|' /C calc'!A0",
            ChangeType = "persona", PreviousValue = "a", CurrentValue = "b", Timestamp = 1767312000
        });
        db.AchievementUnlockArchiveTable.Add(new AchievementUnlockArchive
        {
            AccountName = "alice", SteamId = "76561198000000001", AppId = 570, InternalName = "WIN", Name = "Win", UnlockTime = 1767313000, RecordedAt = 1767313000
        });
        db.LibraryAcquisitionTable.Add(new LibraryAcquisition { AccountName = "alice", AppId = 570, PackageId = 197845, AcquiredAt = 1500000000 });
        db.LibraryGameMetaTable.Add(new LibraryGameMeta { AccountName = "alice", AppId = 730, Status = "playing", Rating = 8, UpdatedAt = 1767312000 });
        await db.SaveChangesAsync();
    }

    private DataExportService CreateService()
        => new(
            _factory,
            new NoWindow(),
            new NoShell(),
            new RevealRegistry(),
            new AppEnvironment(false, "en-US", false, new AppPaths(_root), "4.2.0"),
            new DeviceService(_factory, TimeProvider.System, NullLogger<DeviceService>.Instance),
            new ZoneTimeProvider(Utc8),
            NullLogger<DataExportService>.Instance);

    private sealed class ZoneTimeProvider(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class NoWindow : IMainWindowAccessor
    {
        public Task<MainWindowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MainWindowSnapshot(null, MainWindowAvailability.Missing));
    }

    private sealed class NoShell : IShellLauncher
    {
        public Task<bool> OpenExternalAsync(string url) => Task.FromResult(true);
        public Task<bool> ShowItemInFolderAsync(string fullPath) => Task.FromResult(true);
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
