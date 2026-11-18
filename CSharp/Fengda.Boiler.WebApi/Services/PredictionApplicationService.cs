using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Models;
using Fengda.Cnn.Prediction;

namespace Fengda.Boiler.WebApi.Services;

/// <summary>
/// 实时与历史锅炉预测应用服务。
/// </summary>
public sealed class PredictionApplicationService : IDisposable
{
    private readonly object predictionLock = new();
    private readonly BoilerSnapshotCache snapshotCache;
    private readonly BoilerSnapshotStore snapshotStore;
    private readonly PredictionOptions options;
    private readonly ILogger<PredictionApplicationService> logger;
    private readonly FgrAdvisor fgrAdvisor = new();
    private readonly OnnxForecastPredictor? predictor;

    /// <summary>
    /// 创建预测应用服务并加载 ONNX 模型。
    /// </summary>
    public PredictionApplicationService(
        IWebHostEnvironment environment,
        BoilerSnapshotCache snapshotCache,
        BoilerSnapshotStore snapshotStore,
        PredictionOptions options,
        ILogger<PredictionApplicationService> logger)
    {
        this.snapshotCache = snapshotCache;
        this.snapshotStore = snapshotStore;
        this.options = options;
        this.logger = logger;

        var modelDirectory = ResolveModelDirectory(environment.ContentRootPath, options.ModelDirectory);
        predictor = OnnxForecastPredictor.TryCreate(modelDirectory, out var status);
        ModelStatus = status;
        if (predictor is null)
        {
            FeaturePointNames = [];
            RequiredInputPointNames = [];
            TargetPointNames = [];
            logger.LogError("预测模型加载失败：{Status}", status);
        }
        else
        {
            FeaturePointNames = predictor.FeaturePointNames.ToList();
            RequiredInputPointNames = predictor.RequiredInputPointNames.ToList();
            TargetPointNames = predictor.TargetPointNames.ToList();
            logger.LogInformation("预测模型加载成功：{Status}", status);
        }
    }

    /// <summary>
    /// 当前预测模型加载状态。
    /// </summary>
    public string ModelStatus { get; }

    /// <summary>
    /// 用于界面展示的模型输入点位。
    /// </summary>
    public IReadOnlyList<string> FeaturePointNames { get; }

    /// <summary>
    /// 执行预测必须具备的输入点位。
    /// </summary>
    public IReadOnlyList<string> RequiredInputPointNames { get; }

    /// <summary>
    /// 模型输出目标点位。
    /// </summary>
    public IReadOnlyList<string> TargetPointNames { get; }

    /// <summary>
    /// 使用实时缓存中的最后 60 秒数据执行预测。
    /// </summary>
    public PredictionResponseDto? PredictLatest()
    {
        EnsurePredictorAvailable();
        var input = RequireWindowEndingAt(snapshotCache.GetLatest(OnnxForecastPredictor.RequiredInputRowCount), null, "实时缓存");
        if (input is null)
        {
            return null;
        }

        var ageSeconds = (BoilerTime.ChinaNow - input[^1].RecordedAt).TotalSeconds;
        if (ageSeconds > options.MaxSnapshotAgeSeconds)
        {
            throw new BusinessException(
                503,
                $"实时数据已经超过允许的陈旧时间，最后数据时间为 {input[^1].RecordedAt:yyyy-MM-dd HH:mm:ss}。");
        }

        return BuildPredictionResponse(input, null);
    }

    /// <summary>
    /// 使用指定时间及其之前 60 秒历史数据执行预测，并返回之后 60 秒实际数据。
    /// </summary>
    public PredictionResponseDto? PredictHistory(DateTime specifiedTime)
    {
        EnsurePredictorAvailable();
        var endTime = BoilerTime.TruncateToSecond(specifiedTime);
        var inputRaw = snapshotStore.QueryRange(
            endTime.AddSeconds(-(OnnxForecastPredictor.RequiredInputRowCount - 1)),
            endTime.AddSeconds(1).AddTicks(-1));
        var input = RequireWindowEndingAt(inputRaw, endTime, "历史预测输入");
        if (input is null)
        {
            return null;
        }

        var actualStart = endTime.AddSeconds(1);
        var actualEnd = endTime.AddSeconds(60);
        var actualRaw = snapshotStore.QueryRange(actualStart, actualEnd.AddSeconds(1).AddTicks(-1));
        var actual = RequireWindow(actualRaw, actualStart, actualEnd, "历史预测对比");
        if (actual is null)
        {
            return null;
        }

        return BuildPredictionResponse(input, actual);
    }

