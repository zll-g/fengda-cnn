"""Parser for the historian CSV exports found in the workspace."""

from __future__ import annotations

import csv
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pandas as pd


FLOAT_PATTERN = re.compile(r"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


@dataclass
class HistorianExport:
    source_file: str
    export_meta: dict[str, str]
    point_meta: pd.DataFrame
    data: pd.DataFrame


def _canonical_point_name(value: str) -> str:
    value = value.strip()
    if value.startswith("(A) "):
        value = value[4:]
    return value


def _coerce_float(value: Any) -> float | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    match = FLOAT_PATTERN.search(text)
    if not match:
        return None
    try:
        return float(match.group(0))
    except ValueError:
        return None


def _parse_timestamp(series: pd.Series) -> pd.Series:
    parsed = pd.to_datetime(series, errors="coerce")
    if parsed.isna().any():
        fallback = pd.to_datetime(
            series,
            format="%m/%d/%Y %H:%M:%S.%f",
            errors="coerce",
        )
        parsed = parsed.fillna(fallback)
    if parsed.isna().any():
        fallback = pd.to_datetime(
            series,
            format="%m/%d/%Y %H:%M:%S",
            errors="coerce",
        )
        parsed = parsed.fillna(fallback)
    return parsed


def parse_historian_csv(path: str | Path) -> HistorianExport:
    """Parse a historian export CSV into metadata and time-series tables."""

    source_path = Path(path)
    with source_path.open(encoding="utf-8", errors="ignore", newline="") as handle:
        rows = list(csv.reader(handle))

    point_header_idx = next(
        idx for idx, row in enumerate(rows) if len(row) > 1 and row[1] == "Graph Visible"
    )
    data_header_idx = next(idx for idx, row in enumerate(rows) if row and row[0] == "Date Time")

    export_meta: dict[str, str] = {"raw_title": rows[0][0].lstrip("\ufeff") if rows and rows[0] else ""}
    for row in rows[:point_header_idx]:
        if not row:
            continue
        text = row[0].lstrip("\ufeff").strip()
        if " : " in text:
            key, value = text.split(" : ", 1)
            export_meta[key.strip()] = value.strip()
        elif "=" in text:
            key, value = text.split("=", 1)
            export_meta[key.strip()] = value.strip()

    point_meta_header = rows[point_header_idx]
    point_meta_rows = [row for row in rows[point_header_idx + 1 : data_header_idx] if row]
    point_meta = pd.DataFrame(point_meta_rows, columns=point_meta_header)
    if not point_meta.empty and "Point Name" in point_meta:
        point_meta["Point Name"] = point_meta["Point Name"].map(_canonical_point_name)

    data_header = rows[data_header_idx]
    data_rows = [row for row in rows[data_header_idx + 3 :] if row and row[0].strip()]
    data = pd.DataFrame(data_rows, columns=data_header)
    data = data.rename(columns={"Date Time": "timestamp"})
    data["timestamp"] = _parse_timestamp(data["timestamp"])
    data = data.dropna(subset=["timestamp"]).sort_values("timestamp").reset_index(drop=True)

    for column in data.columns:
        if column == "timestamp":
            continue
        data[column] = data[column].map(_coerce_float)

    return HistorianExport(
        source_file=source_path.name,
        export_meta=export_meta,
        point_meta=point_meta,
        data=data,
    )
