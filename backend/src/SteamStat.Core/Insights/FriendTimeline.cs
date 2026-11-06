using System.Text.Json;

namespace SteamStat.Core.Insights;

/// <summary>friend_status_record 的一行。Previous / Current 是 JSON。</summary>
public sealed record FriendEventRow(string FriendSteamId, string Name, string ChangeType, string? Previous, string? Current, long Time);

/// <summary>好友在玩某款游戏的一段时间。</summary>
public sealed record InsightFriendPlay(string SteamId, string Name, int AppId, long Start, long End);

public sealed record InsightFriendOnline(string SteamId, string Name, long Start, long End);

public sealed record InsightFriendChange(string SteamId, string Name, string Kind, string? From, string? To, long Time);

public sealed class InsightFriendData
{
    public static InsightFriendData Empty { get; } = new([], [], []);

    public InsightFriendData(IReadOnlyList<InsightFriendPlay> plays, IReadOnlyList<InsightFriendOnline> online, IReadOnlyList<InsightFriendChange> changes)
    {
        Plays = plays;
        Online = online;
        Changes = changes;
        _plays = plays.ToLookup(play => play.AppId);
    }

    private readonly ILookup<int, InsightFriendPlay> _plays;

    public IReadOnlyList<InsightFriendPlay> Plays { get; }
    public IReadOnlyList<InsightFriendOnline> Online { get; }
    /// <summary>好友列表增删和改名。</summary>
    public IReadOnlyList<InsightFriendChange> Changes { get; }

    public IEnumerable<InsightFriendPlay> PlaysOf(int appId) => _plays[appId];

    /// <summary>[start, end) 内这款游戏有好友在玩的总秒数，多位好友重叠只算一次。</summary>
    public long OverlapSeconds(int appId, long start, long end)
    {
        var pieces = PlaysOf(appId)
            .Select(play => (Start: Math.Max(play.Start, start), End: Math.Min(play.End, end)))
            .Where(piece => piece.End > piece.Start)
            .OrderBy(piece => piece.Start);
        long total = 0, cursor = long.MinValue;
        foreach (var piece in pieces)
        {
            var from = Math.Max(piece.Start, cursor);
            if (piece.End > from) total += piece.End - from;
            cursor = Math.Max(cursor, piece.End);
        }
        return total;
    }
}

/// <summary>由好友状态变化推算在线和游戏区间。和渲染层 buildIntervals 同一套规则，所有结果都是观测下限。</summary>
public static class FriendTimeline
{
    private const long MaxOnlineSeconds = 24 * 3600;
    private const long MaxGameSeconds = 12 * 3600;

    private sealed record Value(int? PersonaState, int AppId, string? PersonaName);

    private static Value Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Value(null, 0, null);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new Value(null, 0, null);
            int? state = root.TryGetProperty("personaState", out var stateValue) && stateValue.TryGetInt32(out var parsedState) ? parsedState : null;
            var appId = 0;
            if (root.TryGetProperty("gameId", out var game))
            {
                if (game.ValueKind == JsonValueKind.Number) game.TryGetInt32(out appId);
                else if (game.ValueKind == JsonValueKind.String) int.TryParse(game.GetString(), out appId);
            }
            var name = root.TryGetProperty("personaName", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
            return new Value(state, Math.Max(0, appId), name);
        }
        catch (JsonException)
        {
            return new Value(null, 0, null);
        }
    }

    /// <summary>events 按时间升序。收尾时没闭合的区间丢弃：不知道它什么时候结束。</summary>
    public static InsightFriendData Build(IEnumerable<FriendEventRow> events)
    {
        var plays = new List<InsightFriendPlay>();
        var online = new List<InsightFriendOnline>();
        var changes = new List<InsightFriendChange>();
        foreach (var friend in events.GroupBy(row => row.FriendSteamId))
        {
            long? onlineStart = null;
            (int AppId, long Start)? game = null;
            var steamId = friend.Key;
            foreach (var row in friend.OrderBy(item => item.Time))
            {
                switch (row.ChangeType)
                {
                    case "state":
                    {
                        var state = Parse(row.Current).PersonaState ?? 0;
                        if (state != 0) onlineStart ??= row.Time;
                        if (state != 0) break;
                        if (onlineStart != null)
                        {
                            AddOnline(online, steamId, row.Name, onlineStart.Value, row.Time);
                            onlineStart = null;
                        }
                        if (game != null)
                        {
                            AddPlay(plays, steamId, row.Name, game.Value, row.Time);
                            game = null;
                        }
                        break;
                    }
                    case "game":
                    {
                        var appId = Parse(row.Current).AppId;
                        if (game != null && game.Value.AppId != appId)
                        {
                            AddPlay(plays, steamId, row.Name, game.Value, row.Time);
                            game = null;
                        }
                        if (appId > 0 && game == null)
                        {
                            game = (appId, row.Time);
                            onlineStart ??= row.Time;
                        }
                        break;
                    }
                    case "relationship":
                    {
                        var kind = RelationshipOf(row.Current) == 3 ? "added" : "removed";
                        changes.Add(new InsightFriendChange(steamId, row.Name, kind, null, null, row.Time));
                        break;
                    }
                    case "personaName":
                        changes.Add(new InsightFriendChange(steamId, row.Name, "renamed", Parse(row.Previous).PersonaName, Parse(row.Current).PersonaName, row.Time));
                        break;
                }
            }
        }
        return new InsightFriendData(plays, online, changes);
    }

    private static int? RelationshipOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("relationship", out var value)
                   && value.TryGetInt32(out var relationship)
                ? relationship
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddOnline(List<InsightFriendOnline> list, string steamId, string name, long start, long end)
    {
        var capped = Math.Min(end, start + MaxOnlineSeconds);
        if (capped > start) list.Add(new InsightFriendOnline(steamId, name, start, capped));
    }

    private static void AddPlay(List<InsightFriendPlay> list, string steamId, string name, (int AppId, long Start) game, long end)
    {
        var capped = Math.Min(end, game.Start + MaxGameSeconds);
        if (capped > game.Start) list.Add(new InsightFriendPlay(steamId, name, game.AppId, game.Start, capped));
    }
}
