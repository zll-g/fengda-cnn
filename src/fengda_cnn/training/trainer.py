"""PyTorch training loop for the CNN-LSTM-Attention forecast model."""

from __future__ import annotations

import csv
import json
import random
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Callable

import numpy as np

from .metrics import forecast_metrics

try:
    import torch
    from torch import nn
    from torch.utils.data import DataLoader, Dataset
except ImportError:  # pragma: no cover - local environment currently has no torch installed
    torch = None
    nn = None
    DataLoader = None
    Dataset = object

from fengda_cnn.models import CNNLSTMAttentionConfig, CNNLSTMAttentionModel
from .sample_io import invert_normalization


@dataclass
class TrainConfig:
    epochs: int = 30
    batch_size: int = 32
    learning_rate: float = 1e-3
    weight_decay: float = 1e-4
    seed: int = 42
    device: str = "auto"
    early_stopping_patience: int = 8
    gradient_clip_norm: float = 1.0
    loss_name: str = "huber"
    trend_loss_weight: float = 0.25
    scheduler_name: str = "none"
    scheduler_factor: float = 0.5
    scheduler_patience: int = 2
    min_learning_rate: float = 1e-5
    target_loss_weights: tuple[float, ...] | None = None
    selection_metric: str = "val_loss"
    export_onnx: bool = True
    onnx_opset: int = 17


class TrainingCancelledError(RuntimeError):
    """Raised when an external caller requests a cooperative training stop."""


if torch is not None:

    class SequenceDataset(Dataset):
        """Torch dataset wrapping numpy arrays."""

        def __init__(
            self,
            features: np.ndarray,
            targets: np.ndarray,
            sample_weights: np.ndarray | None = None,
        ) -> None:
            self.features = torch.from_numpy(features)
            self.targets = torch.from_numpy(targets)
            if sample_weights is None:
                sample_weights = np.ones(features.shape[0], dtype=np.float32)
            self.sample_weights = torch.from_numpy(sample_weights.astype(np.float32).reshape(-1))

        def __len__(self) -> int:
            return int(self.features.shape[0])

        def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
            return self.features[index], self.targets[index], self.sample_weights[index]


    class WeightedForecastLoss(nn.Module):
        """Point-wise forecast loss with optional per-target weighting."""

        def __init__(
            self,
            loss_name: str,
            target_weights: tuple[float, ...] | None = None,
            trend_loss_weight: float = 0.0,
        ) -> None:
            super().__init__()
            self.trend_loss_weight = max(float(trend_loss_weight), 0.0)
            loss_name = loss_name.lower()
            if loss_name == "mse":
                self.base_loss = nn.MSELoss(reduction="none")
            elif loss_name == "huber":
                self.base_loss = nn.HuberLoss(delta=1.0, reduction="none")
            else:
                raise ValueError(f"Unsupported loss_name: {loss_name}")

            if target_weights is None:
                self.register_buffer("target_weights", torch.empty(0, dtype=torch.float32))
            else:
                weight_tensor = torch.tensor(target_weights, dtype=torch.float32)
                self.register_buffer("target_weights", weight_tensor)

        def _reduce_losses(
            self,
            losses: torch.Tensor,
            sample_weights: torch.Tensor | None,
        ) -> torch.Tensor:
            if self.target_weights.numel() == 0:
                per_sample = losses.mean(dim=(1, 2))
            else:
                weights = self.target_weights.view(1, 1, -1)
                weighted_losses = losses * weights
                per_sample = weighted_losses.sum(dim=(1, 2)) / (losses.shape[1] * weights.sum())

            if sample_weights is None:
                return per_sample.mean()

            weights = sample_weights.reshape(-1).to(per_sample.device).clamp_min(0.0)
            if weights.sum() <= 0:
                return per_sample.mean()

            return (per_sample * weights).sum() / weights.sum()

        def forward(
            self,
            predictions: torch.Tensor,
            targets: torch.Tensor,
            sample_weights: torch.Tensor | None = None,
        ) -> torch.Tensor:
            value_loss = self._reduce_losses(
                self.base_loss(predictions, targets),
                sample_weights,
            )
            if self.trend_loss_weight <= 0.0 or predictions.shape[1] < 2:
                return value_loss

            prediction_trend = predictions[:, 1:, :] - predictions[:, :-1, :]
            target_trend = targets[:, 1:, :] - targets[:, :-1, :]
            trend_loss = self._reduce_losses(
                self.base_loss(prediction_trend, target_trend),
                sample_weights,
            )
            return value_loss + (self.trend_loss_weight * trend_loss)


