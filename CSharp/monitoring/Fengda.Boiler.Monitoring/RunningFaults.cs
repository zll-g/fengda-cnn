using System.Globalization;
using LiteDB;

namespace Fengda.Boiler.Monitoring;

/// <summary>
/// 锅炉运行阶段。
/// </summary>
public static class BoilerOperationPhases
{
    /// <summary>启动初期。</summary>
    public const string Startup = "startup";

    /// <summary>正常运行。</summary>
    public const string Normal = "normal";
}

/// <summary>
/// 运行故障告警等级。
/// </summary>
public static class RunningFaultLevels
{
    /// <summary>预警。</summary>
    public const string Warning = "warning";

    /// <summary>故障。</summary>
    public const string Fault = "fault";
}

/// <summary>
/// 运行故障指标类型。
/// </summary>
public static class RunningFaultMetricTypes
{
    /// <summary>全部指标筛选值。</summary>
    public const string All = "all";

    /// <summary>燃烧器相关指标。</summary>
    public const string Burner = "燃烧器";

    /// <summary>鼓风机相关指标。</summary>
    public const string Blower = "鼓风机";

    /// <summary>烟气环保监测指标。</summary>
    public const string Cems = "CEMS";

    /// <summary>蒸汽压力指标。</summary>
    public const string Pressure = "压力";

    /// <summary>蒸汽温度指标。</summary>
    public const string Temperature = "温度";

    /// <summary>蒸汽流量指标。</summary>
    public const string SteamFlow = "蒸汽流量";

    /// <summary>FGR 相关指标。</summary>
    public const string Fgr = "FGR";

    /// <summary>全部可展示指标类型。</summary>
    public static IReadOnlyList<string> AllDisplayTypes { get; } =
    [
        Burner,
        Blower,
        Cems,
        Pressure,
        Temperature,
        SteamFlow,
        Fgr
    ];
}

/// <summary>
/// 待保存的运行故障候选项。
/// </summary>
public sealed record RunningFaultCandidate(
    string FaultCode,
    string MetricType,
    string MetricName,
    string Level,
    double NumericValue,
    string Unit,
    string NormalRange,
    string Detail,
    string OperationPhase)
{
    /// <summary>当前值，包含单位。</summary>
    public string CurrentValue => RunningFaultValueFormatter.Format(NumericValue, Unit);
}

/// <summary>
/// 已保存的锅炉运行故障记录。
/// </summary>
public sealed class RunningFaultRecord
{
    /// <summary>LiteDB 主键。</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>告警时间，精确到秒。</summary>
    public DateTime AlarmTime { get; set; }

    /// <summary>告警月份，格式 yyyyMM。</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>故障编码。</summary>
    public string FaultCode { get; set; } = string.Empty;

    /// <summary>指标类型。</summary>
    public string MetricType { get; set; } = string.Empty;

    /// <summary>监测指标名称。</summary>
    public string MetricName { get; set; } = string.Empty;

    /// <summary>告警等级，warning 或 fault。</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>当前值，包含单位。</summary>
    public string CurrentValue { get; set; } = string.Empty;

    /// <summary>当前数值。</summary>
    public double NumericValue { get; set; }

    /// <summary>单位。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>正常范围。</summary>
    public string NormalRange { get; set; } = string.Empty;

    /// <summary>故障详情。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>运行阶段。</summary>
    public string OperationPhase { get; set; } = string.Empty;

    /// <summary>触发故障判断的快照时间。</summary>
    public DateTime SnapshotTime { get; set; }
}

/// <summary>
/// 运行故障分页查询结果。
/// </summary>
public sealed class RunningFaultPage
{
    /// <summary>查询月份，格式 yyyyMM。</summary>
    public required string Month { get; init; }

    /// <summary>查询指标类型。</summary>
    public required string MetricType { get; init; }

    /// <summary>当前页码，从 1 开始。</summary>
    public int Page { get; init; }

    /// <summary>每页记录数。</summary>
    public int PageSize { get; init; }

