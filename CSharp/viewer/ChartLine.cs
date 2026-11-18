using Fengda.Cnn.Prediction;

namespace SampleDataViewer;

internal sealed record ChartLine(
    SeriesDefinition Definition,
    IReadOnlyList<TimeValue> Points,
    System.Drawing.Color? ColorOverride = null,
    float LineWidth = 2f);

internal static class TimeSeriesTableChartExtensions
{
    public static IReadOnlyList<ChartLine> GetWindowLines(
        this TimeSeriesTable table,
        IEnumerable<string> pointNames,
        DateTime start,
        TimeSpan duration)
    {
        var end = start.Add(duration);
        var lines = new List<ChartLine>();

        foreach (var pointName in pointNames)
        {
            if (!table.Definitions.TryGetValue(pointName, out var definition))
            {
                continue;
            }

            if (!table.Values.TryGetValue(pointName, out var values))
            {
                continue;
            }

            var points = new List<TimeValue>();
            for (var index = 0; index < table.Timestamps.Count; index++)
            {
                var timestamp = table.Timestamps[index];
                if (timestamp < start)
                {
                    continue;
                }

                if (timestamp >= end)
                {
                    break;
                }

                var value = values[index];
                if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                {
                    points.Add(new TimeValue(timestamp, value.Value));
                }
            }

            lines.Add(new ChartLine(definition, points));
        }

        return lines;
    }
}
