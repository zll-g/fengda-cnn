using Fengda.Boiler.Monitoring;
using Fengda.EarlyWarning;

namespace Fengda.Boiler.WebApi.Services;

/// <summary>
/// 为历史预警详情回放 ONNX 模型的服务。
/// </summary>
/// <remarks>
/// 模型按需加载后在服务生命周期内复用，避免每次打开详情都重新创建 ONNX 推理会话。
/// </remarks>
public sealed class EarlyWarningHistoricalAnalysisService(
    IWebHostEnvironment environment,
    EarlyWarningOptions options,
    ILogger<EarlyWarningHistoricalAnalysisService> logger) : IDisposable
{
    private readonly object syncRoot = new();
    private EarlyWarningOnnxModel? model;
    private string? modelLoadError;

    /// <summary>
    /// 对指定的连续 60 秒快照执行历史预警回放。
    /// </summary>
    /// <param name="snapshots">按时间升序排列的秒级快照。</param>
    /// <param name="evaluation">模型评分和重构结果。</param>
    /// <param name="errorMessage">模型无法加载或窗口不可评分时的中文说明。</param>
    /// <returns>成功完成评分时为 <see langword="true"/>。</returns>
    public bool TryEvaluate(
        IReadOnlyList<BoilerSnapshot> snapshots,
        out EarlyWarningEvaluation? evaluation,
        out string? errorMessage)
    {
        lock (syncRoot)
        {
            if (!TryGetModel(out var loadedModel, out errorMessage))
            {
                evaluation = null;
                return false;
            }

            evaluation = loadedModel.Evaluate(snapshots);
            if (string.Equals(evaluation.Status, "scored", StringComparison.Ordinal))
            {
                errorMessage = null;
                return true;
            }

            errorMessage = evaluation.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (syncRoot)
        {
            model?.Dispose();
            model = null;
        }
    }

    private bool TryGetModel(out EarlyWarningOnnxModel loadedModel, out string? errorMessage)
    {
        if (model is not null)
        {
            loadedModel = model;
            errorMessage = null;
            return true;
        }

        if (modelLoadError is not null)
        {
            loadedModel = null!;
            errorMessage = modelLoadError;
            return false;
        }

        try
        {
            var modelDirectory = ResolveModelDirectory();
            model = EarlyWarningOnnxModel.Load(modelDirectory);
            logger.LogInformation("历史预警详情 ONNX 模型已加载：{ModelVersion}。", model.ModelVersion);
            loadedModel = model;
            errorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "历史预警详情 ONNX 模型加载失败。");
            modelLoadError = $"预警模型加载失败：{exception.Message}";
            loadedModel = null!;
            errorMessage = modelLoadError;
            return false;
        }
    }

    private string ResolveModelDirectory()
    {
        if (Path.IsPathRooted(options.ModelDirectory))
        {
            return options.ModelDirectory;
        }

        var contentRootCandidate = Path.Combine(environment.ContentRootPath, options.ModelDirectory);
        return Directory.Exists(contentRootCandidate)
            ? contentRootCandidate
            : Path.Combine(AppContext.BaseDirectory, options.ModelDirectory);
    }
}
