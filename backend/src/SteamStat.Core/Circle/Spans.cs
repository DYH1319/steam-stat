using SteamStat.Core.Insights;

namespace SteamStat.Core.Circle;

/// <summary>半开区间 [Start, End)，Unix 秒。</summary>
public readonly record struct Span(long Start, long End)
{
    public long Seconds => Math.Max(0, End - Start);

    public Span? Intersect(Span other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return end > start ? new Span(start, end) : null;
    }
}

public static class Spans
{
    /// <summary>合并重叠和相接的区间，按开始时间排序。</summary>
    public static List<Span> Union(IEnumerable<Span> spans)
    {
        var output = new List<Span>();
        foreach (var span in spans.Where(span => span.End > span.Start).OrderBy(span => span.Start))
        {
            if (output.Count > 0 && span.Start <= output[^1].End)
                output[^1] = output[^1] with { End = Math.Max(output[^1].End, span.End) };
            else
                output.Add(span);
        }
        return output;
    }

    public static long Total(IEnumerable<Span> spans) => Union(spans).Sum(span => span.Seconds);

    /// <summary>source 减去 holes。两边都会先合并。</summary>
    public static List<Span> Subtract(IEnumerable<Span> source, IEnumerable<Span> holes)
    {
        var cut = Union(holes);
        var output = new List<Span>();
        foreach (var span in Union(source))
        {
            var cursor = span.Start;
            foreach (var hole in cut)
            {
                if (hole.End <= cursor) continue;
                if (hole.Start >= span.End) break;
                if (hole.Start > cursor) output.Add(new Span(cursor, hole.Start));
                cursor = Math.Max(cursor, hole.End);
                if (cursor >= span.End) break;
            }
            if (cursor < span.End) output.Add(new Span(cursor, span.End));
        }
        return output;
    }

    public static List<Span> Intersect(IEnumerable<Span> left, IEnumerable<Span> right)
    {
        var a = Union(left);
        var b = Union(right);
        var output = new List<Span>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i].Intersect(b[j]) is { } piece) output.Add(piece);
            if (a[i].End < b[j].End) i++;
            else j++;
        }
        return output;
    }
}

/// <summary>
/// 7 × 24 的周作息矩阵，下标 = 星期 × 24 + 小时。星期按「一周起始日」对齐，和渲染层 presenceHeatmap 一致；小时是本地时间，不按逻辑日偏移。
/// </summary>
public static class WeekHours
{
    public const int Cells = 7 * 24;

    public static int Index(long unixSeconds, InsightOptions options)
    {
        var local = options.Local(unixSeconds);
        var weekday = ((int)local.DayOfWeek - options.WeekStart + 7) % 7;
        return weekday * 24 + local.Hour;
    }

    /// <summary>把 [start, end) 按整点拆开累加到矩阵里。夏令时切换日按实际经过的秒数算。</summary>
    public static void Add(double[] matrix, long start, long end, InsightOptions options, double weight = 1)
    {
        var cursor = start;
        while (cursor < end)
        {
            var local = options.Local(cursor);
            var nextHour = local.AddMinutes(-local.Minute).AddSeconds(-local.Second).AddHours(1).ToUnixTimeSeconds();
            var stop = Math.Min(end, Math.Max(nextHour, cursor + 1));
            matrix[Index(cursor, options)] += (stop - cursor) * weight;
            cursor = stop;
        }
    }

    public static double[] From(IEnumerable<Span> spans, InsightOptions options, long from = long.MinValue, long to = long.MaxValue)
    {
        var matrix = new double[Cells];
        foreach (var span in spans)
        {
            var start = Math.Max(span.Start, from);
            var end = Math.Min(span.End, to);
            if (end > start) Add(matrix, start, end, options);
        }
        return matrix;
    }

    /// <summary>两个分布的余弦相似度，任一方全零时为空。</summary>
    public static double? Cosine(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        double dot = 0, a = 0, b = 0;
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            dot += left[index] * right[index];
            a += left[index] * left[index];
            b += right[index] * right[index];
        }
        return a <= 0 || b <= 0 ? null : dot / Math.Sqrt(a * b);
    }

    /// <summary>按小时平移，用于对齐两个时区：shift 为正时把内容往后挪。</summary>
    public static double[] Shift(IReadOnlyList<double> matrix, int shift)
    {
        var output = new double[Cells];
        for (var index = 0; index < Cells; index++)
            output[((index + shift) % Cells + Cells) % Cells] = matrix[index];
        return output;
    }
}
