using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamStat.Platform.Linux;

/// <summary>
/// Steam 的 logs/connection_log.txt。状态行形如
/// <c>[2026-01-01 12:00:00] [Logged On, 4, 11] [U:1:87654321] …</c>，登出后账号变成 <c>[U:1:0]</c>；
/// 掉线或离线模式下账号保留。Steam 在登录完成前启动 steamwebhelper 时 -steamid 为 0，
/// registry.vdf 也可能没有 ActiveProcess，这时只有这里记录着当前账号。
/// </summary>
internal static partial class SteamConnectionLog
{
    /// <summary>状态行之后通常只有每小时一行的更新检查，这个长度足够覆盖</summary>
    private const int TailBytes = 64 * 1024;

    public static string PathIn(string steamRoot) => Path.Combine(steamRoot, "logs", "connection_log.txt");

    public static int ReadAccountId(string path)
    {
        try
        {
            // Steam 正在写这个文件
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return ParseAccountId(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static int ParseAccountId(string text)
    {
        var last = StateLine().Matches(text).LastOrDefault();
        return last != null
               && uint.TryParse(last.Groups["account"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
            ? unchecked((int)accountId)
            : 0;
    }

    [GeneratedRegex(@"^\[[^\]]+\] \[[A-Za-z ]+, \d+, \d+\] \[U:1:(?<account>\d+)\]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StateLine();
}
