using ElectronNet.Features.History;
using FluentAssertions;

namespace ElectronNet.Tests.Features.History;

[TestFixture]
public sealed class SessionCsvParserTests
{
    private const long Now = 1_800_000_000L;
    private const string SteamId = "76561198012345678";

    private static SessionCsvParseResult Parse(string text, string? defaultSteamId = SteamId)
        => SessionCsvParser.Parse(text, defaultSteamId, TimeZoneInfo.Utc, Now);

    [Test]
    public void Parse_SemicolonDelimiter_AndQuotedFields()
    {
        var result = Parse(
            """
            appid;steamid;start;end;duration;note;tags
            570;76561198012345678;1700000000;1700003600;;"note, with comma";single|coop
            730;76561198012345678;1700000000;1700003600;;"line1
            line2";a|b
            """);

        result.Error.Should().BeNull();
        result.Errors.Should().BeEmpty();
        result.Rows.Should().HaveCount(2);
        result.Rows[0].AppId.Should().Be(570);
        result.Rows[0].Duration.Should().Be(3600);
        result.Rows[0].Note.Should().Be("note, with comma");
        result.Rows[0].Tags.Should().Equal("single", "coop");
        result.Rows[1].Note!.Replace("\r\n", "\n").Should().Be("line1\nline2");
        result.Rows[1].Line.Should().Be(3);
        result.Rows[1].Tags.Should().Equal("a", "b");
    }

    [Test]
    public void Parse_TimeFormats_UnixMsIsoAndLocal()
    {
        var result = Parse(
            """
            appId,start,end
            1,1700000000000,1700003600000
            2,2023-11-14T22:13:20+00:00,2023-11-14T23:13:20Z
            3,2023-11-14 22:13:20,2023-11-14 23:13:20
            4,2023/11/14 22:13:20,2023-11-14T23:13:20
            """);

        result.Errors.Should().BeEmpty();
        result.Rows.Should().HaveCount(4);
        result.Rows.Should().OnlyContain(r => r.Duration == 3600);
        result.Rows[0].Start.Should().Be(1_700_000_000);
        result.Rows[1].Start.Should().Be(1_700_000_000);
        result.Rows[2].Start.Should().Be(1_700_000_000);
        result.Rows[3].Start.Should().Be(1_700_000_000);
    }

    [Test]
    public void Parse_DurationMinutes_ComputesEnd()
    {
        var result = Parse(
            """
            appId,start,durationMinutes
            570,1700000000,30.5
            """);

        result.Errors.Should().BeEmpty();
        result.Rows[0].End.Should().Be(1_700_000_000 + 1830);
        result.Rows[0].Duration.Should().Be(1830);
    }

    [Test]
    public void Parse_AccountId_ConvertsToSteamId64()
    {
        var result = Parse(
            """
            appId,steamId,start,end
            570,222222,1700000000,1700003600
            """);

        result.Errors.Should().BeEmpty();
        result.Rows[0].SteamId.Should().Be("76561197960487950");
    }

    [Test]
    public void Parse_MissingColumns_IsGlobalError()
    {
        Parse("appId,start\n570,1700000000").Error.Should().Be("missingColumns");
        Parse("steamId,start\n1,1700000000").Error.Should().Be("missingColumns");
    }

    [Test]
    public void Parse_NoDataRows_IsEmptyError()
    {
        Parse("appId,start,end\n").Error.Should().Be("empty");
        Parse("").Error.Should().Be("empty");
    }

    [Test]
    public void Parse_RowErrorCodes()
    {
        var result = Parse(
            """
            appId,steamId,start,end
            bad,76561198012345678,1700000000,1700003600
            570,123456789012345678,1700000000,1700003600
            570,76561198012345678,nope,1700003600
            570,76561198012345678,1700000000,nope
            570,76561198012345678,1700003600,1700000000
            570,76561198012345678,1700000000,1800000000
            570,76561198012345678,1800000061,1800000700
            """);

        result.Rows.Should().BeEmpty();
        result.Errors.Select(e => e.Code).Should().Equal(
            "invalidAppId",
            "invalidSteamId",
            "invalidStart",
            "invalidEnd",
            "invalidRange",
            "invalidRange",
            "future");
        result.Errors.Select(e => e.Line).Should().Equal(2, 3, 4, 5, 6, 7, 8);
    }

    [Test]
    public void Parse_HeaderAliases_AreNormalized()
    {
        var result = Parse(
            """
            Game ID,Account,Started At,Ended At,Comment,Labels
            570,76561198012345678,1700000000,1700003600,hi,x
            """);

        result.Errors.Should().BeEmpty();
        result.Rows[0].Note.Should().Be("hi");
        result.Rows[0].Tags.Should().Equal("x");
    }
}
