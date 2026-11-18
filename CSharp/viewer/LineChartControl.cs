using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;
using Fengda.Cnn.Prediction;
using ScottPlot.WinForms;
using DrawingColor = System.Drawing.Color;
using PlotColor = ScottPlot.Color;
using PlotCoordinates = ScottPlot.Coordinates;
using PlotFontStyler = ScottPlot.Stylers.FontStyler;

namespace SampleDataViewer;

internal sealed class LineChartControl : UserControl
{
    private const string ChartFontName = "Microsoft YaHei UI";
    private const double FixedYAxisMin = -0.1;
    private const double FixedYAxisMax = 1.1;

    private readonly System.Windows.Forms.Label headerLabel = new();
    private readonly FormsPlot formsPlot = new();
    private readonly TableLayoutPanel layoutPanel = new();
    private readonly Panel cursorValuePanel = new();
    private readonly Label cursorValueTitleLabel = new();
    private readonly RichTextBox cursorValueTextBox = new();

    private Point? cursorPoint;
    private DateTime? cursorTime;
    private DateTime? lastCursorValueSecond;
    private double cursorY;
    private bool enableCursorValueDisplay;
    private string lastCursorText = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ChartTitle { get; set; } = "折线图";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "暂无数据";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool EnableCursorValueDisplay
    {
        get => enableCursorValueDisplay;
        set
        {
            enableCursorValueDisplay = value;
            UpdateCursorValuePanelVisibility();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<ChartLine> Lines { get; private set; } = Array.Empty<ChartLine>();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime WindowStart { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime WindowEndExclusive { get; private set; }

    public LineChartControl()
    {
        BackColor = DrawingColor.White;
        Font = new Font(ChartFontName, 9f);

        layoutPanel.Dock = DockStyle.Fill;
        layoutPanel.RowCount = 2;
        layoutPanel.ColumnCount = 2;
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));

        headerLabel.Dock = DockStyle.Fill;
        headerLabel.Padding = new Padding(12, 4, 8, 0);
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

        ConfigureCursorValuePanel();

        layoutPanel.Controls.Add(headerLabel, 0, 0);
        layoutPanel.SetColumnSpan(headerLabel, 2);
        layoutPanel.Controls.Add(formsPlot, 0, 1);
        layoutPanel.Controls.Add(cursorValuePanel, 1, 1);
        Controls.Add(layoutPanel);
        UpdateCursorValuePanelVisibility();
        RenderPlot();
    }

    public void SetData(IReadOnlyList<ChartLine> lines, DateTime windowStart, DateTime windowEndExclusive)
    {
        Lines = lines;
        WindowStart = windowStart;
        WindowEndExclusive = windowEndExclusive;
        lastCursorValueSecond = null;
        RenderPlot();
        UpdateCursorValues(force: true);
    }

    private void ConfigureCursorValuePanel()
    {
        cursorValuePanel.Dock = DockStyle.Fill;
        cursorValuePanel.BackColor = DrawingColor.FromArgb(248, 250, 252);
        cursorValuePanel.Padding = new Padding(8, 4, 4, 4);

        var panelLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        panelLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panelLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panelLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        cursorValueTitleLabel.Text = "鼠标位置数值";
        cursorValueTitleLabel.Dock = DockStyle.Fill;
        cursorValueTitleLabel.Font = new Font(Font, System.Drawing.FontStyle.Bold);
        cursorValueTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        cursorValueTitleLabel.AutoEllipsis = true;

        cursorValueTextBox.Dock = DockStyle.Fill;
        cursorValueTextBox.ReadOnly = true;
        cursorValueTextBox.BorderStyle = BorderStyle.None;
        cursorValueTextBox.BackColor = cursorValuePanel.BackColor;
        cursorValueTextBox.ForeColor = DrawingColor.FromArgb(31, 41, 55);
        cursorValueTextBox.Font = new Font(ChartFontName, 8.5f);
        cursorValueTextBox.WordWrap = true;
        cursorValueTextBox.DetectUrls = false;
        cursorValueTextBox.TabStop = false;
        cursorValueTextBox.ScrollBars = RichTextBoxScrollBars.Vertical;

        panelLayout.Controls.Add(cursorValueTitleLabel, 0, 0);
        panelLayout.Controls.Add(cursorValueTextBox, 0, 1);
        cursorValuePanel.Controls.Add(panelLayout);
        SetCursorPanelText("鼠标位置数值", "鼠标移到图表上查看实际值", force: true);
    }

    private void AttachPlotMouseHandlers(Control control)
    {
        control.MouseMove += OnPlotMouseMove;
        control.MouseLeave += OnPlotMouseLeave;
        control.MouseEnter += (_, _) => formsPlot.Focus();
        control.Paint += OnPlotSurfacePaint;
        foreach (Control child in control.Controls)
        {
            AttachPlotMouseHandlers(child);
        }
    }

    private void RenderPlot()
    {
        var hasValidWindow = WindowStart != default
            && WindowEndExclusive != default
            && WindowEndExclusive > WindowStart;
        var visibleLines = Lines.Where(line => line.Points.Count > 0).ToList();
        var pointCount = visibleLines.Sum(line => line.Points.Count);
        var timeText = hasValidWindow
            ? $"{WindowStart:yyyy-MM-dd HH:mm:ss} 至 {WindowEndExclusive.AddSeconds(-1):HH:mm:ss}"
            : "等待加载数据";

        headerLabel.Text = $"{ChartTitle} | {timeText} | {visibleLines.Count} 条曲线 / {pointCount} 个点";

        formsPlot.Plot.Clear();
        ConfigurePlotFonts();
        formsPlot.Plot.YLabel("归一化值");
        formsPlot.Plot.XLabel("时间");

        if (!hasValidWindow || visibleLines.Count == 0)
        {
            headerLabel.Text = $"{ChartTitle} | {EmptyText}";
            formsPlot.Plot.Axes.SetLimits(0, 1, FixedYAxisMin, FixedYAxisMax);
            formsPlot.Refresh();
            InvalidatePlotArea();
            return;
        }

        foreach (var line in visibleLines)
        {
            var range = GetNormalizationRange(line);
            var xs = line.Points
                .Select(point => point.Timestamp.ToOADate())
                .ToArray();
            var ys = line.Points
                .Select(point => NormalizeValue(point.Value, range.Min, range.Max, range.IsConstant))
                .ToArray();
            var scatter = formsPlot.Plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromSDColor(
                line.ColorOverride ?? SeriesColorPalette.GetColor(line.Definition.PointName));
            scatter.LineWidth = line.LineWidth;
            scatter.MarkerSize = 0;
        }

        var xMin = WindowStart.ToOADate();
        var xMax = WindowEndExclusive.AddSeconds(-1).ToOADate();
        formsPlot.Plot.Axes.DateTimeTicksBottom();
        formsPlot.Plot.Axes.SetLimits(xMin, xMax, FixedYAxisMin, FixedYAxisMax);
        formsPlot.Plot.HideLegend();
        formsPlot.Refresh();
        InvalidatePlotArea();
    }

    private void OnPlotMouseMove(object? sender, MouseEventArgs e)
    {
        if (!EnableCursorValueDisplay || Lines.Count == 0)
        {
            return;
        }

        var hasValidWindow = WindowStart != default
            && WindowEndExclusive != default
            && WindowEndExclusive > WindowStart;
        if (!hasValidWindow)
        {
            return;
        }

        var point = GetFormsPlotPoint(sender, e);
        var coordinates = formsPlot.Plot.GetCoordinates(
            point.X,
            point.Y,
            formsPlot.Plot.Axes.Bottom,
            formsPlot.Plot.Axes.Left);
        var xMin = WindowStart.ToOADate();
        var xMax = WindowEndExclusive.AddSeconds(-1).ToOADate();

        cursorPoint = point;
        cursorTime = DateTime.FromOADate(Math.Clamp(coordinates.X, xMin, xMax));
        cursorY = Math.Clamp(coordinates.Y, FixedYAxisMin, FixedYAxisMax);
        UpdateCursorValues(force: false);
        formsPlot.Invalidate();
    }

    private void OnPlotMouseLeave(object? sender, EventArgs e)
    {
        if (!EnableCursorValueDisplay)
        {
            return;
        }

        var clientCursor = formsPlot.PointToClient(Cursor.Position);
        if (formsPlot.ClientRectangle.Contains(clientCursor))
        {
            return;
        }

        cursorPoint = null;
        cursorTime = null;
        lastCursorValueSecond = null;
        SetCursorPanelText("鼠标位置数值", "鼠标移到图表上查看实际值", force: true);
        InvalidatePlotArea();
    }

    private void OnPlotSurfacePaint(object? sender, PaintEventArgs e)
    {
        if (!EnableCursorValueDisplay || cursorTime is null || cursorPoint is null)
        {
            return;
        }

        if (sender is Control surface)
        {
            DrawCursorCrosshair(e.Graphics, surface);
        }
    }

    private void DrawCursorCrosshair(Graphics graphics, Control surface)
    {
        if (cursorTime is null)
        {
            return;
        }

        var x = cursorTime.Value.ToOADate();
        var y = cursorY;
        var top = formsPlot.Plot.GetPixel(new PlotCoordinates(x, FixedYAxisMax));
        var bottom = formsPlot.Plot.GetPixel(new PlotCoordinates(x, FixedYAxisMin));
        var left = formsPlot.Plot.GetPixel(new PlotCoordinates(WindowStart.ToOADate(), y));
        var right = formsPlot.Plot.GetPixel(new PlotCoordinates(WindowEndExclusive.AddSeconds(-1).ToOADate(), y));
        var offset = surface.PointToClient(formsPlot.PointToScreen(Point.Empty));

        using var pen = new Pen(DrawingColor.FromArgb(170, 31, 41, 55), 1f)
        {
            DashStyle = DashStyle.Dash,
        };
        var verticalX = (float)(top.X + offset.X);
        var horizontalY = (float)(left.Y + offset.Y);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLine(pen, verticalX, (float)(top.Y + offset.Y), verticalX, (float)(bottom.Y + offset.Y));
        graphics.DrawLine(pen, (float)(left.X + offset.X), horizontalY, (float)(right.X + offset.X), horizontalY);
    }

    private void UpdateCursorValues(bool force)
    {
        if (!EnableCursorValueDisplay)
        {
            return;
        }

        if (cursorTime is null || Lines.Count == 0)
        {
            SetCursorPanelText("鼠标位置数值", "鼠标移到图表上查看实际值", force);
            return;
        }

        var displayTime = RoundToNearestSecond(cursorTime.Value);
        if (!force && lastCursorValueSecond == displayTime)
        {
            return;
        }

        lastCursorValueSecond = displayTime;
        var rows = Lines
            .Select(line => TryBuildCursorValueRow(line, displayTime))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToList();

        var title = $"{displayTime:HH:mm:ss} 数值";
        var text = rows.Count == 0
            ? "附近没有数据点"
            : BuildCursorValueText(rows);
        SetCursorPanelText(title, text, force: true);
    }

    private CursorValueRow? TryBuildCursorValueRow(ChartLine line, DateTime targetTime)
    {
        if (line.Points.Count == 0)
        {
            return null;
        }

        var nearestIndex = FindNearestPointIndex(line.Points, targetTime);
        if (nearestIndex < 0)
        {
            return null;
        }

        var point = line.Points[nearestIndex];
        if (Math.Abs((point.Timestamp - targetTime).TotalSeconds) > 1.5)
        {
            return null;
        }

        var role = line.ColorOverride.HasValue ? "预测" : "实测";
        return new CursorValueRow(
            line.Definition.Label,
            role,
            point.Value,
            line.Definition.Unit);
    }

    private string BuildCursorValueText(IReadOnlyList<CursorValueRow> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.AppendLine($"{row.Name}（{row.Role}）");
            builder.Append($"{FormatValue(row.Value)} {row.Unit}");
        }

        return builder.ToString();
    }

