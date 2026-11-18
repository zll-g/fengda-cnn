using Fengda.Boiler.Monitoring;
using Fengda.EarlyWarning;

namespace Fengda.Boiler.WebApi.Models;

/// <summary>模型预警筛选项。</summary>
public sealed class EarlyWarningOptionsDto
{
    /// <summary>默认查询月份，格式 yyyyMM。</summary>
    public required string CurrentMonth { get; init; }

    /// <summary>可查询月份。</summary>
    public required IReadOnlyList<string> Months { get; init; }

    /// <summary>可查询预警场景。</summary>
    public required IReadOnlyList<string> Scenarios { get; init; }

    /// <summary>可查询预警等级。</summary>
    public required IReadOnlyList<string> Levels { get; init; }
}

/// <summary>模型预警分页查询响应。</summary>
public sealed class EarlyWarningPageResponseDto
{
    /// <summary>查询月份。</summary>
    public required string Month { get; init; }

    /// <summary>查询场景。</summary>
    public required string Scenario { get; init; }

    /// <summary>查询等级。</summary>
    public required string Level { get; init; }

    /// <summary>当前页码。</summary>
    public int Page { get; init; }

    /// <summary>每页记录数。</summary>
    public int PageSize { get; init; }

    /// <summary>记录总数。</summary>
    public int Total { get; init; }

    /// <summary>当前页记录。</summary>
    public required IReadOnlyList<EarlyWarningRecord> Items { get; init; }
}

/// <summary>预警详情趋势图中的一个测点定义。</summary>
public sealed class EarlyWarningTrendPointDto
{
    /// <summary>测点字段标识。</summary>
    public required string Field { get; init; }

    /// <summary>测点中文名称。</summary>
    public required string Name { get; init; }

    /// <summary>测点单位。</summary>
    public required string Unit { get; init; }

    /// <summary>是否为触发预警的目标监测值。</summary>
    public bool IsTarget { get; init; }
}

/// <summary>预警详情趋势图中的一个秒级采样点。</summary>
public sealed class EarlyWarningTrendSampleDto
{
    /// <summary>采样时间，精确到秒。</summary>
    public DateTime RecordedAt { get; init; }

    /// <summary>以测点字段标识为键的原始监测值。</summary>
    public required IReadOnlyDictionary<string, double?> Values { get; init; }
}

/// <summary>预警详情中关联测点的模型重构结果。</summary>
public sealed class EarlyWarningTrendContributorDto
{
    /// <summary>测点字段标识。</summary>
    public required string Field { get; init; }

    /// <summary>测点中文名称。</summary>
    public required string Name { get; init; }

    /// <summary>测点单位。</summary>
    public required string Unit { get; init; }

    /// <summary>告警时刻实际值。</summary>
    public double Actual { get; init; }

    /// <summary>模型重构的正常值。</summary>
    public double Reconstructed { get; init; }

    /// <summary>相对场景评分的贡献度，取值范围为 0 至 1。</summary>
    public double Contribution { get; init; }

    /// <summary>实际值相对重构正常值的中文方向说明。</summary>
    public required string Direction { get; init; }
}

/// <summary>模型预警详情的两分钟前趋势数据。</summary>
public sealed class EarlyWarningTrendResponseDto
{
    /// <summary>预警记录主键。</summary>
    public required string RecordId { get; init; }

    /// <summary>趋势起始时间，包含该时刻。</summary>
    public DateTime StartTime { get; init; }

    /// <summary>预警时间和趋势结束时间，包含该时刻。</summary>
    public DateTime AlarmTime { get; init; }

    /// <summary>趋势读取结果说明；空字符串表示完整读取。</summary>
    public required string Message { get; init; }

    /// <summary>趋势图测点定义，目标监测值排在首位。</summary>
    public required IReadOnlyList<EarlyWarningTrendPointDto> Points { get; init; }

    /// <summary>告警窗口回放得到的关联测点实际值、重构值和偏差方向。</summary>
    public required IReadOnlyList<EarlyWarningTrendContributorDto> Contributors { get; init; }

    /// <summary>按时间升序排列的秒级原始监测值。</summary>
    public required IReadOnlyList<EarlyWarningTrendSampleDto> Samples { get; init; }
}
