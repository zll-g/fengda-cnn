# NOx/O2 上下限异常分析模型应用平台开发说明

## 1. 文档目的

本文定义 `conditional_emission_bounds_v1` 的应用平台接入契约，供 C# WebApi、后台推理服务、预警界面和历史查询开发使用。本文以仓库中的训练代码及 `2026-07-01` 至 `2026-07-15` 训练产物为准。

该模型是**当前工况下排放浓度正常范围估计器**：输入燃烧过程最近 60 秒的参数，输出当前对齐时刻 NOx、O2 的正常下限和上限，再由平台将实测排放浓度与边界比较。它不是未来趋势预测模型，也不是控制器。

本文描述的是平台接入方案。当前仓库尚未实现该模型的 C# 在线推理服务、专用 API 或专用边界结果存储集合；不要将下文的建议 DTO、接口和预警策略误认为已上线能力。

## 2. 模型能力与限制

| 项目 | 契约 |
|---|---|
| 模型版本 | `conditional_emission_bounds_v1` |
| 任务 | 当前工况 NOx/O2 正常区间估计及区间外异常识别 |
| 输入 | 8 个过程测点，连续 60 个一秒采样点，按下表顺序排列 |
| 输出 | NOx 下限、NOx 上限、O2 下限、O2 上限，均为原始工程单位 |
| ONNX 输入名 | `input_sequence` |
| ONNX 输入形状 | `[batch, 60, 8]`，`float32` |
| ONNX 输出名 | `emission_bounds` |
| ONNX 输出形状 | `[batch, 4]`，`float32` |
| 训练分位数 | `0.01 / 0.99`，目标名义覆盖率 98% |
| 默认目标滞后 | 0 秒；平台必须确认分析仪延迟后再确定生产配置 |
| 在线频率 | 每秒可推理一次，要求窗口时序完整 |

NOx/O2 实测值不是模型输入，但必须由实时快照提供给平台完成区间判定。模型输出的上下限不是排放法规限值，也不代表设备安全联锁阈值。超出模型边界表示相对已训练正常工况的统计异常，最终处置应遵循现场规程。

## 3. 数据来源与关系

### 3.1 已有数据链路

```text
LiteDB 数据文件
  └─ snapshots_yyyyMMdd collection（一条 BoilerSnapshot 对应一个秒级时刻）
       ├─ RecordedAt：时间键，按秒对齐
       └─ GYDCS_*：点位读数
            ↓ EarlyWarningDataExporter
db_data/export/dataset_manifest.json + segments/yyyyMMdd.f32
            ↓ train_emission_bounds.py
模型窗口 [t-59s, ..., t] + t 时刻 NOx/O2 标签
            ↓ 训练/校准
ONNX + manifest.json + calibration.json + normalization.json
```

训练数据不是通过多张关系表 join 得到的。LiteDB 日集合中的点位已经合并在同一条 `BoilerSnapshot` 快照内，按 `RecordedAt` 排序后导出为固定列序的秒级矩阵。数据导出使用 25 列；新模型只按 `dataset_manifest.json` 的字段名定位下列 10 个必需字段，不依赖这些字段在 25 列中的固定偏移。

### 3.2 模型点位映射

数组顺序是模型契约的一部分；在线组装输入时不能按中文名称排序或任意调整顺序。

