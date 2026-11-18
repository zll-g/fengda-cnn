using Fengda.Boiler.WebApi.Models;
using LiteDB;

namespace Fengda.Boiler.WebApi.Services;

/// <summary>独立 LiteDB 排放边界趋势与预警记录存储。</summary>
public sealed class EmissionBoundsStore : IDisposable
{
    private const string TrendPrefix = "emission_bounds_";
    private const string EventPrefix = "emission_events_";
    private readonly object syncRoot = new();
    private readonly LiteDatabase database;
    private readonly HashSet<string> indexedCollections = new(StringComparer.Ordinal);

    /// <summary>创建排放边界独立数据库。</summary>
    public EmissionBoundsStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        database = new LiteDatabase(fullPath);
    }

    /// <summary>按分钟主键保存模型评估曲线。</summary>
    public void SaveMinuteSample(EmissionBoundsTrendDto sample)
    {
        var collectionName = TrendCollection(sample.RecordedAt);
        lock (syncRoot)
        {
            var collection = GetCollection<StoredTrend>(collectionName);
            collection.Upsert(new StoredTrend
            {
                Id = sample.RecordedAt.ToString("yyyyMMddHHmm"),
                ModelArtifactId = sample.ModelArtifactId,
                RecordedAt = sample.RecordedAt,
                NoxActual = sample.NoxActual,
                NoxLower = sample.NoxLower,
                NoxUpper = sample.NoxUpper,
                O2Actual = sample.O2Actual,
                O2Lower = sample.O2Lower,
                O2Upper = sample.O2Upper
            });
        }
    }

    /// <summary>保存模型边界预警或运行风险下限提示。</summary>
    public void SaveEvent(EmissionBoundsEventDto item)
    {
        var collectionName = EventCollection(item.RecordedAt);
        lock (syncRoot)
        {
            GetCollection<StoredEvent>(collectionName).Upsert(new StoredEvent
            {
                Id = item.Id,
                ModelArtifactId = item.ModelArtifactId,
                RecordedAt = item.RecordedAt,
                Metric = item.Metric,
                Direction = item.Direction,
                Source = item.Source,
                Level = item.Level,
                Score = item.Score,
                ScoreThreshold = item.ScoreThreshold,
                ActualValue = item.ActualValue,
                LowerBound = item.LowerBound,
                UpperBound = item.UpperBound,
                Unit = item.Unit,
                Description = item.Description
            });
        }
    }

    /// <summary>查询同指标、同来源、同方向在指定时间之后的最新记录，用于服务重启后的冷却抑制。</summary>
    public DateTime? FindLatestEventTime(string metric, string direction, string source, string level,
        string? modelArtifactId, DateTime since, DateTime through)
    {
        var months = new HashSet<string>(StringComparer.Ordinal)
        {
            since.ToString("yyyyMM"),
            through.ToString("yyyyMM")
        };
        lock (syncRoot)
        {
            DateTime? latest = null;
            foreach (var month in months)
            {
                var collectionName = EventPrefix + month;
                if (!database.CollectionExists(collectionName))
                {
                    continue;
                }
                var recent = GetCollection<StoredEvent>(collectionName)
                    .FindAll()
                    .Where(item => item.RecordedAt >= since && item.RecordedAt <= through
                        && string.Equals(item.Metric, metric, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.Direction, direction, StringComparison.Ordinal)
                        && string.Equals(item.Source ?? "model_boundary", source, StringComparison.Ordinal)
                        && string.Equals(item.Level ?? (item.Source == "operating_limit" ? "risk" : "warning"), level, StringComparison.Ordinal)
                        && string.Equals(item.ModelArtifactId, modelArtifactId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => (DateTime?)item.RecordedAt)
                    .Max();
                if (recent.HasValue && (latest is null || recent > latest))
                {
                    latest = recent;
                }
            }
            return latest;
        }
    }

    /// <summary>读取指定自然日保存的分钟级模型趋势。</summary>
    public IReadOnlyList<EmissionBoundsTrendDto> QueryDay(DateTime day, string? modelArtifactId)
    {
        day = day.Date;
        var collectionName = TrendCollection(day);
        lock (syncRoot)
        {
            var collection = GetCollection<StoredTrend>(collectionName);
            return collection.Find(item => item.RecordedAt >= day && item.RecordedAt < day.AddDays(1)
                    && item.ModelArtifactId == modelArtifactId)
                .OrderBy(item => item.RecordedAt)
                .Select(item => new EmissionBoundsTrendDto
                {
                    ModelArtifactId = item.ModelArtifactId,
                    RecordedAt = item.RecordedAt,
                    NoxActual = item.NoxActual,
                    NoxLower = item.NoxLower,
                    NoxUpper = item.NoxUpper,
                    O2Actual = item.O2Actual,
                    O2Lower = item.O2Lower,
                    O2Upper = item.O2Upper
                })
                .ToList();
        }
    }

    /// <summary>分页读取指定月份的排放预警记录。</summary>
    public (int Total, IReadOnlyList<EmissionBoundsEventDto> Items) QueryEvents(string month, string metric, int page, int pageSize)
    {
        var collectionName = EventPrefix + month;
        lock (syncRoot)
        {
            var collection = GetCollection<StoredEvent>(collectionName);
            var query = collection.FindAll()
                .Where(item => string.Equals(metric, "all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Metric, metric, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.RecordedAt)
                .ToList();
            var total = query.Count;
            var items = query.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(item => new EmissionBoundsEventDto
                {
                    Id = item.Id,
                    ModelArtifactId = item.ModelArtifactId,
                    RecordedAt = item.RecordedAt,
                    Metric = item.Metric,
                    Direction = item.Direction,
                    Source = item.Source ?? "model_boundary",
                    Level = item.Level ?? (item.Source == "operating_limit" ? "risk" : "warning"),
                    Score = item.Score,
                    ScoreThreshold = item.ScoreThreshold,
                    ActualValue = item.ActualValue,
                    LowerBound = item.LowerBound,
                    UpperBound = item.UpperBound,
                    Unit = item.Unit,
                    Description = item.Source is null
                        ? item.Description.Replace(
                            "仅为观察记录，不代表法规超标或设备报警。",
                            "此为模型工况预警，不代表法规超标或设备故障，也不触发控制动作。",
                            StringComparison.Ordinal)
                        : item.Description
                })
                .ToList();
            return (total, items);
        }
    }

    /// <summary>列出已存储的年月集合。</summary>
    public IReadOnlyList<string> QueryMonths()
    {
        lock (syncRoot)
        {
            return database.GetCollectionNames()
                .Where(name => name.StartsWith(EventPrefix, StringComparison.Ordinal))
                .Select(name => name[EventPrefix.Length..])
                .Where(month => month.Length == 6)
                .OrderByDescending(month => month, StringComparer.Ordinal)
                .ToList();
        }
    }

    private ILiteCollection<T> GetCollection<T>(string name) where T : class
    {
        var collection = database.GetCollection<T>(name);
        if (indexedCollections.Add(name))
        {
            if (typeof(T) == typeof(StoredTrend))
            {
                ((ILiteCollection<StoredTrend>)collection).EnsureIndex(item => item.RecordedAt);
            }
            else if (typeof(T) == typeof(StoredEvent))
            {
                ((ILiteCollection<StoredEvent>)collection).EnsureIndex(item => item.RecordedAt);
            }
        }
        return collection;
    }

    private static string TrendCollection(DateTime time) => TrendPrefix + time.ToString("yyyyMM");
    private static string EventCollection(DateTime time) => EventPrefix + time.ToString("yyyyMM");

    /// <inheritdoc />
    public void Dispose() => database.Dispose();

    private sealed class StoredTrend
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string? ModelArtifactId { get; set; }
        public DateTime RecordedAt { get; set; }
        public double? NoxActual { get; set; }
        public double? NoxLower { get; set; }
        public double? NoxUpper { get; set; }
        public double? O2Actual { get; set; }
        public double? O2Lower { get; set; }
        public double? O2Upper { get; set; }
    }

    private sealed class StoredEvent
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string? ModelArtifactId { get; set; }
        public DateTime RecordedAt { get; set; }
        public string Metric { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string? Source { get; set; }
        public string? Level { get; set; }
        public double? Score { get; set; }
        public double? ScoreThreshold { get; set; }
        public double ActualValue { get; set; }
        public double LowerBound { get; set; }
        public double UpperBound { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
