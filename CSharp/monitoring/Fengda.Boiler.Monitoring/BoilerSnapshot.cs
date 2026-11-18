using System.Text.Json.Serialization;
using LiteDB;

namespace Fengda.Boiler.Monitoring;

/// <summary>
/// 锅炉运行快照，包含连接状态和全部监控点位。
/// </summary>
public sealed class BoilerSnapshot
{
    private const double RunningValvePositionThresholdPercent = 2.0;

    /// <summary>
    /// LiteDB 内部记录标识，不通过接口输出。
    /// </summary>
    [JsonIgnore]
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 快照记录时间，精确到秒。
    /// </summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>
    /// OPC UA 连接状态。
    /// </summary>
    public string ConnectionState { get; set; } = string.Empty;

    /// <summary>燃烧器燃气检漏压力。</summary>
    public double? GYDCS_K0QHL10AA101AI1 { get; set; }

    /// <summary>燃烧器气阀组入口压力。</summary>
    public double? GYDCS_K0QHL10AA101AI2 { get; set; }

    /// <summary>燃烧器风门调节阀阀位反馈。</summary>
    public double? GYDCS_K0QHL10AA101AI3 { get; set; }

    /// <summary>燃烧器外环气调节阀阀位反馈。</summary>
    public double? GYDCS_K0QHL10AA101AI4 { get; set; }

    /// <summary>燃烧器中心气调节阀阀位反馈。</summary>
    public double? GYDCS_K0QHL10AA101AI5 { get; set; }

    /// <summary>
    /// 是否运行。燃烧器外环气阀位和中心气阀位均大于 2% 时判定为运行，避免零点漂移和单阀短暂动作造成误判。
    /// </summary>
    [BsonIgnore]
    public bool IsRunning => IsValveOpen(GYDCS_K0QHL10AA101AI4) && IsValveOpen(GYDCS_K0QHL10AA101AI5);

    /// <summary>FGR 调节阀阀位反馈。</summary>
    public double? GYDCS_K0QHL10AA101AI6 { get; set; }

    /// <summary>燃烧器火焰信号，值为 1 表示已点火。</summary>
    public double? GYDCS_K0QHL10AA101DI1 { get; set; }

    /// <summary>2 号启动锅炉燃烧器火焰信号2，值为 1 表示已点火。</summary>
    public double? GYDCS_K0QHL10AA102FD { get; set; }

    /// <summary>
    /// 是否已点火。两路冗余火焰信号任意一路为 1 时为 <see langword="true"/>；
    /// 两路均无有效值时为 <see langword="null"/>。
    /// </summary>
    [BsonIgnore]
    public bool? IsIgnited
    {
        get
        {
            var primary = GetFlameState(GYDCS_K0QHL10AA101DI1);
            var redundant = GetFlameState(GYDCS_K0QHL10AA102FD);
            if (primary is true || redundant is true)
            {
                return true;
            }

            return primary is false || redundant is false ? false : null;
        }
    }

    /// <summary>启动锅炉烟囱 NOx 排放浓度。</summary>
    public double? GYDCS_K0HNE10CQ101 { get; set; }

    /// <summary>启动锅炉烟囱 SO2 排放浓度。</summary>
    public double? GYDCS_K0HNE10CQ102 { get; set; }

    /// <summary>启动锅炉烟囱 O2 排放浓度。</summary>
    public double? GYDCS_K0HNE10CQ103 { get; set; }

    /// <summary>启动锅炉烟囱粉尘排放浓度。</summary>
    public double? GYDCS_K0HNE10CQ104 { get; set; }

    /// <summary>启动锅炉烟囱 NO2 排放浓度。</summary>
    public double? GYDCS_K0HNE10CQ105 { get; set; }

    /// <summary>启动锅炉烟囱烟气流量计算值。</summary>
    public double? GYDCS_K0HNE10CF101_CAL { get; set; }

    /// <summary>启动锅炉烟囱湿度。</summary>
    public double? GYDCS_K0HNE10CM101 { get; set; }

    /// <summary>启动锅炉烟囱压力。</summary>
    public double? GYDCS_K0HNE10CP101 { get; set; }

    /// <summary>启动锅炉烟囱烟气温度。</summary>
    public double? GYDCS_K0HNE10CT101 { get; set; }

    /// <summary>炉膛出口烟气压力。</summary>
    public double? GYDCS_K0QHA10CP101 { get; set; }

    /// <summary>炉膛出口烟气温度。</summary>
    public double? GYDCS_K0QHA10CT302 { get; set; }

    /// <summary>实际负荷。</summary>
    public double? GYDCS_K0QHB10CF101_COMP1 { get; set; }

    /// <summary>启动锅炉蒸汽流量补偿值。</summary>
    public double? GYDCS_K0QHB10CF101_COMP { get; set; }

