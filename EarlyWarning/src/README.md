# 异常预警训练程序

`train_early_warning.py` 实现 V1.6 方案中的训练流程：

- 仅使用正常运行数据；中心气阀位或外环气阀位任一有效且大于 `1%` 才视为运行。
- 对运行段前后各剔除 60 秒，并以 60 秒窗口、1 秒步长构建训练样本。
- 使用不包含 NOx、O2、过热蒸汽温度、过热蒸汽压力的运行特征训练对角协方差 GMM。
- 使用 25 个过程量与 3 个内部目标掩码通道训练条件化掩码去噪 LSTM-AE。
- 导出 PyTorch 权重、ONNX、GMM、Z-score 参数、特征契约哈希和训练清单。

## LiteDB 导出数据训练

已导出的 `db_data/export` 可直接用于正式 25 点训练。训练器只读取指定日期，并按自然日时间顺序切分整天数据；根据可用 60 秒窗口数尽量达到训练/验证/测试 `7:2:1`，不随机拆分重叠窗口。数据日期足够时，验证和测试至少各覆盖两个自然日。中心气阀位或外环气阀位任一大于 `1%` 才保留运行数据；任何 60 秒窗口只要包含一个空值、`NaN` 或无穷值，就会被完全丢弃，不做插值。输出清单和控制台会显示各集合的日期范围及实际窗口比例。

仅训练 2026 年 6 月：

```powershell
.\.venv\Scripts\python.exe .\EarlyWarning\src\train_early_warning.py `
  --export-manifest .\db_data\export\dataset_manifest.json `
  --start-date 2026-06-01 `
  --end-date 2026-06-30
```

训练清单中的 `data_summary.daily_partitions` 会记录每一天的运行候选窗口数、因空数据丢弃的窗口数及最终可用窗口数。

## NOx/O2 当前工况边界异常模型

新增的 `train_emission_bounds.py` 与 LSTM-AE 训练器相互独立，不修改其代码或产物。该模型使用 8 个燃烧/FGR/燃气过程参数的 60 秒历史窗口，按时间对齐的当前 NOx、O2 实测值作为训练标签，直接输出当前工况下的 NOx 下限/上限和 O2 下限/上限；NOx、O2 不作为模型输入，也不预测未来轨迹。

```powershell
.\.venv\Scripts\python.exe .\EarlyWarning\src\train_emission_bounds.py `
  --export-manifest .\db_data\export\dataset_manifest.json `
  --start-date 2026-06-01 `
  --end-date 2026-08-30
```

- 默认使用 60 秒输入窗口、5 秒训练滑窗步长、上下分位数 `0.01 / 0.99`，并按整日时间顺序尽量按可用窗口数切分 `7:2:1`；有效自然日不少于10天时，验证集和测试集各至少覆盖2天。在线推理可以每秒运行。
- `--target-lag-seconds` 用于把输入窗口终点向前平移，以补偿过程输送及分析仪响应时间；默认 `0`，正式训练前应根据测点时间戳确认。
- 运行状态分两层：原阀位规则仍用于判定开机/停机；模型训练另要求有效燃烧。默认进入条件为天然气流量 `>=50 m³/h`、鼓风机频率 `>=5 Hz`、鼓风机电流 `>=5 A`，且阀位规则判运行，连续满足 `60` 秒。退出条件为流量 `<20 m³/h`、频率 `<3 Hz`、电流 `<3 A` 任一成立；过渡行立即不参与训练，持续 `10` 秒后退出有效燃烧状态。
- 上述过程阈值是可配置的初始值，尚需现场确认；分别可通过 `--effective-gas-flow-enter-m3-h`、`--effective-gas-flow-exit-m3-h`、`--effective-blower-frequency-enter-hz`、`--effective-blower-frequency-exit-hz`、`--effective-blower-current-enter-a`、`--effective-blower-current-exit-a`、`--effective-enter-dwell-seconds`、`--effective-exit-confirm-seconds` 修改。NOx/O2浓度不用于判定有效工况，避免按输出值筛掉真实排放异常。
- 进入确认时，首个训练目标必须已具备完整连续的 60 秒有效燃烧输入历史。在线门控应采用同一阈值、迟滞、持续时间及完整窗口规则；不满足时返回 `warming_up` / `not_applicable`，不生成正常边界。
- `--transition-trim-seconds` 可选地在有效片段两端增加额外裁剪，默认 `0`；只有确认分析仪延迟或转段残留时再配置，避免与 60 秒进入稳定确认重复裁剪。
- `--max-windows-per-split` 默认 `0`，即训练/验证/测试使用所有有效窗口；正式训练不要传入正数。`data_summary.json`、`manifest.json` 和控制台会分别显示有效窗口总数与实际入集窗口数。
- `--prepare-only` 只生成数据切分清单和历史参数范围统计，不训练模型。
- 可传入 `--recommendation-nox-max`、`--recommendation-o2-min`、`--recommendation-o2-max`，仅用满足给定排放条件的训练行生成历史参考参数范围。未传排放条件时，输出标注为描述性历史范围，不视为排放达标推荐。
- `data_summary.json` 和模型 `manifest.json` 会记录阀位运行规则、有效燃烧阈值、进入/退出持续秒数、逐日阀位运行行数/有效燃烧行数/筛选后行数及切分窗口数，便于 C# 平台复用同一规则。
- 产物独立写入 `EarlyWarning/artifacts/emission_bounds_*`，包括 ONNX、PyTorch 权重、标准化与验证集边界校准参数、训练指标、数据切分清单和 `historical_input_ranges.json`。ONNX 输入为原始工程单位 `[batch, 60, 8]`，输出顺序为 `[NOx下限, NOx上限, O2下限, O2上限]`，单位分别为 `mg/m³`、`mg/m³`、`%`、`%`。

当前历史导出字段将“天然气流量瞬时值”映射为 `natural_gas_inlet_flow_m3_h`（天然气入口管道流量）；投产前需确认现场标签、工程单位和时间聚合与目标点一致。历史分位范围只是关联工况下的参考，不构成自动控制设定值。

## 合成异常回放测试

以下命令基于最终模型生成 NOx、蒸汽温度、蒸汽压力的突升/渐升及组合异常，并额外校验数据缺失和停机时的拒绝评分逻辑。报告、用例定义和 `.npz` 窗口数据写入模型目录的 `simulated_anomaly_test` 子目录。

```powershell
.\.venv\Scripts\python.exe .\EarlyWarning\src\test_simulated_anomalies.py
```

合成异常是链路测试，不能替代真实已复核异常事件的性能验收。

## 历史 CSV 训练约束

现有 `data` 目录可映射的过程测点为 23 个，而方案正式接口要求 25 个。程序默认拒绝训练正式模型，防止悄悄产出不符合方案的模型。

补齐两个点位映射和数据后执行正式训练：

```powershell
.\.venv\Scripts\python.exe .\EarlyWarning\src\train_early_warning.py
```

仅用于当前 23 点数据的开发验证可显式执行：

```powershell
.\.venv\Scripts\python.exe .\EarlyWarning\src\train_early_warning.py `
  --allow-reduced-schema `
  --prepare-only
```

去掉 `--prepare-only` 后将开始训练并导出 ONNX。带有 `reduced_schema_development_only` 的训练产物不得用于正式上线。
