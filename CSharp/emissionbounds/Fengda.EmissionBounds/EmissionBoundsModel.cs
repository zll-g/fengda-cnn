using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Fengda.EmissionBounds;

/// <summary>ONNX 当前工况 NOx/O2 统计上下限模型。</summary>
public sealed class EmissionBoundsOnnxModel : IDisposable
{
    /// <summary>模型输入窗口秒数。</summary>
    public const int WindowSize = 60;

    /// <summary>模型输入测点数量。</summary>
    public const int InputCount = 8;

    /// <summary>模型张量要求的输入字段顺序。</summary>
    public static readonly string[] InputFields =
    [
        "burner_air_damper_pct",
        "blower_frequency_hz",
        "blower_current_a",
        "burner_center_gas_valve_pct",
        "burner_outer_gas_valve_pct",
        "fgr_valve_pct",
        "fgr_fan_frequency_hz",
        "natural_gas_inlet_flow_m3_h",
    ];

    private static readonly string[] OutputFields = ["nox_lower", "nox_upper", "o2_lower", "o2_upper"];
    private readonly InferenceSession session;
    private readonly float[] inputMeans;
    private readonly float[] outputScales;
    private readonly IReadOnlyList<HistoricalInputGroup> historicalInputGroups;

    private EmissionBoundsOnnxModel(InferenceSession session, string modelVersion, float[] inputMeans, float[] outputScales,
        IReadOnlyList<HistoricalInputGroup> historicalInputGroups, EffectiveBurningRule effectiveBurningRule,
        DateTimeOffset? trainingCreatedAt, int trainingStrideSeconds, double? noxTestCoverage, double? o2TestCoverage)
    {
        this.session = session;
        ModelVersion = modelVersion;
        this.inputMeans = inputMeans;
        this.outputScales = outputScales;
        this.historicalInputGroups = historicalInputGroups;
        EffectiveBurningRule = effectiveBurningRule;
        TrainingCreatedAt = trainingCreatedAt;
        TrainingStrideSeconds = trainingStrideSeconds;
        NoxTestCoverage = noxTestCoverage;
        O2TestCoverage = o2TestCoverage;
    }

    /// <summary>当前模型版本。</summary>
    public string ModelVersion { get; }

    /// <summary>训练时使用的有效燃烧状态门槛。</summary>
    public EffectiveBurningRule EffectiveBurningRule { get; }

    /// <summary>模型训练产物创建时间。</summary>
    public DateTimeOffset? TrainingCreatedAt { get; }

    /// <summary>训练窗口滑动步长（秒）。</summary>
    public int TrainingStrideSeconds { get; }

    /// <summary>独立测试集 NOx 区间覆盖率。</summary>
    public double? NoxTestCoverage { get; }

    /// <summary>独立测试集 O2 区间覆盖率。</summary>
    public double? O2TestCoverage { get; }

