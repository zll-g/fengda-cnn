"""Build supervised samples from fixed train/val/test raw data directories."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from fengda_cnn.config import (  # noqa: E402
    DEFAULT_INPUT_STEPS,
    DEFAULT_INTERPOLATE_LIMIT,
    DEFAULT_OUTPUT_STEPS,
    DEFAULT_RESAMPLE_RULE,
)
from fengda_cnn.data.point_registry import FEATURE_COLUMNS, TARGET_COLUMNS  # noqa: E402
from fengda_cnn.data.sample_builder import (  # noqa: E402
    DEFAULT_DIFF_LAGS,
    DEFAULT_TRANSIENT_LAGS,
    build_supervised_samples,
    save_sample_dataset,
)
from fengda_cnn.data.standardization import (  # noqa: E402
    standardize_historian_export,
    summarize_standardized_frame,
)


SPLIT_NAMES = ("train", "val", "test")


def parse_int_list(raw_value: str) -> tuple[int, ...]:
    return tuple(int(item.strip()) for item in raw_value.split(",") if item.strip())


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-dir", default="data", help="Root directory containing train/val/test.")
    parser.add_argument(
        "--output-dir",
        default="artifacts/samples_split_1s_60_60_nox_o2_no2_transient_t015_stride1",
        help="Output directory containing train/val/test sample subdirectories.",
    )
    parser.add_argument("--pattern", default="*.csv", help="Glob pattern for raw files.")
    parser.add_argument("--input-steps", type=int, default=DEFAULT_INPUT_STEPS)
    parser.add_argument("--output-steps", type=int, default=DEFAULT_OUTPUT_STEPS)
    parser.add_argument("--stride", type=int, default=1)
    parser.add_argument("--resample-rule", default=DEFAULT_RESAMPLE_RULE)
    parser.add_argument("--interpolate-limit", type=int, default=DEFAULT_INTERPOLATE_LIMIT)
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
    parser.add_argument(
        "--transient-threshold",
        type=float,
        default=0.15,
        help="Normalized transient score threshold for marking a window transient.",
    )
    parser.add_argument(
        "--transient-weight-boost",
        type=float,
        default=3.0,
        help="Extra sample weight added to strong transient windows.",
    )
    parser.add_argument(
        "--allow-invalid-rows",
        action="store_true",
        help="Keep windows with interpolated rows, while still dropping NaN/inf windows.",
    )
    return parser.parse_args()


def build_split(
    split_name: str,
    input_dir: Path,
    output_dir: Path,
    pattern: str,
    input_steps: int,
    output_steps: int,
    stride: int,
    resample_rule: str,
    interpolate_limit: int,
    require_valid_rows: bool,
    include_input_diffs: bool,
    diff_lags: tuple[int, ...],
    include_target_history: bool,
    transient_lags: tuple[int, ...],
    transient_threshold: float,
    transient_weight_boost: float,
) -> dict[str, object]:
    split_input_dir = input_dir / split_name
    if not split_input_dir.exists():
        raise FileNotFoundError(f"Missing split directory: {split_input_dir}")

    csv_paths = sorted(split_input_dir.glob(pattern))
    if not csv_paths:
        raise FileNotFoundError(f"No files matched {pattern!r} in {split_input_dir}")

    standardized_dir = output_dir / "standardized" / split_name
    standardized_dir.mkdir(parents=True, exist_ok=True)
    frames: list[pd.DataFrame] = []
    files_manifest: list[dict[str, object]] = []

    for csv_path in csv_paths:
        standardized = standardize_historian_export(
            csv_path,
            resample_rule=resample_rule,
            interpolate_limit=interpolate_limit,
        )
        standardized_path = standardized_dir / f"{csv_path.stem}.standardized.csv"
        standardized.to_csv(standardized_path, index=False, encoding="utf-8-sig")
        frames.append(standardized)
        files_manifest.append(
            {
                "source_file": csv_path.name,
                "standardized_file": str(standardized_path.relative_to(output_dir)),
                **summarize_standardized_frame(standardized),
            }
        )
        print(
            f"[{split_name}] standardized {csv_path.name} -> "
            f"{standardized_path.relative_to(output_dir)} ({len(standardized)} rows)"
        )

    combined = pd.concat(frames, ignore_index=True)
    combined_path = standardized_dir / f"{split_name}.all_standardized.csv"
    combined.to_csv(combined_path, index=False, encoding="utf-8-sig")

    dataset = build_supervised_samples(
        frame=combined,
        input_steps=input_steps,
        output_steps=output_steps,
        stride=stride,
        feature_columns=FEATURE_COLUMNS,
        target_columns=TARGET_COLUMNS,
        require_valid_rows=require_valid_rows,
        include_input_diffs=include_input_diffs,
        diff_lags=diff_lags,
        include_target_history=include_target_history,
        transient_lags=transient_lags,
        transient_threshold=transient_threshold,
        transient_weight_boost=transient_weight_boost,
    )
    save_sample_dataset(dataset, output_dir / split_name)
    print(f"[{split_name}] X={dataset.features.shape}, y={dataset.targets.shape}")

    return {
        "split": split_name,
        "source_files": [path.name for path in csv_paths],
        "standardized_rows": int(len(combined)),
        "sample_count": int(dataset.features.shape[0]),
        "feature_columns": dataset.feature_columns,
        "target_columns": dataset.target_columns,
        "transient_sample_count": int(dataset.metadata["is_transient"].sum()) if not dataset.metadata.empty else 0,
        "sample_weight_mean": float(dataset.sample_weights.mean()) if dataset.sample_weights.size else 0.0,
        "sample_weight_max": float(dataset.sample_weights.max()) if dataset.sample_weights.size else 0.0,
        "files": files_manifest,
    }


def main() -> None:
    args = parse_args()
    input_dir = (ROOT / args.input_dir).resolve()
    output_dir = (ROOT / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    diff_lags = parse_int_list(args.diff_lags)
    transient_lags = parse_int_list(args.transient_lags)
    include_input_diffs = not args.no_input_diffs
    include_target_history = not args.no_target_history

    manifest = {
        "input_dir": str(input_dir),
        "output_dir": str(output_dir),
        "input_steps": args.input_steps,
        "output_steps": args.output_steps,
        "stride": args.stride,
        "resample_rule": args.resample_rule,
        "interpolate_limit": args.interpolate_limit,
        "require_valid_rows": not args.allow_invalid_rows,
        "base_feature_columns": FEATURE_COLUMNS,
        "target_columns": TARGET_COLUMNS,
        "include_input_diffs": include_input_diffs,
        "diff_lags": list(diff_lags),
        "include_target_history": include_target_history,
        "transient_lags": list(transient_lags),
        "transient_threshold": args.transient_threshold,
        "transient_weight_boost": args.transient_weight_boost,
        "splits": [],
    }

    for split_name in SPLIT_NAMES:
        split_summary = build_split(
            split_name=split_name,
            input_dir=input_dir,
            output_dir=output_dir,
            pattern=args.pattern,
            input_steps=args.input_steps,
            output_steps=args.output_steps,
            stride=args.stride,
            resample_rule=args.resample_rule,
            interpolate_limit=args.interpolate_limit,
            require_valid_rows=not args.allow_invalid_rows,
            include_input_diffs=include_input_diffs,
            diff_lags=diff_lags,
            include_target_history=include_target_history,
            transient_lags=transient_lags,
            transient_threshold=args.transient_threshold,
            transient_weight_boost=args.transient_weight_boost,
        )
        manifest["splits"].append(split_summary)

    (output_dir / "fixed_split_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(f"[done] samples written to {output_dir}")


if __name__ == "__main__":
    main()
