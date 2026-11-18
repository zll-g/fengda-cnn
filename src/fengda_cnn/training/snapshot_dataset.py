"""Memory-mapped training datasets exported from the prediction service LiteDB store."""

from __future__ import annotations

import json
from collections import OrderedDict
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Sequence

import numpy as np

try:
    import torch
    from torch.utils.data import Dataset
except ImportError:  # pragma: no cover
    torch = None
    Dataset = object


BASE_FEATURE_COLUMNS = [
    "burner_center_gas_valve_pct",
    "burner_outer_gas_valve_pct",
    "burner_air_damper_pct",
    "blower_frequency_hz",
    "blower_current_a",
    "fgr_valve_pct",
    "fgr_fan_frequency_hz",
    "natural_gas_inlet_flow_m3_h",
]
TARGET_COLUMNS = ["stack_nox_mg_m3", "stack_o2_pct", "stack_no2_mg_m3"]
DIFF_LAGS = (1, 5, 10, 30)
INPUT_STEPS = 60
OUTPUT_STEPS = 60
WINDOW_STEPS = INPUT_STEPS + OUTPUT_STEPS


@dataclass(frozen=True)
class SnapshotSegment:
    path: Path
    start_time: np.datetime64
    row_count: int
    valid_starts: np.ndarray
    transient_scales: np.ndarray


@dataclass(frozen=True)
class SnapshotSplit:
    train: list[tuple[int, np.ndarray]]
    validation: list[tuple[int, np.ndarray]]
    test: list[tuple[int, np.ndarray]]
    train_boundary: np.datetime64
    validation_boundary: np.datetime64


@dataclass(frozen=True)
class SnapshotNormalizationStats:
    feature_mean: np.ndarray
    feature_std: np.ndarray
    target_mean: np.ndarray
    target_std: np.ndarray


def model_feature_columns() -> list[str]:
    columns = list(BASE_FEATURE_COLUMNS)
    for column in BASE_FEATURE_COLUMNS:
        for lag in DIFF_LAGS:
            columns.append(f"{column}__diff_{lag}s")
    columns.extend(f"hist__{column}" for column in TARGET_COLUMNS)
    return columns


def load_snapshot_segments(manifest_path: str | Path) -> list[SnapshotSegment]:
    """Load segment metadata and compact valid sample starts without loading all source data."""

    manifest_file = Path(manifest_path)
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    expected_fields = BASE_FEATURE_COLUMNS + TARGET_COLUMNS
    fields = manifest.get("fields", [])
    if fields != expected_fields:
        raise ValueError(f"训练快照字段不匹配: expected={expected_fields}, actual={fields}")

    segments: list[SnapshotSegment] = []
    for item in manifest.get("segments", []):
        row_count = int(item["rowCount"])
        if row_count < WINDOW_STEPS:
            continue
        path = manifest_file.parent / item["file"]
        if not path.exists():
            raise FileNotFoundError(f"缺少训练快照分区: {path}")
        values = np.memmap(path, mode="r", dtype=np.float32, shape=(row_count, len(expected_fields)))
        valid_rows = np.isfinite(values).all(axis=1)
        valid_starts = _window_valid_starts(valid_rows)
        transient_scales = _robust_scales(values[:, : len(BASE_FEATURE_COLUMNS)])
        del values
        if valid_starts.size == 0:
            continue
        segments.append(
            SnapshotSegment(
                path=path,
                start_time=np.datetime64(item["startTime"], "s"),
                row_count=row_count,
                valid_starts=valid_starts,
                transient_scales=transient_scales,
            )
        )

    if not segments:
        raise ValueError("选定时间范围内没有可用于训练的连续 120 秒完整数据。")
    return segments


