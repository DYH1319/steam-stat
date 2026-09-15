using System.Text.Json;
using FluentAssertions;
using SteamKit2.Internal;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementUserStatsMapperTests
{
    [Test]
    public void MapUserStats_FixtureMapsStatusesOrderAndUnmatchedCoordinates()
    {
        var response = LoadBinaryFixture();

        var result = AchievementProtocolMapper.MapUserStats(response);

        result.Entries.Should().HaveCount(4);
        result.Entries.Should().OnlyContain(entry => entry.InternalKey == null);
        result.Entries.Select(entry => entry.InternalName).Should().Equal(
            "ACH_SMOKE_UNLOCKED",
            "ACH_SMOKE_LOCKED_ZERO",
            "ACH_SMOKE_OUT_OF_RANGE",
            "ACH_SMOKE_MISSING_BLOCK");

        result.Entries[0].IsUnlocked.Should().BeTrue();
        result.Entries[0].UnlockTimeUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        result.Entries[0].UnlockTimeUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
        result.Entries[1].IsUnlocked.Should().BeFalse();
        result.Entries[1].UnlockTimeUtc.Should().BeNull();
        result.Entries[2].IsUnlocked.Should().BeNull();
        result.Entries[2].UnlockTimeUtc.Should().BeNull();
        result.Entries[3].IsUnlocked.Should().BeNull();
        result.Entries[3].UnlockTimeUtc.Should().BeNull();

        result.UnmatchedUnlockedCoordinates.Should().Equal(
            new AchievementProtocolCoordinate(99, 0));
    }

    [Test]
    public void MapUserStats_EmptySchemaWithoutBlocks_IsSuccessEmpty()
    {
        var result = AchievementProtocolMapper.MapUserStats(
            new CMsgClientGetUserStatsResponse { schema = [] });

        result.Entries.Should().BeEmpty();
        result.UnmatchedUnlockedCoordinates.Should().BeEmpty();
    }

    [Test]
    public void MapUserStats_WrappedStatsLayout_IsSupported()
    {
        var schema = Doc(Node("wrapper", Node("stats", Stat(7, Bit(0, "ACH_SMOKE_WRAPPED")))));
        var response = Response(schema, Block(7, 1700000001u));

        var result = AchievementProtocolMapper.MapUserStats(response);

        result.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                InternalKey = (uint?)null,
                InternalName = "ACH_SMOKE_WRAPPED",
                IsUnlocked = (bool?)true,
                UnlockTimeUtc = (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1700000001)
            });
    }

    [Test]
    public void MapUserStats_MalformedResponses_AreInvalidData()
    {
        ((Action)(() => AchievementProtocolMapper.MapUserStats(null!)))
            .Should().Throw<InvalidDataException>();

        var emptySchemaWithBlock = Response([], Block(3, 1u));
        ((Action)(() => AchievementProtocolMapper.MapUserStats(emptySchemaWithBlock)))
            .Should().Throw<InvalidDataException>();

        var invalidKv = Response([0x01, 0x02, 0x03], []);
        ((Action)(() => AchievementProtocolMapper.MapUserStats(invalidKv)))
            .Should().Throw<InvalidDataException>();

        var missingStats = Response(Doc(Node("other", Str("k", "v"))), []);
        ((Action)(() => AchievementProtocolMapper.MapUserStats(missingStats)))
            .Should().Throw<InvalidDataException>();

        var duplicateCoordinate = Response(
            Doc(Node("stats", Node("3", Node("bits", Bit(0, "ACH_A"), Bit(0, "ACH_B"))))), []);
        ((Action)(() => AchievementProtocolMapper.MapUserStats(duplicateCoordinate)))
            .Should().Throw<InvalidDataException>();

        var duplicateName = Response(
            Doc(Node("stats", Stat(3, Bit(0, "ACH_SAME"), Bit(1, "ACH_SAME")))), []);
        ((Action)(() => AchievementProtocolMapper.MapUserStats(duplicateName)))
            .Should().Throw<InvalidDataException>();

        var blankName = Response(
            Doc(Node("stats", Stat(3, Bit(0, "  ")))), []);
        ((Action)(() => AchievementProtocolMapper.MapUserStats(blankName)))
            .Should().Throw<InvalidDataException>();

        var duplicateBlock = Response(
            Doc(Node("stats", Stat(3, Bit(0, "ACH_A")))), Block(3, 1u), Block(3, 2u));
        ((Action)(() => AchievementProtocolMapper.MapUserStats(duplicateBlock)))
            .Should().Throw<InvalidDataException>();

        var zeroBlock = Response(
            Doc(Node("stats", Stat(3, Bit(0, "ACH_A")))), Block(0, 1u));
        ((Action)(() => AchievementProtocolMapper.MapUserStats(zeroBlock)))
            .Should().Throw<InvalidDataException>();
    }

    private static CMsgClientGetUserStatsResponse LoadBinaryFixture()
    {
        var resourceName = "SteamStat.Core.Tests.Fixtures.Achievements.user-stats-binary.json";
        using var stream = typeof(AchievementUserStatsMapperTests).Assembly
            .GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull($"embedded fixture '{resourceName}' must exist");
        var fixture = JsonSerializer.Deserialize<FixtureShape>(
            stream!, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        fixture.Should().NotBeNull();
        var response = new CMsgClientGetUserStatsResponse
        {
            schema = Convert.FromBase64String(fixture!.SchemaBase64)
        };
        foreach (var block in fixture.AchievementBlocks)
        {
            var protoBlock = new CMsgClientGetUserStatsResponse.Achievement_Blocks
            {
                achievement_id = block.AchievementId
            };
            protoBlock.unlock_time.AddRange(block.UnlockTime);
            response.achievement_blocks.Add(protoBlock);
        }
        return response;
    }

    private static CMsgClientGetUserStatsResponse Response(
        byte[] schema, params CMsgClientGetUserStatsResponse.Achievement_Blocks[] blocks)
    {
        var response = new CMsgClientGetUserStatsResponse { schema = schema };
        response.achievement_blocks.AddRange(blocks);
        return response;
    }

    private static CMsgClientGetUserStatsResponse.Achievement_Blocks Block(
        uint achievementId, params uint[] unlockTimes)
    {
        var block = new CMsgClientGetUserStatsResponse.Achievement_Blocks
        {
            achievement_id = achievementId
        };
        block.unlock_time.AddRange(unlockTimes);
        return block;
    }

    private static byte[] Doc(params byte[][] children)
        => [.. children.SelectMany(child => child), 0x08];

    private static byte[] Node(string name, params byte[][] children)
        => [0x00, .. Encode(name), .. children.SelectMany(child => child), 0x08];

    private static byte[] Stat(uint statId, params byte[][] bits)
        => Node(statId.ToString(), Node("bits", bits));

    private static byte[] Bit(uint bit, string internalName)
        => Node(bit.ToString(), Str("name", internalName));

    private static byte[] Str(string name, string value)
        => [0x01, .. Encode(name), .. Encode(value)];

    private static byte[] Encode(string value)
        => [.. System.Text.Encoding.UTF8.GetBytes(value), 0x00];

    private sealed record FixtureShape(
        string SchemaBase64,
        IReadOnlyList<FixtureBlock> AchievementBlocks);

    private sealed record FixtureBlock(uint AchievementId, IReadOnlyList<uint> UnlockTime);
}
