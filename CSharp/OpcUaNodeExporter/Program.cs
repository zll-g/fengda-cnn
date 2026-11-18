using Fengda.Boiler.Monitoring;

var options = CommandLineOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

var logDirectory = Path.Combine(options.OutputDirectory, "logs");
Directory.CreateDirectory(logDirectory);
var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
var logPath = Path.Combine(logDirectory, $"opcua-monitor-{timestamp}.log");
var blowerEventLogPath = Path.Combine(logDirectory, "blower-current-events.log");

using var logger = new FileMonitorLogger(logPath);
using var blowerEventLogger = new BlowerCurrentEventLogger(blowerEventLogPath);
using var snapshotStore = new BoilerSnapshotStore(
    options.DatabaseDirectory,
    logger,
    options.LegacyDatabasePath);
using var runningFaultStore = new RunningFaultStore(
    options.FaultDatabasePath,
    logger);
var snapshotCache = new BoilerSnapshotCache();
using var cancellationTokenSource = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    if (!cancellationTokenSource.IsCancellationRequested)
    {
        logger.Info("收到 Ctrl+C，正在停止监控。");
        cancellationTokenSource.Cancel();
    }
};
Console.CancelKeyPress += cancelHandler;

try
{
    logger.Info($"程序启动，目标 OPC UA 地址：{options.EndpointUrl}");
    logger.Info($"LiteDB 月度目录：{options.DatabaseDirectory}");
    logger.Info($"运行故障库：{options.FaultDatabasePath}");
    logger.Info($"快照间隔：{options.SnapshotInterval.TotalSeconds:0.###} 秒");
    logger.Info($"重连等待：{options.ReconnectDelay.TotalSeconds:0.###} 秒");

    var latestSnapshots = snapshotStore.QueryLatest(60);
    snapshotCache.ReplaceWith(latestSnapshots);
    if (latestSnapshots.Count == 0)
    {
        logger.Info("实时缓存初始化完成：数据库中没有可加载的历史快照。");
    }
    else
    {
        logger.Info(
            $"实时缓存初始化完成：已从数据库加载 {latestSnapshots.Count} 条快照，时间范围 {latestSnapshots[0].RecordedAt:yyyy-MM-dd HH:mm:ss} 至 {latestSnapshots[^1].RecordedAt:yyyy-MM-dd HH:mm:ss}。");
    }

    var configuration = await OpcUaApplicationFactory
        .BuildAsync(options, logger, cancellationTokenSource.Token)
        .ConfigureAwait(false);
    await using var monitor = new OpcUaMonitor(
        configuration,
        options,
        logger,
        snapshotStore,
        snapshotCache,
        runningFaultStore,
        blowerEventLogger);

    Console.WriteLine("监控程序正在运行，按 Ctrl+C 停止。");
    await monitor.RunAsync(cancellationTokenSource.Token).ConfigureAwait(false);
    return 0;
}
catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
{
    logger.Info("监控程序已取消。");
    return 0;
}
catch (Exception ex)
{
    logger.Error("监控程序运行失败。", ex);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

internal static class CommandLineOptions
{
    public static BoilerMonitorOptions Parse(string[] args)
    {
        var endpointUrl = "opc.tcp://10.15.3.219:12081";
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
        var databaseDirectory = Path.Combine(Environment.CurrentDirectory, "data");
        var faultDatabasePath = Path.Combine(Environment.CurrentDirectory, "data", "faults.db");
        var snapshotIntervalSeconds = 1;
        var reconnectDelaySeconds = 5;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--endpoint":
                case "-e":
                    endpointUrl = GetValue(args, ref index, "endpoint");
                    break;
                case "--output-dir":
                case "-o":
                    outputDirectory = GetValue(args, ref index, "output-dir");
                    break;
                case "--data-dir":
                case "-d":
                    databaseDirectory = GetValue(args, ref index, "data-dir");
                    break;
                case "--fault-db":
                    faultDatabasePath = GetValue(args, ref index, "fault-db");
                    break;
                case "--interval-seconds":
                case "-i":
                    snapshotIntervalSeconds = GetPositiveIntValue(args, ref index, "interval-seconds");
                    break;
                case "--reconnect-delay-seconds":
                case "-r":
                    reconnectDelaySeconds = GetPositiveIntValue(args, ref index, "reconnect-delay-seconds");
                    break;
                default:
                    if (!args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        endpointUrl = args[index];
                    }

                    break;
            }
        }

        return new BoilerMonitorOptions
        {
            EndpointUrl = endpointUrl,
            OutputDirectory = Path.GetFullPath(outputDirectory),
            DatabaseDirectory = Path.GetFullPath(databaseDirectory),
            FaultDatabasePath = Path.GetFullPath(faultDatabasePath),
            SnapshotIntervalSeconds = snapshotIntervalSeconds,
            ReconnectDelaySeconds = reconnectDelaySeconds
        };
    }

    private static string GetValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"参数 --{optionName} 缺少值。");
        }

        index++;
        return args[index];
    }

    private static int GetPositiveIntValue(string[] args, ref int index, string optionName)
    {
        var rawValue = GetValue(args, ref index, optionName);
        if (!int.TryParse(rawValue, out var value) || value <= 0)
        {
            throw new ArgumentException($"参数 --{optionName} 必须是正整数。");
        }

        return value;
    }
}