def split_snapshot_segments(
    segments: Sequence[SnapshotSegment],
    train_ratio: float,
    validation_ratio: float,
    test_ratio: float,
) -> SnapshotSplit:
    """Create chronological splits and purge 119 seconds around each boundary."""

    if not np.isclose(train_ratio + validation_ratio + test_ratio, 1.0):
        raise ValueError("训练、验证、测试比例之和必须等于 1。")

    total = sum(len(segment.valid_starts) for segment in segments)
    if total < 3:
        raise ValueError("可用样本数量不足，无法划分训练、验证和测试集。")
    train_boundary = _time_at_ordinal(segments, max(int(total * train_ratio), 1))
    validation_boundary = _time_at_ordinal(
        segments,
        min(max(int(total * (train_ratio + validation_ratio)), 2), total - 1),
    )
    train: list[tuple[int, np.ndarray]] = []
    validation: list[tuple[int, np.ndarray]] = []
    test: list[tuple[int, np.ndarray]] = []
    train_cut = max(int(total * train_ratio), 1)
    validation_cut = min(max(int(total * (train_ratio + validation_ratio)), 2), total - 1)
    offset = 0
    for segment_index, segment in enumerate(segments):
        positions = np.arange(offset, offset + len(segment.valid_starts))
        train_starts = segment.valid_starts[positions < train_cut]
        validation_starts = segment.valid_starts[(positions >= train_cut) & (positions < validation_cut)]
        test_starts = segment.valid_starts[positions >= validation_cut]
        if train_starts.size:
            train.append((segment_index, train_starts))
        if validation_starts.size:
            validation.append((segment_index, validation_starts))
        if test_starts.size:
            test.append((segment_index, test_starts))
        offset += len(segment.valid_starts)

    # Only purge when the two adjacent chronological groups touch within a model window.
    # A natural data gap (for example, between two operating days) is already an isolation gap.
    validation_first = _first_sample_time(validation, segments)
    if validation_first is not None:
        train = _purge_before(train, segments, validation_first)
    test_first = _first_sample_time(test, segments)
    if test_first is not None:
        validation = _purge_before(validation, segments, test_first)

    if not train or not validation or not test:
        raise ValueError("按时间划分后至少有一个数据集为空，请扩大时间范围或调整划分比例。")
    return SnapshotSplit(train, validation, test, train_boundary, validation_boundary)


def summarize_split(split: SnapshotSplit, segments: Sequence[SnapshotSegment]) -> dict[str, object]:
    return {
        "split_mode": "chronological_purged_snapshot_segments",
        "input_steps": INPUT_STEPS,
        "output_steps": OUTPUT_STEPS,
        "purge_seconds": WINDOW_STEPS - 1,
        "train_samples": _sample_count(split.train),
        "val_samples": _sample_count(split.validation),
        "test_samples": _sample_count(split.test),
        "train_boundary": str(split.train_boundary),
        "validation_boundary": str(split.validation_boundary),
        "segment_count": len(segments),
    }