| 序号 | 模型字段 | 中文点位 | BoilerSnapshot 字段 / Historian 标签 | 单位 |
|---:|---|---|---|---|
| 0 | `burner_air_damper_pct` | 燃烧器风门调节阀阀位反馈 | `GYDCS_K0QHL10AA101AI3` / `K0QHL10AA101AI3.UNIT3@NET3` | % |
| 1 | `blower_frequency_hz` | 鼓风机变频器频率反馈 | `GYDCS_K0QHL10AN001P` / `K0QHL10AN001P.UNIT3@NET3` | Hz |
| 2 | `blower_current_a` | 鼓风机变频器电流 | `GYDCS_K0QHL10AN001I` / `K0QHL10AN001I.UNIT3@NET3` | A |
| 3 | `burner_center_gas_valve_pct` | 燃烧器中心气调节阀阀位反馈 | `GYDCS_K0QHL10AA101AI5` / `K0QHL10AA101AI5.UNIT3@NET3` | % |
| 4 | `burner_outer_gas_valve_pct` | 燃烧器外环气调节阀阀位反馈 | `GYDCS_K0QHL10AA101AI4` / `K0QHL10AA101AI4.UNIT3@NET3` | % |
| 5 | `fgr_valve_pct` | FGR 调节阀阀位反馈 | `GYDCS_K0QHL10AA101AI6` / `K0QHL10AA101AI6.UNIT3@NET3` | % |
| 6 | `fgr_fan_frequency_hz` | FGR 风机变频器频率反馈 | `GYDCS_K0QHH10AV001P` / `K0QHH10AV001P.UNIT3@NET3` | Hz |
| 7 | `natural_gas_inlet_flow_m3_h` | 天然气流量瞬时值（当前导出映射为天然气入口管道流量） | `GYDCS_K0QHH10CF101` / `K0QHH10CF101-1.UNIT3@NET3` | m³/h（需现场确认） |
| - | `stack_nox_mg_m3` | 烟囱 NOx 排放浓度（标签，不作为输入） | `GYDCS_K0HNE10CQ101` / `K0HNE10CQ101.UNIT3@NET3` | mg/m³ |
| - | `stack_o2_pct` | 烟囱 O2 排放浓度（标签，不作为输入） | `GYDCS_K0HNE10CQ103` / `K0HNE10CQ103.UNIT3@NET3` | % |

天然气字段的现场标签、工程单位、瞬时值/累计值属性及采样语义必须在投产前确认。当前训练映射选用 `GYDCS_K0QHH10CF101`，不是累计量字段 `GYDCS_K0QHH10CF101_SUM`。

### 3.3 导出文件格式

- `dataset_manifest.json` 提供 `fields`、字段说明、日分段文件名、起始时间和行数。
- `segments/yyyyMMdd.f32` 是 little-endian `float32` 二进制矩阵，每个完整日 86,400 行；每行表示一秒，列顺序由 manifest 的 `fields` 决定。
- 缺秒、空值或非有限值用 `NaN` 占位。训练器不插值：任一必需输入或标签无效的秒不参与样本窗口。
- 在线服务不应直接把缺失值替换为 0。窗口不完整时返回 `data_invalid` / `warming_up`，不执行边界判定。

## 4. 时间对齐与样本规则

设要判定的排放实测时刻为 `t`，`target_lag_seconds = L`：

- 模型输入取 `t-L-59s` 至 `t-L`，共 60 个按秒递增的点。
- 标签/实测排放值取 `t` 时刻的 NOx 和 O2。
- `L=0` 时，输入窗口最后一帧与标签时间同为 `t`。若分析仪测量存在输送或响应延迟，应通过现场数据确认 `L`，重新训练并保持线上一致；不可只在推理端单独平移。
- 每个输入字段必须按 manifest 中列出的顺序形成 `[60,8]`。每一秒必须唯一、递增、间隔一秒；不允许重复、错序或跨越缺失秒拼窗。
- 开机状态与模型适用状态分开判定。业务开机状态仍为：中心气阀位 `>1%` **或**外环气阀位 `>1%` 即运行；二者均 `<=1%` 或反馈无效时按停机处理。此规则不代表已形成有效燃烧。
- 有效燃烧默认进入条件：阀位规则判运行，且天然气入口流量 `>=50 m³/h`、鼓风机频率 `>=5 Hz`、鼓风机电流 `>=5 A` 同时成立并连续 `60` 秒。首次可用模型窗口必须由这段连续有效输入组成。
- 有效燃烧默认退出条件：天然气流量 `<20 m³/h`、鼓风机频率 `<3 Hz`、鼓风机电流 `<3 A` 任一成立。退出候选行立即标记为转段/不可分析；连续 `10` 秒后确认退出有效燃烧状态。若候选短暂恢复，仍需积累完整连续 `60` 秒有效窗口后才恢复推理。阀位停机或关键运行测点无效时立即退出。
- 上述流量、频率、电流阈值是基于当前导出数据提出的可配置初值，不是经现场批准的保护/联锁定值。正式投用前须核实测点单位、质量位与低负荷工况；优先以火焰检测或燃烧器运行反馈交叉验证。NOx/O2浓度值不得作为有效工况门控条件。
- 训练时只构建完全位于有效燃烧连续片段内的窗口，缺失/非有限值不插补。`transition_trim_seconds` 默认 `0`；如现场确认分析仪响应或烟气残留需要额外裁剪，再在训练参数中配置，避免与60秒进入稳定确认重复裁剪。在线推理使用同一有效燃烧规则，状态不满足时返回 `warming_up` / `not_applicable`，不生成正常边界。
- 训练滑窗步长默认 5 秒以控制样本量；这不限制在线推理频率。在线建议每秒更新一次结果。

