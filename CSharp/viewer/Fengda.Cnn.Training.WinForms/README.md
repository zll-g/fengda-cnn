# 启动锅炉预测模型训练工作台

该项目是 .NET 10 WinForms 训练工作台。它读取预测服务历史数据目录中的月度 LiteDB 文件，并启动现有 Python/PyTorch 训练程序导出 ONNX 模型。

## 使用方式

1. 启动 `Fengda.Cnn.Training.WinForms`。
2. 选择训练数据目录。目录根部应包含类似 `202606.db` 的历史快照文件；`faults.db` 会被忽略。
3. 选择 Python 程序、数据日期范围、训练轮次、批次大小和运算设备。
4. 点击“扫描数据”确认可用日期和记录数。
5. 点击“生成快照并开始训练”。

训练结果保存在用户选择的数据目录下，以开始训练时的时间戳为目录名，例如 `20260910143020`。

## 大数据处理

- 不生成全量 CSV。
- 仅导出模型需要的 8 个输入字段和 NOx/O2/NO2 三个目标字段。
- 训练快照按天写为 `float32` 二进制分区。
- Python 使用 `numpy.memmap` 读取日分区，按批次即时构建 60 秒输入和未来 60 秒标签。
- 训练、验证、测试按时间顺序划分；相邻时间段只有在模型窗口发生重叠时才保留 119 秒隔离区。

## 输出文件

- `dataset_manifest.json`：本次导出的训练数据分区清单。
- `manifest.json`：训练目录、时间范围和界面参数。
- `split_summary.json`：训练、验证、测试划分结果。
- `normalization_stats.json`：仅基于训练集计算的归一化参数。
- `prepared_data_summary.json`：模型输入/输出形状与特征列定义。
- `history.csv`、`train_summary.json`、`training.log`：训练过程和评估结果。
- `best_model.onnx`：导出的模型。

训练完成后，工作台会使用现有 C# ONNX 预测库加载模型，确认模型包可以用于实时预测服务。
