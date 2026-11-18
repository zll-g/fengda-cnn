"""Training sample generation for multi-step multi-pollutant forecasting."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

import numpy as np
import pandas as pd

from fengda_cnn.config import DEFAULT_INPUT_STEPS, DEFAULT_OUTPUT_STEPS

from .point_registry import FEATURE_COLUMNS, TARGET_COLUMNS

DEFAULT_DIFF_LAGS = (1, 5, 10, 30)
DEFAULT_TRANSIENT_LAGS = (1, 5, 10)
HISTORY_FEATURE_PREFIX = "hist__"
DIFF_FEATURE_SEPARATOR = "__diff_"
DEFAULT_TRANSIENT_COLUMNS = (
    "burner_center_gas_valve_pct",
    "burner_outer_gas_valve_pct",
    "burner_air_damper_pct",
    "blower_frequency_hz",
    "blower_current_a",
    "fgr_valve_pct",
    "fgr_fan_frequency_hz",
    "natural_gas_inlet_flow_m3_h",
)


@dataclass
class SampleDataset:
    features: np.ndarray
    targets: np.ndarray
    sample_weights: np.ndarray
    metadata: pd.DataFrame
    feature_columns: list[str]
    target_columns: list[str]


def _prepare_frame(frame: pd.DataFrame) -> pd.DataFrame:
    prepared = frame.copy()
    prepared["timestamp"] = pd.to_datetime(prepared["timestamp"], errors="coerce")
    prepared = prepared.dropna(subset=["timestamp"]).sort_values(["source_file", "timestamp"])
    return prepared.reset_index(drop=True)


def _normalize_lags(lags: Sequence[int]) -> tuple[int, ...]:
    return tuple(sorted({int(lag) for lag in lags if int(lag) > 0}))


def build_model_feature_columns(
    feature_columns: Sequence[str],
    target_columns: Sequence[str],
    include_input_diffs: bool = True,
    diff_lags: Sequence[int] = DEFAULT_DIFF_LAGS,
    include_target_history: bool = True,
) -> list[str]:
    """Return the model input schema after optional dynamic feature expansion."""

    model_feature_columns = list(feature_columns)
    normalized_lags = _normalize_lags(diff_lags)

    if include_input_diffs:
        for column in feature_columns:
            for lag in normalized_lags:
                model_feature_columns.append(f"{column}{DIFF_FEATURE_SEPARATOR}{lag}s")

    if include_target_history:
        model_feature_columns.extend(f"{HISTORY_FEATURE_PREFIX}{column}" for column in target_columns)

    return model_feature_columns


def _add_engineered_features(
    frame: pd.DataFrame,
    feature_columns: Sequence[str],
    target_columns: Sequence[str],
    group_column: str,
    include_input_diffs: bool,
    diff_lags: Sequence[int],
    include_target_history: bool,
) -> pd.DataFrame:
    engineered = frame.copy()
    grouped = engineered.groupby(group_column, sort=False)

    if include_input_diffs:
        for column in feature_columns:
            for lag in _normalize_lags(diff_lags):
                feature_name = f"{column}{DIFF_FEATURE_SEPARATOR}{lag}s"
                engineered[feature_name] = grouped[column].diff(lag).fillna(0.0)

    if include_target_history:
        for column in target_columns:
            engineered[f"{HISTORY_FEATURE_PREFIX}{column}"] = engineered[column]

    return engineered


def _robust_scales(values: np.ndarray, epsilon: float = 1e-6) -> np.ndarray:
    q95 = np.nanpercentile(values, 95, axis=0)
    q05 = np.nanpercentile(values, 5, axis=0)
    scales = q95 - q05
    fallback = np.nanmax(values, axis=0) - np.nanmin(values, axis=0)
    scales = np.where(np.isfinite(scales) & (scales > epsilon), scales, fallback)
    scales = np.where(np.isfinite(scales) & (scales > epsilon), scales, 1.0)
    return scales.astype(np.float32)


def _transient_score(window: np.ndarray, scales: np.ndarray, lags: Sequence[int]) -> float:
    if window.shape[0] < 2 or window.shape[1] == 0:
        return 0.0

    score = 0.0
    for lag in _normalize_lags(lags):
        if window.shape[0] <= lag:
            continue

        deltas = np.abs(window[lag:] - window[:-lag]) / scales.reshape(1, -1)
        finite = deltas[np.isfinite(deltas)]
        if finite.size:
            score = max(score, float(finite.max()))
    return score


def _sample_weight_from_score(
    score: float,
    threshold: float,
    weight_boost: float,
) -> float:
    if threshold <= 0.0 or weight_boost <= 0.0:
        return 1.0

    if score < threshold:
        return 1.0

    ratio = min(max((score - threshold) / threshold, 0.0), 1.0)
    return 1.0 + (weight_boost * ratio)


def build_supervised_samples(
    frame: pd.DataFrame,
    input_steps: int = DEFAULT_INPUT_STEPS,
    output_steps: int = DEFAULT_OUTPUT_STEPS,
    stride: int = 1,
    feature_columns: list[str] | None = None,
    target_columns: list[str] | None = None,
    group_column: str = "source_file",
    require_valid_rows: bool = True,
    include_input_diffs: bool = True,
    diff_lags: Sequence[int] = DEFAULT_DIFF_LAGS,
    include_target_history: bool = True,
    transient_columns: Sequence[str] | None = DEFAULT_TRANSIENT_COLUMNS,
    transient_lags: Sequence[int] = DEFAULT_TRANSIENT_LAGS,
    transient_threshold: float = 0.15,
    transient_weight_boost: float = 3.0,
) -> SampleDataset:
    """Build sliding-window samples from a standardized dataframe."""

    prepared = _prepare_frame(frame)
    feature_columns = feature_columns or FEATURE_COLUMNS
    target_columns = target_columns or TARGET_COLUMNS
    model_feature_columns = build_model_feature_columns(
        feature_columns=feature_columns,
        target_columns=target_columns,
        include_input_diffs=include_input_diffs,
        diff_lags=diff_lags,
        include_target_history=include_target_history,
    )

    missing_feature_columns = [col for col in feature_columns if col not in prepared.columns]
    missing_target_columns = [col for col in target_columns if col not in prepared.columns]
    if missing_feature_columns or missing_target_columns:
        raise ValueError(
            "Missing columns in standardized dataframe: "
            f"features={missing_feature_columns}, targets={missing_target_columns}"
        )

    prepared = _add_engineered_features(
        frame=prepared,
        feature_columns=feature_columns,
        target_columns=target_columns,
        group_column=group_column,
        include_input_diffs=include_input_diffs,
        diff_lags=diff_lags,
        include_target_history=include_target_history,
    )

    features_list: list[np.ndarray] = []
    targets_list: list[np.ndarray] = []
    sample_weights: list[float] = []
    metadata_rows: list[dict[str, object]] = []
    active_transient_columns = list(transient_columns or feature_columns)

    for source_file, group in prepared.groupby(group_column, sort=False):
        group = group.reset_index(drop=True)
        total_rows = len(group)
        max_start = total_rows - input_steps - output_steps + 1
        if max_start <= 0:
            continue

        feature_values = group[model_feature_columns].to_numpy(dtype=np.float32)
        target_values = group[target_columns].to_numpy(dtype=np.float32)
        transient_feature_columns = [col for col in active_transient_columns if col in group.columns]
        transient_values = group[transient_feature_columns].to_numpy(dtype=np.float32)
        transient_scales = _robust_scales(transient_values) if transient_feature_columns else np.empty(0, dtype=np.float32)
        valid_flags = group["is_valid_row"].astype(bool).to_numpy()
        timestamps = group["timestamp"].to_numpy()

        for start in range(0, max_start, stride):
            input_end = start + input_steps
            output_end = input_end + output_steps

            if require_valid_rows and not valid_flags[start:output_end].all():
                continue

            feature_window = feature_values[start:input_end]
            target_window = target_values[input_end:output_end]
            if not np.isfinite(feature_window).all() or not np.isfinite(target_window).all():
                continue

            score = _transient_score(
                transient_values[start:input_end],
                transient_scales,
                transient_lags,
            )
            sample_weight = _sample_weight_from_score(
                score=score,
                threshold=transient_threshold,
                weight_boost=transient_weight_boost,
            )

            features_list.append(feature_window)
            targets_list.append(target_window)
            sample_weights.append(sample_weight)
            metadata_rows.append(
                {
                    "source_file": source_file,
                    "input_start": pd.Timestamp(timestamps[start]).isoformat(),
                    "input_end": pd.Timestamp(timestamps[input_end - 1]).isoformat(),
                    "target_start": pd.Timestamp(timestamps[input_end]).isoformat(),
                    "target_end": pd.Timestamp(timestamps[output_end - 1]).isoformat(),
                    "run_stage": group.loc[input_end - 1, "run_stage"],
                    "run_stage_code": int(group.loc[input_end - 1, "run_stage_code"]),
                    "missing_values_before_fill_sum": int(
                        group.loc[start:output_end - 1, "missing_values_before_fill"].sum()
                    ),
                    "transient_score": score,
                    "is_transient": bool(score >= transient_threshold),
                    "sample_weight": sample_weight,
                }
            )

    if not features_list:
        empty_features = np.empty((0, input_steps, len(model_feature_columns)), dtype=np.float32)
        empty_targets = np.empty((0, output_steps, len(target_columns)), dtype=np.float32)
        empty_sample_weights = np.empty((0,), dtype=np.float32)
        metadata = pd.DataFrame(
            columns=[
                "source_file",
                "input_start",
                "input_end",
                "target_start",
                "target_end",
                "run_stage",
                "run_stage_code",
                "missing_values_before_fill_sum",
                "transient_score",
                "is_transient",
                "sample_weight",
            ]
        )
        return SampleDataset(
            features=empty_features,
            targets=empty_targets,
            sample_weights=empty_sample_weights,
            metadata=metadata,
            feature_columns=model_feature_columns,
            target_columns=target_columns,
        )

    return SampleDataset(
        features=np.stack(features_list),
        targets=np.stack(targets_list),
        sample_weights=np.asarray(sample_weights, dtype=np.float32),
        metadata=pd.DataFrame(metadata_rows),
        feature_columns=model_feature_columns,
        target_columns=target_columns,
    )


def save_sample_dataset(dataset: SampleDataset, output_dir: str | Path) -> None:
    """Persist samples, metadata and schema to disk."""

    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)

    np.savez_compressed(
        output_path / "samples.npz",
        X=dataset.features,
        y=dataset.targets,
        sample_weight=dataset.sample_weights,
    )
    dataset.metadata.to_csv(output_path / "sample_metadata.csv", index=False, encoding="utf-8-sig")
    schema = {
        "feature_columns": dataset.feature_columns,
        "target_columns": dataset.target_columns,
        "feature_shape": list(dataset.features.shape),
        "target_shape": list(dataset.targets.shape),
        "sample_weight_shape": list(dataset.sample_weights.shape),
    }
    (output_path / "schema.json").write_text(
        json.dumps(schema, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
