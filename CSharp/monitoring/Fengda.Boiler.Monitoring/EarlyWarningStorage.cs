using System.Globalization;
using LiteDB;

namespace Fengda.Boiler.Monitoring;

/// <summary>模型预警等级。</summary>
public static class EarlyWarningRecordLevels
{
    /// <summary>二级预警。</summary>
    public const string Warning = "warning";

    /// <summary>三级报警。</summary>
    public const string Alarm = "alarm";

    /// <summary>可记录等级。</summary>
    public static IReadOnlyList<string> All { get; } = [Warning, Alarm];
}

/// <summary>模型预警场景。</summary>
public static class EarlyWarningRecordScenarios
{
    /// <summary>NOx 排放浓度异常升高。</summary>
    public const string Nox = "nox";

    /// <summary>减温器出口蒸汽温度异常升高。</summary>
    public const string Temperature = "temperature";

    /// <summary>过热蒸汽压力异常升高。</summary>
    public const string Pressure = "pressure";

    /// <summary>全部场景筛选值。</summary>
    public const string All = "all";

    /// <summary>全部可查询场景。</summary>
    public static IReadOnlyList<string> DisplayValues { get; } = [Nox, Temperature, Pressure];
}

/// <summary>已保存的 ONNX 模型预警记录。</summary>
public sealed class EarlyWarningRecord
{
    /// <summary>LiteDB 主键。</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>告警时间，精确到秒。</summary>
    public DateTime AlarmTime { get; set; }

    /// <summary>告警月份，格式 yyyyMM。</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>预警场景标识。</summary>
    public string Scenario { get; set; } = string.Empty;

    /// <summary>预警场景名称。</summary>
    public string ScenarioName { get; set; } = string.Empty;

    /// <summary>预警等级，warning 或 alarm。</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>ONNX 模型版本。</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>模型异常分数。</summary>
    public double Score { get; set; }

    /// <summary>当前预警阈值。</summary>
    public double Threshold { get; set; }

    /// <summary>目标监测指标名称。</summary>
    public string MetricName { get; set; } = string.Empty;

    /// <summary>目标监测指标单位。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>目标监测指标当前实测值。</summary>
    public double ActualValue { get; set; }

    /// <summary>模型重构正常值。</summary>
    public double ReconstructedValue { get; set; }

    /// <summary>关联测点摘要。</summary>
    public string RelatedPoints { get; set; } = string.Empty;

    /// <summary>异常详情。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>建议处置。</summary>
    public string Recommendations { get; set; } = string.Empty;

    /// <summary>预警输入窗口开始时间。</summary>
    public DateTime WindowStart { get; set; }

    /// <summary>预警输入窗口结束时间。</summary>
    public DateTime WindowEnd { get; set; }
}

/// <summary>模型预警分页查询结果。</summary>
public sealed class EarlyWarningPage
{
    /// <summary>查询月份。</summary>
    public required string Month { get; init; }

    /// <summary>查询场景。</summary>
    public required string Scenario { get; init; }

    /// <summary>查询等级。</summary>
    public required string Level { get; init; }

    /// <summary>当前页码。</summary>
    public int Page { get; init; }

    /// <summary>每页记录数。</summary>
    public int PageSize { get; init; }

    /// <summary>记录总数。</summary>
    public int Total { get; init; }

    /// <summary>当前页记录。</summary>
    public required IReadOnlyList<EarlyWarningRecord> Items { get; init; }
}

/// <summary>
/// ONNX 模型预警记录存储服务。
/// 使用 data/faults.db 文件，并以月份划分 early_warnings_yyyyMM 集合。
/// </summary>
public sealed class EarlyWarningStore : IDisposable
{
    private const string CollectionPrefix = "early_warnings_";
    private const int DeduplicateWindowMinutes = 5;
    private readonly object syncRoot = new();
    private readonly LiteDatabase database;
    private readonly HashSet<string> indexedCollections = new(StringComparer.Ordinal);
    private readonly IMonitorLogger logger;

