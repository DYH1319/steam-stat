using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ElectronNet.Constants;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;

namespace ElectronNet.Services;

/// <summary>
/// 好友 Rich Presence（富文本状态）自定义处理器
/// SteamKit2 未内置好友 Rich Presence 支持（PersonaStateCallback 不会暴露 rich_presence 字段），
/// 因此通过自定义 ClientMsgHandler 发送 ClientRichPresenceRequest 并处理 ClientRichPresenceInfo 实现
/// </summary>
public class SteamRichPresenceHandler : ClientMsgHandler
{
    /// <summary>
    /// 请求指定好友的 Rich Presence
    /// </summary>
    /// <param name="appId">好友正在游玩的 AppID（用于消息路由，必须正确设置否则无响应）</param>
    /// <param name="steamIds">好友 SteamID 列表（须均在游玩同一 AppID）</param>
    public void RequestRichPresence(uint appId, IEnumerable<ulong> steamIds)
    {
        var request = new ClientMsgProtobuf<CMsgClientRichPresenceRequest>(EMsg.ClientRichPresenceRequest);
        request.Header.Proto.routing_appid = appId;
        request.Body.steamid_request.AddRange(steamIds);
        Client.Send(request);
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientRichPresenceInfo)
        {
            return;
        }

        var response = new ClientMsgProtobuf<CMsgClientRichPresenceInfo>(packetMsg);
        var entries = new List<RichPresenceInfoCallback.Entry>();

        foreach (var rp in response.Body.rich_presence)
        {
            var kvDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in rp.rich_presense)
            {
                if (!string.IsNullOrEmpty(kv.key))
                {
                    kvDict[kv.key] = kv.value ?? string.Empty;
                }
            }
            entries.Add(new RichPresenceInfoCallback.Entry(rp.steamid_user, kvDict));
        }

        Client.PostCallback(new RichPresenceInfoCallback(entries));
    }
}

/// <summary>
/// Rich Presence 信息回调（由 SteamRichPresenceHandler 发出）
/// </summary>
public sealed class RichPresenceInfoCallback : CallbackMsg
{
    public sealed record Entry(ulong SteamId, Dictionary<string, string> KeyValues);

    public IReadOnlyList<Entry> Entries { get; }

    public RichPresenceInfoCallback(List<Entry> entries)
    {
        Entries = entries;
    }
}

/// <summary>
/// Rich Presence 本地化与解析服务
/// 通过 Community.GetAppRichPresenceLocalization 获取游戏的富文本本地化 Token，
/// 并将好友的 Rich Presence KV（如 steam_display=#display_xxx + 各参数）解析为可读文本
/// </summary>
public static class SteamRichPresenceService
{
    /// <summary>
    /// 每个 (AppID, 语言) 的本地化 Token 缓存
    /// </summary>
    private static readonly ConcurrentDictionary<(uint AppId, string Language), Task<Dictionary<string, string>>> _localizationCache = new();

    /// <summary>
    /// 将 Rich Presence KV 解析为可读文本
    /// </summary>
    public static async Task<string> ResolveAsync(SteamClient client, uint appId, Dictionary<string, string> richPresenceKv)
    {
        if (appId == 0 || richPresenceKv.Count == 0)
        {
            return string.Empty;
        }

        richPresenceKv.TryGetValue("steam_display", out var displayToken);
        richPresenceKv.TryGetValue("status", out var status);

        // 没有 steam_display 时直接使用 status 字段
        if (string.IsNullOrEmpty(displayToken))
        {
            return status ?? string.Empty;
        }

        var language = GetSteamLanguage();
        var tokens = await GetLocalizationAsync(client, appId, language);

        var resolved = ResolveTokens(displayToken, richPresenceKv, tokens);

        // 解析失败（结果仍为未解析的 Token）时回退到 status 字段
        if (string.IsNullOrEmpty(resolved) || resolved.StartsWith('#'))
        {
            return status ?? string.Empty;
        }
        return resolved;
    }

    /// <summary>
    /// 迭代解析 Token：
    /// 1. 从本地化字典查找 steam_display 对应的模板
    /// 2. 反复替换 %key% 占位符（值来自 Rich Presence KV）与 {#token} / #token 引用（值来自本地化字典）
    /// </summary>
    private static string ResolveTokens(string displayToken, Dictionary<string, string> rp, Dictionary<string, string> tokens)
    {
        var result = tokens.GetValueOrDefault(displayToken, displayToken);

        for (var i = 0; i < 10; i++)
        {
            var before = result;

            // 替换 %key% 占位符
            result = Regex.Replace(result, "%([^%]+)%", m => rp.GetValueOrDefault(m.Groups[1].Value, m.Value));

            // 替换 {#token} 与 #token 引用（支持嵌套 Token）
            result = Regex.Replace(result, @"\{#([A-Za-z0-9_]+)\}|#([A-Za-z0-9_]+)", m =>
            {
                var name = $"#{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}";
                return tokens.GetValueOrDefault(name, m.Value);
            });

            if (result == before)
            {
                break;
            }
        }

        return result.Trim();
    }

