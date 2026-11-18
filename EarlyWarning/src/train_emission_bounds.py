"""Train a current-condition NOx/O2 normal-boundary anomaly model."""

from __future__ import annotations

import argparse
import json
import math
import random
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import numpy as np
import torch
from torch import nn
from torch.utils.data import DataLoader, Dataset


ROOT = Path(__file__).resolve().parents[2]
WINDOW_SIZE = 60
INPUT_FIELDS = (
    "burner_air_damper_pct",
    "blower_frequency_hz",
    "blower_current_a",
    "burner_center_gas_valve_pct",
    "burner_outer_gas_valve_pct",
    "fgr_valve_pct",
    "fgr_fan_frequency_hz",
    "natural_gas_inlet_flow_m3_h",
)
TARGET_FIELDS = ("stack_nox_mg_m3", "stack_o2_pct")
TARGET_NAMES = ("nox", "o2")


@dataclass
class Segment:
    day: str
    source_file: str
    inputs: np.ndarray
    targets: np.ndarray


@dataclass(frozen=True)
class EffectiveBurningConfig:
    gas_flow_enter_m3_h: float = 50.0
    gas_flow_exit_m3_h: float = 20.0
    blower_frequency_enter_hz: float = 5.0
    blower_frequency_exit_hz: float = 3.0
    blower_current_enter_a: float = 5.0
    blower_current_exit_a: float = 3.0
    enter_dwell_seconds: int = 60
    exit_confirm_seconds: int = 10

    def to_dict(self) -> dict[str, object]:
        return {
            "valve_running_rule": "center gas valve > 1% or outer gas valve > 1%; invalid feedback counts as closed",
            "entry_conditions_all": {
                "natural_gas_inlet_flow_m3_h_gte": self.gas_flow_enter_m3_h,
                "blower_frequency_hz_gte": self.blower_frequency_enter_hz,
                "blower_current_a_gte": self.blower_current_enter_a,
            },
            "exit_conditions_any": {
                "natural_gas_inlet_flow_m3_h_lt": self.gas_flow_exit_m3_h,
                "blower_frequency_hz_lt": self.blower_frequency_exit_hz,
                "blower_current_a_lt": self.blower_current_exit_a,
            },
            "enter_dwell_seconds": self.enter_dwell_seconds,
            "exit_confirm_seconds": self.exit_confirm_seconds,
            "transition_policy": "entry requires continuous entry conditions; exit candidates are immediately ineligible; a persistent exit resets the state",
            "threshold_status": "configurable provisional defaults; field validation required",
        }


class AdditiveAttention(nn.Module):
    def __init__(self, hidden_size: int) -> None:
        super().__init__()
        self.projection = nn.Linear(hidden_size, hidden_size)
        self.score = nn.Linear(hidden_size, 1, bias=False)

    def forward(self, sequence: torch.Tensor) -> torch.Tensor:
        weights = torch.softmax(self.score(torch.tanh(self.projection(sequence))).squeeze(-1), dim=1)
        return torch.sum(sequence * weights.unsqueeze(-1), dim=1)


