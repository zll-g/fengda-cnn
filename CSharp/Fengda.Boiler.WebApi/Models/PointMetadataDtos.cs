using System.Text.Json;
using Fengda.Boiler.Monitoring;

namespace Fengda.Boiler.WebApi.Models;

/// <summary>
/// 前端图表使用的锅炉监控点位元数据。
/// </summary>
public sealed class PointMetadataDto
{
    /// <summary>前端和预测结果使用的现场点位名。</summary>
    public required string PointName { get; init; }

    /// <summary>锅炉快照 JSON 中对应的属性名。</summary>
    public required string SnapshotPropertyName { get; init; }

    /// <summary>LiteDB 快照实体中的字段名。</summary>
    public required string ColumnName { get; init; }

    /// <summary>点位中文显示名称。</summary>
    public required string Label { get; init; }

    /// <summary>点位单位。</summary>
    public required string Unit { get; init; }

    /// <summary>图表显示下限量程；为空时由前端按当前数据自动计算。</summary>
    public double? LowScale { get; init; }

    /// <summary>图表显示上限量程；为空时由前端按当前数据自动计算。</summary>
    public double? HighScale { get; init; }

    /// <summary>是否属于模型输入展示点位。</summary>
    public bool IsModelInput { get; init; }

    /// <summary>是否属于执行预测必须具备的输入点位。</summary>
    public bool IsRequiredInput { get; init; }

    /// <summary>是否属于历史/预测对比目标点位。</summary>
    public bool IsTarget { get; init; }

    /// <summary>是否属于模型输出目标点位。</summary>
    public bool IsModelTarget { get; init; }

    /// <summary>界面展示排序。</summary>
    public int DisplayOrder { get; init; }
}

/// <summary>
/// 点位元数据接口返回数据。
/// </summary>
public sealed class PointMetadataResponseDto
{
    /// <summary>当前预测模型加载状态。</summary>
    public required string ModelStatus { get; init; }

    /// <summary>全部锅炉监控点位元数据。</summary>
    public required IReadOnlyList<PointMetadataDto> Points { get; init; }
}

/// <summary>
/// 单日历史概览降采样返回数据。
/// </summary>
public sealed class HistoryDayOverviewDto
{
    /// <summary>查询自然日零点时间。</summary>
    public DateTime Day { get; init; }

    /// <summary>降采样间隔秒数。</summary>
    public int IntervalSeconds { get; init; }

    /// <summary>降采样后的快照数量。</summary>
    public int SampleCount { get; init; }

    /// <summary>降采样后的锅炉快照列表。</summary>
    public required IReadOnlyList<BoilerSnapshot> Samples { get; init; }
}

