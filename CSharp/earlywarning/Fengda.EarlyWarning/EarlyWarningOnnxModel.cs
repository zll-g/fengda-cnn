using System.Text.Json;
using Fengda.Boiler.Monitoring;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Fengda.EarlyWarning;

/// <summary>
/// 已发布 LSTM-AE 模型的 25 个过程测点定义。
/// </summary>
public static class EarlyWarningPointRegistry
{
    /// <summary>按模型清单冻结顺序排列的过程测点。</summary>
    public static IReadOnlyList<EarlyWarningPoint> Points { get; } =
    [
        new("burner_gas_leak_pressure", "燃烧器燃气检漏压力", "kPa", x => x.GYDCS_K0QHL10AA101AI1),
        new("burner_gas_inlet_pressure", "燃烧器气阀组入口压力", "kPa", x => x.GYDCS_K0QHL10AA101AI2),
        new("burner_air_damper_pct", "燃烧器风门调节阀阀位反馈", "%", x => x.GYDCS_K0QHL10AA101AI3),
        new("burner_outer_gas_valve_pct", "燃烧器外环气调节阀阀位反馈", "%", x => x.GYDCS_K0QHL10AA101AI4),
        new("burner_center_gas_valve_pct", "燃烧器中心气调节阀阀位反馈", "%", x => x.GYDCS_K0QHL10AA101AI5),
        new("fgr_valve_pct", "FGR 调节阀阀位反馈", "%", x => x.GYDCS_K0QHL10AA101AI6),
        new("stack_nox_mg_m3", "烟囱 NOx 排放浓度", "mg/m³", x => x.GYDCS_K0HNE10CQ101),
        new("stack_so2_mg_m3", "烟囱 SO2 排放浓度", "mg/m³", x => x.GYDCS_K0HNE10CQ102),
        new("stack_o2_pct", "烟囱 O2 排放浓度", "%", x => x.GYDCS_K0HNE10CQ103),
        new("stack_dust_mg_m3", "烟囱粉尘排放浓度", "mg/m³", x => x.GYDCS_K0HNE10CQ104),
        new("stack_no2_mg_m3", "烟囱 NO2 排放浓度", "mg/m³", x => x.GYDCS_K0HNE10CQ105),
        new("stack_gas_flow_m3_h", "烟囱烟气流量", "m³/h", x => x.GYDCS_K0HNE10CF101_CAL),
        new("stack_humidity_pct", "烟囱湿度", "%", x => x.GYDCS_K0HNE10CM101),
        new("stack_pressure_kpa", "烟囱压力", "kPa", x => x.GYDCS_K0HNE10CP101),
        new("stack_gas_temperature_c", "烟囱烟气温度", "℃", x => x.GYDCS_K0HNE10CT101),
        new("furnace_exit_pressure_kpa", "炉膛出口烟气压力", "kPa", x => x.GYDCS_K0QHA10CP101),
        new("furnace_exit_temperature_c", "炉膛出口烟气温度", "℃", x => x.GYDCS_K0QHA10CT302),
        new("boiler_load", "实际负荷", "t/h", x => x.GYDCS_K0QHB10CF101_COMP1),
        new("superheated_steam_pressure_mpa", "过热蒸汽压力", "MPa", x => x.GYDCS_K0QHB10CP101),
        new("desuperheater_outlet_temperature_c", "减温器出口温度", "℃", x => x.GYDCS_K0QHB10CT302),
        new("fgr_fan_frequency_hz", "FGR 风机变频器频率反馈", "Hz", x => x.GYDCS_K0QHH10AV001P),
        new("natural_gas_inlet_flow_m3_h", "天然气入口流量", "m³/h", x => x.GYDCS_K0QHH10CF101),
        new("ignition_solenoid_active", "点火电磁阀", "状态", x => x.GYDCS_K0QHL10AA101DO2),
        new("blower_current_a", "鼓风机变频器电流", "A", x => x.GYDCS_K0QHL10AN001I),
        new("blower_frequency_hz", "鼓风机变频器频率反馈", "Hz", x => x.GYDCS_K0QHL10AN001P),
    ];

