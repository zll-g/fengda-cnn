"""Calibrate directional alert-score thresholds for a deployed emission-bound model."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort


ROOT = Path(__file__).resolve().parents[2]
INPUT_FIELDS = (
    "burner_air_damper_pct",
    "blower_frequency_hz",
    "blower_current_a",
    "burner_center_gas_valve_pct",
    "burner_outer_gas_valve_pct",
    "fgr_valve_pct",
    "fgr_fan_frequency_hz",
    "natural_gas_inlet_flow_m3_h",
)
TARGET_FIELDS = ("stack_nox_mg_m3", "stack_o2_pct")
WINDOW_SIZE = 60


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--dataset-manifest", default=str(ROOT / "db_data" / "export" / "dataset_manifest.json"))
    parser.add_argument("--output", default="")
    parser.add_argument("--calibration-start-date", default="")
    parser.add_argument("--calibration-end-date", default="")
    parser.add_argument("--holdout-start-date", default="")
    parser.add_argument("--holdout-end-date", default="")
    parser.add_argument("--width-floor-quantile", type=float, default=0.10)
    parser.add_argument("--max-validation-episodes-per-day", type=float, default=1.0)
    parser.add_argument("--required-consecutive-seconds", type=int, default=3)
    parser.add_argument("--batch-size", type=int, default=512)
    return parser.parse_args()


def load_split_scores(session: ort.InferenceSession, input_name: str, dataset_manifest: dict,
                      model_manifest: dict, split_name: str, batch_size: int,
                      date_range: tuple[str, str] | None = None) -> tuple[dict[str, list[dict]], list[str]]:
    if date_range is None:
        split_days = set(model_manifest.get("split_days", {}).get(split_name, []))
    else:
        start_date, end_date = date_range
        split_days = {
            str(item["startTime"][:10]) for item in dataset_manifest.get("segments", [])
            if start_date <= str(item["startTime"][:10]) <= end_date
        }
    if not split_days:
        raise ValueError(f"No dataset days found for the {split_name!r} date selection.")

    fields = dataset_manifest.get("fields", [])
    required = list(INPUT_FIELDS + TARGET_FIELDS)
    missing = [field for field in required if field not in fields]
    if missing:
        raise ValueError(f"Dataset manifest is missing required fields: {missing}")
    selected_indexes = [fields.index(field) for field in required]
    daily_segments: dict[str, list[dict]] = {day: [] for day in sorted(split_days)}

    for item in dataset_manifest.get("segments", []):
        day = str(item["startTime"][:10])
        if day not in split_days:
            continue
        row_count = int(item["rowCount"])
        source_path = Path(dataset_manifest["_manifest_path"]).parent / item["file"]
        raw = np.memmap(source_path, mode="r", dtype=np.float32, shape=(row_count, len(fields)))
        selected = np.asarray(raw[:, selected_indexes], dtype=np.float32).copy()
        del raw

        valid = np.isfinite(selected).all(axis=1)
        running = (selected[:, 3] > 1.0) | (selected[:, 4] > 1.0)
        eligible = valid & running
        transitions = np.flatnonzero(np.diff(np.r_[False, eligible, False].astype(np.int8)))

        for start, end in transitions.reshape(-1, 2):
            start = int(start) + WINDOW_SIZE
            end = int(end) - WINDOW_SIZE
            if end - start < WINDOW_SIZE:
                continue
            block = selected[start:end]
            windows = np.lib.stride_tricks.sliding_window_view(block[:, :len(INPUT_FIELDS)], WINDOW_SIZE, axis=0)
            windows = np.moveaxis(windows, -1, 1)
            bounds = []
            for offset in range(0, len(windows), batch_size):
                batch = np.ascontiguousarray(windows[offset:offset + batch_size], dtype=np.float32)
                result = session.run(None, {input_name: batch})[0]
                bounds.append(np.asarray(result, dtype=np.float32))
            prediction = np.concatenate(bounds, axis=0)
            actual = block[WINDOW_SIZE - 1:, len(INPUT_FIELDS):]
            target_seconds = np.arange(start + WINDOW_SIZE - 1, end, dtype=np.int32)
            if len(prediction) != len(actual) or len(prediction) != len(target_seconds):
                raise RuntimeError(f"Mismatched inference rows for {day}.")
            daily_segments[day].append({
                "seconds": target_seconds,
                "bounds": prediction,
                "actual": actual,
            })

    daily_segments = {day: segments for day, segments in daily_segments.items() if segments}
    if not daily_segments:
        raise RuntimeError(f"No valid {split_name} sequences found in the export manifest.")
    return daily_segments, sorted(daily_segments)


def score_segments(daily_segments: dict[str, list[dict]], width_floors: tuple[float, float]) -> dict[str, dict[str, list[np.ndarray]]]:
    result: dict[str, dict[str, list[np.ndarray]]] = {}
    for day, segments in daily_segments.items():
        result[day] = {key: [] for key in ("nox_above", "nox_below", "o2_above", "o2_below")}
        for segment in segments:
            bounds = segment["bounds"]
            actual = segment["actual"]
            nox_width = np.maximum(bounds[:, 1] - bounds[:, 0], width_floors[0])
            o2_width = np.maximum(bounds[:, 3] - bounds[:, 2], width_floors[1])
            result[day]["nox_above"].append(np.maximum(actual[:, 0] - bounds[:, 1], 0.0) / nox_width)
            result[day]["nox_below"].append(np.maximum(bounds[:, 0] - actual[:, 0], 0.0) / nox_width)
            result[day]["o2_above"].append(np.maximum(actual[:, 1] - bounds[:, 3], 0.0) / o2_width)
            result[day]["o2_below"].append(np.maximum(bounds[:, 2] - actual[:, 1], 0.0) / o2_width)
    return result


def episode_count(segments: list[np.ndarray], threshold: float, consecutive: int) -> int:
    episodes = 0
    for scores in segments:
        outside = scores >= threshold
        if len(outside) < consecutive:
            continue
        starts = np.flatnonzero(outside & ~np.r_[False, outside[:-1]])
        ends = np.flatnonzero(outside & ~np.r_[outside[1:], False])
        episodes += int(np.count_nonzero((ends - starts + 1) >= consecutive))
    return episodes


def calibrate_threshold(day_scores: dict[str, dict[str, list[np.ndarray]]], scenario: str,
                        max_episodes_per_day: float, consecutive: int) -> tuple[float | None, dict[str, int]]:
    candidates = np.unique(np.concatenate([
        scores for values in day_scores.values() for scores in values[scenario]
    ]))
    candidates = candidates[candidates > 0]
    if not len(candidates):
        return None, {day: 0 for day in day_scores}

    def counts(threshold: float) -> dict[str, int]:
        return {
            day: episode_count(values[scenario], threshold, consecutive)
            for day, values in day_scores.items()
        }

    low, high = 0, len(candidates) - 1
    selected: float | None = None
    selected_counts = {day: 0 for day in day_scores}
    while low <= high:
        middle = (low + high) // 2
        candidate = float(candidates[middle])
        current_counts = counts(candidate)
        average = sum(current_counts.values()) / len(current_counts)
        if average <= max_episodes_per_day:
            selected = candidate
            selected_counts = current_counts
            high = middle - 1
        else:
            low = middle + 1
    if selected is None:
        selected = float(np.nextafter(candidates[-1], np.inf))
        selected_counts = counts(selected)
    return selected, selected_counts


def main() -> None:
    args = parse_args()
    model_dir = Path(args.model_dir).resolve()
    model_path = model_dir / "best_model.onnx"
    model_manifest_path = model_dir / "manifest.json"
    dataset_manifest_path = Path(args.dataset_manifest).resolve()
    if not 0 < args.width_floor_quantile < 1 or args.max_validation_episodes_per_day < 0:
        raise ValueError("Calibration quantile and episode-rate settings are invalid.")
    if args.required_consecutive_seconds < 1 or args.batch_size < 1:
        raise ValueError("Consecutive seconds and batch size must be positive.")
    custom_calibration = bool(args.calibration_start_date or args.calibration_end_date)
    custom_holdout = bool(args.holdout_start_date or args.holdout_end_date)
    if custom_calibration != bool(args.calibration_start_date and args.calibration_end_date):
        raise ValueError("Both calibration start and end dates must be provided.")
    if custom_holdout != bool(args.holdout_start_date and args.holdout_end_date):
        raise ValueError("Both holdout start and end dates must be provided.")

    model_manifest = json.loads(model_manifest_path.read_text(encoding="utf-8"))
    dataset_manifest = json.loads(dataset_manifest_path.read_text(encoding="utf-8"))
    dataset_manifest["_manifest_path"] = str(dataset_manifest_path)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    validation, validation_days = load_split_scores(
        session, input_name, dataset_manifest, model_manifest, "val", args.batch_size,
        (args.calibration_start_date, args.calibration_end_date) if custom_calibration else None
    )

    widths = [[], []]
    for segments in validation.values():
        for segment in segments:
            widths[0].append(segment["bounds"][:, 1] - segment["bounds"][:, 0])
            widths[1].append(segment["bounds"][:, 3] - segment["bounds"][:, 2])
    width_floors = tuple(max(float(np.quantile(np.concatenate(values), args.width_floor_quantile)), 1e-6)
                         for values in widths)
    validation_scores = score_segments(validation, width_floors)

    scenarios = {}
    for scenario in ("nox_above", "nox_below", "o2_above", "o2_below"):
        threshold, per_day = calibrate_threshold(
            validation_scores, scenario, args.max_validation_episodes_per_day,
            args.required_consecutive_seconds
        )
        sample_count = sum(sum(len(scores) for scores in values[scenario]) for values in validation_scores.values())
        scenarios[scenario] = {
            "threshold": threshold,
            "status": "calibrated" if threshold is not None else "no_validation_exceedances",
            "sample_count": sample_count,
            "episodes_by_validation_day": per_day,
            "mean_episodes_per_validation_day": sum(per_day.values()) / len(per_day),
        }

    test_report = None
    if custom_holdout or (not custom_calibration and model_manifest.get("split_days", {}).get("test", [])):
        test_segments, test_days = load_split_scores(
            session, input_name, dataset_manifest, model_manifest, "test", args.batch_size,
            (args.holdout_start_date, args.holdout_end_date) if custom_holdout else None
        )
        test_scores = score_segments(test_segments, width_floors)
        test_report = {"days": test_days, "episodes_by_scenario": {}}
        for scenario, settings in scenarios.items():
            threshold = settings["threshold"]
            test_report["episodes_by_scenario"][scenario] = {
                day: episode_count(values[scenario], threshold, args.required_consecutive_seconds)
                for day, values in test_scores.items()
            } if threshold is not None else {day: 0 for day in test_scores}

    digest = hashlib.sha256(model_path.read_bytes()).hexdigest()
    payload = {
        "schema_version": 1,
        "calibration_status": "provisional",
        "model_version": model_manifest.get("model_version", "unknown"),
        "model_sha256": digest,
        "method": "one-sided interval exceedance divided by max(predicted interval width, validation width floor)",
        "score_unit": "fraction of a predicted interval width; this is a platform score, not a model probability",
        "width_floor_quantile": args.width_floor_quantile,
        "width_floor": {"nox": width_floors[0], "o2": width_floors[1]},
        "threshold_selection": {
            "source": "chronological post-training calibration data for the exact deployed ONNX" if custom_calibration else "chronological validation split of the exact deployed ONNX",
            "max_confirmed_episodes_per_validation_day": args.max_validation_episodes_per_day,
            "required_consecutive_seconds": args.required_consecutive_seconds,
            "calibration_days": validation_days,
            "warning_level": "warning",
            "note": "Validation data is unlabeled; the episode budget is a provisional statistical operating target, not a validated fault sensitivity guarantee.",
        },
        "recovery": {"score_ratio": 0.7, "consecutive_seconds": 10},
        "cooldown_minutes": 5,
        "scenarios": scenarios,
        "held_out_test": test_report,
    }
    output_path = Path(args.output).resolve() if args.output else model_dir / "alert_thresholds.json"
    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(output_path), "thresholds": scenarios, "held_out_test": test_report},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
