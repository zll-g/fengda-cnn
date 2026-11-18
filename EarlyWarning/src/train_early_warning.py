"""Train the condition-aware masked denoising LSTM-AE early-warning model."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import sys
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable

import numpy as np
import pandas as pd
import torch
from torch import nn
from torch.utils.data import DataLoader, Dataset


ROOT = Path(__file__).resolve().parents[2]
ROOT_SRC = ROOT / "src"
if str(ROOT_SRC) not in sys.path:
    sys.path.insert(0, str(ROOT_SRC))

from fengda_cnn.data.historian_csv import parse_historian_csv  # noqa: E402
from fengda_cnn.data.point_registry import POINT_SPECS  # noqa: E402
from fengda_cnn.data.standardization import _rename_columns  # noqa: E402


SPLITS = ("train", "val", "test")
WINDOW_SIZE = 60
MASK_STEPS = 10
EXPECTED_FORMAL_POINT_COUNT = 25
# The LiteDB exporter freezes this exact 25-point contract in dataset_manifest.json.
EXPORT_FEATURE_COLUMNS = (
    "burner_gas_leak_pressure",
    "burner_gas_inlet_pressure",
    "burner_air_damper_pct",
    "burner_outer_gas_valve_pct",
    "burner_center_gas_valve_pct",
    "fgr_valve_pct",
    "stack_nox_mg_m3",
    "stack_so2_mg_m3",
    "stack_o2_pct",
    "stack_dust_mg_m3",
    "stack_no2_mg_m3",
    "stack_gas_flow_m3_h",
    "stack_humidity_pct",
    "stack_pressure_kpa",
    "stack_gas_temperature_c",
    "furnace_exit_pressure_kpa",
    "furnace_exit_temperature_c",
    "boiler_load",
    "superheated_steam_pressure_mpa",
    "desuperheater_outlet_temperature_c",
    "fgr_fan_frequency_hz",
    "natural_gas_inlet_flow_m3_h",
    "ignition_solenoid_active",
    "blower_current_a",
    "blower_frequency_hz",
)
TARGET_FIELDS = (
    "stack_nox_mg_m3",
    "desuperheater_outlet_temperature_c",
    "superheated_steam_pressure_mpa",
)
GMM_FIELDS = (
    "natural_gas_inlet_flow_m3_h",
    "burner_center_gas_valve_pct",
    "burner_outer_gas_valve_pct",
    "burner_air_damper_pct",
    "blower_frequency_hz",
    "fgr_valve_pct",
    "fgr_fan_frequency_hz",
    "boiler_load",
)


@dataclass(frozen=True)
class TrainConfig:
    window_size: int = WINDOW_SIZE
    stride: int = 1
    epochs: int = 80
    batch_size: int = 256
    learning_rate: float = 5e-4
    weight_decay: float = 1e-4
    early_stopping_patience: int = 10
    mask_probability: float = 0.5
    max_mask_span: int = MASK_STEPS
    seed: int = 42
    gmm_max_iter: int = 100
    gmm_samples: int = 50000


@dataclass
class Segment:
    source_file: str
    values: np.ndarray
    timestamps: np.ndarray


class DiagonalGMM:
    """Small diagonal-covariance GMM implementation to avoid a sklearn dependency."""

    def __init__(self, components: int, max_iter: int, seed: int) -> None:
        self.components = components
        self.max_iter = max_iter
        self.seed = seed
        self.weights: np.ndarray | None = None
        self.means: np.ndarray | None = None
        self.variances: np.ndarray | None = None
        self.log_likelihood: float = float("-inf")

    def _log_probability(self, values: np.ndarray) -> np.ndarray:
        assert self.weights is not None and self.means is not None and self.variances is not None
        diff = values[:, None, :] - self.means[None, :, :]
        log_det = np.log(self.variances).sum(axis=1)
        mahalanobis = (diff * diff / self.variances[None, :, :]).sum(axis=2)
        return np.log(self.weights)[None, :] - 0.5 * (
            values.shape[1] * math.log(2.0 * math.pi) + log_det[None, :] + mahalanobis
        )

    def fit(self, values: np.ndarray) -> "DiagonalGMM":
        if values.ndim != 2 or len(values) < self.components:
            raise ValueError("GMM training data is smaller than the requested component count.")

        rng = np.random.default_rng(self.seed)
        sample_indexes = rng.choice(len(values), size=self.components, replace=False)
        self.means = values[sample_indexes].copy()
        global_variance = np.var(values, axis=0) + 1e-6
        self.variances = np.repeat(global_variance.reshape(1, -1), self.components, axis=0)
        self.weights = np.full(self.components, 1.0 / self.components, dtype=np.float64)

        last_log_likelihood = float("-inf")
        for _ in range(self.max_iter):
            log_probability = self._log_probability(values)
            max_log = log_probability.max(axis=1, keepdims=True)
            stable_probability = np.exp(log_probability - max_log)
            responsibilities = stable_probability / stable_probability.sum(axis=1, keepdims=True)
            counts = responsibilities.sum(axis=0) + 1e-8

            self.weights = counts / len(values)
            self.means = (responsibilities.T @ values) / counts[:, None]
            diff = values[:, None, :] - self.means[None, :, :]
            self.variances = (responsibilities[:, :, None] * diff * diff).sum(axis=0) / counts[:, None]
            self.variances = np.maximum(self.variances, 1e-6)

            log_probability = self._log_probability(values)
            current_log_likelihood = float(
                (np.log(np.exp(log_probability - log_probability.max(axis=1, keepdims=True)).sum(axis=1))
                 + log_probability.max(axis=1)).sum()
            )
            if abs(current_log_likelihood - last_log_likelihood) < 1e-4 * (1.0 + abs(last_log_likelihood)):
                self.log_likelihood = current_log_likelihood
                break
            last_log_likelihood = current_log_likelihood
            self.log_likelihood = current_log_likelihood
        return self

    def predict_proba(self, values: np.ndarray) -> np.ndarray:
        log_probability = self._log_probability(values)
        max_log = log_probability.max(axis=1, keepdims=True)
        probability = np.exp(log_probability - max_log)
        return probability / probability.sum(axis=1, keepdims=True)

    def bic(self, values: np.ndarray) -> float:
        feature_count = values.shape[1]
        parameter_count = self.components * (2 * feature_count + 1) - 1
        return -2.0 * self.log_likelihood + parameter_count * math.log(len(values))

    def to_dict(self) -> dict[str, object]:
        assert self.weights is not None and self.means is not None and self.variances is not None
        return {
            "type": "diagonal_gmm",
            "components": self.components,
            "weights": self.weights.tolist(),
            "means": self.means.tolist(),
            "variances": self.variances.tolist(),
            "log_likelihood": self.log_likelihood,
        }


class AdditiveAttention(nn.Module):
    def __init__(self, hidden_size: int) -> None:
        super().__init__()
        self.projection = nn.Linear(hidden_size, hidden_size)
        self.score = nn.Linear(hidden_size, 1, bias=False)

    def forward(self, sequence: torch.Tensor) -> torch.Tensor:
        weights = torch.softmax(self.score(torch.tanh(self.projection(sequence))).squeeze(-1), dim=1)
        return torch.sum(sequence * weights.unsqueeze(-1), dim=1)


class ConditionMaskedLSTMAE(nn.Module):
    """Condition-aware denoising sequence autoencoder with three mask channels."""

    def __init__(self, process_features: int, condition_count: int, window_size: int) -> None:
        super().__init__()
        self.process_features = process_features
        self.window_size = window_size
        self.input_projection = nn.Sequential(
            nn.Linear(process_features + len(TARGET_FIELDS), 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(0.10),
        )
        self.condition_embedding = nn.Embedding(condition_count, 8)
        self.encoder_1 = nn.LSTM(72, 96, batch_first=True)
        self.encoder_2 = nn.LSTM(96, 64, batch_first=True)
        self.dropout = nn.Dropout(0.10)
        self.attention = AdditiveAttention(64)
        self.latent = nn.Sequential(nn.Linear(64, 32), nn.LayerNorm(32), nn.Tanh())
        self.time_embedding = nn.Embedding(window_size, 8)
        self.decoder_1 = nn.LSTM(48, 96, batch_first=True)
        self.decoder_2 = nn.LSTM(96, 64, batch_first=True)
        self.output_projection = nn.Linear(64, process_features)

    def forward(self, inputs: torch.Tensor, condition_ids: torch.Tensor) -> torch.Tensor:
        projected = self.input_projection(inputs)
        condition = self.condition_embedding(condition_ids).unsqueeze(1).expand(-1, inputs.size(1), -1)
        encoded, _ = self.encoder_1(torch.cat([projected, condition], dim=-1))
        encoded = self.dropout(encoded)
        encoded, _ = self.encoder_2(encoded)
        encoded = self.dropout(encoded)
        latent = self.latent(self.attention(encoded))
        positions = torch.arange(inputs.size(1), device=inputs.device)
        time = self.time_embedding(positions).unsqueeze(0).expand(inputs.size(0), -1, -1)
        repeated_latent = latent.unsqueeze(1).expand(-1, inputs.size(1), -1)
        decoded, _ = self.decoder_1(torch.cat([repeated_latent, time, condition], dim=-1))
        decoded = self.dropout(decoded)
        decoded, _ = self.decoder_2(decoded)
        decoded = self.dropout(decoded)
        return self.output_projection(decoded)


class WindowDataset(Dataset[tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]]):
    def __init__(
        self,
        segments: list[Segment],
        indexes: np.ndarray,
        condition_ids: np.ndarray,
        feature_columns: list[str],
        mask_probability: float,
        max_mask_span: int,
        training: bool,
        seed: int,
    ) -> None:
        self.segments = segments
        self.indexes = indexes
        self.condition_ids = condition_ids
        self.feature_columns = feature_columns
        self.mask_probability = mask_probability
        self.max_mask_span = max_mask_span
        self.training = training
        self.rng = np.random.default_rng(seed)
        self.target_indexes = [feature_columns.index(name) for name in TARGET_FIELDS]

    def __len__(self) -> int:
        return len(self.indexes)

    def __getitem__(self, item: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
        segment_id, start = self.indexes[item]
        target = self.segments[int(segment_id)].values[int(start) : int(start) + WINDOW_SIZE]
        masked = target.copy()
        mask = np.zeros((WINDOW_SIZE, len(TARGET_FIELDS)), dtype=np.float32)

        if self.training and self.rng.random() < self.mask_probability:
            target_slot = int(self.rng.integers(len(TARGET_FIELDS)))
            span = int(self.rng.integers(1, self.max_mask_span + 1))
            mask_start = int(self.rng.integers(0, WINDOW_SIZE - span + 1))
            feature_index = self.target_indexes[target_slot]
            masked[mask_start : mask_start + span, feature_index] = 0.0
            mask[mask_start : mask_start + span, target_slot] = 1.0

        inputs = np.concatenate([masked, mask], axis=1)
        return (
            torch.from_numpy(inputs),
            torch.from_numpy(target.copy()),
            torch.from_numpy(mask),
            torch.tensor(self.condition_ids[item], dtype=torch.long),
        )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-dir", default=str(ROOT / "data"))
    parser.add_argument(
        "--export-manifest",
        default="",
        help="Path to db_data/export/dataset_manifest.json. Enables LiteDB-export training mode.",
    )
    parser.add_argument("--start-date", default="", help="Inclusive YYYY-MM-DD filter for exported daily partitions.")
    parser.add_argument("--end-date", default="", help="Inclusive YYYY-MM-DD filter for exported daily partitions.")
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--expected-input-count", type=int, default=EXPECTED_FORMAL_POINT_COUNT)
    parser.add_argument("--allow-reduced-schema", action="store_true")
    parser.add_argument("--window-size", type=int, default=WINDOW_SIZE)
    parser.add_argument("--stride", type=int, default=1)
    parser.add_argument("--epochs", type=int, default=80)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--learning-rate", type=float, default=5e-4)
    parser.add_argument("--weight-decay", type=float, default=1e-4)
    parser.add_argument("--early-stopping-patience", type=int, default=10)
    parser.add_argument("--mask-probability", type=float, default=0.5)
    parser.add_argument("--max-mask-span", type=int, default=MASK_STEPS)
    parser.add_argument("--gmm-max-iter", type=int, default=100)
    parser.add_argument("--gmm-samples", type=int, default=50000)
    parser.add_argument(
        "--max-windows-per-split",
        type=int,
        default=0,
        help="Optional deterministic cap for development smoke tests; 0 uses every eligible window.",
    )
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--skip-onnx-export", action="store_true")
    return parser.parse_args()


def set_seed(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def finite_number(value: object) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return float("nan")
    return number if math.isfinite(number) else float("nan")


def strict_resample(path: Path, feature_columns: list[str]) -> tuple[pd.DataFrame, dict[str, object]]:
    export = parse_historian_csv(path)
    base = _rename_columns(export.data).sort_values("timestamp").set_index("timestamp")
    agg = {spec.field_name: spec.agg for spec in POINT_SPECS}
    frame = base.resample("1s").agg(agg)
    frame = frame[feature_columns]
    frame["source_file"] = path.name
    frame["valid_input"] = np.isfinite(frame[feature_columns].to_numpy(dtype=float)).all(axis=1)
    center = frame["burner_center_gas_valve_pct"].map(finite_number)
    outer = frame["burner_outer_gas_valve_pct"].map(finite_number)
    frame["running"] = (center > 1.0) | (outer > 1.0)
    return frame.reset_index(), {
        "source_file": path.name,
        "rows": int(len(frame)),
        "valid_rows": int(frame["valid_input"].sum()),
        "running_rows": int(frame["running"].sum()),
    }


def contiguous_segments(frame: pd.DataFrame, feature_columns: list[str], source_file: str) -> list[Segment]:
    eligible = frame["valid_input"].to_numpy(dtype=bool) & frame["running"].to_numpy(dtype=bool)
    timestamps = pd.to_datetime(frame["timestamp"]).to_numpy()
    values = frame[feature_columns].to_numpy(dtype=np.float32)
    result: list[Segment] = []
    start: int | None = None

    def append_segment(begin: int, end: int) -> None:
        # Remove 60 seconds at both edges to exclude start/stop transition windows.
        inner_begin = begin + WINDOW_SIZE
        inner_end = end - WINDOW_SIZE
        if inner_end - inner_begin < WINDOW_SIZE:
            return
        result.append(Segment(source_file, values[inner_begin:inner_end], timestamps[inner_begin:inner_end]))

    for index, is_eligible in enumerate(eligible):
        contiguous = index == 0 or (pd.Timestamp(timestamps[index]) - pd.Timestamp(timestamps[index - 1])).total_seconds() == 1.0
        if is_eligible and (start is None or not contiguous):
            if start is not None:
                append_segment(start, index)
            start = index
        elif not is_eligible and start is not None:
            append_segment(start, index)
            start = None
    if start is not None:
        append_segment(start, len(frame))
    return result


def window_count(mask: np.ndarray, window_size: int) -> int:
    """Count windows whose every second satisfies a boolean eligibility mask."""

    count = 0
    begin: int | None = None
    for index, eligible in enumerate(mask):
        if eligible and begin is None:
            begin = index
        elif not eligible and begin is not None:
            count += max(0, index - begin - window_size + 1)
            begin = None
    if begin is not None:
        count += max(0, len(mask) - begin - window_size + 1)
    return count


def contiguous_export_segments(
    values: np.ndarray, timestamps: np.ndarray, source_file: str, eligible: np.ndarray
) -> list[Segment]:
    """Create finite running segments; no NaN-containing 60-second window can be emitted."""

    result: list[Segment] = []
    begin: int | None = None

    def append_segment(start: int, end: int) -> None:
        # Preserve the approved 60-second start/stop transition exclusion rule.
        inner_start = start + WINDOW_SIZE
        inner_end = end - WINDOW_SIZE
        if inner_end - inner_start < WINDOW_SIZE:
            return
        result.append(Segment(source_file, values[inner_start:inner_end].copy(), timestamps[inner_start:inner_end].copy()))

    for index, is_eligible in enumerate(eligible):
        if is_eligible and begin is None:
            begin = index
        elif not is_eligible and begin is not None:
            append_segment(begin, index)
            begin = None
    if begin is not None:
        append_segment(begin, len(eligible))
    return result


def load_export_segments(
    manifest_path: Path, start_date: str, end_date: str
) -> tuple[list[str], list[Segment], list[dict[str, object]]]:
    """Load exported daily F32 partitions without interpolating any missing process value."""

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    feature_columns = list(manifest.get("fields", []))
    if feature_columns != list(EXPORT_FEATURE_COLUMNS):
        raise ValueError(
            "导出字段契约不匹配；训练要求固定 25 点顺序。"
            f" expected={list(EXPORT_FEATURE_COLUMNS)}, actual={feature_columns}"
        )
    try:
        start = np.datetime64(start_date, "D") if start_date else None
        end = np.datetime64(end_date, "D") if end_date else None
    except ValueError as exception:
        raise ValueError("--start-date 和 --end-date 必须为 YYYY-MM-DD。") from exception
    if start is not None and end is not None and end < start:
        raise ValueError("--end-date 不能早于 --start-date。")

    center_index = feature_columns.index("burner_center_gas_valve_pct")
    outer_index = feature_columns.index("burner_outer_gas_valve_pct")
    segments: list[Segment] = []
    summary: list[dict[str, object]] = []
    for item in manifest.get("segments", []):
        segment_start = np.datetime64(item["startTime"], "s")
        segment_day = segment_start.astype("datetime64[D]")
        if (start is not None and segment_day < start) or (end is not None and segment_day > end):
            continue
        row_count = int(item["rowCount"])
        path = manifest_path.parent / item["file"]
        if not path.exists():
            raise FileNotFoundError(f"导出分区不存在: {path}")
        raw = np.memmap(path, mode="r", dtype=np.float32, shape=(row_count, len(feature_columns)))
        values = np.asarray(raw, dtype=np.float32).copy()
        del raw
        timestamps = segment_start + np.arange(row_count).astype("timedelta64[s]")
        all_finite = np.isfinite(values).all(axis=1)
        center = values[:, center_index]
        outer = values[:, outer_index]
        # Missing/non-numeric valve feedback is treated as not opened, per the approved stop rule.
        running = (np.isfinite(center) & (center > 1.0)) | (np.isfinite(outer) & (outer > 1.0))
        complete_running = all_finite & running
        candidate_windows = window_count(running, WINDOW_SIZE)
        complete_windows = window_count(complete_running, WINDOW_SIZE)
        current = contiguous_export_segments(values, timestamps, path.name, complete_running)
        summary.append(
            {
                "source_file": path.name,
                "day": str(segment_day),
                "rows": row_count,
                "finite_rows": int(all_finite.sum()),
                "running_rows": int(running.sum()),
                "candidate_running_windows": candidate_windows,
                "discarded_empty_windows": candidate_windows - complete_windows,
                "eligible_segments": len(current),
                "eligible_rows_after_transition_trim": int(sum(len(segment.values) for segment in current)),
                "eligible_windows_after_transition_trim": int(sum(max(0, len(segment.values) - WINDOW_SIZE + 1) for segment in current)),
            }
        )
        segments.extend(current)
    if not segments:
        raise RuntimeError("选定导出分区中没有连续、运行且 25 点完整的 60 秒训练数据。")
    return feature_columns, segments, summary


def split_export_segments(segments: list[Segment], stride: int = 1) -> tuple[dict[str, list[Segment]], dict[str, list[str]]]:
    """Chronologically split whole days near a 7:2:1 window ratio, without leakage."""

    if stride <= 0:
        raise ValueError("stride must be positive")
    day_segments: dict[str, list[Segment]] = {}
    for segment in segments:
        day = str(segment.timestamps[0].astype("datetime64[D]"))
        day_segments.setdefault(day, []).append(segment)
    days = sorted(day_segments)
    if len(days) < 3:
        raise RuntimeError("导出训练至少需要 3 个有效自然日，才能按时间划分训练、验证、测试集。")

    day_windows = {
        day: sum(max(0, (len(segment.values) - WINDOW_SIZE) // stride + 1) for segment in day_segments[day])
        for day in days
    }
    total_windows = sum(day_windows.values())
    if total_windows <= 0:
        raise RuntimeError("没有可用于训练、验证和测试的有效窗口。")

    cumulative = np.cumsum([day_windows[day] for day in days], dtype=np.int64)
    # With enough dates, require at least two whole days in both holdout splits.
    minimum_val_days = 2 if len(days) >= 10 else 1
    minimum_test_days = 2 if len(days) >= 20 else 1
    best: tuple[float, int, int] | None = None
    for train_end in range(1, len(days) - minimum_val_days - minimum_test_days + 1):
        for val_end in range(train_end + minimum_val_days, len(days) - minimum_test_days + 1):
            train_windows = int(cumulative[train_end - 1])
            train_val_windows = int(cumulative[val_end - 1])
            error = abs(train_windows - total_windows * 0.7) + abs(train_val_windows - total_windows * 0.9)
            candidate = (error, train_end, val_end)
            if best is None or candidate < best:
                best = candidate
    if best is None:
        raise RuntimeError("无法在至少 3 个有效自然日上构建非空的训练、验证和测试集。")

    _, train_end, val_end = best
    split_days = {
        "train": days[:train_end],
        "val": days[train_end:val_end],
        "test": days[val_end:],
    }
    split_segments = {
        split: [segment for day in assigned_days for segment in day_segments[day]]
        for split, assigned_days in split_days.items()
    }
    window_counts = {
        split: sum(day_windows[day] for day in assigned_days)
        for split, assigned_days in split_days.items()
    }
    actual_ratio = {split: count / total_windows for split, count in window_counts.items()}
    print(
        "[split] chronological 7:2:1 by eligible windows "
        f"(actual train/val/test={actual_ratio['train']:.1%}/{actual_ratio['val']:.1%}/{actual_ratio['test']:.1%})"
    )
    for split, assigned_days in split_days.items():
        print(
            f"[split] {split}: days={assigned_days[0]}..{assigned_days[-1]} "
            f"({len(assigned_days)} days), windows={window_counts[split]:,}"
        )
    return split_segments, split_days


def load_segments(data_dir: Path, split: str, feature_columns: list[str]) -> tuple[list[Segment], list[dict[str, object]]]:
    segments: list[Segment] = []
    summary: list[dict[str, object]] = []
    for path in sorted((data_dir / split).glob("*.csv")):
        frame, item = strict_resample(path, feature_columns)
        current = contiguous_segments(frame, feature_columns, path.name)
        item["eligible_segments"] = len(current)
        item["eligible_rows"] = int(sum(len(segment.values) for segment in current))
        summary.append(item)
        segments.extend(current)
    return segments, summary


def build_indexes(segments: list[Segment], window_size: int, stride: int) -> np.ndarray:
    indexes: list[tuple[int, int]] = []
    for segment_id, segment in enumerate(segments):
        if not np.isfinite(segment.values).all():
            raise ValueError(f"训练段包含空值，拒绝建窗: {segment.source_file}")
        for start in range(0, len(segment.values) - window_size + 1, stride):
            indexes.append((segment_id, start))
    return np.asarray(indexes, dtype=np.int32)


def cap_windows(indexes: np.ndarray, condition_ids: np.ndarray, maximum: int, seed: int) -> tuple[np.ndarray, np.ndarray]:
    """Keep a deterministic subset for smoke tests without changing formal defaults."""

    if maximum <= 0 or len(indexes) <= maximum:
        return indexes, condition_ids
    rng = np.random.default_rng(seed)
    selected = np.sort(rng.choice(len(indexes), size=maximum, replace=False))
    return indexes[selected], condition_ids[selected]


def calculate_condition_features(
    segments: list[Segment], indexes: np.ndarray, feature_columns: list[str]
) -> np.ndarray:
    selected = [feature_columns.index(name) for name in GMM_FIELDS if name in feature_columns]
    if len(selected) != len(GMM_FIELDS):
        missing = sorted(set(GMM_FIELDS) - set(feature_columns))
        raise ValueError(f"GMM feature columns are missing: {missing}")
    features = np.empty((len(indexes), len(selected) * 3), dtype=np.float64)
    for row, (segment_id, start) in enumerate(indexes):
        window = segments[int(segment_id)].values[int(start) : int(start) + WINDOW_SIZE, selected]
        features[row, : len(selected)] = window.mean(axis=0)
        features[row, len(selected) : 2 * len(selected)] = window.std(axis=0)
        features[row, 2 * len(selected) :] = window[-1] - window[-10]
    return features


def fit_condition_model(
    train_features: np.ndarray, config: TrainConfig
) -> tuple[DiagonalGMM, np.ndarray, dict[str, object]]:
    rng = np.random.default_rng(config.seed)
    if len(train_features) > config.gmm_samples:
        fit_values = train_features[rng.choice(len(train_features), config.gmm_samples, replace=False)]
    else:
        fit_values = train_features
    mean = fit_values.mean(axis=0)
    std = np.maximum(fit_values.std(axis=0), 1e-6)
    normalized = (fit_values - mean) / std
    candidates: list[tuple[float, DiagonalGMM]] = []
    candidate_summary: dict[str, object] = {}
    for components in (3, 4, 5):
        model = DiagonalGMM(components, config.gmm_max_iter, config.seed + components).fit(normalized)
        counts = model.predict_proba(normalized).argmax(axis=1)
        min_count = int(np.bincount(counts, minlength=components).min())
        minimum = max(2000, int(len(normalized) * 0.05))
        candidate_summary[str(components)] = {
            "bic": float(model.bic(normalized)),
            "minimum_cluster_windows": min_count,
            "required_minimum_cluster_windows": minimum,
            "accepted": min_count >= minimum,
        }
        if min_count >= minimum:
            candidates.append((model.bic(normalized), model))
    if not candidates:
        # A short time range can legitimately cover one stable operating mode only.
        # Preserve training availability, but freeze the limitation in the artifact.
        selected = DiagonalGMM(1, config.gmm_max_iter, config.seed + 1).fit(normalized)
        return selected, np.stack([mean, std]), {
            "fit_samples": int(len(fit_values)),
            "candidate_bic": candidate_summary,
            "selection": "single_condition_fallback",
            "selection_reason": "No 3-5 component candidate met the minimum condition-cluster coverage.",
        }
    _, selected = min(candidates, key=lambda item: item[0])
    return selected, np.stack([mean, std]), {
        "fit_samples": int(len(fit_values)),
        "candidate_bic": candidate_summary,
        "selection": "bic_minimum_accepted_candidate",
    }


def assign_conditions(model: DiagonalGMM, statistics: np.ndarray, features: np.ndarray) -> np.ndarray:
    normalized = (features - statistics[0]) / statistics[1]
    return model.predict_proba(normalized).argmax(axis=1).astype(np.int64)


def weighted_loss(
    prediction: torch.Tensor, target: torch.Tensor, mask: torch.Tensor, target_indexes: list[int]
) -> torch.Tensor:
    point_weights = torch.ones_like(target)
    for mask_slot, feature_index in enumerate(target_indexes):
        point_weights[:, :, feature_index] = 1.0 + mask[:, :, mask_slot]
    reconstruction = torch.nn.functional.smooth_l1_loss(prediction, target, reduction="none")
    reconstruction_loss = (reconstruction * point_weights).mean()
    trend_loss = torch.nn.functional.smooth_l1_loss(prediction[:, 1:] - prediction[:, :-1], target[:, 1:] - target[:, :-1])
    last_loss = torch.nn.functional.smooth_l1_loss(prediction[:, -10:], target[:, -10:])
    return reconstruction_loss + 0.20 * trend_loss + 0.05 * last_loss


@torch.no_grad()
def evaluate(
    model: nn.Module, loader: DataLoader, device: torch.device, target_indexes: list[int]
) -> dict[str, float]:
    model.eval()
    loss_sum = 0.0
    mae_sum = 0.0
    count = 0
    for inputs, target, mask, condition_ids in loader:
        inputs = inputs.to(device=device, dtype=torch.float32)
        target = target.to(device=device, dtype=torch.float32)
        mask = mask.to(device=device, dtype=torch.float32)
        prediction = model(inputs, condition_ids.to(device))
        batch = len(inputs)
        loss_sum += float(weighted_loss(prediction, target, mask, target_indexes)) * batch
        mae_sum += float((prediction - target).abs().mean()) * batch
        count += batch
    return {"loss": loss_sum / max(count, 1), "mae_standardized": mae_sum / max(count, 1)}


def train_model(
    model: nn.Module,
    train_loader: DataLoader,
    val_loader: DataLoader,
    config: TrainConfig,
    device: torch.device,
    target_indexes: list[int],
    output_dir: Path,
) -> dict[str, object]:
    optimizer = torch.optim.AdamW(model.parameters(), lr=config.learning_rate, weight_decay=config.weight_decay)
    best_loss = float("inf")
    best_epoch = 0
    stale_epochs = 0
    history: list[dict[str, float]] = []

    for epoch in range(1, config.epochs + 1):
        model.train()
        train_loss = 0.0
        train_count = 0
        for inputs, target, mask, condition_ids in train_loader:
            inputs = inputs.to(device=device, dtype=torch.float32)
            target = target.to(device=device, dtype=torch.float32)
            mask = mask.to(device=device, dtype=torch.float32)
            condition_ids = condition_ids.to(device=device)
            optimizer.zero_grad(set_to_none=True)
            prediction = model(inputs, condition_ids)
            loss = weighted_loss(prediction, target, mask, target_indexes)
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            optimizer.step()
            train_loss += float(loss.detach()) * len(inputs)
            train_count += len(inputs)

        validation = evaluate(model, val_loader, device, target_indexes)
        row = {"epoch": epoch, "train_loss": train_loss / max(train_count, 1), **validation}
        history.append(row)
        print(f"[epoch {epoch:03d}] train={row['train_loss']:.6f} val={row['loss']:.6f} mae={row['mae_standardized']:.6f}")

        if validation["loss"] < best_loss:
            best_loss = validation["loss"]
            best_epoch = epoch
            stale_epochs = 0
            torch.save(model.state_dict(), output_dir / "best_model.pt")
        else:
            stale_epochs += 1
            if stale_epochs >= config.early_stopping_patience:
                break

    model.load_state_dict(torch.load(output_dir / "best_model.pt", map_location=device, weights_only=True))
    return {"best_epoch": best_epoch, "best_val_loss": best_loss, "history": history}


def artifact_hash(feature_columns: list[str], mean: np.ndarray, std: np.ndarray) -> str:
    rows = []
    for index, field in enumerate(feature_columns):
        rows.append(f"{index}|{field}|z_score|{mean[index]:.12g}|{std[index]:.12g}\n")
    rows.append(f"mask_v1|{'|'.join(TARGET_FIELDS)}|0\n")
    return "sha256:" + hashlib.sha256("".join(rows).encode("utf-8")).hexdigest()


def export_onnx(model: nn.Module, output_dir: Path, condition_count: int) -> None:
    model.eval().cpu()
    example_inputs = torch.zeros((1, WINDOW_SIZE, model.process_features + len(TARGET_FIELDS)), dtype=torch.float32)
    example_conditions = torch.zeros((1,), dtype=torch.long)
    torch.onnx.export(
        model,
        (example_inputs, example_conditions),
        output_dir / "best_model.onnx",
        input_names=["inputs", "condition_id"],
        output_names=["reconstruction"],
        dynamic_axes={"inputs": {0: "batch"}, "condition_id": {0: "batch"}, "reconstruction": {0: "batch"}},
        opset_version=17,
        dynamo=False,
    )
    del condition_count


def main() -> None:
    args = parse_args()
    if args.window_size != WINDOW_SIZE:
        raise ValueError(f"This implementation is fixed to the V1.6 {WINDOW_SIZE}-second window.")
    if args.stride <= 0:
        raise ValueError("--stride must be positive.")
    if not 0.0 <= args.mask_probability <= 1.0:
        raise ValueError("--mask-probability must be in [0, 1].")

    set_seed(args.seed)
    export_manifest = Path(args.export_manifest) if args.export_manifest else None
    feature_columns = list(EXPORT_FEATURE_COLUMNS) if export_manifest else [spec.field_name for spec in POINT_SPECS]
    if len(feature_columns) != args.expected_input_count and not args.allow_reduced_schema:
        raise RuntimeError(
            f"Formal contract requires {args.expected_input_count} points, but the current registry has "
            f"{len(feature_columns)}. Add the missing point mappings or use --allow-reduced-schema only "
            "for development verification."
        )

    output_dir = Path(args.output_dir) if args.output_dir else ROOT / "EarlyWarning" / "artifacts" / datetime.now().strftime("run_%Y%m%d_%H%M%S")
    output_dir.mkdir(parents=True, exist_ok=True)
    data_dir = Path(args.data_dir)
    config = TrainConfig(
        stride=args.stride,
        epochs=args.epochs,
        batch_size=args.batch_size,
        learning_rate=args.learning_rate,
        weight_decay=args.weight_decay,
        early_stopping_patience=args.early_stopping_patience,
        mask_probability=args.mask_probability,
        max_mask_span=args.max_mask_span,
        seed=args.seed,
        gmm_max_iter=args.gmm_max_iter,
        gmm_samples=args.gmm_samples,
    )

    split_segments: dict[str, list[Segment]] = {}
    split_day_ranges: dict[str, list[str]] = {}
    data_summary: dict[str, object]
    if export_manifest:
        if not export_manifest.exists():
            raise FileNotFoundError(f"导出清单不存在: {export_manifest}")
        feature_columns, all_segments, daily_summary = load_export_segments(
            export_manifest, args.start_date, args.end_date
        )
        split_segments, split_day_ranges = split_export_segments(all_segments, config.stride)
        data_summary = {
            "mode": "litedb_export_f32_strict_complete_window",
            "export_manifest": str(export_manifest.resolve()),
            "start_date": args.start_date or None,
            "end_date": args.end_date or None,
            "daily_partitions": daily_summary,
            "empty_value_policy": "discard_any_60_second_window_with_non_finite_value",
            "split_policy": "chronological_whole_day_split_near_7_2_1_by_eligible_windows",
            "split_days": split_day_ranges,
        }
        for split in SPLITS:
            print(
                f"[data] {split}: segments={len(split_segments[split])}, "
                f"rows={sum(len(item.values) for item in split_segments[split])}"
            )
    else:
        data_summary = {"mode": "historian_csv"}
        for split in SPLITS:
            split_segments[split], summary = load_segments(data_dir, split, feature_columns)
            data_summary[split] = summary
            print(f"[data] {split}: segments={len(split_segments[split])}, rows={sum(len(item.values) for item in split_segments[split])}")

    split_indexes = {split: build_indexes(split_segments[split], WINDOW_SIZE, config.stride) for split in SPLITS}
    if any(len(split_indexes[split]) == 0 for split in SPLITS):
        raise RuntimeError(f"No eligible {WINDOW_SIZE}-second running windows in one or more splits.")

    train_rows = np.concatenate([segment.values for segment in split_segments["train"]], axis=0)
    mean = train_rows.mean(axis=0).astype(np.float32)
    raw_std = train_rows.std(axis=0).astype(np.float32)
    # A constant training-period point must not turn a normal later adjustment into a
    # million-sigma value. The floor is derived from training data only and is frozen.
    std_floor = np.maximum(0.01, np.abs(mean) * 0.01).astype(np.float32)
    std = np.maximum(raw_std, std_floor).astype(np.float32)
    for split in SPLITS:
        for segment in split_segments[split]:
            segment.values = ((segment.values - mean) / std).astype(np.float32)

    gmm_train_features = calculate_condition_features(split_segments["train"], split_indexes["train"], feature_columns)
    gmm, gmm_stats, gmm_summary = fit_condition_model(gmm_train_features, config)
    split_condition_ids = {
        split: assign_conditions(
            gmm,
            gmm_stats,
            calculate_condition_features(split_segments[split], split_indexes[split], feature_columns),
        )
        for split in SPLITS
    }
    available_window_counts = {split: int(len(split_indexes[split])) for split in SPLITS}
    for split in SPLITS:
        split_indexes[split], split_condition_ids[split] = cap_windows(
            split_indexes[split],
            split_condition_ids[split],
            args.max_windows_per_split,
            config.seed + SPLITS.index(split),
        )

    manifest = {
        "model_version": "lstm_ae_early_warning_v1_6",
        "contract_status": "formal" if len(feature_columns) == args.expected_input_count else "reduced_schema_development_only",
        "expected_input_count": args.expected_input_count,
        "process_feature_count": len(feature_columns),
        "model_input_count": len(feature_columns) + len(TARGET_FIELDS),
        "process_feature_columns": feature_columns,
        "target_fields": list(TARGET_FIELDS),
        "gmm_fields": list(GMM_FIELDS),
        "feature_contract_hash": artifact_hash(feature_columns, mean, std),
        "normalization_policy": {
            "type": "z_score_with_relative_scale_floor",
            "absolute_floor": 0.01,
            "relative_floor": 0.01,
            "description": "std = max(training_std, max(0.01, abs(training_mean) * 0.01))",
        },
        "mask_protocol": {"version": "mask_v1", "value": 0.0, "online_mask_steps": MASK_STEPS},
        "config": asdict(config),
        "training_source": "litedb_export" if export_manifest else "historian_csv",
        "data_summary": data_summary,
        "available_window_counts": available_window_counts,
        "window_counts": {split: int(len(split_indexes[split])) for split in SPLITS},
        "gmm_summary": gmm_summary,
    }
    (output_dir / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    (output_dir / "normalization.json").write_text(
        json.dumps(
            {
                "feature_columns": feature_columns,
                "mean": mean.tolist(),
                "raw_std": raw_std.tolist(),
                "std_floor": std_floor.tolist(),
                "std": std.tolist(),
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    (output_dir / "gmm.json").write_text(
        json.dumps({**gmm.to_dict(), "feature_mean": gmm_stats[0].tolist(), "feature_std": gmm_stats[1].tolist()}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    if args.prepare_only:
        print(f"[prepare] complete: {output_dir}")
        return

    device = torch.device("cuda" if args.device == "auto" and torch.cuda.is_available() else args.device if args.device != "auto" else "cpu")
    datasets = {
        split: WindowDataset(
            split_segments[split], split_indexes[split], split_condition_ids[split], feature_columns,
            config.mask_probability if split == "train" else 1.0,
            config.max_mask_span,
            training=split == "train",
            seed=config.seed + SPLITS.index(split),
        )
        for split in SPLITS
    }
    loaders = {
        split: DataLoader(dataset, batch_size=config.batch_size, shuffle=split == "train", num_workers=0)
        for split, dataset in datasets.items()
    }
    model = ConditionMaskedLSTMAE(len(feature_columns), gmm.components, WINDOW_SIZE).to(device)
    target_indexes = [feature_columns.index(field) for field in TARGET_FIELDS]
    training_summary = train_model(model, loaders["train"], loaders["val"], config, device, target_indexes, output_dir)
    test_metrics = evaluate(model, loaders["test"], device, target_indexes)
    training_summary["test_metrics"] = test_metrics
    (output_dir / "training_summary.json").write_text(json.dumps(training_summary, ensure_ascii=False, indent=2), encoding="utf-8")

    if not args.skip_onnx_export:
        export_onnx(model, output_dir, gmm.components)
    print(f"[train] complete: {output_dir}")


if __name__ == "__main__":
    main()