def torch_available() -> bool:
    """Return whether torch is installed in the current environment."""

    return torch is not None


def _resolve_device(device: str) -> "torch.device":
    if torch is None:
        raise ImportError("PyTorch is not installed.")
    if device == "auto":
        if torch.cuda.is_available():
            return torch.device("cuda")
        return torch.device("cpu")
    return torch.device(device)


def set_global_seed(seed: int) -> None:
    """Seed Python, numpy and torch for reproducibility."""

    random.seed(seed)
    np.random.seed(seed)
    if torch is not None:
        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(seed)


def _build_loss(
    loss_name: str,
    target_weights: tuple[float, ...] | None = None,
    trend_loss_weight: float = 0.0,
) -> "nn.Module":
    if nn is None:
        raise ImportError("PyTorch is not installed.")
    return WeightedForecastLoss(
        loss_name=loss_name,
        target_weights=target_weights,
        trend_loss_weight=trend_loss_weight,
    )


def _build_scheduler(
    optimizer: "torch.optim.Optimizer",
    train_config: TrainConfig,
) -> "torch.optim.lr_scheduler.ReduceLROnPlateau | None":
    if torch is None:
        raise ImportError("PyTorch is not installed.")

    scheduler_name = train_config.scheduler_name.lower()
    if scheduler_name == "none":
        return None
    if scheduler_name == "plateau":
        return torch.optim.lr_scheduler.ReduceLROnPlateau(
            optimizer,
            mode="min",
            factor=train_config.scheduler_factor,
            patience=train_config.scheduler_patience,
            min_lr=train_config.min_learning_rate,
        )
    raise ValueError(f"Unsupported scheduler_name: {train_config.scheduler_name}")


def _resolve_selection_score(
    selection_metric: str,
    val_loss: float,
    val_metrics: dict[str, float],
) -> float:
    if selection_metric == "val_loss":
        return val_loss
    if selection_metric not in val_metrics:
        raise ValueError(
            f"selection_metric '{selection_metric}' not found in validation metrics: "
            f"{sorted(val_metrics.keys())}"
        )
    return float(val_metrics[selection_metric])


def _evaluate(
    model: "CNNLSTMAttentionModel",
    loader: "DataLoader",
    criterion: "nn.Module",
    device: "torch.device",
    target_mean: np.ndarray,
    target_std: np.ndarray,
    target_names: list[str],
) -> tuple[float, dict[str, float]]:
    model.eval()
    total_loss = 0.0
    total_examples = 0
    preds: list[np.ndarray] = []
    trues: list[np.ndarray] = []

    with torch.no_grad():
        for batch_x, batch_y, batch_weight in loader:
            batch_x = batch_x.to(device)
            batch_y = batch_y.to(device)
            batch_weight = batch_weight.to(device)
            batch_pred, _ = model(batch_x)
            loss = criterion(batch_pred, batch_y, batch_weight)

            batch_size = batch_x.size(0)
            total_loss += float(loss.item()) * batch_size
            total_examples += batch_size

            preds.append(batch_pred.cpu().numpy())
            trues.append(batch_y.cpu().numpy())

    mean_loss = total_loss / max(total_examples, 1)
    if preds:
        y_pred = invert_normalization(np.concatenate(preds, axis=0), target_mean, target_std)
        y_true = invert_normalization(np.concatenate(trues, axis=0), target_mean, target_std)
        metrics = forecast_metrics(y_true=y_true, y_pred=y_pred, target_names=target_names)
    else:
        metrics = {}
    return mean_loss, metrics


