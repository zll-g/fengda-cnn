"""Forecast evaluation metrics."""

from __future__ import annotations

import math

import numpy as np


SPECIAL_SUMMARY_TARGETS = {
    "stack_nox_mg_m3": ("peak", "peak_nox_mae"),
    "stack_so2_mg_m3": ("peak", "peak_so2_mae"),
    "stack_o2_pct": ("min", "min_o2_mae"),
    "stack_dust_mg_m3": ("peak", "peak_dust_mae"),
    "stack_no2_mg_m3": ("peak", "peak_no2_mae"),
}


def _safe_mean(values: np.ndarray) -> float:
    return float(values.mean()) if values.size else 0.0


def forecast_metrics(
    y_true: np.ndarray,
    y_pred: np.ndarray,
    target_names: list[str] | None = None,
) -> dict[str, float]:
    """Compute MAE/RMSE metrics for multi-step pollutant forecasts."""

    if y_true.shape != y_pred.shape:
        raise ValueError(f"Shape mismatch: y_true={y_true.shape}, y_pred={y_pred.shape}")

    target_names = target_names or [f"target_{idx}" for idx in range(y_true.shape[-1])]
    errors = y_pred - y_true
    abs_errors = np.abs(errors)
    squared_errors = errors**2

    metrics: dict[str, float] = {
        "overall_mae": _safe_mean(abs_errors),
        "overall_rmse": math.sqrt(_safe_mean(squared_errors)),
    }

    for idx, name in enumerate(target_names):
        target_abs = abs_errors[..., idx]
        target_sq = squared_errors[..., idx]
        metrics[f"{name}_mae"] = _safe_mean(target_abs)
        metrics[f"{name}_rmse"] = math.sqrt(_safe_mean(target_sq))

    for idx, name in enumerate(target_names):
        summary_spec = SPECIAL_SUMMARY_TARGETS.get(name)
        if summary_spec is None:
            continue

        reducer, metric_name = summary_spec
        if reducer == "peak":
            reduced_true = y_true[..., idx].max(axis=1)
            reduced_pred = y_pred[..., idx].max(axis=1)
        else:
            reduced_true = y_true[..., idx].min(axis=1)
            reduced_pred = y_pred[..., idx].min(axis=1)
        metrics[metric_name] = _safe_mean(np.abs(reduced_pred - reduced_true))
    return metrics
