using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementSchemaMapperTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    [Test]
    public void OrdinaryFixture_MapsEveryField()
    {
        var snapshot = AchievementProtocolMapper.MapSchema(1, "english", LoadResponse("schema-ordinary.json"));

        snapshot.AppId.Should().Be(1u);
        snapshot.Language.Should().Be("english");
        snapshot.ValveSchemaVersion.Should().Be(7);
        snapshot.SchemaHash.Should().Be(305419896u);
        snapshot.Definitions.Should().HaveCount(3);
        snapshot.Groups.Should().ContainSingle();

        var alpha = snapshot.Definitions[0];
        alpha.InternalKey.Should().Be(11u);
        alpha.InternalName.Should().Be("ACH_SYNTH_ALPHA");
        alpha.LocalizedName.Should().Be("Synthetic Alpha");
        alpha.LocalizedDescription.Should().Be("Synthetic alpha description");
        alpha.Icon.Should().Be("synthetic_alpha.jpg");
        alpha.IconGray.Should().Be("synthetic_alpha_gray.jpg");
        alpha.Hidden.Should().BeFalse();
        alpha.GlobalUnlockedPercent.Should().Be(12.5);
        alpha.GroupId.Should().Be(1u);
        alpha.Archived.Should().BeFalse();
        alpha.ProgressType.Should().Be(SteamAchievementProgressType.Int);
        alpha.MinProgress.Should().Be(0);
        alpha.MaxProgress.Should().Be(10);

        var beta = snapshot.Definitions[1];
        beta.InternalKey.Should().Be(12u);
        beta.InternalName.Should().Be("ACH_SYNTH_BETA");
        beta.Hidden.Should().BeTrue();
        beta.GlobalUnlockedPercent.Should().Be(3.25);
        beta.GroupId.Should().BeNull();
        beta.Archived.Should().BeFalse();
        beta.ProgressType.Should().Be(SteamAchievementProgressType.None);
        beta.MinProgress.Should().BeNull();
        beta.MaxProgress.Should().BeNull();

        var gamma = snapshot.Definitions[2];
        gamma.InternalKey.Should().Be(13u);
        gamma.InternalName.Should().Be("ACH_SYNTH_GAMMA");
        gamma.Archived.Should().BeTrue();
        gamma.GroupId.Should().BeNull();
        gamma.ProgressType.Should().Be(SteamAchievementProgressType.Float);
        gamma.MinProgress.Should().Be(0.5);
        gamma.MaxProgress.Should().Be(1.5);
        gamma.GlobalUnlockedPercent.Should().Be(99.9);

        var group = snapshot.Groups[0];
        group.GroupId.Should().Be(1u);
        group.LocalizedName.Should().Be("Synthetic Group");
        group.DlcAppId.Should().BeNull();
        group.Archived.Should().BeFalse();
        group.DeveloperOnly.Should().BeFalse();
        group.IsPublic.Should().BeTrue();
        group.Order.Should().Be(1u);
    }

    [Test]
    public void EmptyFixture_ProducesSuccessEmptySnapshot()
    {
        var snapshot = AchievementProtocolMapper.MapSchema(1, "english", LoadResponse("schema-empty.json"));

        snapshot.Definitions.Should().BeEmpty();
        snapshot.Groups.Should().BeEmpty();
        snapshot.ValveSchemaVersion.Should().Be(0);
        snapshot.SchemaHash.Should().Be(0u);
    }

    [Test]
    public void BoundaryFixture_PreservesUnknownValuesAndRejectsInvalidPercents()
    {
        var snapshot = AchievementProtocolMapper.MapSchema(1, "english", LoadResponse("schema-boundary.json"));

        var byName = snapshot.Definitions.ToDictionary(
            definition => definition.InternalName, StringComparer.Ordinal);
        var unknownType = byName["ACH_SYNTH_UNKNOWN_TYPE"];
        unknownType.ProgressType.Should().Be(SteamAchievementProgressType.Unknown);
        unknownType.MinProgress.Should().BeNull();
        unknownType.MaxProgress.Should().BeNull();
        unknownType.GlobalUnlockedPercent.Should().Be(1.5);
        byName["ACH_SYNTH_EMPTY_PERCENT"].GlobalUnlockedPercent.Should().BeNull();
        byName["ACH_SYNTH_COMMA_PERCENT"].GlobalUnlockedPercent.Should().BeNull();
        byName["ACH_SYNTH_TEXT_PERCENT"].GlobalUnlockedPercent.Should().BeNull();
        byName["ACH_SYNTH_NAN_PERCENT"].GlobalUnlockedPercent.Should().BeNull();
        byName["ACH_SYNTH_RANGE_PERCENT"].GlobalUnlockedPercent.Should().BeNull();
        byName["ACH_SYNTH_MISSING_GROUP"].GroupId.Should().Be(99u);

        var group = snapshot.Groups.Should().ContainSingle().Subject;
        group.GroupId.Should().Be(2u);
        group.DlcAppId.Should().Be(424242u);
        group.Archived.Should().BeTrue();
        group.DeveloperOnly.Should().BeTrue();
        group.IsPublic.Should().BeFalse();
        group.Order.Should().Be(3u);
    }

    [Test]
    public void PercentParsing_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var response = Response([Achievement("ACH_A", playerPercent: "12.5")]);
            var snapshot = AchievementProtocolMapper.MapSchema(1, "english", response);

            snapshot.Definitions.Should().ContainSingle()
                .Which.GlobalUnlockedPercent.Should().Be(12.5);

            response = Response([Achievement("ACH_B", playerPercent: "12,5")]);
            snapshot = AchievementProtocolMapper.MapSchema(1, "english", response);
            snapshot.Definitions.Should().ContainSingle()
                .Which.GlobalUnlockedPercent.Should().BeNull();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void InvalidDefinitions_AreRejected()
    {
        ((Action)(() => AchievementProtocolMapper.MapSchema(1, "english", null!)))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapSchema(1, "english",
                Response([Achievement("ACH_A"), Achievement("ACH_A")]))))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapSchema(1, "english",
                Response([Achievement("ACH_A", internalKey: 5), Achievement("ACH_B", internalKey: 5)]))))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapSchema(1, "english",
                Response([Achievement("   ")]))))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapSchema(1, "english",
                Response(groups: [Group(1), Group(1)]))))
            .Should().Throw<InvalidDataException>();
    }

    [Test]
    public void Definitions_PreserveProtocolOrder()
    {
        var snapshot = AchievementProtocolMapper.MapSchema(1, "english",
            Response([Achievement("ACH_C"), Achievement("ACH_A"), Achievement("ACH_B")]));

        snapshot.Definitions.Select(definition => definition.InternalName)
            .Should().Equal("ACH_C", "ACH_A", "ACH_B");
    }

    [Test]
    public void PublicSnapshots_DoNotExposeProtocolTypes()
    {
        var roots = new[]
        {
            typeof(SteamAchievementSchemaSnapshot),
            typeof(SteamAchievementUnlockSnapshot)
        };
        foreach (var type in roots.SelectMany(ReachableTypes))
        {
            var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            assemblyName.Should().NotBe("SteamKit2", $"{type} must not leak SteamKit2");
            assemblyName.Should().NotStartWith("protobuf-net", $"{type} must not leak protobuf-net");
            type.IsDefined(typeof(ProtoBuf.ProtoContractAttribute), false)
                .Should().BeFalse($"{type} must not carry ProtoContract");
        }
    }

    private static IEnumerable<Type> ReachableTypes(Type root)
    {
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (type.IsGenericParameter || !visited.Add(type)) continue;
            yield return type;
            if (type.HasElementType) pending.Push(type.GetElementType()!);
            foreach (var argument in type.GetGenericArguments()) pending.Push(argument);
            if (type.Assembly == typeof(SteamAchievementSchemaSnapshot).Assembly)
                foreach (var property in type.GetProperties())
                    pending.Push(property.PropertyType);
        }
    }

    private static AchievementSchemaResponse Response(
        IEnumerable<AchievementSchemaResponse.Achievement>? achievements = null,
        IEnumerable<AchievementSchemaResponse.Group>? groups = null,
        int? schemaVersion = null,
        uint? schemaHash = null)
    {
        var response = new AchievementSchemaResponse
        {
            schema_version = schemaVersion,
            schema_hash = schemaHash
        };
        if (achievements != null) response.achievements.AddRange(achievements);
        if (groups != null) response.groups.AddRange(groups);
        return response;
    }

    private static AchievementSchemaResponse.Achievement Achievement(
        string internalName,
        uint? internalKey = null,
        string? playerPercent = null)
        => new()
        {
            internal_name = internalName,
            internal_key = internalKey,
            player_percent_unlocked = playerPercent ?? string.Empty
        };

    private static AchievementSchemaResponse.Group Group(uint groupId)
        => new() { groupid = groupId };

    private static AchievementSchemaResponse LoadResponse(string name)
    {
        var response = JsonSerializer.Deserialize<AchievementSchemaResponse>(LoadFixture(name), JsonOptions);
        response.Should().NotBeNull($"fixture '{name}' must deserialize");
        return response!;
    }

    private static string LoadFixture(string name)
    {
        var resourceName = $"SteamStat.Core.Tests.Fixtures.Achievements.{name}";
        using var stream = typeof(AchievementSchemaMapperTests).Assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull($"embedded fixture '{resourceName}' must exist");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
