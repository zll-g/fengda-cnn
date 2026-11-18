namespace Fengda.Cnn.Prediction;

/// <summary>
/// 带时间戳的预测数值。
/// </summary>
/// <param name="Timestamp">数值对应的时间。</param>
/// <param name="Value">预测数值。</param>
public sealed record TimeValue(DateTime Timestamp, double Value);

/// <summary>
/// 单个目标点位的未来预测曲线。
/// </summary>
/// <param name="TargetColumn">模型目标字段名。</param>
/// <param name="PointName">现场点位名。</param>
/// <param name="Points">按时间升序排列的预测点。</param>
public sealed record ForecastSeries(
    string TargetColumn,
    string PointName,
    IReadOnlyList<TimeValue> Points);

/// <summary>
/// 一次未来预测的完整结果。
/// </summary>
/// <param name="StartTime">预测开始时间。</param>
/// <param name="EndExclusive">预测结束时间，不包含该时间。</param>
/// <param name="Series">各目标点位的预测曲线。</param>
public sealed record ForecastResult(
    DateTime StartTime,
    DateTime EndExclusive,
    IReadOnlyList<ForecastSeries> Series)
{
    /// <summary>
    /// 按现场点位名查找预测曲线。
    /// </summary>
    public ForecastSeries? FindSeries(string pointName)
    {
        pointName = PointNameNormalizer.CanonicalPointName(pointName);
        return Series.FirstOrDefault(
            item => string.Equals(item.PointName, pointName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 预测输入窗口中的一秒数据。
/// </summary>
public sealed class ForecastInputRow
{
    private readonly Dictionary<string, double> valuesByPointName;

    /// <summary>
    /// 创建一秒预测输入数据。
    /// </summary>
    /// <param name="timestamp">输入数据时间。</param>
    /// <param name="valuesByPointName">按现场点位名组织的数值。</param>
    public ForecastInputRow(DateTime timestamp, IEnumerable<KeyValuePair<string, double>> valuesByPointName)
    {
        Timestamp = timestamp;
        this.valuesByPointName = valuesByPointName
            .ToDictionary(
                item => PointNameNormalizer.CanonicalPointName(item.Key),
                item => item.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>输入数据时间。</summary>
    public DateTime Timestamp { get; }

    /// <summary>按现场点位名组织的数值。</summary>
    public IReadOnlyDictionary<string, double> ValuesByPointName => valuesByPointName;

    /// <summary>尝试获取指定现场点位数值。</summary>
    public bool TryGetValue(string pointName, out double value)
    {
        return valuesByPointName.TryGetValue(PointNameNormalizer.CanonicalPointName(pointName), out value);
    }
}

/// <summary>
/// 供模型使用的连续预测输入窗口。
/// </summary>
public sealed class ForecastInputWindow
{
    /// <summary>
    /// 创建预测输入窗口。
    /// </summary>
    public ForecastInputWindow(IEnumerable<ForecastInputRow> rows)
    {
        Rows = rows.ToList();
        if (Rows.Count == 0)
        {
            throw new ArgumentException("Forecast input window cannot be empty.", nameof(rows));
        }
    }

    /// <summary>按时间升序排列的输入行。</summary>
    public IReadOnlyList<ForecastInputRow> Rows { get; }

    /// <summary>输入窗口开始时间。</summary>
    public DateTime StartTime => Rows[0].Timestamp;

    /// <summary>输入窗口结束时间，不包含该时间。</summary>
    public DateTime EndExclusive => Rows[^1].Timestamp.AddSeconds(1);

    /// <summary>尝试获取指定输入行和现场点位的数值。</summary>
    public bool TryGetValue(int rowIndex, string pointName, out double value)
    {
        value = 0.0;
        return rowIndex >= 0
            && rowIndex < Rows.Count
            && Rows[rowIndex].TryGetValue(pointName, out value);
    }
}

internal static class PointNameNormalizer
{
    public static string CanonicalPointName(string value)
    {
        var text = value.Trim().Trim('"');
        if (text.StartsWith("(A) ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        var lineBreakIndex = text.IndexOfAny(['\r', '\n']);
        if (lineBreakIndex >= 0)
        {
            text = text[..lineBreakIndex];
        }

        return text.Trim();
    }
}
