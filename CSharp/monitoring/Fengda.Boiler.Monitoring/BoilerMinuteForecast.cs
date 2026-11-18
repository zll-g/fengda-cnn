using LiteDB;

namespace Fengda.Boiler.Monitoring;

/// <summary>
/// 按分钟缓存的一次锅炉排放预测结果。
/// </summary>
/// <remarks>
/// 仅保存模型输出曲线，不保存用于推理的 60 秒完整快照，避免重复占用秒级历史数据空间。
/// </remarks>
public sealed class BoilerMinuteForecast
{
    /// <summary>分钟级唯一主键，格式为 yyyyMMddHHmm。</summary>
    [BsonId]
    public long Id { get; set; }

    /// <summary>60 秒模型输入窗口的开始时间，精确到分钟。</summary>
    public DateTime AnchorTime { get; set; }

    /// <summary>模型预测窗口的起始时间。</summary>
    public DateTime ForecastStartTime { get; set; }

    /// <summary>缓存写入时间。</summary>
    public DateTime CachedAt { get; set; }

    /// <summary>每个模型目标点位的 60 秒预测数值。</summary>
    public List<BoilerMinuteForecastSeries> Series { get; set; } = [];
}

/// <summary>
/// 单个模型目标点位的分钟预测曲线。
/// </summary>
public sealed class BoilerMinuteForecastSeries
{
    /// <summary>现场点位名称。</summary>
    public string PointName { get; set; } = string.Empty;

    /// <summary>从预测窗口起始时刻开始、按秒排列的预测数值。</summary>
    public List<double> Values { get; set; } = [];
}
