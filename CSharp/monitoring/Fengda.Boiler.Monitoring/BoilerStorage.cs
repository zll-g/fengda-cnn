using System.Globalization;
using System.Reflection;
using LiteDB;

namespace Fengda.Boiler.Monitoring;

/// <summary>
/// 锅炉时间处理工具。
/// </summary>
public static class BoilerTime
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <summary>
    /// 获取中国标准时间，并截断到整秒。
    /// </summary>
    public static DateTime ChinaNow => TruncateToSecond(DateTimeOffset.UtcNow.ToOffset(ChinaOffset).DateTime);

    /// <summary>
    /// 将时间截断到整秒。
    /// </summary>
    public static DateTime TruncateToSecond(DateTime value)
    {
        return new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);
    }
}

/// <summary>
/// 保存最近 60 秒锅炉快照的线程安全缓存。
/// </summary>
public sealed class BoilerSnapshotCache
{
    private const int Capacity = 60;
    private readonly object syncRoot = new();
    private readonly SortedDictionary<DateTime, BoilerSnapshot> snapshots = [];

    /// <summary>
    /// 当前缓存记录数。
    /// </summary>
    public int Count
    {
        get
        {
            lock (syncRoot)
            {
                return snapshots.Count;
            }
        }
    }

    /// <summary>
    /// 当前缓存中的最新记录时间。
    /// </summary>
    public DateTime? LatestTimestamp
    {
        get
        {
            lock (syncRoot)
            {
                return snapshots.Count == 0 ? null : snapshots.Keys.Last();
            }
        }
    }

    /// <summary>
    /// 添加或更新一条整秒快照，并仅保留最近 60 条。
    /// </summary>
    public void AddOrUpdate(BoilerSnapshot snapshot)
    {
        var copy = snapshot.Copy();
        copy.RecordedAt = BoilerTime.TruncateToSecond(copy.RecordedAt);

        lock (syncRoot)
        {
            snapshots[copy.RecordedAt] = copy;
            while (snapshots.Count > Capacity)
            {
                snapshots.Remove(snapshots.Keys.First());
            }
        }
    }

    /// <summary>
    /// 使用数据库中读取到的最近快照初始化缓存，并仅保留最后 60 条。
    /// </summary>
    public void ReplaceWith(IEnumerable<BoilerSnapshot> initialSnapshots)
    {
        var copies = initialSnapshots
            .Select(snapshot =>
            {
                var copy = snapshot.Copy();
                copy.RecordedAt = BoilerTime.TruncateToSecond(copy.RecordedAt);
                return copy;
            })
            .GroupBy(snapshot => snapshot.RecordedAt)
            .Select(group => group.Last())
            .OrderBy(snapshot => snapshot.RecordedAt)
            .TakeLast(Capacity)
            .ToList();

        lock (syncRoot)
        {
            snapshots.Clear();
            foreach (var snapshot in copies)
            {
                snapshots[snapshot.RecordedAt] = snapshot;
            }
        }
    }

    /// <summary>
    /// 按时间升序返回最近指定数量的缓存快照。
    /// </summary>
    public IReadOnlyList<BoilerSnapshot> GetLatest(int count)
    {
        lock (syncRoot)
        {
            return snapshots.Values
                .TakeLast(Math.Clamp(count, 0, Capacity))
                .Select(snapshot => snapshot.Copy())
                .ToList();
        }
    }
}

