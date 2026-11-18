using System.ComponentModel;
using Fengda.Cnn.Prediction;
using ScottPlot.WinForms;
using DrawingColor = System.Drawing.Color;
using PlotColor = ScottPlot.Color;
using PlotCoordinates = ScottPlot.Coordinates;
using PlotFontStyler = ScottPlot.Stylers.FontStyler;

namespace SampleDataViewer;

internal sealed class OverviewChartControl : UserControl
{
    private const string ChartFontName = "Microsoft YaHei UI";
    private const double FixedYAxisMin = -0.1;
    private const double FixedYAxisMax = 1.1;

    private readonly System.Windows.Forms.Label headerLabel = new();
    private readonly FormsPlot formsPlot = new();
    private readonly TableLayoutPanel layoutPanel = new();

    private TimeSeriesTable? table;
    private DateTime? sampleInputStart;
    private TimeSpan sampleInputDuration;
    private TimeSpan sampleTargetDuration;
    private double fullXMin;
    private double fullXMax = 1;
    private double viewXMin;
    private double viewXMax = 1;
    private DateTime? selectedTime;
    private int renderedLineCount;
    private int renderedPointCount;
    private HashSet<string>? visiblePointNames;

    public event EventHandler<DateTime>? TimeSelected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ChartTitle { get; set; } = "全局数据图（输入 + 输出）";

    public OverviewChartControl()
    {
        BackColor = DrawingColor.White;
        Font = new Font(ChartFontName, 9f);

        layoutPanel.Dock = DockStyle.Fill;
        layoutPanel.RowCount = 2;
        layoutPanel.ColumnCount = 1;
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        headerLabel.Dock = DockStyle.Fill;
        headerLabel.Padding = new Padding(12, 2, 8, 0);
        headerLabel.TextAlign = ContentAlignment.MiddleLeft;
        headerLabel.AutoEllipsis = true;
        headerLabel.Font = new Font(Font, System.Drawing.FontStyle.Bold);

        formsPlot.Dock = DockStyle.Fill;
        formsPlot.TabStop = true;
        formsPlot.UserInputProcessor.Disable();
        AttachPlotMouseHandlers(formsPlot);
        formsPlot.ControlAdded += (_, e) =>
        {
            if (e.Control is not null)
            {
                AttachPlotMouseHandlers(e.Control);
            }
        };
        ScottPlot.Fonts.Default = ChartFontName;

        layoutPanel.Controls.Add(headerLabel, 0, 0);
        layoutPanel.Controls.Add(formsPlot, 0, 1);
        Controls.Add(layoutPanel);
        RenderEmpty();
    }

    public void SetData(TimeSeriesTable sourceTable)
    {
        table = sourceTable;
        visiblePointNames = new HashSet<string>(sourceTable.Definitions.Keys, StringComparer.OrdinalIgnoreCase);
        fullXMin = sourceTable.FirstTimestamp.ToOADate();
        fullXMax = sourceTable.LastTimestamp.ToOADate();
        if (fullXMax <= fullXMin)
        {
            fullXMax = fullXMin + TimeSpan.FromMinutes(1).TotalDays;
        }

        viewXMin = fullXMin;
        viewXMax = fullXMax;
        selectedTime = null;
        RenderData();
    }

    public void SetSampleWindow(DateTime inputStart, TimeSpan inputDuration, TimeSpan targetDuration)
    {
        sampleInputStart = inputStart;
        sampleInputDuration = inputDuration;
        sampleTargetDuration = targetDuration;
        if (table is null)
        {
            InvalidatePlotArea();
            return;
        }

        RenderData();
    }

    public void SetVisibleSeries(IEnumerable<string> pointNames)
    {
        var nextPointNames = new HashSet<string>(pointNames, StringComparer.OrdinalIgnoreCase);
        if (visiblePointNames is not null && visiblePointNames.SetEquals(nextPointNames))
        {
            return;
        }

        visiblePointNames = nextPointNames;
        RenderData();
    }

