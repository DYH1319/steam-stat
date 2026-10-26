using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SteamStat.Core.Helpers;

namespace ElectronNet.Features.History;

public sealed record ParsedSessionRow(
    int Line,
    int AppId,
    string? AppName,
    string? SteamId,
    int StartTime,
    int EndTime,
    int Duration,
    string? Note,
    IReadOnlyList<string> Tags);

public sealed record CsvRowError(int Line, string Code, string? Value);

public sealed record SessionCsvParseResult(
    string? FatalError,
    IReadOnlyList<string> Columns,
    IReadOnlyList<(string Field, string Column)> Mapping,
    int TotalRows,
    IReadOnlyList<ParsedSessionRow> Rows,
    IReadOnlyList<CsvRowError> Errors);

/// <summary>
/// 会话 CSV 解析：自动识别分隔符（, ; Tab）与常见列名别名（本软件 / 其他统计工具导出）。
/// 必需列：appId、开始时间，以及结束时间或时长（秒 / 分钟 / 小时）。
/// 时间支持 Unix 秒 / 毫秒、带时区的 ISO 8601，以及按本地时区解释的「yyyy-MM-dd HH:mm[:ss]」。
/// </summary>
public static partial class SessionCsvParser
{
    public const int MaxRows = 50_000;
    private const int MaxDurationSeconds = 7 * 24 * 3600;
    // 2001-09-09：早于 Steam 发布，过滤掉被误识别为时间戳的小数字
    private const long MinUnixTime = 1_000_000_000;

