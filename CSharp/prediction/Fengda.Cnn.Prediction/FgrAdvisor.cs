namespace Fengda.Cnn.Prediction;

/// <summary>
/// FGR 建议风险等级。
/// </summary>
public enum FgrRecommendationLevel
{
    /// <summary>建议暂不可用。</summary>
    Unavailable,
    /// <summary>运行状态正常。</summary>
    Normal,
    /// <summary>需要关注。</summary>
    Watch,
    /// <summary>存在警告风险。</summary>
    Warning,
    /// <summary>存在危险风险。</summary>
    Danger,
}

/// <summary>
/// FGR 建议动作。
/// </summary>
public enum FgrRecommendationAction
{
    /// <summary>建议动作暂不可用。</summary>
    Unavailable,
    /// <summary>保持当前 FGR 状态。</summary>
    Hold,
    /// <summary>增加 FGR。</summary>
    Increase,
    /// <summary>降低 FGR。</summary>
    Decrease,
    /// <summary>禁止继续增加 FGR。</summary>
    BlockIncrease,
}

/// <summary>
/// FGR 建议规则参数。
/// </summary>
public sealed record FgrRecommendationOptions
{
    /// <summary>NOx 控制目标。</summary>
    public double NoxTargetMgM3 { get; init; } = 100.0;
    /// <summary>NOx 安全下限。</summary>
    public double NoxMinMgM3 { get; init; } = 10.0;
    /// <summary>NOx 控制死区。</summary>
    public double NoxDeadbandMgM3 { get; init; } = 3.0;
    /// <summary>NOx 严重超限裕量。</summary>
    public double NoxCriticalMarginMgM3 { get; init; } = 15.0;
    /// <summary>NOx 趋势关注阈值。</summary>
    public double NoxTrendWatchMgM3 { get; init; } = 3.0;
    /// <summary>O2 硬安全下限。</summary>
    public double O2HardMinPct { get; init; } = 3.0;
    /// <summary>O2 软安全下限。</summary>
    public double O2SoftMinPct { get; init; } = 3.5;
    /// <summary>允许继续压低 NOx 的 O2 裕量。</summary>
    public double O2OptimizationReservePct { get; init; } = 4.2;
    /// <summary>NO2 上限。</summary>
    public double No2MaxMgM3 { get; init; } = 50.0;
    /// <summary>FGR 阀位建议上限。</summary>
    public double FgrValveMaxPct { get; init; } = 65.0;
    /// <summary>FGR 风机频率建议上限。</summary>
    public double FgrFanMaxHz { get; init; } = 40.0;
    /// <summary>FGR 阀位小幅调整步长。</summary>
    public double ValveSmallStepPct { get; init; } = 2.0;
    /// <summary>FGR 阀位大幅调整步长。</summary>
    public double ValveLargeStepPct { get; init; } = 5.0;
    /// <summary>FGR 风机频率小幅调整步长。</summary>
    public double FanSmallStepHz { get; init; } = 1.0;
    /// <summary>FGR 风机频率大幅调整步长。</summary>
    public double FanLargeStepHz { get; init; } = 2.0;
}

/// <summary>
/// 预测风险关键值及其发生时间。
/// </summary>
/// <param name="Value">风险关键值。</param>
/// <param name="Timestamp">风险关键值发生时间。</param>
public sealed record FgrForecastMetric(
    double Value,
    DateTime Timestamp);

/// <summary>
/// FGR 建议使用的未来风险汇总。
/// </summary>
/// <param name="NoxMin">未来 NOx 最低值。</param>
/// <param name="NoxPeak">未来 NOx 峰值。</param>
/// <param name="NoxMean">未来 NOx 平均值。</param>
/// <param name="NoxTrend">未来 NOx 趋势变化量。</param>
/// <param name="O2Min">未来 O2 最低值。</param>
/// <param name="No2Max">未来 NO2 峰值。</param>
public sealed record FgrRiskSummary(
    FgrForecastMetric NoxMin,
    FgrForecastMetric NoxPeak,
    double NoxMean,
    double NoxTrend,
    FgrForecastMetric O2Min,
    FgrForecastMetric No2Max);

/// <summary>
/// 基于未来预测结果生成的 FGR 建议。
/// </summary>
/// <param name="Level">建议风险等级。</param>
/// <param name="Action">建议调节动作。</param>
/// <param name="Title">建议标题。</param>
/// <param name="Detail">建议详细说明。</param>
/// <param name="RiskSummary">未来预测风险汇总。</param>
/// <param name="CurrentValvePct">当前 FGR 阀位百分比。</param>
/// <param name="CurrentFanHz">当前 FGR 风机频率。</param>
/// <param name="SuggestedValvePct">建议 FGR 阀位百分比。</param>
/// <param name="SuggestedFanHz">建议 FGR 风机频率。</param>
public sealed record FgrRecommendation(
    FgrRecommendationLevel Level,
    FgrRecommendationAction Action,
    string Title,
    string Detail,
    FgrRiskSummary? RiskSummary,
    double? CurrentValvePct,
    double? CurrentFanHz,
    double? SuggestedValvePct,
    double? SuggestedFanHz);

