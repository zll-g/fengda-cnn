# CNN-LSTM-Attention 神经网络结构说明

对应实现文件：[src/fengda_cnn/models/cnn_lstm_attention.py](/D:/CodeX/fengda-cnn/src/fengda_cnn/models/cnn_lstm_attention.py)

结构图文件：[神经网络结构图-CNN-LSTM-Attention.svg](/D:/CodeX/fengda-cnn/神经网络结构图-CNN-LSTM-Attention.svg)

## 总体规格

- 输入窗口：`60 x 24`
- 输出窗口：`60 x 5`
- 功能层数：`14`
- 可训练层数：`7`
- 总参数量：`288,236`

## 输入输出解释

- `60 x 24`
  - `60` 个历史时间步
  - `24` 个输入特征
- `60 x 5`
  - `60` 个未来时间步
  - `5` 个预测目标：
    - `NOx`
    - `SO2`
    - `O2`
    - `粉尘`
    - `NO2`

## 分层信息

| 编号 | 层名称 | 类型 | 输出形状 | 神经元/通道 | 参数量 | 说明 |
| --- | --- | --- | --- | --- | ---: | --- |
| 0 | Input | 输入层 | `60 x 24` | `24` 特征/时刻 | 0 | 最近 `60` 秒输入窗口 |
| 1 | Conv1D-1 + ReLU | 卷积层 | `60 x 64` | `64` 通道 | 4,672 | 提取局部时序模式 |
| 2 | Dropout-1 | Dropout | `60 x 64` | `64` 通道 | 0 | `p=0.2` |
| 3 | Conv1D-2 + ReLU | 卷积层 | `60 x 128` | `128` 通道 | 24,704 | 提取更高阶局部特征 |
| 4 | Dropout-2 | Dropout | `60 x 128` | `128` 通道 | 0 | `p=0.2` |
| 5 | LSTM | 循环层 | `60 x 128` | `128` 隐藏单元 | 132,096 | 建模时序依赖 |
| 6 | Dropout-3 | Dropout | `60 x 128` | `128` 隐藏单元 | 0 | `p=0.2` |
| 7 | Attention Projection | 全连接层 | `60 x 128` | `128` 单元 | 16,512 | 映射到注意力空间 |
| 8 | Attention Score + Softmax | 注意力打分层 | `60 x 1` | `1` 分数/时刻 | 128 | 计算时间步权重 |
| 9 | Context Vector | 汇聚层 | `128` | `128` 单元 | 0 | 对输入窗口加权汇聚 |
| 10 | Dense-1 + ReLU | 全连接层 | `256` | `256` 神经元 | 33,024 | 融合上下文特征 |
| 11 | Dropout-4 | Dropout | `256` | `256` 神经元 | 0 | `p=0.3` |
| 12 | Dense-2 | 输出层 | `300` | `300` 神经元 | 77,100 | 对应 `60 x 5` 输出展开 |
| 13 | Reshape | 重塑层 | `60 x 5` | `60` 时间点 x `5` 指标 | 0 | 还原为未来 `60` 秒预测序列 |

## 可训练层

1. `Conv1D-1`
2. `Conv1D-2`
3. `LSTM`
4. `Attention Projection`
5. `Attention Score`
6. `Dense-1`
7. `Dense-2`

## 参数量核对

| 层 | 参数量 |
| --- | ---: |
| Conv1D-1 | 4,672 |
| Conv1D-2 | 24,704 |
| LSTM | 132,096 |
| Attention Projection | 16,512 |
| Attention Score | 128 |
| Dense-1 | 33,024 |
| Dense-2 | 77,100 |
| 合计 | 288,236 |
