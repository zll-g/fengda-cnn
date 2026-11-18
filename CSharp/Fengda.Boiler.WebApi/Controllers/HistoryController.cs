using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>
/// 锅炉历史记录查询接口。
/// </summary>
[ApiController]
[Route("api/history")]
public sealed class HistoryController(BoilerSnapshotStore snapshotStore) : ControllerBase
{
    private const int MaxQueryCount = 3600;
    private const int MinOverviewIntervalSeconds = 1;
    private const int MaxOverviewIntervalSeconds = 300;

    /// <summary>
    /// 从指定开始时间查询历史数据，结果不跨越开始时间所在自然日。
    /// </summary>
    /// <param name="startTime">查询开始时间，建议使用包含 +08:00 时区的 ISO 8601 格式。</param>
    /// <param name="count">需要返回的记录数，范围为 1 至 3600。</param>
    /// <returns>按时间升序排列的完整锅炉快照列表。</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<BoilerSnapshot>>), StatusCodes.Status200OK)]
    public ApiResponse<IReadOnlyList<BoilerSnapshot>> Query(
        [FromQuery] DateTimeOffset? startTime,
        [FromQuery] int count = 60)
    {
        if (startTime is null)
        {
            throw new BusinessException(400, "查询开始时间不能为空。");
        }

        ValidateCount(count);
        return ApiResponses.Success(snapshotStore.QueryDaily(ToChinaTime(startTime.Value), count));
    }

    /// <summary>
    /// 查询数据库最新数据所在自然日的最后若干条记录。
    /// </summary>
    /// <param name="count">需要返回的记录数，范围为 1 至 3600。</param>
    /// <returns>按时间升序排列的完整锅炉快照列表。</returns>
    [HttpGet("latest")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<BoilerSnapshot>>), StatusCodes.Status200OK)]
    public ApiResponse<IReadOnlyList<BoilerSnapshot>> QueryLatest([FromQuery] int count = 60)
    {
        ValidateCount(count);
        return ApiResponses.Success(snapshotStore.QueryLatest(count));
    }

    /// <summary>
    /// 按自然日查询历史概览数据，并按指定秒数降采样。
    /// </summary>
    /// <param name="date">查询日期，格式为 yyyy-MM-dd。</param>
    /// <param name="intervalSeconds">降采样间隔秒数，范围为 1 至 300，默认 10。</param>
    /// <returns>降采样后的完整锅炉快照列表。</returns>
    [HttpGet("day/overview")]
    [ProducesResponseType(typeof(ApiResponse<HistoryDayOverviewDto>), StatusCodes.Status200OK)]
    public ApiResponse<HistoryDayOverviewDto> QueryDayOverview(
        [FromQuery] string? date,
        [FromQuery] int intervalSeconds = 10)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            throw new BusinessException(400, "查询日期不能为空。");
        }

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw new BusinessException(400, "查询日期格式不正确，请使用 yyyy-MM-dd。");
        }

        if (intervalSeconds is < MinOverviewIntervalSeconds or > MaxOverviewIntervalSeconds)
        {
            throw new BusinessException(
                400,
                $"降采样间隔必须在 {MinOverviewIntervalSeconds} 至 {MaxOverviewIntervalSeconds} 秒之间。");
        }

        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1).AddSeconds(-1);
        var samples = snapshotStore.QueryRangeSampled(dayStart, dayEnd, intervalSeconds);

        return ApiResponses.Success(new HistoryDayOverviewDto
        {
            Day = dayStart,
            IntervalSeconds = intervalSeconds,
            SampleCount = samples.Count,
            Samples = samples,
        });
    }

    /// <summary>
    /// 查询指定整秒对应的一条完整锅炉快照。
    /// </summary>
    /// <param name="time">需要查询的时间，建议使用包含 +08:00 时区的 ISO 8601 格式。</param>
    /// <returns>指定时间的完整锅炉快照。</returns>
    [HttpGet("detail")]
    [ProducesResponseType(typeof(ApiResponse<BoilerSnapshot>), StatusCodes.Status200OK)]
    public ApiResponse<BoilerSnapshot> QueryDetail([FromQuery] DateTimeOffset? time)
    {
        if (time is null)
        {
            throw new BusinessException(400, "查询时间不能为空。");
        }

        var chinaTime = ToChinaTime(time.Value);
        var result = snapshotStore.QueryDetail(chinaTime)
            ?? throw new BusinessException(404, $"未找到 {chinaTime:yyyy-MM-dd HH:mm:ss} 的历史数据。");
        return ApiResponses.Success(result);
    }

    private static void ValidateCount(int count)
    {
        if (count is < 1 or > MaxQueryCount)
        {
            throw new BusinessException(400, $"记录数必须在 1 至 {MaxQueryCount} 之间。");
        }
    }

    private static DateTime ToChinaTime(DateTimeOffset time)
    {
        return BoilerTime.TruncateToSecond(time.ToOffset(TimeSpan.FromHours(8)).DateTime);
    }
}