    /// <summary>满足条件的总记录数。</summary>
    public int Total { get; init; }

    /// <summary>当前页故障记录。</summary>
    public required IReadOnlyList<RunningFaultRecord> Items { get; init; }
}

/// <summary>
/// 锅炉运行故障判断服务。
/// </summary>
public static class RunningFaultDetector
{
    /// <summary>
    /// 根据一条锅炉快照判断运行故障候选项。未运行时不返回故障。
    /// </summary>
    public static IReadOnlyList<RunningFaultCandidate> Detect(BoilerSnapshot snapshot)
    {
        if (snapshot.IsRunning is not true)
        {
            return [];
        }

        var faults = new List<RunningFaultCandidate>();
        var phase = ResolveOperationPhase(snapshot);
        var isNormal = string.Equals(phase, BoilerOperationPhases.Normal, StringComparison.Ordinal);
        var isFgrActive = IsFgrActive(snapshot);

        AddBurnerFaults(faults, snapshot, phase);
        AddBlowerFaults(faults, snapshot, phase, isFgrActive);
        AddCemsFaults(faults, snapshot, phase, isNormal);
        AddPressureFaults(faults, snapshot, phase, isNormal);
        AddTemperatureFaults(faults, snapshot, phase, isNormal);
        AddSteamFlowFaults(faults, snapshot, phase, isNormal);
        AddFgrFaults(faults, snapshot, phase, isFgrActive);

        return faults;
    }

    private static string ResolveOperationPhase(BoilerSnapshot snapshot)
    {
        return snapshot.GYDCS_K0QHB10CP101 is { } pressure
            && double.IsFinite(pressure)
            && pressure >= 1.1
            ? BoilerOperationPhases.Normal
            : BoilerOperationPhases.Startup;
    }

    private static bool IsFgrActive(BoilerSnapshot snapshot)
    {
        return IsGreaterThan(snapshot.GYDCS_K0QHH10AV001P, 5)
            || IsGreaterThan(snapshot.GYDCS_K0QHL10AA101AI6, 5);
    }

