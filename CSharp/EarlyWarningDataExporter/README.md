# 早期预警数据导出器

将 `db_data` 中按天存放的 LiteDB 快照导出到 `db_data/export`。运行：

```powershell
dotnet run --project CSharp/EarlyWarningDataExporter -- db_data db_data/export
```

每个可读日表会生成 `segments/yyyyMMdd.f32`：小端 `float32` 矩阵，固定为 86,400 行和 25 列；字段顺序见 `dataset_manifest.json`。缺失秒、重复时间戳之外的秒、空值与非有限数值均以 `NaN` 保留，供训练阶段掩码处理。

只有 LiteDB 日表无法读取时才跳过当天，不会留下部分文件。`export_report.json` 记录各天的导出状态、缺失秒、重复时间戳和无效数值数量。
