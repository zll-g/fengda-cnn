using Fengda.Boiler.Monitoring;

namespace Fengda.Boiler.WebApi.Services;

internal sealed class AspNetMonitorLogger : IMonitorLogger, IDisposable
{
    private readonly ILogger<AspNetMonitorLogger> logger;
    private readonly FileMonitorLogger fileLogger;

    public AspNetMonitorLogger(ILogger<AspNetMonitorLogger> logger, BoilerMonitorOptions options)
    {
        this.logger = logger;
        var logDirectory = Path.Combine(options.OutputDirectory, "logs");
        var logPath = Path.Combine(logDirectory, $"opcua-monitor-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        fileLogger = new FileMonitorLogger(logPath, writeConsole: false);
    }

    public void Info(string message)
    {
        logger.LogInformation("{Message}", message);
        fileLogger.Info(message);
    }

    public void Warn(string message)
    {
        logger.LogWarning("{Message}", message);
        fileLogger.Warn(message);
    }

    public void Error(string message)
    {
        logger.LogError("{Message}", message);
        fileLogger.Error(message);
    }

    public void Error(string message, Exception exception)
    {
        logger.LogError(exception, "{Message}", message);
        fileLogger.Error(message, exception);
    }

    public void Dispose()
    {
        fileLogger.Dispose();
    }
}

internal sealed class BoilerMonitoringHostedService(
    BoilerMonitorOptions options,
    BoilerSnapshotStore snapshotStore,
    BoilerSnapshotCache snapshotCache,
    AspNetMonitorLogger logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logDirectory = Path.Combine(options.OutputDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        using var blowerEventLogger = new BlowerCurrentEventLogger(
            Path.Combine(logDirectory, "blower-current-events.log"));

        InitializeSnapshotCacheFromDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var configuration = await OpcUaApplicationFactory
                    .BuildAsync(options, logger, stoppingToken)
                    .ConfigureAwait(false);
                await using var monitor = new OpcUaMonitor(
                    configuration,
                    options,
                    logger,
                    snapshotStore,
                    snapshotCache,
                    blowerEventLogger);
                await monitor.RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("OPC UA 后台监控服务异常退出，将在重连等待时间后重新启动。", ex);
                try
                {
                    await Task.Delay(options.ReconnectDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void InitializeSnapshotCacheFromDatabase()
    {
        try
        {
            var latestSnapshots = snapshotStore.QueryLatest(60);
            snapshotCache.ReplaceWith(latestSnapshots);
            if (latestSnapshots.Count == 0)
            {
                logger.Info("实时缓存初始化完成：数据库中没有可加载的历史快照。");
                return;
            }

            logger.Info(
                $"实时缓存初始化完成：已从数据库加载 {latestSnapshots.Count} 条快照，时间范围 {latestSnapshots[0].RecordedAt:yyyy-MM-dd HH:mm:ss} 至 {latestSnapshots[^1].RecordedAt:yyyy-MM-dd HH:mm:ss}。");
        }
        catch (Exception ex)
        {
            logger.Error("实时缓存从数据库初始化失败，将继续启动 OPC UA 实时采集。", ex);
        }
    }
}