    /// <summary>按字段标识读取测点定义。</summary>
    public static bool TryGet(string field, out EarlyWarningPoint point)
    {
        point = Points.FirstOrDefault(item => string.Equals(item.Field, field, StringComparison.Ordinal))!;
        return point is not null;
    }
}

/// <summary>预警模型过程测点。</summary>
public sealed record EarlyWarningPoint(string Field, string Name, string Unit, Func<BoilerSnapshot, double?> GetValue);

/// <summary>预警场景等级。</summary>
public static class EarlyWarningLevels
{
    /// <summary>正常。</summary>
    public const string Normal = "normal";

    /// <summary>二级预警。</summary>
    public const string Warning = "warning";

    /// <summary>三级报警。</summary>
    public const string Alarm = "alarm";

    /// <summary>停机未评分。</summary>
    public const string Stopped = "stopped";

    /// <summary>数据不可用未评分。</summary>
    public const string DataUnavailable = "data_unavailable";

    /// <summary>模型不可用未评分。</summary>
    public const string ModelUnavailable = "model_unavailable";
}

/// <summary>预警场景定义。</summary>
public sealed record EarlyWarningScenarioDefinition(
    string Key,
    string Name,
    string TargetField,
    IReadOnlyList<string> RelatedFields,
    IReadOnlyList<string> Recommendations);

/// <summary>模型预警关联测点结果。</summary>
public sealed record EarlyWarningContributor(
    string Field,
    string Name,
    string Unit,
    double Actual,
    double Reconstructed,
    double Contribution,
    string Direction);

/// <summary>一个预警场景的实时评分结果。</summary>
public sealed record EarlyWarningScenarioResult(
    string Key,
    string Name,
    string TargetName,
    string Unit,
    string Level,
    string StatusReason,
    double? Score,
    double? Threshold,
    double? ScoreMultiple,
    double? Actual,
    double? Reconstructed,
    double? Residual,
    double? Trend,
    IReadOnlyList<double> ActualTrend,
    IReadOnlyList<double> ReconstructedTrend,
    IReadOnlyList<EarlyWarningContributor> Contributors,
    IReadOnlyList<string> Recommendations);

/// <summary>一次 60 秒窗口的三场景预警结果。</summary>
public sealed record EarlyWarningEvaluation(
    DateTime WindowStart,
    DateTime WindowEnd,
    string ModelVersion,
    string RunState,
    string Status,
    string Message,
    IReadOnlyList<EarlyWarningScenarioResult> Scenarios);

/// <summary>
/// 使用正式 LSTM-AE ONNX 模型执行三场景异常重构和评分。
/// 当前发布模型采用单工况回退模式，固定传入 condition_id=0。
/// </summary>
public sealed class EarlyWarningOnnxModel : IDisposable
{
    private const int WindowSize = 60;
    private const int MaskSteps = 10;
    private const int TrendSteps = 16;
    private const int ProcessFeatureCount = 25;
    private static readonly IReadOnlyList<EarlyWarningScenarioDefinition> ScenarioDefinitions =
    [
        new(
            "nox",
            "NOx 排放浓度异常升高",
            "stack_nox_mg_m3",
            ["fgr_valve_pct", "fgr_fan_frequency_hz", "burner_center_gas_valve_pct", "stack_o2_pct"],
            ["核查 FGR、燃料与风量的协同响应。", "核查关联执行机构与设定值、联锁状态是否一致。", "持续异常或升级报警时，通知运行人员按规程处置。"]),
        new(
            "temperature",
            "减温器出口蒸汽温度异常升高",
            "desuperheater_outlet_temperature_c",
            ["furnace_exit_temperature_c", "natural_gas_inlet_flow_m3_h", "fgr_valve_pct", "stack_gas_temperature_c"],
            ["核查减温系统和相关执行机构响应。", "核查炉膛热负荷、燃气流量与 FGR 调节状态。", "温升持续时，按温度保护规程处置。"]),
        new(
            "pressure",
            "过热蒸汽压力异常升高",
            "superheated_steam_pressure_mpa",
            ["boiler_load", "natural_gas_inlet_flow_m3_h", "burner_outer_gas_valve_pct", "furnace_exit_pressure_kpa"],
            ["核查产汽量与用汽需求是否匹配。", "核查压力调节、稳压和泄压相关状态。", "压力持续升高时，立即按压力保护规程处置。"]),
    ];

