using System.Drawing;
using Fengda.Cnn.Prediction;

namespace SampleDataViewer;

internal static class SeriesColorPalette
{
    private static readonly Color[] CoolPalette =
    [
        Color.FromArgb(35, 104, 180),
        Color.FromArgb(29, 132, 173),
        Color.FromArgb(24, 145, 135),
        Color.FromArgb(41, 127, 116),
        Color.FromArgb(54, 118, 194),
        Color.FromArgb(68, 92, 183),
        Color.FromArgb(55, 150, 194),
        Color.FromArgb(31, 113, 141),
        Color.FromArgb(43, 160, 151),
        Color.FromArgb(76, 137, 207),
        Color.FromArgb(38, 121, 168),
        Color.FromArgb(70, 116, 174),
        Color.FromArgb(34, 147, 181),
        Color.FromArgb(22, 133, 154),
        Color.FromArgb(63, 145, 202),
        Color.FromArgb(45, 111, 191),
        Color.FromArgb(50, 153, 166),
        Color.FromArgb(82, 132, 196),
    ];

    private static readonly Color[] WarmPalette =
    [
        Color.FromArgb(213, 83, 55),
        Color.FromArgb(226, 117, 48),
        Color.FromArgb(196, 92, 61),
        Color.FromArgb(221, 145, 48),
        Color.FromArgb(183, 76, 82),
        Color.FromArgb(203, 105, 48),
        Color.FromArgb(194, 122, 42),
        Color.FromArgb(215, 76, 73),
    ];

    private static readonly string[] InputPointOrder =
    [
        "K0QHL10AA101AI5.UNIT3@NET3",
        "K0QHL10AA101AI4.UNIT3@NET3",
        "K0QHL10AA101AI3.UNIT3@NET3",
        "K0QHL10AN001P.UNIT3@NET3",
        "K0QHL10AN001I.UNIT3@NET3",
        "K0QHL10AA101AI6.UNIT3@NET3",
        "K0QHH10AV001P.UNIT3@NET3",
        "K0QHL10AA101DO2.UNIT3@NET3",
        "K0QHB10CP101.UNIT3@NET3",
        "K0QHB10CT302.UNIT3@NET3",
        "K0QHB10CF101-COMP.UNIT3@NET3",
        "K0QHA10CT302.UNIT3@NET3",
        "K0QHA10CP101.UNIT3@NET3",
        "K0HNE10CT101.UNIT3@NET3",
        "K0HNE10CP101.UNIT3@NET3",
        "K0HNE10CF101-CAL.UNIT3@NET3",
        "K0HNE10CM101.UNIT3@NET3",
        "K0QHH10CF101-1.UNIT3@NET3",
    ];

    private static readonly string[] TargetPointOrder =
    [
        "K0HNE10CQ101.UNIT3@NET3",
        "K0HNE10CQ102.UNIT3@NET3",
        "K0HNE10CQ103.UNIT3@NET3",
        "K0HNE10CQ104.UNIT3@NET3",
        "K0HNE10CQ105.UNIT3@NET3",
    ];

    private static readonly Dictionary<string, Color> InputColors = InputPointOrder
        .Select((pointName, index) => new { PointName = pointName, Color = CoolPalette[index % CoolPalette.Length] })
        .ToDictionary(item => item.PointName, item => item.Color, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Color> TargetColors = TargetPointOrder
        .Select((pointName, index) => new { PointName = pointName, Color = WarmPalette[index % WarmPalette.Length] })
        .ToDictionary(item => item.PointName, item => item.Color, StringComparer.OrdinalIgnoreCase);

    public static Color GetColor(string pointName)
    {
        pointName = SeriesRegistry.CanonicalPointName(pointName);
        if (InputColors.TryGetValue(pointName, out var inputColor))
        {
            return inputColor;
        }

        if (TargetColors.TryGetValue(pointName, out var targetColor))
        {
            return targetColor;
        }

        return IsLikelyTargetPoint(pointName)
            ? WarmPalette[GetStableIndex(pointName, WarmPalette.Length)]
            : CoolPalette[GetStableIndex(pointName, CoolPalette.Length)];
    }

    public static Color GetPredictionColor(string pointName)
    {
        pointName = SeriesRegistry.CanonicalPointName(pointName);
        var targetIndex = Array.FindIndex(
            TargetPointOrder,
            item => string.Equals(item, pointName, StringComparison.OrdinalIgnoreCase));
        return targetIndex >= 0
            ? CoolPalette[targetIndex % CoolPalette.Length]
            : CoolPalette[GetStableIndex(pointName, CoolPalette.Length)];
    }

    private static bool IsLikelyTargetPoint(string pointName)
    {
        return pointName.Contains("K0HNE10CQ", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetStableIndex(string text, int paletteLength)
    {
        unchecked
        {
            var hash = 23;
            foreach (var ch in text)
            {
                hash = hash * 31 + char.ToUpperInvariant(ch);
            }

            return (hash & int.MaxValue) % paletteLength;
        }
    }
}
