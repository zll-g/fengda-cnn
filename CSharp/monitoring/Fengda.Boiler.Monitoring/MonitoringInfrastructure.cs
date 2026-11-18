using System.Globalization;
using System.Text;

namespace Fengda.Boiler.Monitoring;

/// <summary>
/// OPC UA 锅炉监控运行参数。
/// </summary>
public sealed record BoilerMonitorOptions
{
    /// <summary>OPC UA 服务端地址。</summary>
    public string EndpointUrl { get; init; } = "opc.tcp://10.15.3.219:12081";

    /// <summary>运行数据输出目录。</summary>
    public string OutputDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "artifacts");

    /// <summary>快照保存间隔秒数。</summary>
    public int SnapshotIntervalSeconds { get; init; } = 1;

    /// <summary>断线重连等待秒数。</summary>
    public int ReconnectDelaySeconds { get; init; } = 5;

    /// <summary>快照保存间隔。</summary>
    public TimeSpan SnapshotInterval => TimeSpan.FromSeconds(SnapshotIntervalSeconds);

    /// <summary>断线重连等待时间。</summary>
    public TimeSpan ReconnectDelay => TimeSpan.FromSeconds(ReconnectDelaySeconds);

    /// <summary>月度 LiteDB 数据库目录，文件名格式为 yyyyMM.db。</summary>
    public string DatabaseDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "data");

    /// <summary>运行故障独立 LiteDB 数据库路径。</summary>
    public string FaultDatabasePath { get; init; } = Path.Combine(Environment.CurrentDirectory, "data", "faults.db");

    /// <summary>旧版单文件 LiteDB 路径，仅用于读取历史兼容数据。</summary>
    public string LegacyDatabasePath => Path.Combine(Path.GetFullPath(OutputDirectory), "litedb", "opcua-snapshots.db");
}

/// <summary>
/// 监控组件日志接口。
/// </summary>
public interface IMonitorLogger
{
    /// <summary>记录普通信息。</summary>
    void Info(string message);

    /// <summary>记录警告信息。</summary>
    void Warn(string message);

    /// <summary>记录错误信息。</summary>
    void Error(string message);

    /// <summary>记录异常信息。</summary>
    void Error(string message, Exception exception);
}

/// <summary>
/// 不输出任何内容的监控日志实现。
/// </summary>
public sealed class NullMonitorLogger : IMonitorLogger
{
    /// <summary>共享实例。</summary>
    public static NullMonitorLogger Instance { get; } = new();

    private NullMonitorLogger()
    {
    }

    /// <inheritdoc />
    public void Info(string message) { }

    /// <inheritdoc />
    public void Warn(string message) { }

    /// <inheritdoc />
    public void Error(string message) { }

    /// <inheritdoc />
    public void Error(string message, Exception exception) { }
}

/// <summary>
/// 同时写入文件和控制台的监控日志。
/// </summary>
public sealed class FileMonitorLogger : IMonitorLogger, IDisposable
{
    private readonly object syncRoot = new();
    private readonly StreamWriter writer;
    private readonly bool writeConsole;

    /// <summary>
    /// 创建文件监控日志。
    /// </summary>
    public FileMonitorLogger(string logPath, bool writeConsole = true)
    {
        this.writeConsole = writeConsole;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Environment.CurrentDirectory);
        writer = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
    }

    /// <inheritdoc />
    public void Info(string message) => Write("INFO", message);

    /// <inheritdoc />
    public void Warn(string message) => Write("WARN", message);

    /// <inheritdoc />
    public void Error(string message) => Write("ERROR", message);

    /// <inheritdoc />
    public void Error(string message, Exception exception) => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    /// <inheritdoc />
    public void Dispose()
    {
        lock (syncRoot)
        {
            writer.Dispose();
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (syncRoot)
        {
            writer.WriteLine(line);
        }

        if (writeConsole)
        {
            Console.WriteLine(line);
        }
    }
}

/// <summary>
/// 鼓风机电流启停事件日志接口。
/// </summary>
public interface IBlowerCurrentEventLogger
{
    /// <summary>记录鼓风机启停事件。</summary>
    void Log(string eventType, string pointName, double previousValue, double currentValue, double threshold);
}

/// <summary>
/// 将鼓风机电流启停事件持续追加到固定文件。
/// </summary>
public sealed class BlowerCurrentEventLogger : IBlowerCurrentEventLogger, IDisposable
{
    private readonly object syncRoot = new();
    private readonly StreamWriter writer;

    /// <summary>
    /// 创建鼓风机事件日志。
    /// </summary>
    public BlowerCurrentEventLogger(string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Environment.CurrentDirectory);
        writer = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
    }

    /// <inheritdoc />
    public void Log(string eventType, string pointName, double previousValue, double currentValue, double threshold)
    {
        var line =
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{eventType}] {pointName} 从 {previousValue.ToString(CultureInfo.InvariantCulture)} 变为 {currentValue.ToString(CultureInfo.InvariantCulture)}，阈值={threshold.ToString(CultureInfo.InvariantCulture)}";
        lock (syncRoot)
        {
            writer.WriteLine(line);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (syncRoot)
        {
            writer.Dispose();
        }
    }
}
