"""Generate reproducible synthetic abnormal windows and test the trained LSTM-AE."""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, asdict
from datetime import datetime
from pathlib import Path

import numpy as np
import torch


ROOT = Path(__file__).resolve().parents[2]
if str(Path(__file__).resolve().parent) not in sys.path:
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from train_early_warning import ConditionMaskedLSTMAE, MASK_STEPS, WINDOW_SIZE


@dataclass(frozen=True)
class TestCase:
    case_id: str
    title: str
    category: str
    target_field: str | None
    description: str
    expectation: str


@dataclass(frozen=True)
class CaseResult:
    case_id: str
    title: str
    category: str
    target_field: str | None
    status: str
    score: float | None
    threshold: float | None
    detected: bool
    result: str


PROCESS_CASES = (
    TestCase(
        "nox_step_high",
        "NOx 突升",
        "process_anomaly",
        "stack_nox_mg_m3",
        "末 10 秒 NOx 突升至正常波动范围之外。",
        "触发 NOx 偏高检测",
    ),
    TestCase(
        "nox_fgr_insufficient_ramp",
        "FGR 出力不足引发 NOx 渐升",
        "process_anomaly",
        "stack_nox_mg_m3",
        "FGR 阀位及风机频率下降，同时 NOx 在末 20 秒持续升高。",
        "触发 NOx 偏高检测",
    ),
    TestCase(
        "steam_temperature_ramp_high",
        "蒸汽温度渐升",
        "process_anomaly",
        "desuperheater_outlet_temperature_c",
        "末 20 秒蒸汽温度持续上升。",
        "触发蒸汽温度偏高检测",
    ),
    TestCase(
        "steam_temperature_step_high",
        "炉膛热负荷扰动导致蒸汽温度突升",
        "process_anomaly",
        "desuperheater_outlet_temperature_c",
        "炉膛出口烟温抬升，末 10 秒蒸汽温度突升。",
        "触发蒸汽温度偏高检测",
    ),
    TestCase(
        "steam_pressure_ramp_high",
        "蒸汽压力渐升",
        "process_anomaly",
        "superheated_steam_pressure_mpa",
        "末 20 秒蒸汽压力持续上升。",
        "触发蒸汽压力偏高检测",
    ),
    TestCase(
        "steam_pressure_step_high",
        "产汽失衡导致蒸汽压力突升",
        "process_anomaly",
        "superheated_steam_pressure_mpa",
        "燃气流量与锅炉负荷扰动后，末 10 秒蒸汽压力突升。",
        "触发蒸汽压力偏高检测",
    ),
    TestCase(
        "combined_three_scene_high",
        "NOx、蒸汽温度与压力组合异常",
        "process_anomaly",
        "stack_nox_mg_m3",
        "三个核心场景目标在末 20 秒同时出现高值偏离。",
        "三个场景均应触发",
    ),
)

SYSTEM_CASES = (
    TestCase(
        "data_unavailable",
        "过程输入缺失",
        "system_state",
        None,
        "第 40 秒注入一个 NaN，模拟 Historian 数据质量异常。",
        "返回 data_unavailable，不调用模型评分",
    ),
    TestCase(
        "stopped",
        "停机状态",
        "system_state",
        None,
        "60 秒内中心气阀位与外环气阀位均不大于 1%。",
        "返回 stopped / not_scored，不调用模型评分",
    ),
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--artifact-dir",
        default=str(ROOT / "EarlyWarning" / "artifacts" / "june_2026_export_lstm_ae_v1"),
    )
    parser.add_argument("--export-manifest", default=str(ROOT / "db_data" / "export" / "dataset_manifest.json"))
    parser.add_argument("--validation-day", default="2026-06-29")
    parser.add_argument("--test-day", default="2026-06-30")
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--calibration-windows", type=int, default=128)
    parser.add_argument("--seed", type=int, default=42)
    return parser.parse_args()


def evenly_spaced(values: np.ndarray, count: int) -> np.ndarray:
    if len(values) <= count:
        return values
    indexes = np.linspace(0, len(values) - 1, count, dtype=np.int64)
    return values[indexes]