    private static readonly (string Field, string[] Aliases)[] FieldAliases =
    [
        ("appId", ["appid", "gameid", "steamappid", "appidnumber"]),
        ("appName", ["appname", "name", "game", "gamename", "title"]),
        ("steamId", ["steamid", "steamid64", "userid", "accountsteamid"]),
        ("start", ["start", "starttime", "startedat", "startat", "begin", "begintime", "startdate", "sessionstart"]),
        ("end", ["end", "endtime", "endedat", "endat", "stop", "stoptime", "enddate", "sessionend"]),
        ("durationSeconds", ["duration", "durationseconds", "durationsec", "durations", "seconds", "playtimeseconds", "length"]),
        ("durationMinutes", ["durationminutes", "durationmin", "minutes", "mins", "playtime", "playtimeminutes"]),
        ("durationHours", ["durationhours", "hours", "hrs", "playtimehours"]),
        ("note", ["note", "notes", "comment", "comments", "description"]),
        ("tags", ["tags", "tag", "labels"])
    ];

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
        "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy-M-d H:mm:ss", "yyyy-M-d H:mm",
        "yyyy/M/d H:mm:ss", "yyyy/M/d H:mm", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss.fff"
    ];

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();

    public static SessionCsvParseResult Parse(string content, TimeZoneInfo timeZone)
    {
        var records = ReadRecords(content.TrimStart('\uFEFF'));
        records.RemoveAll(record => record.Fields.All(string.IsNullOrWhiteSpace));
        if (records.Count == 0) return Fatal("empty", []);

        var header = records[0].Fields.Select(field => field.Trim()).ToList();
        var mapping = MapColumns(header);
        if (mapping.Count == 0) return Fatal("noHeader", header);
        if (!mapping.ContainsKey("appId")) return Fatal("missingAppId", header, mapping);
        if (!mapping.ContainsKey("start")) return Fatal("missingStart", header, mapping);
        if (!mapping.ContainsKey("end") && !mapping.ContainsKey("durationSeconds")
            && !mapping.ContainsKey("durationMinutes") && !mapping.ContainsKey("durationHours"))
            return Fatal("missingEndOrDuration", header, mapping);
        if (records.Count - 1 > MaxRows) return Fatal("tooManyRows", header, mapping);

        var rows = new List<ParsedSessionRow>();
        var errors = new List<CsvRowError>();
        foreach (var record in records.Skip(1))
        {
            string? Get(string field) => mapping.TryGetValue(field, out var index) && index < record.Fields.Count
                ? NullIfEmpty(record.Fields[index])
                : null;

            var line = record.Line;
            var appIdText = Get("appId");
            if (!int.TryParse(appIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
            {
                errors.Add(new CsvRowError(line, "invalidAppId", appIdText));
                continue;
            }

            var startText = Get("start");
            if (TryParseTime(startText, timeZone) is not { } start)
            {
                errors.Add(new CsvRowError(line, "invalidStart", startText));
                continue;
            }

            int? end = null;
            var endText = Get("end");
            if (endText != null)
            {
                end = TryParseTime(endText, timeZone);
                if (end == null)
                {
                    errors.Add(new CsvRowError(line, "invalidEnd", endText));
                    continue;
                }
            }

            int? duration = null;
            foreach (var (field, factor) in new[] { ("durationSeconds", 1d), ("durationMinutes", 60d), ("durationHours", 3600d) })
            {
                var text = Get(field);
                if (text == null) continue;
                if (TryParseDuration(text, factor) is not { } seconds)
                {
                    errors.Add(new CsvRowError(line, "invalidDuration", text));
                    duration = -1;
                    break;
                }
                duration = seconds;
                break;
            }
            if (duration == -1) continue;

            if (end == null && duration == null)
            {
                errors.Add(new CsvRowError(line, "invalidEnd", null));
                continue;
            }

            var endTime = end ?? start + duration!.Value;
            var length = duration ?? endTime - start;
            if (endTime <= start || length <= 0 || length > MaxDurationSeconds || length > endTime - start + 60)
            {
                errors.Add(new CsvRowError(line, "invalidRange", $"{startText} ~ {endText ?? length.ToString(CultureInfo.InvariantCulture)}"));
                continue;
            }

            var steamIdText = Get("steamId");
            string? steamId = null;
            if (steamIdText != null)
            {
                steamId = NormalizeSteamId(steamIdText);
                if (steamId == null)
                {
                    errors.Add(new CsvRowError(line, "invalidSteamId", steamIdText));
                    continue;
                }
            }

            var note = Get("note");
            if (note is { Length: > 2000 }) note = note[..2000];
            var tags = (Get("tags") ?? string.Empty)
                .Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => tag.Length > 32 ? tag[..32] : tag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            rows.Add(new ParsedSessionRow(line, appId, Get("appName"), steamId, start, endTime, length, note, tags));
        }

        return new SessionCsvParseResult(
            null,
            header,
            ToMappingList(header, mapping),
            records.Count - 1,
            rows,
            errors);
    }

    /// <summary>17 位 SteamID64，或 32 位 AccountID（自动转换）</summary>
    public static string? NormalizeSteamId(string value)
    {
        if (!ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0) return null;
        const ulong steamIdBase = 76561197960265728UL;
        if (number >= steamIdBase && number < steamIdBase + uint.MaxValue) return number.ToString(CultureInfo.InvariantCulture);
        if (number <= int.MaxValue) return SteamIdHelper.AccountIdToSteamId((int)number);
        return null;
    }

    public static int? TryParseTime(string? value, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var seconds = text.Length >= 12 ? number / 1000 : number;
            return seconds is >= MinUnixTime and < int.MaxValue ? (int)seconds : null;
        }

        if (OffsetSuffix().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
            return ToUnix(withOffset);

        if (DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out local))
        {
            try
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone);
                return ToUnix(new DateTimeOffset(utc, TimeSpan.Zero));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return null;
    }

    private static int? ToUnix(DateTimeOffset value)
    {
        var seconds = value.ToUnixTimeSeconds();
        return seconds is >= MinUnixTime and < int.MaxValue ? (int)seconds : null;
    }

    private static int? TryParseDuration(string text, double factor)
    {
        if (text.Contains(':') && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
            return span.TotalSeconds > 0 ? (int)Math.Round(span.TotalSeconds) : null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            var seconds = Math.Round(value * factor);
            return seconds < int.MaxValue ? (int)seconds : null;
        }
        return null;
    }

    private static Dictionary<string, int> MapColumns(IReadOnlyList<string> header)
    {
        var mapping = new Dictionary<string, int>();
        for (var index = 0; index < header.Count; index++)
        {
            var normalized = Normalize(header[index]);
            foreach (var (field, aliases) in FieldAliases)
            {
                if (mapping.ContainsKey(field) || !aliases.Contains(normalized)) continue;
                mapping[field] = index;
                break;
            }
        }
        return mapping;
    }

    private static string Normalize(string column)
    {
        var builder = new StringBuilder(column.Length);
        foreach (var c in column)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (c == '(' || c == '[') break;
        }
        return builder.ToString();
    }

    private static IReadOnlyList<(string Field, string Column)> ToMappingList(
        IReadOnlyList<string> header, Dictionary<string, int> mapping)
        => mapping.OrderBy(pair => pair.Value).Select(pair => (pair.Key, header[pair.Value])).ToList();

    private static SessionCsvParseResult Fatal(
        string code, IReadOnlyList<string> header, Dictionary<string, int>? mapping = null)
        => new(code, header, mapping == null ? [] : ToMappingList(header, mapping), 0, [], []);

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record CsvRecord(int Line, List<string> Fields);

    /// <summary>RFC 4180 风格读取：支持引号、双引号转义与字段内换行</summary>
    private static List<CsvRecord> ReadRecords(string content)
    {
        var delimiter = DetectDelimiter(content);
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                records.Add(new CsvRecord(recordLine, fields));
                fields = [];
                line++;
                recordLine = line;
            }
            else field.Append(c);
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, fields));
        }
        return records;
    }

    private static char DetectDelimiter(string content)
    {
        var end = content.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? content : content[..end];
        return new[] { ',', ';', '\t' }
            .OrderByDescending(candidate => header.Count(c => c == candidate))
            .First();
    }
}