    /// <summary>从指定目录加载 ONNX、manifest、归一化参数和历史输入分位范围。</summary>
    public static EmissionBoundsOnnxModel Load(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        modelDirectory = Path.GetFullPath(modelDirectory);
        var modelPath = Path.Combine(modelDirectory, "best_model.onnx");
        var manifestPath = Path.Combine(modelDirectory, "manifest.json");
        var normalizationPath = Path.Combine(modelDirectory, "normalization.json");
        var rangesPath = Path.Combine(modelDirectory, "historical_input_ranges.json");
        if (!File.Exists(modelPath) || !File.Exists(manifestPath) || !File.Exists(normalizationPath) || !File.Exists(rangesPath))
        {
            throw new FileNotFoundException($"NOx/O2 上下限模型文件不完整（需要 ONNX、manifest、normalization 和 historical_input_ranges）：{modelDirectory}");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        var version = root.GetProperty("model_version").GetString() ?? "unknown";
        var fields = root.GetProperty("inputs").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        var outputs = root.GetProperty("output_order").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        var shape = root.GetProperty("input_shape").EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Null ? -1 : value.GetInt32()).ToArray();
        if (!fields.SequenceEqual(InputFields, StringComparer.Ordinal)
            || !outputs.SequenceEqual(OutputFields, StringComparer.Ordinal)
            || shape.Length != 3 || shape[1] != WindowSize || shape[2] != InputCount)
        {
            throw new InvalidDataException("NOx/O2 上下限模型 manifest 与当前程序的字段、顺序或张量形状不一致。");
        }

        var ruleRoot = root.GetProperty("effective_burning_rule");
        const string expectedValveRule = "center gas valve > 1% or outer gas valve > 1%; invalid feedback counts as closed";
        if (!string.Equals(ruleRoot.GetProperty("valve_running_rule").GetString(), expectedValveRule, StringComparison.Ordinal))
        {
            throw new InvalidDataException("模型使用的燃气阀运行判定与 Demo 不一致。");
        }
        var entry = ruleRoot.GetProperty("entry_conditions_all");
        var exit = ruleRoot.GetProperty("exit_conditions_any");
        var effectiveBurningRule = new EffectiveBurningRule(
            entry.GetProperty("natural_gas_inlet_flow_m3_h_gte").GetDouble(),
            exit.GetProperty("natural_gas_inlet_flow_m3_h_lt").GetDouble(),
            entry.GetProperty("blower_frequency_hz_gte").GetDouble(),
            exit.GetProperty("blower_frequency_hz_lt").GetDouble(),
            entry.GetProperty("blower_current_a_gte").GetDouble(),
            exit.GetProperty("blower_current_a_lt").GetDouble(),
            ruleRoot.GetProperty("enter_dwell_seconds").GetInt32(),
            ruleRoot.GetProperty("exit_confirm_seconds").GetInt32());
        if (!effectiveBurningRule.IsValid)
        {
            throw new InvalidDataException("模型 manifest 中的有效燃烧门槛无效。");
        }
        var trainingCreatedAt = root.TryGetProperty("created_at", out var createdAtElement)
            && DateTimeOffset.TryParse(createdAtElement.GetString(), out var parsedCreatedAt)
            ? parsedCreatedAt
            : (DateTimeOffset?)null;
        var trainingStrideSeconds = root.TryGetProperty("training_stride_seconds", out var strideElement)
            ? strideElement.GetInt32()
            : 0;
        if (trainingStrideSeconds < 1)
        {
            throw new InvalidDataException("模型 manifest 中的训练窗口步长无效。");
        }

        using var normalization = JsonDocument.Parse(File.ReadAllText(normalizationPath));
        var normalizationRoot = normalization.RootElement;
        var normalizationFields = normalizationRoot.GetProperty("input_fields").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty).ToArray();
        var inputMeans = normalizationRoot.GetProperty("input_mean").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        var targetScales = normalizationRoot.GetProperty("target_std").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        if (!normalizationFields.SequenceEqual(InputFields, StringComparer.Ordinal)
            || inputMeans.Length != InputCount || targetScales.Length != 2
            || inputMeans.Any(value => !float.IsFinite(value))
            || targetScales.Any(value => !float.IsFinite(value) || value <= 0))
        {
            throw new InvalidDataException("边界模型 normalization 文件与输入字段或目标标准差不匹配。");
        }
        float[] outputScales = [targetScales[0], targetScales[0], targetScales[1], targetScales[1]];