    /// <summary>
    /// 使用指定整分钟的 60 秒完整运行快照生成并保存紧凑预测缓存。
    /// </summary>
    /// <remarks>
    /// 输入窗口为 [anchorTime, anchorTime + 59 秒]。数据不连续、缺少模型输入或锅炉未运行时不写入缓存。
    /// </remarks>
    public bool TryCacheMinuteForecast(DateTime anchorTime)
    {
        if (predictor is null)
        {
            return false;
        }

        anchorTime = TruncateToMinute(anchorTime);
        var input = RequireWindow(
            snapshotStore.QueryRange(anchorTime, anchorTime.AddSeconds(OnnxForecastPredictor.RequiredInputRowCount - 1)),
            anchorTime,
            anchorTime.AddSeconds(OnnxForecastPredictor.RequiredInputRowCount - 1),
            "分钟预测缓存输入");
        if (input is null || input.Any(snapshot => snapshot.IsRunning is not true))
        {
            return false;
        }

        try
        {
            var forecast = Predict(input);
            snapshotStore.SaveMinuteForecast(new BoilerMinuteForecast
            {
                AnchorTime = anchorTime,
                ForecastStartTime = forecast.StartTime,
                CachedAt = BoilerTime.ChinaNow,
                Series = forecast.Series
                    .Select(series => new BoilerMinuteForecastSeries
                    {
                        PointName = series.PointName,
                        Values = series.Points.Select(point => point.Value).ToList()
                    })
                    .ToList()
            });
            return true;
        }
        catch (Exception exception) when (predictor is not null)
        {
            logger.LogWarning(exception, "分钟预测缓存生成失败：{AnchorTime:yyyy-MM-dd HH:mm}。", anchorTime);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        predictor?.Dispose();
    }

    private PredictionResponseDto BuildPredictionResponse(
        IReadOnlyList<BoilerSnapshot> input,
        IReadOnlyList<BoilerSnapshot>? actualFuture)
    {
        var forecast = Predict(input);

        var currentSnapshot = input[^1];
        var currentValve = TryGetPredictionValue(currentSnapshot, FgrAdvisor.FgrValvePointName);
        var currentFan = TryGetPredictionValue(currentSnapshot, FgrAdvisor.FgrFanPointName);
        var recommendation = fgrAdvisor.Recommend(forecast, currentValve, currentFan);

        return new PredictionResponseDto
        {
            InputStart = input[0].RecordedAt,
            InputEnd = input[^1].RecordedAt,
            ForecastStart = forecast.StartTime,
            ForecastEnd = forecast.EndExclusive.AddSeconds(-1),
            InputData = input,
            Forecast = forecast,
            FgrRecommendation = recommendation,
            ActualFutureData = actualFuture,
            DataQuality = new PredictionDataQualityDto
            {
                IsValid = true,
                InputCount = input.Count,
                LatestInputTime = input[^1].RecordedAt,
                Message = "输入数据完整，时间连续，全部预测必需点位均为有效数值。"
            }
        };
    }

    private ForecastInputWindow BuildForecastInputWindow(IReadOnlyList<BoilerSnapshot> snapshots)
    {
        var rows = new List<ForecastInputRow>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var values = new List<KeyValuePair<string, double>>(predictor!.RequiredInputPointNames.Count);
            foreach (var pointName in predictor.RequiredInputPointNames)
            {
                if (!BoilerPointBindings.TryGetPredictionBinding(pointName, out _))
                {
                    throw new BusinessException(500, $"预测点位没有配置 OPC UA 映射：{pointName}");
                }

                if (!BoilerPointBindings.TryGetPredictionValue(snapshot, pointName, out var value))
                {
                    throw new BusinessException(
                        409,
                        $"时间 {snapshot.RecordedAt:yyyy-MM-dd HH:mm:ss} 的预测点位 {pointName} 缺失或不是有效数值。");
                }

                values.Add(new KeyValuePair<string, double>(pointName, value));
            }

            rows.Add(new ForecastInputRow(snapshot.RecordedAt, values));
        }

        return new ForecastInputWindow(rows);
    }

    private ForecastResult Predict(IReadOnlyList<BoilerSnapshot> input)
    {
        var forecastWindow = BuildForecastInputWindow(input);
        lock (predictionLock)
        {
            return predictor!.PredictForecast(forecastWindow);
        }
    }

    private static IReadOnlyList<BoilerSnapshot>? RequireWindowEndingAt(
        IReadOnlyList<BoilerSnapshot> rawSnapshots,
        DateTime? expectedEndTime,
        string windowName)
    {
        if (rawSnapshots.Count == 0)
        {
            return null;
        }

        var endTime = expectedEndTime ?? rawSnapshots.Max(snapshot => BoilerTime.TruncateToSecond(snapshot.RecordedAt));
        return RequireWindow(
            rawSnapshots,
            endTime.AddSeconds(-(OnnxForecastPredictor.RequiredInputRowCount - 1)),
            endTime,
            windowName);
    }

    private static IReadOnlyList<BoilerSnapshot>? RequireWindow(
        IReadOnlyList<BoilerSnapshot> rawSnapshots,
        DateTime startTime,
        DateTime endTime,
        string windowName)
    {
        startTime = BoilerTime.TruncateToSecond(startTime);
        endTime = BoilerTime.TruncateToSecond(endTime);
        var expectedCount = (int)(endTime - startTime).TotalSeconds + 1;
        var bySecond = rawSnapshots
            .GroupBy(snapshot => BoilerTime.TruncateToSecond(snapshot.RecordedAt))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(snapshot => snapshot.RecordedAt).First().Copy());

        var result = new List<BoilerSnapshot>(expectedCount);
        for (var time = startTime; time <= endTime; time = time.AddSeconds(1))
        {
            if (!bySecond.TryGetValue(time, out var snapshot))
            {
                return null;
            }

            snapshot.RecordedAt = time;
            result.Add(snapshot);
        }

        return result;
    }

    private static double? TryGetPredictionValue(BoilerSnapshot snapshot, string pointName)
    {
        return BoilerPointBindings.TryGetPredictionValue(snapshot, pointName, out var value) ? value : null;
    }

    private static DateTime TruncateToMinute(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
    }

    private void EnsurePredictorAvailable()
    {
        if (predictor is null)
        {
            throw new BusinessException(503, $"预测模型不可用：{ModelStatus}");
        }
    }

    private static string ResolveModelDirectory(string contentRootPath, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(contentRootPath, configuredPath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath))
        };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }
}
