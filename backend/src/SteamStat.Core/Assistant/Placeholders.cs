using System.Text;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Assistant;

/// <summary>
/// 写作类请求里的游戏和好友换成 {G1}、{F1} 这样的代号再发给模型。
/// 没授权游戏名（或好友昵称）时模型只看到代号；授权了才附上对照表。
/// 模型的回答再换回周刊的占位符 [[g:570|Dota 2]] 或纯文字，所以隐藏游戏和主播模式在显示时仍然生效。
/// </summary>
public sealed partial class Placeholders(bool revealGames, bool revealFriends, IReadOnlySet<int>? hiddenAppIds = null)
{
    private readonly Dictionary<int, (string Token, string Name)> _games = [];
    private readonly Dictionary<string, (string Token, string Name)> _friends = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (char Kind, string Id)> _byToken = new(StringComparer.Ordinal);

    public bool RevealGames => revealGames;
    public bool RevealFriends => revealFriends;

    public string Game(int appId, string name)
    {
        if (_games.TryGetValue(appId, out var known)) return known.Token;
        var token = $"{{G{_games.Count + 1}}}";
        _games[appId] = (token, name);
        _byToken[token] = ('g', appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return token;
    }

    public string Friend(string steamId, string name)
    {
        if (_friends.TryGetValue(steamId, out var known)) return known.Token;
        var token = $"{{F{_friends.Count + 1}}}";
        _friends[steamId] = (token, name);
        _byToken[token] = ('f', steamId);
        return token;
    }

    /// <summary>周刊文字里的占位符换成代号。</summary>
    public string FromWeeklyText(string text)
        => WeeklyToken().Replace(text, match => match.Groups[1].Value == "g"
            ? Game(int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[3].Value)
            : Friend(match.Groups[2].Value, match.Groups[3].Value));

    /// <summary>发给模型的对照表。没授权的一类只说明它们是什么。</summary>
    public string Legend(bool chinese)
    {
        var builder = new StringBuilder();
        if (_games.Count > 0)
        {
            // 隐藏游戏和没有名字的游戏不进对照表，授权了游戏名也一样
            if (revealGames)
                foreach (var (appId, (token, name)) in _games)
                    builder.Append(token).Append(" = ").AppendLine(hiddenAppIds?.Contains(appId) == true || name.Length == 0
                        ? chinese ? "（一款游戏，名字不公开）" : "(a game whose name is private)"
                        : name);
            else
                builder.AppendLine(chinese
                    ? $"{string.Join("、", _games.Values.Select(item => item.Token))} 是游戏，用户没有授权透露名字。"
                    : $"{string.Join(", ", _games.Values.Select(item => item.Token))} are games; the user did not authorize sharing their names.");
        }
        if (_friends.Count > 0)
        {
            if (revealFriends)
                foreach (var (_, (token, name)) in _friends) builder.Append(token).Append(" = ").AppendLine(name);
            else
                builder.AppendLine(chinese
                    ? $"{string.Join("、", _friends.Values.Select(item => item.Token))} 是好友，用户没有授权透露昵称。"
                    : $"{string.Join(", ", _friends.Values.Select(item => item.Token))} are friends; the user did not authorize sharing their names.");
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>换回周刊占位符。模型编出来的代号（不在表里）整个删掉。</summary>
    public string ToWeeklyText(string text)
        => ModelToken().Replace(text, match => _byToken.TryGetValue(match.Value, out var target)
            ? target.Kind == 'g'
                ? $"[[g:{target.Id}|{Sanitize(_games[int.Parse(target.Id, System.Globalization.CultureInfo.InvariantCulture)].Name)}]]"
                : $"[[f:{target.Id}|{Sanitize(_friends[target.Id].Name)}]]"
            : string.Empty);

    /// <summary>换回名字，给不需要显示时再打码的地方（章节名、开场白、前情提要）。</summary>
    public string ToPlainText(string text)
        => ModelToken().Replace(text, match => _byToken.TryGetValue(match.Value, out var target)
            ? target.Kind == 'g'
                ? _games[int.Parse(target.Id, System.Globalization.CultureInfo.InvariantCulture)].Name
                : _friends[target.Id].Name
            : string.Empty);

    private static string Sanitize(string name) => name.Replace("]]", "] ]", StringComparison.Ordinal).Replace("|", "｜", StringComparison.Ordinal);

    [GeneratedRegex(@"\[\[([gf]):(\d+)\|([^\]]*)\]\]")]
    private static partial Regex WeeklyToken();

    [GeneratedRegex(@"\{[GF]\d+\}")]
    private static partial Regex ModelToken();
}