class SnapshotSequenceDataset(Dataset):
    """A lazy sequence dataset that keeps only a small LRU cache of daily segments."""

    def __init__(
        self,
        segments: Sequence[SnapshotSegment],
        groups: Sequence[tuple[int, np.ndarray]],
        normalization: SnapshotNormalizationStats | None = None,
        cache_size: int = 3,
    ) -> None:
        self.segments = list(segments)
        self.groups = [(segment, starts.astype(np.int32, copy=False)) for segment, starts in groups]
        self.normalization = normalization
        self.cache_size = max(cache_size, 1)
        self.prefix_counts = np.cumsum([len(starts) for _, starts in self.groups], dtype=np.int64)
        self.cache: OrderedDict[int, np.ndarray] = OrderedDict()

    def __len__(self) -> int:
        return int(self.prefix_counts[-1]) if self.prefix_counts.size else 0

    def __getitem__(self, index: int):
        features, targets, weight = self.get_raw(index)
        if self.normalization is not None:
            features = (features - self.normalization.feature_mean) / self.normalization.feature_std
            targets = (targets - self.normalization.target_mean) / self.normalization.target_std
        if torch is None:
            return features.astype(np.float32), targets.astype(np.float32), np.float32(weight)
        return (
            torch.from_numpy(features.astype(np.float32, copy=False)),
            torch.from_numpy(targets.astype(np.float32, copy=False)),
            torch.tensor(weight, dtype=torch.float32),
        )

    def get_raw(self, index: int) -> tuple[np.ndarray, np.ndarray, float]:
        group_index = int(np.searchsorted(self.prefix_counts, index, side="right"))
        previous = int(self.prefix_counts[group_index - 1]) if group_index else 0
        segment_index, starts = self.groups[group_index]
        start = int(starts[index - previous])
        values = self._segment_values(segment_index)
        window = values[start : start + WINDOW_STEPS]
        base = window[:INPUT_STEPS, : len(BASE_FEATURE_COLUMNS)].copy()
        target_history = window[:INPUT_STEPS, len(BASE_FEATURE_COLUMNS) :]
        diffs = []
        for feature_index in range(len(BASE_FEATURE_COLUMNS)):
            for lag in DIFF_LAGS:
                delta = np.zeros(INPUT_STEPS, dtype=np.float32)
                delta[lag:] = base[lag:, feature_index] - base[:-lag, feature_index]
                diffs.append(delta.reshape(-1, 1))
        features = np.concatenate([base, *diffs, target_history], axis=1).astype(np.float32, copy=False)
        targets = window[INPUT_STEPS:, len(BASE_FEATURE_COLUMNS) :].astype(np.float32, copy=True)
        weight = _transient_weight(base, self.segments[segment_index].transient_scales)
        return features, targets, weight

    def _segment_values(self, segment_index: int) -> np.ndarray:
        cached = self.cache.pop(segment_index, None)
        if cached is not None:
            self.cache[segment_index] = cached
            return cached
        segment = self.segments[segment_index]
        raw = np.memmap(
            segment.path,
            mode="r",
            dtype=np.float32,
            shape=(segment.row_count, len(BASE_FEATURE_COLUMNS) + len(TARGET_COLUMNS)),
        )
        values = np.asarray(raw, dtype=np.float32).copy()
        del raw
        values = _interpolate_short_gaps(values, limit=20)
        self.cache[segment_index] = values
        while len(self.cache) > self.cache_size:
            self.cache.popitem(last=False)
        return values


def compute_snapshot_normalization(
    dataset: SnapshotSequenceDataset,
    should_stop: Callable[[], bool] | None = None,
) -> SnapshotNormalizationStats:
    """Compute z-score parameters in a streaming pass over the training samples."""

    feature_sum = np.zeros((1, len(model_feature_columns())), dtype=np.float64)
    feature_sum_sq = np.zeros_like(feature_sum)
    target_sum = np.zeros((1, len(TARGET_COLUMNS)), dtype=np.float64)
    target_sum_sq = np.zeros_like(target_sum)
    feature_count = 0
    target_count = 0
    for index in range(len(dataset)):
        if should_stop is not None and index % 128 == 0 and should_stop():
            raise InterruptedError("训练已被用户停止。")
        features, targets, _ = dataset.get_raw(index)
        feature_sum += features.sum(axis=0, keepdims=True, dtype=np.float64)
        feature_sum_sq += np.square(features, dtype=np.float64).sum(axis=0, keepdims=True)
        target_sum += targets.sum(axis=0, keepdims=True, dtype=np.float64)
        target_sum_sq += np.square(targets, dtype=np.float64).sum(axis=0, keepdims=True)
        feature_count += features.shape[0]
        target_count += targets.shape[0]
    feature_mean = feature_sum / max(feature_count, 1)
    target_mean = target_sum / max(target_count, 1)
    feature_std = np.sqrt(np.maximum(feature_sum_sq / max(feature_count, 1) - feature_mean**2, 1e-12))
    target_std = np.sqrt(np.maximum(target_sum_sq / max(target_count, 1) - target_mean**2, 1e-12))
    return SnapshotNormalizationStats(
        feature_mean=feature_mean.astype(np.float32),
        feature_std=feature_std.astype(np.float32),
        target_mean=target_mean.astype(np.float32),
        target_std=target_std.astype(np.float32),
    )


