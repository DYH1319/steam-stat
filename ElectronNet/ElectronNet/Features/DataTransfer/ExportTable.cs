namespace ElectronNet.Features.DataTransfer;

/// <summary>表格导出的列类型：决定 CSV 的文本格式与 xlsx 的单元格类型。</summary>
internal enum ExportColumnKind
{
    Text,
    Integer,
    Real,
    /// <summary>Unix 秒（long）。CSV 写本地时间「yyyy-MM-dd HH:mm:ss」，xlsx 写带日期格式的序列值。</summary>
    Time,
    Boolean
}

internal sealed record ExportColumn(string Key, ExportColumnKind Kind);

/// <summary>一张待导出的表：列定义 + 按列顺序排列的行。</summary>
internal sealed record ExportTable(string Id, IReadOnlyList<ExportColumn> Columns, IAsyncEnumerable<object?[]> Rows);

/// <summary>写入完成后每张表的行数，以及是否因格式上限被截断（xlsx 单表最多 1,048,575 行数据）。</summary>
internal sealed record ExportTableResult(string Id, int Rows, bool Truncated);

internal static class ExportValueFormatter
{
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    internal static DateTime ToLocal(long unixSeconds, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, timeZone);

    internal static long? AsLong(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        uint u => u,
        ulong ul => (long)ul,
        double d => (long)d,
        _ => null
    };

    internal static double? AsDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        _ => AsLong(value)
    };
}
