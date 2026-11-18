using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>锅炉燃烧诊断图表接口。</summary>
[ApiController]
[Route("api/combustion-diagnosis")]
public sealed class CombustionDiagnosisController(BoilerSnapshotStore snapshotStore) : ControllerBase
{
    /// <summary>
    /// 读取近五个自然日的单位产汽耗气量。
    /// </summary>
    /// <param name="date">统计截止日期；未指定时使用中国标准时间当天。</param>
    /// <returns>按日期升序返回五天的 Nm³/t 统计值。</returns>
    [HttpGet("gas-consumption")]
    [ProducesResponseType(typeof(ApiResponse<DailyGasConsumptionTrendDto>), StatusCodes.Status200OK)]
    public ApiResponse<DailyGasConsumptionTrendDto> GetGasConsumption([FromQuery] DateTime? date)
    {
        var endDay = (date ?? BoilerTime.ChinaNow).Date;
        var latestSnapshots = Enumerable.Range(0, 6)
            .Select(offset => endDay.AddDays(offset - 5))
            .ToDictionary(day => day, snapshotStore.QueryDailyLatest);

        var days = Enumerable.Range(0, 5)
            .Select(offset => endDay.AddDays(offset - 4))
            .Select(day => new DailyGasConsumptionDto
            {
                Date = day,
                ConsumptionPerTon = CalculateConsumptionPerTon(
                    latestSnapshots[day],
                    latestSnapshots[day.AddDays(-1)])
            })
            .ToList();

        return ApiResponses.Success(new DailyGasConsumptionTrendDto
        {
            Date = endDay,
            Days = days
        });
    }

    /// <summary>
    /// 读取指定日期的分钟级模型预测缓存及对应实测值。
    /// </summary>
    /// <param name="date">查询日期；未指定时使用中国标准时间当天。</param>
    /// <param name="horizonSeconds">预测提前量，取值范围 1 至 60 秒，默认 30 秒。</param>
    /// <returns>分钟级实测、预测数据；缺失缓存或实测数据时对应字段返回 null。</returns>
    [HttpGet("daily-forecast")]
    [ProducesResponseType(typeof(ApiResponse<DailyCombustionForecastDto>), StatusCodes.Status200OK)]
    public ApiResponse<DailyCombustionForecastDto> GetDailyForecast(
        [FromQuery] DateTime? date,
        [FromQuery] int horizonSeconds = 30)
    {
        if (horizonSeconds is < 1 or > 60)
        {
            throw new BusinessException(400, "预测提前量必须为 1 至 60 秒。" );
        }

        var day = (date ?? BoilerTime.ChinaNow).Date;
        var forecasts = snapshotStore.QueryDailyMinuteForecasts(day);
        var firstTarget = forecasts.Count == 0
            ? day
            : forecasts.Min(item => item.ForecastStartTime).AddSeconds(Math.Min(horizonSeconds, 59));
        var lastTarget = forecasts.Count == 0
            ? day
            : forecasts.Max(item => item.ForecastStartTime).AddSeconds(Math.Min(horizonSeconds, 59));
        var actualByTime = snapshotStore.QueryRange(firstTarget, lastTarget)
            .ToDictionary(snapshot => BoilerTime.TruncateToSecond(snapshot.RecordedAt));

        var samples = new List<DailyCombustionForecastSampleDto>();
        foreach (var forecast in forecasts)
        {
            // 界面按时间偏移理解提前量：选择 30 秒即显示 forecastStart + 30 秒。
            // 当前模型仅提供 forecastStart 至 forecastStart + 59 秒，因此 60 秒回落到最后一个可用预测点。
            var valueIndex = Math.Min(horizonSeconds, 59);
            var targetTime = forecast.ForecastStartTime.AddSeconds(valueIndex);
            actualByTime.TryGetValue(targetTime, out var actualSnapshot);
            if (actualSnapshot is null)
            {
                // 预测目标时刻尚未到达时，不把缺失实测值绘制为 0。
                continue;
            }

            var pointNames = forecast.Series.Select(series => series.PointName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            samples.Add(new DailyCombustionForecastSampleDto
            {
                AnchorTime = forecast.AnchorTime,
                TargetTime = targetTime,
                ActualValues = pointNames.ToDictionary(
                    pointName => pointName,
                    pointName => BoilerPointBindings.TryGetPredictionValue(actualSnapshot, pointName, out var value) ? value : (double?)null,
                    StringComparer.OrdinalIgnoreCase),
                PredictedValues = forecast.Series.ToDictionary(
                    series => series.PointName,
                    series => valueIndex < series.Values.Count ? series.Values[valueIndex] : (double?)null,
                    StringComparer.OrdinalIgnoreCase)
            });
        }

        return ApiResponses.Success(new DailyCombustionForecastDto
        {
            Date = day,
            HorizonSeconds = horizonSeconds,
            Samples = samples,
            CachedMinuteCount = forecasts.Count
        });
    }

    private static double CalculateConsumptionPerTon(BoilerSnapshot? current, BoilerSnapshot? previous)
    {
        if (current is null || previous is null
            || !TryReadPositiveFinite(current.GYDCS_K0QHH10CF101_SUM, out var currentGas)
            || !TryReadPositiveFinite(previous.GYDCS_K0QHH10CF101_SUM, out var previousGas)
            || !TryReadPositiveFinite(current.GYDCS_K0QHB10CF101_SUM1, out var currentSteam)
            || !TryReadPositiveFinite(previous.GYDCS_K0QHB10CF101_SUM1, out var previousSteam))
        {
            return 0;
        }

        var gasUsed = currentGas - previousGas;
        var steamProduced = currentSteam - previousSteam;
        if (!double.IsFinite(gasUsed) || !double.IsFinite(steamProduced)
            || gasUsed <= 0 || steamProduced <= 0)
        {
            return 0;
        }

        var consumption = gasUsed / steamProduced;
        return double.IsFinite(consumption) && consumption > 0 ? consumption : 0;
    }

    private static bool TryReadPositiveFinite(double? value, out double result)
    {
        result = value.GetValueOrDefault();
        return value.HasValue && double.IsFinite(result) && result > 0;
    }
}
