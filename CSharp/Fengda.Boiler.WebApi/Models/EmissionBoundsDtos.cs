namespace Fengda.Boiler.WebApi.Models;

/// <summary>当前排放边界模型评估状态及最近趋势。</summary>
public sealed class EmissionBoundsEvaluationDto
{
    /// <summary>评估状态代码：model_unavailable、warming_up、data_invalid、stopped、normal、out_of_range 或 inference_error。</summary>
    public required string Status { get; init; }

    /// <summary>评估状态中文名称。</summary>
    public required string StatusLabel { get; init; }

    /// <summary>模型版本；模型未加载时为空。</summary>
    public string? ModelVersion { get; init; }

    /// <summary>最近评估时间。</summary>
    public DateTime? EvaluatedAt { get; init; }

    /// <summary>最近快照记录时间。</summary>
    public DateTime? SnapshotTime { get; init; }

    /// <summary>阀位运行状态；中心气阀位或外环气阀位任一有效反馈大于 1% 时为运行。</summary>
    public bool? IsRunning { get; init; }

    /// <summary>模型有效燃烧状态：active、warming_up、not_applicable、transition 或 data_invalid。</summary>
    public string EffectiveBurningStatus { get; init; } = "warming_up";

    /// <summary>最近 60 秒窗口是否全部满足模型有效燃烧规则。</summary>
    public bool IsEffectiveBurning { get; init; }

    /// <summary>当前评分阈值状态；shadow 表示临时影子阈值仅供观察且不生成评分预警记录，trial_only 表示尚无匹配阈值。</summary>
    public string ScoreCalibrationStatus { get; init; } = "unavailable";

    /// <summary>任一路火焰信号为 1 时已点火。</summary>
    public bool? IsIgnited { get; init; }

    /// <summary>当前状态说明；模型边界仅作为观察参考。</summary>
    public required string Message { get; init; }

    /// <summary>当前模型独立测试质量和部署限制。</summary>
    public EmissionBoundsModelQualityDto? ModelQuality { get; init; }

    /// <summary>顶部概览运行参数。</summary>
    public required EmissionBoundsOverviewDto Overview { get; init; }

    /// <summary>八个模型输入因子的当前值、历史范围和局部敏感度。</summary>
    public required IReadOnlyList<EmissionBoundsInputDto> Inputs { get; init; }

    /// <summary>NOx 实测值与模型统计边界。</summary>
    public required EmissionBoundsTargetDto Nox { get; init; }

    /// <summary>O2 实测值与模型统计边界。</summary>
    public required EmissionBoundsTargetDto O2 { get; init; }

    /// <summary>进程内最近 60 秒的逐秒评估曲线。</summary>
    public required IReadOnlyList<EmissionBoundsTrendDto> Trend { get; init; }
}

/// <summary>NOx/O2 模型质量摘要及部署限制。</summary>
public sealed class EmissionBoundsModelQualityDto
{
    /// <summary>模型版本。</summary>
    public required string ModelVersion { get; init; }

    /// <summary>ONNX 文件 SHA-256，用于区分同模型版本的不同训练产物。</summary>
    public required string ModelArtifactId { get; init; }

    /// <summary>模型产物创建时间。</summary>
    public DateTimeOffset? TrainingCreatedAt { get; init; }

    /// <summary>NOx 独立测试集区间覆盖率。</summary>
    public double? NoxTestCoverage { get; init; }

    /// <summary>O₂ 独立测试集区间覆盖率。</summary>
    public double? O2TestCoverage { get; init; }

    /// <summary>当前模型是否已启用评分预警；影子阈值不视为已启用。</summary>
    public bool ModelBoundaryAlertsEnabled { get; init; }

    /// <summary>模型质量提示。</summary>
    public required string Message { get; init; }
}

/// <summary>顶部概览运行参数。</summary>
public sealed class EmissionBoundsOverviewDto
{
    /// <summary>火焰信号状态文字。</summary>
    public required string FlameText { get; init; }

    /// <summary>汽包蒸汽压力，单位 MPa。</summary>
    public double? SteamPressureMpa { get; init; }

    /// <summary>减温器出口温度，单位摄氏度。</summary>
    public double? OutletTemperatureC { get; init; }