/// <summary>
/// 基于未来 NOx、O2 和 NO2 预测曲线生成 FGR 调节建议。
/// </summary>
public sealed class FgrAdvisor
{
    private const double PhysicalMinValvePct = 0.0;
    private const double PhysicalMinFanHz = 0.0;
    private const string NoxPointName = "K0HNE10CQ101.UNIT3@NET3";
    private const string O2PointName = "K0HNE10CQ103.UNIT3@NET3";
    private const string No2PointName = "K0HNE10CQ105.UNIT3@NET3";
    public const string FgrValvePointName = "K0QHL10AA101AI6.UNIT3@NET3";
    public const string FgrFanPointName = "K0QHH10AV001P.UNIT3@NET3";

    private readonly FgrRecommendationOptions options;

    /// <summary>
    /// 创建 FGR 建议器。
    /// </summary>
    public FgrAdvisor(FgrRecommendationOptions? options = null)
    {
        this.options = options ?? new FgrRecommendationOptions();
    }

    /// <summary>
    /// 根据预测结果和当前 FGR 状态生成建议。
    /// </summary>
    public FgrRecommendation Recommend(
        ForecastResult? forecast,
        double? currentValve,
        double? currentFan)
    {
        if (forecast is null)
        {
            return Unavailable("预测结果不可用，暂不生成 FGR 建议。", currentValve, currentFan);
        }

        if (!TryBuildRiskSummary(forecast, out var risk, out var missingReason))
        {
            return Unavailable(missingReason, currentValve, currentFan);
        }

        if (!currentValve.HasValue || !currentFan.HasValue)
        {
            return new FgrRecommendation(
                FgrRecommendationLevel.Unavailable,
                FgrRecommendationAction.Unavailable,
                "FGR建议不可用",
                BuildDetail(risk, currentValve, currentFan, null, null, "当前数据缺少 FGR 阀位或 FGR 风机频率，无法计算建议目标。"),
                risk,
                currentValve,
                currentFan,
                null,
                null);
        }

        if (currentValve.Value > options.FgrValveMaxPct || currentFan.Value > options.FgrFanMaxHz)
        {
            var targetValve = Math.Min(currentValve.Value, options.FgrValveMaxPct);
            var targetFan = Math.Min(currentFan.Value, options.FgrFanMaxHz);
            return CreateRecommendation(
                FgrRecommendationLevel.Warning,
                FgrRecommendationAction.Decrease,
                "FGR高于建议运行上限",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                "当前 FGR 已超过正常运行上限，先回到建议边界内，再继续观察 NOx/O2 响应。");
        }

        if (risk.O2Min.Value < options.O2HardMinPct)
        {
            if (!CanDecrease(currentValve.Value, currentFan.Value))
            {
                return CreateRecommendation(
                    FgrRecommendationLevel.Danger,
                    FgrRecommendationAction.BlockIncrease,
                    "O2低限风险，禁止增加FGR",
                    risk,
                    currentValve,
                    currentFan,
                    currentValve,
                    currentFan,
                    $"未来 O2 最低值低于 {options.O2HardMinPct:0.#}%，且当前 FGR 不具备继续降低空间，禁止增加 FGR。");
            }

            var (targetValve, targetFan) = ApplyStep(currentValve.Value, currentFan.Value, -options.ValveLargeStepPct, -options.FanLargeStepHz);
            return CreateRecommendation(
                FgrRecommendationLevel.Danger,
                FgrRecommendationAction.Decrease,
                "O2低限风险，降低FGR",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                $"未来 O2 最低值低于 {options.O2HardMinPct:0.#}%，继续增加 FGR 不安全，优先恢复燃烧裕量。");
        }

        if (risk.NoxMin.Value < options.NoxMinMgM3)
        {
            var (targetValve, targetFan) = CanDecrease(currentValve.Value, currentFan.Value)
                ? ApplyStep(currentValve.Value, currentFan.Value, -options.ValveSmallStepPct, -options.FanSmallStepHz)
                : (currentValve.Value, currentFan.Value);
            return CreateRecommendation(
                FgrRecommendationLevel.Danger,
                FgrRecommendationAction.BlockIncrease,
                "NOx过低风险，暂缓增加FGR",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                $"未来 NOx 最低值低于 {options.NoxMinMgM3:0.#}mg/m³，按运行经验存在燃烧风险，先不要继续压低 NOx。");
        }

        if (risk.O2Min.Value < options.O2SoftMinPct)
        {
            var (targetValve, targetFan) = CanDecrease(currentValve.Value, currentFan.Value)
                ? ApplyStep(currentValve.Value, currentFan.Value, -options.ValveSmallStepPct, -options.FanSmallStepHz)
                : (currentValve.Value, currentFan.Value);
            return CreateRecommendation(
                FgrRecommendationLevel.Warning,
                FgrRecommendationAction.BlockIncrease,
                "O2裕量偏低，暂缓增加FGR",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                $"未来 O2 已接近 {options.O2HardMinPct:0.#}% 安全下限，当前目标是保住燃烧稳定性。");
        }

        if (risk.No2Max.Value > options.No2MaxMgM3 && CanIncrease(currentValve.Value, currentFan.Value))
        {
            var (targetValve, targetFan) = ApplyStep(currentValve.Value, currentFan.Value, options.ValveSmallStepPct, options.FanSmallStepHz);
            return CreateRecommendation(
                FgrRecommendationLevel.Warning,
                FgrRecommendationAction.Increase,
                "NO2偏高，小幅增加FGR",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                $"未来 NO2 峰值超过 {options.No2MaxMgM3:0.#}mg/m³，且 O2 仍有裕量，建议小幅增加 FGR。");
        }

        if (ShouldIncreaseForNox(risk))
        {
            if (!CanIncrease(currentValve.Value, currentFan.Value))
            {
                return CreateRecommendation(
                    FgrRecommendationLevel.Warning,
                    FgrRecommendationAction.Hold,
                    "NOx偏高但FGR已接近上限",
                    risk,
                    currentValve,
                    currentFan,
                    currentValve,
                    currentFan,
                    "FGR 阀位或风机频率已接近建议上限，暂不继续增加，建议人工确认其它燃烧参数。");
            }

            var isCritical = risk.NoxPeak.Value >= options.NoxTargetMgM3 + options.NoxCriticalMarginMgM3;
            var valveStep = isCritical ? options.ValveLargeStepPct : options.ValveSmallStepPct;
            var fanStep = isCritical ? options.FanLargeStepHz : options.FanSmallStepHz;
            var (targetValve, targetFan) = ApplyStep(currentValve.Value, currentFan.Value, valveStep, fanStep);
            return CreateRecommendation(
                isCritical ? FgrRecommendationLevel.Warning : FgrRecommendationLevel.Watch,
                FgrRecommendationAction.Increase,
                isCritical ? "NOx峰值偏高，增加FGR" : "NOx有上升风险，小幅增加FGR",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                "未来 NOx 峰值或上升趋势超过当前控制带，且 O2 未触及安全下限，建议在约束内增加 FGR。");
        }

        if (risk.O2Min.Value >= options.O2OptimizationReservePct && CanIncrease(currentValve.Value, currentFan.Value))
        {
            var (targetValve, targetFan) = ApplyStep(currentValve.Value, currentFan.Value, options.ValveSmallStepPct, options.FanSmallStepHz);
            return CreateRecommendation(
                FgrRecommendationLevel.Watch,
                FgrRecommendationAction.Increase,
                "O2裕量充足，可小幅压低NOx",
                risk,
                currentValve,
                currentFan,
                targetValve,
                targetFan,
                $"未来 O2 最低值高于 {options.O2OptimizationReservePct:0.#}%，仍有安全裕量，可小幅增加 FGR 以继续压低 NOx。");
        }

        return CreateRecommendation(
            FgrRecommendationLevel.Normal,
            FgrRecommendationAction.Hold,
            "保持当前FGR",
            risk,
            currentValve,
            currentFan,
            currentValve,
            currentFan,
            "未来 NOx、O2、NO2 均处于约束内，建议保持当前 FGR 并继续滚动观察。");
    }