class EmissionBoundsModel(nn.Module):
    """CNN-LSTM-attention encoder with two monotonic quantile-bound heads."""

    def __init__(self, input_size: int = len(INPUT_FIELDS), hidden_size: int = 64, dropout: float = 0.15) -> None:
        super().__init__()
        self.conv1 = nn.Conv1d(input_size, 32, kernel_size=3, padding=1)
        self.conv2 = nn.Conv1d(32, 64, kernel_size=3, padding=1)
        self.conv_dropout = nn.Dropout(dropout)
        self.lstm = nn.LSTM(64, hidden_size, batch_first=True)
        self.lstm_dropout = nn.Dropout(dropout)
        self.attention = AdditiveAttention(hidden_size)
        self.head = nn.Sequential(
            nn.Linear(hidden_size, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(64, 4),
        )

    def forward(self, inputs: torch.Tensor) -> torch.Tensor:
        values = inputs.transpose(1, 2)
        values = self.conv_dropout(torch.relu(self.conv1(values)))
        values = self.conv_dropout(torch.relu(self.conv2(values))).transpose(1, 2)
        values, _ = self.lstm(values)
        raw = self.head(self.attention(self.lstm_dropout(values)))
        nox_low = raw[:, 0]
        nox_high = nox_low + torch.nn.functional.softplus(raw[:, 1])
        o2_low = raw[:, 2]
        o2_high = o2_low + torch.nn.functional.softplus(raw[:, 3])
        return torch.stack((nox_low, nox_high, o2_low, o2_high), dim=1)


class RawUnitsOnnxWrapper(nn.Module):
    """Export raw process inputs and raw engineering-unit bounds as the ONNX contract."""

    def __init__(self, model: EmissionBoundsModel, input_mean: np.ndarray, input_std: np.ndarray,
                 target_mean: np.ndarray, target_std: np.ndarray, calibration_margin: np.ndarray) -> None:
        super().__init__()
        self.model = model
        self.register_buffer("input_mean", torch.as_tensor(input_mean, dtype=torch.float32).view(1, 1, -1))
        self.register_buffer("input_std", torch.as_tensor(input_std, dtype=torch.float32).view(1, 1, -1))
        bound_mean = np.asarray([target_mean[0], target_mean[0], target_mean[1], target_mean[1]], dtype=np.float32)
        bound_std = np.asarray([target_std[0], target_std[0], target_std[1], target_std[1]], dtype=np.float32)
        self.register_buffer("target_mean", torch.as_tensor(bound_mean, dtype=torch.float32).view(1, -1))
        self.register_buffer("target_std", torch.as_tensor(bound_std, dtype=torch.float32).view(1, -1))
        self.register_buffer("calibration_margin", torch.as_tensor(calibration_margin, dtype=torch.float32).view(1, -1))

    def forward(self, raw_inputs: torch.Tensor) -> torch.Tensor:
        standardized = (raw_inputs - self.input_mean) / self.input_std
        standardized_bounds = self.model(standardized)
        target_mean = self.target_mean.expand_as(standardized_bounds)
        target_std = self.target_std.expand_as(standardized_bounds)
        raw_bounds = standardized_bounds * target_std + target_mean + self.calibration_margin
        return torch.stack((torch.clamp_min(raw_bounds[:, 0], 0.0), raw_bounds[:, 1],
                            torch.clamp_min(raw_bounds[:, 2], 0.0), raw_bounds[:, 3]), dim=1)


class WindowDataset(Dataset[tuple[torch.Tensor, torch.Tensor]]):
    def __init__(self, segments: list[Segment], indexes: np.ndarray, window_size: int, target_lag: int,
                 input_mean: np.ndarray, input_std: np.ndarray, target_mean: np.ndarray, target_std: np.ndarray) -> None:
        self.segments = segments
        self.indexes = indexes
        self.window_size = window_size
        self.target_lag = target_lag
        self.input_mean = input_mean
        self.input_std = input_std
        self.target_mean = target_mean
        self.target_std = target_std

    def __len__(self) -> int:
        return len(self.indexes)

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor]:
        segment_id, target_index = self.indexes[index]
        segment = self.segments[int(segment_id)]
        input_end = int(target_index) - self.target_lag + 1
        input_start = input_end - self.window_size
        x = (segment.inputs[input_start:input_end] - self.input_mean) / self.input_std
        y = (segment.targets[int(target_index)] - self.target_mean) / self.target_std
        return torch.from_numpy(x.astype(np.float32, copy=False)), torch.from_numpy(y.astype(np.float32, copy=False))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export-manifest", default=str(ROOT / "db_data" / "export" / "dataset_manifest.json"))
    parser.add_argument("--start-date", default="", help="Inclusive YYYY-MM-DD.")
    parser.add_argument("--end-date", default="", help="Inclusive YYYY-MM-DD.")
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--window-size", type=int, default=WINDOW_SIZE)
    parser.add_argument("--target-lag-seconds", type=int, default=0,
                        help="End the input window this many seconds before the paired NOx/O2 measurement.")
    parser.add_argument("--stride", type=int, default=5, help="Training-window step in seconds; inference may run every second.")
    parser.add_argument("--lower-quantile", type=float, default=0.01)
    parser.add_argument("--upper-quantile", type=float, default=0.99)
    parser.add_argument("--epochs", type=int, default=60)
    parser.add_argument("--batch-size", type=int, default=512)
    parser.add_argument("--learning-rate", type=float, default=5e-4)
    parser.add_argument("--weight-decay", type=float, default=1e-4)
    parser.add_argument("--early-stopping-patience", type=int, default=8)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--max-windows-per-split", type=int, default=0,
                        help="Optional deterministic cap for a short development run; 0 uses every window.")
    parser.add_argument("--effective-gas-flow-enter-m3-h", type=float, default=50.0)
    parser.add_argument("--effective-gas-flow-exit-m3-h", type=float, default=20.0)
    parser.add_argument("--effective-blower-frequency-enter-hz", type=float, default=5.0)
    parser.add_argument("--effective-blower-frequency-exit-hz", type=float, default=3.0)
    parser.add_argument("--effective-blower-current-enter-a", type=float, default=5.0)
    parser.add_argument("--effective-blower-current-exit-a", type=float, default=3.0)
    parser.add_argument("--effective-enter-dwell-seconds", type=int, default=60)
    parser.add_argument("--effective-exit-confirm-seconds", type=int, default=10)
    parser.add_argument("--transition-trim-seconds", type=int, default=0,
                        help="Optional additional trim at effective-run edges after state gating.")
    parser.add_argument("--recommendation-nox-max", type=float, default=None)
    parser.add_argument("--recommendation-o2-min", type=float, default=None)
    parser.add_argument("--recommendation-o2-max", type=float, default=None)
    parser.add_argument("--prepare-only", action="store_true", help="Write split and range reports without fitting the neural network.")
    return parser.parse_args()


