using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace SampleDataViewer;

internal static class HistorianCsvLoader
{
    public static TimeSeriesTable Load(string path)
    {
        var rows = ReadCsvRows(path);
        var pointMeta = ReadPointMetadata(rows);
        var dataHeaderIndex = rows.FindIndex(row =>
            row.Count > 0 && row[0].Trim().Trim('\ufeff') == "Date Time");

        if (dataHeaderIndex < 0)
        {
            throw new InvalidOperationException("未找到 Date Time 数据表头，无法识别原始 historian CSV。");
        }

        var rawHeaders = rows[dataHeaderIndex];
        var pointNamesByColumn = rawHeaders
            .Skip(1)
            .Select(SeriesRegistry.CanonicalPointName)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        var pointNames = pointNamesByColumn
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var definitions = new Dictionary<string, SeriesDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var pointName in pointNames)
        {
            pointMeta.TryGetValue(pointName, out var metadata);
            definitions[pointName] = SeriesRegistry.CreateDefinition(
                pointName,
                metadata.Description,
                metadata.Unit,
                metadata.LowScale,
                metadata.HighScale);
        }

        var timestamps = new List<DateTime>();
        var values = pointNames.ToDictionary(
            pointName => pointName,
            _ => new List<double?>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(dataHeaderIndex + 1))
        {
            if (row.Count == 0 || !TryParseTimestamp(row[0], out var timestamp))
            {
                continue;
            }

            timestamps.Add(timestamp);
            var rowValues = pointNames.ToDictionary(
                pointName => pointName,
                _ => (double?)null,
                StringComparer.OrdinalIgnoreCase);
            for (var columnIndex = 1; columnIndex < rawHeaders.Count; columnIndex++)
            {
                var pointIndex = columnIndex - 1;
                if (pointIndex >= pointNamesByColumn.Count)
                {
                    continue;
                }

                var pointName = pointNamesByColumn[pointIndex];
                var rawValue = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                var parsedValue = ParseDoubleOrNull(rawValue);
                if (!rowValues[pointName].HasValue && parsedValue.HasValue)
                {
                    rowValues[pointName] = parsedValue;
                }
            }

            foreach (var pointName in pointNames)
            {
                values[pointName].Add(rowValues[pointName]);
            }
        }

        var orderedIndexes = Enumerable.Range(0, timestamps.Count)
            .OrderBy(index => timestamps[index])
            .ToList();
        var sortedTimestamps = orderedIndexes
            .Select(index => timestamps[index])
            .ToList();
        var sortedValues = values.ToDictionary(
            item => item.Key,
            item => orderedIndexes.Select(index => item.Value[index]).ToList(),
            StringComparer.OrdinalIgnoreCase);

        return new TimeSeriesTable
        {
            SourceFile = Path.GetFileName(path),
            Timestamps = sortedTimestamps,
            Values = sortedValues,
            Definitions = definitions,
        };
    }

    private static List<List<string>> ReadCsvRows(string path)
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");

        var rows = new List<List<string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null)
            {
                continue;
            }

            rows.Add(fields.ToList());
        }

        return rows;
    }

    private static Dictionary<string, (string Description, string Unit, double? LowScale, double? HighScale)> ReadPointMetadata(
        List<List<string>> rows)
    {
        var result = new Dictionary<string, (string Description, string Unit, double? LowScale, double? HighScale)>(
            StringComparer.OrdinalIgnoreCase);
        var headerIndex = rows.FindIndex(row =>
            row.Count > 2 && row.Any(cell => cell.Trim() == "Point Name"));

        if (headerIndex < 0)
        {
            return result;
        }

        var header = rows[headerIndex];
        var pointNameIndex = header.FindIndex(cell => cell.Trim() == "Point Name");
        var descriptionIndex = header.FindIndex(cell => cell.Trim() == "Description");
        var unitIndex = header.FindIndex(cell => cell.Trim() == "Units");
        var lowScaleIndex = header.FindIndex(cell => cell.Trim() == "Low Scale");
        var highScaleIndex = header.FindIndex(cell => cell.Trim() == "High Scale");

        if (pointNameIndex < 0)
        {
            return result;
        }

        foreach (var row in rows.Skip(headerIndex + 1))
        {
            if (row.Count <= pointNameIndex)
            {
                continue;
            }

            var pointName = SeriesRegistry.CanonicalPointName(row[pointNameIndex]);
            if (string.IsNullOrWhiteSpace(pointName) || pointName == "Date Time")
            {
                break;
            }

            var description = descriptionIndex >= 0 && row.Count > descriptionIndex ? row[descriptionIndex] : string.Empty;
            var unit = unitIndex >= 0 && row.Count > unitIndex ? row[unitIndex] : string.Empty;
            var lowScale = lowScaleIndex >= 0 && row.Count > lowScaleIndex
                ? ParseDoubleOrNull(row[lowScaleIndex])
                : null;
            var highScale = highScaleIndex >= 0 && row.Count > highScaleIndex
                ? ParseDoubleOrNull(row[highScaleIndex])
                : null;
            result[pointName] = (description, unit, lowScale, highScale);
        }

        return result;
    }

    private static bool TryParseTimestamp(string value, out DateTime timestamp)
    {
        var text = value.Trim().Trim('"');
        string[] formats =
        [
            "M/d/yyyy H:mm:ss.FFF",
            "M/d/yyyy HH:mm:ss.FFF",
            "MM/dd/yyyy HH:mm:ss.FFF",
            "M/d/yyyy H:mm:ss",
            "M/d/yyyy HH:mm:ss",
            "MM/dd/yyyy HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/M/d H:mm:ss",
        ];

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
        {
            return true;
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out timestamp)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private static double? ParseDoubleOrNull(string value)
    {
        var text = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }
}
