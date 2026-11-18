"""Train the CNN-LSTM-Attention forecast model from prepared samples."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from fengda_cnn.models import CNNLSTMAttentionConfig  # noqa: E402
from fengda_cnn.training import (  # noqa: E402
    LoadedSampleDataset,
    SampleSplit,
    apply_normalization,
    compute_normalization_stats,
    load_sample_dataset,
    save_normalization_stats,
    save_split_summary,
    split_samples_chronologically,
)
from fengda_cnn.training.trainer import (  # noqa: E402
    TrainConfig,
    torch_available,
    train_forecast_model,
)


SPLIT_NAMES = ("train", "val", "test")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--samples-dir",
        default="artifacts/samples",
        help="Directory containing samples.npz, schema.json, and sample_metadata.csv.",
    )
    parser.add_argument(
        "--output-dir",
        default="artifacts/training_run",
        help="Directory for training outputs.",
    )
    parser.add_argument("--train-ratio", type=float, default=0.7)
    parser.add_argument("--val-ratio", type=float, default=0.15)
    parser.add_argument("--test-ratio", type=float, default=0.15)
    parser.add_argument("--epochs", type=int, default=30)
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--learning-rate", type=float, default=1e-3)
    parser.add_argument("--weight-decay", type=float, default=1e-4)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--loss-name", default="huber", choices=["huber", "mse"])
    parser.add_argument(
        "--trend-loss-weight",
        type=float,
        default=0.25,
        help="Weight for first-difference trend loss on the forecast trajectory.",
    )
    parser.add_argument(
        "--ignore-sample-weights",
        action="store_true",
        help="Ignore transient sample weights stored with the dataset.",
    )
    parser.add_argument("--early-stopping-patience", type=int, default=8)
    parser.add_argument("--scheduler-name", default="none", choices=["none", "plateau"])
    parser.add_argument("--scheduler-factor", type=float, default=0.5)
    parser.add_argument("--scheduler-patience", type=int, default=2)
    parser.add_argument("--min-learning-rate", type=float, default=1e-5)
    parser.add_argument(
        "--selection-metric",
        default="val_loss",
        help="Validation metric used for checkpoint selection and early stopping.",
    )
    parser.add_argument(
        "--target-loss-weights",
        default="",
        help="Comma-separated per-target loss weights, matching the target column order.",
    )
    parser.add_argument(
        "--skip-onnx-export",
        action="store_true",
        help="Do not export best_model.onnx after training.",
    )
    parser.add_argument("--onnx-opset", type=int, default=17)
    parser.add_argument("--conv-channels-1", type=int, default=64)
    parser.add_argument("--conv-channels-2", type=int, default=128)
    parser.add_argument("--kernel-size", type=int, default=3)
    parser.add_argument("--lstm-hidden-size", type=int, default=128)
    parser.add_argument("--dense-hidden-size", type=int, default=256)
    parser.add_argument("--conv-dropout", type=float, default=0.2)
    parser.add_argument("--lstm-dropout", type=float, default=0.2)
    parser.add_argument("--dense-dropout", type=float, default=0.3)
    parser.add_argument(
        "--prepare-only",
        action="store_true",
        help="Only prepare split and normalization artifacts without running torch training.",
    )
    return parser.parse_args()


def parse_target_loss_weights(raw_value: str, expected_size: int) -> tuple[float, ...] | None:
    if not raw_value.strip():
        return None

    weights = tuple(float(item.strip()) for item in raw_value.split(",") if item.strip())
    if len(weights) != expected_size:
        raise ValueError(
            "target-loss-weights size mismatch: "
            f"expected {expected_size}, got {len(weights)}"
        )
    return weights


def has_fixed_split_samples(samples_dir: Path) -> bool:
    return all((samples_dir / split_name / "samples.npz").exists() for split_name in SPLIT_NAMES)


def validate_split_schema(split_datasets: dict[str, LoadedSampleDataset]) -> None:
    first = split_datasets[SPLIT_NAMES[0]]
    for split_name in SPLIT_NAMES[1:]:
        current = split_datasets[split_name]
        if current.feature_columns != first.feature_columns:
            raise ValueError(f"{split_name} feature schema does not match train schema.")
        if current.target_columns != first.target_columns:
            raise ValueError(f"{split_name} target schema does not match train schema.")


def add_configured_split(metadata: pd.DataFrame, split_name: str) -> pd.DataFrame:
    enriched = metadata.copy()
    enriched["configured_split"] = split_name
    return enriched


def load_fixed_split_samples(samples_dir: Path) -> tuple[LoadedSampleDataset, SampleSplit]:
    split_datasets = {
        split_name: load_sample_dataset(samples_dir / split_name)
        for split_name in SPLIT_NAMES
    }
    validate_split_schema(split_datasets)

    train = split_datasets["train"]
    val = split_datasets["val"]
    test = split_datasets["test"]
    metadata = pd.concat(
        [
            add_configured_split(train.metadata, "train"),
            add_configured_split(val.metadata, "val"),
            add_configured_split(test.metadata, "test"),
        ],
        ignore_index=True,
    )
    dataset = LoadedSampleDataset(
        features=np.concatenate([train.features, val.features, test.features], axis=0),
        targets=np.concatenate([train.targets, val.targets, test.targets], axis=0),
        sample_weights=np.concatenate([train.sample_weights, val.sample_weights, test.sample_weights], axis=0),
        metadata=metadata,
        feature_columns=train.feature_columns,
        target_columns=train.target_columns,
    )
    split = SampleSplit(
        train_features=train.features,
        train_targets=train.targets,
        train_sample_weights=train.sample_weights,
        train_metadata=add_configured_split(train.metadata, "train"),
        val_features=val.features,
        val_targets=val.targets,
        val_sample_weights=val.sample_weights,
        val_metadata=add_configured_split(val.metadata, "val"),
        test_features=test.features,
        test_targets=test.targets,
        test_sample_weights=test.sample_weights,
        test_metadata=add_configured_split(test.metadata, "test"),
    )
    return dataset, split


def load_dataset_and_split(args: argparse.Namespace) -> tuple[LoadedSampleDataset, SampleSplit, str]:
    samples_dir = (ROOT / args.samples_dir).resolve()
    if has_fixed_split_samples(samples_dir):
        dataset, split = load_fixed_split_samples(samples_dir)
        return dataset, split, "fixed_directory"

    dataset = load_sample_dataset(samples_dir)
    split = split_samples_chronologically(
        dataset=dataset,
        train_ratio=args.train_ratio,
        val_ratio=args.val_ratio,
        test_ratio=args.test_ratio,
    )
    return dataset, split, "chronological_ratio"


def main() -> None:
    args = parse_args()
    output_dir = (ROOT / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    dataset, split, split_mode = load_dataset_and_split(args)
    save_split_summary(split, output_dir)

    norm_stats = compute_normalization_stats(
        train_features=split.train_features,
        train_targets=split.train_targets,
    )
    save_normalization_stats(norm_stats, output_dir)

    x_train = apply_normalization(split.train_features, norm_stats.feature_mean, norm_stats.feature_std)
    y_train = apply_normalization(split.train_targets, norm_stats.target_mean, norm_stats.target_std)
    x_val = apply_normalization(split.val_features, norm_stats.feature_mean, norm_stats.feature_std)
    y_val = apply_normalization(split.val_targets, norm_stats.target_mean, norm_stats.target_std)
    x_test = apply_normalization(split.test_features, norm_stats.feature_mean, norm_stats.feature_std)
    y_test = apply_normalization(split.test_targets, norm_stats.target_mean, norm_stats.target_std)

    prep_summary = {
        "x_train_shape": list(x_train.shape),
        "y_train_shape": list(y_train.shape),
        "x_val_shape": list(x_val.shape),
        "y_val_shape": list(y_val.shape),
        "x_test_shape": list(x_test.shape),
        "y_test_shape": list(y_test.shape),
        "feature_columns": dataset.feature_columns,
        "target_columns": dataset.target_columns,
        "split_mode": split_mode,
        "sample_weight": {
            "enabled": not args.ignore_sample_weights,
            "train_mean": float(split.train_sample_weights.mean()) if split.train_sample_weights.size else 0.0,
            "train_max": float(split.train_sample_weights.max()) if split.train_sample_weights.size else 0.0,
            "val_mean": float(split.val_sample_weights.mean()) if split.val_sample_weights.size else 0.0,
            "test_mean": float(split.test_sample_weights.mean()) if split.test_sample_weights.size else 0.0,
        },
    }
    (output_dir / "prepared_data_summary.json").write_text(
        json.dumps(prep_summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    print(f"[prep] train={x_train.shape}, val={x_val.shape}, test={x_test.shape}")

    if args.prepare_only or not torch_available():
        if not torch_available():
            print("[train] PyTorch not installed; preprocessing artifacts prepared, training skipped.")
        else:
            print("[train] prepare-only enabled; training skipped.")
        return

    model_config = CNNLSTMAttentionConfig(
        input_size=x_train.shape[-1],
        output_steps=y_train.shape[1],
        output_size=y_train.shape[2],
        conv_channels_1=args.conv_channels_1,
        conv_channels_2=args.conv_channels_2,
        kernel_size=args.kernel_size,
        lstm_hidden_size=args.lstm_hidden_size,
        dense_hidden_size=args.dense_hidden_size,
        conv_dropout=args.conv_dropout,
        lstm_dropout=args.lstm_dropout,
        dense_dropout=args.dense_dropout,
    )
    target_loss_weights = parse_target_loss_weights(args.target_loss_weights, y_train.shape[2])
    train_config = TrainConfig(
        epochs=args.epochs,
        batch_size=args.batch_size,
        learning_rate=args.learning_rate,
        weight_decay=args.weight_decay,
        seed=args.seed,
        device=args.device,
        loss_name=args.loss_name,
        trend_loss_weight=args.trend_loss_weight,
        early_stopping_patience=args.early_stopping_patience,
        scheduler_name=args.scheduler_name,
        scheduler_factor=args.scheduler_factor,
        scheduler_patience=args.scheduler_patience,
        min_learning_rate=args.min_learning_rate,
        target_loss_weights=target_loss_weights,
        selection_metric=args.selection_metric,
        export_onnx=not args.skip_onnx_export,
        onnx_opset=args.onnx_opset,
    )

    summary = train_forecast_model(
        train_features=x_train,
        train_targets=y_train,
        train_sample_weights=None if args.ignore_sample_weights else split.train_sample_weights,
        val_features=x_val,
        val_targets=y_val,
        val_sample_weights=None if args.ignore_sample_weights else split.val_sample_weights,
        test_features=x_test,
        test_targets=y_test,
        test_sample_weights=None if args.ignore_sample_weights else split.test_sample_weights,
        model_config=model_config,
        train_config=train_config,
        target_mean=norm_stats.target_mean,
        target_std=norm_stats.target_std,
        target_names=dataset.target_columns,
        output_dir=output_dir,
    )
    print(f"[train] completed: {summary}")


if __name__ == "__main__":
    main()
