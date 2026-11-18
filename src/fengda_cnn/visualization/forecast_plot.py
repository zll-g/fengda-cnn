"""Matplotlib-based forecast visualizations."""

from __future__ import annotations

from pathlib import Path

import pandas as pd

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
except ImportError:  # pragma: no cover
    plt = None


def _require_matplotlib() -> None:
    if plt is None:
        raise ImportError("matplotlib is required to generate forecast plots.")


TARGET_PLOT_META = {
    "stack_nox_mg_m3": ("NOx", "mg/m3"),
    "stack_so2_mg_m3": ("SO2", "mg/m3"),
    "stack_o2_pct": ("O2", "%"),
    "stack_dust_mg_m3": ("Dust", "mg/m3"),
    "stack_no2_mg_m3": ("NO2", "mg/m3"),
}


def _detect_targets(forecast_frame: pd.DataFrame) -> list[str]:
    targets: list[str] = []
    for column in forecast_frame.columns:
        if not column.endswith("_actual"):
            continue
        target_name = column.removesuffix("_actual")
        if f"{target_name}_pred" in forecast_frame.columns:
            targets.append(target_name)
    if not targets:
        raise ValueError("No forecast target columns were found in forecast_frame.")
    return targets


def plot_forecast_curves(
    forecast_frame: pd.DataFrame,
    output_path: str | Path,
    title_prefix: str = "Forecast",
) -> None:
    """Plot actual vs predicted curves for all configured forecast targets."""

    _require_matplotlib()
    output_file = Path(output_path)
    target_columns = _detect_targets(forecast_frame)

    fig_height = max(3.2 * len(target_columns), 6.0)
    fig, axes = plt.subplots(len(target_columns), 1, figsize=(14, fig_height), sharex=True)
    if len(target_columns) == 1:
        axes = [axes]

    for index, target_name in enumerate(target_columns):
        axis = axes[index]
        label, unit = TARGET_PLOT_META.get(target_name, (target_name, ""))
        axis.plot(
            forecast_frame["timestamp"],
            forecast_frame[f"{target_name}_actual"],
            label=f"{label} Actual",
            linewidth=1.8,
        )
        axis.plot(
            forecast_frame["timestamp"],
            forecast_frame[f"{target_name}_pred"],
            label=f"{label} Pred",
            linewidth=1.6,
            linestyle="--",
        )
        axis.set_ylabel(f"{label} ({unit})" if unit else label)
        axis.set_title(f"{title_prefix} - {label}")
        axis.grid(alpha=0.3)
        axis.legend()

    axes[-1].set_xlabel("Timestamp")

    fig.autofmt_xdate()
    fig.tight_layout()
    fig.savefig(output_file, dpi=160, bbox_inches="tight")
    plt.close(fig)


def plot_attention_weights(
    attention_frame: pd.DataFrame,
    output_path: str | Path,
    title: str = "Input Attention Weights",
) -> None:
    """Plot attention weights across the input window."""

    _require_matplotlib()
    output_file = Path(output_path)

    fig, ax = plt.subplots(figsize=(14, 4.5))
    ax.plot(attention_frame["timestamp"], attention_frame["attention_weight"], linewidth=1.8)
    ax.set_title(title)
    ax.set_ylabel("Attention Weight")
    ax.set_xlabel("Timestamp")
    ax.grid(alpha=0.3)
    fig.autofmt_xdate()
    fig.tight_layout()
    fig.savefig(output_file, dpi=160, bbox_inches="tight")
    plt.close(fig)