    private void SetCursorPanelText(string title, string text, bool force)
    {
        if (!force && lastCursorText == text && cursorValueTitleLabel.Text == title)
        {
            return;
        }

        cursorValueTitleLabel.Text = title;
        lastCursorText = text;
        if (cursorValueTextBox.Text != text)
        {
            cursorValueTextBox.Text = text;
        }
    }

    private void UpdateCursorValuePanelVisibility()
    {
        if (layoutPanel.ColumnStyles.Count < 2)
        {
            return;
        }

        layoutPanel.ColumnStyles[1].Width = EnableCursorValueDisplay ? 250 : 0;
        cursorValuePanel.Visible = EnableCursorValueDisplay;
        if (!EnableCursorValueDisplay)
        {
            cursorPoint = null;
            cursorTime = null;
            lastCursorValueSecond = null;
            return;
        }

        SetCursorPanelText("鼠标位置数值", "鼠标移到图表上查看实际值", force: true);
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

    private void ConfigurePlotFonts()
    {
        new PlotFontStyler(formsPlot.Plot).Set(ChartFontName);
        formsPlot.Plot.Legend.FontName = ChartFontName;
        formsPlot.Plot.Legend.SetBestFontOnEachRender = true;
    }

    private static int FindNearestPointIndex(IReadOnlyList<TimeValue> points, DateTime targetTime)
    {
        var low = 0;
        var high = points.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var comparison = points[mid].Timestamp.CompareTo(targetTime);
            if (comparison == 0)
            {
                return mid;
            }

            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        var candidates = new[] { Math.Clamp(low, 0, points.Count - 1), Math.Clamp(high, 0, points.Count - 1) };
        return candidates
            .Distinct()
            .OrderBy(index => Math.Abs((points[index].Timestamp - targetTime).TotalMilliseconds))
            .FirstOrDefault();
    }

    private static DateTime RoundToNearestSecond(DateTime value)
    {
        return new DateTime(((value.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond);
    }

    private static string FormatValue(double value)
    {
        if (Math.Abs(value) >= 100)
        {
            return value.ToString("0.##");
        }

        if (Math.Abs(value) >= 10)
        {
            return value.ToString("0.###");
        }

        return value.ToString("0.####");
    }

    private static (double Min, double Max, bool IsConstant) GetNormalizationRange(ChartLine line)
    {
        if (TryGetScaleRange(line.Definition, out var scaleMin, out var scaleMax))
        {
            return (scaleMin, scaleMax, false);
        }

        var min = line.Points.Min(point => point.Value);
        var max = line.Points.Max(point => point.Value);
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

    private sealed record CursorValueRow(
        string Name,
        string Role,
        double Value,
        string Unit);
}
