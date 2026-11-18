using System.Globalization;
using System.Text;
using System.Text.Json;
using Fengda.Boiler.Monitoring;
using LiteDB;

var sourceDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "db_data");
var outputDirectory = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(sourceDirectory, "export"));
if (!Directory.Exists(sourceDirectory))
{
    Console.Error.WriteLine($"数据目录不存在: {sourceDirectory}");
    return 2;
}

Directory.CreateDirectory(outputDirectory);
var segmentsDirectory = Path.Combine(outputDirectory, "segments");
Directory.CreateDirectory(segmentsDirectory);

var exporter = new DailySnapshotExporter(sourceDirectory, outputDirectory, segmentsDirectory);
var report = exporter.Export();
Console.WriteLine($"导出完成: {report.ExportedDays} 天，跳过 {report.SkippedDays} 天。报告: {Path.Combine(outputDirectory, "export_report.json")}");
// A recorded unreadable day is an expected data-quality outcome, not a failed export run.
return 0;

internal sealed class DailySnapshotExporter(string sourceDirectory, string outputDirectory, string segmentsDirectory)
{
    private const string SnapshotCollectionPrefix = "snapshots_";
    private const int SecondsPerDay = 24 * 60 * 60;

    private static readonly ExportField[] Fields =
    [
        new("burner_gas_leak_pressure", "燃烧器燃气检漏压力", static x => x.GYDCS_K0QHL10AA101AI1),
        new("burner_gas_inlet_pressure", "燃烧器气阀组入口压力", static x => x.GYDCS_K0QHL10AA101AI2),
        new("burner_air_damper_pct", "燃烧器风门调节阀阀位反馈", static x => x.GYDCS_K0QHL10AA101AI3),
        new("burner_outer_gas_valve_pct", "燃烧器外环气调节阀阀位反馈", static x => x.GYDCS_K0QHL10AA101AI4),
        new("burner_center_gas_valve_pct", "燃烧器中心气调节阀阀位反馈", static x => x.GYDCS_K0QHL10AA101AI5),
        new("fgr_valve_pct", "FGR 调节阀阀位反馈", static x => x.GYDCS_K0QHL10AA101AI6),
        new("stack_nox_mg_m3", "烟囱 NOx 排放浓度", static x => x.GYDCS_K0HNE10CQ101),
        new("stack_so2_mg_m3", "烟囱 SO2 排放浓度", static x => x.GYDCS_K0HNE10CQ102),
        new("stack_o2_pct", "烟囱 O2 排放浓度", static x => x.GYDCS_K0HNE10CQ103),
        new("stack_dust_mg_m3", "烟囱粉尘排放浓度", static x => x.GYDCS_K0HNE10CQ104),
        new("stack_no2_mg_m3", "烟囱 NO2 排放浓度", static x => x.GYDCS_K0HNE10CQ105),
        new("stack_gas_flow_m3_h", "烟囱烟气流量计算值", static x => x.GYDCS_K0HNE10CF101_CAL),
        new("stack_humidity_pct", "烟囱湿度", static x => x.GYDCS_K0HNE10CM101),
        new("stack_pressure_kpa", "烟囱压力", static x => x.GYDCS_K0HNE10CP101),
        new("stack_gas_temperature_c", "烟囱烟气温度", static x => x.GYDCS_K0HNE10CT101),
        new("furnace_exit_pressure_kpa", "炉膛出口烟气压力", static x => x.GYDCS_K0QHA10CP101),
        new("furnace_exit_temperature_c", "炉膛出口烟气温度", static x => x.GYDCS_K0QHA10CT302),
        new("boiler_load", "实际负荷", static x => x.GYDCS_K0QHB10CF101_COMP1),
        new("superheated_steam_pressure_mpa", "过热蒸汽压力", static x => x.GYDCS_K0QHB10CP101),
        new("desuperheater_outlet_temperature_c", "减温器出口蒸汽温度", static x => x.GYDCS_K0QHB10CT302),
        new("fgr_fan_frequency_hz", "FGR 风机变频器频率反馈", static x => x.GYDCS_K0QHH10AV001P),
        new("natural_gas_inlet_flow_m3_h", "天然气入口管道流量", static x => x.GYDCS_K0QHH10CF101),
        new("ignition_solenoid_active", "燃烧器点火电磁阀工作状态", static x => x.GYDCS_K0QHL10AA101DO2),
        new("blower_current_a", "鼓风机变频器电流", static x => x.GYDCS_K0QHL10AN001I),
        new("blower_frequency_hz", "鼓风机变频器频率反馈", static x => x.GYDCS_K0QHL10AN001P),
    ];

    private readonly List<SegmentManifest> segments = [];
    private readonly List<DayExportReport> days = [];

