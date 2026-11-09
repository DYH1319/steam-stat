using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SteamStat.Core.Insights;

namespace SteamStat.Core.Circle;

public sealed record CardTotals(
    int LifetimeMinutes,
    int YearMinutes,
    int Last7Minutes,
    int Last30Minutes,
    int OwnedGames,
    int PlayedGames,
    int CompletedGames,
    int Achievements,
    int YearAchievements);

public sealed record CardGame(int AppId, string Name, int Minutes, int YearMinutes, int Unlocked, int Total, long LastPlayed);

public sealed record CardPersona(string Code, string Zh, string En);

/// <summary>
/// 交换卡的内容。字段名就是格式，只能加不能改；新字段在旧卡里没有，读取时要有默认值。
/// 只放汇总数字和前几十款游戏：要能塞进一条聊天消息。
/// </summary>
public sealed record CardPayload
{
    public int V { get; init; } = ExchangeCardCodec.Version;
    public string SteamId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long GeneratedAt { get; init; }
    public int Year { get; init; }
    /// <summary>生成时的 UTC 偏移（分钟），对齐两个人的作息要用。</summary>
    public int UtcOffsetMinutes { get; init; }
    public CardTotals Totals { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    /// <summary>这一年每个月的分钟数，12 项。</summary>
    public IReadOnlyList<int> Months { get; init; } = [];
    /// <summary>这一年的周作息，168 格，按一周从周一开始，最大的格子是 255。</summary>
    public IReadOnlyList<int> Heatmap { get; init; } = [];
    public IReadOnlyList<CardGame> Games { get; init; } = [];
    public CardPersona? Persona { get; init; }
}

public enum CardDecodeStatus
{
    Ok,
    /// <summary>卡设了口令，没给。</summary>
    NeedsPassphrase,
    /// <summary>口令不对，或者内容被改过（GCM 校验失败，两种情况分不开）。</summary>
    WrongPassphrase,
    Invalid,
    UnsupportedVersion
}

public sealed record CardDecodeResult(CardDecodeStatus Status, CardPayload? Card = null, bool Protected = false);

/// <summary>
/// 交换卡的编码：JSON → Deflate → AES-256-GCM → base64url，前面加 <see cref="Prefix"/>。
/// <para>
/// 没设口令时密钥由固定字符串派生，这只是打包，任何装了 Steam Stat 的人都能读；设了口令才是真的加密，
/// 口令要通过别的渠道告诉对方。界面上把这两种情况说清楚，不把打包说成保密。
/// </para>
/// GCM 的校验同时防止卡在传递中被改动：改了任何一个字符都解不开。卡里的数字是对方自己报的，校验不了真假。
/// </summary>
public static partial class ExchangeCardCodec
{
    public const int Version = 1;
    public const string Prefix = "SSCARD1.";
    public const int MaxEncodedLength = 64 * 1024;
    private const int MaxJsonBytes = 512 * 1024;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const byte FlagPassphrase = 1;
    private const int Pbkdf2Iterations = 210_000;
    private static readonly byte[] PackingSecret = "steam-stat exchange card v1"u8.ToArray();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict
    };

    [GeneratedRegex(@"SSCARD1\.[A-Za-z0-9_-]{40,}")]
    private static partial Regex CardPattern();

