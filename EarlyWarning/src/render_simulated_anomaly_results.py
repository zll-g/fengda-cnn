"""Render a compact visual summary for the saved synthetic anomaly test results."""

from __future__ import annotations

import json
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np


ROOT = Path(__file__).resolve().parents[2]
TEST_DIR = ROOT / "EarlyWarning" / "artifacts" / "june_2026_export_lstm_ae_v1" / "simulated_anomaly_test"
OUTPUT = TEST_DIR / "simulated_anomaly_result_display.png"


def configure_chinese_font() -> None:
    plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "Noto Sans CJK SC"]
    plt.rcParams["axes.unicode_minus"] = False


def line_chart(axis, seconds, baseline, values, title, unit, labels, colors) -> None:
    axis.plot(seconds, baseline, color="#6b7280", linestyle="--", linewidth=2.0, label="正常基线")
    for series, label, color in zip(values, labels, colors):
        axis.plot(seconds, series, color=color, linewidth=2.2, label=label)
    axis.axvspan(50, 59, color="#f59e0b", alpha=0.10, label="末 10 秒遮蔽评分区")
    axis.set_title(title, fontweight="bold")
    axis.set_xlabel("窗口内秒数")
    axis.set_ylabel(unit)
    axis.grid(alpha=0.25)
    axis.legend(fontsize=9, loc="upper left")


def main() -> None:
    configure_chinese_font()
    cases = json.loads((TEST_DIR / "simulated_anomaly_cases.json").read_text(encoding="utf-8"))
    with np.load(TEST_DIR / "simulated_anomaly_windows.npz") as source:
        windows = source["values"]
        fields = source["field_names"].tolist()
        baseline_time = source["baseline_timestamp"].item()
    index = {field: fields.index(field) for field in fields}
    # Each process field is changed in fewer than half of the seven process cases,
    # therefore the pointwise median reconstructs the unmodified held-out baseline.
    baseline = np.median(windows[:7], axis=0)
    seconds = np.arange(windows.shape[1])

    figure, axes = plt.subplots(2, 2, figsize=(15, 9), constrained_layout=True)
    line_chart(
        axes[0, 0],
        seconds,
        baseline[:, index["stack_nox_mg_m3"]],
        [windows[0, :, index["stack_nox_mg_m3"]], windows[1, :, index["stack_nox_mg_m3"]]],
        "NOx 异常结果展示",
        "NOx 浓度",
        ["NOx 突升", "FGR 出力不足导致 NOx 渐升"],
        ["#ef4444", "#f97316"],
    )
    line_chart(
        axes[0, 1],
        seconds,
        baseline[:, index["desuperheater_outlet_temperature_c"]],
        [
            windows[2, :, index["desuperheater_outlet_temperature_c"]],
            windows[3, :, index["desuperheater_outlet_temperature_c"]],
        ],
        "蒸汽温度异常结果展示",
        "温度 (C)",
        ["蒸汽温度渐升", "热负荷扰动导致温度突升"],
        ["#dc2626", "#fb7185"],
    )
    line_chart(
        axes[1, 0],
        seconds,
        baseline[:, index["superheated_steam_pressure_mpa"]],
        [
            windows[4, :, index["superheated_steam_pressure_mpa"]],
            windows[5, :, index["superheated_steam_pressure_mpa"]],
        ],
        "蒸汽压力异常结果展示",
        "压力 (MPa)",
        ["蒸汽压力渐升", "产汽失衡导致压力突升"],
        ["#b91c1c", "#e11d48"],
    )

    process_results = [item for item in cases["results"] if item["category"] == "process_anomaly"]
    labels = [item["title"] for item in process_results]
    ratios = [item["score"] / item["threshold"] for item in process_results]
    bars = axes[1, 1].bar(np.arange(len(labels)), ratios, color="#ef4444")
    axes[1, 1].axhline(1.0, color="#374151", linestyle="--", linewidth=1.6, label="触发阈值 = 1.0")
    axes[1, 1].set_title("异常评分相对阈值", fontweight="bold")
    axes[1, 1].set_ylabel("评分 / 场景阈值")
    axes[1, 1].set_xticks(np.arange(len(labels)), labels, rotation=25, ha="right", fontsize=8)
    axes[1, 1].grid(axis="y", alpha=0.25)
    axes[1, 1].legend(loc="lower left")
    for bar, ratio in zip(bars, ratios):
        axes[1, 1].text(bar.get_x() + bar.get_width() / 2, ratio + 0.06, f"{ratio:.2f}x", ha="center", fontsize=9)
    axes[1, 1].text(
        0.02,
        0.96,
        "数据缺失：data_unavailable（不评分）\n停机：stopped（不评分）",
        transform=axes[1, 1].transAxes,
        va="top",
        fontsize=9,
        bbox={"boxstyle": "round,pad=0.35", "facecolor": "#ecfdf5", "edgecolor": "#10b981"},
    )
    figure.suptitle(f"LSTM-AE 合成异常回放结果 | 基线窗口：{baseline_time}", fontsize=16, fontweight="bold")
    figure.savefig(OUTPUT, dpi=180, bbox_inches="tight")
    print(OUTPUT)


if __name__ == "__main__":
    main()
