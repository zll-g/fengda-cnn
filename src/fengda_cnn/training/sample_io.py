"""Sample loading, chronological splitting, and normalization helpers."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd


@dataclass
class LoadedSampleDataset:
    features: np.ndarray
    targets: np.ndarray
    sample_weights: np.ndarray
    metadata: pd.DataFrame
    feature_columns: list[str]
    target_columns: list[str]


@dataclass
class SampleSplit:
    train_features: np.ndarray
    train_targets: np.ndarray
    train_sample_weights: np.ndarray
    train_metadata: pd.DataFrame
    val_features: np.ndarray
    val_targets: np.ndarray
    val_sample_weights: np.ndarray
    val_metadata: pd.DataFrame
    test_features: np.ndarray
    test_targets: np.ndarray
    test_sample_weights: np.ndarray
    test_metadata: pd.DataFrame


@dataclass
class NormalizationStats:
    feature_mean: np.ndarray
    feature_std: np.ndarray
    target_mean: np.ndarray
    target_std: np.ndarray


def load_sample_dataset(samples_dir: str | Path) -> LoadedSampleDataset:
    """Load features, targets, metadata, and schema from a sample directory."""

    sample_path = Path(samples_dir)
    arrays = np.load(sample_path / "samples.npz")
    metadata = pd.read_csv(sample_path / "sample_metadata.csv")
    schema = json.loads((sample_path / "schema.json").read_text(encoding="utf-8"))
    sample_count = int(arrays["X"].shape[0])
    if "sample_weight" in arrays.files:
        sample_weights = arrays["sample_weight"].astype(np.float32)
    elif "sample_weight" in metadata.columns:
        sample_weights = metadata["sample_weight"].to_numpy(dtype=np.float32)
    else:
        sample_weights = np.ones(sample_count, dtype=np.float32)

    return LoadedSampleDataset(
        features=arrays["X"].astype(np.float32),
        targets=arrays["y"].astype(np.float32),
        sample_weights=sample_weights.reshape(sample_count),
        metadata=metadata,
        feature_columns=list(schema["feature_columns"]),
        target_columns=list(schema["target_columns"]),
    )


def _sorted_indices_by_time(metadata: pd.DataFrame) -> np.ndarray:
    sortable = metadata.copy()
    sortable["target_start"] = pd.to_datetime(sortable["target_start"], errors="coerce")
    sortable = sortable.sort_values(["target_start", "source_file"], kind="stable")
    return sortable.index.to_numpy()


def split_samples_chronologically(
    dataset: LoadedSampleDataset,
    train_ratio: float = 0.7,
    val_ratio: float = 0.15,
    test_ratio: float = 0.15,
) -> SampleSplit:
    """Split samples by chronological target start time."""

    if not np.isclose(train_ratio + val_ratio + test_ratio, 1.0):
        raise ValueError("train_ratio + val_ratio + test_ratio must equal 1.0")

    sample_count = len(dataset.metadata)
    if sample_count != len(dataset.features) or sample_count != len(dataset.targets):
        raise ValueError("Features, targets, and metadata must have the same sample count")
    if sample_count != len(dataset.sample_weights):
        raise ValueError("Features, targets, metadata, and sample weights must have the same sample count")

    order = _sorted_indices_by_time(dataset.metadata)
    features = dataset.features[order]
    targets = dataset.targets[order]
    sample_weights = dataset.sample_weights[order]
    metadata = dataset.metadata.iloc[order].reset_index(drop=True)

    train_end = int(sample_count * train_ratio)
    val_end = train_end + int(sample_count * val_ratio)
    if sample_count > 2:
        train_end = max(train_end, 1)
        val_end = max(val_end, train_end + 1)
        val_end = min(val_end, sample_count - 1)

    return SampleSplit(
        train_features=features[:train_end],
        train_targets=targets[:train_end],
        train_sample_weights=sample_weights[:train_end],
        train_metadata=metadata.iloc[:train_end].reset_index(drop=True),
        val_features=features[train_end:val_end],
        val_targets=targets[train_end:val_end],
        val_sample_weights=sample_weights[train_end:val_end],
        val_metadata=metadata.iloc[train_end:val_end].reset_index(drop=True),
        test_features=features[val_end:],
        test_targets=targets[val_end:],
        test_sample_weights=sample_weights[val_end:],
        test_metadata=metadata.iloc[val_end:].reset_index(drop=True),
    )


def compute_normalization_stats(
    train_features: np.ndarray,
    train_targets: np.ndarray,
    epsilon: float = 1e-6,
) -> NormalizationStats:
    """Compute z-score statistics from the training split only."""

    feature_mean = train_features.mean(axis=(0, 1), keepdims=True).astype(np.float32)
    feature_std = train_features.std(axis=(0, 1), keepdims=True).astype(np.float32)
    target_mean = train_targets.mean(axis=(0, 1), keepdims=True).astype(np.float32)
    target_std = train_targets.std(axis=(0, 1), keepdims=True).astype(np.float32)

    feature_std = np.where(feature_std < epsilon, 1.0, feature_std)
    target_std = np.where(target_std < epsilon, 1.0, target_std)

    return NormalizationStats(
        feature_mean=feature_mean,
        feature_std=feature_std,
        target_mean=target_mean,
        target_std=target_std,
    )


def apply_normalization(
    values: np.ndarray,
    mean: np.ndarray,
    std: np.ndarray,
) -> np.ndarray:
    """Apply z-score normalization to a batch of sequences."""

    return ((values - mean) / std).astype(np.float32)


def invert_normalization(
    values: np.ndarray,
    mean: np.ndarray,
    std: np.ndarray,
) -> np.ndarray:
    """Convert normalized values back to the original scale."""

    return (values * std + mean).astype(np.float32)


def load_normalization_stats(input_dir: str | Path) -> NormalizationStats:
    """Load normalization statistics from a saved training directory."""

    input_path = Path(input_dir)
    arrays = np.load(input_path / "normalization_stats.npz")
    return NormalizationStats(
        feature_mean=arrays["feature_mean"].astype(np.float32),
        feature_std=arrays["feature_std"].astype(np.float32),
        target_mean=arrays["target_mean"].astype(np.float32),
        target_std=arrays["target_std"].astype(np.float32),
    )


def save_normalization_stats(stats: NormalizationStats, output_dir: str | Path) -> None:
    """Persist normalization statistics to disk."""

    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        output_path / "normalization_stats.npz",
        feature_mean=stats.feature_mean,
        feature_std=stats.feature_std,
        target_mean=stats.target_mean,
        target_std=stats.target_std,
    )
    summary = {
        "feature_mean": stats.feature_mean.reshape(-1).astype(float).tolist(),
        "feature_std": stats.feature_std.reshape(-1).astype(float).tolist(),
        "target_mean": stats.target_mean.reshape(-1).astype(float).tolist(),
        "target_std": stats.target_std.reshape(-1).astype(float).tolist(),
    }
    (output_path / "normalization_stats.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def _time_range(metadata: pd.DataFrame) -> dict[str, str | None]:
    if metadata.empty:
        return {"start": None, "end": None}
    start = pd.to_datetime(metadata["target_start"], errors="coerce").min()
    end = pd.to_datetime(metadata["target_start"], errors="coerce").max()
    return {
        "start": start.isoformat() if pd.notna(start) else None,
        "end": end.isoformat() if pd.notna(end) else None,
    }


def save_split_summary(split: SampleSplit, output_dir: str | Path) -> None:
    """Save a human-readable split summary manifest."""

    summary = {
        "train_samples": int(len(split.train_metadata)),
        "val_samples": int(len(split.val_metadata)),
        "test_samples": int(len(split.test_metadata)),
        "train_sample_weight_mean": float(split.train_sample_weights.mean()) if split.train_sample_weights.size else 0.0,
        "val_sample_weight_mean": float(split.val_sample_weights.mean()) if split.val_sample_weights.size else 0.0,
        "test_sample_weight_mean": float(split.test_sample_weights.mean()) if split.test_sample_weights.size else 0.0,
        "train_transient_samples": int(split.train_metadata["is_transient"].sum())
        if "is_transient" in split.train_metadata
        else 0,
        "val_transient_samples": int(split.val_metadata["is_transient"].sum())
        if "is_transient" in split.val_metadata
        else 0,
        "test_transient_samples": int(split.test_metadata["is_transient"].sum())
        if "is_transient" in split.test_metadata
        else 0,
        "train_time_range": _time_range(split.train_metadata),
        "val_time_range": _time_range(split.val_metadata),
        "test_time_range": _time_range(split.test_metadata),
        "train_sources": split.train_metadata["source_file"].value_counts().to_dict(),
        "val_sources": split.val_metadata["source_file"].value_counts().to_dict(),
        "test_sources": split.test_metadata["source_file"].value_counts().to_dict(),
    }
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    (output_path / "split_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
