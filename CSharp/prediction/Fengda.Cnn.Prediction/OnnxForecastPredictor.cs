using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Fengda.Cnn.Prediction;

/// <summary>
/// 使用 ONNX 模型预测未来 60 秒锅炉排放数据。
/// </summary>
public sealed class OnnxForecastPredictor : IDisposable
{
    private const int InputSteps = 60;
    private const int OutputSteps = 60;
    private const string HistoryFeaturePrefix = "hist__";
    private const string DiffFeatureSeparator = "__diff_";

    private static readonly Dictionary<string, string> FieldToPointName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["burner_center_gas_valve_pct"] = "K0QHL10AA101AI5.UNIT3@NET3",
        ["burner_outer_gas_valve_pct"] = "K0QHL10AA101AI4.UNIT3@NET3",
        ["burner_air_damper_pct"] = "K0QHL10AA101AI3.UNIT3@NET3",
        ["blower_frequency_hz"] = "K0QHL10AN001P.UNIT3@NET3",
        ["blower_current_a"] = "K0QHL10AN001I.UNIT3@NET3",
        ["fgr_valve_pct"] = "K0QHL10AA101AI6.UNIT3@NET3",
        ["fgr_fan_frequency_hz"] = "K0QHH10AV001P.UNIT3@NET3",
        ["ignition_solenoid_active"] = "K0QHL10AA101DO2.UNIT3@NET3",
        ["steam_pressure_mpa"] = "K0QHB10CP101.UNIT3@NET3",
        ["steam_temp_c"] = "K0QHB10CT302.UNIT3@NET3",
        ["steam_flow_tph"] = "K0QHB10CF101-COMP.UNIT3@NET3",
        ["furnace_exit_gas_temp_c"] = "K0QHA10CT302.UNIT3@NET3",
        ["furnace_exit_gas_pressure_kpa"] = "K0QHA10CP101.UNIT3@NET3",
        ["stack_nox_mg_m3"] = "K0HNE10CQ101.UNIT3@NET3",
        ["stack_so2_mg_m3"] = "K0HNE10CQ102.UNIT3@NET3",
        ["stack_o2_pct"] = "K0HNE10CQ103.UNIT3@NET3",
        ["stack_dust_mg_m3"] = "K0HNE10CQ104.UNIT3@NET3",
        ["stack_no2_mg_m3"] = "K0HNE10CQ105.UNIT3@NET3",
        ["stack_gas_temp_c"] = "K0HNE10CT101.UNIT3@NET3",
        ["stack_pressure_pa"] = "K0HNE10CP101.UNIT3@NET3",
        ["stack_flow_calc_m3_h"] = "K0HNE10CF101-CAL.UNIT3@NET3",
        ["stack_humidity_pct"] = "K0HNE10CM101.UNIT3@NET3",
        ["natural_gas_inlet_flow_m3_h"] = "K0QHH10CF101-1.UNIT3@NET3",
    };

    private readonly InferenceSession session;
    private readonly string[] featureColumns;
    private readonly string[] targetColumns;
    private readonly float[] featureMean;
    private readonly float[] featureStd;
    private readonly float[] targetMean;
    private readonly float[] targetStd;
    private readonly string[] featurePointNames;
    private readonly string[] requiredInputPointNames;
    private readonly string[] targetPointNames;

    private OnnxForecastPredictor(
        string modelPath,
        string[] featureColumns,
        string[] targetColumns,
        NormalizationStats stats)
    {
        session = new InferenceSession(modelPath);
        this.featureColumns = featureColumns;
        this.targetColumns = targetColumns;
        featurePointNames = MapColumnsToPointNames(featureColumns, includeHistoryFeatures: false);
        requiredInputPointNames = MapColumnsToPointNames(featureColumns, includeHistoryFeatures: true);
        targetPointNames = MapColumnsToPointNames(targetColumns, includeHistoryFeatures: true);
        featureMean = stats.FeatureMean;
        featureStd = stats.FeatureStd;
        targetMean = stats.TargetMean;
        targetStd = stats.TargetStd;
    }

    /// <summary>模型要求的输入行数。</summary>
    public static int RequiredInputRowCount => InputSteps;

    /// <summary>用于界面展示的模型输入点位。</summary>
    public IReadOnlyList<string> FeaturePointNames => featurePointNames;

    /// <summary>执行预测必须提供的全部现场输入点位。</summary>
    public IReadOnlyList<string> RequiredInputPointNames => requiredInputPointNames;

    /// <summary>模型输出目标点位。</summary>
    public IReadOnlyList<string> TargetPointNames => targetPointNames;

    /// <summary>
    /// 从项目根目录下的默认模型目录加载预测器。
    /// </summary>
    public static OnnxForecastPredictor? TryCreateDefault(string projectRoot, out string status)
    {
        var modelDir = FindDefaultModelDirectory(projectRoot);
        if (modelDir is null)
        {
            status = "未找到 ONNX 模型目录";
            return null;
        }

        return TryCreate(modelDir, out status);
    }

    /// <summary>
    /// 从明确指定的模型目录加载预测器。
    /// </summary>
    public static OnnxForecastPredictor? TryCreate(string modelDirectory, out string status)
    {
        try
        {
            var modelDir = Path.GetFullPath(modelDirectory);
            var modelPath = Path.Combine(modelDir, "best_model.onnx");
            var preparedPath = Path.Combine(modelDir, "prepared_data_summary.json");
            var normalizationPath = Path.Combine(modelDir, "normalization_stats.json");
            if (!File.Exists(modelPath) || !File.Exists(preparedPath) || !File.Exists(normalizationPath))
            {
                status = $"ONNX 模型目录不完整：{modelDir}";
                return null;
            }

            var schema = LoadPreparedSummary(preparedPath);
            var stats = LoadNormalizationStats(normalizationPath);
            ValidateModelInputs(schema, stats);
            status = $"ONNX 模型已加载：{Path.GetFileName(modelDir)}";
            return new OnnxForecastPredictor(modelPath, schema.FeatureColumns, schema.TargetColumns, stats);
        }
        catch (Exception ex)
        {
            status = $"ONNX 模型加载失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 使用连续 60 秒输入窗口预测未来 60 秒数据。
    /// </summary>
    public ForecastResult PredictForecast(ForecastInputWindow inputWindow)
    {
        ArgumentNullException.ThrowIfNull(inputWindow);
        ValidateInputWindow(inputWindow);

        var forecastStart = inputWindow.EndExclusive;
        var input = BuildInputTensor(inputWindow);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("input", input)]);
        var forecast = results.First(item => item.Name == "forecast").AsTensor<float>();
        var seriesList = new List<ForecastSeries>();

        for (var targetIndex = 0; targetIndex < targetColumns.Length; targetIndex++)
        {
            var targetColumn = targetColumns[targetIndex];
            if (!FieldToPointName.TryGetValue(targetColumn, out var pointName))
            {
                continue;
            }

            var points = new List<TimeValue>(OutputSteps);
            for (var step = 0; step < OutputSteps; step++)
            {
                var normalizedValue = forecast[0, step, targetIndex];
                var value = normalizedValue * targetStd[targetIndex] + targetMean[targetIndex];
                points.Add(new TimeValue(forecastStart.AddSeconds(step), value));
            }

            seriesList.Add(new ForecastSeries(targetColumn, pointName, points));
        }

        return new ForecastResult(
            forecastStart,
            forecastStart.AddSeconds(OutputSteps),
            seriesList);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        session.Dispose();
    }

    private static void ValidateInputWindow(ForecastInputWindow inputWindow)
    {
        if (inputWindow.Rows.Count != InputSteps)
        {
            throw new ArgumentException(
                $"Forecast input window must contain exactly {InputSteps} rows.",
                nameof(inputWindow));
        }

        for (var index = 1; index < inputWindow.Rows.Count; index++)
        {
            var seconds = (inputWindow.Rows[index].Timestamp - inputWindow.Rows[index - 1].Timestamp).TotalSeconds;
            if (Math.Abs(seconds - 1.0) > 0.001)
            {
                throw new ArgumentException("Forecast input rows must be spaced at 1-second intervals.", nameof(inputWindow));
            }
        }
    }

    private static string[] MapColumnsToPointNames(IEnumerable<string> columns, bool includeHistoryFeatures)
    {
        return columns
            .Select(column =>
            {
                if (!includeHistoryFeatures
                    && column.StartsWith(HistoryFeaturePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return TryResolvePointNameForFeatureColumn(column, out var pointName)
                    ? PointNameNormalizer.CanonicalPointName(pointName)
                    : null;
            })
            .Where(pointName => !string.IsNullOrWhiteSpace(pointName))
            .Select(pointName => pointName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private DenseTensor<float> BuildInputTensor(ForecastInputWindow inputWindow)
    {
        var tensor = new DenseTensor<float>([1, InputSteps, featureColumns.Length]);
        for (var step = 0; step < InputSteps; step++)
        {
            var rawFeatures = BuildRawFeatureRow(inputWindow, step);
            for (var featureIndex = 0; featureIndex < featureColumns.Length; featureIndex++)
            {
                var std = Math.Abs(featureStd[featureIndex]) < 1e-6f ? 1.0f : featureStd[featureIndex];
                tensor[0, step, featureIndex] = (rawFeatures[featureIndex] - featureMean[featureIndex]) / std;
            }
        }

        return tensor;
    }

    private float[] BuildRawFeatureRow(ForecastInputWindow inputWindow, int rowIndex)
    {
        var row = new float[featureColumns.Length];
        var knownValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var featureIndex = 0; featureIndex < featureColumns.Length; featureIndex++)
        {
            var feature = featureColumns[featureIndex];
            if (feature == "run_stage_code")
            {
                continue;
            }

            var value = GetFeatureValue(inputWindow, feature, rowIndex);
            knownValues[feature] = value;
            row[featureIndex] = (float)value;
        }

        var runStageIndex = Array.FindIndex(featureColumns, item => item == "run_stage_code");
        if (runStageIndex >= 0)
        {
            row[runStageIndex] = InferRunStageCode(knownValues);
        }

        return row;
    }

    private static double GetFeatureValue(ForecastInputWindow inputWindow, string feature, int rowIndex)
    {
        if (feature.StartsWith(HistoryFeaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var targetField = feature[HistoryFeaturePrefix.Length..];
            return GetMappedFieldValue(inputWindow, targetField, rowIndex);
        }

        if (TryParseDiffFeature(feature, out var baseField, out var lagSeconds))
        {
            var current = GetMappedFieldValue(inputWindow, baseField, rowIndex);
            return inputWindow.TryGetValue(rowIndex - lagSeconds, ResolvePointName(baseField), out var previous)
                ? current - previous
                : 0.0;
        }

        return GetMappedFieldValue(inputWindow, feature, rowIndex);
    }

    private static double GetMappedFieldValue(ForecastInputWindow inputWindow, string fieldName, int rowIndex)
    {
        var pointName = ResolvePointName(fieldName);
        if (inputWindow.TryGetValue(rowIndex, pointName, out var value))
        {
            return value;
        }

        var timestamp = inputWindow.Rows[rowIndex].Timestamp;
        throw new InvalidOperationException($"Forecast input is missing point {pointName} at {timestamp:HH:mm:ss}.");
    }

    private static string ResolvePointName(string fieldName)
    {
        if (!TryResolvePointNameForFeatureColumn(fieldName, out var pointName))
        {
            throw new InvalidOperationException($"No point mapping is configured for feature column: {fieldName}");
        }

        return pointName;
    }

    private static bool TryResolvePointNameForFeatureColumn(string feature, out string pointName)
    {
        if (FieldToPointName.TryGetValue(feature, out pointName!))
        {
            return true;
        }

        if (feature.StartsWith(HistoryFeaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var targetField = feature[HistoryFeaturePrefix.Length..];
            return FieldToPointName.TryGetValue(targetField, out pointName!);
        }

        if (TryParseDiffFeature(feature, out var baseField, out _))
        {
            return FieldToPointName.TryGetValue(baseField, out pointName!);
        }

        pointName = string.Empty;
        return false;
    }

    private static bool TryParseDiffFeature(string feature, out string baseField, out int lagSeconds)
    {
        baseField = string.Empty;
        lagSeconds = 0;
        var separatorIndex = feature.LastIndexOf(DiffFeatureSeparator, StringComparison.OrdinalIgnoreCase);
        if (separatorIndex < 0)
        {
            return false;
        }

        var suffix = feature[(separatorIndex + DiffFeatureSeparator.Length)..];
        if (!suffix.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(suffix[..^1], out lagSeconds) || lagSeconds <= 0)
        {
            return false;
        }

        baseField = feature[..separatorIndex];
        return !string.IsNullOrWhiteSpace(baseField);
    }

    private static int InferRunStageCode(IReadOnlyDictionary<string, double> values)
    {
        var gasFlow = GetFeatureValue(values, "natural_gas_inlet_flow_m3_h");
        var ignition = GetFeatureValue(values, "ignition_solenoid_active");
        var steamPressure = GetFeatureValue(values, "steam_pressure_mpa");
        var steamFlow = GetFeatureValue(values, "steam_flow_tph");
        var steamTemp = GetFeatureValue(values, "steam_temp_c");

        if (gasFlow <= 1.0 && steamPressure <= 0.1 && steamFlow <= 0.5)
        {
            return 0;
        }

        if (ignition >= 0.5 || (gasFlow > 1.0 && steamPressure < 0.5))
        {
            return 1;
        }

        if (steamPressure < 1.0 || steamTemp < 250.0 || steamFlow < 4.0)
        {
            return 2;
        }

        return 3;
    }

    private static double GetFeatureValue(IReadOnlyDictionary<string, double> values, string feature)
    {
        return values.TryGetValue(feature, out var value) ? value : 0.0;
    }

    private static string? FindDefaultModelDirectory(string projectRoot)
    {
        var transientPreferred = Path.Combine(
            projectRoot,
            "artifacts",
            "formal_training_1s_60_60_nox_o2_no2_transient_t015_stride1");
        if (File.Exists(Path.Combine(transientPreferred, "best_model.onnx")))
        {
            return transientPreferred;
        }

        var preferred = Path.Combine(projectRoot, "artifacts", "formal_training_1s_60_60_nox_o2_no2_stride1");
        if (File.Exists(Path.Combine(preferred, "best_model.onnx")))
        {
            return preferred;
        }

        var legacyPreferred = Path.Combine(projectRoot, "artifacts", "training_run_1s_60_60_excl_3812_peaknox_onnx");
        if (File.Exists(Path.Combine(legacyPreferred, "best_model.onnx")))
        {
            return legacyPreferred;
        }

        var artifactsDir = Path.Combine(projectRoot, "artifacts");
        if (!Directory.Exists(artifactsDir))
        {
            return null;
        }

        return Directory.EnumerateDirectories(artifactsDir)
            .Where(path => File.Exists(Path.Combine(path, "best_model.onnx")))
            .OrderByDescending(path => Directory.GetLastWriteTime(path))
            .FirstOrDefault();
    }

    private static PreparedSummary LoadPreparedSummary(string path)
    {
        var summary = JsonSerializer.Deserialize<PreparedSummary>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return summary is null
            ? throw new InvalidOperationException("prepared_data_summary.json 解析失败")
            : summary;
    }

    private static NormalizationStats LoadNormalizationStats(string path)
    {
        var stats = JsonSerializer.Deserialize<NormalizationStats>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return stats is null
            ? throw new InvalidOperationException("normalization_stats.json 解析失败")
            : stats;
    }

    private static void ValidateModelInputs(PreparedSummary schema, NormalizationStats stats)
    {
        if (schema.FeatureColumns.Length != stats.FeatureMean.Length
            || schema.FeatureColumns.Length != stats.FeatureStd.Length)
        {
            throw new InvalidOperationException("特征列数量与归一化参数不一致");
        }

        if (schema.TargetColumns.Length != stats.TargetMean.Length
            || schema.TargetColumns.Length != stats.TargetStd.Length)
        {
            throw new InvalidOperationException("目标列数量与归一化参数不一致");
        }
    }

    private sealed class PreparedSummary
    {
        [JsonPropertyName("feature_columns")]
        public string[] FeatureColumns { get; set; } = [];

        [JsonPropertyName("target_columns")]
        public string[] TargetColumns { get; set; } = [];
    }

    private sealed class NormalizationStats
    {
        [JsonPropertyName("feature_mean")]
        public float[] FeatureMean { get; set; } = [];

        [JsonPropertyName("feature_std")]
        public float[] FeatureStd { get; set; } = [];

        [JsonPropertyName("target_mean")]
        public float[] TargetMean { get; set; } = [];

        [JsonPropertyName("target_std")]
        public float[] TargetStd { get; set; } = [];
    }
}