        using var rangeDocument = JsonDocument.Parse(File.ReadAllText(rangesPath));
        var historicalInputGroups = new List<HistoricalInputGroup>();
        foreach (var group in rangeDocument.RootElement.GetProperty("groups").EnumerateArray())
        {
            var groupName = group.GetProperty("group").GetString() ?? string.Empty;
            var gasFlowMin = group.GetProperty("gas_flow_min").GetDouble();
            var gasFlowMax = group.GetProperty("gas_flow_max").GetDouble();
            var parametersElement = group.GetProperty("parameters");
            var parameters = new Dictionary<string, HistoricalInputRange>(StringComparer.Ordinal);
            foreach (var field in InputFields)
            {
                if (!parametersElement.TryGetProperty(field, out var parameter))
                {
                    throw new InvalidDataException($"历史输入范围缺少参数 {field}。");
                }
                var range = new HistoricalInputRange(parameter.GetProperty("p10").GetDouble(),
                    parameter.GetProperty("median").GetDouble(), parameter.GetProperty("p90").GetDouble());
                if (!double.IsFinite(range.P10) || !double.IsFinite(range.Median) || !double.IsFinite(range.P90)
                    || range.P10 > range.Median || range.Median > range.P90)
                {
                    throw new InvalidDataException($"历史输入范围 {field} 的分位值无效。");
                }
                parameters.Add(field, range);
            }
            if (string.IsNullOrWhiteSpace(groupName) || !double.IsFinite(gasFlowMin) || !double.IsFinite(gasFlowMax)
                || gasFlowMin > gasFlowMax)
            {
                throw new InvalidDataException("历史输入范围的天然气流量分组无效。");
            }
            historicalInputGroups.Add(new HistoricalInputGroup(groupName, gasFlowMin, gasFlowMax, parameters));
        }
        if (historicalInputGroups.Count == 0)
        {
            throw new InvalidDataException("历史输入范围文件中没有有效工况组。");
        }

        double? noxTestCoverage = null;
        double? o2TestCoverage = null;
        var summaryPath = Path.Combine(modelDirectory, "training_summary.json");
        if (File.Exists(summaryPath))
        {
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var testMetrics = summary.RootElement.GetProperty("test_metrics");
            noxTestCoverage = testMetrics.GetProperty("nox").GetProperty("coverage").GetDouble();
            o2TestCoverage = testMetrics.GetProperty("o2").GetProperty("coverage").GetDouble();
            if (!IsValidCoverage(noxTestCoverage.Value) || !IsValidCoverage(o2TestCoverage.Value))
            {
                throw new InvalidDataException("训练摘要中的测试集覆盖率无效。");
            }
        }

        var session = new InferenceSession(modelPath);
        if (!session.InputMetadata.ContainsKey("input_sequence") || !session.OutputMetadata.ContainsKey("emission_bounds"))
        {
            session.Dispose();
            throw new InvalidDataException("ONNX 模型输入/输出名称与推理契约不匹配。");
        }
        return new EmissionBoundsOnnxModel(session, version, inputMeans, outputScales, historicalInputGroups,
            effectiveBurningRule, trainingCreatedAt, trainingStrideSeconds, noxTestCoverage, o2TestCoverage);
    }

    /// <summary>从应用目录下的 model/emission_bounds 子目录加载模型。</summary>
    public static EmissionBoundsOnnxModel LoadFromApplicationDirectory() =>
        Load(Path.Combine(AppContext.BaseDirectory, "model", "emission_bounds"));

    /// <summary>对最近 60 秒、每秒 8 个原始工程值执行上下限推理。</summary>
    public EmissionBoundsPrediction Predict(ReadOnlySpan<float> inputWindow)
    {
        if (inputWindow.Length != WindowSize * InputCount)
        {
            throw new ArgumentException($"输入窗口应包含 {WindowSize}×{InputCount} 个值。", nameof(inputWindow));
        }
        for (var i = 0; i < inputWindow.Length; i++)
        {
            if (!float.IsFinite(inputWindow[i]))
            {
                throw new ArgumentException("输入窗口含空值或非有限数值，拒绝推理。", nameof(inputWindow));
            }
        }

        var tensor = new DenseTensor<float>(new[] { 1, WindowSize, InputCount });
        for (var time = 0; time < WindowSize; time++)
        {
            for (var feature = 0; feature < InputCount; feature++)
            {
                tensor[0, time, feature] = inputWindow[time * InputCount + feature];
            }
        }
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input_sequence", tensor) };
        using var results = session.Run(inputs);
        var values = results.First(result => result.Name == "emission_bounds").AsTensor<float>();
        if (values.Length != OutputFields.Length || values.Dimensions.Length != 2 || values.Dimensions[0] != 1)
        {
            throw new InvalidDataException("ONNX 输出长度不为 4，与边界模型契约不匹配。");
        }
        var prediction = new EmissionBoundsPrediction(values[0, 0], values[0, 1], values[0, 2], values[0, 3]);
        if (!double.IsFinite(prediction.NoxLower) || !double.IsFinite(prediction.NoxUpper)
            || !double.IsFinite(prediction.O2Lower) || !double.IsFinite(prediction.O2Upper)
            || prediction.NoxLower > prediction.NoxUpper || prediction.O2Lower > prediction.O2Upper)
        {
            throw new InvalidDataException("ONNX 输出边界无效或下限高于上限。");
        }
        return prediction;
    }

    /// <summary>估算各输入轨迹对当前边界输出的相对局部敏感度；结果合计为 100%。</summary>
    public double[]? EstimateInputContributions(ReadOnlySpan<float> inputWindow, EmissionBoundsPrediction baseline)
    {
        var baselineOutputs = ToArray(baseline);
        var effects = new double[InputCount];
        var perturbedWindow = inputWindow.ToArray();
        for (var feature = 0; feature < InputCount; feature++)
        {
            for (var time = 0; time < WindowSize; time++)
            {
                perturbedWindow[time * InputCount + feature] = inputMeans[feature];
            }
            var counterfactual = ToArray(Predict(perturbedWindow));
            var squaredChange = 0.0;
            for (var output = 0; output < OutputFields.Length; output++)
            {
                var standardizedChange = (baselineOutputs[output] - counterfactual[output]) / outputScales[output];
                squaredChange += standardizedChange * standardizedChange;
            }
            effects[feature] = Math.Sqrt(squaredChange / OutputFields.Length);
            for (var time = 0; time < WindowSize; time++)
            {
                perturbedWindow[time * InputCount + feature] = inputWindow[time * InputCount + feature];
            }
        }
        var totalEffect = effects.Sum();
        if (!double.IsFinite(totalEffect) || totalEffect <= 1e-9)
        {
            return null;
        }
        return effects.Select(effect => effect / totalEffect * 100.0).ToArray();
    }

    /// <summary>按当前天然气瞬时流量选择训练数据中的历史分组。</summary>
    public HistoricalInputGroup? FindHistoricalInputGroup(double gasFlow) => double.IsFinite(gasFlow)
        ? historicalInputGroups.FirstOrDefault(group => gasFlow >= group.GasFlowMin && gasFlow <= group.GasFlowMax)
        : null;

    private static double[] ToArray(EmissionBoundsPrediction prediction) =>
        [prediction.NoxLower, prediction.NoxUpper, prediction.O2Lower, prediction.O2Upper];

    private static bool IsValidCoverage(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    /// <inheritdoc />
    public void Dispose() => session.Dispose();
}

