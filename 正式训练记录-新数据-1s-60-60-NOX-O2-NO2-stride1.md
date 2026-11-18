# 正式训练记录（新数据 / 1s / 60输入 / 60输出 / NOX-O2-NO2 / stride1）

## 正式模型

- 训练目录：`artifacts/formal_training_1s_60_60_nox_o2_no2_stride1`
- 样本目录：`artifacts/samples_split_1s_60_60_nox_o2_no2_stride1`
- PyTorch 模型：`artifacts/formal_training_1s_60_60_nox_o2_no2_stride1/best_model.pt`
- ONNX 模型：`artifacts/formal_training_1s_60_60_nox_o2_no2_stride1/best_model.onnx`
- 单次推理验证：`artifacts/formal_inference_1s_60_60_nox_o2_no2_stride1/test_sample_0000`

## 数据配置

- 数据划分：使用项目根目录 `data/train`、`data/val`、`data/test` 固定划分
- 输入窗口：60 秒，1 秒粒度
- 输出窗口：未来 60 秒，1 秒粒度
- 滑窗步长：1 秒
- 输入参数数：8
- 输出目标数：3

## 输入参数

- `burner_center_gas_valve_pct`：燃烧器中心气调节阀阀位反馈
- `burner_outer_gas_valve_pct`：燃烧器外环气调节阀阀位反馈
- `burner_air_damper_pct`：燃烧器风门调节阀阀位反馈
- `blower_frequency_hz`：鼓风机变频器频率反馈
- `blower_current_a`：鼓风机变频器电流
- `fgr_valve_pct`：FGR调节阀阀位反馈
- `fgr_fan_frequency_hz`：FGR风机变频器频率反馈
- `natural_gas_inlet_flow_m3_h`：2号启动锅炉天然气入口流量

## 输出目标

- `stack_nox_mg_m3`：2号启动锅炉烟囱NOX排放浓度，损失权重 0.7
- `stack_o2_pct`：2号启动锅炉烟囱O2排放浓度，损失权重 0.2
- `stack_no2_mg_m3`：2号启动锅炉烟囱NO2排放浓度，损失权重 0.1

## 样本规模

- 训练集：`146651`，形状 `X=(146651, 60, 8)`，`y=(146651, 60, 3)`
- 验证集：`66785`，形状 `X=(66785, 60, 8)`，`y=(66785, 60, 3)`
- 测试集：`22021`，形状 `X=(22021, 60, 8)`，`y=(22021, 60, 3)`

## 模型与训练参数

- 模型：CNN-LSTM-Attention + Dropout
- `conv_channels_1=64`
- `conv_channels_2=128`
- `lstm_hidden_size=128`
- `dense_hidden_size=256`
- `conv_dropout=0.28`
- `lstm_dropout=0.28`
- `dense_dropout=0.35`
- `epochs=40`
- `batch_size=256`
- `learning_rate=0.00045`
- `weight_decay=0.0005`
- `loss=huber`
- `target_loss_weights=0.7,0.2,0.1`
- `selection_metric=peak_nox_mae`
- `early_stopping_patience=8`

## 训练结果

- `best_epoch=1`
- `epochs_ran=9`
- `best_val_loss=0.144178`
- `best_selection_score=2.689071`
- `test_loss=0.034508`
- `test_overall_mae=0.887806`
- `test_overall_rmse=1.390612`
- `test_stack_nox_mg_m3_mae=1.529838`
- `test_stack_o2_pct_mae=0.837254`
- `test_stack_no2_mg_m3_mae=0.296325`
- `test_peak_nox_mae=1.971230`
- `test_min_o2_mae=0.829266`
- `test_peak_no2_mae=0.323391`

## 与 120输入正式模型对比

- `overall MAE`：从 `1.063374` 改善到 `0.887806`
- `NOX MAE`：从 `2.111954` 改善到 `1.529838`
- `peak NOX MAE`：从 `2.629605` 改善到 `1.971230`
- `NO2 MAE`：从 `0.414263` 改善到 `0.296325`
- `O2 MAE`：从 `0.663906` 变为 `0.837254`，O2 单项变差

## 验证结论

- ONNX checker 已通过
- ONNX 输入形状：`[batch, 60, 8]`
- ONNX 输出形状：`[batch, 60, 3]`
- 单次推理脚本已通过
- WinForms 查看器构建已通过，构建目录：`CSharp/viewer/bin/final-60-build`
- 查看器默认模型目录已切换到本正式训练目录
