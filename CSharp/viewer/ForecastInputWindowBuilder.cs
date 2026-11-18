using Fengda.Cnn.Prediction;

namespace SampleDataViewer;

internal static class ForecastInputWindowBuilder
{
    private const int ValueFallbackSeconds = 20;

    public static ForecastInputWindow Build(
        TimeSeriesTable table,
        DateTime inputStart,
        IEnumerable<string> requiredPointNames)
    {
        var pointNames = requiredPointNames
            .Select(SeriesRegistry.CanonicalPointName)
            .Where(pointName => !string.IsNullOrWhiteSpace(pointName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pointNames.Count == 0)
        {
            throw new InvalidOperationException("预测模型未提供输入点位清单。");
        }

        var inputEnd = inputStart.AddSeconds(OnnxForecastPredictor.RequiredInputRowCount - 1);
        var rows = new List<ForecastInputRow>(OnnxForecastPredictor.RequiredInputRowCount);
        for (var step = 0; step < OnnxForecastPredictor.RequiredInputRowCount; step++)
        {
            var timestamp = inputStart.AddSeconds(step);
            var values = new List<KeyValuePair<string, double>>(pointNames.Count);
            foreach (var pointName in pointNames)
            {
                values.Add(new KeyValuePair<string, double>(
                    pointName,
                    GetRequiredValueAtTime(table, pointName, timestamp, inputStart, inputEnd)));
            }

            rows.Add(new ForecastInputRow(timestamp, values));
        }

        return new ForecastInputWindow(rows);
    }

    public static double? GetCurrentValue(TimeSeriesTable table, string pointName, DateTime timestamp)
    {
        return TryGetValueAtTime(
            table,
            pointName,
            timestamp,
            minTimestamp: null,
            maxTimestamp: timestamp,
            out var value)
            ? value
            : null;
    }

    private static double GetRequiredValueAtTime(
        TimeSeriesTable table,
        string pointName,
        DateTime timestamp,
        DateTime minTimestamp,
        DateTime maxTimestamp)
    {
        if (TryGetValueAtTime(table, pointName, timestamp, minTimestamp, maxTimestamp, out var value))
        {
            return value;
        }

        pointName = SeriesRegistry.CanonicalPointName(pointName);
        if (!table.Values.ContainsKey(pointName))
        {
            throw new InvalidOperationException($"当前 CSV 缺少模型输入点：{pointName}");
        }

        throw new InvalidOperationException($"输入点 {pointName} 在 {timestamp:HH:mm:ss} 附近没有有效值。");
    }

    private static bool TryGetValueAtTime(
        TimeSeriesTable table,
        string pointName,
        DateTime timestamp,
        DateTime? minTimestamp,
        DateTime? maxTimestamp,
        out double value)
    {
        value = 0.0;
        pointName = SeriesRegistry.CanonicalPointName(pointName);
        if (!table.Values.TryGetValue(pointName, out var values) || table.Timestamps.Count == 0)
        {
            return false;
        }

        var index = FindNearestTimestampIndex(table.Timestamps, timestamp);
        if (index < 0)
        {
            return false;
        }

        if (TryGetFiniteValue(table, values, index, minTimestamp, maxTimestamp, out value))
        {
            return true;
        }

        for (var offset = 1; offset <= ValueFallbackSeconds; offset++)
        {
            if (TryGetFiniteValue(table, values, index - offset, minTimestamp, maxTimestamp, out value)
                || TryGetFiniteValue(table, values, index + offset, minTimestamp, maxTimestamp, out value))
            {
                return true;
            }
        }

        value = 0.0;
        return false;
    }

    private static int FindNearestTimestampIndex(IReadOnlyList<DateTime> timestamps, DateTime target)
    {
        var left = 0;
        var right = timestamps.Count - 1;
        while (left <= right)
        {
            var mid = left + ((right - left) / 2);
            var comparison = timestamps[mid].CompareTo(target);
            if (comparison == 0)
            {
                return mid;
            }

            if (comparison < 0)
            {
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        var candidates = new[] { right, left }
            .Where(index => index >= 0 && index < timestamps.Count)
            .ToList();
        if (candidates.Count == 0)
        {
            return -1;
        }

        var nearest = candidates
            .OrderBy(index => Math.Abs((timestamps[index] - target).TotalMilliseconds))
            .First();
        return Math.Abs((timestamps[nearest] - target).TotalMilliseconds) <= 750 ? nearest : -1;
    }

    private static bool TryGetFiniteValue(
        TimeSeriesTable table,
        IReadOnlyList<double?> values,
        int index,
        DateTime? minTimestamp,
        DateTime? maxTimestamp,
        out double value)
    {
        value = 0.0;
        if (index < 0 || index >= values.Count || !values[index].HasValue)
        {
            return false;
        }

        var timestamp = table.Timestamps[index];
        if ((minTimestamp.HasValue && timestamp < minTimestamp.Value)
            || (maxTimestamp.HasValue && timestamp > maxTimestamp.Value))
        {
            return false;
        }

        value = values[index]!.Value;
        return double.IsFinite(value);
    }
}