## 5. 训练数据与模型结构

模型实现位于 `EarlyWarning/src/train_emission_bounds.py`，独立于原有 `train_early_warning.py`（LSTM-AE）。训练新模型不得替换或覆盖原模型文件。

网络由两层一维 CNN、LSTM、加性 Attention 和四个边界输出组成：

1. 输入 `[batch,60,8]`，按通道转置后送入 `Conv1d(8,32,kernel=3,padding=1)` 与 ReLU。
2. Dropout（0.15），再经 `Conv1d(32,64,kernel=3,padding=1)` 与 ReLU，并再次 Dropout（0.15）。
3. `LSTM(input_size=64, hidden_size=64, batch_first=True)` 编码 60 步序列。
4. 对 LSTM 序列应用 Dropout 和加性 Attention，得到一个上下文向量。
5. 输出头为 `Linear(64,64) -> LayerNorm(64) -> GELU -> Dropout(0.15) -> Linear(64,4)`。
6. 输出头生成 NOx/O2 下界及正宽度；上界通过 `lower + softplus(width)` 构造，保证上界不低于下界。

训练使用 0.01 和 0.99 分位数 Pinball Loss，优化器为 AdamW。默认训练 60 epoch、batch size 512、学习率 `5e-4`、weight decay `1e-4`、early-stopping patience 8。只有满足有效燃烧状态、测点有效且窗口未跨越转段的样本参与训练、验证和测试；训练、验证、测试按完整日期顺序尽量近似 7:2:1 切分，避免重叠窗口随机泄漏。有效自然日不少于10天时，验证集和测试集各至少覆盖2个自然日；实际窗口比例可能因逐日有效样本量差异而偏离7:2:1，报告必须保留实际日期及数量。

运行状态和转段剔除仅定义训练样本筛选，不等于平台预警等级或告警清除规则。

## 6. ONNX 部署契约

### 6.1 文件清单

一个版本目录至少包括：

| 文件 | 用途 |
|---|---|
| `best_model.onnx` | 在线推理模型；已内嵌输入标准化、输出反标准化和验证集边界校准偏移 |
| `manifest.json` | 模型版本、输入/输出字段顺序、形状、目标滞后、训练分割等元信息 |
| `calibration.json` | 校准方法与各目标上下界偏移，供审计及核对 ONNX 包装结果 |
| `normalization.json` | 输入/目标标准化统计量，ONNX 已内嵌；用于训练复现和审计 |
| `training_summary.json` | 最佳轮次、训练历史、测试指标 |
| `data_summary.json` | 每日阀位运行行、有效燃烧行、筛选后行、有效段及窗口数 |
| `historical_input_ranges.json` | 输入参数历史分位范围；默认是描述性统计，不是控制设定值 |
| `best_model.pt` | PyTorch 权重，仅供训练/分析，不供 C# 在线推理直接加载 |

### 6.2 推理调用

