"""Export 10 complete valid training sample examples with charts and markdown."""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from fengda_cnn.data.point_registry import POINT_SPECS  # noqa: E402
from fengda_cnn.training import load_sample_dataset, split_samples_chronologically  # noqa: E402
from fengda_cnn.visualization.training_sample_plot import plot_training_sample  # noqa: E402


@dataclass
class ExportSample:
    sample_no: int
    split_index: int
    source_file: str
    run_stage: str
    input_start: str
    input_end: str
    target_start: str
    target_end: str
    input_csv_name: str
    target_csv_name: str
    chart_name: str
    target_summary: dict[str, dict[str, float]]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--samples-dir",
        default="artifacts/samples_1s_60_60_excl_3812",
        help="Directory containing exported supervised samples.",
    )
    parser.add_argument(
        "--output-dir",
        default="sample",
        help="Directory to save markdown, csv, and chart files.",
    )
    parser.add_argument("--count", type=int, default=10, help="Number of examples to export.")
    parser.add_argument(
        "--split",
        default="train",
        choices=["train", "val", "test"],
        help="Which chronological split to sample from.",
    )
    parser.add_argument("--train-ratio", type=float, default=0.7)
    parser.add_argument("--val-ratio", type=float, default=0.15)
    parser.add_argument("--test-ratio", type=float, default=0.15)
    return parser.parse_args()


def _select_split_arrays(
    split_name: str,
    split_data,
) -> tuple[np.ndarray, np.ndarray, pd.DataFrame]:
    if split_name == "train":
        return split_data.train_features, split_data.train_targets, split_data.train_metadata
    if split_name == "val":
        return split_data.val_features, split_data.val_targets, split_data.val_metadata
    if split_name == "test":
        return split_data.test_features, split_data.test_targets, split_data.test_metadata
    raise ValueError(f"Unsupported split_name: {split_name}")


def _choose_indices(sample_count: int, count: int) -> np.ndarray:
    if sample_count == 0:
        raise ValueError("No samples are available for export.")
    count = min(count, sample_count)
    if count == sample_count:
        return np.arange(sample_count, dtype=int)
    return np.linspace(0, sample_count - 1, num=count, dtype=int)


def _build_time_index(start: str, periods: int) -> pd.DatetimeIndex:
    return pd.date_range(start=pd.Timestamp(start), periods=periods, freq="1s")


def _target_stats(target_frame: pd.DataFrame, target_columns: list[str]) -> dict[str, dict[str, float]]:
    summary: dict[str, dict[str, float]] = {}
    for column in target_columns:
        values = target_frame[column].to_numpy(dtype=float)
        summary[column] = {
            "mean": float(values.mean()),
            "min": float(values.min()),
            "max": float(values.max()),
        }
    return summary