def load_day_windows(manifest_path: Path, day: str) -> tuple[np.ndarray, np.ndarray, list[str]]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    fields = list(manifest["fields"])
    item = next((value for value in manifest["segments"] if value["startTime"].startswith(day)), None)
    if item is None:
        raise ValueError(f"导出清单没有 {day} 分区。")
    row_count = int(item["rowCount"])
    path = manifest_path.parent / item["file"]
    raw = np.memmap(path, mode="r", dtype=np.float32, shape=(row_count, len(fields)))
    values = np.asarray(raw, dtype=np.float32).copy()
    del raw
    center = fields.index("burner_center_gas_valve_pct")
    outer = fields.index("burner_outer_gas_valve_pct")
    running = (np.isfinite(values[:, center]) & (values[:, center] > 1.0)) | (
        np.isfinite(values[:, outer]) & (values[:, outer] > 1.0)
    )
    valid = np.isfinite(values).all(axis=1) & running
    valid_windows = np.convolve(valid.astype(np.int16), np.ones(WINDOW_SIZE, dtype=np.int16), mode="valid") == WINDOW_SIZE
    starts = np.flatnonzero(valid_windows).astype(np.int32)
    if starts.size == 0:
        raise RuntimeError(f"{day} 没有连续 {WINDOW_SIZE} 秒的完整运行窗口。")
    start_time = np.datetime64(item["startTime"], "s")
    timestamps = start_time + starts.astype("timedelta64[s]")
    windows = np.stack([values[start : start + WINDOW_SIZE] for start in starts]).astype(np.float32)
    return windows, timestamps, fields


def build_model(artifact_dir: Path, feature_count: int) -> tuple[ConditionMaskedLSTMAE, list[str], np.ndarray, np.ndarray]:
    manifest = json.loads((artifact_dir / "manifest.json").read_text(encoding="utf-8"))
    normalization = json.loads((artifact_dir / "normalization.json").read_text(encoding="utf-8"))
    gmm = json.loads((artifact_dir / "gmm.json").read_text(encoding="utf-8"))
    fields = list(manifest["process_feature_columns"])
    if fields != list(normalization["feature_columns"]) or len(fields) != feature_count:
        raise ValueError("模型、归一化文件与导出数据的字段契约不一致。")
    model = ConditionMaskedLSTMAE(feature_count, int(gmm["components"]), WINDOW_SIZE)
    model.load_state_dict(torch.load(artifact_dir / "best_model.pt", map_location="cpu", weights_only=True))
    model.eval()
    return model, fields, np.asarray(normalization["mean"], dtype=np.float32), np.asarray(normalization["std"], dtype=np.float32)


@torch.no_grad()
def scenario_scores(
    model: ConditionMaskedLSTMAE,
    windows_raw: np.ndarray,
    mean: np.ndarray,
    std: np.ndarray,
    target_indexes: list[int],
) -> np.ndarray:
    """Return positive last-10-second reconstruction residuals for each scenario target."""

    normalized = (windows_raw - mean[None, None, :]) / std[None, None, :]
    scores = np.empty((len(windows_raw), len(target_indexes)), dtype=np.float32)
    for slot, target_index in enumerate(target_indexes):
        inputs = normalized.copy()
        masks = np.zeros((len(windows_raw), WINDOW_SIZE, len(target_indexes)), dtype=np.float32)
        inputs[:, -MASK_STEPS:, target_index] = 0.0
        masks[:, -MASK_STEPS:, slot] = 1.0
        tensor = torch.from_numpy(np.concatenate([inputs, masks], axis=2)).to(dtype=torch.float32)
        condition_ids = torch.zeros((len(windows_raw),), dtype=torch.long)
        reconstruction = model(tensor, condition_ids).cpu().numpy()
        residual = normalized[:, -MASK_STEPS:, target_index] - reconstruction[:, -MASK_STEPS:, target_index]
        scores[:, slot] = np.maximum(residual, 0.0).mean(axis=1)
    return scores


def add_ramp(values: np.ndarray, field_index: int, delta: float, duration: int = 20) -> None:
    values[-duration:, field_index] += np.linspace(0.0, delta, duration, dtype=np.float32)