- C# 使用 ONNX Runtime CPU provider 即可开始部署；单条窗口输入张量形状 `[1,60,8]`、元素类型 `float32`。
- 有效燃烧门控不嵌入 ONNX 计算图；C# 必须先依据同目录 `manifest.json` 的 `effective_burning_rule` 判断状态，并确认最近 60 秒窗口连续有效，再调用 ONNX。NOx/O2边界推理本身不能替代运行状态判断。
- ONNX 接收原始工程单位数值。不要在 C# 再做 Z-score，否则会重复标准化。
- 输出 `emission_bounds[0]` 顺序固定为 `[nox_lower, nox_upper, o2_lower, o2_upper]`，工程单位依次为 `mg/m³, mg/m³, %, %`。
- Session 应按模型版本加载一次并复用；不要每秒重新创建 session。模型文件热更新需先校验版本和输入/输出签名，成功后原子切换。
- 推理结果须校验为 finite，并校验 `lower <= upper`。任何 NaN/Infinity、形状不符、版本不兼容或 ONNX 异常都应返回 `inference_error`，不可把错误输出解释为正常。

## 7. 在线状态与异常判定

### 7.1 状态机

建议平台将数据质量、运行状态、边界判定分开表达：

| 状态 | 含义 | 是否产生排放告警 |
|---|---|---|
| `stopped` | 停机判定成立 | 否 |
| `warming_up` | 阀位规则判运行，但尚未连续满足有效燃烧进入条件60秒，或尚未积累完整有效窗口 | 否 |
| `not_applicable` | 已按阀位判运行，但过程参数不满足有效燃烧条件（吹扫、待燃、低流量等） | 否 |
| `transition` | 进入/退出阈值处于确认中，或关键状态测点发生变化 | 否 |
| `data_invalid` | 输入有缺点、重复、间隔不为一秒、缺少实测排放值或数值无效 | 否；显示数据质量告警/状态 |
| `normal` | 模型推理成功，NOx 与 O2 均在边界内 | 否 |
| `out_of_range` | 至少一个目标超出对应边界 | 按平台确认的持续时间/等级规则产生异常事件 |
| `inference_error` | 模型加载或推理失败 | 否；记录系统故障并提示，不伪装成正常 |

阀位运行和模型适用状态应作为两个字段记录，避免把反馈阀位偏置导致的“阀位运行、实际无有效燃烧”解释成稳定运行。不应将 `stopped`、`warming_up`、`not_applicable`、`transition` 或 `data_invalid` 映射为绿色 `normal`，否则用户无法区分无异常和未完成分析。

在线有效工况门控必须读取模型 `manifest.json` 中的 `effective_burning_rule`，并与训练端使用同一阈值和持续时间。阀位停机/关键状态输入无效时立即退出；退出阈值候选行期间不推理；退出确认后回到未就绪状态。短暂退出候选恢复后，也要重新积累连续60秒有效输入窗口，不能跨越过渡行拼窗。

### 7.2 单点判定

对每个排放目标分别计算：

```text
if actual < lower: direction = "below_lower", exceeded = lower - actual
else if actual > upper: direction = "above_upper", exceeded = actual - upper
else: direction = "within_range", exceeded = 0
```

区间宽度为 `upper-lower`。界面可额外显示区间外偏差和相对偏差，但应明确公式及单位。例如相对偏差可定义为 `exceeded / max(upper-lower, epsilon)`；它是平台派生显示指标，不是模型原生输出，也不是训练得到的预警分数。

NOx 高于上限通常是重点关注场景，但低于下限同样属于统计区间外，建议独立标为 `below_lower`，由运行人员确认是否需要业务告警。O2 上下限异常也分别保留方向，不要把所有越界都显示为“排放升高”。

### 7.3 输入特征相对贡献度