/// <summary>
/// 基于 LiteDB 月文件、日集合的锅炉历史快照存储服务。
/// </summary>
public sealed class BoilerSnapshotStore : IDisposable
{
    private const string DailyCollectionPrefix = "snapshots_";
    private const string DailyForecastCollectionPrefix = "forecast_minutes_";
    private const string LegacyCollectionPrefix = "boiler_snapshots_";
    private static readonly IReadOnlyDictionary<string, PropertyInfo> LegacyPointProperties = typeof(LegacySnapshot)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.Name.StartsWith("GYDCS_", StringComparison.Ordinal))
        .ToDictionary(property => property.Name, StringComparer.Ordinal);
    private readonly object syncRoot = new();
    private readonly Dictionary<string, LiteDatabase> monthDatabases = new(StringComparer.Ordinal);
    private readonly HashSet<string> indexedCollections = new(StringComparer.Ordinal);
    private readonly IMonitorLogger logger;
    private readonly string? legacyDatabasePath;
    private LiteDatabase? legacyDatabase;
    private LiteDatabase? legacyArchiveDatabase;
    private BoilerSnapshot? latestSnapshot;

    /// <summary>
    /// 创建锅炉历史快照存储。
    /// </summary>
    /// <param name="storageDirectory">月度 LiteDB 文件目录，例如 data。</param>
    /// <param name="logger">监控日志记录器。</param>
    /// <param name="legacyDatabasePath">旧版单文件 LiteDB 路径，仅用于只读兼容。</param>
    public BoilerSnapshotStore(
        string storageDirectory,
        IMonitorLogger? logger = null,
        string? legacyDatabasePath = null)
    {
        StorageDirectory = Path.GetFullPath(storageDirectory);
        this.logger = logger ?? NullMonitorLogger.Instance;
        Directory.CreateDirectory(StorageDirectory);

        if (!string.IsNullOrWhiteSpace(legacyDatabasePath))
        {
            var fullLegacyPath = Path.GetFullPath(legacyDatabasePath);
            if (File.Exists(fullLegacyPath))
            {
                this.legacyDatabasePath = fullLegacyPath;
                try
                {
                    legacyDatabase = OpenDatabase(fullLegacyPath);
                    var archivePath = $"{fullLegacyPath}2";
                    if (File.Exists(archivePath))
                    {
                        legacyArchiveDatabase = OpenDatabase(archivePath);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Warn($"无法打开旧版 LiteDB 文件 {fullLegacyPath}，将忽略旧数据：{ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 月度 LiteDB 文件保存目录。
    /// </summary>
    public string StorageDirectory { get; }

    /// <summary>
    /// 旧版单文件 LiteDB 路径；不存在时为空。
    /// </summary>
    public string? LegacyDatabasePath => legacyDatabasePath;

    /// <summary>
    /// 保存快照；同一秒使用 yyyyMMddHHmmss 主键直接更新。
    /// </summary>
    public Task SaveAsync(BoilerSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = snapshot.Copy();
        copy.RecordedAt = BoilerTime.TruncateToSecond(copy.RecordedAt);
        copy.Id = ToSecondKey(copy.RecordedAt);

        lock (syncRoot)
        {
            GetCollection(copy.RecordedAt).Upsert(copy);
            if (latestSnapshot is null || copy.RecordedAt >= latestSnapshot.RecordedAt)
            {
                latestSnapshot = copy.Copy();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 查询指定时间开始的数据，结果不跨越开始时间所在自然日。
    /// </summary>
    public IReadOnlyList<BoilerSnapshot> QueryDaily(DateTime startTime, int count)
    {
        startTime = BoilerTime.TruncateToSecond(startTime);
        var dayEnd = startTime.Date.AddDays(1);
        lock (syncRoot)
        {
            return ReadNormalizedAscending(EnumerateSnapshots(startTime, dayEnd, descending: false), count);
        }
    }

    /// <summary>
    /// 查询指定自然日的最后一条秒级快照。
    /// </summary>
    /// <param name="day">需要查询的自然日。</param>
    /// <returns>当天最后一条快照；当天没有记录时返回 <see langword="null"/>。</returns>
    public BoilerSnapshot? QueryDailyLatest(DateTime day)
    {
        day = day.Date;
        lock (syncRoot)
        {
            return ReadNormalizedDescending(
                    EnumerateSnapshots(day, day.AddDays(1), descending: true),
                    1)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// 查询数据库最新数据所在自然日的最后若干条记录，返回结果按时间升序排列。
    /// </summary>
    public IReadOnlyList<BoilerSnapshot> QueryLatest(int count)
    {
        lock (syncRoot)
        {
            var latest = FindLatestSnapshot();
            if (latest is null)
            {
                return [];
            }

            var dayStart = latest.RecordedAt.Date;
            return ReadNormalizedDescending(EnumerateSnapshots(dayStart, dayStart.AddDays(1), descending: true), count);
        }
    }

    /// <summary>
    /// 查询指定整秒的详细快照。
    /// </summary>
    public BoilerSnapshot? QueryDetail(DateTime time)
    {
        time = BoilerTime.TruncateToSecond(time);
        lock (syncRoot)
        {
            if (TryGetCollection(time, out var collection))
            {
                var result = collection.FindById(ToSecondKey(time));
                if (result is not null)
                {
                    return CopyWithSecond(result, time);
                }
            }

            return EnumerateLegacySnapshots(time, time.AddSeconds(1), descending: true)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// 查询包含起止时间的连续时间范围，可跨越自然日和月份。
    /// </summary>
    public IReadOnlyList<BoilerSnapshot> QueryRange(DateTime startTime, DateTime endTime)
    {
        startTime = BoilerTime.TruncateToSecond(startTime);
        endTime = BoilerTime.TruncateToSecond(endTime);
        if (endTime < startTime)
        {
            return [];
        }

        var endExclusive = endTime.AddSeconds(1);
        List<BoilerSnapshot> snapshots;
        lock (syncRoot)
        {
            snapshots = EnumerateSnapshots(startTime, endExclusive, descending: false).ToList();
        }

        return NormalizeToSeconds(snapshots);
    }

    /// <summary>
    /// 保存一次按分钟汇总的模型预测。同一分钟重复写入时覆盖旧结果。
    /// </summary>
    public void SaveMinuteForecast(BoilerMinuteForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        forecast.AnchorTime = TruncateToMinute(forecast.AnchorTime);
        forecast.ForecastStartTime = BoilerTime.TruncateToSecond(forecast.ForecastStartTime);
        forecast.CachedAt = BoilerTime.TruncateToSecond(forecast.CachedAt);
        forecast.Id = ToMinuteKey(forecast.AnchorTime);

        lock (syncRoot)
        {
            GetForecastCollection(forecast.AnchorTime).Upsert(forecast);
        }
    }

    /// <summary>
    /// 读取指定自然日已缓存的分钟预测，按输入窗口起始时间升序返回。
    /// </summary>
    public IReadOnlyList<BoilerMinuteForecast> QueryDailyMinuteForecasts(DateTime day)
    {
        day = day.Date;
        lock (syncRoot)
        {
            if (!TryGetForecastCollection(day, out var collection))
            {
                return [];
            }

            return collection.Query()
                .OrderBy(item => item.AnchorTime)
                .ToList();
        }
    }

    /// <summary>
    /// 返回当前存储目录中存在快照集合的日期。该方法只读取集合名称，不会加载快照内容。
    /// </summary>
    public IReadOnlyList<DateTime> GetAvailableDays()
    {
        lock (syncRoot)
        {
            var days = new SortedSet<DateTime>();
            foreach (var month in Directory.EnumerateFiles(StorageDirectory, "*.db")
                         .Select(Path.GetFileNameWithoutExtension)
                         .Where(name => DateTime.TryParseExact(
                             name,
                             "yyyyMM",
                             CultureInfo.InvariantCulture,
                             DateTimeStyles.None,
                             out _)))
            {
                var monthStart = DateTime.ParseExact(month!, "yyyyMM", CultureInfo.InvariantCulture);
                var database = GetDatabase(monthStart, createIfMissing: false);
                foreach (var collectionName in database.GetCollectionNames()
                             .Where(name => name.StartsWith(DailyCollectionPrefix, StringComparison.Ordinal)))
                {
                    var datePart = collectionName[DailyCollectionPrefix.Length..];
                    if (DateTime.TryParseExact(
                        datePart,
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var day))
                    {
                        days.Add(day.Date);
                    }
                }
            }

            return days.ToList();
        }
    }

    /// <summary>返回指定日期的新格式快照集合记录数，不加载快照内容。</summary>
    public long GetDailyRecordCount(DateTime day)
    {
        return TryGetDailyRecordCount(day, out var count, out _) ? count : 0;
    }

    /// <summary>
    /// 尝试返回指定日期的新格式快照集合记录数；LiteDB 文件或索引异常时不抛出异常。
    /// </summary>
    /// <param name="day">需要统计的自然日。</param>
    /// <param name="count">读取成功时的集合记录数。</param>
    /// <param name="errorMessage">读取失败时的中文错误说明。</param>
    /// <returns>集合可正常读取时为 <see langword="true"/>。</returns>
    public bool TryGetDailyRecordCount(DateTime day, out long count, out string? errorMessage)
    {
        day = day.Date;
        try
        {
            lock (syncRoot)
            {
                count = TryGetCollection(day, out var collection) ? collection.Count() : 0;
                errorMessage = null;
                return true;
            }
        }
        catch (LiteException exception)
        {
            count = 0;
            errorMessage = $"LiteDB 索引读取失败: {exception.Message}";
            logger.Warn($"无法读取 {day:yyyy-MM-dd} 快照集合记录数：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按天顺序访问指定范围内的秒级快照。访问回调在存储锁内执行，
    /// 调用方应仅执行轻量、顺序的写入操作，避免长时间阻塞实时保存。
    /// </summary>
    /// <param name="startTime">起始时间，包含该时间。</param>
    /// <param name="endTime">结束时间，包含该时间。</param>
    /// <param name="visitor">每条归一化到秒的快照访问回调。</param>
    public void VisitRange(DateTime startTime, DateTime endTime, Action<BoilerSnapshot> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        startTime = BoilerTime.TruncateToSecond(startTime);
        endTime = BoilerTime.TruncateToSecond(endTime);
        if (endTime < startTime)
        {
            return;
        }

        foreach (var day in EnumerateDays(startTime, endTime))
        {
            var dayStart = day == startTime.Date ? startTime : day;
            var dayEnd = day == endTime.Date ? endTime.AddSeconds(1) : day.AddDays(1);
            lock (syncRoot)
            {
                foreach (var snapshot in EnumerateDaySnapshots(dayStart, dayEnd, descending: false))
                {
                    visitor(CopyWithSecond(snapshot, BoilerTime.TruncateToSecond(snapshot.RecordedAt)));
                }
            }
        }
    }

    /// <summary>
    /// 尝试按天顺序访问指定范围内的秒级快照；LiteDB 文件或索引异常时不抛出异常。
    /// </summary>
    /// <param name="startTime">起始时间，包含该时间。</param>
    /// <param name="endTime">结束时间，包含该时间。</param>
    /// <param name="visitor">每条归一化到秒的快照访问回调。</param>
    /// <param name="errorMessage">读取失败时的中文错误说明。</param>
    /// <returns>范围数据可正常读取时为 <see langword="true"/>。</returns>
    public bool TryVisitRange(
        DateTime startTime,
        DateTime endTime,
        Action<BoilerSnapshot> visitor,
        out string? errorMessage)
    {
        try
        {
            VisitRange(startTime, endTime, visitor);
            errorMessage = null;
            return true;
        }
        catch (LiteException exception)
        {
            errorMessage = $"LiteDB 索引读取失败: {exception.Message}";
            logger.Warn($"无法读取 {startTime:yyyy-MM-dd HH:mm:ss} 至 {endTime:yyyy-MM-dd HH:mm:ss} 快照：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 查询时间范围内的数据，并在读取过程中按指定秒数降采样。
    /// </summary>
    public IReadOnlyList<BoilerSnapshot> QueryRangeSampled(DateTime startTime, DateTime endTime, int intervalSeconds)
    {
        startTime = BoilerTime.TruncateToSecond(startTime);
        endTime = BoilerTime.TruncateToSecond(endTime);
        if (endTime < startTime || intervalSeconds <= 0)
        {
            return [];
        }

        var samples = new List<BoilerSnapshot>();
        BoilerSnapshot? latestInSecond = null;
        BoilerSnapshot? latestInBucket = null;
        var currentSecond = default(DateTime);
        var currentBucket = -1;

        void AddNormalizedSnapshot(BoilerSnapshot snapshot, DateTime second)
        {
            var bucket = (int)(second - startTime).TotalSeconds / intervalSeconds;
            var normalized = CopyWithSecond(snapshot, second);
            if (currentBucket == bucket)
            {
                latestInBucket = normalized;
                return;
            }

            if (latestInBucket is not null)
            {
                samples.Add(latestInBucket);
            }

            currentBucket = bucket;
            latestInBucket = normalized;
        }

        lock (syncRoot)
        {
            foreach (var snapshot in EnumerateSnapshots(startTime, endTime.AddSeconds(1), descending: false))
            {
                var second = BoilerTime.TruncateToSecond(snapshot.RecordedAt);
                if (latestInSecond is null)
                {
                    latestInSecond = snapshot;
                    currentSecond = second;
                    continue;
                }

                if (second == currentSecond)
                {
                    latestInSecond = snapshot;
                    continue;
                }

                AddNormalizedSnapshot(latestInSecond, currentSecond);
                latestInSecond = snapshot;
                currentSecond = second;
            }
        }

        if (latestInSecond is not null)
        {
            AddNormalizedSnapshot(latestInSecond, currentSecond);
        }

        if (latestInBucket is not null)
        {
            samples.Add(latestInBucket);
        }

        return samples;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (syncRoot)
        {
            foreach (var database in monthDatabases.Values)
            {
                database.Dispose();
            }

            monthDatabases.Clear();
            legacyDatabase?.Dispose();
            legacyDatabase = null;
            legacyArchiveDatabase?.Dispose();
            legacyArchiveDatabase = null;
        }
    }

    private IEnumerable<BoilerSnapshot> EnumerateSnapshots(DateTime startTime, DateTime endExclusive, bool descending)
    {
        var days = EnumerateDays(startTime, endExclusive.AddTicks(-1));
        var orderedDays = descending ? days.Reverse() : days;
        foreach (var day in orderedDays)
        {
            var rangeStart = startTime > day ? startTime : day;
            var dayEnd = day.AddDays(1);
            var rangeEnd = endExclusive < dayEnd ? endExclusive : dayEnd;
            if (rangeStart >= rangeEnd)
            {
                continue;
            }

            foreach (var snapshot in EnumerateDaySnapshots(rangeStart, rangeEnd, descending))
            {
                yield return snapshot;
            }
        }
    }

    private IEnumerable<BoilerSnapshot> EnumerateDaySnapshots(DateTime startTime, DateTime endExclusive, bool descending)
    {
        if (TryGetCollection(startTime, out var collection))
        {
            var query = collection
                .Query()
                .Where(item => item.RecordedAt >= startTime && item.RecordedAt < endExclusive);
            var snapshots = descending
                ? query.OrderByDescending(item => item.RecordedAt).ToEnumerable()
                : query.OrderBy(item => item.RecordedAt).ToEnumerable();
            foreach (var snapshot in snapshots)
            {
                yield return snapshot;
            }

            yield break;
        }

        foreach (var snapshot in EnumerateLegacySnapshots(startTime, endExclusive, descending))
        {
            yield return snapshot;
        }
    }

    private IEnumerable<BoilerSnapshot> EnumerateLegacySnapshots(DateTime startTime, DateTime endExclusive, bool descending)
    {
        var collectionName = $"{LegacyCollectionPrefix}{startTime:yyyyMM}";
        foreach (var database in EnumerateLegacyDatabases())
        {
            if (!database.GetCollectionNames().Contains(collectionName, StringComparer.Ordinal))
            {
                continue;
            }

            var foundSnapshot = false;
            var collection = database.GetCollection<LegacySnapshot>(collectionName);
            var query = collection
                .Query()
                .Where(item => item.RecordedAt >= startTime && item.RecordedAt < endExclusive);
            var snapshots = descending
                ? query.OrderByDescending(item => item.RecordedAt).ToEnumerable()
                : query.OrderBy(item => item.RecordedAt).ToEnumerable();
            foreach (var snapshot in snapshots)
            {
                foundSnapshot = true;
                yield return ConvertLegacySnapshot(snapshot);
            }

            if (foundSnapshot)
            {
                yield break;
            }
        }
    }

    private ILiteCollection<BoilerSnapshot> GetCollection(DateTime time)
    {
        var database = GetDatabase(time, createIfMissing: true);
        var collectionName = GetDailyCollectionName(time);
        var collection = database.GetCollection<BoilerSnapshot>(collectionName);
        EnsureRecordedAtIndex(time, collectionName, collection);
        return collection;
    }

    private ILiteCollection<BoilerMinuteForecast> GetForecastCollection(DateTime time)
    {
        var database = GetDatabase(time, createIfMissing: true);
        return database.GetCollection<BoilerMinuteForecast>(GetDailyForecastCollectionName(time));
    }

    private bool TryGetForecastCollection(DateTime time, out ILiteCollection<BoilerMinuteForecast> collection)
    {
        collection = null!;
        if (!TryGetDatabase(time, out var database))
        {
            return false;
        }

        var collectionName = GetDailyForecastCollectionName(time);
        if (!database.GetCollectionNames().Contains(collectionName, StringComparer.Ordinal))
        {
            return false;
        }

        collection = database.GetCollection<BoilerMinuteForecast>(collectionName);
        return true;
    }

    private bool TryGetCollection(DateTime time, out ILiteCollection<BoilerSnapshot> collection)
    {
        collection = null!;
        if (!TryGetDatabase(time, out var database))
        {
            return false;
        }

        var collectionName = GetDailyCollectionName(time);
        if (!database.GetCollectionNames().Contains(collectionName, StringComparer.Ordinal))
        {
            return false;
        }

        collection = database.GetCollection<BoilerSnapshot>(collectionName);
        EnsureRecordedAtIndex(time, collectionName, collection);
        return true;
    }

    private LiteDatabase GetDatabase(DateTime time, bool createIfMissing)
    {
        var monthKey = time.ToString("yyyyMM", CultureInfo.InvariantCulture);
        if (monthDatabases.TryGetValue(monthKey, out var database))
        {
            return database;
        }

        var databasePath = GetDatabasePath(time);
        if (!createIfMissing && !File.Exists(databasePath))
        {
            throw new FileNotFoundException("未找到指定月份的 LiteDB 文件。", databasePath);
        }

        database = OpenDatabase(databasePath);
        monthDatabases[monthKey] = database;
        return database;
    }

    private bool TryGetDatabase(DateTime time, out LiteDatabase database)
    {
        var databasePath = GetDatabasePath(time);
        if (!File.Exists(databasePath))
        {
            database = null!;
            return false;
        }

        database = GetDatabase(time, createIfMissing: false);
        return true;
    }

    private BoilerSnapshot? FindLatestSnapshot()
    {
        if (latestSnapshot is not null)
        {
            return latestSnapshot.Copy();
        }

        foreach (var month in EnumerateExistingMonthsDescending())
        {
            var database = GetDatabase(month, createIfMissing: false);
            foreach (var collectionName in database.GetCollectionNames()
                .Where(name => name.StartsWith(DailyCollectionPrefix, StringComparison.Ordinal))
                .OrderByDescending(name => name))
            {
                var collection = database.GetCollection<BoilerSnapshot>(collectionName);
                EnsureRecordedAtIndex(month, collectionName, collection);
                var latest = collection
                    .Query()
                    .OrderByDescending(item => item.RecordedAt)
                    .Limit(1)
                    .FirstOrDefault();
                if (latest is not null)
                {
                    latestSnapshot = latest.Copy();
                    return latest;
                }
            }
        }

        var legacyLatest = FindLegacyLatestSnapshot();
        if (legacyLatest is not null)
        {
            latestSnapshot = legacyLatest.Copy();
        }

        return legacyLatest;
    }

    private BoilerSnapshot? FindLegacyLatestSnapshot()
    {
        BoilerSnapshot? latestSnapshot = null;
        foreach (var database in EnumerateLegacyDatabases())
        {
            foreach (var collectionName in database.GetCollectionNames()
                .Where(name => name.StartsWith(LegacyCollectionPrefix, StringComparison.Ordinal))
                .OrderByDescending(name => name))
            {
                var collection = database.GetCollection<LegacySnapshot>(collectionName);
                var latest = collection
                    .Query()
                    .OrderByDescending(item => item.RecordedAt)
                    .Limit(1)
                    .FirstOrDefault();
                if (latest is null)
                {
                    continue;
                }

                var converted = ConvertLegacySnapshot(latest);
                if (latestSnapshot is null || converted.RecordedAt > latestSnapshot.RecordedAt)
                {
                    latestSnapshot = converted;
                }

                break;
            }
        }

        return latestSnapshot;
    }

    private IEnumerable<LiteDatabase> EnumerateLegacyDatabases()
    {
        if (legacyDatabase is not null)
        {
            yield return legacyDatabase;
        }

        if (legacyArchiveDatabase is not null)
        {
            yield return legacyArchiveDatabase;
        }
    }

    private void EnsureRecordedAtIndex(
        DateTime month,
        string collectionName,
        ILiteCollection<BoilerSnapshot> collection)
    {
        var indexKey = $"{month:yyyyMM}/{collectionName}";
        if (!indexedCollections.Contains(indexKey))
        {
            collection.EnsureIndex(item => item.RecordedAt);
            indexedCollections.Add(indexKey);
        }
    }

    private string GetDatabasePath(DateTime time)
    {
        return Path.Combine(StorageDirectory, $"{time:yyyyMM}.db");
    }

    private static LiteDatabase OpenDatabase(string databasePath)
    {
        return new LiteDatabase(new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Shared
        });
    }

    private static string GetDailyCollectionName(DateTime time)
    {
        return $"{DailyCollectionPrefix}{time:yyyyMMdd}";
    }

    private static string GetDailyForecastCollectionName(DateTime time)
    {
        return $"{DailyForecastCollectionPrefix}{time:yyyyMMdd}";
    }

    private static DateTime TruncateToMinute(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
    }

    private static long ToMinuteKey(DateTime value)
    {
        return long.Parse(value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private IEnumerable<DateTime> EnumerateExistingMonthsDescending()
    {
        return Directory
            .EnumerateFiles(StorageDirectory, "*.db")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => DateTime.TryParseExact(
                name,
                "yyyyMM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var month)
                ? (DateTime?)month
                : null)
            .Where(month => month is not null)
            .Select(month => month!.Value)
            .OrderByDescending(month => month);
    }

    private static IEnumerable<DateTime> EnumerateDays(DateTime startTime, DateTime endTime)
    {
        var current = startTime.Date;
        var end = endTime.Date;
        while (current <= end)
        {
            yield return current;
            current = current.AddDays(1);
        }
    }

    private static IReadOnlyList<BoilerSnapshot> ReadNormalizedAscending(
        IEnumerable<BoilerSnapshot> snapshots,
        int count)
    {
        var results = new List<BoilerSnapshot>(count);
        BoilerSnapshot? latestInSecond = null;
        var currentSecond = default(DateTime);

        foreach (var snapshot in snapshots)
        {
            var second = BoilerTime.TruncateToSecond(snapshot.RecordedAt);
            if (latestInSecond is null)
            {
                latestInSecond = snapshot;
                currentSecond = second;
                continue;
            }

            if (second == currentSecond)
            {
                latestInSecond = snapshot;
                continue;
            }

            results.Add(CopyWithSecond(latestInSecond, currentSecond));
            if (results.Count == count)
            {
                return results;
            }

            latestInSecond = snapshot;
            currentSecond = second;
        }

        if (latestInSecond is not null && results.Count < count)
        {
            results.Add(CopyWithSecond(latestInSecond, currentSecond));
        }

        return results;
    }

    private static IReadOnlyList<BoilerSnapshot> ReadNormalizedDescending(
        IEnumerable<BoilerSnapshot> snapshots,
        int count)
    {
        var results = new List<BoilerSnapshot>(count);
        DateTime? previousSecond = null;

        foreach (var snapshot in snapshots)
        {
            var second = BoilerTime.TruncateToSecond(snapshot.RecordedAt);
            if (previousSecond == second)
            {
                continue;
            }

            results.Add(CopyWithSecond(snapshot, second));
            previousSecond = second;
            if (results.Count == count)
            {
                break;
            }
        }

        results.Reverse();
        return results;
    }

    private static IReadOnlyList<BoilerSnapshot> NormalizeToSeconds(IEnumerable<BoilerSnapshot> snapshots)
    {
        return snapshots
            .GroupBy(snapshot => BoilerTime.TruncateToSecond(snapshot.RecordedAt))
            .OrderBy(group => group.Key)
            .Select(group => CopyWithSecond(
                group.OrderByDescending(snapshot => snapshot.RecordedAt).First(),
                group.Key))
            .ToList();
    }

    private static BoilerSnapshot CopyWithSecond(BoilerSnapshot snapshot, DateTime recordedAt)
    {
        var copy = snapshot.Copy();
        copy.Id = ToSecondKey(recordedAt);
        copy.RecordedAt = recordedAt;
        return copy;
    }

    private static string ToSecondKey(DateTime time)
    {
        return BoilerTime.TruncateToSecond(time).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    private static BoilerSnapshot ConvertLegacySnapshot(LegacySnapshot legacy)
    {
        var recordedAt = BoilerTime.TruncateToSecond(legacy.RecordedAt);
        var snapshot = new BoilerSnapshot
        {
            Id = ToSecondKey(recordedAt),
            RecordedAt = recordedAt,
            ConnectionState = legacy.ConnectionState
        };

        foreach (var binding in BoilerPointBindings.All)
        {
            if (LegacyPointProperties.TryGetValue(binding.ColumnName, out var property))
            {
                binding.SetValue(snapshot, ToNullableDouble(property.GetValue(legacy) as string));
            }
        }

        return snapshot;
    }

    private static double? ToNullableDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            ? parsed
            : null;
    }

    private sealed class LegacySnapshot
    {
        [BsonId]
        public ObjectId Id { get; set; } = ObjectId.Empty;

        public DateTime RecordedAt { get; set; }

        public string ConnectionState { get; set; } = string.Empty;

        public string? GYDCS_K0QHL10AA101AI1 { get; set; }
        public string? GYDCS_K0QHL10AA101AI2 { get; set; }
        public string? GYDCS_K0QHL10AA101AI3 { get; set; }
        public string? GYDCS_K0QHL10AA101AI4 { get; set; }
        public string? GYDCS_K0QHL10AA101AI5 { get; set; }
        public string? GYDCS_K0QHL10AA101AI6 { get; set; }
        public string? GYDCS_K0HNE10CQ101 { get; set; }
        public string? GYDCS_K0HNE10CQ102 { get; set; }
        public string? GYDCS_K0HNE10CQ103 { get; set; }
        public string? GYDCS_K0HNE10CQ104 { get; set; }
        public string? GYDCS_K0HNE10CQ105 { get; set; }
        public string? GYDCS_K0HNE10CF101_CAL { get; set; }
        public string? GYDCS_K0HNE10CM101 { get; set; }
        public string? GYDCS_K0HNE10CP101 { get; set; }
        public string? GYDCS_K0HNE10CT101 { get; set; }
        public string? GYDCS_K0QHA10CP101 { get; set; }
        public string? GYDCS_K0QHA10CT302 { get; set; }
        public string? GYDCS_K0QHB10CF101_COMP1 { get; set; }
        public string? GYDCS_K0QHB10CP101 { get; set; }
        public string? GYDCS_K0QHB10CT302 { get; set; }
        public string? GYDCS_K0QHH10AV001P { get; set; }
        public string? GYDCS_K0QHH10CF101 { get; set; }
        public string? GYDCS_K0QHL10AA101DO2 { get; set; }
        public string? GYDCS_K0QHL10AN001I { get; set; }
        public string? GYDCS_K0QHL10AN001P { get; set; }
    }
}
