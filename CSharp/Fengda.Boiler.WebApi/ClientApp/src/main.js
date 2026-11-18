import { createApp, markRaw, nextTick } from "vue";
import {
  CategoryScale,
  Chart,
  BarController,
  BarElement,
  Legend,
  LineController,
  LineElement,
  LinearScale,
  PointElement,
  Tooltip
} from "chart.js";
import "./styles.css";

Chart.register(CategoryScale, LinearScale, LineController, LineElement, BarController, BarElement, PointElement, Tooltip, Legend);

const chartColors = [
  "#0f766e",
  "#b45309",
  "#2563eb",
  "#be123c",
  "#6d28d9",
  "#047857",
  "#c2410c",
  "#0369a1",
  "#9333ea",
  "#4d7c0f"
];

const faultTrendRelatedColors = [
  "#0369a1",
  "#0f766e",
  "#0284c7",
  "#0e7490",
  "#1d4ed8",
  "#0f5f6a"
];

function formatNumber(value) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) {
    return "--";
  }

  const number = Number(value);
  if (Math.abs(number) >= 100) {
    return number.toFixed(1);
  }

  if (Math.abs(number) >= 10) {
    return number.toFixed(2);
  }

  return number.toFixed(3);
}

function toFiniteNumber(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function downsampleSnapshots(snapshots, intervalSeconds) {
  if (!snapshots.length) {
    return [];
  }

  const start = new Date(snapshots[0].recordedAt).getTime();
  const samplesByBucket = new Map();
  snapshots.forEach((snapshot) => {
    const time = new Date(snapshot.recordedAt).getTime();
    const bucket = Number.isFinite(time) && Number.isFinite(start)
      ? Math.floor((time - start) / (intervalSeconds * 1000))
      : samplesByBucket.size;
    samplesByBucket.set(bucket, snapshot);
  });

  return Array.from(samplesByBucket.values());
}

function normalizeSnapshotSeries(snapshots, propertyName) {
  const raw = snapshots.map((snapshot) => toFiniteNumber(snapshot[propertyName]));
  const values = raw.filter((value) => value !== null);
  if (!values.length) {
    return raw;
  }

  const minimum = Math.min(...values);
  const maximum = Math.max(...values);
  const range = maximum - minimum;
  return raw.map((value) => value === null ? null : range < 1e-9 ? 50 : (value - minimum) / range * 100);
}

function toLocalInputValue(isoText) {
  if (!isoText) {
    return "";
  }

  return isoText.slice(0, 19);
}

function toChinaApiTime(localValue) {
  return localValue ? `${localValue}+08:00` : "";
}

function getDatePart(localValue) {
  return localValue ? localValue.slice(0, 10) : "";
}

function formatChinaLocalInput(date) {
  const parts = new Intl.DateTimeFormat("zh-CN", {
    timeZone: "Asia/Shanghai",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23"
  })
    .formatToParts(date)
    .reduce((acc, part) => {
      acc[part.type] = part.value;
      return acc;
    }, {});

  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}:${parts.second}`;
}

function formatChinaMonth(date) {
  return formatChinaLocalInput(date).slice(0, 7).replace("-", "");
}

function formatMonthLabel(month) {
  return month && month.length === 6 ? `${month.slice(0, 4)}-${month.slice(4, 6)}` : "--";
}

function shiftLocalSeconds(localValue, seconds) {
  if (!localValue) {
    return "";
  }

  const date = new Date(`${localValue}+08:00`);
  return formatChinaLocalInput(new Date(date.getTime() + seconds * 1000));
}

async function apiGet(path) {
  const response = await fetch(path, { headers: { Accept: "application/json" } });
  const payload = await response.json();
  if (payload.code !== 200) {
    const error = new Error(payload.message || `接口返回业务码 ${payload.code}`);
    error.code = payload.code;
    throw error;
  }

  return payload.data;
}

function valueFromSnapshot(snapshot, point) {
  if (!snapshot || !point) {
    return null;
  }

  const rawValue = snapshot[point.snapshotPropertyName];
  if (rawValue === null || rawValue === undefined) {
    return null;
  }

  const value = Number(rawValue);
  return Number.isFinite(value) ? value : null;
}

function buildSnapshotSeries(snapshots, points, suffix = "") {
  return points.map((point, index) => {
    const rawPoints = snapshots
      .map((snapshot) => ({
        timestamp: snapshot.recordedAt,
        value: valueFromSnapshot(snapshot, point)
      }))
      .filter((item) => item.value !== null);

    return {
      key: `${point.pointName}${suffix}`,
      point,
      label: suffix ? `${point.label}${suffix}` : point.label,
      color: chartColors[index % chartColors.length],
      points: rawPoints,
      dashed: false
    };
  });
}

function buildForecastSeries(forecast, points, colorOffset = 0) {
  if (!forecast?.series?.length) {
    return [];
  }

  return points
    .map((point, index) => {
      const series = forecast.series.find((item) => item.pointName.toLowerCase() === point.pointName.toLowerCase());
      if (!series) {
        return null;
      }

      return {
        key: `${point.pointName}:forecast`,
        point,
        label: `${point.label}预测`,
        color: chartColors[(index + colorOffset) % chartColors.length],
        points: series.points.map((item) => ({
          timestamp: item.timestamp,
          value: Number(item.value)
        })),
        dashed: true
      };
    })
    .filter(Boolean);
}

function getFixedRange(point) {
  const min = Number(point.lowScale);
  const max = Number(point.highScale);
  return Number.isFinite(min) && Number.isFinite(max) && max > min ? { min, max } : null;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#039;");
}

function getOrCreateTooltip(canvas) {
  const container = canvas.parentNode;
  let tooltip = container.querySelector(".chart-tooltip");
  if (!tooltip) {
    tooltip = document.createElement("div");
    tooltip.className = "chart-tooltip";
    container.appendChild(tooltip);
  }

  return tooltip;
}

function renderExternalTooltip(context) {
  const { chart, tooltip } = context;
  const tooltipElement = getOrCreateTooltip(chart.canvas);

  if (tooltip.opacity === 0) {
    tooltipElement.style.opacity = "0";
    return;
  }

  const rows = tooltip.dataPoints
    .filter((item) => Number.isFinite(item.raw?.actual))
    .map((item) => {
      const raw = item.raw;
      const unit = raw.unit ? ` ${raw.unit}` : "";
      return {
        color: item.dataset.borderColor,
        label: item.dataset.label,
        value: `${formatNumber(raw.actual)}${unit}`
      };
    });

  if (rows.length === 0) {
    tooltipElement.style.opacity = "0";
    return;
  }

  const title = tooltip.dataPoints[0]?.raw?.time?.replace("T", " ").replace("+08:00", "") ?? "";
  tooltipElement.innerHTML = `
    <div class="chart-tooltip-title">${escapeHtml(title)}</div>
    <div class="chart-tooltip-body">
      ${rows
        .map((row) => `
          <div class="chart-tooltip-row">
            <span class="chart-tooltip-marker" style="background:${escapeHtml(row.color)}"></span>
            <span class="chart-tooltip-label">${escapeHtml(row.label)}</span>
            <strong>${escapeHtml(row.value)}</strong>
          </div>
        `)
        .join("")}
    </div>
  `;

  const { offsetLeft, offsetTop } = chart.canvas;
  const container = chart.canvas.parentNode;
  const mouseX = Number.isFinite(chart._lastEvent?.x) ? chart._lastEvent.x : tooltip.caretX;
  const mouseY = Number.isFinite(chart._lastEvent?.y) ? chart._lastEvent.y : tooltip.caretY;
  let left = offsetLeft + mouseX + 14;
  let top = offsetTop + mouseY + 14;
  tooltipElement.style.opacity = "1";
  if (left + tooltipElement.offsetWidth > container.clientWidth) {
    left = Math.max(0, offsetLeft + mouseX - tooltipElement.offsetWidth - 14);
  }

  if (top + tooltipElement.offsetHeight > container.clientHeight) {
    top = Math.max(0, offsetTop + mouseY - tooltipElement.offsetHeight - 14);
  }

  tooltipElement.style.left = `${left}px`;
  tooltipElement.style.top = `${top}px`;
}

function createLineChart(canvas, seriesList, options = {}) {
  const hiddenKeys = options.hiddenKeys ?? new Set();
  const labels = Array.from(
    new Set(seriesList.flatMap((series) => series.points.map((point) => point.timestamp)))
  ).sort();

  const rangeByPoint = new Map();
  for (const series of seriesList) {
    const key = series.point.pointName;
    const fixedRange = getFixedRange(series.point);
    if (fixedRange) {
      rangeByPoint.set(key, fixedRange);
      continue;
    }

    const values = series.points.map((point) => point.value).filter((value) => Number.isFinite(value));
    if (values.length === 0) {
      continue;
    }

    const existing = rangeByPoint.get(key);
    const min = Math.min(...values, existing?.min ?? Number.POSITIVE_INFINITY);
    const max = Math.max(...values, existing?.max ?? Number.NEGATIVE_INFINITY);
    rangeByPoint.set(key, { min, max });
  }

  const datasets = seriesList.map((series) => {
    const range = rangeByPoint.get(series.point.pointName) ?? { min: 0, max: 1 };
    const isConstant = Math.abs(range.max - range.min) < 1e-9;
    const valuesByTime = new Map(series.points.map((point) => [point.timestamp, point.value]));
    return {
      seriesKey: series.key,
      label: series.label,
      hidden: hiddenKeys.has(series.key),
      borderColor: series.color,
      backgroundColor: series.color,
      borderDash: series.dashed ? [8, 5] : [],
      borderWidth: series.dashed ? 2 : 1.8,
      pointRadius: labels.length > 180 ? 0 : 1.5,
      pointHoverRadius: 4,
      tension: 0.18,
      spanGaps: true,
      data: labels.map((timestamp) => {
        const actual = valuesByTime.get(timestamp);
        if (!Number.isFinite(actual)) {
          return { x: timestamp, y: null, actual: null, unit: series.point.unit, time: timestamp };
        }

        const normalized = isConstant ? 0.5 : Math.max(0, Math.min(1, (actual - range.min) / (range.max - range.min)));
        return {
          x: timestamp,
          y: normalized,
          actual,
          unit: series.point.unit,
          time: timestamp,
          range
        };
      })
    };
  });

  return markRaw(new Chart(canvas, {
    type: "line",
    data: { labels, datasets },
    options: {
      animation: false,
      maintainAspectRatio: false,
      parsing: false,
      interaction: { mode: "index", intersect: false },
      onClick: options.onClick,
      plugins: {
        legend: {
          position: "bottom",
          labels: { boxWidth: 12, boxHeight: 2, usePointStyle: false },
          onClick(_event, legendItem, legend) {
            const chart = legend.chart;
            const datasetIndex = legendItem.datasetIndex;
            chart.setDatasetVisibility(datasetIndex, !chart.isDatasetVisible(datasetIndex));
            chart.update("none");
            options.onVisibilityChange?.(chart);
          }
        },
        tooltip: {
          enabled: false,
          external: renderExternalTooltip
        }
      },
      scales: {
        x: {
          ticks: {
            autoSkip: true,
            maxTicksLimit: 10,
            callback(value) {
              const label = labels[value] ?? "";
              return label.slice(11, 19);
            }
          },
          grid: { display: false }
        },
        y: {
          min: 0,
          max: 1,
          ticks: {
            stepSize: 0.25,
            callback(value) {
              return `${Math.round(value * 100)}%`;
            }
          },
          title: {
            display: true,
            text: "窗口归一化"
          }
        }
      }
    }
  }));
}

function emissionBoundsChartDatasets(samples, target, targetDto) {
  const isNox = target === "nox";
  const actual = samples.map((sample) => toFiniteNumber(isNox ? sample.noxActual : sample.o2Actual));
  const lower = samples.map((sample) => toFiniteNumber(isNox ? sample.noxLower : sample.o2Lower));
  const upper = samples.map((sample) => toFiniteNumber(isNox ? sample.noxUpper : sample.o2Upper));
  const riskLine = samples.map(() => toFiniteNumber(targetDto?.operatingRiskLowerLimit));
  return [
    {
      id: "model-lower",
      label: "模型下限",
      data: lower,
      borderColor: "#64748b",
      borderDash: [5, 4],
      borderWidth: 1.5,
      pointRadius: 0,
      tension: 0.18,
      spanGaps: true,
      yAxisID: "y"
    },
    {
      id: "model-upper",
      label: "模型上限",
      data: upper,
      borderColor: "#2563eb",
      backgroundColor: "rgba(37, 99, 235, 0.08)",
      borderDash: [5, 4],
      borderWidth: 1.5,
      pointRadius: 0,
      tension: 0.18,
      spanGaps: true,
      fill: "-1",
      yAxisID: "y"
    },
    {
      id: "actual",
      label: "实测值",
      data: actual,
      borderColor: "#ea580c",
      backgroundColor: "#ea580c",
      borderWidth: 2,
      pointRadius: samples.length > 30 ? 0 : 2,
      pointHoverRadius: 4,
      tension: 0.18,
      spanGaps: true,
      yAxisID: "y"
    },
    {
      id: "operating-risk-lower",
      label: "运行风险下限",
      data: riskLine,
      borderColor: "#be123c",
      borderDash: [2, 3],
      borderWidth: 1.5,
      pointRadius: 0,
      tension: 0,
      spanGaps: true,
      yAxisID: "y"
    },
  ];
}

function createEmissionBoundsChart(canvas, samples, target, targetDto) {
  if (!canvas) {
    return null;
  }
  const isNox = target === "nox";
  const unit = isNox ? "mg/m³" : "%";
  const labels = samples.map((sample) => String(sample.recordedAt ?? "").slice(11, 19));
  const datasets = emissionBoundsChartDatasets(samples, target, targetDto);
  const concentrations = datasets.slice(0, 4).flatMap((dataset) => dataset.data);
  const finiteValues = [...concentrations, targetDto?.operatingRiskLowerLimit]
    .map(toFiniteNumber).filter((value) => value !== null);
  const minimum = finiteValues.length ? Math.min(...finiteValues) : 0;
  const maximum = finiteValues.length ? Math.max(...finiteValues) : 1;
  const padding = Math.max((maximum - minimum) * 0.16, Math.abs(maximum) * 0.03, 0.1);

  return markRaw(new Chart(canvas, {
    type: "line",
    data: {
      labels,
      datasets
    },
    options: {
      animation: false,
      maintainAspectRatio: false,
      interaction: { mode: "index", intersect: false },
      plugins: {
        legend: {
          position: "bottom",
          labels: { boxWidth: 14, boxHeight: 2, usePointStyle: false, padding: 14 }
        },
        tooltip: {
          callbacks: {
            label(context) {
              const value = toFiniteNumber(context.raw);
              return `${context.dataset.label}：${value === null ? "--" : `${formatNumber(value)} ${unit}`}`;
            }
          }
        }
      },
      scales: {
        x: {
          grid: { display: false },
          ticks: { autoSkip: true, maxTicksLimit: 7, maxRotation: 0 }
        },
        y: {
          suggestedMin: minimum - padding,
          suggestedMax: maximum + padding,
          ticks: { maxTicksLimit: 5, callback: (value) => `${formatNumber(value)}` },
          title: { display: true, text: unit }
        }
      }
    }
  }));
}

function clampZoomRange(minimum, maximum, lowerBound, upperBound) {
  if (maximum - minimum >= upperBound - lowerBound) {
    return { minimum: lowerBound, maximum: upperBound };
  }

  if (minimum < lowerBound) {
    maximum += lowerBound - minimum;
    minimum = lowerBound;
  }

  if (maximum > upperBound) {
    minimum -= maximum - upperBound;
    maximum = upperBound;
  }

  return {
    minimum: Math.max(lowerBound, minimum),
    maximum: Math.min(upperBound, maximum)
  };
}

function applyCombustionPredictionZoom(chart, zoom) {
  if (!zoom) {
    return;
  }

  if (zoom.y && Number.isFinite(zoom.y.minimum) && Number.isFinite(zoom.y.maximum)) {
    const range = clampZoomRange(zoom.y.minimum, zoom.y.maximum, 0, 1);
    chart.options.scales.y.min = range.minimum;
    chart.options.scales.y.max = range.maximum;
  }

  if (zoom.x?.startTime && zoom.x?.endTime && chart.__fullPredictionLabels.length > 1) {
    const startIndex = chart.__fullPredictionLabels.indexOf(zoom.x.startTime);
    const endIndex = chart.__fullPredictionLabels.indexOf(zoom.x.endTime);
    if (startIndex >= 0 && endIndex > startIndex) {
      setCombustionPredictionXWindow(chart, startIndex, endIndex);
    }
  }

  chart.update("none");
}

function initializeCombustionPredictionData(chart) {
  chart.__fullPredictionLabels = [...chart.data.labels];
  chart.__fullPredictionDatasets = chart.data.datasets.map((dataset) => ({
    ...dataset,
    data: [...dataset.data]
  }));
  chart.__predictionWindowStart = 0;
  chart.__predictionWindowEnd = chart.__fullPredictionLabels.length - 1;
}

function setCombustionPredictionXWindow(chart, startIndex, endIndex) {
  const upperBound = chart.__fullPredictionLabels.length - 1;
  const range = clampZoomRange(startIndex, endIndex, 0, upperBound);
  const start = Math.round(range.minimum);
  const end = Math.round(range.maximum);
  if (end <= start) {
    return false;
  }

  chart.__predictionWindowStart = start;
  chart.__predictionWindowEnd = end;
  chart.data.labels = chart.__fullPredictionLabels.slice(start, end + 1);
  chart.data.datasets = chart.__fullPredictionDatasets.map((dataset) => ({
    ...dataset,
    data: dataset.data.slice(start, end + 1)
  }));
  return true;
}

function attachCombustionPredictionZoom(canvas, chart, onZoomChanged) {
  if (canvas.__combustionPredictionWheelHandler) {
    canvas.removeEventListener("wheel", canvas.__combustionPredictionWheelHandler);
  }

  const wheelHandler = (event) => {
    const scale = event.ctrlKey ? chart.scales.x : chart.scales.y;
    const upperBound = event.ctrlKey ? chart.__fullPredictionLabels.length - 1 : 1;
    if (!scale || upperBound <= 0) {
      return;
    }

    event.preventDefault();
    const zoomFactor = event.deltaY < 0 ? 0.82 : 1.22;
    const mousePixel = event.ctrlKey ? event.offsetX : event.offsetY;
    const localCenter = Number(scale.getValueForPixel(mousePixel));
    const center = event.ctrlKey ? chart.__predictionWindowStart + localCenter : localCenter;
    const currentMinimum = Number(scale.min);
    const currentMaximum = Number(scale.max);
    const windowMinimum = event.ctrlKey ? chart.__predictionWindowStart : currentMinimum;
    const windowMaximum = event.ctrlKey ? chart.__predictionWindowEnd : currentMaximum;
    if (!Number.isFinite(center) || !Number.isFinite(windowMinimum) || !Number.isFinite(windowMaximum)) {
      return;
    }

    const range = windowMaximum - windowMinimum;
    const next = clampZoomRange(
      center - (center - windowMinimum) * zoomFactor,
      center + (windowMaximum - center) * zoomFactor,
      0,
      upperBound
    );
    if (range <= 0 || next.maximum - next.minimum < 0.002) {
      return;
    }

    const nextMinimum = event.ctrlKey ? Math.round(next.minimum) : next.minimum;
    const nextMaximum = event.ctrlKey ? Math.round(next.maximum) : next.maximum;
    if (nextMaximum <= nextMinimum) {
      return;
    }

    if (event.ctrlKey) {
      if (!setCombustionPredictionXWindow(chart, nextMinimum, nextMaximum)) {
        return;
      }
    } else {
      scale.options.min = nextMinimum;
      scale.options.max = nextMaximum;
    }
    chart.update("none");
    onZoomChanged?.(event.ctrlKey
      ? {
        x: {
          startTime: chart.__fullPredictionLabels[nextMinimum],
          endTime: chart.__fullPredictionLabels[nextMaximum]
        }
      }
      : { y: { minimum: next.minimum, maximum: next.maximum } });
  };

  canvas.__combustionPredictionWheelHandler = wheelHandler;
  canvas.addEventListener("wheel", wheelHandler, { passive: false });
}

createApp({
  template: `
    <main class="app-shell">
      <header class="top-bar">
        <div>
          <h1>丰达启动锅炉实时预测</h1>
          <p>{{ metadata.modelStatus || "正在读取模型状态" }}</p>
        </div>
        <nav class="tab-bar" aria-label="预测模式">
          <button :class="{ active: activeTab === 'realtime' }" @click="switchTab('realtime')">实时预测</button>
          <button :class="{ active: activeTab === 'history' }" @click="switchTab('history')">历史预测</button>
          <button :class="{ active: activeTab === 'combustion' }" @click="switchTab('combustion')">燃烧诊断</button>
          <button :class="{ active: activeTab === 'emissionBounds' }" @click="switchTab('emissionBounds')">排放边界监测</button>
          <a href="/swagger" target="_blank" rel="noreferrer">Swagger</a>
        </nav>
      </header>

      <section v-if="activeTab === 'realtime'" class="workspace">
        <div class="toolbar">
          <label class="switch-row">
            <input type="checkbox" v-model="realtime.autoRefresh" @change="syncRealtimeTimer" />
            <span>自动刷新</span>
          </label>
          <label>
            间隔
            <select v-model.number="realtime.intervalSeconds" @change="syncRealtimeTimer">
              <option :value="1">1 秒</option>
              <option :value="2">2 秒</option>
            </select>
          </label>
          <button @click="refreshRealtime" :disabled="realtime.loading">手动刷新</button>
          <span class="status-text" :class="{ error: realtime.error }">{{ realtime.message }}</span>
        </div>

        <section class="realtime-tabs-region">
          <div class="realtime-tab-header">
            <nav class="workspace-tabs" aria-label="实时预测工作区">
              <button :class="{ active: realtimeWorkspaceTab === 'prediction' }" @click="realtimeWorkspaceTab = 'prediction'">实时预测</button>
              <button :class="{ active: realtimeWorkspaceTab === 'warning' }" @click="realtimeWorkspaceTab = 'warning'">异常监测预警</button>
            </nav>
            <div class="tab-operation-state" aria-live="polite">
              <span>当前状态</span>
              <strong class="operation-state" :class="realtimeRunningClass">{{ realtimeRunningText }}</strong>
            </div>
          </div>

          <div class="realtime-tab-content">
            <template v-if="realtimeWorkspaceTab === 'prediction'">
        <div class="summary-strip">
          <div>
            <span>输入窗口</span>
            <strong>{{ realtime.response?.inputStart?.slice(11, 19) || "--" }} - {{ realtime.response?.inputEnd?.slice(11, 19) || "--" }}</strong>
          </div>
          <div>
            <span>预测窗口</span>
            <strong>{{ realtime.response?.forecastStart?.slice(11, 19) || "--" }} - {{ realtime.response?.forecastEnd?.slice(11, 19) || "--" }}</strong>
          </div>
          <div>
            <span>输入数量</span>
            <strong>{{ realtime.response?.dataQuality?.inputCount ?? "--" }}</strong>
          </div>
          <div>
            <span>运行状态</span>
            <strong class="operation-state" :class="realtimeRunningClass">{{ realtimeRunningText }}</strong>
          </div>
        </div>

        <div class="content-grid">
          <section class="chart-stack">
            <section class="chart-panel">
              <div class="section-heading">
                <h2>输入参数折线图（当前60秒）</h2>
                <div class="chart-actions">
                  <span>鼠标悬停查看实际值</span>
                  <button @click="setChartVisibility('realtimeInput', true)">全部显示</button>
                  <button @click="setChartVisibility('realtimeInput', false)">全部隐藏</button>
                </div>
              </div>
              <div class="chart-frame">
                <canvas ref="realtimeInputChart"></canvas>
              </div>
            </section>
            <section class="chart-panel">
              <div class="section-heading">
                <h2>目标/预测数据折线图（未来60秒）</h2>
                <div class="chart-actions">
                  <span>鼠标悬停查看实际值</span>
                  <button @click="setChartVisibility('realtimeTarget', true)">全部显示</button>
                  <button @click="setChartVisibility('realtimeTarget', false)">全部隐藏</button>
                </div>
              </div>
              <div class="chart-frame">
                <canvas ref="realtimeTargetChart"></canvas>
              </div>
            </section>
          </section>
          <aside class="side-panel">
            <FgrPanel :recommendation="realtime.response?.fgrRecommendation" />
          </aside>
        </div>
            </template>

            <section v-else class="early-warning-grid">
              <EarlyWarningCard
                v-for="warning in earlyWarnings.latest?.scenarios ?? []"
                :key="warning.key"
                :warning="warning" />
            </section>
          </div>
        </section>

        <section class="fault-section" aria-label="设备故障预警">
          <div class="section-heading fault-section-heading">
            <h2>设备故障预警</h2>
            <div class="fault-filters" aria-label="查询条件">
              <label>年月
                <select v-model="runningFaults.month" @change="onFaultMonthChange">
                  <option v-for="month in runningFaults.months" :key="month" :value="month">{{ formatMonthLabel(month) }}</option>
                </select>
              </label>
              <label>场景
                <select v-model="runningFaults.scenario" @change="onFaultFilterChange">
                  <option value="all">全部</option>
                  <option v-for="scenario in runningFaults.scenarios" :key="scenario" :value="scenario">{{ earlyWarningScenarioText(scenario) }}</option>
                </select>
              </label>
              <label>等级
                <select v-model="runningFaults.level" @change="onFaultFilterChange">
                  <option value="all">全部</option>
                  <option v-for="level in runningFaults.levels" :key="level" :value="level">{{ faultLevelText(level) }}</option>
                </select>
              </label>
              <button @click="loadRealtimeFaults({ resetPage: true })" :disabled="runningFaults.loading">刷新</button>
            </div>
          </div>
          <div class="fault-table-wrap">
            <table class="fault-table">
              <thead><tr><th>告警时间</th><th>预警场景</th><th>监测指标</th><th>等级</th><th>实测值</th><th>模型参考值</th><th>操作</th></tr></thead>
              <tbody>
                <tr v-if="runningFaults.items.length === 0"><td colspan="7" class="empty-cell">{{ runningFaults.loading ? "正在加载" : "暂无模型预警记录" }}</td></tr>
                <tr v-for="item in runningFaults.items" :key="'realtime-' + item.id" :class="['fault-row', faultLevelClass(item.level)]">
                  <td>{{ formatFaultTime(item.alarmTime) }}</td>
                  <td>{{ item.scenarioName }}</td>
                  <td>{{ item.metricName || item.scenarioName }}</td>
                  <td><span class="fault-level" :class="faultLevelClass(item.level)">{{ faultLevelText(item.level) }}</span></td>
                  <td>{{ formatWarningValue(item.actualValue, item.unit) }}</td>
                  <td>{{ formatWarningValue(item.reconstructedValue, item.unit) }}</td>
                  <td><button class="fault-detail-button" @click="showFaultReport(item)">详情</button></td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="fault-footer">
            <span :class="{ error: runningFaults.error }">{{ runningFaults.message }}</span>
            <div class="fault-pagination">
              <button @click="changeFaultPage(runningFaults.page - 1)" :disabled="runningFaults.page <= 1 || runningFaults.loading">上一页</button>
              <span>{{ runningFaults.page }} / {{ runningFaultTotalPages }}</span>
              <button @click="changeFaultPage(runningFaults.page + 1)" :disabled="runningFaults.page >= runningFaultTotalPages || runningFaults.loading">下一页</button>
            </div>
          </div>
        </section>

      </section>

      <section v-if="activeTab === 'combustion'" class="combustion-dashboard" aria-label="锅炉燃烧诊断">
        <div class="combustion-dashboard-heading">
          <div><h2>锅炉燃烧诊断</h2><p>左侧数据时间：{{ combustionLatestTime }}</p></div>
          <span class="combustion-status" :class="combustionFgrLevelClass">当前状态：{{ combustionFgrStatusText }}</span>
        </div>

        <div class="combustion-dashboard-grid">
          <div class="combustion-column combustion-left-column">
            <section class="combustion-panel">
              <div class="combustion-panel-heading"><h3>燃烧基础参数</h3><span class="panel-mark" aria-hidden="true"></span></div>
              <div class="combustion-metric-grid">
                <div class="combustion-metric combustion-metric-emphasis"><span>火焰信号</span><strong :class="combustionFlameClass">{{ combustionFlameText }}</strong></div>
                <div class="combustion-metric"><span>FGR阀位开度</span><strong>{{ combustionValue('gydcS_K0QHL10AA101AI6') }}<small>%</small></strong></div>
                <div class="combustion-metric"><span>鼓风机频率</span><strong>{{ combustionValue('gydcS_K0QHL10AN001P') }}<small>Hz</small></strong></div>
                <div class="combustion-metric"><span>天然气入口流量</span><strong>{{ combustionValue('gydcS_K0QHH10CF101') }}<small>m³/h</small></strong></div>
              </div>
            </section>

            <section class="combustion-panel">
              <div class="combustion-panel-heading"><h3>蒸汽运行参数</h3><span class="panel-mark" aria-hidden="true"></span></div>
              <div class="combustion-metric-grid">
                <div class="combustion-metric combustion-metric-emphasis"><span>实际负荷</span><strong>{{ combustionActualLoadPercent() }}<small>%</small></strong></div>
                <div class="combustion-metric"><span>过热蒸汽压力</span><strong>{{ combustionValue('gydcS_K0QHB10CP101') }}<small>MPa</small></strong></div>
                <div class="combustion-metric"><span>减温器出口温度</span><strong>{{ combustionValue('gydcS_K0QHB10CT302') }}<small>℃</small></strong></div>
                <div class="combustion-metric"><span>蒸汽流量补偿值</span><strong>{{ combustionValue('gydcS_K0QHB10CF101_COMP') }}<small>t/h</small></strong></div>
              </div>
            </section>

            <section class="combustion-panel combustion-chart-panel combustion-small-chart-panel">
              <div class="combustion-panel-heading"><h3>负荷/燃气流量/烟气O2趋势</h3><span class="panel-mark" aria-hidden="true"></span></div>
              <div class="combustion-chart-frame"><canvas ref="combustionTrendChart"></canvas></div>
            </section>
          </div>

          <div class="combustion-column combustion-center-column">
            <section class="combustion-panel combustion-chart-panel combustion-main-chart-panel">
              <div class="combustion-panel-heading combustion-main-chart-heading">
                <h3>锅炉燃烧神经网络预测图（全天分钟级）</h3>
                <label class="combustion-horizon-select">预测提前量
                  <select v-model.number="combustion.horizonSeconds" @change="changeCombustionHorizon">
                    <option v-for="second in 60" :key="second" :value="second">{{ second }} 秒</option>
                  </select>
                </label>
              </div>
              <p class="combustion-chart-message" :class="{ error: combustion.forecastError }">{{ combustion.forecastMessage }}</p>
              <div class="combustion-chart-frame"><canvas ref="combustionPredictionChart"></canvas></div>
            </section>

            <section class="combustion-panel combustion-warning-panel">
              <div class="combustion-panel-heading combustion-fault-heading">
                <h3>设备故障预警</h3>
                <div class="combustion-fault-filters" aria-label="查询条件">
                  <label>年月
                    <select v-model="runningFaults.month" @change="onFaultMonthChange">
                      <option v-for="month in runningFaults.months" :key="month" :value="month">{{ formatMonthLabel(month) }}</option>
                    </select>
                  </label>
                  <label>场景
                    <select v-model="runningFaults.scenario" @change="onFaultFilterChange">
                      <option value="all">全部</option>
                      <option v-for="scenario in runningFaults.scenarios" :key="scenario" :value="scenario">{{ earlyWarningScenarioText(scenario) }}</option>
                    </select>
                  </label>
                  <label>等级
                    <select v-model="runningFaults.level" @change="onFaultFilterChange">
                      <option value="all">全部</option>
                      <option v-for="level in runningFaults.levels" :key="level" :value="level">{{ faultLevelText(level) }}</option>
                    </select>
                  </label>
                  <button @click="loadRealtimeFaults({ resetPage: true })" :disabled="runningFaults.loading">刷新</button>
                </div>
              </div>
              <div class="fault-table-wrap combustion-fault-table-wrap">
                <table class="fault-table combustion-fault-table">
                  <thead><tr><th>告警时间</th><th>预警场景</th><th>等级</th><th>实测 / 模型参考</th><th>操作</th></tr></thead>
                  <tbody>
                    <tr v-if="runningFaults.items.length === 0"><td colspan="5" class="empty-cell">{{ runningFaults.loading ? "正在加载" : "暂无模型预警记录" }}</td></tr>
                    <tr v-for="item in runningFaults.items" :key="item.id" :class="['fault-row', faultLevelClass(item.level)]">
                      <td>{{ formatFaultTime(item.alarmTime) }}</td>
                      <td>{{ item.scenarioName }}</td>
                      <td><span class="fault-level" :class="faultLevelClass(item.level)">{{ faultLevelText(item.level) }}</span></td>
                      <td class="combustion-fault-values"><strong>{{ formatWarningValue(item.actualValue, item.unit) }}</strong><span>{{ formatWarningValue(item.reconstructedValue, item.unit) }}</span></td>
                      <td><button class="fault-detail-button" @click="showFaultReport(item)">详情</button></td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <div class="fault-footer combustion-fault-footer">
                <span :class="{ error: runningFaults.error }">{{ runningFaults.message }}</span>
                <div class="fault-pagination">
                  <button @click="changeFaultPage(runningFaults.page - 1)" :disabled="runningFaults.page <= 1 || runningFaults.loading">上一页</button>
                  <span>{{ runningFaults.page }} / {{ runningFaultTotalPages }}</span>
                  <button @click="changeFaultPage(runningFaults.page + 1)" :disabled="runningFaults.page >= runningFaultTotalPages || runningFaults.loading">下一页</button>
                </div>
              </div>
            </section>
          </div>

          <div class="combustion-column combustion-right-column">
            <FgrPanel class="combustion-dashboard-fgr" :recommendation="realtime.response?.fgrRecommendation" :show-gauge="true" />

            <section class="combustion-panel combustion-chart-panel combustion-small-chart-panel">
              <div class="combustion-panel-heading"><h3>单位产汽耗气量柱状图</h3><span class="panel-mark" aria-hidden="true"></span></div>
              <div class="combustion-chart-frame"><canvas ref="combustionConsumptionChart"></canvas></div>
            </section>
          </div>
        </div>
      </section>

      <section v-if="activeTab === 'emissionBounds'" class="emission-dashboard" aria-label="NOx 与 O2 排放边界监测">
        <header class="emission-dashboard-heading">
          <div>
            <h2>NOx / O₂ 排放边界监测</h2>
            <p>{{ emissionBounds.message }}</p>
          </div>
          <div class="emission-heading-actions">
            <span class="emission-status-pill" :class="emissionStatusClass(emissionBounds.current?.status)">{{ emissionBounds.current?.statusLabel || "等待后台服务" }}</span>
            <span v-if="emissionBounds.current?.modelQuality && !emissionBounds.current.modelQuality.modelBoundaryAlertsEnabled" class="emission-calibration-note">{{ emissionBounds.current.scoreCalibrationStatus === "shadow" ? "影子阈值 · 仅观察" : "试验模型 · 评分预警未启用" }}</span>
            <span class="emission-updated">{{ emissionBounds.current?.evaluatedAt ? formatFaultTime(emissionBounds.current.evaluatedAt) : "尚无评估" }}</span>
            <label class="switch-row emission-refresh-toggle">
              <input type="checkbox" v-model="emissionBounds.autoRefresh" @change="syncEmissionBoundsTimer" />
              <span>自动刷新</span>
            </label>
            <button @click="refreshEmissionBounds(true)" :disabled="emissionBounds.loading">刷新</button>
          </div>
        </header>

        <section v-if="emissionBounds.current?.modelQuality" class="emission-quality-banner" role="status">
          <div class="emission-quality-heading">
            <strong>{{ emissionBounds.current.scoreCalibrationStatus === "shadow" ? "试验模型质量与评分观察提示" : "试验模型质量提示" }}</strong>
            <span>版本 {{ emissionBounds.current.modelQuality.modelVersion }} · 训练 {{ String(emissionBounds.current.modelQuality.trainingCreatedAt || "").slice(0, 10) || "日期未知" }}</span>
          </div>
          <div class="emission-quality-metrics">
            <span>NOx 测试集覆盖率 <b>{{ emissionCoverageText(emissionBounds.current.modelQuality.noxTestCoverage) }}</b></span>
            <span>O₂ 测试集覆盖率 <b>{{ emissionCoverageText(emissionBounds.current.modelQuality.o2TestCoverage) }}</b></span>
          </div>
          <p>{{ emissionBounds.current.modelQuality.message }}</p>
        </section>

        <section class="emission-overview-strip" aria-label="锅炉运行概览">
          <div class="emission-overview-item emission-flame-item">
            <span>点火信号 <small>DI</small></span>
            <strong :class="emissionBounds.current?.isIgnited === true ? 'ignited' : emissionBounds.current?.isIgnited === false ? 'not-ignited' : 'unknown'">
              <i></i>{{ emissionBounds.current?.overview?.flameText || "--" }}
            </strong>
            <small>{{ emissionBounds.current?.isRunning === true ? (emissionBounds.current?.isEffectiveBurning ? "有效燃烧工况" : "阀位运行，模型未评分") : emissionBounds.current?.isRunning === false ? "阀位停机" : "运行状态未知" }}</small>
          </div>
          <div class="emission-overview-item">
            <span>过热蒸汽压力 <small>AI</small></span>
            <strong>{{ formatEmissionValue(emissionBounds.current?.overview?.steamPressureMpa, "MPa") }}</strong>
            <small>当前测点值</small>
          </div>
          <div class="emission-overview-item">
            <span>减温器出口蒸汽温度 <small>AI</small></span>
            <strong>{{ formatEmissionValue(emissionBounds.current?.overview?.outletTemperatureC, "℃") }}</strong>
            <small>当前测点值</small>
          </div>
          <div class="emission-overview-item">
            <span>蒸汽流量补偿值 <small>AI</small></span>
            <strong>{{ formatEmissionValue(emissionBounds.current?.overview?.steamFlowCompensationTph, "t/h") }}</strong>
            <small>当前测点值</small>
          </div>
        </section>

        <div class="emission-main-grid">
          <section class="emission-panel emission-input-panel">
            <div class="emission-panel-heading">
              <h3>相关因子与历史参考</h3>
              <span>权重为模型局部敏感度估算 · 合计约 100%</span>
            </div>
            <div class="emission-input-table-wrap">
              <table class="emission-input-table">
                <thead><tr><th>相关因子</th><th>历史参考范围</th><th>当前运行值</th><th>敏感度</th><th>参考状态</th></tr></thead>
                <tbody>
                  <tr v-for="input in emissionBounds.current?.inputs ?? []" :key="input.field">
                    <td><strong>{{ input.name }}</strong><small>{{ input.unit }} · {{ input.pointName }}</small></td>
                    <td><span v-if="input.historicalP10 !== null && input.historicalP10 !== undefined">{{ formatNumber(input.historicalP10) }} ~ {{ formatNumber(input.historicalP90) }} {{ input.unit }}</span><span v-else class="muted">暂无匹配分组</span></td>
                    <td><strong>{{ formatEmissionValue(input.currentValue, input.unit) }}</strong></td>
                    <td>
                      <div class="emission-sensitivity"><strong>{{ emissionSensitivityText(input.sensitivityPercent) }}</strong><i><b :style="emissionSensitivityStyle(input.sensitivityPercent)"></b></i></div>
                    </td>
                    <td>
                      <span class="emission-reference-state" :class="emissionInputAssessmentClass(input.assessment)">{{ input.assessment }}</span>
                      <small v-if="input.assessment === '建议上调'">低于历史参考范围</small>
                      <small v-else-if="input.assessment === '建议下调'">高于历史参考范围</small>
                    </td>
                  </tr>
                  <tr v-if="!emissionBounds.current?.inputs?.length"><td colspan="5" class="empty-cell">{{ emissionBounds.loading ? "正在读取模型评估" : "等待后台模型评估" }}</td></tr>
                </tbody>
              </table>
            </div>
            <p class="emission-input-note">低于历史参考范围时提示上调，高于时提示下调，仅供运行观察；P10–P90 不是运行设定值，敏感度不代表因果关系。任何操作须结合现场规程确认，不生成控制指令。</p>
          </section>

          <div class="emission-target-stack">
            <section class="emission-panel emission-target-panel">
              <div class="emission-target-heading">
                <div>
                  <h3>烟囱 NOx 排放浓度 <span class="emission-ai-tag">AI</span><span class="emission-target-status" :class="emissionTargetClass(emissionBounds.current?.nox?.status)">{{ emissionTargetText(emissionBounds.current?.nox?.status) }}</span></h3>
                  <strong class="emission-target-actual">{{ formatEmissionValue(emissionBounds.current?.nox?.actualValue, "mg/m³") }}</strong>
                  <div class="emission-target-limits">
                    <small>模型范围 {{ formatEmissionValue(emissionBounds.current?.nox?.lowerBound, "mg/m³") }} ~ {{ formatEmissionValue(emissionBounds.current?.nox?.upperBound, "mg/m³") }}</small>
                    <small class="emission-risk-limit" :class="emissionRiskClass(emissionBounds.current?.nox?.operatingRiskStatus)">运行风险下限 {{ formatEmissionValue(emissionBounds.current?.nox?.operatingRiskLowerLimit, "mg/m³") }} · {{ emissionRiskText(emissionBounds.current?.nox?.operatingRiskStatus) }}</small>
                  </div>
                  <div v-if="emissionBounds.current?.modelQuality?.modelBoundaryAlertsEnabled" class="emission-score-lines">
                    <small :class="emissionScoreClass(emissionBounds.current?.nox?.aboveAlertStatus)">偏高评分(区间倍数) {{ emissionScoreText(emissionBounds.current?.nox?.aboveScore, emissionBounds.current?.nox?.aboveScoreThreshold) }} · {{ emissionScoreStatusText(emissionBounds.current?.nox?.aboveAlertStatus, emissionBounds.current?.nox?.aboveConfirmSeconds, emissionBounds.current?.nox?.aboveRequiredSeconds) }}</small>
                    <small :class="emissionScoreClass(emissionBounds.current?.nox?.belowAlertStatus)">偏低评分(区间倍数) {{ emissionScoreText(emissionBounds.current?.nox?.belowScore, emissionBounds.current?.nox?.belowScoreThreshold) }} · {{ emissionScoreStatusText(emissionBounds.current?.nox?.belowAlertStatus, emissionBounds.current?.nox?.belowConfirmSeconds, emissionBounds.current?.nox?.belowRequiredSeconds) }}</small>
                  </div>
                  <div v-else class="emission-score-lines">
                    <small>偏高评分 / {{ emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "影子阈值" : "阈值" }} {{ emissionScoreText(emissionBounds.current?.nox?.aboveScore, emissionBounds.current?.nox?.aboveScoreThreshold) }}</small>
                    <small>偏低评分 / {{ emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "影子阈值" : "阈值" }} {{ emissionScoreText(emissionBounds.current?.nox?.belowScore, emissionBounds.current?.nox?.belowScoreThreshold) }}</small>
                    <small class="score-unavailable">{{ emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "临时阈值仅作观察，不生成评分预警记录" : "阈值未校准 · 评分仅供观察" }}</small>
                  </div>
                </div>
              </div>
              <div class="emission-chart-frame"><canvas ref="emissionNoxChart"></canvas></div>
            </section>
            <section class="emission-panel emission-target-panel">
              <div class="emission-target-heading">
                <div>
                  <h3>烟囱 O₂ 排放浓度 <span class="emission-ai-tag">AI</span><span class="emission-target-status" :class="emissionTargetClass(emissionBounds.current?.o2?.status)">{{ emissionTargetText(emissionBounds.current?.o2?.status) }}</span></h3>
                  <strong class="emission-target-actual">{{ formatEmissionValue(emissionBounds.current?.o2?.actualValue, "%") }}</strong>
                  <div class="emission-target-limits">
                    <small>模型范围 {{ formatEmissionValue(emissionBounds.current?.o2?.lowerBound, "%") }} ~ {{ formatEmissionValue(emissionBounds.current?.o2?.upperBound, "%") }}</small>
                    <small class="emission-risk-limit" :class="emissionRiskClass(emissionBounds.current?.o2?.operatingRiskStatus)">运行风险下限 {{ formatEmissionValue(emissionBounds.current?.o2?.operatingRiskLowerLimit, "%") }} · {{ emissionRiskText(emissionBounds.current?.o2?.operatingRiskStatus) }}</small>
                  </div>
                  <div v-if="emissionBounds.current?.modelQuality?.modelBoundaryAlertsEnabled" class="emission-score-lines">
                    <small :class="emissionScoreClass(emissionBounds.current?.o2?.aboveAlertStatus)">偏高评分(区间倍数) {{ emissionScoreText(emissionBounds.current?.o2?.aboveScore, emissionBounds.current?.o2?.aboveScoreThreshold) }} · {{ emissionScoreStatusText(emissionBounds.current?.o2?.aboveAlertStatus, emissionBounds.current?.o2?.aboveConfirmSeconds, emissionBounds.current?.o2?.aboveRequiredSeconds) }}</small>
                    <small :class="emissionScoreClass(emissionBounds.current?.o2?.belowAlertStatus)">偏低评分(区间倍数) {{ emissionScoreText(emissionBounds.current?.o2?.belowScore, emissionBounds.current?.o2?.belowScoreThreshold) }} · {{ emissionScoreStatusText(emissionBounds.current?.o2?.belowAlertStatus, emissionBounds.current?.o2?.belowConfirmSeconds, emissionBounds.current?.o2?.belowRequiredSeconds) }}</small>
                  </div>
                  <div v-else class="emission-score-lines">
                    <small>偏高评分 / {{ emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "影子阈值" : "阈值" }} {{ emissionScoreText(emissionBounds.current?.o2?.aboveScore, emissionBounds.current?.o2?.aboveScoreThreshold) }}</small>
                    <small>偏低评分 / {{ emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "影子阈值" : "阈值" }} {{ emissionScoreText(emissionBounds.current?.o2?.belowScore, emissionBounds.current?.o2?.belowScoreThreshold) }}</small>
                    <small class="score-unavailable">{{ emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "临时阈值仅作观察，不生成评分预警记录" : "阈值未校准 · 评分仅供观察" }}</small>
                  </div>
                </div>
              </div>
              <div class="emission-chart-frame"><canvas ref="emissionO2Chart"></canvas></div>
            </section>
          </div>
        </div>

        <section class="emission-panel emission-events-panel">
          <div class="emission-panel-heading emission-events-heading">
            <div><h3>NOx / O₂ 排放预警记录</h3><p>{{ emissionBounds.current?.modelQuality?.modelBoundaryAlertsEnabled ? "模型边界需连续评分达阈值后记录；低于运行风险线仍独立记录，不触发设备联锁或控制动作。" : emissionBounds.current?.scoreCalibrationStatus === "shadow" ? "临时影子阈值只供观察，不写入模型评分预警；低于运行风险线仍独立记录，不触发设备联锁或控制动作。" : "试验版暂不生成模型边界评分记录；低于运行风险线仍独立记录，不触发设备联锁或控制动作。" }}</p></div>
            <div class="emission-event-filters">
              <label>年月
                <select v-model="emissionBounds.eventMonth" @change="loadEmissionEvents(true)">
                  <option v-for="month in emissionBounds.months" :key="month" :value="month">{{ formatMonthLabel(month) }}</option>
                </select>
              </label>
              <label>指标
                <select v-model="emissionBounds.eventMetric" @change="loadEmissionEvents(true)">
                  <option value="all">全部指标</option><option value="NOx">NOx</option><option value="O₂">O₂</option>
                </select>
              </label>
              <button @click="loadEmissionEvents(false)" :disabled="emissionBounds.eventsLoading">刷新</button>
            </div>
          </div>
          <div class="emission-events-table-wrap">
            <table class="emission-events-table">
              <thead><tr><th>预警时间</th><th>预警类型</th><th>指标</th><th>预警方向</th><th>实测值</th><th>评分 / 阈值</th><th>触发边界</th><th>说明</th></tr></thead>
              <tbody>
                <tr v-if="!emissionBounds.events.length"><td colspan="8" class="empty-cell">{{ emissionBounds.eventsLoading ? "正在加载" : "暂无排放预警记录" }}</td></tr>
                <tr v-for="item in emissionBounds.events" :key="item.id" :class="emissionEventRowClass(item)">
                  <td>{{ formatFaultTime(item.recordedAt) }}</td><td><span class="emission-event-source" :class="item.source === 'operating_limit' ? 'operating' : 'model'">{{ emissionEventSourceText(item.source, item.score, item.modelArtifactId) }}</span></td><td>{{ item.metric }}</td><td><span class="emission-event-direction">{{ item.direction }}</span></td>
                  <td>{{ formatEmissionValue(item.actualValue, item.unit) }}</td>
                  <td>{{ emissionScoreText(item.score, item.scoreThreshold) }}</td><td>{{ emissionEventBoundaryText(item) }}</td><td>{{ item.description }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="emission-events-footer"><span :class="{ error: emissionBounds.eventsError }">{{ emissionBounds.eventsMessage }}</span>
            <div class="fault-pagination"><button @click="changeEmissionEventPage(emissionBounds.eventPage - 1)" :disabled="emissionBounds.eventPage <= 1 || emissionBounds.eventsLoading">上一页</button><span>{{ emissionBounds.eventPage }} / {{ emissionEventTotalPages }}</span><button @click="changeEmissionEventPage(emissionBounds.eventPage + 1)" :disabled="emissionBounds.eventPage >= emissionEventTotalPages || emissionBounds.eventsLoading">下一页</button></div>
          </div>
        </section>
      </section>

      <section v-if="activeTab === 'history'" class="workspace">
        <div class="toolbar history-toolbar">
          <label>
            日期
            <input type="date" v-model="history.date" />
          </label>
          <label>
            概览采样
            <select v-model.number="history.intervalSeconds">
              <option :value="5">5 秒</option>
              <option :value="10">10 秒</option>
              <option :value="30">30 秒</option>
              <option :value="60">60 秒</option>
            </select>
          </label>
          <button @click="loadHistoryOverview" :disabled="history.loadingOverview">加载概览</button>
          <label>
            预测时间
            <input type="datetime-local" step="1" v-model="history.selectedLocalTime" />
          </label>
          <button @click="moveHistory(-1)">前一秒</button>
          <button @click="moveHistory(1)">后一秒</button>
          <button @click="loadHistoryPrediction" :disabled="history.loadingPrediction">预测</button>
          <label>
            自动速度
            <select v-model.number="history.autoIntervalMs" @change="syncHistoryAutoTimer">
              <option :value="200">200ms</option>
              <option :value="400">400ms</option>
              <option :value="600">600ms</option>
              <option :value="800">800ms</option>
              <option :value="1000">1000ms</option>
            </select>
          </label>
          <button @click="startHistoryAuto(-1)" :class="{ active: history.autoDirection === -1 }">自动上一秒</button>
          <button @click="startHistoryAuto(1)" :class="{ active: history.autoDirection === 1 }">自动下一秒</button>
          <button @click="stopHistoryAuto('自动步进已停止。')" :disabled="history.autoDirection === 0">停止自动</button>
          <span class="status-text" :class="{ error: history.error }">{{ history.message }}</span>
        </div>

        <section class="overview-panel">
          <div class="section-heading">
            <h2>单日概览</h2>
            <div class="chart-actions">
              <span>{{ history.overview ? history.overview.sampleCount + " 个采样点" : "等待加载" }}</span>
              <button @click="setChartVisibility('overview', true)">全部显示</button>
              <button @click="setChartVisibility('overview', false)">全部隐藏</button>
            </div>
          </div>
          <div class="chart-frame compact">
            <canvas ref="overviewChart"></canvas>
          </div>
        </section>

        <div class="content-grid">
          <section class="chart-stack">
            <section class="chart-panel">
              <div class="section-heading">
                <h2>输入参数折线图（当前60秒）</h2>
                <div class="chart-actions">
                  <span>鼠标悬停查看实际值</span>
                  <button @click="setChartVisibility('historyInput', true)">全部显示</button>
                  <button @click="setChartVisibility('historyInput', false)">全部隐藏</button>
                </div>
              </div>
              <div class="chart-frame">
                <canvas ref="historyInputChart"></canvas>
              </div>
            </section>
            <section class="chart-panel">
              <div class="section-heading">
                <h2>目标/预测数据折线图（未来60秒）</h2>
                <div class="chart-actions">
                  <span>鼠标悬停查看实际值</span>
                  <button @click="setChartVisibility('historyTarget', true)">全部显示</button>
                  <button @click="setChartVisibility('historyTarget', false)">全部隐藏</button>
                </div>
              </div>
              <div class="chart-frame">
                <canvas ref="historyTargetChart"></canvas>
              </div>
            </section>
          </section>
          <aside class="side-panel">
            <FgrPanel :recommendation="history.response?.fgrRecommendation" />
          </aside>
        </div>
      </section>

      <div v-if="faultReport" class="fault-report-backdrop" @click.self="closeFaultReport">
        <section class="fault-report" :class="faultLevelClass(faultReport.level)" role="dialog" aria-modal="true" aria-labelledby="fault-report-title">
          <header class="fault-report-header">
            <div>
              <h2 id="fault-report-title">模型预警详细报告</h2>
              <p>{{ faultReport.scenarioName }}</p>
            </div>
            <button class="fault-report-close" title="关闭报告" aria-label="关闭报告" @click="closeFaultReport">×</button>
          </header>

          <div class="fault-report-level">
            <span class="fault-level" :class="faultLevelClass(faultReport.level)">{{ faultLevelText(faultReport.level) }}</span>
            <span>告警时间：{{ formatFaultTime(faultReport.alarmTime) }}</span>
            <span>模型版本：{{ faultReport.modelVersion || "--" }}</span>
          </div>

          <section class="fault-report-section">
            <h3>预警判定</h3>
            <dl class="fault-report-metrics">
              <div><dt>异常评分</dt><dd>{{ formatNumber(faultReport.score) }}</dd></div>
              <div><dt>预警阈值</dt><dd>{{ formatNumber(faultReport.threshold) }}</dd></div>
              <div><dt>评分倍数</dt><dd>{{ faultScoreRatio(faultReport) }}</dd></div>
              <div><dt>当前实测</dt><dd>{{ formatWarningValue(faultReport.actualValue, faultReport.unit) }}</dd></div>
              <div><dt>模型参考值</dt><dd>{{ formatWarningValue(faultReport.reconstructedValue, faultReport.unit) }}</dd></div>
              <div><dt>当前偏差</dt><dd>{{ faultResidual(faultReport) }}</dd></div>
            </dl>
          </section>

          <section class="fault-report-section fault-report-trend">
            <h3>告警前两分钟趋势</h3>
            <p v-if="faultTrend.message" :class="{ error: faultTrend.error }">{{ faultTrend.message }}</p>
            <p v-if="faultTrend.loading">正在读取历史监测数据。</p>
            <div v-else-if="faultTrend.data?.samples?.length" class="fault-trend-chart"><canvas ref="faultTrendChart"></canvas></div>
            <p v-else>暂无可展示的趋势数据。</p>
          </section>

          <section class="fault-report-section">
            <h3>异常详情</h3>
            <p>{{ faultReport.detail || "模型未返回异常详情。" }}</p>
            <p class="fault-window">预警输入窗口：{{ formatFaultTime(faultReport.windowStart) }} 至 {{ formatFaultTime(faultReport.windowEnd) }}</p>
          </section>

          <section class="fault-report-section">
            <h3>关联监测点</h3>
            <div v-if="faultTrend.data?.contributors?.length" class="contributor-list fault-report-contributor-list">
              <div class="contributor-header" aria-hidden="true"><span>关联测点</span><span>实际值</span><span>模型参考值</span></div>
              <div v-for="point in faultTrend.data.contributors" :key="point.field" class="contributor-row">
                <strong :title="point.name">{{ point.name }}</strong>
                <span class="contributor-value">{{ formatWarningValue(point.actual, point.unit) }}</span>
                <span class="contributor-value">
                  {{ formatWarningValue(point.reconstructed, point.unit) }}
                  <small class="contributor-deviation" :class="faultContributorDeviationClass(point)">{{ faultContributorDeviationText(point) }} · 贡献 {{ Math.round((point.contribution || 0) * 100) }}%</small>
                </span>
              </div>
            </div>
            <p v-else>{{ faultTrend.loading ? "正在回放模型重构关联测点。" : "未能生成关联测点重构结果。" }}</p>
          </section>

          <section class="fault-report-section fault-report-recommendations">
            <h3>建议处置</h3>
            <ol v-if="faultRecommendations(faultReport).length"><li v-for="recommendation in faultRecommendations(faultReport)" :key="recommendation">{{ recommendation }}</li></ol>
            <p v-else>模型未提供处置建议，请结合运行规程进行核查。</p>
          </section>
        </section>
      </div>
    </main>
  `,
  components: {
    EarlyWarningCard: {
      props: ["warning"],
      data() {
        return { chart: null };
      },
      computed: {
        levelClass() {
          return this.warning?.level ?? "data_unavailable";
        },
        levelText() {
          return {
            normal: "正常",
            warning: "预警（二级）",
            alarm: "报警（三级）",
            stopped: "停机，未评分",
            data_unavailable: "数据不可用",
            model_unavailable: "模型不可用"
          }[this.warning?.level] ?? "未评分";
        },
        hasScore() {
          return Number.isFinite(Number(this.warning?.score));
        },
        labels() {
          const count = this.warning?.actualTrend?.length ?? 0;
          return Array.from({ length: count }, (_value, index) => `${index - count + 1}s`);
        }
      },
      watch: {
        warning: {
          deep: true,
          handler() {
            this.$nextTick(() => this.renderChart());
          }
        }
      },
      mounted() {
        this.renderChart();
      },
      beforeUnmount() {
        this.chart?.destroy();
      },
      methods: {
        format(value, unit = "") {
          return Number.isFinite(Number(value)) ? `${formatNumber(value)}${unit ? ` ${unit}` : ""}` : "--";
        },
        contributorDeviationText(item) {
          const actual = Number(item?.actual);
          const reconstructed = Number(item?.reconstructed);
          if (!Number.isFinite(actual) || !Number.isFinite(reconstructed)) {
            return "--";
          }

          const deviation = actual - reconstructed;
          const equalityTolerance = Math.max(Math.abs(reconstructed) * 1e-6, 1e-6);
          if (Math.abs(deviation) <= equalityTolerance) {
            return "=";
          }
          if (item.direction === "接近正常值") {
            return "-";
          }

          const baseline = Math.abs(reconstructed);
          if (baseline <= 1e-9) {
            return deviation > 0 ? "↑ 无基准" : "↓ 无基准";
          }

          const percentage = deviation / baseline * 100;
          return percentage > 0
            ? `↑ +${formatNumber(percentage)}%`
            : `↓ ${formatNumber(percentage)}%`;
        },
        contributorDeviationClass(item) {
          const actual = Number(item?.actual);
          const reconstructed = Number(item?.reconstructed);
          if (!Number.isFinite(actual) || !Number.isFinite(reconstructed)) {
            return "unknown";
          }

          const equalityTolerance = Math.max(Math.abs(reconstructed) * 1e-6, 1e-6);
          if (Math.abs(actual - reconstructed) <= equalityTolerance) {
            return "equal";
          }
          if (item.direction === "接近正常值") {
            return "near";
          }
          return actual > reconstructed ? "higher" : "lower";
        },
        renderChart() {
          const canvas = this.$refs.chart;
          if (!canvas) {
            return;
          }

          this.chart?.destroy();
          const actual = this.warning?.actualTrend ?? [];
          const normal = this.warning?.reconstructedTrend ?? [];
          this.chart = markRaw(new Chart(canvas, {
            type: "line",
            data: {
              labels: this.labels,
              datasets: [
                {
                  label: "实际值",
                  data: actual,
                  borderColor: "#b45309",
                  backgroundColor: "#b45309",
                  borderWidth: 2,
                  pointRadius: 1.5,
                  tension: 0.18,
                  spanGaps: true
                },
                {
                  label: "模型参考值",
                  data: normal,
                  borderColor: "#0f766e",
                  backgroundColor: "#0f766e",
                  borderWidth: 2,
                  borderDash: [6, 4],
                  pointRadius: 1.5,
                  tension: 0.18,
                  spanGaps: true
                }
              ]
            },
            options: {
              animation: false,
              maintainAspectRatio: false,
              interaction: { mode: "index", intersect: false },
              plugins: {
                legend: { position: "bottom", labels: { boxWidth: 10, usePointStyle: true } },
                tooltip: {
                  callbacks: {
                    label: (context) => `${context.dataset.label}: ${formatNumber(context.parsed.y)} ${this.warning?.unit ?? ""}`
                  }
                }
              },
              scales: {
                x: { grid: { display: false }, ticks: { maxTicksLimit: 6 } },
                y: { title: { display: true, text: this.warning?.unit ?? "" }, ticks: { callback: (value) => formatNumber(value) } }
              }
            }
          }));
        }
      },
      template: `
        <section class="early-warning-card" :class="levelClass">
          <header class="early-warning-heading">
            <div>
              <h2>{{ warning?.name || "异常监测预警" }}</h2>
              <span>{{ warning?.targetName || "--" }}</span>
            </div>
            <div class="early-warning-score">
              <strong>{{ hasScore ? format(warning.score) : "--" }}</strong>
              <span>阈值 {{ hasScore ? format(warning.threshold) : "--" }}</span>
            </div>
            <span class="early-warning-level" :class="levelClass">{{ levelText }}</span>
          </header>

          <div class="early-warning-detail-row">
            <section class="early-warning-chart">
              <h3>实际值与模型参考值</h3>
              <div class="early-warning-chart-frame"><canvas ref="chart"></canvas></div>
            </section>

            <section class="early-warning-section early-warning-contributors">
              <h3>关联监测点</h3>
              <div v-if="warning?.contributors?.length" class="contributor-list">
                <div class="contributor-header" aria-hidden="true">
                  <span>关联测点</span>
                  <span>实际值</span>
                  <span>模型参考值</span>
                </div>
                <div v-for="item in warning.contributors" :key="item.field" class="contributor-row">
                  <strong :title="item.name">{{ item.name }}</strong>
                  <span class="contributor-value">{{ format(item.actual, item.unit) }}</span>
                  <span class="contributor-value">{{ format(item.reconstructed, item.unit) }}</span>
                  <span class="contributor-deviation" :class="contributorDeviationClass(item)" title="相对于模型参考值的偏差">
                    {{ contributorDeviationText(item) }} · 贡献 {{ Math.round((item.contribution || 0) * 100) }}%
                  </span>
                </div>
              </div>
              <p v-else class="muted-message">暂无可展示的关联测点。</p>
            </section>
          </div>

          <section class="early-warning-section early-warning-signal">
            <h3>异常信号及建议</h3>
            <div class="early-warning-signal-layout">
              <dl>
                <div><dt>当前实测</dt><dd>{{ format(warning?.actual, warning?.unit) }}</dd></div>
                <div><dt>模型参考值</dt><dd>{{ format(warning?.reconstructed, warning?.unit) }}</dd></div>
                <div><dt>正向残差</dt><dd>{{ format(warning?.residual, warning?.unit) }}</dd></div>
                <div><dt>最近趋势</dt><dd>{{ format(warning?.trend, warning?.unit) }}</dd></div>
              </dl>
              <div class="early-warning-advice">
                <p class="signal-reason">{{ warning?.statusReason || "等待后台预警服务返回数据。" }}</p>
                <ol v-if="warning?.level === 'warning' || warning?.level === 'alarm'" class="recommendation-list">
                  <li v-for="recommendation in warning.recommendations" :key="recommendation">{{ recommendation }}</li>
                </ol>
              </div>
            </div>
          </section>
        </section>
      `
    },
    FgrPanel: {
      props: ["recommendation", "showGauge"],
      computed: {
        levelClass() {
          const level = this.recommendation?.level ?? "unavailable";
          const names = ["unavailable", "normal", "watch", "warning", "danger"];
          if (Number.isInteger(level) && names[level]) {
            return names[level];
          }

          return String(level).toLowerCase();
        },
        statusText() {
          return {
            normal: "正常",
            watch: "关注",
            warning: "警告",
            danger: "危险",
            unavailable: "暂不可用"
          }[this.levelClass] ?? "暂不可用";
        }
      },
      methods: {
        formatValue(value, unit) {
          const number = Number(value);
          if (!Number.isFinite(number)) {
            return "--";
          }

          return `${formatNumber(number)}${unit ? ` ${unit}` : ""}`;
        }
      },
      template: `
        <section class="fgr-panel" :class="levelClass">
          <h3>FGR建议</h3>
          <div v-if="showGauge" class="combustion-state-layout fgr-state-layout">
            <ul class="combustion-state-list fgr-state-list" aria-label="FGR风险等级图例">
              <li class="good">正常</li>
              <li class="adjust">关注</li>
              <li class="warning">警告</li>
              <li class="steady">危险</li>
            </ul>
            <div class="combustion-gauge" :class="levelClass" :aria-label="'当前 FGR 状态：' + statusText"><span>{{ statusText }}</span></div>
          </div>
          <strong>{{ recommendation?.title || "等待预测结果" }}</strong>
          <p>{{ recommendation?.detail || "预测完成后显示建议。" }}</p>
          <dl>
            <div>
              <dt>当前阀位</dt>
              <dd>{{ formatValue(recommendation?.currentValvePct, "%") }}</dd>
            </div>
            <div>
              <dt>建议阀位</dt>
              <dd>{{ formatValue(recommendation?.suggestedValvePct, "%") }}</dd>
            </div>
            <div>
              <dt>当前频率</dt>
              <dd>{{ formatValue(recommendation?.currentFanHz, "Hz") }}</dd>
            </div>
            <div>
              <dt>建议频率</dt>
              <dd>{{ formatValue(recommendation?.suggestedFanHz, "Hz") }}</dd>
            </div>
          </dl>
        </section>
      `
    }
  },
  data() {
    return {
      activeTab: "realtime",
      realtimeWorkspaceTab: "prediction",
      metadata: { modelStatus: "", points: [] },
      realtime: {
        autoRefresh: false,
        intervalSeconds: 2,
        timer: null,
        loading: false,
        error: false,
        message: "等待刷新",
        response: null
      },
      combustion: {
        timer: null,
        loading: false,
        error: false,
        message: "等待加载燃烧参数",
        snapshots: [],
        horizonSeconds: 30,
        dailyForecast: null,
        forecastLoading: false,
        forecastError: false,
        forecastMessage: "等待加载当天分钟预测缓存",
        forecastDate: "",
        forecastLoadedAt: 0,
        gasConsumption: null,
        gasConsumptionDate: "",
        gasConsumptionLoadedAt: 0,
        predictionZoom: null
      },
      emissionBounds: {
        autoRefresh: true,
        timer: null,
        loading: false,
        error: false,
        message: "等待后台排放边界服务",
        current: null,
        eventMonth: formatChinaMonth(new Date()),
        months: [formatChinaMonth(new Date())],
        eventMetric: "all",
        eventPage: 1,
        eventPageSize: 5,
        eventTotal: 0,
        events: [],
        eventsLoading: false,
        eventsError: false,
        eventsMessage: "等待加载排放预警记录",
        eventsLastLoadedAt: 0
      },
      earlyWarnings: {
        latest: null,
        error: false,
        message: "等待后台预警服务返回数据"
      },
      runningFaults: {
        month: formatChinaMonth(new Date()),
        months: [formatChinaMonth(new Date())],
        scenario: "all",
        scenarios: [],
        level: "all",
        levels: [],
        page: 1,
        pageSize: 5,
        total: 0,
        items: [],
        loading: false,
        error: false,
        message: "等待加载故障记录"
      },
      faultReport: null,
      faultTrend: {
        data: null,
        loading: false,
        error: false,
        message: ""
      },
      history: {
        date: "",
        intervalSeconds: 10,
        autoIntervalMs: 400,
        autoDirection: 0,
        autoTimer: null,
        selectedLocalTime: "",
        loadingOverview: false,
        loadingPrediction: false,
        error: false,
        message: "等待加载历史数据",
        overview: null,
        response: null
      },
      charts: {
        overview: null,
        realtimeInput: null,
        realtimeTarget: null,
        faultTrend: null,
        combustionPrediction: null,
        combustionTrend: null,
        combustionConsumption: null,
        emissionNox: null,
        emissionO2: null,
        historyInput: null,
        historyTarget: null
      },
      hiddenSeriesByChart: {
        overview: [],
        realtimeInput: [],
        realtimeTarget: [],
        historyInput: [],
        historyTarget: []
      }
    };
  },
  computed: {
    overviewPoints() {
      return this.metadata.points
        .slice()
        .sort((a, b) => a.displayOrder - b.displayOrder);
    },
    inputPoints() {
      return this.metadata.points
        .filter((point) => point.isModelInput)
        .sort((a, b) => a.displayOrder - b.displayOrder);
    },
    targetPoints() {
      return this.metadata.points
        .filter((point) => point.isModelTarget)
        .sort((a, b) => a.displayOrder - b.displayOrder);
    },
    realtimeLatestSnapshot() {
      const inputData = this.realtime.response?.inputData ?? [];
      return inputData.length > 0 ? inputData[inputData.length - 1] : null;
    },
    realtimeRunningText() {
      if (!this.realtimeLatestSnapshot || typeof this.realtimeLatestSnapshot.isRunning !== "boolean") {
        return "--";
      }

      return this.realtimeLatestSnapshot.isRunning ? "运行" : "未运行";
    },
    realtimeRunningClass() {
      if (!this.realtimeLatestSnapshot || typeof this.realtimeLatestSnapshot.isRunning !== "boolean") {
        return "unknown";
      }

      return this.realtimeLatestSnapshot.isRunning ? "running" : "stopped";
    },
    combustionLatestSnapshot() {
      const snapshots = this.combustion.snapshots;
      return snapshots.length > 0 ? snapshots[snapshots.length - 1] : null;
    },
    combustionLatestTime() {
      const recordedAt = this.combustionLatestSnapshot?.recordedAt;
      return recordedAt ? this.formatFaultTime(recordedAt) : "--";
    },
    combustionFlameText() {
      const isIgnited = this.combustionLatestSnapshot?.isIgnited;
      return isIgnited === true ? "已点火" : isIgnited === false ? "未点火" : "未知";
    },
    combustionFlameClass() {
      const isIgnited = this.combustionLatestSnapshot?.isIgnited;
      return isIgnited === true ? "ignited" : isIgnited === false ? "not-ignited" : "unknown";
    },
    combustionFgrLevelClass() {
      const level = this.realtime.response?.fgrRecommendation?.level;
      const numericLevels = ["unavailable", "normal", "watch", "warning", "danger"];
      if (Number.isInteger(level) && numericLevels[level]) {
        return numericLevels[level];
      }

      return numericLevels.includes(String(level).toLowerCase())
        ? String(level).toLowerCase()
        : "unavailable";
    },
    combustionFgrStatusText() {
      return {
        normal: "运行正常",
        watch: "需要关注",
        warning: "存在警告风险",
        danger: "存在危险风险",
        unavailable: "暂不可用"
      }[this.combustionFgrLevelClass] ?? "暂不可用";
    },
    runningFaultTotalPages() {
      return Math.max(1, Math.ceil(this.runningFaults.total / this.runningFaults.pageSize));
    },
    emissionEventTotalPages() {
      return Math.max(1, Math.ceil(this.emissionBounds.eventTotal / this.emissionBounds.eventPageSize));
    }
  },
  async mounted() {
    await this.initialize();
  },
  beforeUnmount() {
    this.stopRealtimeTimer();
    this.stopHistoryAuto();
    this.stopCombustionTimer();
    this.stopEmissionBoundsTimer();
    Object.values(this.charts).forEach((chart) => chart?.destroy());
  },
  methods: {
    async switchTab(tabName) {
      if (tabName !== "history") {
        this.stopHistoryAuto();
      }
      if (tabName !== "combustion") {
        this.stopCombustionTimer();
      }
      if (tabName !== "emissionBounds") {
        this.stopEmissionBoundsTimer();
      }

      this.activeTab = tabName;
      await nextTick();
      if (tabName === "combustion") {
        await this.refreshRealtime();
        await this.loadCombustionData();
        this.startCombustionTimer();
      }
      if (tabName === "emissionBounds") {
        await this.loadEmissionMonths();
        await this.refreshEmissionBounds(true);
        this.syncEmissionBoundsTimer();
      }
      this.rerenderCharts();
    },
    async initialize() {
      try {
        const metadata = await apiGet("/api/metadata/points");
        this.metadata = metadata;
        await this.loadFaultOptions();
        await this.loadRealtimeFaults({ resetPage: true, silent: true });
        await this.loadLatestEarlyWarnings();

        const latest = await apiGet("/api/history/latest?count=1");
        if (latest.length > 0) {
          const selected = shiftLocalSeconds(toLocalInputValue(latest[0].recordedAt), -60);
          this.history.selectedLocalTime = selected;
          this.history.date = getDatePart(selected);
          await this.loadHistoryOverview();
          await this.loadHistoryPrediction();
        } else {
          this.history.date = formatChinaLocalInput(new Date()).slice(0, 10);
        }
      } catch (error) {
        this.history.error = true;
        this.history.message = error.message;
      }
    },
    emissionStatusClass(status) {
      if (status === "normal") return "normal";
      if (status === "out_of_range") return "outside";
      if (status === "score_pending") return "pending";
      if (status === "stopped") return "stopped";
      if (status === "model_unavailable" || status === "inference_error" || status === "data_invalid") return "invalid";
      return "pending";
    },
    emissionTargetClass(status) {
      if (status === "normal") return "inside";
      if (status === "above") return "above";
      if (status === "below") return "below";
      if (status === "missing") return "invalid";
      return "pending";
    },
    emissionTargetText(status) {
      return ({ normal: "模型范围内", above: "高于模型上限", below: "低于模型下限", missing: "实测缺失", unavailable: "待评估" })[status] ?? "待评估";
    },
    emissionScoreClass(status) {
      return status === "warning" ? "score-warning" : status === "pending" ? "score-pending" : status === "unavailable" ? "score-unavailable" : "score-normal";
    },
    emissionScoreText(score, threshold) {
      const value = toFiniteNumber(score);
      const limit = toFiniteNumber(threshold);
      if (value === null) return limit === null ? "--" : `-- / ${limit.toFixed(3)}`;
      return `${value.toFixed(3)} / ${limit === null ? "未校准" : limit.toFixed(3)}`;
    },
    emissionCoverageText(value) {
      const coverage = toFiniteNumber(value);
      return coverage === null ? "未知" : `${(coverage * 100).toFixed(1)}%`;
    },
    emissionScoreStatusText(status, seconds, requiredSeconds) {
      if (status === "warning") return "已确认预警";
      if (status === "pending") return `确认中 ${Number(seconds) || 0}/${Number(requiredSeconds) || "--"} 秒`;
      return ({ normal: "未达阈值", unavailable: "评分不可用" })[status] ?? "待评估";
    },
    emissionRiskClass(status) {
      return status === "below" ? "below-risk" : status === "normal" ? "risk-normal" : "risk-unknown";
    },
    emissionRiskText(status) {
      return ({ normal: "正常", below: "低于风险线", missing: "实测缺失", unavailable: "待评估" })[status] ?? "待评估";
    },
    emissionEventSourceText(source, score, modelArtifactId) {
      if (source === "operating_limit") return "运行风险";
      if (!modelArtifactId) return toFiniteNumber(score) === null ? "历史模型边界" : "历史模型评分";
      return toFiniteNumber(score) === null ? "模型边界" : "模型评分";
    },
    emissionEventRowClass(item) {
      if (item.source === "operating_limit") return "emission-risk-row";
      return item.direction === "偏高" ? "observation-high" : "observation-low";
    },
    emissionEventBoundaryText(item) {
      if (item.source === "operating_limit") {
        return `< ${this.formatNumber(item.lowerBound)} ${item.unit}`;
      }
      const bound = item.direction === "偏高" ? item.upperBound : item.lowerBound;
      return `${item.direction === "偏高" ? "模型上限" : "模型下限"} ${this.formatNumber(bound)} ${item.unit}`;
    },
    formatEmissionValue(value, unit) {
      const number = toFiniteNumber(value);
      return number === null ? "--" : `${formatNumber(number)} ${unit}`;
    },
    emissionSensitivityText(value) {
      const number = toFiniteNumber(value);
      return number === null ? "--" : `${number.toFixed(1)}%`;
    },
    emissionSensitivityStyle(value) {
      const number = toFiniteNumber(value);
      return { width: `${number === null ? 0 : Math.min(100, Math.max(0, number))}%` };
    },
    emissionInputAssessmentClass(assessment) {
      return ({
        "建议上调": "increase",
        "建议下调": "decrease",
        "历史参考范围内": "inside"
      })[assessment] ?? "unknown";
    },
    async refreshEmissionBounds(forceEvents = false) {
      if (this.emissionBounds.loading) return;
      this.emissionBounds.loading = true;
      try {
        this.emissionBounds.current = await apiGet("/api/emission-bounds/current");
        this.emissionBounds.error = false;
        this.emissionBounds.message = this.emissionBounds.current?.message || "评估结果已更新。";
        this.renderEmissionBoundsCharts();
        if (forceEvents || Date.now() - this.emissionBounds.eventsLastLoadedAt >= 5000) {
          await this.loadEmissionEvents(false);
        }
      } catch (error) {
        this.emissionBounds.error = true;
        this.emissionBounds.message = error.message;
      } finally {
        this.emissionBounds.loading = false;
      }
    },
    syncEmissionBoundsTimer() {
      this.stopEmissionBoundsTimer();
      if (this.activeTab !== "emissionBounds" || !this.emissionBounds.autoRefresh) return;
      this.emissionBounds.timer = window.setInterval(() => this.refreshEmissionBounds(), 1000);
    },
    stopEmissionBoundsTimer() {
      if (this.emissionBounds.timer !== null) {
        window.clearInterval(this.emissionBounds.timer);
        this.emissionBounds.timer = null;
      }
    },
    async loadEmissionMonths() {
      try {
        const months = await apiGet("/api/emission-bounds/months");
        this.emissionBounds.months = months.length ? months : [formatChinaMonth(new Date())];
        if (!this.emissionBounds.months.includes(this.emissionBounds.eventMonth)) {
          this.emissionBounds.eventMonth = this.emissionBounds.months[0];
        }
      } catch (error) {
        this.emissionBounds.eventsError = true;
        this.emissionBounds.eventsMessage = error.message;
      }
    },
    async loadEmissionEvents(resetPage = false) {
      if (this.emissionBounds.eventsLoading) return;
      if (resetPage) this.emissionBounds.eventPage = 1;
      this.emissionBounds.eventsLoading = true;
      const query = new URLSearchParams({
        month: this.emissionBounds.eventMonth,
        metric: this.emissionBounds.eventMetric,
        page: String(this.emissionBounds.eventPage),
        pageSize: String(this.emissionBounds.eventPageSize)
      });
      try {
        const result = await apiGet(`/api/emission-bounds/events?${query}`);
        this.emissionBounds.eventPage = result.page;
        this.emissionBounds.eventPageSize = result.pageSize;
        this.emissionBounds.eventTotal = result.total;
        this.emissionBounds.events = result.items;
        this.emissionBounds.eventsError = false;
        this.emissionBounds.eventsMessage = result.total ? `共 ${result.total} 条排放预警记录` : "暂无排放预警记录";
      } catch (error) {
        this.emissionBounds.eventsError = true;
        this.emissionBounds.eventsMessage = error.message;
      } finally {
        this.emissionBounds.eventsLoading = false;
        this.emissionBounds.eventsLastLoadedAt = Date.now();
      }
    },
    changeEmissionEventPage(page) {
      const target = Math.min(Math.max(1, page), this.emissionEventTotalPages);
      if (target === this.emissionBounds.eventPage) return;
      this.emissionBounds.eventPage = target;
      this.loadEmissionEvents(false);
    },
    renderEmissionBoundsCharts() {
      const samples = this.emissionBounds.current?.trend ?? [];
      for (const [key, target] of [["emissionNox", "nox"], ["emissionO2", "o2"]]) {
        const canvasRef = target === "nox" ? "emissionNoxChart" : "emissionO2Chart";
        const canvas = this.$refs[canvasRef];
        if (!canvas) {
          this.charts[key]?.destroy();
          this.charts[key] = null;
          continue;
        }
        const isNox = target === "nox";
        const labels = samples.map((sample) => String(sample.recordedAt ?? "").slice(11, 19));
        const targetDto = this.emissionBounds.current?.[target];
        const datasets = emissionBoundsChartDatasets(samples, target, targetDto);
        const values = datasets.slice(0, 4).flatMap((dataset) => dataset.data);
        const all = values.flat().filter((value) => value !== null);
        const minimum = all.length ? Math.min(...all) : 0;
        const maximum = all.length ? Math.max(...all) : 1;
        const padding = Math.max((maximum - minimum) * 0.16, Math.abs(maximum) * 0.03, 0.1);
        const existing = this.charts[key];
        if (!existing) {
          this.charts[key] = createEmissionBoundsChart(canvas, samples, target, targetDto);
          continue;
        }
        existing.data.labels = labels;
        const visibility = new Map(existing.data.datasets.map((dataset) => [dataset.id ?? dataset.label, dataset.hidden]));
        existing.data.datasets = datasets.map((dataset) => ({
          ...dataset,
          hidden: visibility.get(dataset.id ?? dataset.label) ?? false
        }));
        existing.options.scales.y.suggestedMin = minimum - padding;
        existing.options.scales.y.suggestedMax = maximum + padding;
        existing.update("none");
      }
    },
    setChartVisibility(chartKey, visible) {
      const chart = this.charts[chartKey];
      if (!chart) {
        return;
      }

      chart.data.datasets.forEach((_dataset, index) => {
        chart.setDatasetVisibility(index, visible);
      });
      chart.update("none");
      this.hiddenSeriesByChart[chartKey] = visible
        ? []
        : chart.data.datasets.map((dataset) => dataset.seriesKey).filter(Boolean);
    },
    updateHiddenSeries(chartKey, chart) {
      this.hiddenSeriesByChart[chartKey] = chart.data.datasets
        .filter((_dataset, index) => !chart.isDatasetVisible(index))
        .map((dataset) => dataset.seriesKey)
        .filter(Boolean);
    },
    chartOptions(chartKey, extraOptions = {}) {
      return {
        ...extraOptions,
        hiddenKeys: new Set(this.hiddenSeriesByChart[chartKey] ?? []),
        onVisibilityChange: (chart) => this.updateHiddenSeries(chartKey, chart)
      };
    },
    syncRealtimeTimer() {
      this.stopRealtimeTimer();
      if (this.realtime.autoRefresh) {
        this.refreshRealtime();
        this.realtime.timer = window.setInterval(this.refreshRealtime, this.realtime.intervalSeconds * 1000);
      }
    },
    stopRealtimeTimer() {
      if (this.realtime.timer) {
        window.clearInterval(this.realtime.timer);
        this.realtime.timer = null;
      }
    },
    startCombustionTimer() {
      this.stopCombustionTimer();
      if (this.activeTab !== "combustion") {
        return;
      }

      this.combustion.timer = window.setInterval(() => this.loadCombustionData(), 2000);
    },
    stopCombustionTimer() {
      if (this.combustion.timer) {
        window.clearInterval(this.combustion.timer);
        this.combustion.timer = null;
      }
    },
    async loadCombustionData() {
      if (this.combustion.loading) {
        return;
      }

      this.combustion.loading = true;
      this.combustion.error = false;
      try {
        this.combustion.snapshots = await apiGet("/api/history/latest?count=600") ?? [];
        this.combustion.message = "燃烧参数已刷新";
        const forecastChanged = await this.loadCombustionDailyForecast();
        const consumptionChanged = await this.loadCombustionGasConsumption();
        if (this.activeTab === "combustion") {
          await nextTick();
          this.renderCombustionTrendChart();
          if (forecastChanged || !this.charts.combustionPrediction) {
            this.renderCombustionPredictionChart();
          }
          if (consumptionChanged || !this.charts.combustionConsumption) {
            this.renderCombustionConsumptionChart();
          }
        }
      } catch (error) {
        this.combustion.error = true;
        this.combustion.message = error.message;
      } finally {
        this.combustion.loading = false;
      }
    },
    async loadCombustionDailyForecast(force = false) {
      const latestTime = this.combustion.snapshots.at(-1)?.recordedAt;
      const date = latestTime?.slice(0, 10) ?? formatChinaLocalInput(new Date()).slice(0, 10);
      const now = Date.now();
      if (!force
        && this.combustion.forecastDate === date
        && now - this.combustion.forecastLoadedAt < 15000) {
        return false;
      }

      this.combustion.forecastLoading = true;
      this.combustion.forecastError = false;
      try {
        const params = new URLSearchParams({
          date,
          horizonSeconds: String(this.combustion.horizonSeconds)
        });
        const dailyForecast = await apiGet(`/api/combustion-diagnosis/daily-forecast?${params}`);
        const changed = force
          || this.combustion.dailyForecast?.cachedMinuteCount !== dailyForecast.cachedMinuteCount
          || this.combustion.dailyForecast?.samples?.length !== dailyForecast.samples?.length
          || this.combustion.forecastDate !== date;
        this.combustion.dailyForecast = dailyForecast;
        this.combustion.forecastDate = date;
        this.combustion.forecastLoadedAt = now;
        const count = this.combustion.dailyForecast?.cachedMinuteCount ?? 0;
        this.combustion.forecastMessage = count > 0
          ? `当天已缓存 ${count} 个完整分钟的模型预测，当前显示提前 ${this.combustion.horizonSeconds} 秒。`
          : "当天尚无满足连续 60 秒运行数据的分钟预测缓存。";
        return changed;
      } catch (error) {
        this.combustion.forecastError = true;
        this.combustion.forecastMessage = error.message;
        return false;
      } finally {
        this.combustion.forecastLoading = false;
      }
    },
    async loadCombustionGasConsumption(force = false) {
      const latestTime = this.combustion.snapshots.at(-1)?.recordedAt;
      const date = latestTime?.slice(0, 10) ?? formatChinaLocalInput(new Date()).slice(0, 10);
      const now = Date.now();
      if (!force
        && this.combustion.gasConsumptionDate === date
        && now - this.combustion.gasConsumptionLoadedAt < 15000) {
        return false;
      }

      const trend = await apiGet(`/api/combustion-diagnosis/gas-consumption?date=${encodeURIComponent(date)}`);
      const changed = force
        || this.combustion.gasConsumptionDate !== date
        || JSON.stringify(this.combustion.gasConsumption?.days) !== JSON.stringify(trend?.days);
      this.combustion.gasConsumption = trend;
      this.combustion.gasConsumptionDate = date;
      this.combustion.gasConsumptionLoadedAt = now;
      return changed;
    },
    async changeCombustionHorizon() {
      if (await this.loadCombustionDailyForecast(true)) {
        await nextTick();
        this.renderCombustionPredictionChart();
      }
    },
    async refreshRealtime() {
      this.realtime.loading = true;
      this.realtime.error = false;
      try {
        this.realtime.response = await apiGet("/api/predictions/latest");
        this.realtime.message = `刷新成功 ${this.realtime.response.inputEnd.replace("T", " ").replace("+08:00", "")}`;
        await this.loadLatestEarlyWarnings();
        await this.loadRealtimeFaults({ resetPage: true, silent: true });
        await nextTick();
        this.renderRealtimeCharts();
      } catch (error) {
        this.realtime.error = true;
        this.realtime.message = error.message;
      } finally {
        this.realtime.loading = false;
      }
    },
    async loadFaultOptions() {
      const query = new URLSearchParams({ month: this.runningFaults.month });
      const options = await apiGet(`/api/early-warnings/options?${query}`);
      const months = options.months?.length ? options.months : [options.currentMonth];
      this.runningFaults.months = months.includes(this.runningFaults.month)
        ? months
        : [this.runningFaults.month, ...months];
      this.runningFaults.scenarios = options.scenarios ?? [];
      this.runningFaults.levels = options.levels ?? [];
      if (!this.runningFaults.month) {
        this.runningFaults.month = options.currentMonth;
      }

      if (
        this.runningFaults.scenario !== "all"
        && !this.runningFaults.scenarios.includes(this.runningFaults.scenario)
      ) {
        this.runningFaults.scenario = "all";
      }
      if (
        this.runningFaults.level !== "all"
        && !this.runningFaults.levels.includes(this.runningFaults.level)
      ) {
        this.runningFaults.level = "all";
      }
    },
    async loadRealtimeFaults(options = {}) {
      const { resetPage = false, silent = false } = options;
      if (resetPage) {
        this.runningFaults.page = 1;
      }

      if (!silent) {
        this.runningFaults.loading = true;
      }

      this.runningFaults.error = false;
      try {
        const query = new URLSearchParams({
          month: this.runningFaults.month,
          scenario: this.runningFaults.scenario,
          level: this.runningFaults.level,
          page: String(this.runningFaults.page),
          pageSize: String(this.runningFaults.pageSize)
        });
        const result = await apiGet(`/api/early-warnings/records?${query}`);
        this.runningFaults.month = result.month;
        this.runningFaults.scenario = result.scenario;
        this.runningFaults.level = result.level;
        this.runningFaults.page = result.page;
        this.runningFaults.pageSize = result.pageSize;
        this.runningFaults.total = result.total;
        this.runningFaults.items = result.items ?? [];
        this.runningFaults.message = `共 ${result.total} 条记录`;
      } catch (error) {
        this.runningFaults.error = true;
        this.runningFaults.message = error.message;
      } finally {
        this.runningFaults.loading = false;
      }
    },
    async onFaultMonthChange() {
      await this.loadFaultOptions();
      await this.loadRealtimeFaults({ resetPage: true });
    },
    async onFaultFilterChange() {
      await this.loadRealtimeFaults({ resetPage: true });
    },
    async loadLatestEarlyWarnings() {
      this.earlyWarnings.error = false;
      try {
        this.earlyWarnings.latest = await apiGet("/api/early-warnings/latest");
        this.earlyWarnings.message = this.earlyWarnings.latest.message || "后台预警服务已更新。";
      } catch (error) {
        this.earlyWarnings.error = true;
        this.earlyWarnings.message = error.message;
      }
    },
    async changeFaultPage(page) {
      const targetPage = Math.min(Math.max(1, page), this.runningFaultTotalPages);
      if (targetPage === this.runningFaults.page) {
        return;
      }

      this.runningFaults.page = targetPage;
      await this.loadRealtimeFaults();
    },
    formatMonthLabel(month) {
      return formatMonthLabel(month);
    },
    formatFaultTime(value) {
      return value ? value.replace("T", " ").replace("+08:00", "") : "--";
    },
    formatNumber(value) {
      return formatNumber(value);
    },
    formatWarningValue(value, unit) {
      return Number.isFinite(Number(value)) ? `${formatNumber(value)}${unit ? ` ${unit}` : ""}` : "--";
    },
    combustionValue(propertyName) {
      return formatNumber(this.combustionLatestSnapshot?.[propertyName]);
    },
    combustionActualLoadPercent() {
      const steamFlow = toFiniteNumber(this.combustionLatestSnapshot?.gydcS_K0QHB10CF101_COMP);
      return steamFlow === null ? "--" : formatNumber(steamFlow / 15 * 100);
    },
    async showFaultReport(item) {
      this.faultReport = item;
      this.faultTrend = { data: null, loading: true, error: false, message: "" };
      this.charts.faultTrend?.destroy();
      this.charts.faultTrend = null;

      try {
        const query = new URLSearchParams({ month: item.month });
        const trend = await apiGet(`/api/early-warnings/records/${encodeURIComponent(item.id)}/trend?${query}`);
        if (this.faultReport?.id !== item.id) {
          return;
        }

        this.faultTrend = {
          data: trend,
          loading: false,
          error: false,
          message: trend.message || ""
        };
        await nextTick();
        this.renderFaultTrendChart();
      } catch (error) {
        if (this.faultReport?.id === item.id) {
          this.faultTrend = { data: null, loading: false, error: true, message: error.message };
        }
      }
    },
    closeFaultReport() {
      this.faultReport = null;
      this.faultTrend = { data: null, loading: false, error: false, message: "" };
      this.charts.faultTrend?.destroy();
      this.charts.faultTrend = null;
    },
    faultScoreRatio(item) {
      const score = Number(item?.score);
      const threshold = Number(item?.threshold);
      return Number.isFinite(score) && Number.isFinite(threshold) && threshold > 0
        ? `${formatNumber(score / threshold)} 倍`
        : "--";
    },
    faultResidual(item) {
      const actual = Number(item?.actualValue);
      const reconstructed = Number(item?.reconstructedValue);
      return Number.isFinite(actual) && Number.isFinite(reconstructed)
        ? this.formatWarningValue(actual - reconstructed, item?.unit)
        : "--";
    },
    faultRecommendations(item) {
      return (item?.recommendations ?? "")
        .split("；")
        .map((recommendation) => recommendation.trim())
        .filter(Boolean);
    },
    faultContributorDeviationText(item) {
      const actual = Number(item?.actual);
      const reconstructed = Number(item?.reconstructed);
      if (!Number.isFinite(actual) || !Number.isFinite(reconstructed)) {
        return "--";
      }

      const deviation = actual - reconstructed;
      const equalityTolerance = Math.max(Math.abs(reconstructed) * 1e-6, 1e-6);
      if (Math.abs(deviation) <= equalityTolerance) {
        return "=";
      }
      if (item.direction === "接近正常值") {
        return "-";
      }

      const baseline = Math.abs(reconstructed);
      if (baseline <= 1e-9) {
        return deviation > 0 ? "↑ 无基准" : "↓ 无基准";
      }

      const percentage = deviation / baseline * 100;
      return percentage > 0
        ? `↑ +${formatNumber(percentage)}%`
        : `↓ ${formatNumber(percentage)}%`;
    },
    faultContributorDeviationClass(item) {
      const actual = Number(item?.actual);
      const reconstructed = Number(item?.reconstructed);
      if (!Number.isFinite(actual) || !Number.isFinite(reconstructed)) {
        return "unknown";
      }

      const equalityTolerance = Math.max(Math.abs(reconstructed) * 1e-6, 1e-6);
      if (Math.abs(actual - reconstructed) <= equalityTolerance) {
        return "equal";
      }
      if (item.direction === "接近正常值") {
        return "near";
      }
      return actual > reconstructed ? "higher" : "lower";
    },
    renderFaultTrendChart() {
      const canvas = this.$refs.faultTrendChart;
      const trend = this.faultTrend.data;
      if (!canvas || !trend?.samples?.length || !trend?.points?.length) {
        return;
      }

      this.charts.faultTrend?.destroy();
      const axesByUnit = new Map();
      const scales = {
        x: {
          grid: { display: false },
          ticks: { maxTicksLimit: 7 }
        }
      };
      trend.points.forEach((point) => {
        const unit = point.unit || "无单位";
        if (axesByUnit.has(unit)) {
          return;
        }

        const index = axesByUnit.size;
        const axisId = `unit-${index}`;
        axesByUnit.set(unit, axisId);
        scales[axisId] = {
          type: "linear",
          position: index % 2 === 0 ? "left" : "right",
          grid: { drawOnChartArea: index === 0 },
          title: { display: true, text: unit },
          ticks: { callback: (value) => formatNumber(value) }
        };
      });

      const labels = trend.samples.map((sample) => this.formatFaultTime(sample.recordedAt).slice(11));
      const lastIndex = trend.samples.length - 1;
      let relatedColorIndex = 0;
      const datasets = trend.points.map((point) => {
        const color = point.isTarget
          ? "#c2410c"
          : faultTrendRelatedColors[relatedColorIndex++ % faultTrendRelatedColors.length];
        return {
          label: point.isTarget ? `目标实际值：${point.name}` : point.name,
          unit: point.unit,
          data: trend.samples.map((sample) => sample.values?.[point.field] ?? null),
          yAxisID: axesByUnit.get(point.unit || "无单位"),
          borderColor: color,
          backgroundColor: color,
          borderWidth: point.isTarget ? 2.5 : 1.7,
          pointRadius: (context) => context.dataIndex === lastIndex ? 3 : 0,
          pointHoverRadius: 4,
          tension: 0.15,
          spanGaps: true
        };
      });
      const targetPoint = trend.points.find((point) => point.isTarget);
      const reconstructedValue = Number(this.faultReport?.reconstructedValue);
      if (targetPoint && Number.isFinite(reconstructedValue)) {
        datasets.splice(1, 0, {
          label: "告警时刻模型参考值",
          unit: targetPoint.unit,
          data: Array(trend.samples.length).fill(reconstructedValue),
          yAxisID: axesByUnit.get(targetPoint.unit || "无单位"),
          borderColor: "#f97316",
          backgroundColor: "#f97316",
          borderWidth: 2,
          borderDash: [7, 4],
          pointRadius: 0,
          pointHoverRadius: 4,
          spanGaps: true
        });
      }

      this.charts.faultTrend = markRaw(new Chart(canvas, {
        type: "line",
        data: { labels, datasets },
        options: {
          animation: false,
          maintainAspectRatio: false,
          interaction: { mode: "index", intersect: false },
          plugins: {
            legend: { position: "bottom", labels: { boxWidth: 10, usePointStyle: true } },
            tooltip: {
              mode: "index",
              intersect: false,
              callbacks: {
                label: (context) => {
                  return `${context.dataset.label}: ${formatNumber(context.parsed.y)} ${context.dataset.unit ?? ""}`;
                }
              }
            }
          },
          scales
        }
      }));
    },
    earlyWarningScenarioText(scenario) {
      return {
        nox: "NOx 排放浓度",
        temperature: "减温器出口温度",
        pressure: "过热蒸汽压力"
      }[scenario] ?? scenario;
    },
    faultLevelText(level) {
      return level === "alarm" ? "报警（三级）" : "预警（二级）";
    },
    faultLevelClass(level) {
      return level === "alarm" ? "fault" : "warning";
    },
    async loadHistoryOverview() {
      if (!this.history.date) {
        this.history.error = true;
        this.history.message = "请选择查询日期。";
        return;
      }

      this.history.loadingOverview = true;
      this.history.error = false;
      try {
        const query = new URLSearchParams({
          date: this.history.date,
          intervalSeconds: String(this.history.intervalSeconds)
        });
        this.history.overview = await apiGet(`/api/history/day/overview?${query}`);
        this.history.message = `概览已加载：${this.history.overview.sampleCount} 个采样点`;
        await nextTick();
        this.renderOverviewChart();
      } catch (error) {
        this.history.error = true;
        this.history.message = error.message;
      } finally {
        this.history.loadingOverview = false;
      }
    },
    async loadHistoryPrediction() {
      if (!this.history.selectedLocalTime) {
        this.history.error = true;
        this.history.message = "请选择预测时间。";
        return false;
      }

      this.history.loadingPrediction = true;
      this.history.error = false;
      try {
        const time = encodeURIComponent(toChinaApiTime(this.history.selectedLocalTime));
        this.history.response = await apiGet(`/api/predictions/history?time=${time}`);
        this.history.message = `历史预测完成：${this.history.response.inputStart.slice(11, 19)} - ${this.history.response.forecastEnd.slice(11, 19)}`;
        await nextTick();
        this.renderHistoryCharts();
        return true;
      } catch (error) {
        this.history.error = true;
        this.history.message = error.message;
        return false;
      } finally {
        this.history.loadingPrediction = false;
      }
    },
    moveHistory(seconds) {
      this.stepHistory(seconds);
    },
    async stepHistory(seconds) {
      if (this.history.loadingPrediction) {
        return;
      }

      if (!this.history.selectedLocalTime) {
        this.history.error = true;
        this.history.message = "请选择预测时间。";
        this.stopHistoryAuto();
        return;
      }

      this.history.selectedLocalTime = shiftLocalSeconds(this.history.selectedLocalTime, seconds);
      const success = await this.loadHistoryPrediction();
      if (!success && this.history.autoDirection !== 0) {
        this.stopHistoryAuto("自动步进已停止。");
      }
    },
    startHistoryAuto(direction) {
      if (!this.history.selectedLocalTime) {
        this.history.error = true;
        this.history.message = "请选择预测时间。";
        return;
      }

      this.stopHistoryAuto();
      this.history.autoDirection = direction;
      this.history.message = direction > 0 ? "自动下一秒已启动。" : "自动上一秒已启动。";
      this.stepHistory(direction);
      this.syncHistoryAutoTimer();
    },
    syncHistoryAutoTimer() {
      if (this.history.autoTimer) {
        window.clearInterval(this.history.autoTimer);
        this.history.autoTimer = null;
      }

      if (this.history.autoDirection !== 0) {
        const direction = this.history.autoDirection;
        this.history.autoTimer = window.setInterval(() => {
          this.stepHistory(direction);
        }, this.history.autoIntervalMs);
      }
    },
    stopHistoryAuto(message = "") {
      if (this.history.autoTimer) {
        window.clearInterval(this.history.autoTimer);
        this.history.autoTimer = null;
      }

      this.history.autoDirection = 0;
      if (message) {
        this.history.message = message;
      }
    },
    rerenderCharts() {
      this.renderOverviewChart();
      this.renderRealtimeCharts();
      this.renderCombustionDashboardCharts();
      this.renderEmissionBoundsCharts();
      this.renderHistoryCharts();
    },
    renderCombustionDashboardCharts() {
      if (this.activeTab !== "combustion") {
        return;
      }

      this.renderCombustionPredictionChart();
      this.renderCombustionTrendChart();
      this.renderCombustionConsumptionChart();
    },
    renderCombustionPredictionChart() {
      const canvas = this.$refs.combustionPredictionChart;
      if (!canvas) {
        return;
      }

      this.charts.combustionPrediction?.destroy();
      const samples = this.combustion.dailyForecast?.samples ?? [];
      const getValue = (values, pointName) => {
        if (!values) {
          return null;
        }

        const key = Object.keys(values).find((item) => item.toLowerCase() === pointName.toLowerCase());
        return key ? toFiniteNumber(values[key]) : null;
      };
      const series = this.targetPoints.flatMap((point, index) => {
        const color = chartColors[index % chartColors.length];
        return [
          {
            key: `${point.pointName}:daily-actual`,
            point,
            label: `${point.label}实际`,
            color,
            dashed: false,
            points: samples.map((sample) => ({
              timestamp: sample.targetTime,
              value: getValue(sample.actualValues, point.pointName)
            }))
          },
          {
            key: `${point.pointName}:daily-predicted`,
            point,
            label: `${point.label}预测`,
            color,
            dashed: true,
            points: samples.map((sample) => ({
              timestamp: sample.targetTime,
              value: getValue(sample.predictedValues, point.pointName)
            }))
          }
        ];
      });
      this.charts.combustionPrediction = createLineChart(
        canvas,
        series,
        this.chartOptions("combustionPrediction")
      );
      initializeCombustionPredictionData(this.charts.combustionPrediction);
      applyCombustionPredictionZoom(this.charts.combustionPrediction, this.combustion.predictionZoom);
      attachCombustionPredictionZoom(canvas, this.charts.combustionPrediction, (zoom) => {
        this.combustion.predictionZoom = { ...(this.combustion.predictionZoom ?? {}), ...zoom };
      });
    },
    renderCombustionTrendChart() {
      const canvas = this.$refs.combustionTrendChart;
      if (!canvas) {
        return;
      }

      const snapshots = downsampleSnapshots(this.combustion.snapshots, 10);
      const properties = [
        "gydcS_K0QHB10CF101_COMP",
        "gydcS_K0QHH10CF101",
        "gydcS_K0HNE10CQ103"
      ];
      const points = properties
        .map((property) => this.metadata.points.find((point) => point.snapshotPropertyName === property))
        .filter(Boolean);
      const series = buildSnapshotSeries(snapshots, points);
      const actualLoadSeries = series.find((item) => item.point.snapshotPropertyName === "gydcS_K0QHB10CF101_COMP");
      if (actualLoadSeries) {
        actualLoadSeries.key = "actual-load-percent";
        actualLoadSeries.label = "实际负荷";
        actualLoadSeries.point = {
          ...actualLoadSeries.point,
          pointName: "actual-load-percent",
          label: "实际负荷",
          unit: "%",
          lowScale: null,
          highScale: null
        };
        actualLoadSeries.points = actualLoadSeries.points.map((point) => ({
          ...point,
          value: point.value / 15 * 100
        }));
      }
      this.charts.combustionTrend?.destroy();
      this.charts.combustionTrend = createLineChart(
        canvas,
        series,
        this.chartOptions("combustionTrend")
      );
    },
    renderCombustionConsumptionChart() {
      const canvas = this.$refs.combustionConsumptionChart;
      if (!canvas) {
        return;
      }

      this.charts.combustionConsumption?.destroy();
      const days = this.combustion.gasConsumption?.days ?? [];
      this.charts.combustionConsumption = markRaw(new Chart(canvas, {
        type: "bar",
        data: {
          labels: days.map((item) => `${item.date.slice(5, 10).replace("-", "/")}`),
          datasets: [{
            label: "Nm³/t",
            data: days.map((item) => toFiniteNumber(item.consumptionPerTon) ?? 0),
            backgroundColor: "#f97316",
            borderRadius: 2,
            maxBarThickness: 42
          }]
        },
        options: this.combustionChartOptions("Nm³/t")
      }));
    },
    combustionChartOptions(title, normalized = false) {
      const yScale = normalized
        ? {
          min: 0,
          max: 100,
          grid: { color: "#e7edf2" },
          title: { display: true, text: title },
          ticks: { stepSize: 25, callback: (value) => `${formatNumber(value)}%` }
        }
        : {
          grid: { color: "#e7edf2" },
          title: { display: true, text: title },
          ticks: { callback: (value) => formatNumber(value) }
        };

      return {
        animation: false,
        maintainAspectRatio: false,
        interaction: { mode: "index", intersect: false },
        plugins: {
          legend: { position: "top", align: "start", labels: { boxWidth: 10, usePointStyle: true } },
          tooltip: { callbacks: { label: (context) => `${context.dataset.label}: ${formatNumber(context.parsed.y)}${normalized ? "%" : ""}` } }
        },
        scales: {
          x: { grid: { display: false } },
          y: yScale
        }
      };
    },
    renderOverviewChart() {
      if (!this.$refs.overviewChart || !this.history.overview) {
        return;
      }

      this.charts.overview?.destroy();
      const points = this.overviewPoints;
      const series = buildSnapshotSeries(this.history.overview.samples, points);
      this.charts.overview = createLineChart(this.$refs.overviewChart, series, this.chartOptions("overview", {
        onClick: (_event, elements) => {
          if (!elements.length) {
            return;
          }

          const element = elements[0];
          const dataset = this.charts.overview.data.datasets[element.datasetIndex];
          const item = dataset.data[element.index];
          if (item?.time) {
            this.history.selectedLocalTime = toLocalInputValue(item.time);
            this.loadHistoryPrediction();
          }
        }
      }));
    },
    renderRealtimeCharts() {
      if (!this.realtime.response) {
        return;
      }

      this.renderPredictionCharts(
        "realtime",
        this.realtime.response,
        this.$refs.realtimeInputChart,
        this.$refs.realtimeTargetChart);
    },
    renderHistoryCharts() {
      if (!this.history.response) {
        return;
      }

      this.renderPredictionCharts(
        "history",
        this.history.response,
        this.$refs.historyInputChart,
        this.$refs.historyTargetChart);
    },
    renderPredictionCharts(prefix, response, inputCanvas, targetCanvas) {
      if (!inputCanvas || !targetCanvas) {
        return;
      }

      const inputKey = `${prefix}Input`;
      const targetKey = `${prefix}Target`;
      this.charts[inputKey]?.destroy();
      this.charts[targetKey]?.destroy();

      const inputSeries = buildSnapshotSeries(response.inputData ?? [], this.inputPoints);
      const actualSeries = buildSnapshotSeries(response.actualFutureData ?? [], this.targetPoints, "实际");
      const forecastSeries = buildForecastSeries(response.forecast, this.targetPoints);
      this.charts[inputKey] = createLineChart(inputCanvas, inputSeries, this.chartOptions(inputKey));
      this.charts[targetKey] = createLineChart(
        targetCanvas,
        actualSeries.concat(forecastSeries),
        this.chartOptions(targetKey));
    }
  }
}).mount("#app");