    /// <summary>启动锅炉蒸汽流量累计。</summary>
    public double? GYDCS_K0QHB10CF101_SUM1 { get; set; }

    /// <summary>启动锅炉过热蒸汽压力。</summary>
    public double? GYDCS_K0QHB10CP101 { get; set; }

    /// <summary>减温器出口蒸汽温度。</summary>
    public double? GYDCS_K0QHB10CT302 { get; set; }

    /// <summary>FGR 风机变频器频率反馈。</summary>
    public double? GYDCS_K0QHH10AV001P { get; set; }

    /// <summary>启动锅炉天然气入口管道流量。</summary>
    public double? GYDCS_K0QHH10CF101 { get; set; }

    /// <summary>启动锅炉天然气入口管道流量累计。</summary>
    public double? GYDCS_K0QHH10CF101_SUM { get; set; }

    /// <summary>燃烧器点火电磁阀工作状态。</summary>
    public double? GYDCS_K0QHL10AA101DO2 { get; set; }

    /// <summary>鼓风机变频器电流。</summary>
    public double? GYDCS_K0QHL10AN001I { get; set; }

    /// <summary>鼓风机变频器频率反馈。</summary>
    public double? GYDCS_K0QHL10AN001P { get; set; }

    /// <summary>
    /// 创建当前快照的独立副本。
    /// </summary>
    public BoilerSnapshot Copy() => (BoilerSnapshot)MemberwiseClone();

    private static bool IsValveOpen(double? value)
    {
        return value is { } number
            && double.IsFinite(number)
            && number > RunningValvePositionThresholdPercent;
    }

    private static bool? GetFlameState(double? value)
    {
        if (value is not { } number || !double.IsFinite(number))
        {
            return null;
        }

        return number == 1;
    }
}

/// <summary>
/// OPC UA 点位与锅炉快照字段、预测输入点名之间的绑定关系。
/// </summary>
public sealed record BoilerPointBinding(
    string NodeId,
    string ColumnName,
    string? PredictionPointName,
    Action<BoilerSnapshot, double?> SetValue,
    Func<BoilerSnapshot, double?> GetValue);