    private bool ShouldIncreaseForNox(FgrRiskSummary risk)
    {
        return risk.NoxPeak.Value > options.NoxTargetMgM3 + options.NoxDeadbandMgM3
            || risk.NoxTrend > options.NoxTrendWatchMgM3;
    }

    private bool CanIncrease(double currentValve, double currentFan)
    {
        return currentValve < options.FgrValveMaxPct - 0.1
            && currentFan < options.FgrFanMaxHz - 0.1;
    }

    private bool CanDecrease(double currentValve, double currentFan)
    {
        return currentValve > PhysicalMinValvePct + 0.1
            || currentFan > PhysicalMinFanHz + 0.1;
    }

    private (double Valve, double Fan) ApplyStep(double currentValve, double currentFan, double valveDelta, double fanDelta)
    {
        var valve = Math.Clamp(currentValve + valveDelta, PhysicalMinValvePct, options.FgrValveMaxPct);
        var fan = Math.Clamp(currentFan + fanDelta, PhysicalMinFanHz, options.FgrFanMaxHz);
        return (valve, fan);
    }

    private FgrRecommendation CreateRecommendation(
        FgrRecommendationLevel level,
        FgrRecommendationAction action,
        string title,
        FgrRiskSummary risk,
        double? currentValve,
        double? currentFan,
        double? suggestedValve,
        double? suggestedFan,
        string reason)
    {
        return new FgrRecommendation(
            level,
            action,
            title,
            BuildDetail(risk, currentValve, currentFan, suggestedValve, suggestedFan, reason),
            risk,
            currentValve,
            currentFan,
            suggestedValve,
            suggestedFan);
    }

