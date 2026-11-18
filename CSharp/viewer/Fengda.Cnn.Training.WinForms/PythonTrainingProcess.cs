using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fengda.Cnn.Training.WinForms;

/// <summary>Python 训练进程输出的结构化进度事件。</summary>
internal sealed class TrainingProgressEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
    [JsonPropertyName("stage")]
    public string? Stage { get; init; }
    [JsonPropertyName("progress")]
    public double? Progress { get; init; }
    [JsonPropertyName("epoch")]
    public int? Epoch { get; init; }
    [JsonPropertyName("total_epochs")]
    public int? TotalEpochs { get; init; }
    [JsonPropertyName("train_loss")]
    public double? TrainLoss { get; init; }
    [JsonPropertyName("val_loss")]
    public double? ValLoss { get; init; }
    [JsonPropertyName("selection_score")]
    public double? SelectionScore { get; init; }
    [JsonPropertyName("learning_rate")]
    public double? LearningRate { get; init; }
    [JsonPropertyName("metrics")]
    public Dictionary<string, double>? Metrics { get; init; }
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>启动、观察并停止独立 Python 训练进程。</summary>
internal sealed class PythonTrainingProcess : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private Process? process;
    private string? stopFilePath;

    public bool IsRunning => process is { HasExited: false };

    public async Task<int> RunAsync(
        string pythonPath,
        string scriptPath,
        string manifestPath,
        string outputDirectory,
        int epochs,
        int batchSize,
        string device,
        Action<TrainingProgressEvent> onProgress,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        stopFilePath = Path.Combine(outputDirectory, "stop.request");
        if (File.Exists(stopFilePath))
        {
            File.Delete(stopFilePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--manifest");
        startInfo.ArgumentList.Add(manifestPath);
        startInfo.ArgumentList.Add("--output-dir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--epochs");
        startInfo.ArgumentList.Add(epochs.ToString());
        startInfo.ArgumentList.Add("--batch-size");
        startInfo.ArgumentList.Add(batchSize.ToString());
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--stop-file");
        startInfo.ArgumentList.Add(stopFilePath);

        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Python 训练进程。");
        }

        var stdoutTask = ReadOutputAsync(process.StandardOutput, onProgress, onLog, cancellationToken);
        var stderrTask = ReadErrorAsync(process.StandardError, onLog, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        return process.ExitCode;
    }

    public void RequestStop()
    {
        if (!string.IsNullOrWhiteSpace(stopFilePath))
        {
            File.WriteAllText(stopFilePath, "stop");
        }
    }

    public void ForceStop()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose()
    {
        process?.Dispose();
        process = null;
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        Action<TrainingProgressEvent> onProgress,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var progress = JsonSerializer.Deserialize<TrainingProgressEvent>(line, JsonOptions);
                if (progress is not null && !string.IsNullOrWhiteSpace(progress.Type))
                {
                    onProgress(progress);
                    continue;
                }
            }
            catch (JsonException)
            {
                // Python libraries may emit an ordinary line; preserve it as a log entry.
            }

            onLog(line);
        }
    }

    private static async Task ReadErrorAsync(StreamReader reader, Action<string> onLog, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                onLog($"[stderr] {line}");
            }
        }
    }
}