    /// <summary>按场景标识获取目标测点及关联测点定义。</summary>
    public static bool TryGetScenarioDefinition(string key, out EarlyWarningScenarioDefinition definition)
    {
        definition = ScenarioDefinitions.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))!;
        return definition is not null;
    }

    private readonly InferenceSession session;
    private readonly float[] mean;
    private readonly float[] std;
    private readonly IReadOnlyDictionary<string, int> targetIndexes;
    private readonly IReadOnlyDictionary<string, double> thresholds;

    private EarlyWarningOnnxModel(
        InferenceSession session,
        float[] mean,
        float[] std,
        IReadOnlyDictionary<string, int> targetIndexes,
        IReadOnlyDictionary<string, double> thresholds,
        double alarmMultiplier,
        string modelVersion)
    {
        this.session = session;
        this.mean = mean;
        this.std = std;
        this.targetIndexes = targetIndexes;
        this.thresholds = thresholds;
        AlarmMultiplier = alarmMultiplier;
        ModelVersion = modelVersion;
    }

    /// <summary>加载成功的模型版本。</summary>
    public string ModelVersion { get; }

    /// <summary>三级报警阈值倍率。</summary>
    public double AlarmMultiplier { get; }

    /// <summary>三类正式预警场景。</summary>
    public IReadOnlyList<EarlyWarningScenarioDefinition> Scenarios => ScenarioDefinitions;

    /// <summary>创建模型尚未加载、停机或数据不可用时的三场景状态结果。</summary>
    public static EarlyWarningEvaluation CreateStatusEvaluation(DateTime time, string modelVersion, string status, string message)
    {
        var scenarios = ScenarioDefinitions.Select(scenario =>
        {
            var point = EarlyWarningPointRegistry.Points.First(item => item.Field == scenario.TargetField);
            return new EarlyWarningScenarioResult(
                scenario.Key, scenario.Name, point.Name, point.Unit, status, message,
                null, null, null, null, null, null, null, [], [], [], scenario.Recommendations);
        }).ToList();
        return new EarlyWarningEvaluation(time, time, modelVersion, status == EarlyWarningLevels.Stopped ? "stopped" : "unavailable", status, message, scenarios);
    }

    /// <summary>从模型目录加载 ONNX 及其冻结配置。</summary>
    public static EarlyWarningOnnxModel Load(string modelDirectory)
    {
        var fullDirectory = Path.GetFullPath(modelDirectory);
        var onnxPath = Path.Combine(fullDirectory, "best_model.onnx");
        var manifestPath = Path.Combine(fullDirectory, "manifest.json");
        var normalizationPath = Path.Combine(fullDirectory, "normalization.json");
        var thresholdsPath = Path.Combine(fullDirectory, "inference_thresholds.json");
        foreach (var path in new[] { onnxPath, manifestPath, normalizationPath, thresholdsPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"缺少预警模型文件：{path}", path);
            }
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var version = manifest.RootElement.GetProperty("model_version").GetString() ?? "unknown";
        var features = ReadStrings(manifest.RootElement.GetProperty("process_feature_columns"));
        if (features.Count != ProcessFeatureCount || !features.SequenceEqual(EarlyWarningPointRegistry.Points.Select(point => point.Field)))
        {
            throw new InvalidDataException("预警模型 25 测点契约与实时快照映射不一致。");
        }
        if (manifest.RootElement.GetProperty("config").GetProperty("window_size").GetInt32() != WindowSize)
        {
            throw new InvalidDataException("当前预警服务仅支持 60 秒窗口模型。");
        }

        using var normalization = JsonDocument.Parse(File.ReadAllText(normalizationPath));
        if (!features.SequenceEqual(ReadStrings(normalization.RootElement.GetProperty("feature_columns"))))
        {
            throw new InvalidDataException("模型特征清单与标准化参数不一致。");
        }
        var mean = ReadFloats(normalization.RootElement.GetProperty("mean"));
        var std = ReadFloats(normalization.RootElement.GetProperty("std"));
        if (mean.Length != ProcessFeatureCount || std.Length != ProcessFeatureCount || std.Any(value => !float.IsFinite(value) || value <= 0))
        {
            throw new InvalidDataException("预警模型标准化参数无效。");
        }

        using var thresholdsDocument = JsonDocument.Parse(File.ReadAllText(thresholdsPath));
        var thresholdRoot = thresholdsDocument.RootElement;
        var thresholds = ScenarioDefinitions.ToDictionary(
            scenario => scenario.TargetField,
            scenario => thresholdRoot.GetProperty("thresholds").GetProperty(scenario.TargetField).GetDouble(),
            StringComparer.Ordinal);
        if (thresholds.Values.Any(value => !double.IsFinite(value) || value <= 0))
        {
            throw new InvalidDataException("预警模型评分阈值无效。");
        }
        var alarmMultiplier = thresholdRoot.GetProperty("alarm_multiplier").GetDouble();
        if (!double.IsFinite(alarmMultiplier) || alarmMultiplier <= 1)
        {
            throw new InvalidDataException("三级报警倍率必须大于 1。");
        }

        var indexes = ScenarioDefinitions.ToDictionary(
            scenario => scenario.TargetField,
            scenario => features.IndexOf(scenario.TargetField),
            StringComparer.Ordinal);
        if (indexes.Values.Any(index => index < 0))
        {
            throw new InvalidDataException("预警模型未包含全部目标测点。");
        }

        return new EarlyWarningOnnxModel(new InferenceSession(onnxPath), mean, std, indexes, thresholds, alarmMultiplier, version);
    }

    /// <summary>对一个连续完整的 60 秒快照窗口执行三场景预警推理。</summary>
    public EarlyWarningEvaluation Evaluate(IReadOnlyList<BoilerSnapshot> snapshots)
    {
        var now = snapshots.LastOrDefault()?.RecordedAt ?? BoilerTime.ChinaNow;
        if (!TryBuildWindow(snapshots, out var rawWindow, out var reason))
        {
            return CreateUnavailableEvaluation(snapshots, EarlyWarningLevels.DataUnavailable, reason);
        }
        if (snapshots.Any(snapshot => snapshot.IsIgnited is null))
        {
            return CreateUnavailableEvaluation(snapshots, EarlyWarningLevels.DataUnavailable, "火焰信号缺失，无法判断连续窗口内的锅炉运行状态。");
        }
        if (snapshots.Any(snapshot => snapshot.IsIgnited is false))
        {
            return CreateUnavailableEvaluation(snapshots, EarlyWarningLevels.Stopped, "连续 60 秒窗口包含停机状态，未执行模型评分。");
        }

        var results = ScenarioDefinitions.Select(scenario => InferScenario(rawWindow, scenario)).ToList();
        return new EarlyWarningEvaluation(
            snapshots[0].RecordedAt,
            snapshots[^1].RecordedAt,
            ModelVersion,
            "running",
            "scored",
            $"已使用模型 {ModelVersion} 完成三场景评分。",
            results);
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();

    private EarlyWarningScenarioResult InferScenario(float[] rawWindow, EarlyWarningScenarioDefinition scenario)
    {
        var targetIndex = targetIndexes[scenario.TargetField];
        var threshold = thresholds[scenario.TargetField];
        try
        {
            var input = new DenseTensor<float>(new[] { 1, WindowSize, ProcessFeatureCount + ScenarioDefinitions.Count });
            for (var time = 0; time < WindowSize; time++)
            {
                for (var feature = 0; feature < ProcessFeatureCount; feature++)
                {
                    var standardized = (rawWindow[time * ProcessFeatureCount + feature] - mean[feature]) / std[feature];
                    input[0, time, feature] = time >= WindowSize - MaskSteps && feature == targetIndex ? 0f : standardized;
                }
            }
            var targetSlot = ScenarioDefinitions.Select(item => item.TargetField).ToList().IndexOf(scenario.TargetField);
            for (var time = WindowSize - MaskSteps; time < WindowSize; time++)
            {
                input[0, time, ProcessFeatureCount + targetSlot] = 1f;
            }

            var condition = new DenseTensor<long>(new[] { 1 });
            condition[0] = 0;
            using var outputs = session.Run(
            [
                NamedOnnxValue.CreateFromTensor("inputs", input),
                NamedOnnxValue.CreateFromTensor("condition_id", condition),
            ]);
            var reconstruction = outputs.First(output => output.Name == "reconstruction").AsTensor<float>();
            var targetPoint = EarlyWarningPointRegistry.Points[targetIndex];
            var actualTrend = new double[TrendSteps];
            var reconstructedTrend = new double[TrendSteps];
            for (var offset = 0; offset < TrendSteps; offset++)
            {
                var time = WindowSize - TrendSteps + offset;
                actualTrend[offset] = rawWindow[time * ProcessFeatureCount + targetIndex];
                reconstructedTrend[offset] = reconstruction[0, time, targetIndex] * std[targetIndex] + mean[targetIndex];
            }

            var score = 0.0;
            for (var time = WindowSize - MaskSteps; time < WindowSize; time++)
            {
                var actual = (rawWindow[time * ProcessFeatureCount + targetIndex] - mean[targetIndex]) / std[targetIndex];
                score += Math.Max(0, actual - reconstruction[0, time, targetIndex]);
            }
            score /= MaskSteps;
            var level = score >= threshold * AlarmMultiplier
                ? EarlyWarningLevels.Alarm
                : score >= threshold
                    ? EarlyWarningLevels.Warning
                    : EarlyWarningLevels.Normal;
            var reconstructedValues = EarlyWarningPointRegistry.Points
                .Select((_, index) => (Field: EarlyWarningPointRegistry.Points[index].Field, Value: (double)reconstruction[0, WindowSize - 1, index] * std[index] + mean[index]))
                .ToDictionary(item => item.Field, item => item.Value, StringComparer.Ordinal);
            var contributors = BuildContributors(rawWindow, reconstructedValues, scenario);
            var actualValue = actualTrend[^1];
            var reconstructedValue = reconstructedTrend[^1];
            return new EarlyWarningScenarioResult(
                scenario.Key,
                scenario.Name,
                targetPoint.Name,
                targetPoint.Unit,
                level,
                level == EarlyWarningLevels.Normal ? "当前评分未达到预警阈值。" : "模型评分达到预警阈值。",
                score,
                threshold,
                score / threshold,
                actualValue,
                reconstructedValue,
                actualValue - reconstructedValue,
                actualTrend[^1] - actualTrend[^Math.Max(0, TrendSteps - 11)],
                actualTrend,
                reconstructedTrend,
                contributors,
                scenario.Recommendations);
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or InvalidOperationException or ArgumentException)
        {
            return CreateUnavailableScenario(scenario, EarlyWarningLevels.ModelUnavailable, $"ONNX 推理失败：{exception.Message}");
        }
    }

    private static IReadOnlyList<EarlyWarningContributor> BuildContributors(
        float[] rawWindow,
        IReadOnlyDictionary<string, double> reconstructedValues,
        EarlyWarningScenarioDefinition scenario)
    {
        var values = new List<(EarlyWarningContributor Contributor, double Severity)>();
        foreach (var field in scenario.RelatedFields)
        {
            if (!EarlyWarningPointRegistry.TryGet(field, out var point)
                || !reconstructedValues.TryGetValue(field, out var reconstructed))
            {
                continue;
            }
            var index = EarlyWarningPointRegistry.Points
                .Select((candidate, index) => (candidate, index))
                .First(item => string.Equals(item.candidate.Field, field, StringComparison.Ordinal))
                .index;
            var actual = rawWindow[(WindowSize - 1) * ProcessFeatureCount + index];
            var deviation = actual - reconstructed;
            var scale = Math.Max(Math.Abs(reconstructed) * 0.05, 0.01);
            var severity = Math.Abs(deviation) / scale;
            var direction = deviation > scale * 0.25 ? "高于正常值" : deviation < -scale * 0.25 ? "低于正常值" : "接近正常值";
            values.Add((new EarlyWarningContributor(field, point.Name, point.Unit, actual, reconstructed, 0, direction), severity));
        }

        var total = values.Sum(item => item.Severity);
        return values
            .OrderByDescending(item => item.Severity)
            .Select(item => item.Contributor with { Contribution = total <= 1e-9 ? 0 : item.Severity / total })
            .ToList();
    }

    private EarlyWarningEvaluation CreateUnavailableEvaluation(IReadOnlyList<BoilerSnapshot> snapshots, string status, string reason)
    {
        var start = snapshots.FirstOrDefault()?.RecordedAt ?? BoilerTime.ChinaNow;
        var end = snapshots.LastOrDefault()?.RecordedAt ?? start;
        return new EarlyWarningEvaluation(
            start,
            end,
            ModelVersion,
            status == EarlyWarningLevels.Stopped ? "stopped" : "unavailable",
            status,
            reason,
            ScenarioDefinitions.Select(scenario => CreateUnavailableScenario(scenario, status, reason)).ToList());
    }

    private EarlyWarningScenarioResult CreateUnavailableScenario(EarlyWarningScenarioDefinition scenario, string status, string reason)
    {
        var point = EarlyWarningPointRegistry.Points.First(item => item.Field == scenario.TargetField);
        return new EarlyWarningScenarioResult(
            scenario.Key, scenario.Name, point.Name, point.Unit, status, reason,
            null, thresholds.GetValueOrDefault(scenario.TargetField), null, null, null, null, null,
            [], [], [], scenario.Recommendations);
    }

    private static bool TryBuildWindow(IReadOnlyList<BoilerSnapshot> snapshots, out float[] window, out string reason)
    {
        window = [];
        reason = string.Empty;
        if (snapshots.Count != WindowSize)
        {
            reason = $"实时缓存不足 {WindowSize} 秒，当前仅有 {snapshots.Count} 条数据。";
            return false;
        }
        for (var row = 0; row < WindowSize; row++)
        {
            var expected = snapshots[0].RecordedAt.AddSeconds(row);
            if (BoilerTime.TruncateToSecond(snapshots[row].RecordedAt) != expected)
            {
                reason = $"预警输入时间不连续，缺少 {expected:yyyy-MM-dd HH:mm:ss} 的快照。";
                return false;
            }
        }

        window = new float[WindowSize * ProcessFeatureCount];
        for (var row = 0; row < WindowSize; row++)
        {
            for (var feature = 0; feature < ProcessFeatureCount; feature++)
            {
                var point = EarlyWarningPointRegistry.Points[feature];
                var value = point.GetValue(snapshots[row]);
                if (value is not { } number || !double.IsFinite(number))
                {
                    window = [];
                    reason = $"时间 {snapshots[row].RecordedAt:yyyy-MM-dd HH:mm:ss} 的测点“{point.Name}”数据无效。";
                    return false;
                }
                window[row * ProcessFeatureCount + feature] = (float)number;
            }
        }
        return true;
    }

    private static List<string> ReadStrings(JsonElement element) => element.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToList();

    private static float[] ReadFloats(JsonElement element) => element.EnumerateArray().Select(value => value.GetSingle()).ToArray();
}
