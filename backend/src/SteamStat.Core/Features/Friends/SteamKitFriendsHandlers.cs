using Microsoft.Extensions.Logging;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamStat.Core.Features.Friends;

public sealed class SteamRichPresenceHandler : ClientMsgHandler
{
    public void RequestRichPresence(uint appId, IEnumerable<ulong> steamIds)
    {
        var request = new ClientMsgProtobuf<CMsgClientRichPresenceRequest>(EMsg.ClientRichPresenceRequest);
        request.Header.Proto.routing_appid = appId;
        request.Body.steamid_request.AddRange(steamIds);
        Client.Send(request);
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientRichPresenceInfo) return;
        var response = new ClientMsgProtobuf<CMsgClientRichPresenceInfo>(packetMsg);
        var entries = new List<RichPresenceInfoCallback.Entry>();
        foreach (var presence in response.Body.rich_presence)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in presence.rich_presense)
                if (!string.IsNullOrEmpty(item.key)) values[item.key] = item.value ?? string.Empty;
            entries.Add(new RichPresenceInfoCallback.Entry(presence.steamid_user, values));
        }
        Client.PostCallback(new RichPresenceInfoCallback(entries));
    }
}

public sealed class RichPresenceInfoCallback(IReadOnlyList<RichPresenceInfoCallback.Entry> entries) : CallbackMsg
{
    public sealed record Entry(ulong SteamId, Dictionary<string, string> KeyValues);
    public IReadOnlyList<Entry> Entries { get; } = entries;
}

public sealed class PersonaStateRichPresenceHandler : ClientMsgHandler
{
    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientPersonaState) return;
        var state = new ClientMsgProtobuf<CMsgClientPersonaState>(packetMsg);
        foreach (var friend in state.Body.friends)
        {
            if (friend.rich_presence == null || friend.rich_presence.Count == 0) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in friend.rich_presence)
                if (!string.IsNullOrEmpty(item.key)) values[item.key] = item.value ?? string.Empty;
            Client.PostCallback(new PersonaStateRichPresenceCallback(friend.friendid, friend.game_played_app_id, values));
        }
    }
}

public sealed class PersonaStateRichPresenceCallback(
    ulong steamId, uint appId, Dictionary<string, string> keyValues) : CallbackMsg
{
    public ulong SteamId { get; } = steamId;
    public uint AppId { get; } = appId;
    public Dictionary<string, string> KeyValues { get; } = keyValues;
}

public sealed class SteamLevelsHandler(ILogger logger) : ClientMsgHandler
{
    public void RequestFriendLevels(IEnumerable<uint> accountIds)
    {
        var ids = accountIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var request = new ClientMsgProtobuf<CMsgClientFSGetFriendsSteamLevels>(EMsg.ClientFSGetFriendsSteamLevels);
        request.Body.accountids.AddRange(ids);
        Client.Send(request);
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientFSGetFriendsSteamLevelsResponse) return;
        try
        {
            var response = new ClientMsgProtobuf<CMsgClientFSGetFriendsSteamLevelsResponse>(packetMsg);
            var levels = response.Body.friends.Where(item => item.accountid != 0)
                .ToDictionary(item => item.accountid, item => (int)item.level);
            Client.PostCallback(new FriendsSteamLevelsCallback(levels));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to parse friends Steam levels");
        }
    }
}

public sealed class FriendsSteamLevelsCallback(Dictionary<uint, int> levels) : CallbackMsg
{
    public IReadOnlyDictionary<uint, int> Levels { get; } = levels;
}

[ProtoContract]
public sealed class CCommunityGetAppRichPresenceLocalizationRequest : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing)
        => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
    [ProtoMember(2)] public string language { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class CCommunityGetAppRichPresenceLocalizationResponse : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing)
        => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
    [ProtoMember(2)] public List<TokenList> token_lists { get; } = [];

    [ProtoContract]
    public sealed class TokenList : IExtensible
    {
        private IExtension? _extensionData;
        IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
        [ProtoMember(1)] public string language { get; set; } = string.Empty;
        [ProtoMember(2)] public List<Token> tokens { get; } = [];
    }

    [ProtoContract]
    public sealed class Token : IExtensible
    {
        private IExtension? _extensionData;
        IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
        [ProtoMember(1)] public string name { get; set; } = string.Empty;
        [ProtoMember(2)] public string value { get; set; } = string.Empty;
    }
}