def generate_process_cases(base: np.ndarray, fields: list[str], std: np.ndarray) -> tuple[list[TestCase], list[np.ndarray], list[dict[str, object]]]:
    index = {field: fields.index(field) for field in fields}
    nonnegative_fields = {
        "fgr_valve_pct",
        "fgr_fan_frequency_hz",
        "natural_gas_inlet_flow_m3_h",
        "boiler_load",
    }
    cases: list[TestCase] = []
    values: list[np.ndarray] = []
    metadata: list[dict[str, object]] = []

    def append(case: TestCase, changes: list[tuple[str, float, int]]) -> None:
        window = base.copy()
        applied: list[dict[str, object]] = []
        for field, sigma, duration in changes:
            magnitude = max(abs(std[index[field]]) * abs(sigma), 0.01)
            delta = float(np.copysign(magnitude, sigma))
            add_ramp(window, index[field], delta, duration)
            if field in nonnegative_fields:
                # Actuator positions, frequencies, flow and load cannot be negative.
                window[:, index[field]] = np.maximum(window[:, index[field]], 0.0)
            applied.append({"field": field, "delta_raw": delta, "duration_seconds": duration})
        cases.append(case)
        values.append(window)
        metadata.append({"case_id": case.case_id, "changes": applied})

    append(PROCESS_CASES[0], [("stack_nox_mg_m3", 8.0, 10)])
    append(
        PROCESS_CASES[1],
        [("fgr_valve_pct", -8.0, 20), ("fgr_fan_frequency_hz", -8.0, 20), ("stack_nox_mg_m3", 8.0, 20)],
    )
    append(PROCESS_CASES[2], [("desuperheater_outlet_temperature_c", 8.0, 20)])
    append(
        PROCESS_CASES[3],
        [("furnace_exit_temperature_c", 4.0, 20), ("desuperheater_outlet_temperature_c", 10.0, 10)],
    )
    append(PROCESS_CASES[4], [("superheated_steam_pressure_mpa", 8.0, 20)])
    append(
        PROCESS_CASES[5],
        [("natural_gas_inlet_flow_m3_h", 4.0, 20), ("boiler_load", 4.0, 20), ("superheated_steam_pressure_mpa", 10.0, 10)],
    )
    append(
        PROCESS_CASES[6],
        [
            ("stack_nox_mg_m3", 8.0, 20),
            ("desuperheater_outlet_temperature_c", 8.0, 20),
            ("superheated_steam_pressure_mpa", 8.0, 20),
        ],
    )
    return cases, values, metadata


def validate_window(values: np.ndarray, fields: list[str]) -> str:
    if not np.isfinite(values).all():
        return "data_unavailable"
    center = values[:, fields.index("burner_center_gas_valve_pct")]
    outer = values[:, fields.index("burner_outer_gas_valve_pct")]
    if not ((center > 1.0).any() or (outer > 1.0).any()):
        return "stopped"
    return "ready"


