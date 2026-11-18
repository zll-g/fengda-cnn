"""Standardization pipeline for historian exports."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd

from fengda_cnn.config import DEFAULT_INTERPOLATE_LIMIT, DEFAULT_RESAMPLE_RULE, STAGE_CODES

from .historian_csv import HistorianExport, parse_historian_csv
from .point_registry import FIELD_NAMES, POINT_REGISTRY, POINT_SPECS


def _infer_run_stage(frame: pd.DataFrame) -> pd.Series:
    gas_flow = frame["natural_gas_inlet_flow_m3_h"].fillna(0.0)
    ignition = frame["ignition_solenoid_active"].fillna(0.0)
    steam_pressure = frame["steam_pressure_mpa"].fillna(0.0)
    steam_flow = frame["steam_flow_tph"].fillna(0.0)
    steam_temp = frame["steam_temp_c"].fillna(0.0)

    conditions = [
        (gas_flow <= 1.0) & (steam_pressure <= 0.1) & (steam_flow <= 0.5),
        (ignition >= 0.5) | ((gas_flow > 1.0) & (steam_pressure < 0.5)),
        (steam_pressure < 1.0) | (steam_temp < 250.0) | (steam_flow < 4.0),
    ]
    stages = np.select(
        conditions,
        ["shutdown", "ignition", "ramp_up"],
        default="steady_run",
    )
    return pd.Series(stages, index=frame.index)


def _rename_columns(raw_frame: pd.DataFrame) -> pd.DataFrame:
    rename_map = {}
    keep_columns = ["timestamp"]
    for spec in POINT_SPECS:
        if spec.point_name in raw_frame.columns:
            rename_map[spec.point_name] = spec.field_name
            keep_columns.append(spec.point_name)
    standardized = raw_frame[keep_columns].rename(columns=rename_map).copy()
    if standardized.columns.duplicated().any():
        collapsed_columns = {}
        for column in dict.fromkeys(standardized.columns):
            matching_columns = standardized.loc[:, standardized.columns == column]
            collapsed_columns[column] = matching_columns.bfill(axis=1).iloc[:, 0]
        standardized = pd.DataFrame(collapsed_columns)
    for field_name in FIELD_NAMES:
        if field_name not in standardized.columns:
            standardized[field_name] = np.nan
    return standardized[["timestamp"] + FIELD_NAMES]


def standardize_export(
    export: HistorianExport,
    resample_rule: str = DEFAULT_RESAMPLE_RULE,
    interpolate_limit: int = DEFAULT_INTERPOLATE_LIMIT,
) -> pd.DataFrame:
    """Convert a parsed export into a business-ready dataframe."""

    base = _rename_columns(export.data)
    base = base.sort_values("timestamp").set_index("timestamp")

    agg_map = {spec.field_name: spec.agg for spec in POINT_SPECS}
    resampled = base.resample(resample_rule).agg(agg_map)
    missing_before_fill = resampled.isna().sum(axis=1)

    binary_columns = [spec.field_name for spec in POINT_SPECS if spec.is_binary]
    continuous_columns = [spec.field_name for spec in POINT_SPECS if not spec.is_binary]

    filled = resampled.copy()
    filled[continuous_columns] = filled[continuous_columns].interpolate(
        method="time",
        limit=interpolate_limit,
        limit_direction="both",
    )
    filled[binary_columns] = filled[binary_columns].fillna(0.0)

    filled["missing_values_before_fill"] = missing_before_fill
    filled["is_valid_row"] = missing_before_fill.eq(0)
    filled["run_stage"] = _infer_run_stage(filled)
    filled["run_stage_code"] = filled["run_stage"].map(STAGE_CODES).astype(int)
    filled["source_file"] = export.source_file
    filled["source_start_time"] = export.export_meta.get("Start Time", "")
    filled["source_end_time"] = export.export_meta.get("End Time", "")

    return filled.reset_index()


def standardize_historian_export(
    path: str | Path,
    resample_rule: str = DEFAULT_RESAMPLE_RULE,
    interpolate_limit: int = DEFAULT_INTERPOLATE_LIMIT,
) -> pd.DataFrame:
    """Parse and standardize one historian export file."""

    export = parse_historian_csv(path)
    return standardize_export(
        export=export,
        resample_rule=resample_rule,
        interpolate_limit=interpolate_limit,
    )


def summarize_standardized_frame(frame: pd.DataFrame) -> dict[str, object]:
    """Create a small summary for logging and manifests."""

    return {
        "rows": int(len(frame)),
        "valid_rows": int(frame["is_valid_row"].sum()),
        "valid_ratio": float(frame["is_valid_row"].mean()) if len(frame) else 0.0,
        "start_timestamp": frame["timestamp"].min().isoformat() if len(frame) else None,
        "end_timestamp": frame["timestamp"].max().isoformat() if len(frame) else None,
        "run_stage_counts": frame["run_stage"].value_counts(dropna=False).to_dict(),
    }