def effective_burning_mask(inputs: np.ndarray, valve_running: np.ndarray,
                           config: EffectiveBurningConfig) -> np.ndarray:
    """Return rows belonging to stable effective combustion, with causal hysteresis."""
    flow = inputs[:, INPUT_FIELDS.index("natural_gas_inlet_flow_m3_h")]
    blower_frequency = inputs[:, INPUT_FIELDS.index("blower_frequency_hz")]
    blower_current = inputs[:, INPUT_FIELDS.index("blower_current_a")]
    operational_values = np.column_stack((flow, blower_frequency, blower_current))
    operational_valid = np.isfinite(operational_values).all(axis=1)
    entry_candidate = (
        (flow >= config.gas_flow_enter_m3_h)
        & (blower_frequency >= config.blower_frequency_enter_hz)
        & (blower_current >= config.blower_current_enter_a)
    )
    exit_candidate = (
        (flow < config.gas_flow_exit_m3_h)
        | (blower_frequency < config.blower_frequency_exit_hz)
        | (blower_current < config.blower_current_exit_a)
    )

    eligible = np.zeros(len(inputs), dtype=bool)
    active = False
    enter_count = 0
    enter_start = 0
    exit_count = 0
    for index in range(len(inputs)):
        if not valve_running[index] or not operational_valid[index]:
            active = False
            enter_count = 0
            exit_count = 0
            continue

        if active:
            if exit_candidate[index]:
                exit_count += 1
                if exit_count >= config.exit_confirm_seconds:
                    active = False
                    enter_count = 0
                    exit_count = 0
                continue
            exit_count = 0
            eligible[index] = True
            continue

        if entry_candidate[index]:
            if enter_count == 0:
                enter_start = index
            enter_count += 1
            if enter_count >= config.enter_dwell_seconds:
                active = True
                eligible[enter_start:index + 1] = True
                exit_count = 0
        else:
            enter_count = 0

    return eligible


def finite_run_segments(manifest_path: Path, start_date: str, end_date: str, window_size: int,
                        target_lag: int, effective_config: EffectiveBurningConfig,
                        transition_trim_seconds: int) -> tuple[list[Segment], dict[str, dict[str, int]], list[str]]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    fields = list(manifest.get("fields", []))
    required = list(INPUT_FIELDS + TARGET_FIELDS)
    missing = [field for field in required if field not in fields]
    if missing:
        raise ValueError(f"导出清单缺少必需测点：{missing}")
    if start_date and end_date and end_date < start_date:
        raise ValueError("--end-date 不能早于 --start-date。")
    selected_indexes = [fields.index(field) for field in required]
    input_indexes = list(range(len(INPUT_FIELDS)))
    target_indexes = list(range(len(INPUT_FIELDS), len(required)))
    center_index = fields.index("burner_center_gas_valve_pct")
    outer_index = fields.index("burner_outer_gas_valve_pct")
    segments: list[Segment] = []
    daily: dict[str, dict[str, int]] = {}

    for item in manifest.get("segments", []):
        day = str(item["startTime"][:10])
        if (start_date and day < start_date) or (end_date and day > end_date):
            continue
        row_count = int(item["rowCount"])
        source_path = manifest_path.parent / item["file"]
        raw = np.memmap(source_path, mode="r", dtype=np.float32, shape=(row_count, len(fields)))
        selected = np.asarray(raw[:, selected_indexes], dtype=np.float32).copy()
        center = np.asarray(raw[:, center_index], dtype=np.float32)
        outer = np.asarray(raw[:, outer_index], dtype=np.float32)
        del raw

        valid = np.isfinite(selected).all(axis=1)
        running = (np.isfinite(center) & (center > 1.0)) | (np.isfinite(outer) & (outer > 1.0))
        effective_burning = effective_burning_mask(selected[:, input_indexes], running, effective_config)
        eligible = valid & effective_burning
        daily[day] = {
            "rows": row_count,
            "valid_rows": int(valid.sum()),
            "valve_running_rows": int(running.sum()),
            "effective_burning_rows": int(effective_burning.sum()),
            "eligible_rows": int(eligible.sum()),
            "excluded_by_effective_state_gate_rows": int((valid & running & ~effective_burning).sum()),
            "segments": 0,
            "rows_after_effective_state_filter": 0,
            "windows": 0,
        }
        positions = np.flatnonzero(np.diff(np.r_[False, eligible, False].astype(np.int8)))
        for begin, end in positions.reshape(-1, 2):
            begin = int(begin) + transition_trim_seconds
            end = int(end) - transition_trim_seconds
            if end - begin < window_size + target_lag:
                continue
            block = selected[begin:end]
            segment = Segment(day, Path(item["file"]).name, block[:, input_indexes], block[:, target_indexes])
            if segment_window_count(segment, window_size, target_lag, 1) <= 0:
                continue
            segments.append(segment)
            daily[day]["segments"] += 1
            daily[day]["rows_after_effective_state_filter"] += len(block)
    days = sorted(daily)
    if not segments:
        raise RuntimeError("所选日期内没有满足运行、数据完整和窗口长度要求的训练片段。")
    return segments, daily, days


def segment_window_count(segment: Segment, window_size: int, target_lag: int, stride: int) -> int:
    first_target = window_size - 1 + target_lag
    if first_target >= len(segment.inputs):
        return 0
    return len(range(first_target, len(segment.inputs), stride))


