using System.Globalization;
using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>
/// 锅炉运行故障记录查询接口。
/// </summary>
[ApiController]
[Route("api/running-faults")]
public sealed class RunningFaultController(RunningFaultStore runningFaultStore) : ControllerBase
{
    private const int DefaultPageSize = 5;
    private const int MaxPageSize = 50;

    /// <summary>
    /// 分页查询指定月份的运行故障记录。
    /// </summary>
    /// <param name="month">查询月份，格式 yyyyMM；为空时使用当前月份。</param>
    /// <param name="metricType">指标类型，all 表示全部指标。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页记录数，默认 5。</param>
    /// <returns>运行故障分页数据。</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<RunningFaultPageResponseDto>), StatusCodes.Status200OK)]
    public ApiResponse<RunningFaultPageResponseDto> Query(
        [FromQuery] string? month,
        [FromQuery] string? metricType = RunningFaultMetricTypes.All,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var queryMonth = NormalizeMonth(month);
        var queryMetricType = NormalizeMetricType(metricType);
        ValidatePage(page, pageSize);

        var result = runningFaultStore.Query(queryMonth, queryMetricType, page, pageSize);
        return ApiResponses.Success(new RunningFaultPageResponseDto
        {
            Month = result.Month,
            MetricType = result.MetricType,
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            Items = result.Items
        });
    }

    /// <summary>
    /// 查询运行故障筛选下拉选项。
    /// </summary>
    /// <param name="month">用于查询指标类型的月份，格式 yyyyMM；为空时使用当前月份。</param>
    /// <returns>月份与指标类型选项。</returns>
    [HttpGet("options")]
    [ProducesResponseType(typeof(ApiResponse<RunningFaultOptionsDto>), StatusCodes.Status200OK)]
    public ApiResponse<RunningFaultOptionsDto> QueryOptions([FromQuery] string? month)
    {
        var currentMonth = NormalizeMonth(month);
        var months = runningFaultStore.QueryMonths().ToList();
        if (!months.Contains(currentMonth, StringComparer.Ordinal))
        {
            months.Insert(0, currentMonth);
        }

        return ApiResponses.Success(new RunningFaultOptionsDto
        {
            CurrentMonth = currentMonth,
            Months = months,
            MetricTypes = runningFaultStore.QueryMetricTypes(currentMonth)
        });
    }

    private static string NormalizeMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            return BoilerTime.ChinaNow.ToString("yyyyMM", CultureInfo.InvariantCulture);
        }

        var normalized = month.Trim();
        if (!DateTime.TryParseExact(
                normalized,
                "yyyyMM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new BusinessException(400, "查询月份格式不正确，请使用 yyyyMM。");
        }

        return normalized;
    }

    private static string NormalizeMetricType(string? metricType)
    {
        if (string.IsNullOrWhiteSpace(metricType))
        {
            return RunningFaultMetricTypes.All;
        }

        var normalized = metricType.Trim();
        if (string.Equals(normalized, RunningFaultMetricTypes.All, StringComparison.Ordinal))
        {
            return normalized;
        }

        if (!RunningFaultMetricTypes.AllDisplayTypes.Contains(normalized, StringComparer.Ordinal))
        {
            throw new BusinessException(400, "指标类型不正确。");
        }

        return normalized;
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new BusinessException(400, "页码必须大于等于1。");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new BusinessException(400, $"每页记录数必须在1至{MaxPageSize}之间。");
        }
    }
}