`conditional_emission_bounds_v1` 原生只输出 NOx/O2 四个边界，不包含特征贡献度或因果解释。Demo 的输入图贡献度采用局部单特征遮蔽敏感度估算：对当前完整 60 秒窗口，逐个将某一输入特征的整段轨迹替换为训练集该特征均值，分别重算四个边界；将四个输出变化按对应目标训练标准差标准化，计算 RMS 作为该特征影响量，最后在 8 个输入间归一化为百分比。该数值表示模型输出对输入轨迹的相对敏感度，不是异常因果贡献、特征重要性的全局统计，也不能直接转换为控制建议。仅当当前窗口完整、处于有效燃烧/模型适用状态且基准推理成功时显示；否则应显示不可用状态。

### 7.4 持续确认、等级与恢复

模型只给边界，不提供持续时间、报警等级、迟滞或原因诊断。以下需作为平台配置并经运行/工艺人员确认，不得写死为模型事实：

- 连续越界多久产生提示/报警，或采用最近 N 次中 M 次越界的去抖策略。
- 提醒、预警、报警等级对应的持续时间和超限幅度。
- 恢复条件，例如连续若干秒回到边界内；恢复阈值可配置为边界内缩的 hysteresis band，防止频繁闪烁。
- 同一目标同一方向的事件合并、冷却期、升级和恢复记录策略。
- NOx 与 O2 同时越界时的组合显示及事件关联方式。

在阈值、运行规程和真实异常样本未确认前，平台可先显示“边界外/持续时间”，但不应擅自生成具有操作指令含义的自动处置建议，更不能闭环调节阀门或燃气流量。

## 8. 平台 API 建议

遵循现有 WebApi 的 `ApiResponse<T> { Code, Message, Data }` 包装方式。新增边界服务建议单独命名空间，避免与当前 LSTM-AE 的 `/api/early-warnings` 查询及 `EarlyWarningRecord` 语义混用。

建议接口（最终路由可按现有 API 规范调整）：

| 方法与路由 | 用途 |
|---|---|
| `GET /api/emission-bounds/latest` | 查询最近一次在线边界、实测值、质量状态及更新时间 |
| `GET /api/emission-bounds/history?startTime=...&endTime=...&interval=1s` | 查询指定时间区间边界与实测值，用于趋势图 |
| `GET /api/emission-bounds/alarms?month=yyyyMM&target=nox&page=1&pageSize=...` | 分页查询确认后的历史越界事件 |
| `GET /api/emission-bounds/model` | 查询当前激活模型版本、训练日期范围、滞后配置和状态 |

### 8.1 最新结果响应示例

以下为**建议 JSON 契约示例**，字段名可按 C# PascalCase 或现有序列化策略统一；示例阈值仅展示结构，不是现场限值。

```json
{
  "code": 200,
  "message": "",
  "data": {
    "recordedAt": "2026-07-15T12:30:00+08:00",
    "status": "out_of_range",
    "isRunning": true,
    "modelVersion": "conditional_emission_bounds_v1",
    "windowStart": "2026-07-15T12:29:01+08:00",
    "windowEnd": "2026-07-15T12:30:00+08:00",
    "inputQuality": {
      "expectedSamples": 60,
      "receivedSamples": 60,
      "missingFields": [],
      "message": ""
    },
    "targets": [
      {
        "field": "stack_nox_mg_m3",
        "name": "烟囱NOx排放浓度",
        "unit": "mg/m³",
        "actual": 138.0,
        "lower": 70.0,
        "upper": 102.0,
        "direction": "above_upper",
        "exceededBy": 36.0,
        "isOutOfRange": true
      },
      {
        "field": "stack_o2_pct",
        "name": "烟囱O2排放浓度",
        "unit": "%",
        "actual": 4.8,
        "lower": 3.2,
        "upper": 5.1,
        "direction": "within_range",
        "exceededBy": 0.0,
        "isOutOfRange": false
      }
    ]
  }
}
```

### 8.2 建议 C# DTO

建议使用专用 DTO，而不是把上下限塞入现有 `ReconstructedValue` 字段：