def _export_best_model_to_onnx(
    model: "CNNLSTMAttentionModel",
    model_config: CNNLSTMAttentionConfig,
    input_steps: int,
    output_path: Path,
    device: "torch.device",
    opset_version: int,
) -> dict[str, object]:
    """Export the trained model to ONNX using the normalized model input shape."""

    onnx_path = output_path / "best_model.onnx"
    model.eval()
    dummy_input = torch.zeros(
        1,
        input_steps,
        model_config.input_size,
        dtype=torch.float32,
        device=device,
    )
    torch.onnx.export(
        model,
        dummy_input,
        str(onnx_path),
        export_params=True,
        opset_version=opset_version,
        do_constant_folding=True,
        input_names=["input"],
        output_names=["forecast", "attention_weights"],
        dynamic_axes={
            "input": {0: "batch"},
            "forecast": {0: "batch"},
            "attention_weights": {0: "batch"},
        },
        dynamo=False,
    )
    return {
        "onnx_model_path": str(onnx_path),
        "onnx_opset": opset_version,
        "onnx_input_shape": [1, input_steps, model_config.input_size],
        "onnx_outputs": ["forecast", "attention_weights"],
    }


def train_forecast_model(
    train_features: np.ndarray,
    train_targets: np.ndarray,
    train_sample_weights: np.ndarray | None,
    val_features: np.ndarray,
    val_targets: np.ndarray,
    val_sample_weights: np.ndarray | None,
    test_features: np.ndarray,
    test_targets: np.ndarray,
    test_sample_weights: np.ndarray | None,
    model_config: CNNLSTMAttentionConfig,
    train_config: TrainConfig,
    target_mean: np.ndarray,
    target_std: np.ndarray,
    target_names: list[str],
    output_dir: str | Path,
    progress_callback: Callable[[dict[str, object]], None] | None = None,
    should_stop: Callable[[], bool] | None = None,
) -> dict[str, object]:
    """Train the forecast model and persist checkpoints and logs."""

    if torch is None or nn is None:
        raise ImportError(
            "PyTorch is not installed. Install the optional dependency group `train` to run training."
        )

    train_loader = DataLoader(
        SequenceDataset(train_features, train_targets, train_sample_weights),
        batch_size=train_config.batch_size,
        shuffle=True,
    )
    val_loader = DataLoader(
        SequenceDataset(val_features, val_targets, val_sample_weights),
        batch_size=train_config.batch_size,
        shuffle=False,
    )
    test_loader = DataLoader(
        SequenceDataset(test_features, test_targets, test_sample_weights),
        batch_size=train_config.batch_size,
        shuffle=False,
    )

    return train_forecast_model_with_loaders(
        train_loader=train_loader,
        val_loader=val_loader,
        test_loader=test_loader,
        model_config=model_config,
        train_config=train_config,
        target_mean=target_mean,
        target_std=target_std,
        target_names=target_names,
        output_dir=output_dir,
        input_steps=train_features.shape[1],
        progress_callback=progress_callback,
        should_stop=should_stop,
    )


