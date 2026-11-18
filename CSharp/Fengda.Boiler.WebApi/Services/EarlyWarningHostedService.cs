using Fengda.Boiler.Monitoring;
using Fengda.EarlyWarning;

namespace Fengda.Boiler.WebApi.Services;

/// <summary>ONNX 异常监测预警运行参数。</summary>
public sealed record EarlyWarningOptions
{
    /// <summary>是否启用后台 ONNX 异常监测预警。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>预警模型目录，相对路径基于 WebApi 内容根目录。</summary>
    public string ModelDirectory { get; init; } = Path.Combine("model", "early-warning");

    /// <summary>检查实时缓存的最小间隔毫秒数。</summary>
    public int PollIntervalMilliseconds { get; init; } = 250;
}

/// <summary>供接口读取的线程安全模型预警结果缓存。</summary>
public sealed class EarlyWarningRuntimeCache
{
    private readonly object syncRoot = new();
    private EarlyWarningEvaluation current = EarlyWarningOnnxModel.CreateStatusEvaluation(
        BoilerTime.ChinaNow,
        "未加载",
        EarlyWarningLevels.ModelUnavailable,
        "后台预警服务正在初始化模型。");

    /// <summary>写入最新模型预警结果。</summary>
    public void Update(EarlyWarningEvaluation evaluation)
    {
        lock (syncRoot)
        {
            current = evaluation;
        }
    }

    /// <summary>读取最新模型预警结果。</summary>
    public EarlyWarningEvaluation GetLatest()
    {
        lock (syncRoot)
        {
            return current;
        }
    }
}

/// <summary>
/// 启动即运行的 ONNX 异常监测预警服务。
/// 仅使用最近连续 60 秒快照，不阻塞 OPC UA 快照保存与未来趋势预测。
/// </summary>
public sealed class EarlyWarningHostedService(
    IWebHostEnvironment environment,
    EarlyWarningOptions options,
    BoilerSnapshotCache snapshotCache,
    EarlyWarningRuntimeCache runtimeCache,
    EarlyWarningStore earlyWarningStore,
    ILogger<EarlyWarningHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            runtimeCache.Update(EarlyWarningOnnxModel.CreateStatusEvaluation(
                BoilerTime.ChinaNow,
                "已禁用",
                EarlyWarningLevels.ModelUnavailable,
                "后台异常监测预警服务已禁用。"));
            return;
        }

        EarlyWarningOnnxModel? model = null;
        try
        {
            var modelDirectory = ResolveModelDirectory();
            model = EarlyWarningOnnxModel.Load(modelDirectory);
            logger.LogInformation("ONNX 异常监测预警模型已加载：{ModelVersion}。", model.ModelVersion);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "ONNX 异常监测预警模型加载失败。");
            runtimeCache.Update(EarlyWarningOnnxModel.CreateStatusEvaluation(
                BoilerTime.ChinaNow,
                "加载失败",
                EarlyWarningLevels.ModelUnavailable,
                $"预警模型加载失败：{exception.Message}"));
            return;
        }

        using (model)
        {
            DateTime? lastProcessedTime = null;
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(options.PollIntervalMilliseconds, 100, 5000));
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var latest = snapshotCache.GetLatest(60);
                    var latestTime = latest.LastOrDefault()?.RecordedAt;
                    if (latestTime is null || latestTime == lastProcessedTime)
                    {
                        continue;
                    }

                    var evaluation = model.Evaluate(latest);
                    runtimeCache.Update(evaluation);
                    lastProcessedTime = latestTime;
                    SaveTriggeredWarnings(evaluation, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "ONNX 异常监测预警服务执行失败。");
                    runtimeCache.Update(EarlyWarningOnnxModel.CreateStatusEvaluation(
                        BoilerTime.ChinaNow,
                        model.ModelVersion,
                        EarlyWarningLevels.ModelUnavailable,
                        $"预警服务执行失败：{exception.Message}"));
                }
            }
        }
    }

    private void SaveTriggeredWarnings(EarlyWarningEvaluation evaluation, CancellationToken cancellationToken)
    {
        if (!string.Equals(evaluation.Status, "scored", StringComparison.Ordinal))
        {
            return;
        }

        var candidates = evaluation.Scenarios
            .Where(scenario => scenario.Level is EarlyWarningLevels.Warning or EarlyWarningLevels.Alarm)
            .Where(scenario => scenario.Score is not null && scenario.Threshold is not null && scenario.Actual is not null && scenario.Reconstructed is not null)
            .Select(scenario => new EarlyWarningPersistCandidate(
                scenario.Key,
                scenario.Name,
                scenario.Level,
                scenario.Score!.Value,
                scenario.Threshold!.Value,
                scenario.TargetName,
                scenario.Unit,
                scenario.Actual!.Value,
                scenario.Reconstructed!.Value,
                string.Join("；", scenario.Contributors.Take(4).Select(item => $"{item.Name}({item.Contribution:P0})")),
                scenario.StatusReason,
                string.Join("；", scenario.Recommendations)))
            .ToList();
        if (candidates.Count > 0)
        {
            earlyWarningStore.SaveTriggered(evaluation.WindowStart, evaluation.WindowEnd, evaluation.ModelVersion, candidates, cancellationToken);
        }
    }

    private string ResolveModelDirectory()
    {
        if (Path.IsPathRooted(options.ModelDirectory))
        {
            return options.ModelDirectory;
        }

        var contentRootCandidate = Path.Combine(environment.ContentRootPath, options.ModelDirectory);
        if (Directory.Exists(contentRootCandidate))
        {
            return contentRootCandidate;
        }

        return Path.Combine(AppContext.BaseDirectory, options.ModelDirectory);
    }
}
