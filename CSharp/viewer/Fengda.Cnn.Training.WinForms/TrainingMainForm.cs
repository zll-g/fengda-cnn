using System.Diagnostics;
using System.Text.Json;
using Fengda.Cnn.Prediction;
using ScottPlot.WinForms;
using PlotFontStyler = ScottPlot.Stylers.FontStyler;

namespace Fengda.Cnn.Training.WinForms;

/// <summary>锅炉预测模型训练工作台主界面。</summary>
internal sealed class TrainingMainForm : Form
{
    private readonly TextBox dataDirectoryTextBox = new();
    private readonly TextBox pythonPathTextBox = new();
    private readonly DateTimePicker startDatePicker = new();
    private readonly DateTimePicker endDatePicker = new();
    private readonly NumericUpDown epochsInput = new();
    private readonly NumericUpDown batchSizeInput = new();
    private readonly ComboBox deviceComboBox = new();
    private readonly Button scanButton = new();
    private readonly Button startButton = new();
    private readonly Button stopButton = new();
    private readonly Button openResultButton = new();
    private readonly Label dataSummaryLabel = new();
    private readonly Label statusLabel = new();
    private readonly Label epochLabel = new();
    private readonly ProgressBar stageProgressBar = new();
    private readonly ProgressBar epochProgressBar = new();
    private readonly DataGridView dayGrid = new();
    private readonly FormsPlot lossPlot = new();
    private readonly FormsPlot metricPlot = new();
    private readonly RichTextBox logTextBox = new();
    private readonly List<double> epochNumbers = [];
    private readonly List<double> trainLosses = [];
    private readonly List<double> validationLosses = [];
    private readonly List<double> peakNoxMaes = [];
    private readonly List<double> minO2Maes = [];
    private readonly PythonTrainingProcess pythonTrainingProcess = new();

    private CancellationTokenSource? operationCancellation;
    private bool isExporting;
    private string? latestRunDirectory;

    public TrainingMainForm()
    {
        Text = "启动锅炉预测模型训练工作台";
        MinimumSize = new Size(1180, 760);
        Size = new Size(1480, 940);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        ScottPlot.Fonts.Default = "Microsoft YaHei UI";
        BuildLayout();
        ConfigureDefaults();
        FormClosed += (_, _) =>
        {
            operationCancellation?.Cancel();
            pythonTrainingProcess.Dispose();
        };
    }

    private void ConfigureDefaults()
    {
        dataDirectoryTextBox.Text = Path.Combine(FindProjectRoot(), "CSharp", "Fengda.Boiler.WebApi", "data");
        pythonPathTextBox.Text = "python";
        deviceComboBox.Items.AddRange(["auto", "cpu", "cuda"]);
        deviceComboBox.SelectedIndex = 0;
        epochsInput.Minimum = 1;
        epochsInput.Maximum = 10000;
        epochsInput.Value = 30;
        batchSizeInput.Minimum = 1;
        batchSizeInput.Maximum = 4096;
        batchSizeInput.Value = 32;
        startDatePicker.Format = DateTimePickerFormat.Custom;
        endDatePicker.Format = DateTimePickerFormat.Custom;
        startDatePicker.CustomFormat = "yyyy-MM-dd 00:00:00";
        endDatePicker.CustomFormat = "yyyy-MM-dd 23:59:59";
        startDatePicker.Value = DateTime.Today;
        endDatePicker.Value = DateTime.Today;
        SetRunState(false);
        RenderCharts();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        Controls.Add(root);
        root.Controls.Add(BuildConfigurationPanel(), 0, 0);
        root.Controls.Add(BuildMonitoringPanel(), 0, 1);
        root.Controls.Add(BuildLogPanel(), 0, 2);
    }

    private Control BuildConfigurationPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 6,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(248, 250, 252),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        AddLabel(panel, "训练数据目录", 0, 0);
        dataDirectoryTextBox.Dock = DockStyle.Fill;
        panel.Controls.Add(dataDirectoryTextBox, 1, 0);
        var browseDataButton = CreateButton("选择目录", (_, _) => BrowseDirectory(dataDirectoryTextBox));
        panel.Controls.Add(browseDataButton, 2, 0);
        scanButton.Text = "扫描数据";
        scanButton.Dock = DockStyle.Fill;
        scanButton.Click += async (_, _) => await ScanDataDirectoryAsync();
        panel.Controls.Add(scanButton, 3, 0);
        dataSummaryLabel.Dock = DockStyle.Fill;
        dataSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        dataSummaryLabel.AutoEllipsis = true;
        panel.Controls.Add(dataSummaryLabel, 4, 0);
        panel.SetColumnSpan(dataSummaryLabel, 2);