def markdown_report(
    output: Path,
    artifact_dir: Path,
    validation_day: str,
    test_day: str,
    thresholds: dict[str, float],
    results: list[CaseResult],
) -> None:
    passed = sum(result.detected for result in results)
    lines = [
        "# LSTM-AE 合成异常测试报告",
        "",
        f"- 生成时间：{datetime.now().astimezone().isoformat(timespec='seconds')}",
        f"- 模型目录：`{artifact_dir}`",
        f"- 正常阈值标定数据：{validation_day}（末 10 秒正向重构残差 P99）",
        f"- 合成异常基线数据：{test_day}",
        f"- 覆盖用例：{len(results)}，通过：{passed}，失败：{len(results) - passed}",
        "",
        "## 阈值",
        "",
        "| 场景目标 | 正向残差 P99 阈值（标准化） |",
        "| --- | ---: |",
    ]
    for field, threshold in thresholds.items():
        lines.append(f"| `{field}` | {threshold:.6f} |")
    lines.extend(
        [
            "",
            "## 用例结果",
            "",
            "| 用例 | 类别 | 目标/状态 | 分数 | 阈值 | 结果 |",
            "| --- | --- | --- | ---: | ---: | --- |",
        ]
    )
    for result in results:
        subject = result.target_field or result.status
        score = "-" if result.score is None else f"{result.score:.6f}"
        threshold = "-" if result.threshold is None else f"{result.threshold:.6f}"
        lines.append(f"| {result.title} | {result.category} | `{subject}` | {score} | {threshold} | {result.result} |")
    lines.extend(
        [
            "",
            "## 判定说明",
            "",
            "- 过程异常测试在末 10 秒遮蔽目标测点后执行重构，使用实际值减重构值的正向残差均值评分。",
            "- 合成异常仅用于验证训练、数据契约、遮蔽协议和评分链路可运行；不能替代真实已复核异常事件的召回率与提前量验收。",
            "- 数据缺失和停机用例验证的是安全拒绝评分逻辑，不属于神经网络异常分类结果。",
            "- 当前模型为单工况 GMM 降级版本；收集更多运行工况后应重新进行多工况标定及异常回放。",
        ]
    )
    (output / "simulated_anomaly_test_report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    args = parse_args()
    artifact_dir = Path(args.artifact_dir)
    manifest_path = Path(args.export_manifest)
    output = Path(args.output_dir) if args.output_dir else artifact_dir / "simulated_anomaly_test"
    output.mkdir(parents=True, exist_ok=True)

    validation_windows, _, validation_fields = load_day_windows(manifest_path, args.validation_day)
    test_windows, test_times, test_fields = load_day_windows(manifest_path, args.test_day)
    if validation_fields != test_fields:
        raise ValueError("验证日与测试日字段不一致。")
    model, fields, mean, std = build_model(artifact_dir, len(test_fields))
    if fields != test_fields:
        raise ValueError("模型字段顺序与导出字段不一致。")
    target_indexes = [fields.index(field) for field in json.loads((artifact_dir / "manifest.json").read_text(encoding="utf-8"))["target_fields"]]
    target_fields = [fields[index] for index in target_indexes]

    calibration = evenly_spaced(validation_windows, args.calibration_windows)
    normal_scores = scenario_scores(model, calibration, mean, std, target_indexes)
    thresholds = {field: float(np.quantile(normal_scores[:, index], 0.99)) for index, field in enumerate(target_fields)}

    # Choose a deterministic valid normal window in the middle of the held-out test day.
    baseline_index = len(test_windows) // 2
    baseline = test_windows[baseline_index]
    process_cases, process_windows, synthetic_metadata = generate_process_cases(baseline, fields, std)
    process_values = np.stack(process_windows)
    process_scores = scenario_scores(model, process_values, mean, std, target_indexes)

    results: list[CaseResult] = []
    for row, case in enumerate(process_cases):
        if case.case_id == "combined_three_scene_high":
            triggered = [
                bool(process_scores[row, slot] > thresholds[field]) for slot, field in enumerate(target_fields)
            ]
            results.append(
                CaseResult(
                    case.case_id,
                    case.title,
                    case.category,
                    ", ".join(target_fields),
                    "ready",
                    float(process_scores[row].min()),
                    float(min(thresholds.values())),
                    all(triggered),
                    "通过：三个场景均触发" if all(triggered) else "失败：至少一个场景未触发",
                )
            )
            continue
        slot = target_fields.index(case.target_field or "")
        score = float(process_scores[row, slot])
        threshold = thresholds[case.target_field or ""]
        detected = score > threshold
        results.append(
            CaseResult(
                case.case_id,
                case.title,
                case.category,
                case.target_field,
                "ready",
                score,
                threshold,
                detected,
                "通过：触发预期场景" if detected else "失败：未超过正常阈值",
            )
        )

    data_unavailable = baseline.copy()
    data_unavailable[39, fields.index("stack_o2_pct")] = np.nan
    stopped = baseline.copy()
    stopped[:, fields.index("burner_center_gas_valve_pct")] = 0.0
    stopped[:, fields.index("burner_outer_gas_valve_pct")] = 0.0
    for case, window, expected in ((SYSTEM_CASES[0], data_unavailable, "data_unavailable"), (SYSTEM_CASES[1], stopped, "stopped")):
        status = validate_window(window, fields)
        detected = status == expected
        results.append(
            CaseResult(
                case.case_id,
                case.title,
                case.category,
                None,
                status,
                None,
                None,
                detected,
                f"通过：{status}，未调用模型评分" if detected else f"失败：状态为 {status}",
            )
        )

    all_windows = np.concatenate([process_values, data_unavailable[None, :, :], stopped[None, :, :]], axis=0)
    all_cases = list(process_cases) + list(SYSTEM_CASES)
    np.savez_compressed(
        output / "simulated_anomaly_windows.npz",
        values=all_windows.astype(np.float32),
        field_names=np.asarray(fields),
        baseline_timestamp=np.asarray(str(test_times[baseline_index])),
    )
    case_manifest = {
        "generated_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "model_artifact_dir": str(artifact_dir.resolve()),
        "export_manifest": str(manifest_path.resolve()),
        "baseline_timestamp": str(test_times[baseline_index]),
        "window_size_seconds": WINDOW_SIZE,
        "case_definitions": [asdict(case) for case in all_cases],
        "synthetic_process_changes": synthetic_metadata,
        "results": [asdict(result) for result in results],
    }
    (output / "simulated_anomaly_cases.json").write_text(json.dumps(case_manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    markdown_report(output, artifact_dir, args.validation_day, args.test_day, thresholds, results)
    failed = [result.case_id for result in results if not result.detected]
    print(f"[test] cases={len(results)}, passed={len(results) - len(failed)}, failed={len(failed)}")
    if failed:
        print(f"[test] failed cases: {', '.join(failed)}")
        raise SystemExit(1)
    print(f"[test] report: {output / 'simulated_anomaly_test_report.md'}")


if __name__ == "__main__":
    main()