def _write_markdown(
    output_path: Path,
    samples_dir: Path,
    split_name: str,
    exported_samples: list[ExportSample],
    feature_columns: list[str],
    target_columns: list[str],
) -> None:
    lines: list[str] = []
    lines.append("# 训练样本示例导出")
    lines.append("")
    lines.append("## 说明")
    lines.append("")
    lines.append(f"- 样本来源目录：`{samples_dir}`")
    lines.append(f"- 使用数据分区：`{split_name}`")
    lines.append(f"- 导出样本数：`{len(exported_samples)}`")
    lines.append("- 选样策略：在所选分区内按时间顺序等间隔抽取。")
    lines.append("- 每个样本均包含：`60` 秒输入窗口、`60` 秒目标窗口、完整 `csv` 数据和图表图片。")
    lines.append("")
    lines.append("## 字段说明")
    lines.append("")
    lines.append(f"- 输入特征数：`{len(feature_columns)}`")
    lines.append(f"- 目标变量数：`{len(target_columns)}`")
    lines.append(f"- 目标变量：`{', '.join(target_columns)}`")
    lines.append("")
    lines.append("## 样本总览")
    lines.append("")
    lines.append("| 样本 | 来源文件 | 工况 | 输入开始 | 目标开始 | 图表 | 输入CSV | 目标CSV |")
    lines.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for sample in exported_samples:
        lines.append(
            f"| sample_{sample.sample_no:02d} | {sample.source_file} | {sample.run_stage} | "
            f"{sample.input_start} | {sample.target_start} | "
            f"[chart](./{sample.chart_name}) | [input](./{sample.input_csv_name}) | [target](./{sample.target_csv_name}) |"
        )
    lines.append("")

    unit_map = {spec.field_name: spec.unit for spec in POINT_SPECS}
    for sample in exported_samples:
        lines.append(f"## sample_{sample.sample_no:02d}")
        lines.append("")
        lines.append(f"- 分区内样本索引：`{sample.split_index}`")
        lines.append(f"- 来源文件：`{sample.source_file}`")
        lines.append(f"- 工况：`{sample.run_stage}`")
        lines.append(f"- 输入窗口：`{sample.input_start}` 到 `{sample.input_end}`")
        lines.append(f"- 目标窗口：`{sample.target_start}` 到 `{sample.target_end}`")
        lines.append(f"- 数据文件：[input](./{sample.input_csv_name}) / [target](./{sample.target_csv_name})")
        lines.append("")
        lines.append(f"![sample_{sample.sample_no:02d}_chart](./{sample.chart_name})")
        lines.append("")
        lines.append("| 目标变量 | 均值 | 最小值 | 最大值 |")
        lines.append("| --- | ---: | ---: | ---: |")
        for target_name, stats in sample.target_summary.items():
            unit = unit_map.get(target_name, "")
            label = f"{target_name} ({unit})" if unit else target_name
            lines.append(
                f"| {label} | {stats['mean']:.4f} | {stats['min']:.4f} | {stats['max']:.4f} |"
            )
        lines.append("")

    output_path.write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    args = parse_args()
    samples_dir = (ROOT / args.samples_dir).resolve()
    output_dir = (ROOT / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    dataset = load_sample_dataset(samples_dir)
    split_data = split_samples_chronologically(
        dataset=dataset,
        train_ratio=args.train_ratio,
        val_ratio=args.val_ratio,
        test_ratio=args.test_ratio,
    )
    features, targets, metadata = _select_split_arrays(args.split, split_data)
    selected_indices = _choose_indices(len(metadata), args.count)

    exported_samples: list[ExportSample] = []
    manifest_rows: list[dict[str, object]] = []

    for sample_no, split_index in enumerate(selected_indices, start=1):
        metadata_row = metadata.iloc[int(split_index)]
        input_frame = pd.DataFrame(
            features[int(split_index)],
            columns=dataset.feature_columns,
        )
        input_frame.insert(
            0,
            "timestamp",
            _build_time_index(metadata_row["input_start"], len(input_frame)),
        )

        target_frame = pd.DataFrame(
            targets[int(split_index)],
            columns=dataset.target_columns,
        )
        target_frame.insert(
            0,
            "timestamp",
            _build_time_index(metadata_row["target_start"], len(target_frame)),
        )

        input_csv_name = f"sample_{sample_no:02d}_input.csv"
        target_csv_name = f"sample_{sample_no:02d}_target.csv"
        chart_name = f"sample_{sample_no:02d}_chart.png"

        input_frame.to_csv(output_dir / input_csv_name, index=False, encoding="utf-8-sig")
        target_frame.to_csv(output_dir / target_csv_name, index=False, encoding="utf-8-sig")

        plot_training_sample(
            input_frame=input_frame,
            target_frame=target_frame,
            feature_columns=dataset.feature_columns,
            target_columns=dataset.target_columns,
            output_path=output_dir / chart_name,
            title=(
                f"Sample {sample_no:02d} | {metadata_row['source_file']} | "
                f"{metadata_row['target_start']}"
            ),
        )

        target_summary = _target_stats(target_frame, dataset.target_columns)
        exported_sample = ExportSample(
            sample_no=sample_no,
            split_index=int(split_index),
            source_file=str(metadata_row["source_file"]),
            run_stage=str(metadata_row["run_stage"]),
            input_start=str(metadata_row["input_start"]),
            input_end=str(metadata_row["input_end"]),
            target_start=str(metadata_row["target_start"]),
            target_end=str(metadata_row["target_end"]),
            input_csv_name=input_csv_name,
            target_csv_name=target_csv_name,
            chart_name=chart_name,
            target_summary=target_summary,
        )
        exported_samples.append(exported_sample)
        manifest_rows.append(
            {
                "sample_no": sample_no,
                "split_index": int(split_index),
                "source_file": metadata_row["source_file"],
                "run_stage": metadata_row["run_stage"],
                "input_start": metadata_row["input_start"],
                "input_end": metadata_row["input_end"],
                "target_start": metadata_row["target_start"],
                "target_end": metadata_row["target_end"],
                "input_csv": input_csv_name,
                "target_csv": target_csv_name,
                "chart": chart_name,
            }
        )

    manifest_frame = pd.DataFrame(manifest_rows)
    manifest_frame.to_csv(output_dir / "sample_manifest.csv", index=False, encoding="utf-8-sig")
    _write_markdown(
        output_path=output_dir / "training_samples_overview.md",
        samples_dir=samples_dir,
        split_name=args.split,
        exported_samples=exported_samples,
        feature_columns=dataset.feature_columns,
        target_columns=dataset.target_columns,
    )

    print(f"[export] output_dir={output_dir}")
    print(f"[export] sample_count={len(exported_samples)}")


if __name__ == "__main__":
    main()