internal static class BoilerPointMetadataCatalog
{
    private static readonly HashSet<string> TargetPointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "K0HNE10CQ101.UNIT3@NET3",
        "K0HNE10CQ102.UNIT3@NET3",
        "K0HNE10CQ103.UNIT3@NET3",
        "K0HNE10CQ104.UNIT3@NET3",
        "K0HNE10CQ105.UNIT3@NET3",
    };

    private static readonly IReadOnlyDictionary<string, string> PointNamesByColumn =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GYDCS_K0QHB10CF101_COMP1"] = "K0QHB10CF101-COMP.UNIT3@NET3",
            ["GYDCS_K0HNE10CF101_CAL"] = "K0HNE10CF101-CAL.UNIT3@NET3",
            ["GYDCS_K0QHH10CF101"] = "K0QHH10CF101-1.UNIT3@NET3",
        };

    private static readonly IReadOnlyDictionary<string, (string Label, string Unit)> Labels =
        new Dictionary<string, (string Label, string Unit)>(StringComparer.OrdinalIgnoreCase)
        {
            ["GYDCS_K0QHL10AA101AI1"] = ("燃烧器燃气检漏压力", "KPa"),
            ["GYDCS_K0QHL10AA101AI2"] = ("燃烧器气阀组入口压力", "KPa"),
            ["K0QHL10AA101AI3.UNIT3@NET3"] = ("燃烧器风门开度", "%"),
            ["K0QHL10AA101AI4.UNIT3@NET3"] = ("燃烧器外环气阀位", "%"),
            ["K0QHL10AA101AI5.UNIT3@NET3"] = ("燃烧器中心气阀位", "%"),
            ["K0QHL10AA101AI6.UNIT3@NET3"] = ("FGR调节阀开度", "%"),
            ["GYDCS_K0QHL10AA101DI1"] = ("燃烧器火焰信号", "状态"),
            ["GYDCS_K0QHL10AA102FD"] = ("2号启动锅炉燃烧器火焰信号2", "状态"),
            ["K0HNE10CQ101.UNIT3@NET3"] = ("烟囱NOx排放浓度", "mg/m³"),
            ["K0HNE10CQ102.UNIT3@NET3"] = ("烟囱SO₂排放浓度", "mg/m³"),
            ["K0HNE10CQ103.UNIT3@NET3"] = ("烟囱O₂排放浓度", "%"),
            ["K0HNE10CQ104.UNIT3@NET3"] = ("烟囱粉尘排放浓度", "mg/m³"),
            ["K0HNE10CQ105.UNIT3@NET3"] = ("烟囱NO₂排放浓度", "mg/m³"),
            ["K0HNE10CF101-CAL.UNIT3@NET3"] = ("烟囱烟气流量", "m³/h"),
            ["GYDCS_K0HNE10CM101"] = ("烟囱湿度", "%"),
            ["GYDCS_K0HNE10CP101"] = ("烟囱压力", "Pa"),
            ["GYDCS_K0HNE10CT101"] = ("烟囱烟气温度", "℃"),
            ["GYDCS_K0QHA10CP101"] = ("炉膛出口烟气压力", "KPa"),
            ["GYDCS_K0QHA10CT302"] = ("炉膛出口烟气温度", "℃"),
            ["K0QHB10CF101-COMP.UNIT3@NET3"] = ("蒸汽流量", "t/h"),
            ["GYDCS_K0QHB10CF101_COMP"] = ("启动锅炉蒸汽流量补偿值", "t/h"),
            ["GYDCS_K0QHB10CF101_SUM1"] = ("启动锅炉蒸汽流量累计", "t"),
            ["K0QHB10CP101.UNIT3@NET3"] = ("过热蒸汽压力", "MPa"),
            ["K0QHB10CT302.UNIT3@NET3"] = ("减温器出口蒸汽温度", "℃"),
            ["K0QHH10AV001P.UNIT3@NET3"] = ("FGR风机频率", "Hz"),
            ["K0QHH10CF101-1.UNIT3@NET3"] = ("天然气入口流量", "m³/h"),
            ["GYDCS_K0QHH10CF101_SUM"] = ("启动锅炉天然气入口管道流量累计", "m³"),
            ["K0QHL10AA101DO2.UNIT3@NET3"] = ("点火电磁阀", ""),
            ["K0QHL10AN001I.UNIT3@NET3"] = ("鼓风机电流", "A"),
            ["K0QHL10AN001P.UNIT3@NET3"] = ("鼓风机频率", "Hz"),
        };

    private static readonly IReadOnlyDictionary<string, (double LowScale, double HighScale)> GraphScales =
        new Dictionary<string, (double LowScale, double HighScale)>(StringComparer.OrdinalIgnoreCase)
        {
            ["K0QHL10AA101AI5.UNIT3@NET3"] = (0, 100),
            ["K0QHL10AA101AI4.UNIT3@NET3"] = (0, 100),
            ["K0QHL10AA101AI3.UNIT3@NET3"] = (0, 100),
            ["K0QHL10AN001P.UNIT3@NET3"] = (0, 50),
            ["K0QHL10AN001I.UNIT3@NET3"] = (0, 100.7),
            ["K0QHL10AA101AI6.UNIT3@NET3"] = (0, 100),
            ["GYDCS_K0QHL10AA101DI1"] = (0, 1),
            ["GYDCS_K0QHL10AA102FD"] = (0, 1),
            ["K0QHH10AV001P.UNIT3@NET3"] = (0, 50),
            ["K0QHL10AA101DO2.UNIT3@NET3"] = (0, 1),
            ["K0QHB10CP101.UNIT3@NET3"] = (0, 2.5),
            ["K0QHB10CT302.UNIT3@NET3"] = (0, 500),
            ["K0QHB10CF101-COMP.UNIT3@NET3"] = (0, 100),
            ["K0QHA10CT302.UNIT3@NET3"] = (0, 1200),
            ["K0QHA10CP101.UNIT3@NET3"] = (0, 3),
            ["K0HNE10CQ101.UNIT3@NET3"] = (0, 100),
            ["K0HNE10CQ102.UNIT3@NET3"] = (0, 100),
            ["K0HNE10CQ103.UNIT3@NET3"] = (0, 25),
            ["K0HNE10CQ104.UNIT3@NET3"] = (0, 15),
            ["K0HNE10CQ105.UNIT3@NET3"] = (0, 100),
            ["K0HNE10CT101.UNIT3@NET3"] = (0, 300),
            ["K0HNE10CP101.UNIT3@NET3"] = (-5000, 5000),
            ["K0HNE10CF101-CAL.UNIT3@NET3"] = (0, 100),
            ["K0HNE10CM101.UNIT3@NET3"] = (0, 40),
            ["K0QHH10CF101-1.UNIT3@NET3"] = (0, 1500),
        };

    public static IReadOnlyList<PointMetadataDto> Build(
        IReadOnlyCollection<string> modelInputPointNames,
        IReadOnlyCollection<string> requiredInputPointNames,
        IReadOnlyCollection<string> modelTargetPointNames)
    {
        var modelInputs = modelInputPointNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredInputs = requiredInputPointNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelTargets = modelTargetPointNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return BoilerPointBindings.All
            .Select((binding, index) =>
            {
                var pointName = ResolvePointName(binding);
                var label = ResolveLabel(pointName, binding.ColumnName);
                var graphScale = ResolveGraphScale(pointName, binding.ColumnName);
                return new PointMetadataDto
                {
                    PointName = pointName,
                    SnapshotPropertyName = JsonNamingPolicy.CamelCase.ConvertName(binding.ColumnName),
                    ColumnName = binding.ColumnName,
                    Label = label.Label,
                    Unit = label.Unit,
                    LowScale = graphScale?.LowScale,
                    HighScale = graphScale?.HighScale,
                    IsModelInput = modelInputs.Contains(pointName),
                    IsRequiredInput = requiredInputs.Contains(pointName),
                    IsTarget = TargetPointNames.Contains(pointName),
                    IsModelTarget = modelTargets.Contains(pointName),
                    DisplayOrder = index,
                };
            })
            .ToList();
    }

    private static string ResolvePointName(BoilerPointBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.PredictionPointName))
        {
            return binding.PredictionPointName;
        }

        if (PointNamesByColumn.TryGetValue(binding.ColumnName, out var pointName))
        {
            return pointName;
        }

        return $"{binding.ColumnName["GYDCS_".Length..]}.UNIT3@NET3";
    }

    private static (string Label, string Unit) ResolveLabel(string pointName, string columnName)
    {
        if (Labels.TryGetValue(pointName, out var label))
        {
            return label;
        }

        if (Labels.TryGetValue(columnName, out label))
        {
            return label;
        }

        return (pointName, string.Empty);
    }

    private static (double LowScale, double HighScale)? ResolveGraphScale(string pointName, string columnName)
    {
        if (GraphScales.TryGetValue(pointName, out var scale))
        {
            return scale;
        }

        if (GraphScales.TryGetValue(columnName, out scale))
        {
            return scale;
        }

        return null;
    }
}
