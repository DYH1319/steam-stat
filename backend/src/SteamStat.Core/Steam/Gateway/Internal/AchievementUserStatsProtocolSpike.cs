using System.Text.Json;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal sealed class AchievementUserStatsProtocolHandler : ClientMsgHandler
{
    public AsyncJob<AchievementUserStatsCallback> GetUserStats(uint appId, ulong steamId)
    {
        if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
        if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
        var request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats)
        {
            SourceJobID = Client.GetNextJobID()
        };
        request.ProtoHeader.routing_appid = appId;
        request.Body.game_id = appId;
        request.Body.crc_stats = 0;
        request.Body.steam_id_for_user = steamId;
        Client.Send(request);
        return new AsyncJob<AchievementUserStatsCallback>(Client, request.SourceJobID);
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientGetUserStatsResponse) return;
        Client.PostCallback(new AchievementUserStatsCallback(packetMsg));
    }
}

internal sealed class AchievementUserStatsCallback : CallbackMsg
{
    public AchievementUserStatsCallback(IPacketMsg packetMsg)
    {
        var response = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);
        JobID = response.TargetJobID;
        Result = (EResult)response.Body.eresult;
        Body = response.Body;
    }

    public EResult Result { get; }
    public CMsgClientGetUserStatsResponse Body { get; }
}

internal sealed record SanitizedAchievementUserStatsFixture(
    SanitizedAchievementSchema Schema,
    IReadOnlyList<SanitizedAchievementBlock> AchievementBlocks);

internal sealed record SanitizedAchievementSchema(
    int SchemaVersion,
    uint SchemaHash,
    IReadOnlyList<SanitizedAchievementDefinition> Definitions,
    IReadOnlyList<SanitizedAchievementGroup> Groups);

internal sealed record SanitizedAchievementDefinition(
    uint StatId, uint Bit, uint InternalKey, string InternalName, bool Hidden, uint? GroupId);

internal sealed record SanitizedAchievementGroup(uint GroupId, bool Archived, bool DeveloperOnly, bool IsPublic);

internal sealed record SanitizedAchievementBlock(uint AchievementId, IReadOnlyList<uint> UnlockTime);

internal readonly record struct AchievementProtocolCoordinate(uint StatId, uint Bit);

internal sealed record DecodedAchievementUnlock(
    uint InternalKey, string InternalName, bool Hidden, uint? GroupId,
    AchievementProtocolCoordinate Coordinate, bool? IsUnlocked, DateTimeOffset? UnlockTimeUtc);

internal sealed record DecodedAchievementFixture(
    int SchemaVersion, uint SchemaHash,
    IReadOnlyList<DecodedAchievementUnlock> Entries,
    IReadOnlyList<AchievementProtocolCoordinate> UnmatchedUnlockedCoordinates);

internal static class SanitizedAchievementUserStatsFixtureDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static DecodedAchievementFixture Decode(string json)
    {
        if (json == null) throw new InvalidDataException("Fixture payload must not be null.");
        SanitizedAchievementUserStatsFixture? fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<SanitizedAchievementUserStatsFixture>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Fixture payload is not valid JSON.", exception);
        }
        if (fixture?.Schema == null) throw new InvalidDataException("Fixture payload has no schema.");

        var definitions = fixture.Schema.Definitions ?? [];
        var blocks = fixture.AchievementBlocks ?? [];

        var coordinates = new HashSet<AchievementProtocolCoordinate>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.InternalName))
                throw new InvalidDataException("Achievement definition has an empty internal name.");
            if (!coordinates.Add(new AchievementProtocolCoordinate(definition.StatId, definition.Bit)))
                throw new InvalidDataException($"Duplicate achievement coordinate ({definition.StatId},{definition.Bit}).");
            if (!names.Add(definition.InternalName))
                throw new InvalidDataException($"Duplicate achievement internal name '{definition.InternalName}'.");
        }

        var blocksById = new Dictionary<uint, SanitizedAchievementBlock>();
        foreach (var block in blocks)
            if (!blocksById.TryAdd(block.AchievementId, block))
                throw new InvalidDataException($"Duplicate achievement block '{block.AchievementId}'.");

        var entries = new List<DecodedAchievementUnlock>(definitions.Count);
        foreach (var definition in definitions)
        {
            bool? isUnlocked = null;
            DateTimeOffset? unlockTimeUtc = null;
            if (blocksById.TryGetValue(definition.StatId, out var block))
            {
                var unlockTimes = block.UnlockTime ?? [];
                if (definition.Bit < unlockTimes.Count)
                {
                    var seconds = unlockTimes[(int)definition.Bit];
                    if (seconds == 0)
                    {
                        isUnlocked = false;
                    }
                    else
                    {
                        isUnlocked = true;
                        unlockTimeUtc = ToUtc(seconds);
                    }
                }
            }
            entries.Add(new DecodedAchievementUnlock(
                definition.InternalKey,
                definition.InternalName,
                definition.Hidden,
                definition.GroupId,
                new AchievementProtocolCoordinate(definition.StatId, definition.Bit),
                isUnlocked,
                unlockTimeUtc));
        }

        var unmatched = new List<AchievementProtocolCoordinate>();
        foreach (var block in blocks)
        {
            var unlockTimes = block.UnlockTime ?? [];
            for (var bit = 0; bit < unlockTimes.Count; bit++)
            {
                if (unlockTimes[bit] == 0) continue;
                var coordinate = new AchievementProtocolCoordinate(block.AchievementId, (uint)bit);
                if (!coordinates.Contains(coordinate)) unmatched.Add(coordinate);
            }
        }
        unmatched.Sort((left, right) =>
            left.StatId != right.StatId
                ? left.StatId.CompareTo(right.StatId)
                : left.Bit.CompareTo(right.Bit));

        return new DecodedAchievementFixture(
            fixture.Schema.SchemaVersion, fixture.Schema.SchemaHash, entries, unmatched);
    }

    private static DateTimeOffset ToUtc(uint seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException($"Unlock timestamp '{seconds}' is out of range.", exception);
        }
    }
}
