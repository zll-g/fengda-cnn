namespace SampleDataViewer;

internal static class SeriesRegistry
{
    private static readonly HashSet<string> TargetPoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "K0HNE10CQ101.UNIT3@NET3",
        "K0HNE10CQ102.UNIT3@NET3",
        "K0HNE10CQ103.UNIT3@NET3",
        "K0HNE10CQ104.UNIT3@NET3",
        "K0HNE10CQ105.UNIT3@NET3",
    };

    private static readonly Dictionary<string, (string Label, string Unit)> KnownSeries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["K0QHL10AA101AI5.UNIT3@NET3"] = ("燃烧器中心气阀位", "%"),
            ["K0QHL10AA101AI4.UNIT3@NET3"] = ("燃烧器外环气阀位", "%"),
            ["K0QHL10AA101AI3.UNIT3@NET3"] = ("燃烧器风门开度", "%"),
            ["K0QHL10AN001P.UNIT3@NET3"] = ("鼓风机频率", "Hz"),
            ["K0QHL10AN001I.UNIT3@NET3"] = ("鼓风机电流", "A"),
            ["K0QHL10AA101AI6.UNIT3@NET3"] = ("FGR调节阀开度", "%"),
            ["K0QHH10AV001P.UNIT3@NET3"] = ("FGR风机频率", "Hz"),
            ["K0QHL10AA101DO2.UNIT3@NET3"] = ("点火电磁阀", ""),
            ["K0QHB10CP101.UNIT3@NET3"] = ("过热蒸汽压力", "MPa"),
            ["K0QHB10CT302.UNIT3@NET3"] = ("减温器出口蒸汽温度", "℃"),
            ["K0QHB10CF101-COMP.UNIT3@NET3"] = ("蒸汽流量", "t/h"),
            ["K0QHA10CT302.UNIT3@NET3"] = ("炉膛出口烟气温度", "℃"),
            ["K0QHA10CP101.UNIT3@NET3"] = ("炉膛出口烟气压力", "KPa"),
            ["K0HNE10CQ101.UNIT3@NET3"] = ("烟囱NOx排放浓度", "mg/m³"),
            ["K0HNE10CQ102.UNIT3@NET3"] = ("烟囱SO₂排放浓度", "mg/m³"),
            ["K0HNE10CQ103.UNIT3@NET3"] = ("烟囱O₂排放浓度", "%"),
            ["K0HNE10CQ104.UNIT3@NET3"] = ("烟囱粉尘排放浓度", "mg/m³"),
            ["K0HNE10CQ105.UNIT3@NET3"] = ("烟囱NO₂排放浓度", "mg/m³"),
            ["K0HNE10CT101.UNIT3@NET3"] = ("烟囱烟气温度", "℃"),
            ["K0HNE10CP101.UNIT3@NET3"] = ("烟囱压力", "Pa"),
            ["K0HNE10CF101-CAL.UNIT3@NET3"] = ("烟囱烟气流量", "m³/h"),
            ["K0HNE10CM101.UNIT3@NET3"] = ("烟囱湿度", "%"),
            ["K0QHH10CF101-1.UNIT3@NET3"] = ("天然气入口流量", "m³/h"),
        };

    public static string CanonicalPointName(string value)
    {
        var text = value.Trim().Trim('"');
        if (text.StartsWith("(A) ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        var lineBreakIndex = text.IndexOfAny(['\r', '\n']);
        if (lineBreakIndex >= 0)
        {
            text = text[..lineBreakIndex];
        }

        return text.Trim();
    }

    public static SeriesDefinition CreateDefinition(
        string pointName,
        string? description = null,
        string? unit = null,
        double? lowScale = null,
        double? highScale = null)
    {
        pointName = CanonicalPointName(pointName);
        if (KnownSeries.TryGetValue(pointName, out var known))
        {
            return new SeriesDefinition(
                pointName,
                known.Label,
                known.Unit,
                lowScale,
                highScale,
                TargetPoints.Contains(pointName));
        }

        var label = string.IsNullOrWhiteSpace(description) ? pointName : description.Trim();
        return new SeriesDefinition(
            pointName,
            label,
            unit?.Trim() ?? string.Empty,
            lowScale,
            highScale,
            TargetPoints.Contains(pointName));
    }
}
