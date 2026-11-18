using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Fengda.EmissionBounds;

namespace Fengda.Boiler.WebApi.Services;

/// <summary>NOx/O2 排放边界模型后台服务参数。</summary>
public sealed record EmissionBoundsOptions
{
    /// <summary>是否启用后台模型评估。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>模型文件目录，相对路径基于 WebApi 内容根目录。</summary>
    public string ModelDirectory { get; init; } = Path.Combine("model", "emission_bounds");

    /// <summary>独立 LiteDB 文件路径，相对路径基于 WebApi 内容根目录。</summary>
    public string DatabasePath { get; init; } = Path.Combine("data", "emission_bounds.db");

    /// <summary>轮询最近实时快照的间隔毫秒数。</summary>
    public int PollIntervalMilliseconds { get; init; } = 250;

    /// <summary>快照超过该秒数时视为陈旧，不再推理。</summary>
    public int MaxSnapshotAgeSeconds { get; init; } = 5;

    /// <summary>同指标同偏离方向的观察记录抑制时间分钟数。</summary>
    public int EventCooldownMinutes { get; init; } = 5;

}

/// <summary>供接口读取的排放边界当前评估和最近趋势缓存。</summary>
public sealed class EmissionBoundsRuntimeCache
{
    private const int TrendCapacity = 60;
    private readonly object syncRoot = new();
    private readonly SortedDictionary<DateTime, EmissionBoundsTrendDto> trends = [];
    private EmissionBoundsModelQualityDto? modelQuality;
    private EmissionBoundsEvaluationDto current = Empty("warming_up", "预热中", "等待实时快照。", null);

    /// <summary>更新当前模型质量摘要。</summary>
    public void SetModelQuality(EmissionBoundsModelQualityDto quality)
    {
        lock (syncRoot)
        {
            modelQuality = quality;
            current = CopyWithTrend(current, trends.Values.ToArray(), modelQuality);
        }
    }

    /// <summary>读取当前模型质量摘要。</summary>
    public EmissionBoundsModelQualityDto? GetModelQuality()
    {
        lock (syncRoot)
        {
            return modelQuality;
        }
    }

    /// <summary>更新当前结果并保存最近逐秒评估曲线。</summary>
    public void Update(EmissionBoundsEvaluationDto evaluation)
    {
        lock (syncRoot)
        {
            if (evaluation.Nox.LowerBound.HasValue && evaluation.O2.LowerBound.HasValue && evaluation.EvaluatedAt.HasValue)
            {
                trends[evaluation.EvaluatedAt.Value] = new EmissionBoundsTrendDto
                {
                    RecordedAt = evaluation.EvaluatedAt.Value,
                    NoxActual = evaluation.Nox.ActualValue,
                    NoxLower = evaluation.Nox.LowerBound,
                    NoxUpper = evaluation.Nox.UpperBound,
                    O2Actual = evaluation.O2.ActualValue,
                    O2Lower = evaluation.O2.LowerBound,
                    O2Upper = evaluation.O2.UpperBound
                };
                while (trends.Count > TrendCapacity)
                {
                    trends.Remove(trends.Keys.First());
                }
            }
            current = CopyWithTrend(evaluation, trends.Values.ToArray(), modelQuality);
        }
    }

    /// <summary>读取最近评估及进程内趋势。</summary>
    public EmissionBoundsEvaluationDto GetLatest()
    {
        lock (syncRoot)
        {
            return CopyWithTrend(current, trends.Values.ToArray(), modelQuality);
        }
    }

    /// <summary>创建尚未完成推理的状态响应。</summary>
    public static EmissionBoundsEvaluationDto Empty(string status, string label, string message, string? modelVersion) => new()
    {
        Status = status,
        StatusLabel = label,
        ModelVersion = modelVersion,
        Message = message,
        Overview = new EmissionBoundsOverviewDto { FlameText = "--" },
        Inputs = EmissionBoundsHostedService.InputDefinitions
            .Select(field => new EmissionBoundsInputDto
            {
                Field = field.Field,
                PointName = field.PointName,
                Name = field.Name,
                Unit = field.Unit,
                Assessment = "等待数据"
            }).ToArray(),
        Nox = new EmissionBoundsTargetDto
        {
            Name = "烟囱 NOx 排放浓度", Unit = "mg/m³", Status = "unavailable",
            OperatingRiskLowerLimit = EmissionBoundsHostedService.NoxOperatingRiskLowerLimit,
            OperatingRiskStatus = "unavailable", AboveAlertStatus = "unavailable", BelowAlertStatus = "unavailable"
        },
        O2 = new EmissionBoundsTargetDto
        {
            Name = "烟囱 O₂ 排放浓度", Unit = "%", Status = "unavailable",
            OperatingRiskLowerLimit = EmissionBoundsHostedService.O2OperatingRiskLowerLimit,
            OperatingRiskStatus = "unavailable", AboveAlertStatus = "unavailable", BelowAlertStatus = "unavailable"
        },
        Trend = []
    };

    private static EmissionBoundsEvaluationDto CopyWithTrend(EmissionBoundsEvaluationDto source,
        IReadOnlyList<EmissionBoundsTrendDto> trend, EmissionBoundsModelQualityDto? modelQuality) => new()
    {
        Status = source.Status,
        StatusLabel = source.StatusLabel,
        ModelVersion = source.ModelVersion,
        EvaluatedAt = source.EvaluatedAt,
        SnapshotTime = source.SnapshotTime,
        IsRunning = source.IsRunning,
        IsIgnited = source.IsIgnited,
        EffectiveBurningStatus = source.EffectiveBurningStatus,
        IsEffectiveBurning = source.IsEffectiveBurning,
        ScoreCalibrationStatus = source.ScoreCalibrationStatus,
        Message = source.Message,
        ModelQuality = modelQuality,
        Overview = source.Overview,
        Inputs = source.Inputs,
        Nox = source.Nox,
        O2 = source.O2,
        Trend = trend
    };
}

