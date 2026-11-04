using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ElectronNet.Features.DataTransfer;

/// <summary>
/// 最小的 Office Open XML（.xlsx）写入器：每张表一个工作表，首行加粗并冻结，
/// 文本用 inlineStr（不会被当成公式），时间写成带「yyyy-mm-dd hh:mm:ss」格式的序列值。
/// 逐行流式写入 zip，不在内存中保留整张表。
/// </summary>
internal static class XlsxWorkbookWriter
{
    /// <summary>Excel 单表 1,048,576 行，扣掉表头。</summary>
    internal const int MaxDataRows = 1_048_575;
    private const int MaxCellText = 32_767;

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    // cellXfs 下标
    private const int StyleHeader = 1;
    private const int StyleTime = 2;

    internal static async Task<IReadOnlyList<ExportTableResult>> WriteAsync(
        Stream output,
        IReadOnlyList<ExportTable> tables,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        if (tables.Count == 0) throw new ArgumentException("At least one table is required.", nameof(tables));

        var sheetNames = UniqueSheetNames(tables.Select(table => table.Id));
        var results = new List<ExportTableResult>(tables.Count);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        await WriteEntryAsync(zip, "[Content_Types].xml", ContentTypes(tables.Count)).ConfigureAwait(false);
        await WriteEntryAsync(zip, "_rels/.rels",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{PackageRelNs}"><Relationship Id="rId1" Type="{RelNs}/officeDocument" Target="xl/workbook.xml"/></Relationships>""").ConfigureAwait(false);
        await WriteEntryAsync(zip, "xl/workbook.xml", Workbook(sheetNames)).ConfigureAwait(false);
        await WriteEntryAsync(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(tables.Count)).ConfigureAwait(false);
        await WriteEntryAsync(zip, "xl/styles.xml", Styles).ConfigureAwait(false);

        for (var index = 0; index < tables.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry($"xl/worksheets/sheet{index + 1}.xml", CompressionLevel.Optimal);
            await using var stream = entry.Open();
            results.Add(await WriteSheetAsync(stream, tables[index], timeZone, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private static async Task<ExportTableResult> WriteSheetAsync(Stream stream, ExportTable table, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
        var columns = table.Columns;
        var builder = new StringBuilder(1024);
        builder.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{MainNs}">""");
        builder.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        builder.Append("<cols>");
        for (var i = 0; i < columns.Count; i++)
        {
            var width = columns[i].Kind switch
            {
                ExportColumnKind.Time => 20,
                ExportColumnKind.Text => Math.Clamp(columns[i].Key.Length + 4, 14, 40),
                _ => Math.Clamp(columns[i].Key.Length + 3, 10, 24)
            };
            builder.Append(CultureInfo.InvariantCulture, $"""<col min="{i + 1}" max="{i + 1}" width="{width}" customWidth="1"/>""");
        }
        builder.Append("</cols><sheetData>");

        builder.Append("""<row r="1">""");
        for (var i = 0; i < columns.Count; i++) AppendInlineString(builder, CellRef(i, 1), columns[i].Key, StyleHeader);
        builder.Append("</row>");
        await writer.WriteAsync(builder, cancellationToken).ConfigureAwait(false);

        var rows = 0;
        var truncated = false;
        await foreach (var row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (rows >= MaxDataRows)
            {
                truncated = true;
                break;
            }
            var rowNumber = rows + 2;
            builder.Clear();
            builder.Append(CultureInfo.InvariantCulture, $"""<row r="{rowNumber}">""");
            for (var i = 0; i < columns.Count; i++)
            {
                AppendCell(builder, CellRef(i, rowNumber), i < row.Length ? row[i] : null, columns[i].Kind, timeZone);
            }
            builder.Append("</row>");
            await writer.WriteAsync(builder, cancellationToken).ConfigureAwait(false);
            rows++;
        }

        await writer.WriteAsync("</sheetData></worksheet>").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ExportTableResult(table.Id, rows, truncated);
    }

    private static void AppendCell(StringBuilder builder, string reference, object? value, ExportColumnKind kind, TimeZoneInfo timeZone)
    {
        if (value == null) return;
        switch (kind)
        {
            case ExportColumnKind.Integer:
                if (ExportValueFormatter.AsLong(value) is { } integer)
                    builder.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}"><v>{integer}</v></c>""");
                return;
            case ExportColumnKind.Real:
                if (ExportValueFormatter.AsDouble(value) is { } real && double.IsFinite(real))
                    builder.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}"><v>{real.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                return;
            case ExportColumnKind.Boolean:
                builder.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" t="b"><v>{(value is true ? 1 : 0)}</v></c>""");
                return;
            case ExportColumnKind.Time:
                if (ExportValueFormatter.AsLong(value) is > 0 and var seconds)
                {
                    var serial = ExportValueFormatter.ToLocal(seconds, timeZone).ToOADate();
                    builder.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{StyleTime}"><v>{serial.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                }
                return;
            default:
                AppendInlineString(builder, reference, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, null);
                return;
        }
    }