def split_days_by_windows(segments: list[Segment], window_size: int, target_lag: int,
                          stride: int) -> tuple[dict[str, list[str]], dict[str, int]]:
    per_day: dict[str, int] = {}
    for segment in segments:
        per_day[segment.day] = per_day.get(segment.day, 0) + segment_window_count(segment, window_size, target_lag, stride)
    days = sorted(day for day, count in per_day.items() if count > 0)
    if len(days) < 3:
        raise RuntimeError("按日期划分训练/验证/测试至少需要 3 个有效自然日。")
    total = sum(per_day[day] for day in days)
    cumulative = np.cumsum([per_day[day] for day in days], dtype=np.int64)
    min_val_days = 2 if len(days) >= 10 else 1
    min_test_days = 2 if len(days) >= 10 else 1
    best: tuple[int, int, int] | None = None
    for train_end in range(1, len(days) - min_val_days - min_test_days + 1):
        for val_end in range(train_end + min_val_days, len(days) - min_test_days + 1):
            train_count = int(cumulative[train_end - 1])
            train_val_count = int(cumulative[val_end - 1])
            error = abs(train_count - total * 0.7) + abs(train_val_count - total * 0.9)
            candidate = (error, train_end, val_end)
            if best is None or candidate < best:
                best = candidate
    if best is None:
        raise RuntimeError("当前有效日期不足以建立非空的时间顺序切分。")
    _, train_end, val_end = best
    assignments = {"train": days[:train_end], "val": days[train_end:val_end], "test": days[val_end:]}
    counts = {split: sum(per_day[day] for day in assigned) for split, assigned in assignments.items()}
    return assignments, counts


def build_indexes(segments: list[Segment], assigned_days: list[str], window_size: int,
                  target_lag: int, stride: int) -> np.ndarray:
    day_set = set(assigned_days)
    rows: list[tuple[np.ndarray, np.ndarray]] = []
    for segment_id, segment in enumerate(segments):
        if segment.day not in day_set:
            continue
        first_target = window_size - 1 + target_lag
        targets = np.arange(first_target, len(segment.inputs), stride, dtype=np.int32)
        if len(targets):
            rows.append((np.full(len(targets), segment_id, dtype=np.int32), targets))
    if not rows:
        return np.empty((0, 2), dtype=np.int32)
    return np.column_stack((np.concatenate([row[0] for row in rows]), np.concatenate([row[1] for row in rows])))


def cap_indexes(indexes: np.ndarray, maximum: int, seed: int) -> np.ndarray:
    if maximum <= 0 or len(indexes) <= maximum:
        return indexes
    rng = np.random.default_rng(seed)
    return indexes[np.sort(rng.choice(len(indexes), maximum, replace=False))]


def quantile_loss(prediction: torch.Tensor, target: torch.Tensor, lower_q: float, upper_q: float) -> torch.Tensor:
    low = prediction[:, [0, 2]]
    high = prediction[:, [1, 3]]
    low_error = target - low
    high_error = target - high
    low_loss = torch.maximum(lower_q * low_error, (lower_q - 1.0) * low_error)
    high_loss = torch.maximum(upper_q * high_error, (upper_q - 1.0) * high_error)
    return 0.5 * (low_loss.mean() + high_loss.mean())


@torch.no_grad()
def evaluate(model: nn.Module, loader: DataLoader, device: torch.device, lower_q: float,
             upper_q: float, target_mean: np.ndarray, target_std: np.ndarray,
             calibration_margin: np.ndarray | None = None) -> dict[str, object]:
    model.eval()
    loss_sum = 0.0
    count = 0
    pred_chunks: list[np.ndarray] = []
    target_chunks: list[np.ndarray] = []
    for inputs, targets in loader:
        inputs = inputs.to(device=device, dtype=torch.float32)
        targets = targets.to(device=device, dtype=torch.float32)
        predictions = model(inputs)
        batch = len(inputs)
        loss_sum += float(quantile_loss(predictions, targets, lower_q, upper_q)) * batch
        count += batch
        pred_chunks.append(predictions.cpu().numpy())
        target_chunks.append(targets.cpu().numpy())
    predictions = np.concatenate(pred_chunks)
    targets = np.concatenate(target_chunks)
    scale = np.asarray([target_std[0], target_std[0], target_std[1], target_std[1]], dtype=np.float32)
    offset = np.asarray([target_mean[0], target_mean[0], target_mean[1], target_mean[1]], dtype=np.float32)
    actual = targets * np.asarray(target_std, dtype=np.float32)[None, :] + np.asarray(target_mean, dtype=np.float32)[None, :]
    bounds = predictions * scale[None, :] + offset[None, :]
    if calibration_margin is not None:
        bounds += calibration_margin[None, :]
        bounds[:, 0] = np.maximum(bounds[:, 0], 0.0)
        bounds[:, 2] = np.maximum(bounds[:, 2], 0.0)
    metrics: dict[str, object] = {"pinball_loss_standardized": loss_sum / max(count, 1)}
    for index, name in enumerate(TARGET_NAMES):
        low_index, high_index = (0, 1) if index == 0 else (2, 3)
        low = bounds[:, low_index]
        high = bounds[:, high_index]
        values = actual[:, index]
        metrics[name] = {
            "mae_lower_bound": float(np.mean(np.abs(low - values))),
            "mae_upper_bound": float(np.mean(np.abs(high - values))),
            "coverage": float(np.mean((values >= low) & (values <= high))),
            "mean_interval_width": float(np.mean(high - low)),
            "below_rate": float(np.mean(values < low)),
            "above_rate": float(np.mean(values > high)),
        }
    return metrics


