using Fengda.Boiler.Monitoring;
using Fengda.Cnn.Prediction;

namespace Fengda.Boiler.WebApi.Models;

/// <summary>
/// 预测接口返回的数据质量信息。
/// </summary>
public sealed class PredictionDataQualityDto
{
    /// <summary>输入数据是否完整并可以预测。</summary>
    public bool IsValid { get; init; }

    /// <summary>用于预测的输入记录数量。</summary>
    public int InputCount { get; init; }

    /// <summary>输入窗口最后一条快照时间。</summary>
    public DateTime LatestInputTime { get; init; }

    /// <summary>数据质量说明。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 预测接口完整返回数据。
/// </summary>
public sealed class PredictionResponseDto
{
    /// <summary>预测输入窗口开始时间。</summary>
    public DateTime InputStart { get; init; }

    /// <summary>预测输入窗口结束时间，包含该秒。</summary>
    public DateTime InputEnd { get; init; }

    /// <summary>未来预测开始时间。</summary>
    public DateTime ForecastStart { get; init; }

    /// <summary>未来预测结束时间，包含该秒。</summary>
    public DateTime ForecastEnd { get; init; }

    /// <summary>用于预测的最近 60 秒完整锅炉快照。</summary>
    public required IReadOnlyList<BoilerSnapshot> InputData { get; init; }

    /// <summary>未来 60 秒预测结果。</summary>
    public required ForecastResult Forecast { get; init; }

    /// <summary>基于预测结果生成的 FGR 建议。</summary>
    public required FgrRecommendation FgrRecommendation { get; init; }

    /// <summary>指定时间之后 60 秒的实际锅炉快照，仅历史预测接口返回。</summary>
    public IReadOnlyList<BoilerSnapshot>? ActualFutureData { get; init; }

    /// <summary>本次预测输入数据质量。</summary>
    public required PredictionDataQualityDto DataQuality { get; init; }
}

/// <summary>
/// 预测服务配置。
/// </summary>
public sealed class PredictionOptions
{
    /// <summary>ONNX 模型目录。</summary>
    public string ModelDirectory { get; init; } = string.Empty;

    /// <summary>实时预测允许的最大快照陈旧秒数。</summary>
    public int MaxSnapshotAgeSeconds { get; init; } = 5;
}