def train_forecast_model_with_loaders(
    train_loader: "DataLoader",
    val_loader: "DataLoader",
    test_loader: "DataLoader",
    model_config: CNNLSTMAttentionConfig,
    train_config: TrainConfig,
    target_mean: np.ndarray,
    target_std: np.ndarray,
    target_names: list[str],
    output_dir: str | Path,
    input_steps: int,
    progress_callback: Callable[[dict[str, object]], None] | None = None,
    should_stop: Callable[[], bool] | None = None,
) -> dict[str, object]:
    """Train with caller-provided loaders, including lazy or memory-mapped datasets."""

    if torch is None or nn is None:
        raise ImportError(
            "PyTorch is not installed. Install the optional dependency group `train` to run training."
        )

    set_global_seed(train_config.seed)
    device = _resolve_device(train_config.device)
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    model = CNNLSTMAttentionModel(model_config).to(device)
    optimizer = torch.optim.AdamW(
        model.parameters(),
        lr=train_config.learning_rate,
        weight_decay=train_config.weight_decay,
    )
    criterion = _build_loss(
        train_config.loss_name,
        target_weights=train_config.target_loss_weights,
        trend_loss_weight=train_config.trend_loss_weight,
    )
    scheduler = _build_scheduler(optimizer, train_config)

    history_rows: list[dict[str, float | int]] = []
    best_val_loss = float("inf")
    best_selection_score = float("inf")
    best_epoch = -1
    patience = 0

    for epoch in range(1, train_config.epochs + 1):
        model.train()
        total_train_loss = 0.0
        total_train_examples = 0

        for batch_x, batch_y, batch_weight in train_loader:
            if should_stop is not None and should_stop():
                raise TrainingCancelledError("训练已被用户停止。")
            batch_x = batch_x.to(device)
            batch_y = batch_y.to(device)
            batch_weight = batch_weight.to(device)

            optimizer.zero_grad()
            batch_pred, _ = model(batch_x)
            loss = criterion(batch_pred, batch_y, batch_weight)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), train_config.gradient_clip_norm)
            optimizer.step()

            batch_size = batch_x.size(0)
            total_train_loss += float(loss.item()) * batch_size
            total_train_examples += batch_size

        train_loss = total_train_loss / max(total_train_examples, 1)
        val_loss, val_metrics = _evaluate(
            model=model,
            loader=val_loader,
            criterion=criterion,
            device=device,
            target_mean=target_mean,
            target_std=target_std,
            target_names=target_names,
        )

        selection_score = _resolve_selection_score(
            train_config.selection_metric,
            val_loss,
            val_metrics,
        )

        if scheduler is not None:
            scheduler.step(selection_score)

        current_learning_rate = float(optimizer.param_groups[0]["lr"])
        history_row = {
            "epoch": epoch,
            "train_loss": train_loss,
            "val_loss": val_loss,
            "selection_score": selection_score,
            "learning_rate": current_learning_rate,
        }
        history_row.update(val_metrics)
        history_rows.append(history_row)
        if progress_callback is not None:
            progress_callback(
                {
                    "epoch": epoch,
                    "total_epochs": train_config.epochs,
                    "train_loss": train_loss,
                    "val_loss": val_loss,
                    "selection_score": selection_score,
                    "learning_rate": current_learning_rate,
                    "metrics": val_metrics,
                }
            )

        if selection_score < best_selection_score:
            best_val_loss = val_loss
            best_selection_score = selection_score
            best_epoch = epoch
            patience = 0
            torch.save(
                {
                    "model_state_dict": model.state_dict(),
                    "model_config": asdict(model_config),
                    "train_config": asdict(train_config),
                    "best_val_loss": best_val_loss,
                    "best_selection_score": best_selection_score,
                    "best_epoch": best_epoch,
                },
                output_path / "best_model.pt",
            )
        else:
            patience += 1

        if patience >= train_config.early_stopping_patience:
            break

    history_path = output_path / "history.csv"
    with history_path.open("w", newline="", encoding="utf-8-sig") as handle:
        if history_rows:
            writer = csv.DictWriter(handle, fieldnames=list(history_rows[0].keys()))
            writer.writeheader()
            writer.writerows(history_rows)
        else:
            handle.write("")

    checkpoint = torch.load(output_path / "best_model.pt", map_location=device)
    model.load_state_dict(checkpoint["model_state_dict"])
    test_loss, test_metrics = _evaluate(
        model=model,
        loader=test_loader,
        criterion=criterion,
        device=device,
        target_mean=target_mean,
        target_std=target_std,
        target_names=target_names,
    )

    summary = {
        "best_epoch": best_epoch,
        "best_val_loss": best_val_loss,
        "best_selection_metric": train_config.selection_metric,
        "best_selection_score": best_selection_score,
        "test_loss": test_loss,
        "epochs_ran": len(history_rows),
        "device": str(device),
    }
    summary.update({f"test_{key}": value for key, value in test_metrics.items()})
    if train_config.export_onnx:
        summary.update(
            _export_best_model_to_onnx(
                model=model,
                model_config=model_config,
                input_steps=input_steps,
                output_path=output_path,
                device=device,
                opset_version=train_config.onnx_opset,
            )
        )
    (output_path / "train_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return summary