/// <summary>与训练数据筛选一致的有效燃烧状态滞回参数。</summary>
public sealed record EffectiveBurningRule(
    double GasFlowEnterM3H,
    double GasFlowExitM3H,
    double BlowerFrequencyEnterHz,
    double BlowerFrequencyExitHz,
    double BlowerCurrentEnterA,
    double BlowerCurrentExitA,
    int EnterDwellSeconds,
    int ExitConfirmSeconds)
{
    /// <summary>参数范围和持续时间是否合法。</summary>
    public bool IsValid =>
        double.IsFinite(GasFlowEnterM3H) && GasFlowEnterM3H > GasFlowExitM3H && GasFlowExitM3H >= 0
        && double.IsFinite(BlowerFrequencyEnterHz) && BlowerFrequencyEnterHz > BlowerFrequencyExitHz && BlowerFrequencyExitHz >= 0
        && double.IsFinite(BlowerCurrentEnterA) && BlowerCurrentEnterA > BlowerCurrentExitA && BlowerCurrentExitA >= 0
        && EnterDwellSeconds > 0 && ExitConfirmSeconds > 0;
}

/// <summary>NOx 与 O2 的模型统计上下限。</summary>
public sealed record EmissionBoundsPrediction(double NoxLower, double NoxUpper, double O2Lower, double O2Upper);

/// <summary>单个输入特征在训练数据中的历史分位范围。</summary>
public sealed record HistoricalInputRange(double P10, double Median, double P90);

/// <summary>按天然气流量划分的训练数据历史输入范围。</summary>
public sealed record HistoricalInputGroup(string Name, double GasFlowMin, double GasFlowMax,
    IReadOnlyDictionary<string, HistoricalInputRange> Parameters);
