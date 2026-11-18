namespace SampleDataViewer;

internal sealed record SeriesDefinition(
    string PointName,
    string Label,
    string Unit,
    double? LowScale,
    double? HighScale,
    bool IsTarget);

internal sealed class TimeSeriesTable
{
    public required string SourceFile { get; init; }

    public required List<DateTime> Timestamps { get; init; }

    public required Dictionary<string, List<double?>> Values { get; init; }

    public required Dictionary<string, SeriesDefinition> Definitions { get; init; }

    public DateTime FirstTimestamp => Timestamps[0];

    public DateTime LastTimestamp => Timestamps[^1];

    public IReadOnlyList<SeriesDefinition> InputSeries =>
        Definitions.Values
            .Where(definition => !definition.IsTarget)
            .OrderBy(definition => definition.Label, StringComparer.CurrentCulture)
            .ToList();

    public IReadOnlyList<SeriesDefinition> TargetSeries =>
        Definitions.Values
            .Where(definition => definition.IsTarget)
            .OrderBy(definition => definition.Label, StringComparer.CurrentCulture)
            .ToList();
}