    private FgrRecommendation Unavailable(string reason, double? currentValve, double? currentFan)
    {
        return new FgrRecommendation(
            FgrRecommendationLevel.Unavailable,
            FgrRecommendationAction.Unavailable,
            "FGR建议不可用",
            reason,
            null,
            currentValve,
            currentFan,
            null,
            null);
    }

    private string BuildDetail(
        FgrRiskSummary risk,
        double? currentValve,
        double? currentFan,
        double? suggestedValve,
        double? suggestedFan,
        string reason)
    {
        var currentText = $"当前FGR：阀位 {FormatNullable(currentValve, "%")}，风机 {FormatNullable(currentFan, "Hz")}";
        var targetText = suggestedValve.HasValue && suggestedFan.HasValue
            ? $"建议目标：阀位 {FormatNullable(suggestedValve, "%")}，风机 {FormatNullable(suggestedFan, "Hz")}"
            : "建议目标：暂无";
        return
            $"未来60秒：NOx范围 {risk.NoxMin.Value:0.##}-{risk.NoxPeak.Value:0.##}mg/m³，" +
            $"峰值 @ {risk.NoxPeak.Timestamp:HH:mm:ss}，" +
            $"均值 {risk.NoxMean:0.##}mg/m³，趋势 {risk.NoxTrend:+0.##;-0.##;0}mg/m³；\r\n" +
            $"O2最低 {risk.O2Min.Value:0.###}% @ {risk.O2Min.Timestamp:HH:mm:ss}；" +
            $"NO2峰值 {risk.No2Max.Value:0.##}mg/m³ @ {risk.No2Max.Timestamp:HH:mm:ss}。\r\n" +
            $"{currentText}\r\n" +
            $"{targetText}\r\n" +
            $"依据：{reason}\r\n" +
            "说明：第一版为规则建议，未模拟调整后的二次预测曲线。";
    }

    private static string FormatNullable(double? value, string unit)
    {
        return value.HasValue ? $"{value.Value:0.##}{unit}" : "--";
    }

    private static bool TryBuildRiskSummary(ForecastResult forecast, out FgrRiskSummary risk, out string missingReason)
    {
        risk = null!;
        missingReason = string.Empty;

        var noxPoints = GetFinitePoints(forecast, NoxPointName);
        var o2Points = GetFinitePoints(forecast, O2PointName);
        var no2Points = GetFinitePoints(forecast, No2PointName);

        if (noxPoints.Count == 0)
        {
            missingReason = "预测结果中缺少 NOx 曲线，暂不生成 FGR 建议。";
            return false;
        }

        if (o2Points.Count == 0)
        {
            missingReason = "预测结果中缺少 O2 曲线，暂不生成 FGR 建议。";
            return false;
        }

        if (no2Points.Count == 0)
        {
            missingReason = "预测结果中缺少 NO2 曲线，暂不生成 FGR 建议。";
            return false;
        }

        var noxMin = noxPoints.MinBy(point => point.Value)!;
        var noxPeak = noxPoints.MaxBy(point => point.Value)!;
        var o2Min = o2Points.MinBy(point => point.Value)!;
        var no2Max = no2Points.MaxBy(point => point.Value)!;
        risk = new FgrRiskSummary(
            new FgrForecastMetric(noxMin.Value, noxMin.Timestamp),
            new FgrForecastMetric(noxPeak.Value, noxPeak.Timestamp),
            noxPoints.Average(point => point.Value),
            CalculateTrend(noxPoints),
            new FgrForecastMetric(o2Min.Value, o2Min.Timestamp),
            new FgrForecastMetric(no2Max.Value, no2Max.Timestamp));
        return true;
    }

    private static List<TimeValue> GetFinitePoints(ForecastResult forecast, string pointName)
    {
        return forecast.FindSeries(pointName)?.Points
            .Where(point => double.IsFinite(point.Value))
            .ToList() ?? [];
    }

    private static double CalculateTrend(IReadOnlyList<TimeValue> points)
    {
        if (points.Count < 2)
        {
            return 0.0;
        }

        var window = Math.Min(10, points.Count);
        var head = points.Take(window).Average(point => point.Value);
        var tail = points.Skip(points.Count - window).Average(point => point.Value);
        return tail - head;
    }
}