    /// <summary>创建模型预警记录存储服务。</summary>
    public EarlyWarningStore(string databasePath, IMonitorLogger? logger = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        this.logger = logger ?? NullMonitorLogger.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? Environment.CurrentDirectory);
        database = new LiteDatabase(new ConnectionString { Filename = DatabasePath, Connection = ConnectionType.Shared });
    }

    /// <summary>故障库文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 写入当前窗口新触发的模型预警。相同场景和等级在五分钟内不重复写入。
    /// </summary>
    public IReadOnlyList<EarlyWarningRecord> SaveTriggered(
        DateTime windowStart,
        DateTime windowEnd,
        string modelVersion,
        IEnumerable<EarlyWarningPersistCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var alarmTime = BoilerTime.TruncateToSecond(windowEnd);
        var month = alarmTime.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var inserted = new List<EarlyWarningRecord>();
        lock (syncRoot)
        {
            var collection = GetCollection(month);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasRecentDuplicate(collection, candidate, alarmTime))
                {
                    continue;
                }
                var record = CreateRecord(alarmTime, month, windowStart, windowEnd, modelVersion, candidate);
                collection.Upsert(record);
                inserted.Add(record);
            }
        }

        if (inserted.Count > 0)
        {
            logger.Warn($"ONNX 模型预警记录写入完成：{inserted.Count} 条，时间 {alarmTime:yyyy-MM-dd HH:mm:ss}。");
        }
        return inserted;
    }

    /// <summary>分页查询指定月份的模型预警记录。</summary>
    public EarlyWarningPage Query(string month, string scenario, string level, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        scenario = NormalizeScenario(scenario);
        level = NormalizeLevel(level);
        lock (syncRoot)
        {
            if (!TryGetCollection(month, out var collection))
            {
                return CreatePage(month, scenario, level, page, pageSize, 0, []);
            }
            var query = collection.Query();
            if (!string.Equals(scenario, EarlyWarningRecordScenarios.All, StringComparison.Ordinal))
            {
                query = query.Where(item => item.Scenario == scenario);
            }
            if (!string.Equals(level, EarlyWarningRecordScenarios.All, StringComparison.Ordinal))
            {
                query = query.Where(item => item.Level == level);
            }
            var total = query.Count();
            var items = query.OrderByDescending(item => item.AlarmTime).Skip((page - 1) * pageSize).Limit(pageSize).ToList();
            return CreatePage(month, scenario, level, page, pageSize, total, items);
        }
    }

    /// <summary>按月份和主键查询单条模型预警记录。</summary>
    public bool TryGet(string month, string id, out EarlyWarningRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (syncRoot)
        {
            if (!TryGetCollection(month, out var collection))
            {
                return false;
            }

            record = collection.FindById(id)!;
            return record is not null;
        }
    }

    /// <summary>查询已有模型预警月份。</summary>
    public IReadOnlyList<string> QueryMonths()
    {
        lock (syncRoot)
        {
            return database.GetCollectionNames()
                .Where(name => name.StartsWith(CollectionPrefix, StringComparison.Ordinal))
                .Select(name => name[CollectionPrefix.Length..])
                .Where(IsValidMonth)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(month => month)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (syncRoot)
        {
            database.Dispose();
        }
    }

    private static EarlyWarningPage CreatePage(string month, string scenario, string level, int page, int pageSize, int total, IReadOnlyList<EarlyWarningRecord> items) => new()
    {
        Month = month,
        Scenario = scenario,
        Level = level,
        Page = page,
        PageSize = pageSize,
        Total = total,
        Items = items
    };

    private ILiteCollection<EarlyWarningRecord> GetCollection(string month)
    {
        var name = GetCollectionName(month);
        var collection = database.GetCollection<EarlyWarningRecord>(name);
        EnsureIndexes(name, collection);
        return collection;
    }

    private bool TryGetCollection(string month, out ILiteCollection<EarlyWarningRecord> collection)
    {
        collection = null!;
        var name = GetCollectionName(month);
        if (!database.GetCollectionNames().Contains(name, StringComparer.Ordinal))
        {
            return false;
        }
        collection = database.GetCollection<EarlyWarningRecord>(name);
        EnsureIndexes(name, collection);
        return true;
    }

    private void EnsureIndexes(string name, ILiteCollection<EarlyWarningRecord> collection)
    {
        if (indexedCollections.Contains(name))
        {
            return;
        }
        collection.EnsureIndex(item => item.AlarmTime);
        collection.EnsureIndex(item => item.Scenario);
        collection.EnsureIndex(item => item.Level);
        indexedCollections.Add(name);
    }

    private static bool HasRecentDuplicate(ILiteCollection<EarlyWarningRecord> collection, EarlyWarningPersistCandidate candidate, DateTime alarmTime)
    {
        var windowStart = alarmTime.AddMinutes(-DeduplicateWindowMinutes);
        return collection.Query()
            .Where(item => item.AlarmTime >= windowStart && item.Scenario == candidate.Scenario && item.Level == candidate.Level)
            .Limit(1)
            .FirstOrDefault() is not null;
    }

    private static EarlyWarningRecord CreateRecord(DateTime alarmTime, string month, DateTime windowStart, DateTime windowEnd, string modelVersion, EarlyWarningPersistCandidate candidate) => new()
    {
        Id = $"{alarmTime:yyyyMMddHHmmss}_{candidate.Scenario}_{candidate.Level}",
        AlarmTime = alarmTime,
        Month = month,
        Scenario = candidate.Scenario,
        ScenarioName = candidate.ScenarioName,
        Level = candidate.Level,
        ModelVersion = modelVersion,
        Score = candidate.Score,
        Threshold = candidate.Threshold,
        MetricName = candidate.MetricName,
        Unit = candidate.Unit,
        ActualValue = candidate.ActualValue,
        ReconstructedValue = candidate.ReconstructedValue,
        RelatedPoints = candidate.RelatedPoints,
        Detail = candidate.Detail,
        Recommendations = candidate.Recommendations,
        WindowStart = BoilerTime.TruncateToSecond(windowStart),
        WindowEnd = BoilerTime.TruncateToSecond(windowEnd)
    };

    private static string GetCollectionName(string month) => $"{CollectionPrefix}{month}";

    private static string NormalizeScenario(string? scenario) => string.IsNullOrWhiteSpace(scenario) ? EarlyWarningRecordScenarios.All : scenario.Trim();

    private static string NormalizeLevel(string? level) => string.IsNullOrWhiteSpace(level) ? EarlyWarningRecordScenarios.All : level.Trim();

    private static bool IsValidMonth(string month) => DateTime.TryParseExact(month, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>待持久化的模型预警记录信息。</summary>
public sealed record EarlyWarningPersistCandidate(
    string Scenario,
    string ScenarioName,
    string Level,
    double Score,
    double Threshold,
    string MetricName,
    string Unit,
    double ActualValue,
    double ReconstructedValue,
    string RelatedPoints,
    string Detail,
    string Recommendations);
