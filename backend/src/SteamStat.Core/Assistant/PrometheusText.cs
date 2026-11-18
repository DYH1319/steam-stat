using System.Globalization;
using System.Text;

namespace SteamStat.Core.Assistant;

public sealed record PrometheusSample(IReadOnlyList<KeyValuePair<string, string>> Labels, double Value);

public sealed record PrometheusMetric(string Name, string Type, string Help, IReadOnlyList<PrometheusSample> Samples);

/// <summary>Prometheus 文本格式 0.0.4（/metrics）。标签值按规范转义，指标名由调用方保证合法。</summary>
public static class PrometheusText
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Write(IEnumerable<PrometheusMetric> metrics)
    {
        var builder = new StringBuilder();
        foreach (var metric in metrics)
        {
            builder.Append("# HELP ").Append(metric.Name).Append(' ').Append(EscapeHelp(metric.Help)).Append('\n');
            builder.Append("# TYPE ").Append(metric.Name).Append(' ').Append(metric.Type).Append('\n');
            foreach (var sample in metric.Samples)
            {
                builder.Append(metric.Name);
                if (sample.Labels.Count > 0)
                {
                    builder.Append('{');
                    for (var index = 0; index < sample.Labels.Count; index++)
                    {
                        if (index > 0) builder.Append(',');
                        builder.Append(sample.Labels[index].Key).Append("=\"").Append(EscapeLabel(sample.Labels[index].Value)).Append('"');
                    }
                    builder.Append('}');
                }
                builder.Append(' ').Append(Format(sample.Value)).Append('\n');
            }
        }
        return builder.ToString();
    }

    private static string Format(double value)
        => double.IsNaN(value) ? "NaN"
            : double.IsPositiveInfinity(value) ? "+Inf"
            : double.IsNegativeInfinity(value) ? "-Inf"
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static string EscapeHelp(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n");

    private static string EscapeLabel(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
