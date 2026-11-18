using Fengda.Boiler.WebApi.Models;
using Fengda.Boiler.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>
/// 锅炉实时与历史预测接口。
/// </summary>
[ApiController]
[Route("api/predictions")]
public sealed class PredictionController(PredictionApplicationService predictionService) : ControllerBase
{
    /// <summary>
    /// 使用实时缓存中的最后连续 60 秒完整数据预测未来 60 秒，并生成 FGR 建议。
    /// </summary>
    /// <returns>输入数据、预测结果、FGR 建议和数据质量信息；连续输入不足时 data 为 null。</returns>
    [HttpGet("latest")]
    [ProducesResponseType(typeof(ApiResponse<PredictionResponseDto?>), StatusCodes.Status200OK)]
    public ApiResponse<PredictionResponseDto?> PredictLatest()
    {
        return ApiResponses.Success(predictionService.PredictLatest());
    }

    /// <summary>
    /// 使用指定时间及其之前 60 秒数据预测未来 60 秒，并返回之后 60 秒实际数据用于对比。
    /// </summary>
    /// <param name="time">预测输入窗口的最后一秒，建议使用包含 +08:00 时区的 ISO 8601 格式。</param>
    /// <returns>输入数据、预测结果、FGR 建议和预测后实际数据；前后连续数据不足时 data 为 null。</returns>
    [HttpGet("history")]
    [ProducesResponseType(typeof(ApiResponse<PredictionResponseDto?>), StatusCodes.Status200OK)]
    public ApiResponse<PredictionResponseDto?> PredictHistory([FromQuery] DateTimeOffset? time)
    {
        if (time is null)
        {
            throw new BusinessException(400, "指定预测时间不能为空。");
        }

        var chinaTime = time.Value.ToOffset(TimeSpan.FromHours(8)).DateTime;
        return ApiResponses.Success(predictionService.PredictHistory(chinaTime));
    }
}
