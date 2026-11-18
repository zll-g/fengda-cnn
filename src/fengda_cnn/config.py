"""Project-level configuration constants."""

from __future__ import annotations

DEFAULT_RESAMPLE_RULE = "1s"
DEFAULT_INPUT_WINDOW_SECONDS = 60
DEFAULT_FORECAST_HORIZON_SECONDS = 60
DEFAULT_INPUT_STEPS = 60
DEFAULT_OUTPUT_STEPS = 60
DEFAULT_SAMPLE_STRIDE = 1
DEFAULT_INTERPOLATE_LIMIT = 20

TARGET_COLUMNS = [
    "stack_nox_mg_m3",
    "stack_o2_pct",
    "stack_no2_mg_m3",
]

QUALITY_COLUMNS = [
    "missing_values_before_fill",
    "is_valid_row",
]

STAGE_CODES = {
    "shutdown": 0,
    "ignition": 1,
    "ramp_up": 2,
    "steady_run": 3,
}
