using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ElectronNet.Services;

namespace ElectronNet.Features.History;

/// <summary>
/// 解析出的一行有效会话
/// </summary>
public sealed record ParsedSessionRow(
    int Line,
    string SteamId,
    int AppId,
    int Start,
    int End,
    int Duration,
    string? Note,
    IReadOnlyList<string> Tags);

/// <summary>
/// 单行解析错误（Line 为记录起始的物理行号，1 起）
/// </summary>
public sealed record SessionCsvRowError(int Line, string Code);

/// <summary>
/// CSV 解析结果：有效行、逐行错误与全局错误码
/// </summary>
public sealed record SessionCsvParseResult(
    IReadOnlyList<ParsedSessionRow> Rows,
    IReadOnlyList<SessionCsvRowError> Errors,
    string? Error);

/// <summary>
/// 通用会话 CSV 导入解析器（RFC4180 引号、自动分隔符、宽松时间格式）
/// </summary>
public static partial class SessionCsvParser
{
    private const ulong SteamId64Base = 76561197960265728UL;
    private const int MaxDurationSeconds = 7 * 24 * 3600;

    private static readonly string[] ExactLocalFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy/MM/dd HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm"
    ];

    public static SessionCsvParseResult Parse(
        string text,
        string? defaultSteamId,
        TimeZoneInfo tz,
        long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tz);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var (headerFields, records, delimiter) = ParseRecords(text);
        if (headerFields == null)
        {
            return new SessionCsvParseResult([], [], "empty");
        }

        var columns = ResolveColumns(headerFields);
        var errors = new List<SessionCsvRowError>();
        var rows = new List<ParsedSessionRow>();

        var hasEnd = columns.End >= 0;
        var hasDuration = columns.DurationSeconds >= 0 || columns.DurationMinutes >= 0;
        if (columns.AppId < 0 || columns.Start < 0 || (!hasEnd && !hasDuration))
        {
            return new SessionCsvParseResult([], [], "missingColumns");
        }
        if (records.Count == 0)
        {
            return new SessionCsvParseResult([], [], "empty");
        }

        foreach (var record in records)
        {
            string? Field(int index)
                => index >= 0 && index < record.Fields.Count ? record.Fields[index].Trim() : null;

            var line = record.Line;

            var appIdField = Field(columns.AppId);
            if (!int.TryParse(appIdField, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)
                || appId <= 0)
            {
                errors.Add(new SessionCsvRowError(line, "invalidAppId"));
                continue;
            }

            var steamIdField = Field(columns.SteamId);
            var steamId = string.IsNullOrEmpty(steamIdField)
                ? defaultSteamId
                : steamIdField;
            if (ulong.TryParse(steamId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId)
                && numericId > 0 && numericId <= 4294967295UL)
            {
                steamId = (SteamId64Base + numericId).ToString(CultureInfo.InvariantCulture);
            }
            if (steamId == null || !SteamId64Regex().IsMatch(steamId))
            {
                errors.Add(new SessionCsvRowError(line, "invalidSteamId"));
                continue;
            }

            var startField = Field(columns.Start);
            if (string.IsNullOrEmpty(startField) || !TryParseTime(startField, tz, out var start))
            {
                errors.Add(new SessionCsvRowError(line, "invalidStart"));
                continue;
            }

            long end;
            var endField = hasEnd ? Field(columns.End) : null;
            if (!string.IsNullOrEmpty(endField))
            {
                if (!TryParseTime(endField, tz, out end))
                {
                    errors.Add(new SessionCsvRowError(line, "invalidEnd"));
                    continue;
                }
            }
            else if (hasDuration)
            {
                double? durationSeconds = null;
                if (columns.DurationSeconds >= 0)
                {
                    var durationField = Field(columns.DurationSeconds);
                    if (!string.IsNullOrEmpty(durationField))
                    {
                        if (!double.TryParse(durationField, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                        {
                            errors.Add(new SessionCsvRowError(line, "invalidEnd"));
                            continue;
                        }
                        durationSeconds = seconds;
                    }
                }
                if (durationSeconds == null && columns.DurationMinutes >= 0)
                {
                    var durationField = Field(columns.DurationMinutes);
                    if (!string.IsNullOrEmpty(durationField))
                    {
                        if (!double.TryParse(durationField, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
                        {
                            errors.Add(new SessionCsvRowError(line, "invalidEnd"));
                            continue;
                        }
                        durationSeconds = minutes * 60;
                    }
                }
                if (durationSeconds == null)
                {
                    errors.Add(new SessionCsvRowError(line, "invalidEnd"));
                    continue;
                }
                end = start + (long)Math.Round(durationSeconds.Value, MidpointRounding.AwayFromZero);
            }
            else
            {
                errors.Add(new SessionCsvRowError(line, "invalidEnd"));
                continue;
            }

            var duration = end - start;
            if (duration <= 0 || duration > MaxDurationSeconds)
            {
                errors.Add(new SessionCsvRowError(line, "invalidRange"));
                continue;
            }
            if (start > nowUnix + 60)
            {
                errors.Add(new SessionCsvRowError(line, "future"));
                continue;
            }

            var note = Field(columns.Note);
            if (note != null && note.Length > 2000) note = note[..2000];
            var tags = columns.Tags >= 0
                ? SessionManagementService.NormalizeTags(
                    (Field(columns.Tags) ?? string.Empty).Split([';', '|', ','], StringSplitOptions.None))
                : (IReadOnlyList<string>)[];

            rows.Add(new ParsedSessionRow(
                line,
                steamId,
                appId,
                (int)start,
                (int)end,
                (int)duration,
                string.IsNullOrWhiteSpace(note) ? null : note,
                tags));
        }

        return new SessionCsvParseResult(rows, errors, null);
    }

    private static (List<string>? Header, List<CsvRecord> Records, char Delimiter) ParseRecords(string text)
    {
        var physicalLines = new List<(int LineNumber, string Text)>();
        var lineStart = 0;
        var lineNumber = 1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n') continue;
            var raw = text[lineStart..i];
            if (raw.EndsWith('\r')) raw = raw[..^1];
            physicalLines.Add((lineNumber, raw));
            lineStart = i + 1;
            lineNumber++;
        }

        // 先按物理行找表头行以检测分隔符
        var headerIndex = -1;
        for (var i = 0; i < physicalLines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(physicalLines[i].Text))
            {
                headerIndex = i;
                break;
            }
        }
        if (headerIndex < 0) return (null, [], ',');

        var delimiter = DetectDelimiter(physicalLines[headerIndex].Text);
        var records = SplitRecords(text, delimiter);
        if (records.Count == 0) return (null, [], delimiter);

        return (records[0].Fields, records.Skip(1).ToList(), delimiter);
    }

    private static char DetectDelimiter(string headerLine)
    {
        var candidates = new[] { ',', ';', '\t' };
        var best = ',';
        var bestCount = -1;
        foreach (var candidate in candidates)
        {
            var inQuotes = false;
            var count = 0;
            for (var i = 0; i < headerLine.Length; i++)
            {
                var c = headerLine[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < headerLine.Length && headerLine[i + 1] == '"') i++;
                        else inQuotes = false;
                    }
                }
                else if (c == '"') inQuotes = true;
                else if (c == candidate) count++;
            }
            if (count > bestCount)
            {
                bestCount = count;
                best = candidate;
            }
        }
        return best;
    }

    private sealed record CsvRecord(int Line, List<string> Fields);

    private static List<CsvRecord> SplitRecords(string text, char delimiter)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordStartLine = 1;
        var line = 1;
        var recordHasContent = false;

        void FlushField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void FlushRecord()
        {
            FlushField();
            if (recordHasContent || fields.Count > 1 || fields.Any(f => !string.IsNullOrWhiteSpace(f)))
            {
                records.Add(new CsvRecord(recordStartLine, [.. fields]));
            }
            fields.Clear();
            recordHasContent = false;
        }

        var i = 0;
        // 跳过表头前的空行
        while (i < text.Length && (text[i] == '\n' || text[i] == '\r' || text[i] == ' ' || text[i] == '\t'))
        {
            if (text[i] == '\n') { line++; recordStartLine = line; }
            i++;
        }

        var fieldQuoted = false;
        var fieldStarted = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            if (!fieldStarted && c == '"')
            {
                inQuotes = true;
                fieldStarted = true;
                fieldQuoted = true;
                recordHasContent = true;
                continue;
            }
            if (c == delimiter)
            {
                FlushField();
                fieldStarted = false;
                fieldQuoted = false;
                recordHasContent = true;
                continue;
            }
            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                FlushRecord();
                line++;
                recordStartLine = line;
                continue;
            }
            fieldStarted = true;
            if (c != ' ' && c != '\t' || fieldQuoted || field.Length > 0)
            {
                recordHasContent = recordHasContent || !char.IsWhiteSpace(c);
            }
            field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0 || recordHasContent)
        {
            FlushRecord();
        }
        return records;
    }

    private static (int AppId, int SteamId, int Start, int End, int DurationSeconds, int DurationMinutes, int Note, int Tags)
        ResolveColumns(List<string> headerFields)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headerFields.Count; i++)
        {
            var normalized = NormalizeHeader(headerFields[i]);
            if (normalized.Length > 0 && !map.ContainsKey(normalized)) map[normalized] = i;
        }

        int Index(params string[] aliases)
        {
            foreach (var alias in aliases)
                if (map.TryGetValue(alias, out var index)) return index;
            return -1;
        }

        return (
            Index("appid", "app", "gameid"),
            Index("steamid", "steamid64", "account", "user"),
            Index("start", "starttime", "startedat", "begin", "from"),
            Index("end", "endtime", "endedat", "to", "stop"),
            Index("duration", "durationseconds", "seconds", "durationsec"),
            Index("durationminutes", "minutes", "durationmin"),
            Index("note", "notes", "comment"),
            Index("tags", "tag", "labels"));
    }

    private static string NormalizeHeader(string field)
    {
        var builder = new StringBuilder(field.Length);
        foreach (var c in field.Trim())
        {
            if (c is ' ' or '_' or '-') continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static bool TryParseTime(string value, TimeZoneInfo tz, out long unix)
    {
        value = value.Trim();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            unix = raw > 100_000_000_000L ? raw / 1000 : raw;
            return true;
        }

        if (ExplicitOffsetRegex().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
        {
            unix = offset.ToUnixTimeSeconds();
            return true;
        }

        if (DateTime.TryParseExact(value, ExactLocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out exact))
        {
            unix = new DateTimeOffset(DateTime.SpecifyKind(exact, DateTimeKind.Unspecified), tz.GetUtcOffset(exact))
                .ToUnixTimeSeconds();
            return true;
        }

        unix = 0;
        return false;
    }

    [GeneratedRegex(@"^\d{17}$")]
    private static partial Regex SteamId64Regex();

    [GeneratedRegex(@"(?:[zZ]|[+-]\d{2}:?\d{2})$")]
    private static partial Regex ExplicitOffsetRegex();
}
