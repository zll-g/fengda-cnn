using Fengda.Boiler.Monitoring;

namespace Fengda.Boiler.WebApi.Services;

/// <summary>
/// 在每个完整分钟结束后写入一次紧凑的全天预测缓存。
/// </summary>
public sealed class MinuteForecastCacheHostedService(
    BoilerSnapshotCache snapshotCache,
    PredictionApplicationService predictionService,
    ILogger<MinuteForecastCacheHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastAttemptedMinute = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CacheLatestCompletedMinute(ref lastAttemptedMinute);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "分钟预测缓存服务执行失败。");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void CacheLatestCompletedMinute(ref DateTime? lastAttemptedMinute)
    {
        var latest = snapshotCache.GetLatest(1).LastOrDefault();
        if (latest is null)
        {
            return;
        }

        var anchor = new DateTime(
            latest.RecordedAt.Year,
            latest.RecordedAt.Month,
            latest.RecordedAt.Day,
            latest.RecordedAt.Hour,
            latest.RecordedAt.Minute,
            0,
            latest.RecordedAt.Kind).AddMinutes(-1);
        if (lastAttemptedMinute == anchor)
        {
            return;
        }

        lastAttemptedMinute = anchor;
        if (predictionService.TryCacheMinuteForecast(anchor))
        {
            logger.LogInformation("分钟预测缓存已写入：{AnchorTime:yyyy-MM-dd HH:mm}。", anchor);
        }
    }
}