- `EmissionBoundsEvaluationDto`: `RecordedAt`, `Status`, `IsRunning`, `ModelVersion`, `WindowStart`, `WindowEnd`, `InputQuality`, `Targets`。
- `EmissionBoundsTargetDto`: `Field`, `Name`, `Unit`, `Actual`, `Lower`, `Upper`, `Direction`, `ExceededBy`, `IsOutOfRange`。
- `EmissionBoundsInputQualityDto`: `ExpectedSamples`, `ReceivedSamples`, `MissingSeconds`, `MissingFields`, `Message`。
- 历史趋势样点使用 `RecordedAt` 加 NOx/O2 的 `Actual/Lower/Upper`；所有时间升序，缺失值用 `null`，不以 0 填充。

统一使用项目既有 `ApiResponse<T>` 包装，错误时填业务错误码和中文消息。具体 DTO 属性大小写由 Web JSON 配置决定，前后端按实际 serializer contract 落地。

## 9. 存储关系建议

1. 实时输入和排放实测仍来自日分区 `snapshots_yyyyMMdd` / `BoilerSnapshot`，不复制创建一份点位事实表。
2. 当前最新评估结果放入后台内存缓存，供 latest 接口读取；缓存无结果时明确返回 `warming_up` 或 `data_invalid`。
3. 为审计与复盘，建议对已确认的越界事件单独建立月分区 collection，例如 `emission_bounds_alarms_yyyyMM`，保存目标、方向、实测值、上下限、超限量、起止时间、持续秒数、模型版本和输入窗口时间。
4. 不建议默认将每秒所有正常评估结果永久写入 `faults.db`。若产品需要全量逐秒边界趋势，应单独评估存储保留期、写入吞吐和归档策略，可按分钟降采样并在告警附近保留秒级数据。
5. 若选择复用 `EarlyWarningStore`，必须扩展清晰的 `nox`、`o2` 场景及 `lower/upper` 字段；现有 `ActualValue/ReconstructedValue/Score/Threshold` 并不能无损表达区间结果。建议优先采用独立实体和独立 API，避免影响原 LSTM-AE 告警数据。
6. 事件 ID 建议按目标、方向和首次越界时间生成；同一事件在持续期间更新 `LastSeenAt`/持续时长，不要每秒插入一条独立报警。事件恢复时写入 `RecoveredAt` 和恢复原因。

建议事件字段：`Id`, `Target`, `Direction`, `FirstSeenAt`, `LastSeenAt`, `RecoveredAt`, `DurationSeconds`, `ActualAtTrigger`, `LowerAtTrigger`, `UpperAtTrigger`, `MaxExceededBy`, `ModelVersion`, `WindowStart`, `WindowEnd`, `AcknowledgedBy`, `AcknowledgedAt`。

## 10. 页面展示建议

- NOx、O2 分开显示实测值、正常下限、正常上限、当前状态和越界方向。
- 趋势图绘制实测线及上下界带；停机、数据无效和模型未预热用不同状态提示，不绘制虚假的 0 值。
- 显示模型版本、推理时刻、窗口覆盖时间、数据有效点数、目标滞后设置和最近错误信息。
- 显示越界持续时间和事件状态；等级标签必须来自经业务确认的配置。
- 历史查询使用当时激活模型版本生成的边界。若只保存告警而未保存每秒边界，历史图需用对应版本回放计算，并注明这是回放结果；不能拿当前新模型重算后伪装成历史实时输出。
- `historical_input_ranges.json` 可作为诊断信息展示或提供给工艺人员参考；当前训练未配置排放筛选条件，文件状态是 `descriptive_only_limits_not_configured`，不可称为“推荐操作范围”或自动设定值。
- Demo 根据当前瞬时天然气流量在文件记录的各组实测最小/最大值中选择流量参考组，并将各输入参数的训练集 P10–P90 显示在输入图标题中，不覆盖图内时序曲线，也不显示中位数；若当前流量不落入任一训练组的实测范围，则显示无匹配范围。界面显示名“历史推荐运行值范围”仅为用户指定的展示标签；该统计未按排放条件筛选，不可视为经过验证的操作建议。分组外插值或将其用于控制均不受模型验证支持。

## 11. 历史基线训练验收状态（有效燃烧门控前）

