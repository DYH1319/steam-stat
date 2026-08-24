using ElectronNet.Constants;
using SteamKit2;
using SteamKit2.Internal;

namespace ElectronNet.Services;

/// <summary>
/// 好友 Steam 等级自定义处理器
/// SteamKit2 未内置好友等级支持，通过发送 ClientFSGetFriendsSteamLevels
/// 并处理 ClientFSGetFriendsSteamLevelsResponse 实现批量获取好友等级
/// </summary>
public class SteamLevelsHandler : ClientMsgHandler
{
    /// <summary>
    /// 请求指定好友的 Steam 等级（按 AccountID 批量请求），结果通过 FriendsSteamLevelsCallback 返回
    /// </summary>
    public void RequestFriendLevels(IEnumerable<uint> accountIds)
    {
        var ids = accountIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var request = new ClientMsgProtobuf<CMsgClientFSGetFriendsSteamLevels>(EMsg.ClientFSGetFriendsSteamLevels);
        request.Body.accountids.AddRange(ids);
        Client.Send(request);
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientFSGetFriendsSteamLevelsResponse)
        {
            return;
        }

        try
        {
            var response = new ClientMsgProtobuf<CMsgClientFSGetFriendsSteamLevelsResponse>(packetMsg);
            var levels = response.Body.friends
                .Where(f => f.accountid != 0)
                .ToDictionary(f => f.accountid, f => (int)f.level);

            Client.PostCallback(new FriendsSteamLevelsCallback(levels));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Failed to parse friends steam levels: {ex.Message}");
        }
    }
}

/// <summary>
/// 好友 Steam 等级回调（AccountID -> 等级）
/// </summary>
public sealed class FriendsSteamLevelsCallback : CallbackMsg
{
    public IReadOnlyDictionary<uint, int> Levels { get; }

    public FriendsSteamLevelsCallback(Dictionary<uint, int> levels)
    {
        Levels = levels;
    }
}