    private static void AddBurnerFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase)
    {
        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHL10AA101AI4,
            7.6,
            64,
            "OUTER_GAS_VALVE_RANGE",
            RunningFaultMetricTypes.Burner,
            "燃烧器外环气阀位",
            "%",
            "7.6%-64%",
            "燃烧器外环气阀位不在运行允许范围内。",
            phase);
        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHL10AA101AI5,
            7.6,
            62,
            "CENTER_GAS_VALVE_RANGE",
            RunningFaultMetricTypes.Burner,
            "燃烧器中心气阀位",
            "%",
            "7.6%-62%",
            "燃烧器中心气阀位不在运行允许范围内。",
            phase);
    }

    private static void AddBlowerFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase,
        bool isFgrActive)
    {
        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHL10AN001P,
            20,
            48,
            "BLOWER_FREQUENCY_RANGE",
            RunningFaultMetricTypes.Blower,
            "鼓风机频率",
            "Hz",
            "20-48Hz",
            "鼓风机频率不在运行允许范围内。",
            phase);

        var airDamperMax = isFgrActive ? 72 : 35;
        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHL10AA101AI3,
            4,
            airDamperMax,
            "AIR_DAMPER_RANGE",
            RunningFaultMetricTypes.Blower,
            "鼓风机风门开度",
            "%",
            isFgrActive ? "4%-72%（FGR投运）" : "4%-35%（未投运FGR）",
            "鼓风机风门开度不在运行允许范围内。",
            phase);
    }

    private static void AddCemsFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase,
        bool isNormal)
    {
        if (TryGetFinite(snapshot.GYDCS_K0HNE10CQ103, out var oxygen))
        {
            if (oxygen < 3)
            {
                faults.Add(Create(
                    "O2_LOW_FLAMEOUT_RISK",
                    RunningFaultMetricTypes.Cems,
                    "O2排放浓度",
                    RunningFaultLevels.Fault,
                    oxygen,
                    "%",
                    "不低于3%；正常运行4%-6%",
                    "O2排放浓度低于3%，存在熄火风险。",
                    phase));
            }
            else if (isNormal && (oxygen < 4 || oxygen > 6))
            {
                faults.Add(Create(
                    "O2_NORMAL_RANGE",
                    RunningFaultMetricTypes.Cems,
                    "O2排放浓度",
                    RunningFaultLevels.Warning,
                    oxygen,
                    "%",
                    "4%-6%",
                    "正常运行时 O2 排放浓度不在4%-6%范围内。",
                    phase));
            }
        }

        if (TryGetFinite(snapshot.GYDCS_K0HNE10CQ101, out var nox))
        {
            if (nox < 10)
            {
                faults.Add(Create(
                    "NOX_LOW_FLAMEOUT_RISK",
                    RunningFaultMetricTypes.Cems,
                    "NOx排放浓度",
                    RunningFaultLevels.Fault,
                    nox,
                    "mg/m³",
                    "不低于10mg/m³；正常运行30-40mg/m³",
                    "NOx排放浓度低于10mg/m³，存在熄火风险。",
                    phase));
            }
            else if (isNormal && nox > 50)
            {
                faults.Add(Create(
                    "NOX_HIGH_STANDARD",
                    RunningFaultMetricTypes.Cems,
                    "NOx排放浓度",
                    RunningFaultLevels.Fault,
                    nox,
                    "mg/m³",
                    "不超过50mg/m³",
                    "正常运行时 NOx 排放浓度超过50mg/m³。",
                    phase));
            }
            else if (isNormal && (nox < 30 || nox > 40))
            {
                faults.Add(Create(
                    "NOX_NORMAL_RANGE",
                    RunningFaultMetricTypes.Cems,
                    "NOx排放浓度",
                    RunningFaultLevels.Warning,
                    nox,
                    "mg/m³",
                    "30-40mg/m³",
                    "正常运行时 NOx 排放浓度不在30-40mg/m³范围内。",
                    phase));
            }
        }

        AddGreaterThan(
            faults,
            snapshot.GYDCS_K0HNE10CQ102,
            35,
            "SO2_HIGH",
            RunningFaultMetricTypes.Cems,
            "SO2排放浓度",
            RunningFaultLevels.Fault,
            "mg/m³",
            "小于等于35mg/m³",
            "SO2排放浓度超过35mg/m³。",
            phase);
        AddGreaterThan(
            faults,
            snapshot.GYDCS_K0HNE10CQ104,
            10,
            "DUST_HIGH",
            RunningFaultMetricTypes.Cems,
            "粉尘排放浓度",
            RunningFaultLevels.Fault,
            "mg/m³",
            "不超过10mg/m³",
            "粉尘排放浓度超过10mg/m³。",
            phase);
    }

    private static void AddPressureFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase,
        bool isNormal)
    {
        if (!TryGetFinite(snapshot.GYDCS_K0QHB10CP101, out var pressure))
        {
            return;
        }

        if (pressure > 1.7)
        {
            faults.Add(Create(
                "STEAM_PRESSURE_HIGH_TRIP_RISK",
                RunningFaultMetricTypes.Pressure,
                "过热蒸汽压力",
                RunningFaultLevels.Fault,
                pressure,
                "MPa",
                "运行波动1.1-1.3MPa，高于1.7MPa跳炉",
                "过热蒸汽压力高于1.7MPa，存在跳炉风险。",
                phase));
        }
        else if (isNormal && (pressure < 1.1 || pressure > 1.3))
        {
            faults.Add(Create(
                "STEAM_PRESSURE_NORMAL_RANGE",
                RunningFaultMetricTypes.Pressure,
                "过热蒸汽压力",
                RunningFaultLevels.Warning,
                pressure,
                "MPa",
                "1.1-1.3MPa",
                "正常运行时过热蒸汽压力不在1.1-1.3MPa范围内。",
                phase));
        }
    }

    private static void AddTemperatureFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase,
        bool isNormal)
    {
        if (!isNormal)
        {
            return;
        }

        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHB10CT302,
            280,
            310,
            "DESUPERHEATER_OUTLET_TEMPERATURE_RANGE",
            RunningFaultMetricTypes.Temperature,
            "减温器出口蒸汽温度",
            "℃",
            "280-310℃",
            "正常运行时减温器出口蒸汽温度不在280-310℃范围内。",
            phase);
    }

    private static void AddSteamFlowFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase,
        bool isNormal)
    {
        if (!TryGetFinite(snapshot.GYDCS_K0QHB10CF101_COMP1, out var flow))
        {
            return;
        }

        if (flow > 15)
        {
            faults.Add(Create(
                "STEAM_FLOW_HIGH",
                RunningFaultMetricTypes.SteamFlow,
                "出口蒸汽流量",
                RunningFaultLevels.Fault,
                flow,
                "t/h",
                "4-14t/h，最高不超过15t/h",
                "出口蒸汽流量超过15t/h。",
                phase));
        }
        else if (isNormal && (flow < 4 || flow > 14))
        {
            faults.Add(Create(
                "STEAM_FLOW_NORMAL_RANGE",
                RunningFaultMetricTypes.SteamFlow,
                "出口蒸汽流量",
                RunningFaultLevels.Warning,
                flow,
                "t/h",
                "4-14t/h",
                "正常运行时出口蒸汽流量不在4-14t/h范围内。",
                phase));
        }
    }

    private static void AddFgrFaults(
        ICollection<RunningFaultCandidate> faults,
        BoilerSnapshot snapshot,
        string phase,
        bool isFgrActive)
    {
        if (!isFgrActive)
        {
            return;
        }

        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHH10AV001P,
            22,
            40,
            "FGR_FAN_FREQUENCY_RANGE",
            RunningFaultMetricTypes.Fgr,
            "FGR风机频率",
            "Hz",
            "22-40Hz",
            "FGR投运后风机频率不在22-40Hz范围内。",
            phase);
        AddOutsideRange(
            faults,
            snapshot.GYDCS_K0QHL10AA101AI6,
            27,
            65,
            "FGR_VALVE_RANGE",
            RunningFaultMetricTypes.Fgr,
            "FGR调节阀开度",
            "%",
            "27%-65%",
            "FGR投运后调节阀开度不在27%-65%范围内。",
            phase);
    }

    private static void AddOutsideRange(
        ICollection<RunningFaultCandidate> faults,
        double? value,
        double min,
        double max,
        string faultCode,
        string metricType,
        string metricName,
        string unit,
        string normalRange,
        string detail,
        string phase)
    {
        if (TryGetFinite(value, out var number) && (number < min || number > max))
        {
            faults.Add(Create(
                faultCode,
                metricType,
                metricName,
                RunningFaultLevels.Warning,
                number,
                unit,
                normalRange,
                detail,
                phase));
        }
    }

    private static void AddGreaterThan(
        ICollection<RunningFaultCandidate> faults,
        double? value,
        double max,
        string faultCode,
        string metricType,
        string metricName,
        string level,
        string unit,
        string normalRange,
        string detail,
        string phase)
    {
        if (TryGetFinite(value, out var number) && number > max)
        {
            faults.Add(Create(
                faultCode,
                metricType,
                metricName,
                level,
                number,
                unit,
                normalRange,
                detail,
                phase));
        }
    }

    private static RunningFaultCandidate Create(
        string faultCode,
        string metricType,
        string metricName,
        string level,
        double numericValue,
        string unit,
        string normalRange,
        string detail,
        string phase)
    {
        return new RunningFaultCandidate(
            faultCode,
            metricType,
            metricName,
            level,
            numericValue,
            unit,
            normalRange,
            detail,
            phase);
    }

    private static bool TryGetFinite(double? value, out double number)
    {
        number = value.GetValueOrDefault();
        return value is not null && double.IsFinite(number);
    }

    private static bool IsGreaterThan(double? value, double threshold)
    {
        return TryGetFinite(value, out var number) && number > threshold;
    }
}

