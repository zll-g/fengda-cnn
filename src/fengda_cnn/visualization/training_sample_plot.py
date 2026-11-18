"""Visualization helpers for exported training sample examples."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd

try:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
except ImportError:  # pragma: no cover
    plt = None


TARGET_PLOT_META = {
    "stack_nox_mg_m3": ("NOx", "mg/m3"),
    "stack_so2_mg_m3": ("SO2", "mg/m3"),
    "stack_o2_pct": ("O2", "%"),
    "stack_dust_mg_m3": ("Dust", "mg/m3"),
    "stack_no2_mg_m3": ("NO2", "mg/m3"),
}


def _require_matplotlib() -> None:
    if plt is None:
        raise ImportError("matplotlib is required to generate training sample plots.")


def _normalize_for_heatmap(values: np.ndarray) -> np.ndarray:
    feature_mean = values.mean(axis=0, keepdims=True)
    feature_std = values.std(axis=0, keepdims=True)
    feature_std = np.where(feature_std < 1e-6, 1.0, feature_std)
    normalized = (values - feature_mean) / feature_std
    return normalized.T


def plot_training_sample(
    input_frame: pd.DataFrame,
    target_frame: pd.DataFrame,
    feature_columns: list[str],
    target_columns: list[str],
    output_path: str | Path,
    title: str,
) -> None:
    """Render one complete training sample into a single summary figure."""

    _require_matplotlib()
    output_file = Path(output_path)

    heatmap_values = _normalize_for_heatmap(input_frame[feature_columns].to_numpy(dtype=float))
    fig, axes = plt.subplots(
        1 + len(target_columns),
        1,
        figsize=(16, 20),
        sharex=False,
        gridspec_kw={"height_ratios": [3.2] + [1.2] * len(target_columns)},
    )

    heatmap_axis = axes[0]
    image = heatmap_axis.imshow(
        heatmap_values,
        aspect="auto",
        cmap="coolwarm",
        interpolation="nearest",
    )
    heatmap_axis.set_title(f"{title} - Input Feature Heatmap")
    heatmap_axis.set_ylabel("Feature")
    heatmap_axis.set_yticks(np.arange(len(feature_columns)))
    heatmap_axis.set_yticklabels(feature_columns, fontsize=7)
    heatmap_axis.set_xticks(np.arange(len(input_frame)))
    heatmap_axis.set_xticklabels(
        input_frame["timestamp"].dt.strftime("%H:%M:%S"),
        rotation=90,
        fontsize=7,
    )
    heatmap_axis.set_xlabel("Input Window Timestamp")
    colorbar = fig.colorbar(image, ax=heatmap_axis, fraction=0.02, pad=0.01)
    colorbar.set_label("Normalized Value")

    for axis, target_name in zip(axes[1:], target_columns, strict=True):
        label, unit = TARGET_PLOT_META.get(target_name, (target_name, ""))
        axis.plot(
            target_frame["timestamp"],
            target_frame[target_name],
            linewidth=1.8,
            color="#1f77b4",
        )
        axis.set_title(f"Target Window - {label}")
        axis.set_ylabel(f"{label} ({unit})" if unit else label)
        axis.grid(alpha=0.3)
        axis.tick_params(axis="x", rotation=30, labelsize=8)
        axis.tick_params(axis="y", labelsize=8)

    axes[-1].set_xlabel("Target Window Timestamp")
    fig.tight_layout()
    fig.savefig(output_file, dpi=160, bbox_inches="tight")
    plt.close(fig)
