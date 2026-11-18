namespace Fengda.Boiler.WebApi.Models;

/// <summary>燃烧诊断全天预测图接口返回结果。</summary>
public sealed class DailyCombustionForecastDto
{
    /// <summary>查询日期。</summary>
    public DateTime Date { get; init; }

    /// <summary>选择的预测提前量，范围为 1 至 60 秒。</summary>
    public int HorizonSeconds { get; init; }

    /// <summary>当天按分钟排列的实测与模型预测采样点。</summary>
    public required IReadOnlyList<DailyCombustionForecastSampleDto> Samples { get; init; }

    /// <summary>当前返回的有效分钟预测数量。</summary>
    public int CachedMinuteCount { get; init; }
}

/// <summary>单个分钟预测采样点。</summary>
public sealed class DailyCombustionForecastSampleDto
{
    /// <summary>60 秒输入窗口的起始时间。</summary>
    public DateTime AnchorTime { get; init; }

    /// <summary>对应实测与预测值的目标时间。</summary>
    public DateTime TargetTime { get; init; }

    /// <summary>各目标点位的实测值。</summary>
    public required IReadOnlyDictionary<string, double?> ActualValues { get; init; }

    /// <summary>各目标点位的模型预测值。</summary>
    public required IReadOnlyDictionary<string, double?> PredictedValues { get; init; }
}

/// <summary>近五日单位产汽耗气量接口返回结果。</summary>
public sealed class DailyGasConsumptionTrendDto
{
    /// <summary>统计截止日期。</summary>
    public DateTime Date { get; init; }

    /// <summary>由早至晚排列的五个自然日统计值。</summary>
    public required IReadOnlyList<DailyGasConsumptionDto> Days { get; init; }
}

/// <summary>单日单位产汽耗气量统计值。</summary>
public sealed class DailyGasConsumptionDto
{
    /// <summary>统计日期。</summary>
    public DateTime Date { get; init; }

    /// <summary>单位产汽耗气量，单位为 Nm³/t；无法计算时为 0。</summary>
    public double ConsumptionPerTon { get; init; }
}
