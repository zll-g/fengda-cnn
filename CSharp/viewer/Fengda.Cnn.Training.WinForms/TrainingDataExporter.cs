using System.Globalization;
using System.Text.Json;
using Fengda.Boiler.Monitoring;

namespace Fengda.Cnn.Training.WinForms;

/// <summary>训练数据目录的概览信息。</summary>
internal sealed record TrainingDataDirectorySummary(
    IReadOnlyList<TrainingDaySummary> Days,
    DateTime? FirstDay,
    DateTime? LastDay);

/// <summary>单日历史快照概览。</summary>
internal sealed record TrainingDaySummary(DateTime Day, long? RecordCount, string? ReadError)
{
    /// <summary>供界面显示的记录数或读取失败状态。</summary>
    public string RecordCountDisplay => RecordCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "读取失败";
}

/// <summary>本次扫描或导出中无法读取的日集合信息。</summary>
internal sealed record TrainingDataReadIssue(DateTime Day, string Message);

/// <summary>本次训练使用的数据范围。</summary>
internal sealed record TrainingDataExportResult(
    string ManifestPath,
    int SegmentCount,
    long TotalSeconds,
    long ActualSnapshotCount,
    long CompleteSnapshotCount,
    IReadOnlyList<TrainingDataReadIssue> ReadIssues);

/// <summary>
/// 从预测服务的 LiteDB 历史目录按天导出训练所需字段。
/// 输出为按秒、按天拆分的 float32 矩阵，供 Python 通过 numpy.memmap 顺序读取。
/// </summary>
internal sealed class TrainingDataExporter(string dataDirectory)
{
    internal static readonly string[] FieldNames =
    [
        "burner_center_gas_valve_pct",
        "burner_outer_gas_valve_pct",
        "burner_air_damper_pct",
        "blower_frequency_hz",
        "blower_current_a",
        "fgr_valve_pct",
        "fgr_fan_frequency_hz",
        "natural_gas_inlet_flow_m3_h",
        "stack_nox_mg_m3",
        "stack_o2_pct",
        "stack_no2_mg_m3",
    ];

    private static readonly Func<BoilerSnapshot, double?>[] FieldReaders =
    [
        static snapshot => snapshot.GYDCS_K0QHL10AA101AI5,
        static snapshot => snapshot.GYDCS_K0QHL10AA101AI4,
        static snapshot => snapshot.GYDCS_K0QHL10AA101AI3,
        static snapshot => snapshot.GYDCS_K0QHL10AN001P,
        static snapshot => snapshot.GYDCS_K0QHL10AN001I,
        static snapshot => snapshot.GYDCS_K0QHL10AA101AI6,
        static snapshot => snapshot.GYDCS_K0QHH10AV001P,
        static snapshot => snapshot.GYDCS_K0QHH10CF101,
        static snapshot => snapshot.GYDCS_K0HNE10CQ101,
        static snapshot => snapshot.GYDCS_K0HNE10CQ103,
        static snapshot => snapshot.GYDCS_K0HNE10CQ105,
    ];

    private readonly string dataDirectory = Path.GetFullPath(dataDirectory);