    /// <summary>从一段聊天文字里找出卡。</summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxEncodedLength * 4) return null;
        var match = CardPattern().Match(text);
        return match.Success && match.Length <= MaxEncodedLength ? match.Value : null;
    }

    public static string Encode(CardPayload card, string? passphrase)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(card, Json);
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        var plaintext = compressed.ToArray();

        var flags = string.IsNullOrEmpty(passphrase) ? (byte)0 : FlagPassphrase;
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = DeriveKey(flags, salt, passphrase);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Header(flags));

        var envelope = new byte[1 + SaltLength + NonceLength + TagLength + ciphertext.Length];
        envelope[0] = flags;
        salt.CopyTo(envelope, 1);
        nonce.CopyTo(envelope, 1 + SaltLength);
        tag.CopyTo(envelope, 1 + SaltLength + NonceLength);
        ciphertext.CopyTo(envelope, 1 + SaltLength + NonceLength + TagLength);
        return Prefix + Base64Url(envelope);
    }

    public static CardDecodeResult Decode(string? text, string? passphrase)
    {
        var code = Extract(text);
        if (code == null) return new CardDecodeResult(CardDecodeStatus.Invalid);
        byte[] envelope;
        try
        {
            envelope = FromBase64Url(code[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return new CardDecodeResult(CardDecodeStatus.Invalid);
        }
        if (envelope.Length < 1 + SaltLength + NonceLength + TagLength + 1) return new CardDecodeResult(CardDecodeStatus.Invalid);
        var flags = envelope[0];
        if ((flags & ~FlagPassphrase) != 0) return new CardDecodeResult(CardDecodeStatus.UnsupportedVersion);
        var isProtected = (flags & FlagPassphrase) != 0;
        if (isProtected && string.IsNullOrEmpty(passphrase)) return new CardDecodeResult(CardDecodeStatus.NeedsPassphrase, Protected: true);

        var salt = envelope.AsSpan(1, SaltLength);
        var nonce = envelope.AsSpan(1 + SaltLength, NonceLength);
        var tag = envelope.AsSpan(1 + SaltLength + NonceLength, TagLength);
        var ciphertext = envelope.AsSpan(1 + SaltLength + NonceLength + TagLength);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(DeriveKey(flags, salt.ToArray(), isProtected ? passphrase : null), TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Header(flags));
        }
        catch (AuthenticationTagMismatchException)
        {
            return new CardDecodeResult(isProtected ? CardDecodeStatus.WrongPassphrase : CardDecodeStatus.Invalid, Protected: isProtected);
        }

        try
        {
            using var input = new DeflateStream(new MemoryStream(plaintext), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                // 压缩炸弹：解压后的大小有上限
                if (output.Length > MaxJsonBytes) return new CardDecodeResult(CardDecodeStatus.Invalid, Protected: isProtected);
            }
            var card = JsonSerializer.Deserialize<CardPayload>(output.ToArray(), Json);
            if (card == null) return new CardDecodeResult(CardDecodeStatus.Invalid, Protected: isProtected);
            if (card.V > Version) return new CardDecodeResult(CardDecodeStatus.UnsupportedVersion, Protected: isProtected);
            var clean = Sanitize(card);
            return clean == null
                ? new CardDecodeResult(CardDecodeStatus.Invalid, Protected: isProtected)
                : new CardDecodeResult(CardDecodeStatus.Ok, clean, isProtected);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or NotSupportedException)
        {
            return new CardDecodeResult(CardDecodeStatus.Invalid, Protected: isProtected);
        }
    }

    /// <summary>别人的卡是不可信输入：长度和数值都裁到合理范围，身份不对的整张不要。</summary>
    public static CardPayload? Sanitize(CardPayload card)
    {
        if (card.SteamId.Length is < 1 or > 20 || !card.SteamId.All(char.IsAsciiDigit) || !ulong.TryParse(card.SteamId, out _)) return null;
        if (card.GeneratedAt <= 0 || card.Year is < 2003 or > 2200) return null;
        static int Minutes(int value) => Math.Clamp(value, 0, 50_000_000);
        static string Text(string? value, int max)
        {
            var text = (value ?? string.Empty).Trim();
            return text.Length > max ? text[..max] : text;
        }
        var totals = card.Totals ?? new CardTotals(0, 0, 0, 0, 0, 0, 0, 0, 0);
        return card with
        {
            Name = Text(card.Name, 64),
            UtcOffsetMinutes = Math.Clamp(card.UtcOffsetMinutes, -14 * 60, 14 * 60),
            Totals = new CardTotals(
                Minutes(totals.LifetimeMinutes), Minutes(totals.YearMinutes), Minutes(totals.Last7Minutes), Minutes(totals.Last30Minutes),
                Math.Clamp(totals.OwnedGames, 0, 100_000), Math.Clamp(totals.PlayedGames, 0, 100_000), Math.Clamp(totals.CompletedGames, 0, 100_000),
                Math.Clamp(totals.Achievements, 0, 10_000_000), Math.Clamp(totals.YearAchievements, 0, 10_000_000)),
            Months = Enumerable.Range(0, 12).Select(index => index < (card.Months?.Count ?? 0) ? Minutes(card.Months![index]) : 0).ToList(),
            Heatmap = Enumerable.Range(0, WeekHours.Cells).Select(index => index < (card.Heatmap?.Count ?? 0) ? Math.Clamp(card.Heatmap![index], 0, 255) : 0).ToList(),
            Games = (card.Games ?? [])
                .Where(game => game != null && game.AppId > 0)
                .DistinctBy(game => game.AppId)
                .Take(100)
                .Select(game => new CardGame(game.AppId, Text(game.Name, 128), Minutes(game.Minutes), Minutes(game.YearMinutes),
                    Math.Clamp(game.Unlocked, 0, 10_000), Math.Clamp(game.Total, 0, 10_000), Math.Max(0, game.LastPlayed)))
                .ToList(),
            Persona = card.Persona is { } persona && persona.Code.Length is > 0 and <= 8
                ? new CardPersona(Text(persona.Code, 8), Text(persona.Zh, 40), Text(persona.En, 60))
                : null
        };
    }

    private static byte[] DeriveKey(byte flags, byte[] salt, string? passphrase)
        => (flags & FlagPassphrase) != 0
            ? Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase!), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32)
            : HKDF.DeriveKey(HashAlgorithmName.SHA256, PackingSecret, 32, salt, "card"u8.ToArray());

    private static byte[] Header(byte flags) => [.. Encoding.ASCII.GetBytes(Prefix), flags];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return Convert.FromBase64String(value);
    }
}