以下是历史基线产物目录，不包含本次新增的 `effective_burning_rule`，不可作为已实施有效燃烧门控的模型部署。更新训练器后需重新训练并检查新产物 `manifest.json`，再按第7节接入相同的 C# 在线门控：

```text
EarlyWarning/artifacts/emission_bounds_20260701_20260715_run_20261026/
```

本次训练使用 2026-07-01 至 2026-07-15 数据，完整日期近似时间切分：训练 7月1–9日（113,558 窗口），验证 7月10、12–14日（40,796 窗口），测试 7月15日（8,938 窗口）。7月11日无运行数据；测试集只覆盖一个自然日，评估代表性有限。

测试指标：

| 指标 | NOx | O2 |
|---|---:|---:|
| 区间覆盖率 | 87.80% | 99.98% |
| 平均区间宽度 | 4.416 mg/m³ | 1.985 个百分点 |
| 实测低于下限比例 | 0.02% | 0.00% |
| 实测高于上限比例 | 12.17% | 0.02% |

尽管名义目标覆盖率为 98%，当前 7月15日测试日 NOx 实测值高于上限比例仍约 12.17%，覆盖率仅 87.80%。因此当前 ONNX 可用于联调、趋势显示和受控试运行，但 **NOx 边界尚未达到可直接作为正式生产预警阈值的验收状态**。在扩大日期范围、增加不同负荷/启停/FGR工况并复核逐日覆盖率前，不应对外承诺 98% 实际覆盖率，也不建议仅靠平台调阈值掩盖模型偏差。

生产验收建议至少包含：按日/负荷/FGR 状态分层的覆盖率和区间宽度、真实异常事件召回率、正常工况误报率、启停及数据缺失行为、分析仪时延确认、ONNX Runtime 与 PyTorch 输出一致性、模型升级和回滚演练。

## 12. 上线检查清单

- [ ] 现场逐项核对 8 个输入标签、单位、量程、更新频率和质量位。
- [ ] 确认天然气流量是瞬时流量且工程单位与训练数据一致。
- [ ] 通过历史对齐分析确认 `target_lag_seconds`；若改变须重新训练并更新模型版本。
- [ ] 核对上线 ONNX 的 `manifest.json` 中输入顺序、形状、模型版本和目标滞后。
- [ ] 从同一 `manifest.json` 读取 `effective_burning_rule`，验证 C# 在线状态机与训练端的进入/退出阈值、持续时间和过渡窗口处理一致；NOx/O2浓度不得用作有效燃烧门控。
- [ ] 验证同一 60 秒输入在 Python 与 C#/ONNX Runtime 输出一致（浮点容差由测试定义）。
- [ ] 验证缺秒、重复秒、非数值、停机、冷启动、ONNX 加载失败均能得到正确非告警状态。
- [ ] 由工艺/运行人员确认越界持续时间、告警等级、恢复迟滞、事件合并和提示文字。
- [ ] 使用多日独立测试数据通过 NOx/O2 分层覆盖率、误报与异常召回验收。
- [ ] 为每条事件记录模型版本和窗口时间，完成上线、回滚和历史复盘方案。
- [ ] 明确模型只提供监测边界，不输出自动阀门调节指令，不替代法定排放监测或现场联锁。

## 13. 相关文件

- 训练器：`EarlyWarning/src/train_emission_bounds.py`
- 训练程序说明：`EarlyWarning/src/README.md`
- 历史基线模型目录（有效燃烧门控前）：`EarlyWarning/artifacts/emission_bounds_20260701_20260715_run_20261026/`
- LiteDB 导出器：`CSharp/EarlyWarningDataExporter/Program.cs`
- 快照字段和 Historian 映射：`CSharp/monitoring/Fengda.Boiler.Monitoring/BoilerSnapshot.cs`
- 现有统一 API 响应：`CSharp/Fengda.Boiler.WebApi/Models/ApiResponse.cs`
- 现有 LSTM-AE 预警记录接口参考：`CSharp/Fengda.Boiler.WebApi/Controllers/EarlyWarningController.cs`