    /// <summary>蒸汽流量补偿值，单位 t/h。</summary>
    public double? SteamFlowCompensationTph { get; init; }
}

/// <summary>一个模型输入因子及其训练数据参考信息。</summary>
public sealed class EmissionBoundsInputDto
{
    /// <summary>模型输入字段名。</summary>
    public required string Field { get; init; }

    /// <summary>现场 OPC UA 测点名。</summary>
    public required string PointName { get; init; }

    /// <summary>测点中文名称。</summary>
    public required string Name { get; init; }

    /// <summary>工程单位。</summary>
    public required string Unit { get; init; }

    /// <summary>当前值；缺失时为空。</summary>
    public double? CurrentValue { get; init; }

    /// <summary>训练集历史 P10 参考值；当前天然气流量无匹配训练分组时为空。</summary>
    public double? HistoricalP10 { get; init; }

    /// <summary>训练集历史中位数；当前天然气流量无匹配训练分组时为空。</summary>
    public double? HistoricalMedian { get; init; }

    /// <summary>训练集历史 P90 参考值；当前天然气流量无匹配训练分组时为空。</summary>
    public double? HistoricalP90 { get; init; }

    /// <summary>归一化模型局部敏感度估算百分比；不是因果权重。</summary>
    public double? SensitivityPercent { get; init; }

    /// <summary>当前值相对训练集历史 P10–P90 的状态及参考调整方向；不是控制指令。</summary>
    public required string Assessment { get; init; }
}

/// <summary>单个排放目标的实测值和模型统计边界。</summary>
public sealed class EmissionBoundsTargetDto
{
    /// <summary>指标名称。</summary>
    public required string Name { get; init; }

    /// <summary>工程单位。</summary>
    public required string Unit { get; init; }

    /// <summary>分析仪实测值。</summary>
    public double? ActualValue { get; init; }

    /// <summary>模型统计下限。</summary>
    public double? LowerBound { get; init; }

    /// <summary>模型统计上限。</summary>
    public double? UpperBound { get; init; }

    /// <summary>运行经验风险下限；NOx 为 10 mg/m³，O2 为 3%。</summary>
    public double? OperatingRiskLowerLimit { get; init; }

    /// <summary>评分归一化使用的区间宽度下限；未配置时按模型区间宽度计算。</summary>
    public double? ScoreWidthFloor { get; init; }

    /// <summary>运行风险下限状态：normal、below、missing 或 unavailable。</summary>
    public required string OperatingRiskStatus { get; init; }

    /// <summary>高于模型上限的归一化越界评分。</summary>
    public double? AboveScore { get; init; }

    /// <summary>高于模型上限的预警评分阈值。</summary>
    public double? AboveScoreThreshold { get; init; }

    /// <summary>偏高评分状态：normal、pending、warning 或 unavailable。</summary>
    public required string AboveAlertStatus { get; init; }

    /// <summary>偏高评分连续确认秒数。</summary>
    public int AboveConfirmSeconds { get; init; }

    /// <summary>偏高评分预警所需连续确认秒数。</summary>
    public int AboveRequiredSeconds { get; init; }

    /// <summary>低于模型下限的归一化越界评分。</summary>
    public double? BelowScore { get; init; }

    /// <summary>低于模型下限的预警评分阈值。</summary>
    public double? BelowScoreThreshold { get; init; }

    /// <summary>偏低评分状态：normal、pending、warning 或 unavailable。</summary>
    public required string BelowAlertStatus { get; init; }

    /// <summary>偏低评分连续确认秒数。</summary>
    public int BelowConfirmSeconds { get; init; }

    /// <summary>偏低评分预警所需连续确认秒数。</summary>
    public int BelowRequiredSeconds { get; init; }

    /// <summary>normal、below、above、missing 或 unavailable。</summary>
    public required string Status { get; init; }
}

/// <summary>单个时刻的 NOx/O2 实测值和模型统计边界。</summary>
public sealed class EmissionBoundsTrendDto
{
    /// <summary>生成该边界的 ONNX 文件 SHA-256。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>评估时间。</summary>
    public DateTime RecordedAt { get; init; }

    /// <summary>NOx 实测浓度，单位 mg/m³。</summary>
    public double? NoxActual { get; init; }

    /// <summary>NOx 模型统计下限，单位 mg/m³。</summary>
    public double? NoxLower { get; init; }