/// <summary>从自己的档案生成交换卡。档案来自语义视图，隐藏游戏已经不在里面。</summary>
public static class ExchangeCardBuilder
{
    public const int TopLifetime = 40;
    public const int TopYear = 20;
    public const int MaxGames = 60;

    public static CardPayload Build(InsightArchive archive, string steamId, string name, int year, PlayerPersona? persona)
    {
        var options = archive.Options;
        var now = archive.Now;
        var sessions = archive.Sessions;
        var yearSessions = sessions.Where(session => options.LogicalDate(session.Start).Year == year).ToList();

        var owned = archive.Owned.GroupBy(game => game.AppId)
            .ToDictionary(group => group.Key, group => (Minutes: group.Sum(game => game.PlaytimeMinutes), LastPlayed: group.Max(game => game.LastPlayed), Name: group.First().Name));
        var recorded = sessions.GroupBy(session => session.AppId)
            .ToDictionary(group => group.Key, group => (Minutes: (int)(group.Sum(session => session.Seconds) / 60), LastPlayed: group.Max(session => session.End), Name: group.Last().Name));
        var yearByApp = yearSessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => (int)(group.Sum(session => session.Seconds) / 60));
        var progress = archive.Progress.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => (Total: group.Max(item => item.Total), Unlocked: group.Max(item => item.Unlocked)));

        int LifetimeOf(int appId) => Math.Max(owned.GetValueOrDefault(appId).Minutes, recorded.GetValueOrDefault(appId).Minutes);
        string NameOf(int appId) => owned.TryGetValue(appId, out var game) && game.Name.Length > 0 ? game.Name : recorded.TryGetValue(appId, out var played) ? played.Name : $"App {appId}";

        var apps = owned.Keys.Union(recorded.Keys).ToList();
        var top = apps.OrderByDescending(LifetimeOf).Take(TopLifetime)
            .Concat(yearByApp.OrderByDescending(pair => pair.Value).Take(TopYear).Select(pair => pair.Key))
            .Distinct()
            .Where(appId => LifetimeOf(appId) > 0 || yearByApp.GetValueOrDefault(appId) > 0)
            .Take(MaxGames)
            .Select(appId => new CardGame(
                appId, NameOf(appId), LifetimeOf(appId), yearByApp.GetValueOrDefault(appId),
                progress.GetValueOrDefault(appId).Unlocked, progress.GetValueOrDefault(appId).Total,
                Math.Max(owned.GetValueOrDefault(appId).LastPlayed, recorded.GetValueOrDefault(appId).LastPlayed)))
            .ToList();

        var months = new int[12];
        foreach (var session in yearSessions) months[options.LogicalDate(session.Start).Month - 1] += (int)(session.Seconds / 60);

        var heat = WeekHours.From(yearSessions.Select(session => new Span(session.Start, session.End)), options with { WeekStart = 1 });
        var max = heat.Max();

        int Window(long days)
        {
            var from = now - days * 86400;
            return (int)(sessions.Sum(session => Math.Max(0, Math.Min(session.End, now) - Math.Max(session.Start, from))) / 60);
        }

        return new CardPayload
        {
            SteamId = steamId,
            Name = name,
            GeneratedAt = now,
            Year = year,
            UtcOffsetMinutes = (int)options.Local(now).Offset.TotalMinutes,
            Totals = new CardTotals(
                apps.Sum(LifetimeOf),
                months.Sum(),
                Window(7),
                Window(30),
                owned.Count,
                apps.Count(appId => LifetimeOf(appId) > 0),
                progress.Count(pair => pair.Value.Total > 0 && pair.Value.Unlocked >= pair.Value.Total),
                archive.Unlocks.Count,
                archive.Unlocks.Count(unlock => options.LogicalDate(unlock.Time).Year == year)),
            Months = months,
            Heatmap = heat.Select(value => max <= 0 ? 0 : (int)Math.Round(value / max * 255)).ToList(),
            Games = top,
            Persona = persona == null ? null : new CardPersona(persona.Code, persona.Name.Zh, persona.Name.En)
        };
    }
}
