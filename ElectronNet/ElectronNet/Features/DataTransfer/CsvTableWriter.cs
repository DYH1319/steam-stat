using System.Globalization;
using System.Text;

namespace ElectronNet.Features.DataTransfer;

/// <summary>
/// RFC 4180 CSV：UTF-8 带 BOM（Excel 直接打开不乱码）、CRLF 换行、必要时加引号。
/// 文本列以 = + - @ 或制表 / 回车开头时前置单引号，防止在表格软件里被当成公式执行
/// （好友昵称、备注等内容来自外部，不可信）。数值列不做处理。
/// 时间列写本地时间「yyyy-MM-dd HH:mm:ss」，与会话 CSV 导入器的本地时间格式一致，可以再导入。
/// </summary>
internal static class CsvTableWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    internal static async Task<ExportTableResult> WriteAsync(
        Stream output,
        ExportTable table,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(output, Utf8WithBom, bufferSize: 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(string.Join(',', table.Columns.Select(column => Escape(column.Key)))).ConfigureAwait(false);

        var rows = 0;
        var line = new StringBuilder(256);
        await foreach (var row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            line.Clear();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0) line.Append(',');
                line.Append(FormatCell(i < row.Length ? row[i] : null, table.Columns[i].Kind, timeZone));
            }
            await writer.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            rows++;
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ExportTableResult(table.Id, rows, false);
    }

    internal static string FormatCell(object? value, ExportColumnKind kind, TimeZoneInfo timeZone)
    {
        if (value == null) return string.Empty;
        switch (kind)
        {
            case ExportColumnKind.Integer:
                return ExportValueFormatter.AsLong(value)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            case ExportColumnKind.Real:
                return ExportValueFormatter.AsDouble(value)?.ToString("0.############", CultureInfo.InvariantCulture) ?? string.Empty;
            case ExportColumnKind.Boolean:
                return value is true ? "true" : "false";
            case ExportColumnKind.Time:
                var seconds = ExportValueFormatter.AsLong(value);
                return seconds is > 0
                    ? ExportValueFormatter.ToLocal(seconds.Value, timeZone).ToString(ExportValueFormatter.TimeFormat, CultureInfo.InvariantCulture)
                    : string.Empty;
            default:
                return Escape(NeutralizeFormula(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }
    }

    internal static string NeutralizeFormula(string text)
        => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    internal static string Escape(string text)
    {
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