def _window_valid_starts(valid_rows: np.ndarray) -> np.ndarray:
    required = np.ones(WINDOW_STEPS, dtype=np.int16)
    rolling = np.convolve(valid_rows.astype(np.int16), required, mode="valid")
    return np.flatnonzero(rolling == WINDOW_STEPS).astype(np.int32)


def _robust_scales(values: np.ndarray) -> np.ndarray:
    with np.errstate(invalid="ignore"):
        scales = np.nanpercentile(values, 95, axis=0) - np.nanpercentile(values, 5, axis=0)
    fallback = np.nanmax(values, axis=0) - np.nanmin(values, axis=0)
    scales = np.where(np.isfinite(scales) & (scales > 1e-6), scales, fallback)
    return np.where(np.isfinite(scales) & (scales > 1e-6), scales, 1.0).astype(np.float32)


def _transient_weight(base: np.ndarray, scales: np.ndarray) -> float:
    score = 0.0
    for lag in (1, 5, 10):
        if len(base) <= lag:
            continue
        score = max(score, float(np.max(np.abs(base[lag:] - base[:-lag]) / scales)))
    if score < 0.15:
        return 1.0
    return 1.0 + (3.0 * min((score - 0.15) / 0.15, 1.0))


def _interpolate_short_gaps(values: np.ndarray, limit: int) -> np.ndarray:
    result = values.copy()
    for column in range(result.shape[1]):
        source = result[:, column]
        valid = np.flatnonzero(np.isfinite(source))
        if valid.size < 2:
            continue
        missing = ~np.isfinite(source)
        for start, end in _missing_runs(missing):
            if start == 0 or end == len(source) or end - start > limit:
                continue
            source[start:end] = np.interp(np.arange(start, end), valid, source[valid])
    return result


def _missing_runs(missing: np.ndarray):
    starts = np.flatnonzero(missing & np.r_[True, ~missing[:-1]])
    ends = np.flatnonzero(missing & np.r_[~missing[1:], True]) + 1
    return zip(starts, ends, strict=True)


def _time_at_ordinal(segments: Sequence[SnapshotSegment], ordinal: int) -> np.datetime64:
    remaining = ordinal
    for segment in segments:
        if remaining < len(segment.valid_starts):
            return segment.start_time + np.timedelta64(int(segment.valid_starts[remaining]), "s")
        remaining -= len(segment.valid_starts)
    last = segments[-1]
    return last.start_time + np.timedelta64(int(last.valid_starts[-1]), "s")


def _sample_count(groups: Sequence[tuple[int, np.ndarray]]) -> int:
    return int(sum(len(starts) for _, starts in groups))


def _first_sample_time(
    groups: Sequence[tuple[int, np.ndarray]],
    segments: Sequence[SnapshotSegment],
) -> np.datetime64 | None:
    for segment_index, starts in groups:
        if starts.size:
            return segments[segment_index].start_time + np.timedelta64(int(starts[0]), "s")
    return None


def _purge_before(
    groups: Sequence[tuple[int, np.ndarray]],
    segments: Sequence[SnapshotSegment],
    next_start: np.datetime64,
) -> list[tuple[int, np.ndarray]]:
    limit = next_start - np.timedelta64(WINDOW_STEPS - 1, "s")
    kept: list[tuple[int, np.ndarray]] = []
    for segment_index, starts in groups:
        sample_times = segments[segment_index].start_time + starts.astype("timedelta64[s]")
        valid = starts[sample_times < limit]
        if valid.size:
            kept.append((segment_index, valid))
    return kept
