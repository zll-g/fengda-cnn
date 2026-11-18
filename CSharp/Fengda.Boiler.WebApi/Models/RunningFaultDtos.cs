using Fengda.Boiler.Monitoring;

namespace Fengda.Boiler.WebApi.Models;

/// <summary>
/// 运行故障分页查询返回数据。
/// </summary>
public sealed class RunningFaultPageResponseDto
{
    /// <summary>查询月份，格式 yyyyMM。</summary>
    public required string Month { get; init; }

    /// <summary>查询指标类型，all 表示全部指标。</summary>
    public required string MetricType { get; init; }

    /// <summary>当前页码，从 1 开始。</summary>
    public int Page { get; init; }

    /// <summary>每页记录数。</summary>
    public int PageSize { get; init; }

    /// <summary>满足查询条件的总记录数。</summary>
    public int Total { get; init; }

    /// <summary>当前页运行故障记录。</summary>
    public required IReadOnlyList<RunningFaultRecord> Items { get; init; }
}

/// <summary>
/// 运行故障筛选下拉选项。
/// </summary>
public sealed class RunningFaultOptionsDto
{
    /// <summary>当前默认月份，格式 yyyyMM。</summary>
    public required string CurrentMonth { get; init; }

    /// <summary>可查询月份列表，按倒序排列。</summary>
    public required IReadOnlyList<string> Months { get; init; }

    /// <summary>可查询指标类型列表。</summary>
    public required IReadOnlyList<string> MetricTypes { get; init; }
}