    /// <summary>NOx 模型统计上限，单位 mg/m³。</summary>
    public double? NoxUpper { get; init; }

    /// <summary>O2 实测浓度，单位 %。</summary>
    public double? O2Actual { get; init; }

    /// <summary>O2 模型统计下限，单位 %。</summary>
    public double? O2Lower { get; init; }

    /// <summary>O2 模型统计上限，单位 %。</summary>
    public double? O2Upper { get; init; }
}

/// <summary>NOx/O2 排放预警记录分页响应。</summary>
public sealed class EmissionBoundsEventPageDto
{
    /// <summary>查询月份，格式 yyyyMM。</summary>
    public required string Month { get; init; }

    /// <summary>当前页码。</summary>
    public int Page { get; init; }

    /// <summary>每页记录数。</summary>
    public int PageSize { get; init; }

    /// <summary>符合条件的总记录数。</summary>
    public int Total { get; init; }

    /// <summary>当前页预警记录。</summary>
    public required IReadOnlyList<EmissionBoundsEventDto> Items { get; init; }
}

/// <summary>指定自然日的分钟级排放边界历史趋势。</summary>
public sealed class EmissionBoundsHistoryDto
{
    /// <summary>查询自然日。</summary>
    public DateTime Date { get; init; }

    /// <summary>本次查询对应的 ONNX 文件 SHA-256。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>当日保存的分钟级样本数量。</summary>
    public int SampleCount { get; init; }

    /// <summary>按时间升序排列的模型边界趋势。</summary>
    public required IReadOnlyList<EmissionBoundsTrendDto> Samples { get; init; }
}

/// <summary>单条模型边界预警或运行风险下限提示；不触发设备联锁。</summary>
public sealed class EmissionBoundsEventDto
{
    /// <summary>记录编号。</summary>
    public required string Id { get; init; }

    /// <summary>生成该记录的 ONNX 文件 SHA-256；为空表示旧记录未记录模型指纹。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>记录时间。</summary>
    public DateTime RecordedAt { get; init; }

    /// <summary>NOx 或 O2。</summary>
    public required string Metric { get; init; }

    /// <summary>偏低或偏高。</summary>
    public required string Direction { get; init; }

    /// <summary>触发来源：model_boundary 或 operating_limit。</summary>
    public string Source { get; init; } = "model_boundary";

    /// <summary>预警等级：warning 或 risk。</summary>
    public string Level { get; init; } = "warning";

    /// <summary>归一化越界评分；运行风险下限提示为空。</summary>
    public double? Score { get; init; }

    /// <summary>触发预警的评分阈值；运行风险下限提示为空。</summary>
    public double? ScoreThreshold { get; init; }

    /// <summary>实测值。</summary>
    public double ActualValue { get; init; }

    /// <summary>模型下限。</summary>
    public double LowerBound { get; init; }

    /// <summary>模型上限。</summary>
    public double UpperBound { get; init; }

    /// <summary>单位。</summary>
    public required string Unit { get; init; }

    /// <summary>记录说明。</summary>
    public required string Description { get; init; }
}

/// <summary>排放边界模型基本信息。</summary>
public sealed class EmissionBoundsModelInfoDto
{
    /// <summary>模型加载状态。</summary>
    public required string Status { get; init; }

    /// <summary>模型版本。</summary>
    public string? ModelVersion { get; init; }

    /// <summary>ONNX 文件 SHA-256，用于准确识别训练产物。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>模型产物创建时间。</summary>
    public DateTimeOffset? TrainingCreatedAt { get; init; }

    /// <summary>NOx 独立测试集区间覆盖率。</summary>
    public double? NoxTestCoverage { get; init; }

    /// <summary>O₂ 独立测试集区间覆盖率。</summary>
    public double? O2TestCoverage { get; init; }

    /// <summary>是否启用经当前模型校准的边界评分预警。</summary>
    public bool ModelBoundaryAlertsEnabled { get; init; }

    /// <summary>每个输入窗口秒数。</summary>
    public int WindowSeconds { get; init; }

    /// <summary>是否为未来预测；本模型输出当前工况统计边界，固定为 false。</summary>
    public bool IsFutureForecast { get; init; }

    /// <summary>模型部署提示。</summary>
    public required string Message { get; init; }
}
