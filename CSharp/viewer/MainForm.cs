using Fengda.Cnn.Prediction;

namespace SampleDataViewer;

internal sealed class MainForm : Form
{
    private static readonly TimeSpan InputWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TargetWindow = TimeSpan.FromSeconds(60);

    private readonly ComboBox fileComboBox = new();
    private readonly Button loadButton = new();
    private readonly Button browseButton = new();
    private readonly Button previousButton = new();
    private readonly Button nextButton = new();
    private readonly CheckBox autoForwardCheckBox = new();
    private readonly CheckBox autoBackwardCheckBox = new();
    private readonly Label statusLabel = new();
    private readonly OverviewChartControl overviewChart = new();
    private readonly LineChartControl inputChart = new();
    private readonly LineChartControl targetChart = new();
    private readonly CheckedListBox inputCheckedList = new();
    private readonly CheckedListBox targetCheckedList = new();
    private readonly Button selectAllInputButton = new();
    private readonly Button clearInputButton = new();
    private readonly Button selectAllTargetButton = new();
    private readonly Button clearTargetButton = new();
    private readonly FlowLayoutPanel colorLegendFlowPanel = new();
    private readonly Label fgrAdviceTitleLabel = new();
    private readonly Label fgrAdviceActionLabel = new();
    private readonly RichTextBox fgrAdviceDetailTextBox = new();
    private readonly System.Windows.Forms.Timer autoMoveTimer = new();
    private readonly FgrAdvisor fgrAdvisor = new();

    private readonly Dictionary<string, string> filePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> lastLegendPointNames = [];
    private TimeSeriesTable? currentTable;
    private TimeSeriesTable? lastLegendTable;
    private OnnxForecastPredictor? forecastPredictor;
    private string forecastPredictorStatus = "预测模型尚未加载";
    private bool forecastPredictorLoadAttempted;
    private IReadOnlyList<string> modelInputPointNames = Array.Empty<string>();
    private IReadOnlyList<string> modelTargetPointNames = Array.Empty<string>();
    private DateTime currentInputStart;
    private int autoMoveDirection;

    public MainForm()
    {
        Text = "样本数据查看工具";
        MinimumSize = new Size(1180, 760);
        Size = new Size(1440, 920);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);