def make_historical_ranges(segments: list[Segment], training_days: list[str], output_path: Path,
                           recommendation_nox_max: float | None, recommendation_o2_min: float | None,
                           recommendation_o2_max: float | None) -> dict[str, object]:
    train_set = set(training_days)
    inputs = np.concatenate([segment.inputs for segment in segments if segment.day in train_set], axis=0)
    targets = np.concatenate([segment.targets for segment in segments if segment.day in train_set], axis=0)
    emission_filter_configured = any(value is not None for value in
                                     (recommendation_nox_max, recommendation_o2_min, recommendation_o2_max))
    eligible = np.ones(len(inputs), dtype=bool)
    if recommendation_nox_max is not None:
        eligible &= targets[:, 0] <= recommendation_nox_max
    if recommendation_o2_min is not None:
        eligible &= targets[:, 1] >= recommendation_o2_min
    if recommendation_o2_max is not None:
        eligible &= targets[:, 1] <= recommendation_o2_max

    selected_inputs = inputs[eligible]
    selected_targets = targets[eligible]
    if len(selected_inputs) == 0:
        payload = {
            "status": "no_matching_training_rows",
            "basis": "training rows after transition trimming; emission screening applied only when limits are provided",
            "screening": {"nox_max": recommendation_nox_max, "o2_min": recommendation_o2_min,
                          "o2_max": recommendation_o2_max},
            "groups": [],
        }
        output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        return payload

    gas_index = INPUT_FIELDS.index("natural_gas_inlet_flow_m3_h")
    cut_points = np.quantile(selected_inputs[:, gas_index], [1 / 3, 2 / 3]).tolist()
    group_labels = np.digitize(selected_inputs[:, gas_index], cut_points, right=True)
    groups: list[dict[str, object]] = []
    for group_id, label in enumerate(("low_gas_flow", "mid_gas_flow", "high_gas_flow")):
        mask = group_labels == group_id
        if int(mask.sum()) == 0:
            continue
        parameter_ranges: dict[str, object] = {}
        for field_index, field in enumerate(INPUT_FIELDS):
            q10, q50, q90 = np.quantile(selected_inputs[mask, field_index], [0.1, 0.5, 0.9])
            parameter_ranges[field] = {"p10": float(q10), "median": float(q50), "p90": float(q90)}
        groups.append({
            "group": label,
            "sample_count": int(mask.sum()),
            "gas_flow_min": float(selected_inputs[mask, gas_index].min()),
            "gas_flow_max": float(selected_inputs[mask, gas_index].max()),
            "parameters": parameter_ranges,
            "emissions": {
                "nox_p10": float(np.quantile(selected_targets[mask, 0], 0.1)),
                "nox_median": float(np.median(selected_targets[mask, 0])),
                "nox_p90": float(np.quantile(selected_targets[mask, 0], 0.9)),
                "o2_p10": float(np.quantile(selected_targets[mask, 1], 0.1)),
                "o2_median": float(np.median(selected_targets[mask, 1])),
                "o2_p90": float(np.quantile(selected_targets[mask, 1], 0.9)),
            },
        })
    payload = {
        "status": "emission_screened_historical_reference" if emission_filter_configured else "descriptive_only_limits_not_configured",
        "basis": "training split only; ranges are historical percentiles, not causal control guarantees",
        "grouping": "tertiles of instantaneous natural gas inlet flow",
        "sample_count_total": int(len(selected_inputs)),
        "screening": {"nox_max": recommendation_nox_max, "o2_min": recommendation_o2_min,
                      "o2_max": recommendation_o2_max},
        "groups": groups,
    }
    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return payload


def train_epoch(model: nn.Module, loader: DataLoader, optimizer: torch.optim.Optimizer,
                device: torch.device, lower_q: float, upper_q: float) -> float:
    model.train()
    total_loss = 0.0
    total_count = 0
    for inputs, targets in loader:
        inputs = inputs.to(device=device, dtype=torch.float32)
        targets = targets.to(device=device, dtype=torch.float32)
        optimizer.zero_grad(set_to_none=True)
        prediction = model(inputs)
        loss = quantile_loss(prediction, targets, lower_q, upper_q)
        loss.backward()
        nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        optimizer.step()
        total_loss += float(loss.detach()) * len(inputs)
        total_count += len(inputs)
    return total_loss / max(total_count, 1)