        AddLabel(panel, "Python 路径", 0, 1);
        pythonPathTextBox.Dock = DockStyle.Fill;
        panel.Controls.Add(pythonPathTextBox, 1, 1);
        panel.Controls.Add(CreateButton("选择程序", (_, _) => BrowsePython()), 2, 1);
        AddLabel(panel, "运算设备", 3, 1);
        deviceComboBox.Dock = DockStyle.Fill;
        deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        panel.Controls.Add(deviceComboBox, 4, 1);

        AddLabel(panel, "起始日期", 0, 2);
        startDatePicker.Dock = DockStyle.Fill;
        panel.Controls.Add(startDatePicker, 1, 2);
        AddLabel(panel, "结束日期", 2, 2);
        endDatePicker.Dock = DockStyle.Fill;
        panel.Controls.Add(endDatePicker, 3, 2);
        AddLabel(panel, "训练轮次", 4, 2);
        epochsInput.Dock = DockStyle.Fill;
        panel.Controls.Add(epochsInput, 5, 2);

        AddLabel(panel, "批次大小", 0, 3);
        batchSizeInput.Dock = DockStyle.Fill;
        panel.Controls.Add(batchSizeInput, 1, 3);
        startButton.Text = "生成快照并开始训练";
        startButton.Dock = DockStyle.Fill;
        startButton.Click += async (_, _) => await StartTrainingAsync();
        panel.Controls.Add(startButton, 3, 3);
        stopButton.Text = "停止训练";
        stopButton.Dock = DockStyle.Fill;
        stopButton.Click += (_, _) => RequestStop();
        panel.Controls.Add(stopButton, 4, 3);
        openResultButton.Text = "打开结果目录";
        openResultButton.Dock = DockStyle.Fill;
        openResultButton.Click += (_, _) => OpenResultDirectory();
        panel.Controls.Add(openResultButton, 5, 3);
        return panel;
    }

    private Control BuildMonitoringPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0, 10, 0, 0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(BuildProgressPanel(), 0, 0);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, 0)!, 2);
        panel.Controls.Add(BuildDayPanel(), 0, 1);
        panel.Controls.Add(BuildChartPanel(), 1, 1);
        return panel;
    }

    private Control BuildProgressPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        epochLabel.Dock = DockStyle.Fill;
        epochLabel.TextAlign = ContentAlignment.MiddleLeft;
        AddLabel(panel, "当前状态", 0, 0);
        panel.Controls.Add(statusLabel, 1, 0);
        AddLabel(panel, "总体进度", 0, 1);
        stageProgressBar.Dock = DockStyle.Fill;
        panel.Controls.Add(stageProgressBar, 1, 1);
        AddLabel(panel, "训练进度", 0, 2);
        var epochPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        epochPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        epochPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        epochProgressBar.Dock = DockStyle.Fill;
        epochPanel.Controls.Add(epochProgressBar, 0, 0);
        epochPanel.Controls.Add(epochLabel, 1, 0);
        panel.Controls.Add(epochPanel, 1, 2);
        return panel;
    }

    private Control BuildDayPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 0, 10, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = "可用历史数据", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        dayGrid.Dock = DockStyle.Fill;
        dayGrid.ReadOnly = true;
        dayGrid.AllowUserToAddRows = false;
        dayGrid.AllowUserToDeleteRows = false;
        dayGrid.AutoGenerateColumns = false;
        dayGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dayGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "日期", DataPropertyName = "Day", Width = 135, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd" } });
        dayGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "记录数", DataPropertyName = "RecordCountDisplay", Width = 90 });
        dayGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "读取状态", DataPropertyName = "ReadError", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(dayGrid, 0, 1);
        return panel;
    }

    private Control BuildChartPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        lossPlot.Dock = DockStyle.Fill;
        metricPlot.Dock = DockStyle.Fill;
        lossPlot.Margin = new Padding(0, 0, 0, 6);
        metricPlot.Margin = new Padding(0, 6, 0, 0);
        panel.Controls.Add(lossPlot, 0, 0);
        panel.Controls.Add(metricPlot, 0, 1);
        return panel;
    }

    private Control BuildLogPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 10, 0, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = "训练日志", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        logTextBox.Dock = DockStyle.Fill;
        logTextBox.ReadOnly = true;
        logTextBox.Font = new Font("Cascadia Mono", 8.5f);
        logTextBox.BackColor = Color.FromArgb(250, 250, 250);
        logTextBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(logTextBox, 0, 1);
        return panel;
    }

    private static void AddLabel(TableLayoutPanel panel, string text, int column, int row)
    {
        panel.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, column, row);
    }

    private static Button CreateButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill };
        button.Click += click;
        return button;
    }

    private async Task ScanDataDirectoryAsync()
    {
        if (!Directory.Exists(dataDirectoryTextBox.Text))
        {
            MessageBox.Show("请选择存在的数据目录。", "训练数据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetRunState(true, scanningOnly: true);
        operationCancellation = new CancellationTokenSource();
        try
        {
            statusLabel.Text = "正在扫描数据目录...";
            var exporter = new TrainingDataExporter(dataDirectoryTextBox.Text);
            var summary = await Task.Run(() => exporter.Scan(operationCancellation.Token));
            dayGrid.DataSource = summary.Days;
            if (summary.FirstDay is not null && summary.LastDay is not null)
            {
                startDatePicker.Value = summary.FirstDay.Value;
                endDatePicker.Value = summary.LastDay.Value;
            }

            dataSummaryLabel.Text = summary.Days.Count == 0
                ? "未找到 yyyyMM.db 历史文件"
                : $"{summary.Days.Count:N0} 天，{summary.Days.Count(day => day.ReadError is not null):N0} 天读取失败，{summary.Days.Sum(day => day.RecordCount ?? 0):N0} 条有效记录";
            statusLabel.Text = "数据扫描完成";
            AppendLog($"扫描完成: {dataSummaryLabel.Text}");
            foreach (var day in summary.Days.Where(day => day.ReadError is not null))
            {
                AppendLog($"{day.Day:yyyy-MM-dd} 读取失败: {day.ReadError}");
            }
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "数据扫描已停止";
        }
        catch (Exception error)
        {
            statusLabel.Text = "数据扫描失败";
            AppendLog($"扫描失败: {error.Message}");
            MessageBox.Show(error.Message, "扫描失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            SetRunState(false);
        }
    }

    private async Task StartTrainingAsync()
    {
        if (!Directory.Exists(dataDirectoryTextBox.Text))
        {
            MessageBox.Show("请选择存在的训练数据目录。", "训练数据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var scriptPath = Path.Combine(FindProjectRoot(), "scripts", "train_snapshot_model.py");
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show($"未找到训练脚本: {scriptPath}", "训练", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!await VerifyPythonEnvironmentAsync())
        {
            return;
        }

        var startTime = startDatePicker.Value.Date;
        var endTime = endDatePicker.Value.Date.AddDays(1).AddSeconds(-1);
        if (endTime < startTime)
        {
            MessageBox.Show("结束日期不能早于起始日期。", "训练", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        latestRunDirectory = CreateRunDirectory(dataDirectoryTextBox.Text);
        ResetTrainingView();
        AppendLog($"本次训练目录: {latestRunDirectory}");
        SetRunState(true);
        operationCancellation = new CancellationTokenSource();
        try
        {
            isExporting = true;
            statusLabel.Text = "正在生成训练数据快照...";
            stageProgressBar.Value = 2;
            var exporter = new TrainingDataExporter(dataDirectoryTextBox.Text);
            var export = await Task.Run(() => exporter.Export(
                latestRunDirectory,
                startTime,
                endTime,
                message => InvokeOnUi(() =>
                {
                    statusLabel.Text = message;
                    AppendLog(message);
                }),
                operationCancellation.Token));
            isExporting = false;
            stageProgressBar.Value = 5;
            var runManifest = new
            {
                createdAt = DateTime.Now,
                sourceDirectory = Path.GetFullPath(dataDirectoryTextBox.Text),
                startTime,
                endTime,
                epochs = (int)epochsInput.Value,
                batchSize = (int)batchSizeInput.Value,
                device = deviceComboBox.Text,
                dataExport = export,
            };
            File.WriteAllText(Path.Combine(latestRunDirectory, "manifest.json"), JsonSerializer.Serialize(runManifest, new JsonSerializerOptions { WriteIndented = true }));
            AppendLog($"训练快照完成: {export.SegmentCount} 个分区，{export.TotalSeconds:N0} 秒，完整记录 {export.CompleteSnapshotCount:N0} 条。");
            if (export.ReadIssues.Count > 0)
            {
                AppendLog($"已跳过 {export.ReadIssues.Count:N0} 个无法读取的日集合，它们不会参与训练。");
            }

            var exitCode = await pythonTrainingProcess.RunAsync(
                pythonPathTextBox.Text.Trim(),
                scriptPath,
                export.ManifestPath,
                latestRunDirectory,
                (int)epochsInput.Value,
                (int)batchSizeInput.Value,
                deviceComboBox.Text,
                HandleProgress,
                AppendLog,
                operationCancellation.Token);
            if (exitCode == 0)
            {
                ValidateExportedModel(latestRunDirectory);
                statusLabel.Text = "训练、导出与模型加载校验完成";
                stageProgressBar.Value = 100;
                AppendLog("训练成功完成。");
            }
            else if (exitCode == 2)
            {
                statusLabel.Text = "训练已停止";
                AppendLog("训练已按请求停止。");
            }
            else
            {
                statusLabel.Text = $"训练失败，进程退出码 {exitCode}";
                AppendLog($"训练失败，进程退出码 {exitCode}。");
            }
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = isExporting ? "数据快照导出已停止" : "训练已停止";
            AppendLog(statusLabel.Text);
        }
        catch (Exception error)
        {
            statusLabel.Text = "训练异常";
            AppendLog($"训练异常: {error}");
            MessageBox.Show(error.Message, "训练异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            isExporting = false;
            operationCancellation?.Dispose();
            operationCancellation = null;
            SetRunState(false);
        }
    }

    private void HandleProgress(TrainingProgressEvent progress)
    {
        InvokeOnUi(() =>
        {
            if (progress.Progress is { } stageProgress)
            {
                stageProgressBar.Value = Math.Clamp((int)Math.Round(stageProgress), 0, 100);
            }

            if (progress.Type == "stage")
            {
                statusLabel.Text = progress.Stage ?? "正在处理训练任务";
                AppendLog(statusLabel.Text);
                return;
            }

            if (progress.Type == "epoch" && progress.Epoch is { } epoch && progress.TotalEpochs is { } totalEpochs)
            {
                epochProgressBar.Value = Math.Clamp((int)Math.Round(epoch * 100.0 / Math.Max(totalEpochs, 1)), 0, 100);
                epochLabel.Text = $"第 {epoch}/{totalEpochs} 轮 | 学习率 {progress.LearningRate:0.######}";
                epochNumbers.Add(epoch);
                trainLosses.Add(progress.TrainLoss ?? double.NaN);
                validationLosses.Add(progress.ValLoss ?? double.NaN);
                peakNoxMaes.Add(GetMetric(progress.Metrics, "stack_nox_mg_m3_mae", "peak_nox_mae"));
                minO2Maes.Add(GetMetric(progress.Metrics, "stack_o2_pct_mae", "min_o2_mae"));
                RenderCharts();
                return;
            }

            if (progress.Type is "error" or "cancelled")
            {
                AppendLog(progress.Message ?? "训练过程结束。");
            }
        });
    }

    private static double GetMetric(IReadOnlyDictionary<string, double>? metrics, params string[] names)
    {
        if (metrics is not null)
        {
            foreach (var name in names)
            {
                if (metrics.TryGetValue(name, out var value))
                {
                    return value;
                }
            }
        }

        return double.NaN;
    }

    private void RenderCharts()
    {
        lossPlot.Plot.Clear();
        ConfigurePlotFont(lossPlot);
        lossPlot.Plot.Title("训练损失与验证损失");
        lossPlot.Plot.XLabel("Epoch");
        lossPlot.Plot.YLabel("Loss");
        if (epochNumbers.Count > 0)
        {
            var train = lossPlot.Plot.Add.Scatter(epochNumbers.ToArray(), trainLosses.ToArray());
            train.LegendText = "训练损失";
            train.Color = ScottPlot.Color.FromHex("#1677C8");
            var validation = lossPlot.Plot.Add.Scatter(epochNumbers.ToArray(), validationLosses.ToArray());
            validation.LegendText = "验证损失";
            validation.Color = ScottPlot.Color.FromHex("#D85A3A");
            lossPlot.Plot.ShowLegend();
        }
        lossPlot.Refresh();

        metricPlot.Plot.Clear();
        ConfigurePlotFont(metricPlot);
        metricPlot.Plot.Title("验证集关键指标");
        metricPlot.Plot.XLabel("Epoch");
        metricPlot.Plot.YLabel("MAE");
        if (epochNumbers.Count > 0)
        {
            var nox = metricPlot.Plot.Add.Scatter(epochNumbers.ToArray(), peakNoxMaes.ToArray());
            nox.LegendText = "NOx MAE";
            nox.Color = ScottPlot.Color.FromHex("#138A72");
            var o2 = metricPlot.Plot.Add.Scatter(epochNumbers.ToArray(), minO2Maes.ToArray());
            o2.LegendText = "O2 MAE";
            o2.Color = ScottPlot.Color.FromHex("#A66A14");
            metricPlot.Plot.ShowLegend();
        }
        metricPlot.Refresh();
    }

    private static void ConfigurePlotFont(FormsPlot plot)
    {
        const string fontName = "Microsoft YaHei UI";
        new PlotFontStyler(plot.Plot).Set(fontName);
        plot.Plot.Legend.FontName = fontName;
        plot.Plot.Legend.SetBestFontOnEachRender = true;
    }

    private void ValidateExportedModel(string runDirectory)
    {
        var predictor = OnnxForecastPredictor.TryCreate(runDirectory, out var status);
        predictor?.Dispose();
        if (predictor is null)
        {
            throw new InvalidOperationException($"ONNX 模型导出后无法加载: {status}");
        }

        AppendLog($"ONNX 模型校验通过: {status}");
    }

    private void RequestStop()
    {
        if (isExporting)
        {
            operationCancellation?.Cancel();
            return;
        }

        if (pythonTrainingProcess.IsRunning)
        {
            pythonTrainingProcess.RequestStop();
            statusLabel.Text = "已请求安全停止，正在等待当前批次结束...";
            AppendLog(statusLabel.Text);
        }
    }

    private void ResetTrainingView()
    {
        epochNumbers.Clear();
        trainLosses.Clear();
        validationLosses.Clear();
        peakNoxMaes.Clear();
        minO2Maes.Clear();
        stageProgressBar.Value = 0;
        epochProgressBar.Value = 0;
        epochLabel.Text = "尚未开始";
        logTextBox.Clear();
        RenderCharts();
    }

    private void SetRunState(bool running, bool scanningOnly = false)
    {
        scanButton.Enabled = !running || scanningOnly;
        startButton.Enabled = !running;
        stopButton.Enabled = running && !scanningOnly;
        openResultButton.Enabled = !string.IsNullOrWhiteSpace(latestRunDirectory) && Directory.Exists(latestRunDirectory);
        dataDirectoryTextBox.Enabled = !running;
        pythonPathTextBox.Enabled = !running;
        startDatePicker.Enabled = !running;
        endDatePicker.Enabled = !running;
        epochsInput.Enabled = !running;
        batchSizeInput.Enabled = !running;
        deviceComboBox.Enabled = !running;
    }

    private void BrowseDirectory(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 yyyyMM.db 历史文件的训练数据目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private async Task<bool> VerifyPythonEnvironmentAsync()
    {
        var pythonPath = pythonPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            MessageBox.Show("请填写 Python 程序路径。", "训练环境", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import sys, numpy, torch; print(f'Python {sys.version.split()[0]}, NumPy {numpy.__version__}, PyTorch {torch.__version__}')");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Python 程序。");
            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError) ? "Python 环境检查失败。" : standardError.Trim());
            }

            AppendLog($"训练环境已验证: {standardOutput.Trim()}");
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show($"Python 环境不可用: {error.Message}", "训练环境", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void BrowsePython()
    {
        using var dialog = new OpenFileDialog { Filter = "Python 程序 (python.exe)|python.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            pythonPathTextBox.Text = dialog.FileName;
        }
    }

    private void OpenResultDirectory()
    {
        if (latestRunDirectory is not null && Directory.Exists(latestRunDirectory))
        {
            Process.Start(new ProcessStartInfo { FileName = latestRunDirectory, UseShellExecute = true });
        }
    }

    private void AppendLog(string text)
    {
        if (logTextBox.InvokeRequired)
        {
            InvokeOnUi(() => AppendLog(text));
            return;
        }

        logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        logTextBox.SelectionStart = logTextBox.TextLength;
        logTextBox.ScrollToCaret();
        if (latestRunDirectory is not null)
        {
            File.AppendAllText(Path.Combine(latestRunDirectory, "training.log"), $"[{DateTime.Now:O}] {text}{Environment.NewLine}");
        }
    }

    private void InvokeOnUi(Action action)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }

        action();
    }

    private static string CreateRunDirectory(string dataDirectory)
    {
        var baseName = DateTime.Now.ToString("yyyyMMddHHmmss");
        var directory = Path.Combine(dataDirectory, baseName);
        for (var suffix = 1; Directory.Exists(directory); suffix++)
        {
            directory = Path.Combine(dataDirectory, $"{baseName}-{suffix:D2}");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "pyproject.toml")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Environment.CurrentDirectory;
    }
}
