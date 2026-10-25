using ElectronNet.Features.History;
using FluentAssertions;

namespace ElectronNet.Tests.Features.History;

[TestFixture]
public sealed class SessionCsvParserTests
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    [Test]
    public void Parse_MapsAliasesDetectsDelimiterAndParsesTimes()
    {
        const string csv = "\uFEFFApp ID;Game Name;Start Time;End Time;Steam ID;Notes;Tags\r\n" +
                           "570;\"Dota 2; Reborn\";2026-01-02 08:00;2026-01-02 09:30;76561198000000001;\"line1\nline2\";a|b\r\n" +
                           "730;CS2;1767312000;1767315600;;;\r\n";

        var result = SessionCsvParser.Parse(csv, Utc8);

        result.FatalError.Should().BeNull();
        result.TotalRows.Should().Be(2);
        result.Mapping.Select(m => m.Field).Should().Equal("appId", "appName", "start", "end", "steamId", "note", "tags");
        result.Errors.Should().BeEmpty();

        var dota = result.Rows[0];
        dota.Line.Should().Be(2);
        dota.AppName.Should().Be("Dota 2; Reborn");
        dota.StartTime.Should().Be((int)new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        dota.Duration.Should().Be(5400);
        dota.SteamId.Should().Be("76561198000000001");
        dota.Note.Should().Be("line1\nline2");
        dota.Tags.Should().Equal("a", "b");

        var cs = result.Rows[1];
        cs.Line.Should().Be(4);
        cs.SteamId.Should().BeNull();
        cs.Duration.Should().Be(3600);
    }

    [Test]
    public void Parse_UsesDurationColumnsAndReportsRowErrors()
    {
        const string csv = "appid,start,duration_minutes,steamid\n" +
                           "440,2026-03-01T10:00:00Z,90,39734273\n" +
                           "abc,2026-03-01T10:00:00Z,90,\n" +
                           "440,not-a-date,90,\n" +
                           "440,2026-03-01T10:00:00Z,-5,\n" +
                           "440,2026-03-01T10:00:00Z,1:30:00,\n";

        var result = SessionCsvParser.Parse(csv, TimeZoneInfo.Utc);

        result.Rows.Should().HaveCount(2);
        result.Rows[0].Duration.Should().Be(5400);
        result.Rows[0].SteamId.Should().Be("76561198000000001");
        result.Rows[1].Duration.Should().Be(5400);
        result.Errors.Select(e => (e.Line, e.Code)).Should().Equal(
            (3, "invalidAppId"), (4, "invalidStart"), (5, "invalidDuration"));
    }

    [TestCase("", "empty")]
    [TestCase("foo,bar\n1,2", "noHeader")]
    [TestCase("name,start,end\nx,1,2", "missingAppId")]
    [TestCase("appid,end\n1,2", "missingStart")]
    [TestCase("appid,start\n1,2", "missingEndOrDuration")]
    public void Parse_ReportsFatalHeaderErrors(string csv, string expected)
        => SessionCsvParser.Parse(csv, TimeZoneInfo.Utc).FatalError.Should().Be(expected);
}
