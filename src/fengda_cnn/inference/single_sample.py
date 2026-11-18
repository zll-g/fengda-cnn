"""Single-sample inference and forecast artifact generation."""

from __future__ import annotations

import json
from dataclasses import asdict
from pathlib import Path

import numpy as np
import pandas as pd
import torch

from fengda_cnn.models import CNNLSTMAttentionConfig, CNNLSTMAttentionModel
from fengda_cnn.training import (
    LoadedSampleDataset,
    SampleSplit,
    apply_normalization,
    forecast_metrics,
    invert_normalization,
    load_normalization_stats,
    load_sample_dataset,
    split_samples_chronologically,
)


SPLIT_NAMES = ("train", "val", "test")


def _select_split(split: SampleSplit, split_name: str) -> tuple[np.ndarray, np.ndarray, pd.DataFrame]:
    split_name = split_name.lower()
    if split_name == "train":
        return split.train_features, split.train_targets, split.train_metadata
    if split_name == "val":
        return split.val_features, split.val_targets, split.val_metadata
    if split_name == "test":
        return split.test_features, split.test_targets, split.test_metadata
    raise ValueError(f"Unsupported split_name: {split_name}")


def _has_fixed_split_samples(samples_path: Path) -> bool:
    return all((samples_path / split_name / "samples.npz").exists() for split_name in SPLIT_NAMES)


def _build_time_index(start: str, periods: int, freq: str = "1s") -> pd.DatetimeIndex:
    return pd.date_range(start=pd.Timestamp(start), periods=periods, freq=freq)


def _target_aliases(target_columns: list[str]) -> dict[str, str]:
    aliases = {
        "stack_nox_mg_m3": "NOx",
        "stack_so2_mg_m3": "SO2",
        "stack_o2_pct": "O2",
        "stack_dust_mg_m3": "Dust",
        "stack_no2_mg_m3": "NO2",
    }
    return {target_name: aliases.get(target_name, target_name) for target_name in target_columns}


def _prepare_input_context(
    sample_features: np.ndarray,
    feature_columns: list[str],
    metadata_row: pd.Series,
) -> pd.DataFrame:
    input_timestamps = _build_time_index(metadata_row["input_start"], len(sample_features))
    frame = pd.DataFrame(sample_features, columns=feature_columns)
    frame.insert(0, "timestamp", input_timestamps)
    return frame


def _prepare_forecast_frame(
    y_true: np.ndarray,
    y_pred: np.ndarray,
    target_columns: list[str],
    metadata_row: pd.Series,
) -> pd.DataFrame:
    timestamps = _build_time_index(metadata_row["target_start"], y_true.shape[0])
    frame = pd.DataFrame({"timestamp": timestamps})
    for idx, target_name in enumerate(target_columns):
        frame[f"{target_name}_actual"] = y_true[:, idx]
        frame[f"{target_name}_pred"] = y_pred[:, idx]
        frame[f"{target_name}_error"] = y_pred[:, idx] - y_true[:, idx]
    frame["seconds_from_target_start"] = np.arange(len(frame))
    return frame


def _prepare_attention_frame(attention_weights: np.ndarray, metadata_row: pd.Series) -> pd.DataFrame:
    timestamps = _build_time_index(metadata_row["input_start"], attention_weights.shape[0])
    return pd.DataFrame(
        {
            "timestamp": timestamps,
            "attention_weight": attention_weights,
            "seconds_from_input_start": np.arange(len(attention_weights)),
        }
    )


def run_single_sample_inference(
    *,
    samples_dir: str | Path,
    model_dir: str | Path,
    output_dir: str | Path,
    split_name: str = "test",
    sample_index: int = 0,
    train_ratio: float = 0.7,
    val_ratio: float = 0.15,
    test_ratio: float = 0.15,
) -> dict[str, object]:
    """Run one forecast sample through the trained model and save artifacts."""

    samples_path = Path(samples_dir)
    model_path = Path(model_dir)
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)

    if _has_fixed_split_samples(samples_path):
        dataset = load_sample_dataset(samples_path / split_name)
        features, targets, metadata = dataset.features, dataset.targets, dataset.metadata
    else:
        dataset = load_sample_dataset(samples_path)
        split = split_samples_chronologically(
            dataset=dataset,
            train_ratio=train_ratio,
            val_ratio=val_ratio,
            test_ratio=test_ratio,
        )
        features, targets, metadata = _select_split(split, split_name)
    if sample_index < 0 or sample_index >= len(metadata):
        raise IndexError(f"sample_index out of range for split={split_name}: {sample_index}")

    metadata_row = metadata.iloc[sample_index]
    sample_features = features[sample_index]
    sample_targets = targets[sample_index]

    norm_stats = load_normalization_stats(model_path)
    checkpoint = torch.load(model_path / "best_model.pt", map_location="cpu")
    model_config = CNNLSTMAttentionConfig(**checkpoint["model_config"])
    model = CNNLSTMAttentionModel(model_config)
    model.load_state_dict(checkpoint["model_state_dict"])
    model.eval()

    normalized_input = apply_normalization(
        sample_features[np.newaxis, ...],
        norm_stats.feature_mean,
        norm_stats.feature_std,
    )

    with torch.no_grad():
        forecast_tensor, attention_tensor = model(torch.from_numpy(normalized_input))

    pred_normalized = forecast_tensor.cpu().numpy()[0]
    attention_weights = attention_tensor.cpu().numpy()[0]
    y_pred = invert_normalization(
        pred_normalized[np.newaxis, ...],
        norm_stats.target_mean,
        norm_stats.target_std,
    )[0]
    y_true = sample_targets

    metrics = forecast_metrics(
        y_true=y_true[np.newaxis, ...],
        y_pred=y_pred[np.newaxis, ...],
        target_names=dataset.target_columns,
    )
    forecast_frame = _prepare_forecast_frame(y_true, y_pred, dataset.target_columns, metadata_row)
    input_context_frame = _prepare_input_context(sample_features, dataset.feature_columns, metadata_row)
    attention_frame = _prepare_attention_frame(attention_weights, metadata_row)

    forecast_frame.to_csv(output_path / "forecast.csv", index=False, encoding="utf-8-sig")
    input_context_frame.to_csv(output_path / "input_context.csv", index=False, encoding="utf-8-sig")
    attention_frame.to_csv(output_path / "attention_weights.csv", index=False, encoding="utf-8-sig")

    summary = {
        "split": split_name,
        "sample_index": sample_index,
        "source_file": metadata_row["source_file"],
        "input_start": metadata_row["input_start"],
        "input_end": metadata_row["input_end"],
        "target_start": metadata_row["target_start"],
        "target_end": metadata_row["target_end"],
        "run_stage": metadata_row["run_stage"],
        "run_stage_code": int(metadata_row["run_stage_code"]),
        "missing_values_before_fill_sum": int(metadata_row["missing_values_before_fill_sum"]),
        "model_config": asdict(model_config),
        "train_config": checkpoint["train_config"],
        "best_epoch": checkpoint["best_epoch"],
        "best_val_loss": checkpoint["best_val_loss"],
        "target_aliases": _target_aliases(dataset.target_columns),
    }
    summary.update(metrics)
    (output_path / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return summary