    public ExportReport Export()
    {
        foreach (var databasePath in Directory.EnumerateFiles(sourceDirectory, "*.db")
                     .Where(path => !string.Equals(Path.GetFileName(path), "faults.db", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ExportDatabase(databasePath);
        }

        var manifest = new DatasetManifest(
            Version: 2,
            SourceDirectory: sourceDirectory,
            CreatedAt: DateTime.Now,
            Fields: Fields.Select(field => field.Name).ToArray(),
            FieldDescriptions: Fields.ToDictionary(field => field.Name, field => field.Description, StringComparer.Ordinal),
            Segments: segments.OrderBy(segment => segment.StartTime).ToArray());
        WriteJson(Path.Combine(outputDirectory, "dataset_manifest.json"), manifest);

        var report = new ExportReport(
            CreatedAt: DateTime.Now,
            SourceDirectory: sourceDirectory,
            OutputDirectory: outputDirectory,
            ExportedDays: days.Count(day => day.Exported),
            SkippedDays: days.Count(day => !day.Exported),
            Days: days.OrderBy(day => day.Day).ToArray());
        WriteJson(Path.Combine(outputDirectory, "export_report.json"), report);
        return report;
    }

    private void ExportDatabase(string databasePath)
    {
        string[] collectionNames;
        try
        {
            using var database = OpenDatabase(databasePath);
            collectionNames = database.GetCollectionNames()
                .Where(name => name.StartsWith(SnapshotCollectionPrefix, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"无法读取数据库 {databasePath}: {exception.Message}");
            return;
        }

        foreach (var collectionName in collectionNames)
        {
            if (!DateTime.TryParseExact(
                    collectionName[SnapshotCollectionPrefix.Length..],
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day))
            {
                continue;
            }

            // A corrupted collection can invalidate LiteDB's internal reader state.
            // Isolating the connection prevents one bad day from masking later days.
            ExportDay(databasePath, collectionName, day.Date, Path.GetFileName(databasePath));
        }
    }

    private void ExportDay(string databasePath, string collectionName, DateTime day, string sourceFileName)
    {
        Console.WriteLine($"正在导出 {day:yyyy-MM-dd}...");
        try
        {
            using var database = OpenDatabase(databasePath);
            var records = database.GetCollection<BoilerSnapshot>(collectionName)
                .FindAll()
                .OrderBy(snapshot => snapshot.RecordedAt)
                .ToArray();
            var result = WriteDay(day, sourceFileName, records);
            days.Add(result.Report);
            if (result.Segment is not null)
            {
                segments.Add(result.Segment);
            }
        }
        catch (Exception exception)
        {
            // Do not emit a partial day when its LiteDB collection itself cannot be read.
            days.Add(new DayExportReport(day, sourceFileName, false, 0, 0, 0, 0, 0, exception.Message));
            Console.Error.WriteLine($"跳过 {day:yyyy-MM-dd}: 日表无法读取: {exception.Message}");
        }
    }

    private DayWriteResult WriteDay(DateTime day, string sourceFileName, IReadOnlyList<BoilerSnapshot> records)
    {
        var temporaryPath = Path.Combine(segmentsDirectory, $"{day:yyyyMMdd}.f32.tmp");
        var finalPath = Path.Combine(segmentsDirectory, $"{day:yyyyMMdd}.f32");
        var rows = new Dictionary<DateTime, BoilerSnapshot>();
        var duplicateCount = 0;
        var outsideDayCount = 0;
        foreach (var snapshot in records)
        {
            var timestamp = BoilerTime.TruncateToSecond(snapshot.RecordedAt);
            if (timestamp.Date != day)
            {
                outsideDayCount++;
                continue;
            }

            if (!rows.TryAdd(timestamp, snapshot))
            {
                duplicateCount++;
            }
        }

        var missingSeconds = 0;
        var invalidValues = 0;
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            for (var timestamp = day; timestamp < day.AddDays(1); timestamp = timestamp.AddSeconds(1))
            {
                if (!rows.TryGetValue(timestamp, out var snapshot))
                {
                    missingSeconds++;
                    WriteMissingRow(writer);
                    continue;
                }

                invalidValues += WriteSnapshotRow(writer, snapshot);
            }
        }

        File.Move(temporaryPath, finalPath, overwrite: true);
        var report = new DayExportReport(
            day,
            sourceFileName,
            true,
            records.Count,
            missingSeconds,
            duplicateCount,
            outsideDayCount,
            invalidValues,
            null);
        return new DayWriteResult(
            report,
            new SegmentManifest(
                Path.Combine("segments", $"{day:yyyyMMdd}.f32").Replace('\\', '/'),
                day,
                SecondsPerDay,
                records.Count,
                SecondsPerDay - missingSeconds,
                invalidValues));
    }

    private static LiteDatabase OpenDatabase(string path) => new(new ConnectionString
    {
        Filename = path,
        Connection = ConnectionType.Shared,
        ReadOnly = true,
    });

    private static int WriteSnapshotRow(BinaryWriter writer, BoilerSnapshot snapshot)
    {
        var invalidValues = 0;
        foreach (var field in Fields)
        {
            var value = field.Reader(snapshot);
            if (value is not { } numeric || !double.IsFinite(numeric))
            {
                writer.Write(float.NaN);
                invalidValues++;
            }
            else
            {
                writer.Write((float)numeric);
            }
        }

        return invalidValues;
    }

    private static void WriteMissingRow(BinaryWriter writer)
    {
        for (var index = 0; index < Fields.Length; index++)
        {
            writer.Write(float.NaN);
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record ExportField(string Name, string Description, Func<BoilerSnapshot, double?> Reader);

    private sealed record DayWriteResult(DayExportReport Report, SegmentManifest? Segment);

    private sealed record DatasetManifest(
        int Version,
        string SourceDirectory,
        DateTime CreatedAt,
        IReadOnlyList<string> Fields,
        IReadOnlyDictionary<string, string> FieldDescriptions,
        IReadOnlyList<SegmentManifest> Segments);

    private sealed record SegmentManifest(
        string File,
        DateTime StartTime,
        int RowCount,
        int SourceRecordCount,
        int TimestampedRecordCount,
        int InvalidValueCount);
}

internal sealed record DayExportReport(
    DateTime Day,
    string SourceFile,
    bool Exported,
    int SourceRecordCount,
    int MissingSecondCount,
    int DuplicateTimestampCount,
    int OutsideDayTimestampCount,
    int InvalidValueCount,
    string? SkipReason);

internal sealed record ExportReport(
    DateTime CreatedAt,
    string SourceDirectory,
    string OutputDirectory,
    int ExportedDays,
    int SkippedDays,
    IReadOnlyList<DayExportReport> Days);
