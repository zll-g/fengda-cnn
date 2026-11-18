"""CLI script to parse raw historian CSV files and standardize them to 1-second frames."""

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

from fengda_cnn.data.standardization import (  # noqa: E402
    standardize_historian_export,
    summarize_standardized_frame,
)
from fengda_cnn.config import DEFAULT_INTERPOLATE_LIMIT, DEFAULT_RESAMPLE_RULE  # noqa: E402


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-dir", default=".", help="Directory containing raw CSV files.")
    parser.add_argument("--pattern", default="*.csv", help="Glob pattern for raw files.")
    parser.add_argument(
        "--output-dir",
        default="artifacts/standardized",
        help="Directory for standardized outputs.",
    )
    parser.add_argument("--resample-rule", default=DEFAULT_RESAMPLE_RULE, help="Pandas resample rule.")
    parser.add_argument(
        "--interpolate-limit",
        type=int,
        default=DEFAULT_INTERPOLATE_LIMIT,
        help="Maximum number of missing resample buckets to interpolate.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    input_dir = Path(args.input_dir).resolve()
    output_dir = (ROOT / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    frames = []
    manifest = []
    for csv_path in sorted(input_dir.glob(args.pattern)):
        standardized = standardize_historian_export(
            csv_path,
            resample_rule=args.resample_rule,
            interpolate_limit=args.interpolate_limit,
        )
        output_path = output_dir / f"{csv_path.stem}.standardized.csv"
        standardized.to_csv(output_path, index=False, encoding="utf-8-sig")
        frames.append(standardized)
        manifest.append(
            {
                "source_file": csv_path.name,
                "output_file": output_path.name,
                **summarize_standardized_frame(standardized),
            }
        )
        print(f"[standardized] {csv_path.name} -> {output_path.name} ({len(standardized)} rows)")

    if frames:
        combined = pd.concat(frames, ignore_index=True)
        combined_path = output_dir / "all_standardized.csv"
        combined.to_csv(combined_path, index=False, encoding="utf-8-sig")
        print(f"[combined] {combined_path.name} ({len(combined)} rows)")

    (output_dir / "standardization_summary.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