    /// <summary>扫描可用日期及每日记录数，不读取快照字段。</summary>
    public TrainingDataDirectorySummary Scan(CancellationToken cancellationToken)
    {
        using var store = new BoilerSnapshotStore(dataDirectory);
        var days = store.GetAvailableDays();
        var summaries = new List<TrainingDaySummary>(days.Count);
        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.TryGetDailyRecordCount(day, out var count, out var errorMessage))
            {
                summaries.Add(new TrainingDaySummary(day, count, null));
            }
            else
            {
                summaries.Add(new TrainingDaySummary(day, null, errorMessage));
            }
        }

        return new TrainingDataDirectorySummary(
            summaries,
            summaries.Count == 0 ? null : summaries[0].Day,
            summaries.Count == 0 ? null : summaries[^1].Day);
    }

    /// <summary>将指定范围的快照导出为本次训练专属的二进制分区数据。</summary>
    public TrainingDataExportResult Export(
        string runDirectory,
        DateTime startTime,
        DateTime endTime,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        startTime = BoilerTime.TruncateToSecond(startTime);
        endTime = BoilerTime.TruncateToSecond(endTime);
        if (endTime < startTime)
        {
            throw new ArgumentException("训练结束时间不能早于开始时间。", nameof(endTime));
        }

        var workDirectory = Path.Combine(runDirectory, "work");
        var segmentsDirectory = Path.Combine(workDirectory, "segments");
        Directory.CreateDirectory(segmentsDirectory);

        var segmentManifests = new List<SegmentManifest>();
        long totalSeconds = 0;
        long actualSnapshotCount = 0;
        long completeSnapshotCount = 0;
        var readIssues = new List<TrainingDataReadIssue>();
        using var store = new BoilerSnapshotStore(dataDirectory);

        for (var day = startTime.Date; day <= endTime.Date; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentStart = day == startTime.Date ? startTime : day;
            var segmentEnd = day == endTime.Date ? endTime : day.AddDays(1).AddSeconds(-1);
            var rowCount = checked((int)(segmentEnd - segmentStart).TotalSeconds + 1);
            var fileName = $"{day:yyyyMMdd}.f32";
            var relativePath = Path.Combine("work", "segments", fileName).Replace('\\', '/');
            var temporaryPath = Path.Combine(segmentsDirectory, $"{fileName}.tmp");
            var finalPath = Path.Combine(segmentsDirectory, fileName);
            report?.Invoke($"正在导出 {day:yyyy-MM-dd}，共 {rowCount:N0} 秒...");

            long actualForDay = 0;
            long completeForDay = 0;
            var expected = segmentStart;
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var writer = new BinaryWriter(stream))
            {
                var readable = store.TryGetDailyRecordCount(day, out _, out var countError);
                string? rangeError = null;
                if (readable)
                {
                    readable = store.TryVisitRange(segmentStart, segmentEnd, snapshot =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var recordedAt = BoilerTime.TruncateToSecond(snapshot.RecordedAt);
                        while (expected < recordedAt && expected <= segmentEnd)
                        {
                            WriteMissingRow(writer);
                            expected = expected.AddSeconds(1);
                        }

                        if (recordedAt != expected || expected > segmentEnd)
                        {
                            return;
                        }

                        var complete = WriteSnapshotRow(writer, snapshot);
                        actualForDay++;
                        if (complete)
                        {
                            completeForDay++;
                        }

                        expected = expected.AddSeconds(1);
                    }, out rangeError);
                }

                if (readable)
                {
                    // The range is valid and all written rows can be retained.
                }
                else
                {
                    var errorMessage = countError ?? rangeError ?? "未知 LiteDB 读取错误。";
                    readIssues.Add(new TrainingDataReadIssue(day, errorMessage));
                    report?.Invoke($"{day:yyyy-MM-dd} 数据库索引无法读取，已按缺失数据跳过: {errorMessage}");
                }

                while (expected <= segmentEnd)
                {
                    WriteMissingRow(writer);
                    expected = expected.AddSeconds(1);
                }
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            totalSeconds += rowCount;
            actualSnapshotCount += actualForDay;
            completeSnapshotCount += completeForDay;
            segmentManifests.Add(new SegmentManifest(
                relativePath,
                segmentStart,
                rowCount,
                actualForDay,
                completeForDay));
        }

        var manifest = new TrainingDatasetManifest(
            Version: 1,
            SourceDirectory: dataDirectory,
            CreatedAt: DateTime.Now,
            StartTime: startTime,
            EndTime: endTime,
            Fields: FieldNames,
            Segments: segmentManifests,
            SourceFiles: BuildSourceFiles(startTime, endTime));
        var manifestPath = Path.Combine(runDirectory, "dataset_manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new TrainingDataExportResult(
            manifestPath,
            segmentManifests.Count,
            totalSeconds,
            actualSnapshotCount,
            completeSnapshotCount,
            readIssues);
    }

    private IReadOnlyList<SourceFileManifest> BuildSourceFiles(DateTime startTime, DateTime endTime)
    {
        var months = new HashSet<string>(StringComparer.Ordinal);
        for (var day = startTime.Date; day <= endTime.Date; day = day.AddDays(1))
        {
            months.Add(day.ToString("yyyyMM", CultureInfo.InvariantCulture));
        }

        return months.OrderBy(month => month, StringComparer.Ordinal)
            .Select(month => Path.Combine(dataDirectory, $"{month}.db"))
            .Where(File.Exists)
            .Select(path => new SourceFileManifest(
                Path.GetFileName(path),
                new FileInfo(path).Length,
                new FileInfo(path).LastWriteTimeUtc))
            .ToList();
    }

    private static bool WriteSnapshotRow(BinaryWriter writer, BoilerSnapshot snapshot)
    {
        var complete = true;
        foreach (var reader in FieldReaders)
        {
            var value = reader(snapshot);
            var numeric = value is { } number && double.IsFinite(number)
                ? (float)number
                : float.NaN;
            if (!float.IsFinite(numeric))
            {
                complete = false;
            }

            writer.Write(numeric);
        }

        return complete;
    }

    private static void WriteMissingRow(BinaryWriter writer)
    {
        for (var index = 0; index < FieldReaders.Length; index++)
        {
            writer.Write(float.NaN);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record TrainingDatasetManifest(
        int Version,
        string SourceDirectory,
        DateTime CreatedAt,
        DateTime StartTime,
        DateTime EndTime,
        IReadOnlyList<string> Fields,
        IReadOnlyList<SegmentManifest> Segments,
        IReadOnlyList<SourceFileManifest> SourceFiles);

    private sealed record SegmentManifest(
        string File,
        DateTime StartTime,
        int RowCount,
        long ActualSnapshotCount,
        long CompleteSnapshotCount);

    private sealed record SourceFileManifest(string FileName, long Length, DateTime LastWriteTimeUtc);
}