/// <summary>
/// 锅炉运行故障 LiteDB 存储服务。
/// </summary>
public sealed class RunningFaultStore : IDisposable
{
    private const string CollectionPrefix = "running_faults_";
    private const int DeduplicateWindowMinutes = 5;
    private readonly object syncRoot = new();
    private readonly LiteDatabase database;
    private readonly HashSet<string> indexedCollections = new(StringComparer.Ordinal);

    /// <summary>
    /// 创建运行故障存储服务。
    /// </summary>
    /// <param name="databasePath">独立故障库文件路径，例如 data/faults.db。</param>
    /// <param name="logger">监控日志记录器。</param>
    public RunningFaultStore(string databasePath, IMonitorLogger? logger = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Logger = logger ?? NullMonitorLogger.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? Environment.CurrentDirectory);
        database = new LiteDatabase(new ConnectionString
        {
            Filename = DatabasePath,
            Connection = ConnectionType.Shared
        });
    }

    /// <summary>故障库文件路径。</summary>
    public string DatabasePath { get; }

    private IMonitorLogger Logger { get; }

    /// <summary>
    /// 判断快照运行故障并保存新故障记录。
    /// </summary>
    public IReadOnlyList<RunningFaultRecord> SaveDetectedFaults(
        BoilerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.IsRunning is not true)
        {
            return [];
        }

        var candidates = RunningFaultDetector.Detect(snapshot);
        if (candidates.Count == 0)
        {
            return [];
        }

        return SaveFaults(snapshot.RecordedAt, candidates, cancellationToken);
    }

    /// <summary>
    /// 分页查询指定月份的运行故障记录。
    /// </summary>
    public RunningFaultPage Query(string month, string metricType, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        metricType = NormalizeMetricType(metricType);

        lock (syncRoot)
        {
            if (!TryGetCollection(month, out var collection))
            {
                return new RunningFaultPage
                {
                    Month = month,
                    MetricType = metricType,
                    Page = page,
                    PageSize = pageSize,
                    Total = 0,
                    Items = []
                };
            }

            var total = Count(collection, metricType);
            var items = QueryItems(collection, metricType, page, pageSize);
            return new RunningFaultPage
            {
                Month = month,
                MetricType = metricType,
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = items
            };
        }
    }

    /// <summary>
    /// 查询已有故障月份，按月份倒序返回。
    /// </summary>
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

    /// <summary>
    /// 查询指定月份已有的指标类型；无记录时返回配置中的全部指标类型。
    /// </summary>
    public IReadOnlyList<string> QueryMetricTypes(string? month = null)
    {
        lock (syncRoot)
        {
            if (!string.IsNullOrWhiteSpace(month)
                && IsValidMonth(month)
                && TryGetCollection(month, out var collection))
            {
                var existing = collection
                    .FindAll()
                    .Select(item => item.MetricType)
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(type => type)
                    .ToList();
                if (existing.Count > 0)
                {
                    return existing;
                }
            }

            return RunningFaultMetricTypes.AllDisplayTypes;
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

    private IReadOnlyList<RunningFaultRecord> SaveFaults(
        DateTime snapshotTime,
        IEnumerable<RunningFaultCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var alarmTime = BoilerTime.TruncateToSecond(snapshotTime);
        var month = alarmTime.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var inserted = new List<RunningFaultRecord>();

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

                var record = CreateRecord(alarmTime, snapshotTime, month, candidate);
                collection.Upsert(record);
                inserted.Add(record);
            }
        }

        if (inserted.Count > 0)
        {
            Logger.Warn($"运行故障记录写入完成：{inserted.Count} 条，时间 {alarmTime:yyyy-MM-dd HH:mm:ss}。");
        }

        return inserted;
    }

    private static int Count(ILiteCollection<RunningFaultRecord> collection, string metricType)
    {
        var query = collection.Query();
        if (!string.Equals(metricType, RunningFaultMetricTypes.All, StringComparison.Ordinal))
        {
            query = query.Where(item => item.MetricType == metricType);
        }

        return query.Count();
    }

    private static IReadOnlyList<RunningFaultRecord> QueryItems(
        ILiteCollection<RunningFaultRecord> collection,
        string metricType,
        int page,
        int pageSize)
    {
        var query = collection.Query();
        if (!string.Equals(metricType, RunningFaultMetricTypes.All, StringComparison.Ordinal))
        {
            query = query.Where(item => item.MetricType == metricType);
        }

        return query
            .OrderByDescending(item => item.AlarmTime)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToEnumerable()
            .ToList();
    }

    private bool HasRecentDuplicate(
        ILiteCollection<RunningFaultRecord> collection,
        RunningFaultCandidate candidate,
        DateTime alarmTime)
    {
        var windowStart = alarmTime.AddMinutes(-DeduplicateWindowMinutes);
        return collection
            .Query()
            .Where(item =>
                item.AlarmTime >= windowStart
                && item.FaultCode == candidate.FaultCode
                && item.MetricName == candidate.MetricName
                && item.Level == candidate.Level)
            .Limit(1)
            .FirstOrDefault() is not null;
    }

    private RunningFaultRecord CreateRecord(
        DateTime alarmTime,
        DateTime snapshotTime,
        string month,
        RunningFaultCandidate candidate)
    {
        return new RunningFaultRecord
        {
            Id = BuildId(alarmTime, candidate),
            AlarmTime = alarmTime,
            Month = month,
            FaultCode = candidate.FaultCode,
            MetricType = candidate.MetricType,
            MetricName = candidate.MetricName,
            Level = candidate.Level,
            CurrentValue = candidate.CurrentValue,
            NumericValue = candidate.NumericValue,
            Unit = candidate.Unit,
            NormalRange = candidate.NormalRange,
            Detail = candidate.Detail,
            OperationPhase = candidate.OperationPhase,
            SnapshotTime = BoilerTime.TruncateToSecond(snapshotTime)
        };
    }

    private ILiteCollection<RunningFaultRecord> GetCollection(string month)
    {
        var collectionName = GetCollectionName(month);
        var collection = database.GetCollection<RunningFaultRecord>(collectionName);
        EnsureIndexes(collectionName, collection);
        return collection;
    }

    private bool TryGetCollection(string month, out ILiteCollection<RunningFaultRecord> collection)
    {
        collection = null!;
        var collectionName = GetCollectionName(month);
        if (!database.GetCollectionNames().Contains(collectionName, StringComparer.Ordinal))
        {
            return false;
        }

        collection = database.GetCollection<RunningFaultRecord>(collectionName);
        EnsureIndexes(collectionName, collection);
        return true;
    }

    private void EnsureIndexes(string collectionName, ILiteCollection<RunningFaultRecord> collection)
    {
        if (indexedCollections.Contains(collectionName))
        {
            return;
        }

        collection.EnsureIndex(item => item.AlarmTime);
        collection.EnsureIndex(item => item.MetricType);
        collection.EnsureIndex(item => item.FaultCode);
        indexedCollections.Add(collectionName);
    }

    private static string BuildId(DateTime alarmTime, RunningFaultCandidate candidate)
    {
        return string.Join(
            "_",
            alarmTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            candidate.FaultCode,
            candidate.Level);
    }

    private static string GetCollectionName(string month)
    {
        return $"{CollectionPrefix}{month}";
    }

    private static string NormalizeMetricType(string? metricType)
    {
        return string.IsNullOrWhiteSpace(metricType)
            ? RunningFaultMetricTypes.All
            : metricType.Trim();
    }

    private static bool IsValidMonth(string month)
    {
        return DateTime.TryParseExact(
            month,
            "yyyyMM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }
}

internal static class RunningFaultValueFormatter
{
    public static string Format(double value, string unit)
    {
        var text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }
}