    /// <summary>
    /// 获取指定 App 的 Rich Presence 本地化 Token（带缓存）
    /// </summary>
    private static async Task<Dictionary<string, string>> GetLocalizationAsync(SteamClient client, uint appId, string language)
    {
        var key = (appId, language);
        var task = _localizationCache.GetOrAdd(key, k => FetchLocalizationAsync(client, k.AppId, k.Language));
        var result = await task;

        // 获取失败时移除缓存，下次重试
        if (result.Count == 0)
        {
            _localizationCache.TryRemove(key, out _);
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> FetchLocalizationAsync(SteamClient client, uint appId, string language)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var unifiedMessages = client.GetHandler<SteamUnifiedMessages>();
            if (unifiedMessages == null)
            {
                return dict;
            }

            // SteamKit2 未内置 Community 服务，使用字符串形式的 RPC 端点与自定义 Proto 消息
            var response = await unifiedMessages.SendMessage<CCommunityGetAppRichPresenceLocalizationRequest, CCommunityGetAppRichPresenceLocalizationResponse>(
                "Community.GetAppRichPresenceLocalization#1",
                new CCommunityGetAppRichPresenceLocalizationRequest
                {
                    appid = appId,
                    language = language
                });

            if (response.Result != EResult.OK)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} GetAppRichPresenceLocalization failed for AppID {appId}: {response.Result}");
                return dict;
            }

            var tokenLists = response.Body?.token_lists ?? [];
            var tokenList = tokenLists.FirstOrDefault(l => l.language == language) ?? tokenLists.FirstOrDefault();
            if (tokenList != null)
            {
                foreach (var token in tokenList.tokens)
                {
                    if (!string.IsNullOrEmpty(token.name))
                    {
                        dict[token.name] = token.value ?? string.Empty;
                    }
                }
            }

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Loaded {dict.Count} rich presence localization tokens for AppID {appId} ({language})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} FetchLocalizationAsync failed for AppID {appId}: {ex.Message}");
        }
        return dict;
    }

    /// <summary>
    /// 根据应用设置获取 Steam API 使用的语言名称
    /// </summary>
    public static string GetSteamLanguage()
    {
        try
        {
            var language = SettingService.GetSettings().Language;
            if (string.IsNullOrEmpty(language) || language == "system")
            {
                language = Program.Locale;
            }

            if (language != null && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return language.Contains("TW", StringComparison.OrdinalIgnoreCase) || language.Contains("HK", StringComparison.OrdinalIgnoreCase)
                    ? "tchinese"
                    : "schinese";
            }
            return "english";
        }
        catch
        {
            return "english";
        }
    }
}

/// <summary>
/// 直接从 ClientPersonaState 报文读取 rich_presence 的自定义处理器
/// </summary>
public class PersonaStateRichPresenceHandler : ClientMsgHandler
{
    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientPersonaState)
        {
            return;
        }

        var personaState = new ClientMsgProtobuf<CMsgClientPersonaState>(packetMsg);

        foreach (var friend in personaState.Body.friends)
        {
            if (friend.rich_presence == null || friend.rich_presence.Count == 0)
            {
                continue;
            }

            var kvDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in friend.rich_presence)
            {
                if (!string.IsNullOrEmpty(kv.key))
                {
                    kvDict[kv.key] = kv.value ?? string.Empty;
                }
            }

            Client.PostCallback(new PersonaStateRichPresenceCallback(friend.friendid, friend.game_played_app_id, kvDict));
        }
    }
}

/// <summary>
/// 从 CMsgClientPersonaState 直接解析出的 Rich Presence 回调
/// </summary>
public sealed class PersonaStateRichPresenceCallback : CallbackMsg
{
    public ulong SteamId { get; }
    public uint AppId { get; }
    public Dictionary<string, string> KeyValues { get; }

    public PersonaStateRichPresenceCallback(ulong steamId, uint appId, Dictionary<string, string> keyValues)
    {
        SteamId = steamId;
        AppId = appId;
        KeyValues = keyValues;
    }
}

#region Community.GetAppRichPresenceLocalization Proto 消息定义（SteamKit2 未内置该服务）

// ReSharper disable InconsistentNaming
[ProtoContract]
public class CCommunityGetAppRichPresenceLocalizationRequest : IExtensible
{
    private IExtension? __pbn__extensionData;

    IExtension IExtensible.GetExtensionObject(bool createIfMissing)
        => Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

    [ProtoMember(1)]
    public uint appid { get; set; }

    [ProtoMember(2)]
    public string language { get; set; } = string.Empty;
}

[ProtoContract]
public class CCommunityGetAppRichPresenceLocalizationResponse : IExtensible
{
    private IExtension? __pbn__extensionData;

    IExtension IExtensible.GetExtensionObject(bool createIfMissing)
        => Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

    [ProtoMember(1)]
    public uint appid { get; set; }

    [ProtoMember(2)]
    public List<TokenList> token_lists { get; } = [];

    [ProtoContract]
    public class TokenList : IExtensible
    {
        private IExtension? __pbn__extensionData;

        IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            => Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

        [ProtoMember(1)]
        public string language { get; set; } = string.Empty;

        [ProtoMember(2)]
        public List<Token> tokens { get; } = [];
    }

    [ProtoContract]
    public class Token : IExtensible
    {
        private IExtension? __pbn__extensionData;

        IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            => Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

        [ProtoMember(1)]
        public string name { get; set; } = string.Empty;

        [ProtoMember(2)]
        public string value { get; set; } = string.Empty;
    }
}
// ReSharper restore InconsistentNaming

#endregion
