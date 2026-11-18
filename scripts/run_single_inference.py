"""Run single-sample inference and export 60-point forecast visualizations."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from fengda_cnn.inference import run_single_sample_inference  # noqa: E402
from fengda_cnn.visualization import plot_attention_weights, plot_forecast_curves  # noqa: E402


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples-dir", default="artifacts/samples_split_1s_60_60_nox_o2_no2_transient_t015_stride1")
    parser.add_argument("--model-dir", default="artifacts/formal_training_1s_60_60_nox_o2_no2_transient_t015_stride1")
    parser.add_argument("--output-dir", default="artifacts/formal_inference_1s_60_60_nox_o2_no2_transient_t015_stride1")
    parser.add_argument("--split", default="test", choices=["train", "val", "test"])
    parser.add_argument("--sample-index", type=int, default=0)
    parser.add_argument("--train-ratio", type=float, default=0.7)
    parser.add_argument("--val-ratio", type=float, default=0.15)
    parser.add_argument("--test-ratio", type=float, default=0.15)
    parser.add_argument(
        "--skip-plots",
        action="store_true",
        help="Only export csv/json artifacts without generating png plots.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    output_dir = (ROOT / args.output_dir).resolve() / f"{args.split}_sample_{args.sample_index:04d}"
    output_dir.mkdir(parents=True, exist_ok=True)

    summary = run_single_sample_inference(
        samples_dir=ROOT / args.samples_dir,
        model_dir=ROOT / args.model_dir,
        output_dir=output_dir,
        split_name=args.split,
        sample_index=args.sample_index,
        train_ratio=args.train_ratio,
        val_ratio=args.val_ratio,
        test_ratio=args.test_ratio,
    )

    if not args.skip_plots:
        forecast_frame = pd.read_csv(output_dir / "forecast.csv", parse_dates=["timestamp"])
        attention_frame = pd.read_csv(output_dir / "attention_weights.csv", parse_dates=["timestamp"])
        plot_forecast_curves(
            forecast_frame,
            output_dir / "forecast_plot.png",
            title_prefix=f"{args.split.title()} Sample {args.sample_index}",
        )
        plot_attention_weights(
            attention_frame,
            output_dir / "attention_plot.png",
            title=f"{args.split.title()} Sample {args.sample_index} - Attention",
        )

    print(f"[inference] output_dir={output_dir}")
    print(f"[inference] summary={summary}")


if __name__ == "__main__":
    main()