    private void RenderEmpty()
    {
        headerLabel.Text = $"{ChartTitle} | 等待加载数据";
        formsPlot.Plot.Clear();
        ConfigurePlotFonts();
        formsPlot.Plot.Axes.SetLimits(0, 1, FixedYAxisMin, FixedYAxisMax);
        formsPlot.Plot.HideLegend();
        formsPlot.Refresh();
        InvalidatePlotArea();
    }

    private void AttachPlotMouseHandlers(Control control)
    {
        control.MouseClick += OnPlotMouseClick;
        control.MouseWheel += OnPlotMouseWheel;
        control.MouseEnter += (_, _) => formsPlot.Focus();
        foreach (Control child in control.Controls)
        {
            AttachPlotMouseHandlers(child);
        }
    }

    private void RenderData()
    {
        if (table is null)
        {
            RenderEmpty();
            return;
        }

        formsPlot.Plot.Clear();
        ConfigurePlotFonts();

        var definitions = table.InputSeries
            .Concat(table.TargetSeries)
            .Where(IsSeriesVisible)
            .ToList();
        var lineCount = 0;
        var pointCount = 0;
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (!table.Values.TryGetValue(definition.PointName, out var values))
            {
                continue;
            }

            var points = BuildSeriesPoints(table.Timestamps, values);
            if (points.Count == 0)
            {
                continue;
            }

            var range = GetNormalizationRange(definition, points);
            var xs = points.Select(point => point.Timestamp.ToOADate()).ToArray();
            var ys = points
                .Select(point => NormalizeValue(point.Value, range.Min, range.Max, range.IsConstant))
                .ToArray();

            var scatter = formsPlot.Plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromSDColor(SeriesColorPalette.GetColor(definition.PointName));
            scatter.LineWidth = 1;
            scatter.MarkerSize = 0;

            lineCount++;
            pointCount += points.Count;
        }