def predict_raw_bounds(model: nn.Module, loader: DataLoader, device: torch.device,
                       target_mean: np.ndarray, target_std: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    model.eval()
    prediction_chunks: list[np.ndarray] = []
    target_chunks: list[np.ndarray] = []
    with torch.no_grad():
        for inputs, targets in loader:
            predictions = model(inputs.to(device=device, dtype=torch.float32)).cpu().numpy()
            actual = targets.numpy() * target_std[None, :] + target_mean[None, :]
            target_chunks.append(actual)
            prediction_chunks.append(predictions * np.asarray([target_std[0], target_std[0], target_std[1], target_std[1]])[None, :]
                                     + np.asarray([target_mean[0], target_mean[0], target_mean[1], target_mean[1]])[None, :])
    return np.concatenate(prediction_chunks), np.concatenate(target_chunks)


def calibrate_interval_margins(predictions: np.ndarray, actual: np.ndarray, nominal_coverage: float) -> np.ndarray:
    margins = np.zeros(4, dtype=np.float32)
    for target_index, (lower_index, upper_index) in enumerate(((0, 1), (2, 3))):
        misses = np.maximum(predictions[:, lower_index] - actual[:, target_index],
                            actual[:, target_index] - predictions[:, upper_index])
        misses = np.maximum(misses, 0.0)
        rank = min(len(misses), max(1, int(math.ceil((len(misses) + 1) * nominal_coverage))))
        margin = float(np.partition(misses, rank - 1)[rank - 1])
        margins[lower_index] = -margin
        margins[upper_index] = margin
    return margins


def export_onnx(model: EmissionBoundsModel, output_dir: Path, input_mean: np.ndarray,
                input_std: np.ndarray, target_mean: np.ndarray, target_std: np.ndarray,
                calibration_margin: np.ndarray,
                window_size: int) -> None:
    wrapper = RawUnitsOnnxWrapper(model.cpu().eval(), input_mean, input_std, target_mean, target_std,
                                  calibration_margin).eval()
    example = torch.zeros((1, window_size, len(INPUT_FIELDS)), dtype=torch.float32)
    torch.onnx.export(
        wrapper,
        (example,),
        output_dir / "best_model.onnx",
        input_names=["input_sequence"],
        output_names=["emission_bounds"],
        dynamic_axes={"input_sequence": {0: "batch"}, "emission_bounds": {0: "batch"}},
        opset_version=17,
        dynamo=False,
    )


def main() -> None:
    args = parse_args()
    if args.window_size < 2 or args.target_lag_seconds < 0 or args.stride <= 0:
        raise ValueError("window-size 至少为 2，target-lag 不得为负数，stride 必须大于 0。")
    if not (0.0 < args.lower_quantile < 0.5 < args.upper_quantile < 1.0):
        raise ValueError("分位数必须满足 0 < lower < 0.5 < upper < 1。")
    if args.max_windows_per_split < 0 or args.epochs < 1 or args.batch_size < 1:
        raise ValueError("训练轮数、批大小和窗口上限参数无效。")
    effective_config = EffectiveBurningConfig(
        gas_flow_enter_m3_h=args.effective_gas_flow_enter_m3_h,
        gas_flow_exit_m3_h=args.effective_gas_flow_exit_m3_h,
        blower_frequency_enter_hz=args.effective_blower_frequency_enter_hz,
        blower_frequency_exit_hz=args.effective_blower_frequency_exit_hz,
        blower_current_enter_a=args.effective_blower_current_enter_a,
        blower_current_exit_a=args.effective_blower_current_exit_a,
        enter_dwell_seconds=args.effective_enter_dwell_seconds,
        exit_confirm_seconds=args.effective_exit_confirm_seconds,
    )
    thresholds = (
        effective_config.gas_flow_enter_m3_h, effective_config.gas_flow_exit_m3_h,
        effective_config.blower_frequency_enter_hz, effective_config.blower_frequency_exit_hz,
        effective_config.blower_current_enter_a, effective_config.blower_current_exit_a,
    )
    if not all(math.isfinite(value) for value in thresholds):
        raise ValueError("有效燃烧阈值必须为有限数值。")
    if any(value < 0 for value in thresholds):
        raise ValueError("有效燃烧流量、频率和电流阈值不得为负数。")
    if (effective_config.gas_flow_enter_m3_h <= effective_config.gas_flow_exit_m3_h
            or effective_config.blower_frequency_enter_hz <= effective_config.blower_frequency_exit_hz
            or effective_config.blower_current_enter_a <= effective_config.blower_current_exit_a):
        raise ValueError("有效燃烧进入阈值必须分别高于退出阈值。")
    if effective_config.enter_dwell_seconds < 1 or effective_config.exit_confirm_seconds < 1:
        raise ValueError("有效燃烧进入稳定秒数和退出确认秒数必须大于 0。")
    if args.transition_trim_seconds < 0:
        raise ValueError("transition-trim-seconds 不得为负数。")

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(args.seed)

    manifest_path = Path(args.export_manifest)
    if not manifest_path.exists():
        raise FileNotFoundError(f"导出清单不存在：{manifest_path}")
    output_dir = Path(args.output_dir) if args.output_dir else ROOT / "EarlyWarning" / "artifacts" / datetime.now().strftime("emission_bounds_%Y%m%d_%H%M%S")
    output_dir.mkdir(parents=True, exist_ok=True)

    segments, daily, _ = finite_run_segments(
        manifest_path, args.start_date, args.end_date, args.window_size, args.target_lag_seconds,
        effective_config, args.transition_trim_seconds,
    )
    assignments, window_counts = split_days_by_windows(segments, args.window_size, args.target_lag_seconds, args.stride)
    for segment in segments:
        daily[segment.day]["windows"] += segment_window_count(
            segment, args.window_size, args.target_lag_seconds, args.stride
        )
    index_sets = {
        split: cap_indexes(
            build_indexes(segments, days, args.window_size, args.target_lag_seconds, args.stride),
            args.max_windows_per_split,
            args.seed + index,
        )
        for index, (split, days) in enumerate(assignments.items())
    }
    for day, count in daily.items():
        split_name = next((key for key, values in assignments.items() if day in values), None)
        if split_name is not None:
            count["split"] = split_name

    print(f"[data] selected effective-burning segments={len(segments)}; features={len(INPUT_FIELDS)}; labels={len(TARGET_FIELDS)}")
    print("[state] entry: valve running + gas flow/fan frequency/fan current above entry thresholds "
          f"for {effective_config.enter_dwell_seconds}s; exit candidates are immediately excluded, "
          f"state resets after {effective_config.exit_confirm_seconds}s")
    total_windows = sum(window_counts.values())
    selected_window_counts = {split: len(indexes) for split, indexes in index_sets.items()}
    for split, days in assignments.items():
        ratio = window_counts[split] / total_windows
        print(f"[split] {split}: {days[0]}..{days[-1]} ({len(days)} days), "
              f"eligible_windows={window_counts[split]:,}, selected_windows={selected_window_counts[split]:,} ({ratio:.1%})")

    historical_ranges = make_historical_ranges(
        segments, assignments["train"], output_dir / "historical_input_ranges.json",
        args.recommendation_nox_max, args.recommendation_o2_min, args.recommendation_o2_max,
    )
    if historical_ranges["status"] == "descriptive_only_limits_not_configured":
        print("[ranges] descriptive historical ranges written; set emission limits to mark them as screened references")

    data_report = {
        "format": "litedb_export_f32",
        "start_date": args.start_date or None,
        "end_date": args.end_date or None,
        "window_size_seconds": args.window_size,
        "target_lag_seconds": args.target_lag_seconds,
        "target_alignment": "For an emission measurement at t, use the 60-second process-input window ending at t - target_lag_seconds.",
        "training_stride_seconds": args.stride,
        "inputs": list(INPUT_FIELDS),
        "targets": list(TARGET_FIELDS),
        "daily_partitions": daily,
        "split_days": assignments,
        "eligible_window_counts": window_counts,
        "selected_window_counts": selected_window_counts,
        "max_windows_per_split": args.max_windows_per_split,
        "window_sampling_policy": "none; all eligible windows used" if args.max_windows_per_split == 0 else "deterministic random cap without replacement for splits exceeding the limit",
        "split_policy": "chronological whole-day split approximating 70:20:10 by eligible windows",
        "running_rule": "center gas valve > 1% or outer gas valve > 1%; invalid valve feedback is treated as closed",
        "effective_burning_rule": effective_config.to_dict(),
        "transition_trim_seconds": args.transition_trim_seconds,
        "window_eligibility_rule": "all rows in the input window and the paired target row must be valid and within the effective-burning segment",
        "missing_value_policy": "strictly discard rows with any non-finite model input or target; no imputation",
    }
    (output_dir / "data_summary.json").write_text(json.dumps(data_report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.prepare_only:
        print(f"[prepare] complete: {output_dir}")
        return

    train_rows = [segment for segment in segments if segment.day in set(assignments["train"])]
    input_train = np.concatenate([segment.inputs for segment in train_rows], axis=0)
    target_train = np.concatenate([segment.targets for segment in train_rows], axis=0)
    input_mean = input_train.mean(axis=0).astype(np.float32)
    input_raw_std = input_train.std(axis=0).astype(np.float32)
    input_std = np.maximum(input_raw_std, np.maximum(0.01, np.abs(input_mean) * 0.01)).astype(np.float32)
    target_mean = target_train.mean(axis=0).astype(np.float32)
    target_raw_std = target_train.std(axis=0).astype(np.float32)
    target_std = np.maximum(target_raw_std, np.maximum(0.01, np.abs(target_mean) * 0.01)).astype(np.float32)

    normalization = {
        "input_fields": list(INPUT_FIELDS), "input_mean": input_mean.tolist(),
        "input_raw_std": input_raw_std.tolist(), "input_std": input_std.tolist(),
        "target_fields": list(TARGET_FIELDS), "target_mean": target_mean.tolist(),
        "target_raw_std": target_raw_std.tolist(), "target_std": target_std.tolist(),
    }
    (output_dir / "normalization.json").write_text(json.dumps(normalization, ensure_ascii=False, indent=2), encoding="utf-8")

    datasets = {
        split: WindowDataset(segments, index_sets[split], args.window_size, args.target_lag_seconds,
                             input_mean, input_std, target_mean, target_std)
        for split in ("train", "val", "test")
    }
    loaders = {
        split: DataLoader(dataset, batch_size=args.batch_size, shuffle=split == "train", num_workers=0)
        for split, dataset in datasets.items()
    }
    if any(len(dataset) == 0 for dataset in datasets.values()):
        raise RuntimeError("训练、验证或测试集合为空；请扩大日期范围或检查数据质量。")

    device_name = "cuda" if args.device == "auto" and torch.cuda.is_available() else "cpu" if args.device == "auto" else args.device
    device = torch.device(device_name)
    model = EmissionBoundsModel().to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.learning_rate, weight_decay=args.weight_decay)
    best_loss = math.inf
    best_epoch = 0
    stale = 0
    history: list[dict[str, float]] = []
    for epoch in range(1, args.epochs + 1):
        train_loss = train_epoch(model, loaders["train"], optimizer, device, args.lower_quantile, args.upper_quantile)
        validation = evaluate(model, loaders["val"], device, args.lower_quantile, args.upper_quantile, target_mean, target_std)
        val_loss = float(validation["pinball_loss_standardized"])
        history.append({"epoch": epoch, "train_pinball_loss": train_loss, "val_pinball_loss": val_loss})
        print(f"[epoch {epoch:03d}] train_pinball={train_loss:.6f} val_pinball={val_loss:.6f} "
              f"nox_coverage={validation['nox']['coverage']:.4f} o2_coverage={validation['o2']['coverage']:.4f}")
        if val_loss < best_loss:
            best_loss = val_loss
            best_epoch = epoch
            stale = 0
            torch.save(model.state_dict(), output_dir / "best_model.pt")
        else:
            stale += 1
            if stale >= args.early_stopping_patience:
                break

    model.load_state_dict(torch.load(output_dir / "best_model.pt", map_location=device, weights_only=True))
    val_bounds, val_actual = predict_raw_bounds(model, loaders["val"], device, target_mean, target_std)
    calibration_margin = calibrate_interval_margins(
        val_bounds, val_actual, args.upper_quantile - args.lower_quantile
    )
    calibration = {
        "nominal_coverage": args.upper_quantile - args.lower_quantile,
        "method": "symmetric empirical interval-miss calibration on chronological validation split",
        "margins_raw_units": {
            "nox_lower_adjustment": float(calibration_margin[0]),
            "nox_upper_adjustment": float(calibration_margin[1]),
            "o2_lower_adjustment": float(calibration_margin[2]),
            "o2_upper_adjustment": float(calibration_margin[3]),
        },
        "note": "Time-series dependence means empirical coverage must still be monitored by operating day.",
    }
    (output_dir / "calibration.json").write_text(json.dumps(calibration, ensure_ascii=False, indent=2), encoding="utf-8")
    test_metrics = evaluate(model, loaders["test"], device, args.lower_quantile, args.upper_quantile,
                            target_mean, target_std, calibration_margin)
    export_onnx(model, output_dir, input_mean, input_std, target_mean, target_std,
                calibration_margin, args.window_size)
    (output_dir / "manifest.json").write_text(json.dumps({
        "model_version": "conditional_emission_bounds_v1",
        "task": "current_condition_nox_o2_normal_bounds_anomaly_analysis",
        "inference_is_future_forecast": False,
        "inputs": list(INPUT_FIELDS),
        "targets": list(TARGET_FIELDS),
        "output_order": ["nox_lower", "nox_upper", "o2_lower", "o2_upper"],
        "input_shape": [None, args.window_size, len(INPUT_FIELDS)],
        "output_shape": [None, 4],
        "target_lag_seconds": args.target_lag_seconds,
        "target_alignment": "For a label at t, the input window ends at t - target_lag_seconds; this is contemporaneous boundary estimation after lag alignment, not future forecasting.",
        "training_stride_seconds": args.stride,
        "effective_burning_rule": effective_config.to_dict(),
        "transition_trim_seconds": args.transition_trim_seconds,
        "max_windows_per_split": args.max_windows_per_split,
        "selected_window_counts": selected_window_counts,
        "window_sampling_policy": "none; all eligible windows used" if args.max_windows_per_split == 0 else "deterministic random cap without replacement for splits exceeding the limit",
        "lower_quantile": args.lower_quantile,
        "upper_quantile": args.upper_quantile,
        "split_days": assignments,
        "eligible_window_counts": window_counts,
        "normalization_file": "normalization.json",
        "calibration_file": "calibration.json",
        "historical_ranges_file": "historical_input_ranges.json",
        "created_at": datetime.now().astimezone().isoformat(),
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    (output_dir / "training_summary.json").write_text(json.dumps({
        "best_epoch": best_epoch,
        "best_val_pinball_loss_standardized": best_loss,
        "history": history,
        "test_metrics": test_metrics,
        "device": str(device),
        "training_window_counts": {split: len(indexes) for split, indexes in index_sets.items()},
        "max_windows_per_split": args.max_windows_per_split,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[train] complete: {output_dir}")


if __name__ == "__main__":
    main()
