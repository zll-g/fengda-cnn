"""CLI script to build supervised training samples from standardized data."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from fengda_cnn.config import DEFAULT_INPUT_STEPS, DEFAULT_OUTPUT_STEPS  # noqa: E402
from fengda_cnn.data.sample_builder import (  # noqa: E402
    DEFAULT_DIFF_LAGS,
    DEFAULT_TRANSIENT_LAGS,
    build_supervised_samples,
    save_sample_dataset,
)


def parse_source_file_list(raw_values: list[str]) -> list[str]:
    names: list[str] = []
    for raw_value in raw_values:
        parts = [part.strip() for part in raw_value.split(",")]
        names.extend(part for part in parts if part)
    return names


def parse_int_list(raw_value: str) -> tuple[int, ...]:
    return tuple(int(item.strip()) for item in raw_value.split(",") if item.strip())


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--input-file",
        default="artifacts/standardized/all_standardized.csv",
        help="Combined standardized CSV file.",
    )
    parser.add_argument(
        "--output-dir",
        default="artifacts/samples",
        help="Directory for training sample outputs.",
    )
    parser.add_argument("--input-steps", type=int, default=DEFAULT_INPUT_STEPS)
    parser.add_argument("--output-steps", type=int, default=DEFAULT_OUTPUT_STEPS)
    parser.add_argument("--stride", type=int, default=1)
    parser.add_argument(
        "--no-input-diffs",
        action="store_true",
        help="Disable dynamic input difference features.",
    )
    parser.add_argument(
        "--diff-lags",
        default=",".join(str(lag) for lag in DEFAULT_DIFF_LAGS),
        help="Comma-separated lag seconds used for input difference features.",
    )
    parser.add_argument(
        "--no-target-history",
        action="store_true",
        help="Disable historical target values as model input features.",
    )
    parser.add_argument(
        "--transient-lags",
        default=",".join(str(lag) for lag in DEFAULT_TRANSIENT_LAGS),
        help="Comma-separated lag seconds used to score transient samples.",
    )
    parser.add_argument("--transient-threshold", type=float, default=0.15)
    parser.add_argument("--transient-weight-boost", type=float, default=3.0)
    parser.add_argument(
        "--exclude-source-files",
        nargs="*",
        default=[],
        help="One or more source_file names to exclude. Supports comma-separated values.",
    )
    parser.add_argument(
        "--allow-invalid-rows",
        action="store_true",
        help="Keep windows that contain rows marked invalid.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    input_path = (ROOT / args.input_file).resolve()
    output_dir = (ROOT / args.output_dir).resolve()

    frame = pd.read_csv(input_path)
    excluded_source_files = parse_source_file_list(args.exclude_source_files)
    if excluded_source_files:
        original_rows = len(frame)
        frame = frame.loc[~frame["source_file"].isin(excluded_source_files)].copy()
        print(
            "[filter] excluded source files="
            f"{excluded_source_files}; rows {original_rows} -> {len(frame)}"
        )

    dataset = build_supervised_samples(
        frame=frame,
        input_steps=args.input_steps,
        output_steps=args.output_steps,
        stride=args.stride,
        require_valid_rows=not args.allow_invalid_rows,
        include_input_diffs=not args.no_input_diffs,
        diff_lags=parse_int_list(args.diff_lags),
        include_target_history=not args.no_target_history,
        transient_lags=parse_int_list(args.transient_lags),
        transient_threshold=args.transient_threshold,
        transient_weight_boost=args.transient_weight_boost,
    )
    save_sample_dataset(dataset, output_dir)

    print(f"[samples] X shape={dataset.features.shape}")
    print(f"[samples] y shape={dataset.targets.shape}")
    print(f"[samples] metadata rows={len(dataset.metadata)}")


if __name__ == "__main__":
    main()