        ConfigureAutoMoveTimer();
        BuildLayout();
        overviewChart.TimeSelected += (_, selectedTime) => SelectOverviewTime(selectedTime);
        Load += (_, _) => InitializeFileList();
        FormClosed += (_, _) =>
        {
            autoMoveTimer.Dispose();
            forecastPredictor?.Dispose();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        Controls.Add(root);

        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildMainArea(), 0, 1);
        root.Controls.Add(BuildColorLegendBar(), 0, 2);
    }

    private Control BuildToolbar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        panel.Controls.Add(BuildFileSelectionRow(), 0, 0);
        panel.Controls.Add(BuildAutoMoveRow(), 0, 1);
        return panel;
    }

    private Control BuildFileSelectionRow()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));

        var fileLabel = new Label
        {
            Text = "数据文件",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        };

        fileComboBox.Dock = DockStyle.Fill;
        fileComboBox.DropDownStyle = ComboBoxStyle.DropDownList;

        loadButton.Text = "加载";
        loadButton.Dock = DockStyle.Fill;
        loadButton.Margin = new Padding(0, 4, 8, 4);
        loadButton.Click += (_, _) => LoadSelectedFile();

        browseButton.Text = "选择文件...";
        browseButton.Dock = DockStyle.Fill;
        browseButton.Margin = new Padding(0, 4, 0, 4);
        browseButton.Click += (_, _) => BrowseFile();

        fileLabel.Margin = new Padding(0, 4, 8, 4);
        fileComboBox.Margin = new Padding(0, 5, 8, 4);

        panel.Controls.Add(fileLabel, 0, 0);
        panel.Controls.Add(fileComboBox, 1, 0);
        panel.Controls.Add(loadButton, 2, 0);
        panel.Controls.Add(browseButton, 3, 0);
        return panel;
    }

    private Control BuildAutoMoveRow()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var controlLabel = new Label
        {
            Text = "窗口控制",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 8, 4),
        };

        statusLabel.Text = "请选择原始 CSV 文件";
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.AutoEllipsis = true;
        statusLabel.Margin = new Padding(8, 4, 0, 4);

        panel.Controls.Add(controlLabel, 0, 0);
        panel.Controls.Add(BuildAutoMovePanel(), 1, 0);
        panel.Controls.Add(statusLabel, 2, 0);
        return panel;
    }

    private void ConfigureAutoMoveTimer()
    {
        autoMoveTimer.Interval = 200;
        autoMoveTimer.Tick += (_, _) =>
        {
            if (autoMoveDirection == 0 || currentTable is null)
            {
                return;
            }

            MoveWindow(autoMoveDirection);
        };
    }

    private Control BuildAutoMovePanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            Margin = new Padding(0, 0, 0, 0),
        };

        autoBackwardCheckBox.Text = "自动后退";
        autoBackwardCheckBox.AutoSize = true;
        autoBackwardCheckBox.Margin = new Padding(0, 0, 12, 0);
        autoBackwardCheckBox.CheckedChanged += (_, _) => UpdateAutoMoveState(-1);

        autoForwardCheckBox.Text = "自动前进";
        autoForwardCheckBox.AutoSize = true;
        autoForwardCheckBox.Margin = new Padding(0, 0, 0, 0);
        autoForwardCheckBox.CheckedChanged += (_, _) => UpdateAutoMoveState(1);

        panel.Controls.Add(autoBackwardCheckBox);
        panel.Controls.Add(autoForwardCheckBox);
        return panel;
    }

    private void UpdateAutoMoveState(int requestedDirection)
    {
        if (requestedDirection > 0 && autoForwardCheckBox.Checked)
        {
            if (autoBackwardCheckBox.Checked)
            {
                autoBackwardCheckBox.Checked = false;
            }

            StartAutoMove(1);
            return;
        }

        if (requestedDirection < 0 && autoBackwardCheckBox.Checked)
        {
            if (autoForwardCheckBox.Checked)
            {
                autoForwardCheckBox.Checked = false;
            }

            StartAutoMove(-1);
            return;
        }

        if (!autoForwardCheckBox.Checked && !autoBackwardCheckBox.Checked)
        {
            StopAutoMove();
        }
    }

    private void StartAutoMove(int direction)
    {
        autoMoveDirection = direction;
        autoMoveTimer.Start();
    }

    private void StopAutoMove()
    {
        autoMoveTimer.Stop();
        autoMoveDirection = 0;
    }

    private Control BuildMainArea()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        previousButton.Text = "◀\r\n后退\r\n1秒";
        previousButton.Dock = DockStyle.Fill;
        previousButton.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        previousButton.Click += (_, _) => MoveWindow(-1);

        nextButton.Text = "前进\r\n1秒\r\n▶";
        nextButton.Dock = DockStyle.Fill;
        nextButton.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        nextButton.Click += (_, _) => MoveWindow(1);

        area.Controls.Add(previousButton, 0, 0);
        area.Controls.Add(BuildCenterArea(), 1, 0);
        area.Controls.Add(nextButton, 2, 0);
        return area;
    }

    private Control BuildCenterArea()
    {
        var center = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
        };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        overviewChart.Dock = DockStyle.Fill;
        overviewChart.Margin = new Padding(0, 0, 10, 8);

        inputChart.ChartTitle = "输入参数折线图（当前60秒）";
        inputChart.Dock = DockStyle.Fill;
        inputChart.Margin = new Padding(0, 0, 10, 8);
        inputChart.EnableCursorValueDisplay = true;

        targetChart.ChartTitle = "目标/预测数据折线图（未来60秒）";
        targetChart.Dock = DockStyle.Fill;
        targetChart.Margin = new Padding(0, 4, 10, 4);
        targetChart.EnableCursorValueDisplay = true;

        center.Controls.Add(overviewChart, 0, 0);
        center.SetColumnSpan(overviewChart, 2);
        center.Controls.Add(inputChart, 0, 1);
        center.Controls.Add(targetChart, 0, 2);
        center.Controls.Add(BuildSelectorPanel("输入参数显示", inputCheckedList, selectAllInputButton, clearInputButton), 1, 1);
        center.Controls.Add(BuildTargetSidePanel(), 1, 2);

        inputCheckedList.ItemCheck += (_, _) => BeginInvoke(RefreshCharts);
        targetCheckedList.ItemCheck += (_, _) => BeginInvoke(RefreshCharts);

        selectAllInputButton.Click += (_, _) => SetAllChecked(inputCheckedList, true);
        clearInputButton.Click += (_, _) => SetAllChecked(inputCheckedList, false);
        selectAllTargetButton.Click += (_, _) => SetAllChecked(targetCheckedList, true);
        clearTargetButton.Click += (_, _) => SetAllChecked(targetCheckedList, false);

        return center;
    }

    private Control BuildTargetSidePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var selectorPanel = BuildSelectorPanel("目标数据显示", targetCheckedList, selectAllTargetButton, clearTargetButton);
        selectorPanel.Margin = new Padding(0);
        var advisorPanel = BuildFgrAdvisorPanel();
        advisorPanel.Margin = new Padding(8, 0, 8, 8);

        panel.Controls.Add(selectorPanel, 0, 0);
        panel.Controls.Add(advisorPanel, 0, 1);
        return panel;
    }

    private Control BuildFgrAdvisorPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        fgrAdviceTitleLabel.Text = "FGR建议";
        fgrAdviceTitleLabel.Dock = DockStyle.Fill;
        fgrAdviceTitleLabel.Font = new Font(Font, FontStyle.Bold);
        fgrAdviceTitleLabel.TextAlign = ContentAlignment.MiddleLeft;

        fgrAdviceActionLabel.Text = "等待预测结果";
        fgrAdviceActionLabel.Dock = DockStyle.Fill;
        fgrAdviceActionLabel.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        fgrAdviceActionLabel.TextAlign = ContentAlignment.MiddleLeft;
        fgrAdviceActionLabel.AutoEllipsis = true;
        fgrAdviceActionLabel.ForeColor = Color.FromArgb(75, 85, 99);

        fgrAdviceDetailTextBox.Dock = DockStyle.Fill;
        fgrAdviceDetailTextBox.ReadOnly = true;
        fgrAdviceDetailTextBox.BorderStyle = BorderStyle.None;
        fgrAdviceDetailTextBox.BackColor = panel.BackColor;
        fgrAdviceDetailTextBox.ForeColor = Color.FromArgb(31, 41, 55);
        fgrAdviceDetailTextBox.Font = new Font("Microsoft YaHei UI", 8.2f);
        fgrAdviceDetailTextBox.WordWrap = true;
        fgrAdviceDetailTextBox.DetectUrls = false;
        fgrAdviceDetailTextBox.TabStop = false;
        fgrAdviceDetailTextBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        fgrAdviceDetailTextBox.Text = "加载 CSV 和预测模型后显示建议。";

        panel.Controls.Add(fgrAdviceTitleLabel, 0, 0);
        panel.Controls.Add(fgrAdviceActionLabel, 0, 1);
        panel.Controls.Add(fgrAdviceDetailTextBox, 0, 2);
        return panel;
    }

    private Control BuildColorLegendBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 6, 0, 0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var titleLabel = new Label
        {
            Text = "颜色图例",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 6, 0, 0),
        };

        colorLegendFlowPanel.Dock = DockStyle.Fill;
        colorLegendFlowPanel.FlowDirection = FlowDirection.LeftToRight;
        colorLegendFlowPanel.WrapContents = true;
        colorLegendFlowPanel.AutoScroll = true;
        colorLegendFlowPanel.Padding = new Padding(0, 2, 0, 0);

        panel.Controls.Add(titleLabel, 0, 0);
        panel.Controls.Add(colorLegendFlowPanel, 1, 0);
        return panel;
    }

    private Control BuildSelectorPanel(string title, CheckedListBox list, Button selectAllButton, Button clearButton)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 2,
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        selectAllButton.Text = "全选";
        selectAllButton.Dock = DockStyle.Fill;
        clearButton.Text = "全不选";
        clearButton.Dock = DockStyle.Fill;

        list.Dock = DockStyle.Fill;
        list.CheckOnClick = true;
        list.HorizontalScrollbar = true;
        list.IntegralHeight = false;

        panel.Controls.Add(titleLabel, 0, 0);
        panel.SetColumnSpan(titleLabel, 2);
        panel.Controls.Add(selectAllButton, 0, 1);
        panel.Controls.Add(clearButton, 1, 1);
        panel.Controls.Add(list, 0, 2);
        panel.SetColumnSpan(list, 2);
        return panel;
    }

    private void InitializeFileList()
    {
        var root = FindProjectRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.csv", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.CurrentCulture))
        {
            var fileName = Path.GetFileName(file);
            filePaths[fileName] = file;
            fileComboBox.Items.Add(fileName);
        }

        if (fileComboBox.Items.Count > 0)
        {
            fileComboBox.SelectedIndex = 0;
        }
    }

    private void LoadSelectedFile()
    {
        if (fileComboBox.SelectedItem is not string fileName || !filePaths.TryGetValue(fileName, out var path))
        {
            MessageBox.Show("请选择一个有效的数据文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        LoadFile(path);
    }

    private void BrowseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择原始 CSV 数据文件",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            InitialDirectory = FindProjectRoot(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var fileName = Path.GetFileName(dialog.FileName);
        filePaths[fileName] = dialog.FileName;
        if (!fileComboBox.Items.Contains(fileName))
        {
            fileComboBox.Items.Add(fileName);
        }

        fileComboBox.SelectedItem = fileName;
        LoadFile(dialog.FileName);
    }

    private void LoadFile(string path)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            currentTable = HistorianCsvLoader.Load(path);
            if (currentTable.Timestamps.Count == 0)
            {
                throw new InvalidOperationException("文件中没有可显示的时间序列数据。");
            }

            currentInputStart = ClampInputStart(currentTable.FirstTimestamp);
            EnsureForecastPredictorLoaded();
            modelInputPointNames = GetAvailableModelPointNames(forecastPredictor?.FeaturePointNames ?? Array.Empty<string>());
            modelTargetPointNames = GetAvailableModelPointNames(forecastPredictor?.TargetPointNames ?? Array.Empty<string>());

            PopulateSeriesList(inputCheckedList, GetDefinitionsInPointOrder(modelInputPointNames));
            PopulateSeriesList(targetCheckedList, GetDefinitionsInPointOrder(modelTargetPointNames));
            overviewChart.SetData(currentTable);
            RefreshCharts();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private static void PopulateSeriesList(CheckedListBox list, IReadOnlyList<SeriesDefinition> definitions)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var definition in definitions)
        {
            list.Items.Add(new SeriesListItem(definition), true);
        }

        list.EndUpdate();
    }

    private IReadOnlyList<string> GetAvailableModelPointNames(IEnumerable<string> pointNames)
    {
        if (currentTable is null)
        {
            return Array.Empty<string>();
        }

        return pointNames
            .Select(SeriesRegistry.CanonicalPointName)
            .Where(pointName => currentTable.Definitions.ContainsKey(pointName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<SeriesDefinition> GetDefinitionsInPointOrder(IEnumerable<string> pointNames)
    {
        if (currentTable is null)
        {
            return Array.Empty<SeriesDefinition>();
        }

        var definitions = new List<SeriesDefinition>();
        foreach (var pointName in pointNames)
        {
            if (currentTable.Definitions.TryGetValue(pointName, out var definition))
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    private void SelectOverviewTime(DateTime selectedTime)
    {
        if (currentTable is null)
        {
            return;
        }

        currentInputStart = ClampInputStart(selectedTime);
        RefreshCharts();
    }

    private void MoveWindow(int seconds)
    {
        if (currentTable is null)
        {
            return;
        }

        currentInputStart = ClampInputStart(currentInputStart.AddSeconds(seconds));
        RefreshCharts();
    }

    private DateTime ClampInputStart(DateTime value)
    {
        if (currentTable is null)
        {
            return value;
        }

        var minStart = currentTable.FirstTimestamp;
        var maxStart = GetMaxInputStart();
        if (value < minStart)
        {
            return minStart;
        }

        if (value > maxStart)
        {
            return maxStart;
        }

        return value;
    }

    private DateTime GetMaxInputStart()
    {
        if (currentTable is null)
        {
            return DateTime.MinValue;
        }

        var maxStart = currentTable.LastTimestamp
            .AddSeconds(1)
            .Subtract(InputWindow)
            .Subtract(TargetWindow);
        return maxStart < currentTable.FirstTimestamp ? currentTable.FirstTimestamp : maxStart;
    }

    private void RefreshCharts()
    {
        if (currentTable is null)
        {
            return;
        }

        var selectedInput = GetCheckedPointNames(inputCheckedList);
        var selectedTarget = GetCheckedPointNames(targetCheckedList);
        var targetStart = currentInputStart.Add(InputWindow);
        var inputEnd = currentInputStart.Add(InputWindow);
        var targetEnd = targetStart.Add(TargetWindow);
        var inputLines = currentTable.GetWindowLines(selectedInput, currentInputStart, InputWindow);
        var targetLines = currentTable.GetWindowLines(selectedTarget, targetStart, TargetWindow);
        var forecastResult = GetForecastResult();
        var predictionLines = BuildPredictionLines(forecastResult, selectedTarget);
        var combinedTargetLines = targetLines.Concat(predictionLines).ToList();
        var inputPointCount = inputLines.Sum(line => line.Points.Count);
        var targetPointCount = targetLines.Sum(line => line.Points.Count);

        UpdateFgrAdvice(forecastResult, inputEnd.AddSeconds(-1));
        overviewChart.SetSampleWindow(currentInputStart, InputWindow, TargetWindow);
        overviewChart.SetVisibleSeries(selectedInput.Concat(selectedTarget));
        UpdateColorLegend(selectedInput.Concat(selectedTarget));
        inputChart.SetData(inputLines, currentInputStart, inputEnd);
        targetChart.SetData(combinedTargetLines, targetStart, targetEnd);

        statusLabel.Text =
            $"{currentTable.SourceFile} | 输入 {currentInputStart:HH:mm:ss}-{inputEnd.AddSeconds(-1):HH:mm:ss} | " +
            $"目标 {targetStart:HH:mm:ss}-{targetEnd.AddSeconds(-1):HH:mm:ss} | " +
            $"点数 {inputPointCount}/{targetPointCount} | {forecastPredictorStatus}";
    }

    private ForecastResult? GetForecastResult()
    {
        if (currentTable is null)
        {
            return null;
        }

        EnsureForecastPredictorLoaded();

        if (forecastPredictor is null)
        {
            return null;
        }

        try
        {
            var inputWindow = ForecastInputWindowBuilder.Build(
                currentTable,
                currentInputStart,
                forecastPredictor.RequiredInputPointNames);
            var forecast = forecastPredictor.PredictForecast(inputWindow);
            forecastPredictorStatus = "预测模型运行正常";
            return forecast;
        }
        catch (Exception ex)
        {
            forecastPredictorStatus = $"预测失败：{ex.Message}";
            return null;
        }
    }

    private IReadOnlyList<ChartLine> BuildPredictionLines(
        ForecastResult? forecastResult,
        IReadOnlyList<string> selectedTarget)
    {
        if (currentTable is null || forecastResult is null || selectedTarget.Count == 0)
        {
            return Array.Empty<ChartLine>();
        }

        var selectedTargets = selectedTarget.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = new List<ChartLine>();
        foreach (var series in forecastResult.Series)
        {
            if (!selectedTargets.Contains(series.PointName)
                || !currentTable.Definitions.TryGetValue(series.PointName, out var definition))
            {
                continue;
            }

            lines.Add(new ChartLine(
                definition,
                series.Points,
                SeriesColorPalette.GetPredictionColor(series.PointName),
                2f));
        }

        return lines;
    }

    private void UpdateFgrAdvice(ForecastResult? forecastResult, DateTime currentTime)
    {
        if (currentTable is null)
        {
            SetFgrAdviceUnavailable("加载 CSV 后显示建议。");
            return;
        }

        var currentValve = ForecastInputWindowBuilder.GetCurrentValue(
            currentTable,
            FgrAdvisor.FgrValvePointName,
            currentTime);
        var currentFan = ForecastInputWindowBuilder.GetCurrentValue(
            currentTable,
            FgrAdvisor.FgrFanPointName,
            currentTime);
        var recommendation = fgrAdvisor.Recommend(forecastResult, currentValve, currentFan);
        fgrAdviceActionLabel.Text = recommendation.Title;
        fgrAdviceActionLabel.ForeColor = GetFgrAdviceColor(recommendation.Level);
        fgrAdviceDetailTextBox.Text = recommendation.Detail;
    }

    private void SetFgrAdviceUnavailable(string detail)
    {
        fgrAdviceActionLabel.Text = "FGR建议不可用";
        fgrAdviceActionLabel.ForeColor = GetFgrAdviceColor(FgrRecommendationLevel.Unavailable);
        fgrAdviceDetailTextBox.Text = detail;
    }

    private static Color GetFgrAdviceColor(FgrRecommendationLevel level)
    {
        return level switch
        {
            FgrRecommendationLevel.Normal => Color.FromArgb(20, 120, 80),
            FgrRecommendationLevel.Watch => Color.FromArgb(161, 98, 7),
            FgrRecommendationLevel.Warning => Color.FromArgb(194, 65, 12),
            FgrRecommendationLevel.Danger => Color.FromArgb(185, 28, 28),
            _ => Color.FromArgb(75, 85, 99),
        };
    }

    private void EnsureForecastPredictorLoaded()
    {
        if (forecastPredictorLoadAttempted)
        {
            return;
        }

        forecastPredictorLoadAttempted = true;
        forecastPredictor = OnnxForecastPredictor.TryCreateDefault(FindProjectRoot(), out forecastPredictorStatus);
    }

    private void UpdateColorLegend(IEnumerable<string> selectedPointNames)
    {
        var selected = selectedPointNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ReferenceEquals(currentTable, lastLegendTable)
            && selected.SequenceEqual(lastLegendPointNames, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        colorLegendFlowPanel.SuspendLayout();
        foreach (var control in colorLegendFlowPanel.Controls.Cast<Control>().ToList())
        {
            control.Dispose();
        }

        colorLegendFlowPanel.Controls.Clear();

        if (currentTable is null)
        {
            colorLegendFlowPanel.ResumeLayout();
            return;
        }

        var addedCount = 0;
        foreach (var pointName in selected)
        {
            if (!currentTable.Definitions.TryGetValue(pointName, out var definition))
            {
                continue;
            }

            colorLegendFlowPanel.Controls.Add(BuildColorLegendItem(definition));
            addedCount++;
        }

        if (addedCount == 0)
        {
            colorLegendFlowPanel.Controls.Add(new Label
            {
                Text = "未选择任何参数",
                AutoSize = true,
                ForeColor = Color.FromArgb(104, 113, 124),
                Margin = new Padding(0, 5, 0, 0),
            });
        }

        lastLegendTable = currentTable;
        lastLegendPointNames.Clear();
        lastLegendPointNames.AddRange(selected);
        colorLegendFlowPanel.ResumeLayout();
    }

    private Control BuildColorLegendItem(SeriesDefinition definition)
    {
        var item = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 14, 4),
        };

        var colorLine = new Panel
        {
            BackColor = SeriesColorPalette.GetColor(definition.PointName),
            Width = 28,
            Height = 4,
            Margin = new Padding(0, 10, 5, 0),
        };
        var label = new Label
        {
            Text = string.IsNullOrWhiteSpace(definition.Unit)
                ? definition.Label
                : $"{definition.Label} ({definition.Unit})",
            Width = 178,
            Height = 24,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        item.Controls.Add(colorLine);
        item.Controls.Add(label);
        return item;
    }

    private static List<string> GetCheckedPointNames(CheckedListBox list)
    {
        return list.CheckedItems
            .OfType<SeriesListItem>()
            .Select(item => item.Definition.PointName)
            .ToList();
    }

    private void SetAllChecked(CheckedListBox list, bool isChecked)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            list.SetItemChecked(index, isChecked);
        }

        RefreshCharts();
    }

    private static string FindProjectRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private sealed class SeriesListItem(SeriesDefinition definition)
    {
        public SeriesDefinition Definition { get; } = definition;

        public override string ToString()
        {
            var unit = string.IsNullOrWhiteSpace(Definition.Unit) ? string.Empty : $" ({Definition.Unit})";
            return $"{Definition.Label}{unit}";
        }
    }
}