/// <summary>
/// 锅炉监控点位绑定清单。
/// </summary>
public static class BoilerPointBindings
{
    /// <summary>
    /// 全部 OPC UA 监控点位。
    /// </summary>
    public static IReadOnlyList<BoilerPointBinding> All { get; } =
    [
        Bind("K0QHL10AA101AI1", null, static (x, v) => x.GYDCS_K0QHL10AA101AI1 = v, static x => x.GYDCS_K0QHL10AA101AI1),
        Bind("K0QHL10AA101AI2", null, static (x, v) => x.GYDCS_K0QHL10AA101AI2 = v, static x => x.GYDCS_K0QHL10AA101AI2),
        Bind("K0QHL10AA101AI3", "K0QHL10AA101AI3.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHL10AA101AI3 = v, static x => x.GYDCS_K0QHL10AA101AI3),
        Bind("K0QHL10AA101AI4", "K0QHL10AA101AI4.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHL10AA101AI4 = v, static x => x.GYDCS_K0QHL10AA101AI4),
        Bind("K0QHL10AA101AI5", "K0QHL10AA101AI5.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHL10AA101AI5 = v, static x => x.GYDCS_K0QHL10AA101AI5),
        Bind("K0QHL10AA101AI6", "K0QHL10AA101AI6.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHL10AA101AI6 = v, static x => x.GYDCS_K0QHL10AA101AI6),
        Bind("K0QHL10AA101DI1", null, static (x, v) => x.GYDCS_K0QHL10AA101DI1 = v, static x => x.GYDCS_K0QHL10AA101DI1),
        Bind("K0QHL10AA102FD", null, static (x, v) => x.GYDCS_K0QHL10AA102FD = v, static x => x.GYDCS_K0QHL10AA102FD),
        Bind("K0HNE10CQ101", "K0HNE10CQ101.UNIT3@NET3", static (x, v) => x.GYDCS_K0HNE10CQ101 = v, static x => x.GYDCS_K0HNE10CQ101),
        Bind("K0HNE10CQ102", null, static (x, v) => x.GYDCS_K0HNE10CQ102 = v, static x => x.GYDCS_K0HNE10CQ102),
        Bind("K0HNE10CQ103", "K0HNE10CQ103.UNIT3@NET3", static (x, v) => x.GYDCS_K0HNE10CQ103 = v, static x => x.GYDCS_K0HNE10CQ103),
        Bind("K0HNE10CQ104", null, static (x, v) => x.GYDCS_K0HNE10CQ104 = v, static x => x.GYDCS_K0HNE10CQ104),
        Bind("K0HNE10CQ105", "K0HNE10CQ105.UNIT3@NET3", static (x, v) => x.GYDCS_K0HNE10CQ105 = v, static x => x.GYDCS_K0HNE10CQ105),
        Bind("K0HNE10CF101_CAL", null, static (x, v) => x.GYDCS_K0HNE10CF101_CAL = v, static x => x.GYDCS_K0HNE10CF101_CAL),
        Bind("K0HNE10CM101", null, static (x, v) => x.GYDCS_K0HNE10CM101 = v, static x => x.GYDCS_K0HNE10CM101),
        Bind("K0HNE10CP101", null, static (x, v) => x.GYDCS_K0HNE10CP101 = v, static x => x.GYDCS_K0HNE10CP101),
        Bind("K0HNE10CT101", null, static (x, v) => x.GYDCS_K0HNE10CT101 = v, static x => x.GYDCS_K0HNE10CT101),
        Bind("K0QHA10CP101", null, static (x, v) => x.GYDCS_K0QHA10CP101 = v, static x => x.GYDCS_K0QHA10CP101),
        Bind("K0QHA10CT302", null, static (x, v) => x.GYDCS_K0QHA10CT302 = v, static x => x.GYDCS_K0QHA10CT302),
        Bind("K0QHB10CF101_COMP1", null, static (x, v) => x.GYDCS_K0QHB10CF101_COMP1 = v, static x => x.GYDCS_K0QHB10CF101_COMP1),
        Bind("K0QHB10CF101_COMP", null, static (x, v) => x.GYDCS_K0QHB10CF101_COMP = v, static x => x.GYDCS_K0QHB10CF101_COMP),
        Bind("K0QHB10CF101-SUM1", "GYDCS_K0QHB10CF101_SUM1", null, static (x, v) => x.GYDCS_K0QHB10CF101_SUM1 = v, static x => x.GYDCS_K0QHB10CF101_SUM1),
        Bind("K0QHB10CP101", null, static (x, v) => x.GYDCS_K0QHB10CP101 = v, static x => x.GYDCS_K0QHB10CP101),
        Bind("K0QHB10CT302", null, static (x, v) => x.GYDCS_K0QHB10CT302 = v, static x => x.GYDCS_K0QHB10CT302),
        Bind("K0QHH10AV001P", "K0QHH10AV001P.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHH10AV001P = v, static x => x.GYDCS_K0QHH10AV001P),
        Bind("K0QHH10CF101", "K0QHH10CF101-1.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHH10CF101 = v, static x => x.GYDCS_K0QHH10CF101),
        Bind("K0QHH10CF101_SUM", null, static (x, v) => x.GYDCS_K0QHH10CF101_SUM = v, static x => x.GYDCS_K0QHH10CF101_SUM),
        Bind("K0QHL10AA101DO2", null, static (x, v) => x.GYDCS_K0QHL10AA101DO2 = v, static x => x.GYDCS_K0QHL10AA101DO2),
        Bind("K0QHL10AN001I", "K0QHL10AN001I.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHL10AN001I = v, static x => x.GYDCS_K0QHL10AN001I),
        Bind("K0QHL10AN001P", "K0QHL10AN001P.UNIT3@NET3", static (x, v) => x.GYDCS_K0QHL10AN001P = v, static x => x.GYDCS_K0QHL10AN001P)
    ];

    private static readonly IReadOnlyDictionary<string, BoilerPointBinding> PredictionBindings =
        All.Where(binding => binding.PredictionPointName is not null)
            .ToDictionary(binding => binding.PredictionPointName!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按预测输入点名查找对应的 OPC UA 点位绑定。
    /// </summary>
    public static bool TryGetPredictionBinding(string predictionPointName, out BoilerPointBinding binding)
    {
        return PredictionBindings.TryGetValue(predictionPointName, out binding!);
    }

    /// <summary>
    /// 尝试从快照读取指定预测点位的数值。
    /// </summary>
    public static bool TryGetPredictionValue(BoilerSnapshot snapshot, string predictionPointName, out double value)
    {
        value = 0;
        if (!TryGetPredictionBinding(predictionPointName, out var binding)
            || binding.GetValue(snapshot) is not { } numericValue
            || !double.IsFinite(numericValue))
        {
            return false;
        }

        value = numericValue;
        return true;
    }

    private static BoilerPointBinding Bind(
        string tagName,
        string? predictionPointName,
        Action<BoilerSnapshot, double?> setter,
        Func<BoilerSnapshot, double?> getter)
    {
        return Bind(tagName, $"GYDCS_{tagName}", predictionPointName, setter, getter);
    }

    private static BoilerPointBinding Bind(
        string tagName,
        string columnName,
        string? predictionPointName,
        Action<BoilerSnapshot, double?> setter,
        Func<BoilerSnapshot, double?> getter)
    {
        return new BoilerPointBinding(
            $"ns=1;s=GYDCS.{tagName}",
            columnName,
            predictionPointName,
            setter,
            getter);
    }
}