/// <summary>
/// 使用 OPC UA 实时快照启动即运行的 NOx/O2 边界评估服务。
/// 同时检测模型统计边界和已确认的运行风险下限，并生成不触发联锁的监测预警。
/// </summary>
public sealed class EmissionBoundsHostedService(
    IWebHostEnvironment environment,
    EmissionBoundsOptions options,
    BoilerSnapshotCache snapshotCache,
    EmissionBoundsRuntimeCache runtimeCache,
    EmissionBoundsStore store,
    ILogger<EmissionBoundsHostedService> logger)
    : BackgroundService
{
    internal const double NoxOperatingRiskLowerLimit = 10;
    internal const double O2OperatingRiskLowerLimit = 3;

    internal sealed record InputDefinition(
        string Field, string PointName, string Name, string Unit, Func<BoilerSnapshot, double?> ReadValue);

    internal static readonly InputDefinition[] InputDefinitions =
    [
        new("burner_air_damper_pct", "K0QHL10AA101AI3", "燃烧器风门阀位", "%", snapshot => snapshot.GYDCS_K0QHL10AA101AI3),
        new("blower_frequency_hz", "K0QHL10AN001P", "鼓风机变频器频率", "Hz", snapshot => snapshot.GYDCS_K0QHL10AN001P),
        new("blower_current_a", "K0QHL10AN001I", "鼓风机变频器电流", "A", snapshot => snapshot.GYDCS_K0QHL10AN001I),
        new("burner_center_gas_valve_pct", "K0QHL10AA101AI5", "燃烧器中心气阀位", "%", snapshot => snapshot.GYDCS_K0QHL10AA101AI5),
        new("burner_outer_gas_valve_pct", "K0QHL10AA101AI4", "燃烧器外环气阀位", "%", snapshot => snapshot.GYDCS_K0QHL10AA101AI4),
        new("fgr_valve_pct", "K0QHL10AA101AI6", "FGR 调节阀阀位", "%", snapshot => snapshot.GYDCS_K0QHL10AA101AI6),
        new("fgr_fan_frequency_hz", "K0QHH10AV001P", "FGR 风机变频器频率", "Hz", snapshot => snapshot.GYDCS_K0QHH10AV001P),
        new("natural_gas_inlet_flow_m3_h", "K0QHH10CF101", "天然气入口瞬时流量", "m³/h", snapshot => snapshot.GYDCS_K0QHH10CF101)
    ];

    private readonly Dictionary<string, DateTime> lastEventTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScoreLatch> scoreLatches = new(StringComparer.Ordinal)
    {
        ["nox_above"] = new(), ["nox_below"] = new(), ["o2_above"] = new(), ["o2_below"] = new()
    };
    private AlertThresholdProfile? alertThresholds;
    private EffectiveBurningWindowGate? effectiveBurningGate;
    private string? modelArtifactId;
    private DateTime? lastProcessedTime;
    private DateTime? lastPersistedMinute;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            runtimeCache.Update(EmissionBoundsRuntimeCache.Empty("model_unavailable", "已禁用", "排放边界模型服务已禁用。", null));
            return;
        }

        EmissionBoundsOnnxModel model;
        try
        {
            var modelDirectory = ResolveModelDirectory();
            model = EmissionBoundsOnnxModel.Load(modelDirectory);
            modelArtifactId = GetModelArtifactId(modelDirectory);
            effectiveBurningGate = new EffectiveBurningWindowGate(model.EffectiveBurningRule);
            logger.LogInformation("NOx/O2 排放边界 ONNX 模型已加载：{ModelVersion}。", model.ModelVersion);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "NOx/O2 排放边界模型加载失败。");
            runtimeCache.Update(EmissionBoundsRuntimeCache.Empty(
                "model_unavailable", "模型不可用", $"模型加载失败：{exception.Message}", null));
            return;
        }

        try
        {
            alertThresholds = LoadAlertThresholds(ResolveModelDirectory(), model.ModelVersion);
            logger.LogInformation("NOx/O2 评分阈值已加载，校准状态：{CalibrationStatus}。", alertThresholds.CalibrationStatus);
        }
        catch (Exception exception)
        {
            alertThresholds = null;
            logger.LogWarning("当前模型未配置匹配的评分阈值，按试验模型仅显示边界，不生成模型评分预警记录：{Reason}", exception.Message);
        }

        runtimeCache.SetModelQuality(new EmissionBoundsModelQualityDto
        {
            ModelVersion = model.ModelVersion,
            ModelArtifactId = modelArtifactId!,
            TrainingCreatedAt = model.TrainingCreatedAt,
            NoxTestCoverage = model.NoxTestCoverage,
            O2TestCoverage = model.O2TestCoverage,
            ModelBoundaryAlertsEnabled = ModelBoundaryAlertsEnabled,
            Message = BuildModelQualityMessage(model, alertThresholds)
        });

        using (model)
        using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(options.PollIntervalMilliseconds, 100, 5000))))
        {
            do
            {
                try
                {
                    EvaluateLatest(model, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    ResetScoreLatches();
                    logger.LogError(exception, "NOx/O2 排放边界评估失败。");
                    runtimeCache.Update(EmissionBoundsRuntimeCache.Empty(
                        "inference_error", "推理异常", $"模型评估失败：{exception.Message}", model.ModelVersion));
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }

    private void EvaluateLatest(EmissionBoundsOnnxModel model, CancellationToken cancellationToken)
    {
        var snapshots = snapshotCache.GetLatest(EmissionBoundsOnnxModel.WindowSize);
        var latest = snapshots.LastOrDefault();
        if (latest is null)
        {
            ResetScoreLatches();
            runtimeCache.Update(EmissionBoundsRuntimeCache.Empty("warming_up", "等待数据", "等待 OPC UA 实时快照。", model.ModelVersion));
            return;
        }

        if (latest.RecordedAt == lastProcessedTime)
        {
            if ((BoilerTime.ChinaNow - latest.RecordedAt).TotalSeconds > Math.Clamp(options.MaxSnapshotAgeSeconds, 1, 60))
            {
                ResetScoreLatches();
                effectiveBurningGate?.Reset();
                runtimeCache.Update(BuildStatus("data_invalid", "数据已过期", "最新实时快照已过期，已暂停模型评估。", latest, model.ModelVersion, snapshotExpired: true, effectiveBurningStatus: "data_invalid"));
            }
            return;
        }

        var firstSnapshot = lastProcessedTime is null;
        var latestHasGap = lastProcessedTime is { } previousTime && latest.RecordedAt != previousTime.AddSeconds(1);
        if (latestHasGap)
        {
            ResetScoreLatches();
            effectiveBurningGate?.Reset();
        }
        lastProcessedTime = latest.RecordedAt;
        var ageSeconds = (BoilerTime.ChinaNow - latest.RecordedAt).TotalSeconds;
        if (ageSeconds > Math.Clamp(options.MaxSnapshotAgeSeconds, 1, 60) || ageSeconds < -2)
        {
            ResetScoreLatches();
            effectiveBurningGate?.Reset();
            runtimeCache.Update(BuildStatus("data_invalid", "数据已过期", "最新实时快照超出允许时间范围，已暂停模型评估。", latest, model.ModelVersion, snapshotExpired: true, effectiveBurningStatus: "data_invalid"));
            return;
        }

        var gate = effectiveBurningGate!;
        if (firstSnapshot)
        {
            gate.Reset();
            foreach (var snapshot in snapshots)
            {
                gate.Process(snapshot);
            }
        }
        else
        {
            gate.Process(latest);
        }

        var valveRunning = IsValveRunning(latest);
        var latestGateSample = gate.Latest;
        if (!valveRunning)
        {
            ResetScoreLatches();
            runtimeCache.Update(BuildStatus("stopped", "阀位停机", "中心气阀位和外环气阀位均未超过 1%，当前不进行 NOx/O₂ 模型评估。", latest, model.ModelVersion,
                effectiveBurningStatus: "not_applicable"));
            return;
        }

        if (latestGateSample is null || !latestGateSample.GateInputsValid)
        {
            ResetScoreLatches();
            runtimeCache.Update(BuildStatus("data_invalid", "有效工况数据缺失", "天然气流量、鼓风机频率或鼓风机电流无有效值，已退出模型适用状态。", latest, model.ModelVersion,
                effectiveBurningStatus: "data_invalid"));
            return;
        }

        if (snapshots.Count < EmissionBoundsOnnxModel.WindowSize)
        {
            ResetScoreLatches();
            runtimeCache.Update(BuildStatus("warming_up", "数据预热", $"需要连续 {EmissionBoundsOnnxModel.WindowSize} 秒快照，当前已缓存 {snapshots.Count} 秒。", latest, model.ModelVersion,
                effectiveBurningStatus: latestGateSample.Eligible ? "active" : gate.EntrySeconds > 0 ? "warming_up" : "not_applicable"));
            return;
        }
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (snapshots[index].RecordedAt != snapshots[0].RecordedAt.AddSeconds(index))
            {
                ResetScoreLatches();
                gate.Reset();
                runtimeCache.Update(BuildStatus("data_invalid", "数据不连续", $"最近 {EmissionBoundsOnnxModel.WindowSize} 秒快照存在时间缺口，已暂停推理。", latest, model.ModelVersion,
                    effectiveBurningStatus: "data_invalid"));
                return;
            }
        }

        if (!gate.IsActive || !gate.IsWindowEligible(snapshots))
        {
            ResetScoreLatches();
            var gateStatus = gate.IsActive ? "transition" : gate.EntrySeconds > 0 ? "warming_up" : "not_applicable";
            var label = gateStatus switch
            {
                "transition" => "有效工况转段",
                "warming_up" => "有效工况确认中",
                _ => "未达有效工况"
            };
            var gateMessage = gateStatus switch
            {
                "transition" => "最近 60 秒含退出候选或转段数据；这些数据不参与推理，需重新积累完整有效窗口。",
                "warming_up" => $"天然气流量、鼓风机频率和电流需连续满足进入条件 {model.EffectiveBurningRule.EnterDwellSeconds} 秒后才开始推理（当前 {gate.EntrySeconds} 秒）。",
                _ => "阀位已判定运行，但当前过程参数未满足模型有效燃烧进入条件。"
            };
            runtimeCache.Update(BuildStatus(gateStatus, label, gateMessage, latest, model.ModelVersion,
                effectiveBurningStatus: gateStatus));
            return;
        }

        var inputWindow = new float[EmissionBoundsOnnxModel.WindowSize * EmissionBoundsOnnxModel.InputCount];
        for (var second = 0; second < snapshots.Count; second++)
        {
            for (var feature = 0; feature < InputDefinitions.Length; feature++)
            {
                var value = InputDefinitions[feature].ReadValue(snapshots[second]);
                if (value is null || !double.IsFinite(value.Value) || !float.IsFinite((float)value.Value))
                {
                    ResetScoreLatches();
                    runtimeCache.Update(BuildStatus("data_invalid", "输入数据缺失", $"最近 60 秒模型输入“{InputDefinitions[feature].Name}”存在空值或无效值，已拒绝推理。", latest, model.ModelVersion,
                        effectiveBurningStatus: "data_invalid"));
                    return;
                }
                inputWindow[second * InputDefinitions.Length + feature] = (float)value.Value;
            }
        }

        var prediction = model.Predict(inputWindow);
        var noxActual = FiniteOrNull(latest.GYDCS_K0HNE10CQ101);
        var o2Actual = FiniteOrNull(latest.GYDCS_K0HNE10CQ103);
        var noxStatus = GetTargetStatus(noxActual, prediction.NoxLower, prediction.NoxUpper);
        var o2Status = GetTargetStatus(o2Actual, prediction.O2Lower, prediction.O2Upper);
        var boundaryExceeded = noxStatus is "above" or "below" || o2Status is "above" or "below";
        var noxAboveScore = EvaluateScore("nox_above", "nox", noxActual, prediction.NoxLower, prediction.NoxUpper, above: true);
        var noxBelowScore = EvaluateScore("nox_below", "nox", noxActual, prediction.NoxLower, prediction.NoxUpper, above: false);
        var o2AboveScore = EvaluateScore("o2_above", "o2", o2Actual, prediction.O2Lower, prediction.O2Upper, above: true);
        var o2BelowScore = EvaluateScore("o2_below", "o2", o2Actual, prediction.O2Lower, prediction.O2Upper, above: false);
        double[]? sensitivities = model.EstimateInputContributions(inputWindow, prediction);
        var currentFlow = latest.GYDCS_K0QHH10CF101.GetValueOrDefault(double.NaN);
        var referenceGroup = model.FindHistoricalInputGroup(currentFlow);
        var inputDtos = InputDefinitions.Select((definition, index) =>
        {
            var currentValue = FiniteOrNull(definition.ReadValue(latest));
            var range = referenceGroup?.Parameters.GetValueOrDefault(definition.Field);
            var assessment = "当前值不可用";
            if (range is null)
            {
                assessment = "无匹配历史参考范围";
            }
            else if (currentValue is { } current)
            {
                assessment = current < range.P10
                    ? "建议上调"
                    : current > range.P90
                        ? "建议下调"
                        : "历史参考范围内";
            }
            return new EmissionBoundsInputDto
            {
                Field = definition.Field,
                PointName = definition.PointName,
                Name = definition.Name,
                Unit = definition.Unit,
                CurrentValue = currentValue,
                HistoricalP10 = range?.P10,
                HistoricalMedian = range?.Median,
                HistoricalP90 = range?.P90,
                SensitivityPercent = sensitivities is { Length: EmissionBoundsOnnxModel.InputCount } ? sensitivities[index] : null,
                Assessment = assessment
            };
        }).ToArray();

        var operatingRiskExceeded = noxActual < NoxOperatingRiskLowerLimit || o2Actual < O2OperatingRiskLowerLimit;
        var anyScoreThresholdReached = new[] { noxAboveScore, noxBelowScore, o2AboveScore, o2BelowScore }
            .Any(score => score.Status == "warning");
        var scoreWarning = ModelBoundaryAlertsEnabled && anyScoreThresholdReached;
        var shadowThresholdReached = IsShadowThresholdMode && anyScoreThresholdReached;
        var status = noxActual is null || o2Actual is null
            ? "target_missing"
            : operatingRiskExceeded || scoreWarning ? "out_of_range" : boundaryExceeded || shadowThresholdReached ? "score_pending" : "normal";
        var statusLabel = status switch
        {
            "target_missing" => "排放实测缺失",
            "out_of_range" when operatingRiskExceeded => "运行风险预警",
            "out_of_range" => "评分预警",
            "score_pending" when IsShadowThresholdMode && shadowThresholdReached => "影子阈值已达到",
            "score_pending" when IsShadowThresholdMode => "影子阈值观察",
            "score_pending" when alertThresholds is null => "试验模型边界观察",
            "score_pending" => "评分未达预警阈值",
            _ => "模型范围内"
        };
        var message = status switch
        {
            "target_missing" => "模型边界已计算，但 NOx 或 O₂ 分析仪实测值缺失，无法比较。",
            "out_of_range" when operatingRiskExceeded && scoreWarning => "运行风险下限或连续评分已触发预警。请结合现场规程核查；模型边界不代表法规限值，页面不会触发联锁或控制动作。",
            "out_of_range" when operatingRiskExceeded => "NOx 或 O₂ 实测值低于运行风险下限。请结合现场规程核查；页面不会触发联锁或控制动作。",
            "out_of_range" => "NOx 或 O₂ 连续评分达到预警阈值。评分为统计偏离量，不代表法规超标或故障；页面不会触发联锁或控制动作。",
            "score_pending" when IsShadowThresholdMode && shadowThresholdReached => "临时影子评分阈值已达到；当前仅用于观察与现场评估，不生成模型评分预警记录，也不触发控制动作。",
            "score_pending" when IsShadowThresholdMode => "实测值超出模型统计边界，正在按临时影子阈值观察；尚未达到连续确认条件，不生成模型评分预警记录。",
            "score_pending" when alertThresholds is null => "实测值超出试验模型边界；该模型独立测试覆盖率偏低，且没有匹配校准的评分阈值，仅供回放与联调观察，不生成模型评分预警记录。",
            "score_pending" => "实测值超出模型边界，但连续评分尚未达到预警阈值；继续观察。",
            _ => "NOx 与 O₂ 实测值均在模型统计范围内，且高于运行风险下限。模型边界不是法规限值。"
        };
        if (alertThresholds?.CalibrationStatus == "provisional" && status is "normal" or "score_pending")
        {
            message += " 当前评分阈值为历史数据校准候选值，尚需现场确认。";
        }
        var evaluatedAt = latest.RecordedAt;
        var evaluation = new EmissionBoundsEvaluationDto
        {
            Status = status,
            StatusLabel = statusLabel,
            ModelVersion = model.ModelVersion,
            EvaluatedAt = evaluatedAt,
            SnapshotTime = latest.RecordedAt,
            IsRunning = valveRunning,
            IsIgnited = latest.IsIgnited,
            EffectiveBurningStatus = "active",
            IsEffectiveBurning = true,
            ScoreCalibrationStatus = alertThresholds?.CalibrationStatus ?? "trial_only",
            Message = message,
            Overview = BuildOverview(latest),
            Inputs = inputDtos,
            Nox = new EmissionBoundsTargetDto
            {
                Name = "烟囱 NOx 排放浓度", Unit = "mg/m³", ActualValue = noxActual,
                LowerBound = prediction.NoxLower, UpperBound = prediction.NoxUpper, Status = noxStatus,
                OperatingRiskLowerLimit = NoxOperatingRiskLowerLimit,
                ScoreWidthFloor = alertThresholds?.WidthFloor.GetValueOrDefault("nox"),
                OperatingRiskStatus = GetOperatingRiskStatus(noxActual, NoxOperatingRiskLowerLimit),
                AboveScore = noxAboveScore.Score, AboveScoreThreshold = noxAboveScore.Threshold,
                AboveAlertStatus = noxAboveScore.Status, AboveConfirmSeconds = noxAboveScore.ConfirmSeconds,
                AboveRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0,
                BelowScore = noxBelowScore.Score, BelowScoreThreshold = noxBelowScore.Threshold,
                BelowAlertStatus = noxBelowScore.Status, BelowConfirmSeconds = noxBelowScore.ConfirmSeconds,
                BelowRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0
            },
            O2 = new EmissionBoundsTargetDto
            {
                Name = "烟囱 O₂ 排放浓度", Unit = "%", ActualValue = o2Actual,
                LowerBound = prediction.O2Lower, UpperBound = prediction.O2Upper, Status = o2Status,
                OperatingRiskLowerLimit = O2OperatingRiskLowerLimit,
                ScoreWidthFloor = alertThresholds?.WidthFloor.GetValueOrDefault("o2"),
                OperatingRiskStatus = GetOperatingRiskStatus(o2Actual, O2OperatingRiskLowerLimit),
                AboveScore = o2AboveScore.Score, AboveScoreThreshold = o2AboveScore.Threshold,
                AboveAlertStatus = o2AboveScore.Status, AboveConfirmSeconds = o2AboveScore.ConfirmSeconds,
                AboveRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0,
                BelowScore = o2BelowScore.Score, BelowScoreThreshold = o2BelowScore.Threshold,
                BelowAlertStatus = o2BelowScore.Status, BelowConfirmSeconds = o2BelowScore.ConfirmSeconds,
                BelowRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0
            },
            Trend = []
        };
        runtimeCache.Update(evaluation);

        var trend = new EmissionBoundsTrendDto
        {
            ModelArtifactId = modelArtifactId,
            RecordedAt = evaluatedAt,
            NoxActual = noxActual,
            NoxLower = prediction.NoxLower,
            NoxUpper = prediction.NoxUpper,
            O2Actual = o2Actual,
            O2Lower = prediction.O2Lower,
            O2Upper = prediction.O2Upper
        };
        PersistMinuteTrend(trend);
        PersistBoundaryEvents(evaluatedAt, noxActual, prediction.NoxLower, prediction.NoxUpper,
            noxAboveScore, noxBelowScore, o2Actual, prediction.O2Lower, prediction.O2Upper,
            o2AboveScore, o2BelowScore, cancellationToken);
    }

    private void PersistMinuteTrend(EmissionBoundsTrendDto trend)
    {
        var minute = new DateTime(trend.RecordedAt.Year, trend.RecordedAt.Month, trend.RecordedAt.Day,
            trend.RecordedAt.Hour, trend.RecordedAt.Minute, 0, trend.RecordedAt.Kind);
        if (lastPersistedMinute == minute)
        {
            return;
        }
        lastPersistedMinute = minute;
        try
        {
            store.SaveMinuteSample(trend);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存排放边界分钟趋势失败：{Time:yyyy-MM-dd HH:mm:ss}。", trend.RecordedAt);
        }
    }

    private void PersistBoundaryEvents(DateTime time, double? nox, double noxLower, double noxUpper,
        ScoreDecision noxAbove, ScoreDecision noxBelow, double? o2, double o2Lower, double o2Upper,
        ScoreDecision o2Above, ScoreDecision o2Below, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TrySaveScoredBoundaryEvent(time, "NOx", "偏高", nox, noxLower, noxUpper, "mg/m³", noxAbove);
        TrySaveScoredBoundaryEvent(time, "NOx", "偏低", nox, noxLower, noxUpper, "mg/m³", noxBelow);
        TrySaveScoredBoundaryEvent(time, "O₂", "偏高", o2, o2Lower, o2Upper, "%", o2Above);
        TrySaveScoredBoundaryEvent(time, "O₂", "偏低", o2, o2Lower, o2Upper, "%", o2Below);
        TrySaveOperatingRiskEvent(time, "NOx", nox, NoxOperatingRiskLowerLimit, noxLower, noxUpper, "mg/m³");
        TrySaveOperatingRiskEvent(time, "O₂", o2, O2OperatingRiskLowerLimit, o2Lower, o2Upper, "%");
    }

    private void TrySaveScoredBoundaryEvent(DateTime time, string metric, string direction, double? actual,
        double lower, double upper, string unit, ScoreDecision score)
    {
        if (!ModelBoundaryAlertsEnabled || actual is not { } value || score.Status != "warning"
            || score.Score is not { } scoreValue || score.Threshold is not { } threshold)
        {
            return;
        }
        var bound = direction == "偏高" ? upper : lower;
        SaveEventWithCooldown(time, metric, direction, "model_boundary", "warning", value, lower, upper, unit,
            $"{metric} 实测值{direction}于模型统计边界（{lower:0.###}–{upper:0.###} {unit}），连续评分 {scoreValue:0.###} 达到阈值 {threshold:0.###}（按区间宽度归一化），边界值 {bound:0.###} {unit}。这是工况预警，不代表法规超标或设备故障，也不触发控制动作。",
            scoreValue, threshold);
    }

    private void TrySaveOperatingRiskEvent(DateTime time, string metric, double? actual, double riskLowerLimit,
        double modelLower, double modelUpper, string unit)
    {
        if (actual is not { } value || value >= riskLowerLimit)
        {
            return;
        }

        SaveEventWithCooldown(time, metric, "低于运行风险下限", "operating_limit", "risk", value,
            riskLowerLimit, riskLowerLimit, unit,
            $"{metric} 实测值 {value:0.###} {unit} 低于运行风险下限 {riskLowerLimit:0.###} {unit}。请按现场规程核查；模型统计范围为 {modelLower:0.###}–{modelUpper:0.###} {unit}，此提示不触发控制动作。",
            null, null);
    }

    private void SaveEventWithCooldown(DateTime time, string metric, string direction, string source, string level,
        double value, double lower, double upper, string unit, string description, double? score, double? scoreThreshold)
    {
        var key = $"{modelArtifactId}:{source}:{metric}:{direction}:{level}";
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(options.EventCooldownMinutes, 1, 60));
        var previous = lastEventTimes.TryGetValue(key, out var inMemoryPrevious)
            ? inMemoryPrevious
            : store.FindLatestEventTime(metric, direction, source, level, modelArtifactId, time - cooldown, time);
        if (previous.HasValue && time - previous.Value < cooldown)
        {
            return;
        }
        var artifactPrefix = modelArtifactId is null ? "unknown" : modelArtifactId[..Math.Min(8, modelArtifactId.Length)];
        var id = $"{time:yyyyMMddHHmmss}_{artifactPrefix}_{metric}_{source}_{direction}";
        try
        {
            store.SaveEvent(new EmissionBoundsEventDto
            {
                Id = id,
                ModelArtifactId = modelArtifactId,
                RecordedAt = time,
                Metric = metric,
                Direction = direction,
                Source = source,
                Level = level,
                Score = score,
                ScoreThreshold = scoreThreshold,
                ActualValue = value,
                LowerBound = lower,
                UpperBound = upper,
                Unit = unit,
                Description = description
            });
            lastEventTimes[key] = time;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存排放预警记录失败：{Metric} {Source} {Direction}。", metric, source, direction);
        }
    }

    private ScoreDecision EvaluateScore(string scenario, string metricKey, double? actual, double lower, double upper, bool above)
    {
        if (actual is not { } value || !double.IsFinite(value)
            || !double.IsFinite(lower) || !double.IsFinite(upper))
        {
            scoreLatches[scenario].Reset();
            return ScoreDecision.Unavailable;
        }

        var widthFloor = alertThresholds?.WidthFloor.GetValueOrDefault(metricKey) ?? 1e-6;
        var width = Math.Max(upper - lower, widthFloor);
        var rawScore = above ? Math.Max(value - upper, 0) / width : Math.Max(lower - value, 0) / width;
        var score = double.IsFinite(rawScore) ? rawScore : 0;
        if (alertThresholds is null)
        {
            scoreLatches[scenario].Reset();
            return new ScoreDecision(score, null, "unavailable", 0);
        }

        var threshold = alertThresholds.Scenarios[scenario].Threshold;
        var state = scoreLatches[scenario].Advance(score, threshold,
            alertThresholds.ThresholdSelection.RequiredConsecutiveSeconds,
            alertThresholds.Recovery.ScoreRatio,
            alertThresholds.Recovery.ConsecutiveSeconds);
        return new ScoreDecision(score, threshold, state.Status, state.ConfirmSeconds);
    }

    private void ResetScoreLatches()
    {
        foreach (var latch in scoreLatches.Values)
        {
            latch.Reset();
        }
    }

    private EmissionBoundsEvaluationDto BuildStatus(string status, string label, string message,
        BoilerSnapshot latest, string modelVersion, bool snapshotExpired = false,
        string effectiveBurningStatus = "warming_up") => new()
    {
        Status = status,
        StatusLabel = label,
        ModelVersion = modelVersion,
        SnapshotTime = latest.RecordedAt,
        IsRunning = snapshotExpired ? null : IsValveRunning(latest),
        IsIgnited = snapshotExpired ? null : latest.IsIgnited,
        EffectiveBurningStatus = snapshotExpired ? "data_invalid" : effectiveBurningStatus,
        IsEffectiveBurning = !snapshotExpired && effectiveBurningStatus == "active",
        Message = message,
        Overview = snapshotExpired
            ? new EmissionBoundsOverviewDto { FlameText = "数据已过期" }
            : BuildOverview(latest),
        Inputs = InputDefinitions.Select(definition => new EmissionBoundsInputDto
        {
            Field = definition.Field,
            PointName = definition.PointName,
            Name = definition.Name,
            Unit = definition.Unit,
            CurrentValue = snapshotExpired ? null : FiniteOrNull(definition.ReadValue(latest)),
            Assessment = snapshotExpired ? "数据已过期" : "等待完整窗口推理"
        }).ToArray(),
        Nox = new EmissionBoundsTargetDto
        {
            Name = "烟囱 NOx 排放浓度", Unit = "mg/m³", ActualValue = snapshotExpired ? null : FiniteOrNull(latest.GYDCS_K0HNE10CQ101), Status = "unavailable",
            OperatingRiskLowerLimit = NoxOperatingRiskLowerLimit,
            ScoreWidthFloor = alertThresholds?.WidthFloor.GetValueOrDefault("nox"),
            OperatingRiskStatus = IsValveRunning(latest) && !snapshotExpired
                ? GetOperatingRiskStatus(FiniteOrNull(latest.GYDCS_K0HNE10CQ101), NoxOperatingRiskLowerLimit)
                : "unavailable",
            AboveScoreThreshold = alertThresholds?.Scenarios.GetValueOrDefault("nox_above")?.Threshold,
            BelowScoreThreshold = alertThresholds?.Scenarios.GetValueOrDefault("nox_below")?.Threshold,
            AboveRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0,
            BelowRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0,
            AboveAlertStatus = "unavailable", BelowAlertStatus = "unavailable"
        },
        O2 = new EmissionBoundsTargetDto
        {
            Name = "烟囱 O₂ 排放浓度", Unit = "%", ActualValue = snapshotExpired ? null : FiniteOrNull(latest.GYDCS_K0HNE10CQ103), Status = "unavailable",
            OperatingRiskLowerLimit = O2OperatingRiskLowerLimit,
            ScoreWidthFloor = alertThresholds?.WidthFloor.GetValueOrDefault("o2"),
            OperatingRiskStatus = IsValveRunning(latest) && !snapshotExpired
                ? GetOperatingRiskStatus(FiniteOrNull(latest.GYDCS_K0HNE10CQ103), O2OperatingRiskLowerLimit)
                : "unavailable",
            AboveScoreThreshold = alertThresholds?.Scenarios.GetValueOrDefault("o2_above")?.Threshold,
            BelowScoreThreshold = alertThresholds?.Scenarios.GetValueOrDefault("o2_below")?.Threshold,
            AboveRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0,
            BelowRequiredSeconds = alertThresholds?.ThresholdSelection.RequiredConsecutiveSeconds ?? 0,
            AboveAlertStatus = "unavailable", BelowAlertStatus = "unavailable"
        },
        ScoreCalibrationStatus = alertThresholds?.CalibrationStatus ?? "trial_only",
        Trend = []
    };

    private static EmissionBoundsOverviewDto BuildOverview(BoilerSnapshot snapshot) => new()
    {
        FlameText = snapshot.IsIgnited switch { true => "已点火", false => "未点火", _ => "未知" },
        SteamPressureMpa = FiniteOrNull(snapshot.GYDCS_K0QHB10CP101),
        OutletTemperatureC = FiniteOrNull(snapshot.GYDCS_K0QHB10CT302),
        SteamFlowCompensationTph = FiniteOrNull(snapshot.GYDCS_K0QHB10CF101_COMP)
    };

    private static bool IsValveRunning(BoilerSnapshot snapshot) =>
        snapshot.GYDCS_K0QHL10AA101AI5 is { } center && double.IsFinite(center) && center > 1.0
        || snapshot.GYDCS_K0QHL10AA101AI4 is { } outer && double.IsFinite(outer) && outer > 1.0;

    private static string BuildModelQualityMessage(EmissionBoundsOnnxModel model, AlertThresholdProfile? thresholds)
    {
        var noxCoverage = model.NoxTestCoverage is { } nox ? $"{nox * 100:0.0}%" : "未知";
        var o2Coverage = model.O2TestCoverage is { } o2 ? $"{o2 * 100:0.0}%" : "未知";
        var alertNote = thresholds?.CalibrationStatus switch
        {
            "shadow" => "已设置临时评分阈值 1.0（区间宽度倍数），仅作影子观察，不产生模型评分预警记录。",
            not null => "当前模型评分阈值文件与模型版本匹配。",
            _ => "未配置与该模型版本匹配的评分阈值，模型边界仅用于观察，不产生模型评分预警记录。"
        };
        return $"试验模型，仅供历史回放与联调，不作为正式生产预警依据。独立测试集区间覆盖率：NOx {noxCoverage}、O₂ {o2Coverage}。{alertNote}";
    }

    private bool IsShadowThresholdMode => string.Equals(alertThresholds?.CalibrationStatus, "shadow", StringComparison.OrdinalIgnoreCase);

    private bool ModelBoundaryAlertsEnabled => alertThresholds is not null && !IsShadowThresholdMode;

    private string ResolveModelDirectory()
    {
        if (Path.IsPathRooted(options.ModelDirectory))
        {
            return options.ModelDirectory;
        }
        var applicationPath = Path.Combine(AppContext.BaseDirectory, options.ModelDirectory);
        return Directory.Exists(applicationPath)
            ? applicationPath
            : Path.Combine(environment.ContentRootPath, options.ModelDirectory);
    }

    private static string GetModelArtifactId(string modelDirectory)
    {
        var modelPath = Path.Combine(modelDirectory, "best_model.onnx");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant();
    }

    private static AlertThresholdProfile LoadAlertThresholds(string modelDirectory, string modelVersion)
    {
        var modelPath = Path.Combine(modelDirectory, "best_model.onnx");
        var thresholdPath = Path.Combine(modelDirectory, "alert_thresholds.json");
        var profile = JsonSerializer.Deserialize<AlertThresholdProfile>(File.ReadAllText(thresholdPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("评分阈值文件为空或格式无效。");
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant();
        if (!string.Equals(profile.ModelSha256, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("评分阈值文件对应的 ONNX 模型哈希不匹配。");
        }
        if (!string.Equals(profile.ModelVersion, modelVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("评分阈值文件中的模型版本与当前模型不匹配。");
        }
        if (profile.ThresholdSelection.RequiredConsecutiveSeconds < 1 || profile.Recovery.ConsecutiveSeconds < 1
            || profile.Recovery.ScoreRatio is <= 0 or >= 1
            || !ValidPositive(profile.WidthFloor.GetValueOrDefault("nox"))
            || !ValidPositive(profile.WidthFloor.GetValueOrDefault("o2")))
        {
            throw new InvalidDataException("评分阈值文件中的确认、恢复或区间宽度参数无效。");
        }
        foreach (var name in new[] { "nox_above", "nox_below", "o2_above", "o2_below" })
        {
            if (!profile.Scenarios.TryGetValue(name, out var scenario) || !ValidPositive(scenario.Threshold))
            {
                throw new InvalidDataException($"评分阈值文件缺少有效场景 {name}。");
            }
        }
        return profile;
    }

    private static bool ValidPositive(double value) => double.IsFinite(value) && value > 0;

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static string GetTargetStatus(double? actual, double lower, double upper) => actual switch
    {
        null => "missing",
        { } value when value < lower => "below",
        { } value when value > upper => "above",
        _ => "normal"
    };

    private static string GetOperatingRiskStatus(double? actual, double lowerLimit) => actual switch
    {
        null => "missing",
        { } value when value < lowerLimit => "below",
        _ => "normal"
    };

    private sealed class AlertThresholdProfile
    {
        [JsonPropertyName("model_version")] public string ModelVersion { get; init; } = string.Empty;
        [JsonPropertyName("model_sha256")] public string ModelSha256 { get; init; } = string.Empty;
        [JsonPropertyName("calibration_status")] public string CalibrationStatus { get; init; } = "provisional";
        [JsonPropertyName("threshold_selection")] public ThresholdSelectionSettings ThresholdSelection { get; init; } = new();
        [JsonPropertyName("width_floor")] public Dictionary<string, double> WidthFloor { get; init; } = [];
        [JsonPropertyName("recovery")] public RecoverySettings Recovery { get; init; } = new();
        [JsonPropertyName("scenarios")] public Dictionary<string, ScenarioThreshold> Scenarios { get; init; } = [];
    }

    private sealed class ScenarioThreshold
    {
        [JsonPropertyName("threshold")] public double Threshold { get; init; }
    }

    private sealed class ThresholdSelectionSettings
    {
        [JsonPropertyName("required_consecutive_seconds")] public int RequiredConsecutiveSeconds { get; init; }
    }

    private sealed class RecoverySettings
    {
        [JsonPropertyName("score_ratio")] public double ScoreRatio { get; init; }
        [JsonPropertyName("consecutive_seconds")] public int ConsecutiveSeconds { get; init; }
    }

    private sealed record ScoreDecision(double? Score, double? Threshold, string Status, int ConfirmSeconds)
    {
        public static ScoreDecision Unavailable { get; } = new(null, null, "unavailable", 0);
    }

    private sealed class ScoreLatch
    {
        private int aboveSeconds;
        private int recoverySeconds;
        private bool active;

        public (string Status, int ConfirmSeconds) Advance(double score, double threshold, int requiredSeconds,
            double recoveryRatio, int requiredRecoverySeconds)
        {
            if (score >= threshold)
            {
                aboveSeconds++;
                recoverySeconds = 0;
                if (aboveSeconds >= requiredSeconds)
                {
                    active = true;
                }
            }
            else
            {
                aboveSeconds = 0;
                if (active)
                {
                    recoverySeconds = score <= threshold * recoveryRatio ? recoverySeconds + 1 : 0;
                    if (recoverySeconds >= requiredRecoverySeconds)
                    {
                        active = false;
                        recoverySeconds = 0;
                    }
                }
            }

            return (active ? "warning" : score >= threshold ? "pending" : "normal",
                active ? requiredSeconds : aboveSeconds);
        }

        public void Reset()
        {
            aboveSeconds = 0;
            recoverySeconds = 0;
            active = false;
        }
    }
}
