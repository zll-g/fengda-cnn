using Fengda.Boiler.WebApi.Models;
using Fengda.Boiler.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fengda.Boiler.WebApi.Controllers;

/// <summary>
/// 前端界面元数据接口。
/// </summary>
[ApiController]
[Route("api/metadata")]
public sealed class MetadataController(PredictionApplicationService predictionService) : ControllerBase
{
    /// <summary>
    /// 查询锅炉监控点位、中文名称、单位和模型输入输出标识。
    /// </summary>
    /// <returns>前端图表使用的点位元数据。</returns>
    [HttpGet("points")]
    [ProducesResponseType(typeof(ApiResponse<PointMetadataResponseDto>), StatusCodes.Status200OK)]
    public ApiResponse<PointMetadataResponseDto> QueryPoints()
    {
        var points = BoilerPointMetadataCatalog.Build(
            predictionService.FeaturePointNames,
            predictionService.RequiredInputPointNames,
            predictionService.TargetPointNames);
        return ApiResponses.Success(new PointMetadataResponseDto
        {
            ModelStatus = predictionService.ModelStatus,
            Points = points,
        });
    }
}