        renderedLineCount = lineCount;
        renderedPointCount = pointCount;
        AddSampleRegionBoxes();
        formsPlot.Plot.Axes.DateTimeTicksBottom();
        ApplyAxisLimits();
        formsPlot.Plot.HideLegend();
        UpdateHeader(lineCount, pointCount);
        formsPlot.Refresh();
        InvalidatePlotArea();
    }

    private bool IsSeriesVisible(SeriesDefinition definition)
    {
        return visiblePointNames is null || visiblePointNames.Contains(definition.PointName);
    }

    private void OnPlotMouseClick(object? sender, MouseEventArgs e)
    {
        if (table is null || e.Button != MouseButtons.Left)
        {
            return;
        }

        var point = GetFormsPlotPoint(sender, e);
        var coordinates = formsPlot.Plot.GetCoordinates(point.X, point.Y, formsPlot.Plot.Axes.Bottom, formsPlot.Plot.Axes.Left);
        selectedTime = DateTime.FromOADate(Math.Clamp(coordinates.X, fullXMin, fullXMax));
        UpdateHeader();
        TimeSelected?.Invoke(this, selectedTime.Value);
    }

    private void OnPlotMouseWheel(object? sender, MouseEventArgs e)
    {
        if (table is null)
        {
            return;
        }

        var point = GetFormsPlotPoint(sender, e);
        var coordinates = formsPlot.Plot.GetCoordinates(point.X, point.Y, formsPlot.Plot.Axes.Bottom, formsPlot.Plot.Axes.Left);
        var anchorX = Math.Clamp(coordinates.X, viewXMin, viewXMax);
        var currentSpan = Math.Max(viewXMax - viewXMin, TimeSpan.FromSeconds(1).TotalDays);
        var fullSpan = Math.Max(fullXMax - fullXMin, TimeSpan.FromSeconds(1).TotalDays);
        var zoomFactor = e.Delta > 0 ? 0.8 : 1.25;
        var newSpan = Math.Clamp(currentSpan * zoomFactor, TimeSpan.FromSeconds(5).TotalDays, fullSpan);
        var anchorFraction = Math.Clamp((anchorX - viewXMin) / currentSpan, 0.0, 1.0);

        viewXMin = anchorX - newSpan * anchorFraction;
        viewXMax = viewXMin + newSpan;
        if (viewXMin < fullXMin)
        {
            viewXMax += fullXMin - viewXMin;
            viewXMin = fullXMin;
        }

        if (viewXMax > fullXMax)
        {
            viewXMin -= viewXMax - fullXMax;
            viewXMax = fullXMax;
        }

        viewXMin = Math.Max(fullXMin, viewXMin);
        viewXMax = Math.Min(fullXMax, viewXMax);
        ApplyAxisLimits();
        formsPlot.Refresh();
        InvalidatePlotArea();
    }

    private void AddSampleRegionBoxes()
    {
        if (table is null || !sampleInputStart.HasValue)
        {
            return;
        }

        AddSampleRegionBox(
            sampleInputStart.Value,
            sampleInputStart.Value.Add(sampleInputDuration),
            DrawingColor.FromArgb(35, 104, 180),
            "输入样本区域");
        AddSampleRegionBox(
            sampleInputStart.Value.Add(sampleInputDuration),
            sampleInputStart.Value.Add(sampleInputDuration).Add(sampleTargetDuration),
            DrawingColor.FromArgb(226, 117, 48),
            "输出样本区域");
    }

    private void AddSampleRegionBox(DateTime start, DateTime end, DrawingColor color, string label)
    {
        var startX = Math.Max(fullXMin, start.ToOADate());
        var endX = Math.Min(fullXMax, end.ToOADate());
        if (endX <= startX)
        {
            return;
        }

        var boxBottom = FixedYAxisMin + 0.02;
        var boxTop = FixedYAxisMax - 0.02;
        var xs = new[] { startX, endX, endX, startX, startX };
        var ys = new[] { boxBottom, boxBottom, boxTop, boxTop, boxBottom };
        var outline = formsPlot.Plot.Add.Scatter(xs, ys);
        outline.Color = PlotColor.FromSDColor(color);
        outline.LineWidth = 2.5f;
        outline.MarkerSize = 0;

        var text = formsPlot.Plot.Add.Text(label, startX + (endX - startX) * 0.5, boxTop - 0.04);
        text.LabelFontColor = PlotColor.FromSDColor(color);
        text.LabelFontName = ChartFontName;
        text.LabelFontSize = 12;
    }

    private void DrawSampleRegions(Graphics g, Control surface)
    {
        if (table is null || !sampleInputStart.HasValue || formsPlot.Width <= 0 || formsPlot.Height <= 0)
        {
            return;
        }

        DrawSampleRegion(
            g,
            surface,
            sampleInputStart.Value,
            sampleInputStart.Value.Add(sampleInputDuration),
            DrawingColor.FromArgb(35, 104, 180),
            "输入样本区域");
        DrawSampleRegion(
            g,
            surface,
            sampleInputStart.Value.Add(sampleInputDuration),
            sampleInputStart.Value.Add(sampleInputDuration).Add(sampleTargetDuration),
            DrawingColor.FromArgb(226, 117, 48),
            "输出样本区域");
    }

    private void DrawSampleRegion(
        Graphics g,
        Control surface,
        DateTime start,
        DateTime end,
        DrawingColor color,
        string label)
    {
        var startX = Math.Max(viewXMin, start.ToOADate());
        var endX = Math.Min(viewXMax, end.ToOADate());
        if (endX <= startX)
        {
            return;
        }

        var topLeft = formsPlot.Plot.GetPixel(new PlotCoordinates(startX, FixedYAxisMax));
        var bottomRight = formsPlot.Plot.GetPixel(new PlotCoordinates(endX, FixedYAxisMin));
        var offset = surface.PointToClient(formsPlot.PointToScreen(Point.Empty));
        var left = Math.Min(topLeft.X, bottomRight.X) + offset.X;
        var right = Math.Max(topLeft.X, bottomRight.X) + offset.X;
        var top = Math.Min(topLeft.Y, bottomRight.Y) + offset.Y;
        var bottom = Math.Max(topLeft.Y, bottomRight.Y) + offset.Y;
        var rect = RectangleF.FromLTRB(left, top, right, bottom);
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        using var fillBrush = new SolidBrush(DrawingColor.FromArgb(36, color));
        using var borderPen = new Pen(DrawingColor.FromArgb(220, color), 2f);
        g.FillRectangle(fillBrush, rect);
        g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);

        using var labelFont = new Font(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold);
        using var labelBrush = new SolidBrush(color);
        using var labelBackBrush = new SolidBrush(DrawingColor.FromArgb(238, DrawingColor.White));
        var labelSize = g.MeasureString(label, labelFont);
        var labelRect = new RectangleF(rect.Left + 6, rect.Top + 5, labelSize.Width + 10, labelSize.Height + 4);
        g.FillRectangle(labelBackBrush, labelRect);
        g.DrawString(label, labelFont, labelBrush, labelRect.Left + 5, labelRect.Top + 2);
    }

    private Point GetFormsPlotPoint(object? sender, MouseEventArgs e)
    {
        if (sender is not Control source || ReferenceEquals(source, formsPlot))
        {
            return new Point(e.X, e.Y);
        }

        return formsPlot.PointToClient(source.PointToScreen(new Point(e.X, e.Y)));
    }

    private void InvalidatePlotArea()
    {
        formsPlot.Invalidate(true);
        foreach (Control child in formsPlot.Controls)
        {
            child.Invalidate(true);
        }
    }

    private void UpdateHeader(int? lineCount = null, int? pointCount = null)
    {
        if (table is null)
        {
            return;
        }

        var actualLineCount = lineCount ?? renderedLineCount;
        var actualPointCount = pointCount ?? renderedPointCount;
        var countText = $"{actualLineCount} 条曲线 / {actualPointCount} 个点";
        var selectedText = selectedTime.HasValue
            ? $" | 已选择 {selectedTime.Value:yyyy-MM-dd HH:mm:ss}"
            : string.Empty;
        headerLabel.Text =
            $"{ChartTitle} | {table.FirstTimestamp:yyyy-MM-dd HH:mm:ss} 至 {table.LastTimestamp:HH:mm:ss} | " +
            $"{countText} | 鼠标滚轮横向缩放，左键点击选择时间{selectedText}";
    }

    private void ApplyAxisLimits()
    {
        formsPlot.Plot.Axes.SetLimits(viewXMin, viewXMax, FixedYAxisMin, FixedYAxisMax);
    }

    private void ConfigurePlotFonts()
    {
        new PlotFontStyler(formsPlot.Plot).Set(ChartFontName);
        formsPlot.Plot.Legend.FontName = ChartFontName;
        formsPlot.Plot.Legend.SetBestFontOnEachRender = true;
    }

    private static List<TimeValue> BuildSeriesPoints(IReadOnlyList<DateTime> timestamps, IReadOnlyList<double?> values)
    {
        var count = Math.Min(timestamps.Count, values.Count);
        var points = new List<TimeValue>(count);
        for (var index = 0; index < count; index++)
        {
            var value = values[index];
            if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
            {
                points.Add(new TimeValue(timestamps[index], value.Value));
            }
        }

        return points;
    }

    private static (double Min, double Max, bool IsConstant) GetNormalizationRange(
        SeriesDefinition definition,
        IReadOnlyList<TimeValue> points)
    {
        if (TryGetScaleRange(definition, out var scaleMin, out var scaleMax))
        {
            return (scaleMin, scaleMax, false);
        }

        var min = points.Min(point => point.Value);
        var max = points.Max(point => point.Value);
        return (min, max, Math.Abs(max - min) < 1e-9);
    }

    private static double NormalizeValue(double value, double min, double max, bool isConstant)
    {
        if (isConstant)
        {
            return 0.5;
        }

        return Math.Clamp((value - min) / (max - min), 0.0, 1.0);
    }

    private static bool TryGetScaleRange(SeriesDefinition definition, out double min, out double max)
    {
        min = definition.LowScale ?? 0.0;
        max = definition.HighScale ?? 0.0;
        return definition.LowScale.HasValue
            && definition.HighScale.HasValue
            && double.IsFinite(min)
            && double.IsFinite(max)
            && Math.Abs(max - min) > 1e-9;
    }

}
