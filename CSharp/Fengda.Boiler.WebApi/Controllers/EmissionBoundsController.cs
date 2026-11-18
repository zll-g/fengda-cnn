using System.Globalization;
using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Fengda.Boiler.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>NOx/O₂ 排放边界模型监测接口。</summary>
[ApiController]
[Route("api/emission-bounds")]
public sealed class EmissionBoundsController(
    EmissionBoundsRuntimeCache runtimeCache,
    EmissionBoundsStore store) : ControllerBase
{
    private const int DefaultPageSize = 10;

    /// <summary>查询后台模型服务的最新状态、输入因子及最近 60 秒趋势。</summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(ApiResponse<EmissionBoundsEvaluationDto>), StatusCodes.Status200OK)]
    public ApiResponse<EmissionBoundsEvaluationDto> GetCurrent() => ApiResponses.Success(runtimeCache.GetLatest());

    /// <summary>查询模型信息和输入窗口契约。</summary>
    [HttpGet("model")]
    [ProducesResponseType(typeof(ApiResponse<EmissionBoundsModelInfoDto>), StatusCodes.Status200OK)]
    public ApiResponse<EmissionBoundsModelInfoDto> GetModelInfo()
    {
        var current = runtimeCache.GetLatest();
        var quality = runtimeCache.GetModelQuality();
        return ApiResponses.Success(new EmissionBoundsModelInfoDto
        {
            Status = current.ModelVersion is null ? "unavailable" : "loaded",
            ModelVersion = current.ModelVersion,
            ModelArtifactId = quality?.ModelArtifactId,
            TrainingCreatedAt = quality?.TrainingCreatedAt,
            NoxTestCoverage = quality?.NoxTestCoverage,
            O2TestCoverage = quality?.O2TestCoverage,
            ModelBoundaryAlertsEnabled = quality?.ModelBoundaryAlertsEnabled ?? false,
            WindowSeconds = Fengda.EmissionBounds.EmissionBoundsOnnxModel.WindowSize,
            IsFutureForecast = false,
            Message = "模型估算当前工况下 NOx/O₂ 的统计边界，不是未来排放预测，也不是法规或联锁限值。 "
                + (quality?.Message ?? string.Empty)
        });
    }

    /// <summary>读取指定自然日每分钟保存的排放上下限趋势。</summary>
    /// <param name="date">查询日期，格式 yyyy-MM-dd；未指定时使用中国标准时间当天。</param>
    [HttpGet("history")]
    [ProducesResponseType(typeof(ApiResponse<EmissionBoundsHistoryDto>), StatusCodes.Status200OK)]
    public ApiResponse<EmissionBoundsHistoryDto> GetHistory([FromQuery] string? date)
    {
        var day = NormalizeDate(date);
        var modelArtifactId = runtimeCache.GetModelQuality()?.ModelArtifactId;
        var samples = store.QueryDay(day, modelArtifactId);
        return ApiResponses.Success(new EmissionBoundsHistoryDto
        {
            Date = day,
            ModelArtifactId = modelArtifactId,
            SampleCount = samples.Count,
            Samples = samples
        });
    }

    /// <summary>分页查询指定月份的 NOx/O2 排放预警记录。</summary>
    /// <param name="month">查询月份，格式 yyyyMM；未指定时使用当前月。</param>
    /// <param name="metric">指标筛选：all、NOx 或 O₂。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页记录数，范围 1 至 100。</param>
    [HttpGet("events")]
    [ProducesResponseType(typeof(ApiResponse<EmissionBoundsEventPageDto>), StatusCodes.Status200OK)]
    public ApiResponse<EmissionBoundsEventPageDto> GetEvents(
        [FromQuery] string? month,
        [FromQuery] string metric = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var normalizedMonth = NormalizeMonth(month);
        metric = NormalizeMetric(metric);
        if (page < 1)
        {
            throw new BusinessException(400, "页码必须大于等于 1。");
        }
        if (pageSize is < 1 or > 100)
        {
            throw new BusinessException(400, "每页记录数必须在 1 至 100 之间。");
        }

        var result = store.QueryEvents(normalizedMonth, metric, page, pageSize);
        return ApiResponses.Success(new EmissionBoundsEventPageDto
        {
            Month = normalizedMonth,
            Page = page,
            PageSize = pageSize,
            Total = result.Total,
            Items = result.Items
        });
    }

    /// <summary>查询已产生排放预警记录的月份选项。</summary>
    [HttpGet("months")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<string>>), StatusCodes.Status200OK)]
    public ApiResponse<IReadOnlyList<string>> GetMonths()
    {
        var currentMonth = BoilerTime.ChinaNow.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var months = store.QueryMonths().ToList();
        if (!months.Contains(currentMonth, StringComparer.Ordinal))
        {
            months.Insert(0, currentMonth);
        }
        return ApiResponses.Success<IReadOnlyList<string>>(months);
    }

    private static DateTime NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BoilerTime.ChinaNow.Date;
        }
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new BusinessException(400, "日期格式不正确，请使用 yyyy-MM-dd。");
        }
        return date.Date;
    }

    private static string NormalizeMonth(string? value)
    {
        var month = string.IsNullOrWhiteSpace(value)
            ? BoilerTime.ChinaNow.ToString("yyyyMM", CultureInfo.InvariantCulture)
            : value.Trim();
        if (!DateTime.TryParseExact(month, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new BusinessException(400, "月份格式不正确，请使用 yyyyMM。");
        }
        return month;
    }

    private static string NormalizeMetric(string value)
    {
        var metric = string.IsNullOrWhiteSpace(value) ? "all" : value.Trim();
        if (metric.Equals("all", StringComparison.OrdinalIgnoreCase)) return "all";
        if (metric.Equals("nox", StringComparison.OrdinalIgnoreCase)) return "NOx";
        if (metric.Equals("o2", StringComparison.OrdinalIgnoreCase) || metric == "O₂") return "O₂";
        throw new BusinessException(400, "指标类型不正确，请使用 all、NOx 或 O₂。");
    }
}