    private static void AppendInlineString(StringBuilder builder, string reference, string text, int? style)
    {
        if (text.Length > MaxCellText) text = text[..MaxCellText];
        builder.Append("<c r=\"").Append(reference).Append('"');
        if (style != null) builder.Append(CultureInfo.InvariantCulture, $" s=\"{style}\"");
        builder.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
            .Append(EscapeXml(text))
            .Append("</t></is></c>");
    }

    /// <summary>XML 转义并去掉 XML 1.0 不允许的控制字符与孤立代理项。</summary>
    internal static string EscapeXml(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            if (c < 0x20 && c is not ('\t' or '\n' or '\r')) continue;
            if (c is '\uFFFE' or '\uFFFF') continue;
            switch (c)
            {
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '&': builder.Append("&amp;"); break;
                case '"': builder.Append("&quot;"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    internal static string CellRef(int columnIndex, int rowNumber)
    {
        var name = new StringBuilder();
        var n = columnIndex + 1;
        while (n > 0)
        {
            var remainder = (n - 1) % 26;
            name.Insert(0, (char)('A' + remainder));
            n = (n - 1) / 26;
        }
        return name.Append(rowNumber.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    /// <summary>工作表名最多 31 个字符，不能含 []:*?/\，且不能重复（不区分大小写）。</summary>
    internal static IReadOnlyList<string> UniqueSheetNames(IEnumerable<string> ids)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var id in ids)
        {
            var baseName = new string(id.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim('\'');
            if (baseName.Length == 0) baseName = "Sheet";
            if (baseName.Length > 31) baseName = baseName[..31];
            var name = baseName;
            for (var suffix = 2; !used.Add(name); suffix++)
            {
                var tail = $" ({suffix})";
                name = (baseName.Length + tail.Length > 31 ? baseName[..(31 - tail.Length)] : baseName) + tail;
            }
            names.Add(name);
        }
        return names;
    }

    private static string ContentTypes(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        builder.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>""");
        builder.Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        builder.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }
        return builder.Append("</Types>").ToString();
    }

    private static string Workbook(IReadOnlyList<string> sheetNames)
    {
        var builder = new StringBuilder();
        builder.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{MainNs}" xmlns:r="{RelNs}"><sheets>""");
        for (var i = 0; i < sheetNames.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"""<sheet name="{SecurityElement.Escape(sheetNames[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        }
        return builder.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRels(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{PackageRelNs}">""");
        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"""<Relationship Id="rId{i}" Type="{RelNs}/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        }
        builder.Append(CultureInfo.InvariantCulture,
            $"""<Relationship Id="rId{sheetCount + 1}" Type="{RelNs}/styles" Target="styles.xml"/>""");
        return builder.Append("</Relationships>").ToString();
    }

    private const string Styles =
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="{MainNs}">"""
        + """<numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts>"""
        + """<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>"""
        + """<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>"""
        + """<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"""
        + """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>"""
        + """<cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>"""
        + """<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>"""
        + """<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>"""
        + """<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>"""
        + "</styleSheet>";

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(content)).ConfigureAwait(false);
    }
}
