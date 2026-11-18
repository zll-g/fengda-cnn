using System.Globalization;
using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Fengda.Boiler.WebApi.Services;
using Fengda.EarlyWarning;
using Microsoft.AspNetCore.Mvc;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>ONNX 异常监测预警查询接口。</summary>
[ApiController]
[Route("api/early-warnings")]
public sealed class EarlyWarningController(
    EarlyWarningRuntimeCache runtimeCache,
    EarlyWarningStore earlyWarningStore,
    BoilerSnapshotStore snapshotStore,
    EarlyWarningHistoricalAnalysisService historicalAnalysisService) : ControllerBase
{
    private const int DefaultPageSize = 5;
    private const int MaxPageSize = 50;

    /// <summary>查询后台服务缓存的最新三场景预警结果。</summary>
    [HttpGet("latest")]
    [ProducesResponseType(typeof(ApiResponse<EarlyWarningEvaluation>), StatusCodes.Status200OK)]
    public ApiResponse<EarlyWarningEvaluation> GetLatest() => ApiResponses.Success(runtimeCache.GetLatest());

    /// <summary>分页查询指定月份的 ONNX 模型预警记录。</summary>
    /// <param name="month">查询月份，格式 yyyyMM；为空时使用当前月份。</param>
    /// <param name="scenario">预警场景，all 表示全部。</param>
    /// <param name="level">预警等级，all 表示全部。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页记录数，默认 5。</param>
    [HttpGet("records")]
    [ProducesResponseType(typeof(ApiResponse<EarlyWarningPageResponseDto>), StatusCodes.Status200OK)]
    public ApiResponse<EarlyWarningPageResponseDto> QueryRecords(
        [FromQuery] string? month,
        [FromQuery] string? scenario = EarlyWarningRecordScenarios.All,
        [FromQuery] string? level = EarlyWarningRecordScenarios.All,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var result = earlyWarningStore.Query(NormalizeMonth(month), NormalizeScenario(scenario), NormalizeLevel(level), ValidatePage(page), ValidatePageSize(pageSize));
        return ApiResponses.Success(new EarlyWarningPageResponseDto
        {
            Month = result.Month,
            Scenario = result.Scenario,
            Level = result.Level,
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            Items = result.Items
        });
    }

    /// <summary>查询一条模型预警发生前两分钟的趋势及关联测点重构结果。</summary>
    /// <param name="id">模型预警记录主键。</param>
    /// <param name="month">预警记录所属月份，格式 yyyyMM。</param>
    /// <returns>告警时刻前 120 秒至告警时刻的原始秒级监测值，以及基于告警输入窗口回放得到的关联测点重构结果。</returns>
    [HttpGet("records/{id}/trend")]
    [ProducesResponseType(typeof(ApiResponse<EarlyWarningTrendResponseDto>), StatusCodes.Status200OK)]
    public ApiResponse<EarlyWarningTrendResponseDto> QueryRecordTrend(string id, [FromQuery] string? month)
    {
        var recordMonth = NormalizeMonth(month);
        if (!earlyWarningStore.TryGet(recordMonth, id, out var record))
        {
            throw new BusinessException(404, "未找到指定的模型预警记录。");
        }

        if (!EarlyWarningOnnxModel.TryGetScenarioDefinition(record.Scenario, out var scenario))
        {
            throw new BusinessException(400, "该预警记录的场景定义不可用，无法读取趋势数据。");
        }

        var fields = new[] { scenario.TargetField }
            .Concat(scenario.RelatedFields)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var points = fields
            .Select(field =>
            {
                if (!EarlyWarningPointRegistry.TryGet(field, out var point))
                {
                    throw new BusinessException(400, $"预警场景中的测点“{field}”定义不可用。");
                }

                return point;
            })
            .ToList();
        var startTime = BoilerTime.TruncateToSecond(record.AlarmTime.AddMinutes(-2));
        var endTime = BoilerTime.TruncateToSecond(record.AlarmTime);
        var snapshots = new List<BoilerSnapshot>();
        var succeeded = snapshotStore.TryVisitRange(startTime, endTime, snapshots.Add, out var errorMessage);
        var samples = snapshots
            .Select(snapshot => new EarlyWarningTrendSampleDto
            {
                RecordedAt = snapshot.RecordedAt,
                Values = points.ToDictionary(point => point.Field, point => point.GetValue(snapshot), StringComparer.Ordinal)
            })
            .ToList();
        var message = !succeeded
            ? errorMessage ?? "读取预警趋势历史数据失败。"
            : samples.Count == 0
                ? "未查询到预警发生前两分钟的历史监测数据。"
                : samples.Count < 121
                    ? $"历史数据不完整，已读取 {samples.Count} 条秒级记录。"
                    : string.Empty;

        var contributors = Array.Empty<EarlyWarningTrendContributorDto>();
        if (succeeded)
        {
            var windowStart = BoilerTime.TruncateToSecond(record.WindowStart);
            var windowEnd = BoilerTime.TruncateToSecond(record.WindowEnd);
            var inputWindow = snapshots
                .Where(snapshot => snapshot.RecordedAt >= windowStart && snapshot.RecordedAt <= windowEnd)
                .OrderBy(snapshot => snapshot.RecordedAt)
                .ToList();
            if (historicalAnalysisService.TryEvaluate(inputWindow, out var evaluation, out var replayError)
                && evaluation is not null)
            {
                contributors = evaluation.Scenarios
                    .FirstOrDefault(item => string.Equals(item.Key, record.Scenario, StringComparison.Ordinal))?
                    .Contributors
                    .Select(item => new EarlyWarningTrendContributorDto
                    {
                        Field = item.Field,
                        Name = item.Name,
                        Unit = item.Unit,
                        Actual = item.Actual,
                        Reconstructed = item.Reconstructed,
                        Contribution = item.Contribution,
                        Direction = item.Direction
                    })
                    .ToArray() ?? [];
            }
            else if (!string.IsNullOrWhiteSpace(replayError))
            {
                message = string.IsNullOrWhiteSpace(message)
                    ? $"无法生成关联测点重构结果：{replayError}"
                    : $"{message} 无法生成关联测点重构结果：{replayError}";
            }
        }

        return ApiResponses.Success(new EarlyWarningTrendResponseDto
        {
            RecordId = record.Id,
            StartTime = startTime,
            AlarmTime = endTime,
            Message = message,
            Points = points.Select(point => new EarlyWarningTrendPointDto
            {
                Field = point.Field,
                Name = point.Name,
                Unit = point.Unit,
                IsTarget = string.Equals(point.Field, scenario.TargetField, StringComparison.Ordinal)
            }).ToList(),
            Contributors = contributors,
            Samples = samples
        });
    }

    /// <summary>查询模型预警记录筛选项。</summary>
    /// <param name="month">默认查询月份，格式 yyyyMM；为空时使用当前月份。</param>
    [HttpGet("options")]
    [ProducesResponseType(typeof(ApiResponse<EarlyWarningOptionsDto>), StatusCodes.Status200OK)]
    public ApiResponse<EarlyWarningOptionsDto> QueryOptions([FromQuery] string? month)
    {
        var currentMonth = NormalizeMonth(month);
        var months = earlyWarningStore.QueryMonths().ToList();
        if (!months.Contains(currentMonth, StringComparer.Ordinal))
        {
            months.Insert(0, currentMonth);
        }
        return ApiResponses.Success(new EarlyWarningOptionsDto
        {
            CurrentMonth = currentMonth,
            Months = months,
            Scenarios = EarlyWarningRecordScenarios.DisplayValues,
            Levels = EarlyWarningRecordLevels.All
        });
    }

    private static string NormalizeMonth(string? month)
    {
        var value = string.IsNullOrWhiteSpace(month) ? BoilerTime.ChinaNow.ToString("yyyyMM", CultureInfo.InvariantCulture) : month.Trim();
        if (!DateTime.TryParseExact(value, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new BusinessException(400, "查询月份格式不正确，请使用 yyyyMM。 ");
        }
        return value;
    }

    private static string NormalizeScenario(string? scenario)
    {
        var value = string.IsNullOrWhiteSpace(scenario) ? EarlyWarningRecordScenarios.All : scenario.Trim();
        if (!string.Equals(value, EarlyWarningRecordScenarios.All, StringComparison.Ordinal) && !EarlyWarningRecordScenarios.DisplayValues.Contains(value, StringComparer.Ordinal))
        {
            throw new BusinessException(400, "预警场景不正确。 ");
        }
        return value;
    }

    private static string NormalizeLevel(string? level)
    {
        var value = string.IsNullOrWhiteSpace(level) ? EarlyWarningRecordScenarios.All : level.Trim();
        if (!string.Equals(value, EarlyWarningRecordScenarios.All, StringComparison.Ordinal) && !EarlyWarningRecordLevels.All.Contains(value, StringComparer.Ordinal))
        {
            throw new BusinessException(400, "预警等级不正确。 ");
        }
        return value;
    }

    private static int ValidatePage(int page)
    {
        if (page < 1)
        {
            throw new BusinessException(400, "页码必须大于等于1。 ");
        }
        return page;
    }

    private static int ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new BusinessException(400, $"每页记录数必须在1至{MaxPageSize}之间。 ");
        }
        return pageSize;
    }
}
