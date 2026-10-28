(function () {
  // element -> { kind, chart?, canvas?, width, hover }
  const charts = new WeakMap();
  const models = new Map();
  const observers = new WeakMap();
  const fallbackColor = "#71717a";

  function read(model, key) {
    const lower = key.charAt(0).toLowerCase() + key.slice(1);
    return model?.[lower] ?? model?.[key];
  }

  // Colours are design tokens (CSS custom properties), so light and dark themes come from the stylesheet.
  function token(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  }

  function axisColor() {
    return token("--muted-foreground", fallbackColor);
  }

  function gridColor() {
    return token("--border", "#e4e4e7");
  }

  function surfaceColor() {
    return token("--card", "#ffffff");
  }

  function axisFont() {
    return `11px ${token("--font-sans", "system-ui, sans-serif")}`;
  }

  function destroy(element) {
    models.delete(element);
    observers.get(element)?.disconnect();
    observers.delete(element);
    teardown(element);
  }

  function teardown(element) {
    const existing = charts.get(element);
    if (!existing) {
      return;
    }

    if (existing.kind === "uplot") {
      existing.chart.destroy();
    }

    charts.delete(element);
    element.replaceChildren();
  }

  function render(element, model) {
    if (!element || !model) {
      return;
    }

    models.set(element, model);
    observe(element);
    if (window.uPlot) {
      renderUPlot(element, model);
      return;
    }

    renderCanvas(element, model);
  }

  // Redraw when the card changes width (sidebar toggle, window resize); uPlot draws at a fixed size.
  function observe(element) {
    if (observers.has(element) || typeof ResizeObserver === "undefined") {
      return;
    }

    let frame = 0;
    const observer = new ResizeObserver(() => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(() => {
        const existing = charts.get(element);
        const model = models.get(element);
        if (model && element.isConnected && existing && Math.floor(element.getBoundingClientRect().width) !== existing.width) {
          render(element, model);
        }
      });
    });
    observer.observe(element);
    observers.set(element, observer);

    element.addEventListener("pointerleave", () => {
      const existing = charts.get(element);
      if (existing) {
        existing.hover = null;
        hideHover(element);
      }
    });
  }

  function renderUPlot(element, model) {
    const timestamps = read(model, "Timestamps") ?? [];
    const datasets = chartSeries(model);
    const data = [timestamps, ...datasets.map((series) => series.values)];
    const visibleSeries = datasets.filter((series) => series.visible);
    const scale = scaleInfo(timestamps, visibleSeries);
    const timeScale = timeScaleRange(model, timestamps);
    const hover = charts.get(element)?.hover ?? null;
    const surface = surfaceColor();

    teardown(element);
    const size = chartSize(element);
    const options = {
      ...size,
      cursor: {
        x: false,
        y: false,
        drag: { x: false, y: false },
        points: {
          size: 9,
          width: 2,
          stroke: () => surface,
          fill: (chart, seriesIndex) => datasets[seriesIndex - 1]?.color ?? fallbackColor,
        },
      },
      legend: {
        show: false,
      },
      scales: {
        x: { time: true, range: [timeScale.min, timeScale.max] },
        y: { range: [0, scale.max] },
      },
      axes: [
        {
          stroke: axisColor(),
          font: axisFont(),
          grid: { show: false },
          ticks: { stroke: gridColor(), width: 1, size: 4 },
        },
        {
          stroke: axisColor(),
          font: axisFont(),
          grid: { stroke: gridColor(), width: 1 },
          ticks: { show: false },
          values: (_, values) => values.map(formatCount),
        },
      ],
      series: [
        {},
        ...datasets.map((dataset) => ({
          label: dataset.label,
          show: dataset.visible,
          stroke: dataset.color,
          width: 2,
          points: { show: false },
        })),
      ],
      hooks: {
        setCursor: [(chart) => updateHover(element, chart, model, datasets)],
      },
    };

    const chart = new window.uPlot(options, data, element);
    chart.setScale("x", timeScale);
    chart.setScale("y", { min: 0, max: scale.max });
    charts.set(element, { kind: "uplot", chart, width: size.width, hover: null });
    updateDebugAttributes(element, model, timestamps, data);
    renderClipMarkers(element, scale.clipped, timestamps, chart);

    // Keep the readout under a pointer that is still over the chart when live data redraws it.
    if (hover) {
      chart.setCursor(hover);
    }
  }

  function renderCanvas(element, model) {
    let existing = charts.get(element);
    if (existing?.kind !== "canvas") {
      teardown(element);
      const canvas = document.createElement("canvas");
      canvas.className = "throughput-chart__canvas";
      element.appendChild(canvas);
      existing = { kind: "canvas", canvas, width: 0, hover: null };
      charts.set(element, existing);
    }

    existing.width = drawCanvas(existing.canvas, model);
    updateDebugAttributes(element, model, read(model, "Timestamps") ?? [], []);
  }

  function drawCanvas(canvas, model) {
    const rect = canvas.parentElement.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    const width = Math.max(320, Math.floor(rect.width));
    const height = Math.max(200, Math.floor(rect.height));
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;

    const context = canvas.getContext("2d");
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
    context.clearRect(0, 0, width, height);

    const padding = { top: 14, right: 18, bottom: 24, left: 40 };
    const plotWidth = width - padding.left - padding.right;
    const plotHeight = height - padding.top - padding.bottom;
    const timestamps = read(model, "Timestamps") ?? [];
    const visibleDatasets = chartSeries(model).filter((dataset) => dataset.visible);
    const pointCount = timestamps.length;
    const scale = scaleInfo(timestamps, visibleDatasets);
    const maxValue = scale.max;

    context.strokeStyle = gridColor();
    context.lineWidth = 1;
    for (let index = 0; index <= 4; index++) {
      const y = padding.top + (plotHeight / 4) * index;
      context.beginPath();
      context.moveTo(padding.left, y);
      context.lineTo(width - padding.right, y);
      context.stroke();
    }

    context.fillStyle = axisColor();
    context.font = axisFont();
    context.textAlign = "right";
    context.textBaseline = "middle";
    for (let index = 0; index <= 4; index++) {
      const value = Math.round(maxValue - (maxValue / 4) * index);
      const y = padding.top + (plotHeight / 4) * index;
      context.fillText(formatCount(value), padding.left - 8, y);
    }

    context.lineJoin = "round";
    context.lineCap = "round";
    for (const dataset of visibleDatasets) {
      context.strokeStyle = dataset.color;
      context.lineWidth = 2;
      context.beginPath();
      for (let index = 0; index < pointCount; index++) {
        const x = padding.left + (plotWidth * index) / Math.max(1, pointCount - 1);
        const value = Math.min(dataset.values[index] ?? 0, maxValue);
        const y = padding.top + plotHeight - (plotHeight * value) / maxValue;
        if (index === 0) {
          context.moveTo(x, y);
        } else {
          context.lineTo(x, y);
        }
      }

      context.stroke();
    }

    renderClipMarkers(canvas.parentElement, scale.clipped, timestamps);
    return width;
  }

  // Crosshair + readout: a hairline snapped to the nearest bucket and one tooltip listing every visible series.
  function updateHover(element, chart, model, datasets) {
    const index = chart.cursor.idx;
    const state = charts.get(element);
    if (index == null || chart.cursor.left == null || chart.cursor.left < 0) {
      if (state) {
        state.hover = null;
      }

      hideHover(element);
      return;
    }

    if (state) {
      state.hover = { left: chart.cursor.left, top: chart.cursor.top };
    }

    const timestamp = chart.data[0][index];
    const x = chart.valToPos(timestamp, "x");
    const over = chart.over;

    let crosshair = over.querySelector(".throughput-chart__crosshair");
    if (!crosshair) {
      crosshair = document.createElement("div");
      crosshair.className = "throughput-chart__crosshair";
      crosshair.setAttribute("aria-hidden", "true");
      over.appendChild(crosshair);
    }

    crosshair.style.transform = `translateX(${Math.round(x)}px)`;
    crosshair.hidden = false;

    let tooltip = element.querySelector(".throughput-chart__tooltip");
    if (!tooltip) {
      tooltip = document.createElement("div");
      tooltip.className = "throughput-chart__tooltip";
      // Pointer-only readout; the legend totals and the window table carry the same numbers for everyone.
      tooltip.setAttribute("aria-hidden", "true");
      element.appendChild(tooltip);
    }

    const bucketSeconds = read(model, "BucketSizeSeconds") ?? 0;
    const heading = document.createElement("div");
    heading.className = "throughput-chart__tooltip-time";
    heading.textContent = `${formatTime(timestamp)} – ${formatTime(timestamp + bucketSeconds)}`;

    const rows = datasets
      .filter((dataset) => dataset.visible)
      .map((dataset) => {
        const row = document.createElement("div");
        row.className = "throughput-chart__tooltip-row";
        const key = document.createElement("span");
        key.className = "throughput-chart__tooltip-key";
        key.style.backgroundColor = dataset.color;
        const value = document.createElement("strong");
        value.textContent = (dataset.values[index] ?? 0).toLocaleString();
        const label = document.createElement("span");
        label.textContent = dataset.label;
        row.append(key, value, label);
        return row;
      });

    if (rows.length === 0) {
      const empty = document.createElement("div");
      empty.className = "throughput-chart__tooltip-empty";
      empty.textContent = "All series are hidden";
      rows.push(empty);
    }

    tooltip.replaceChildren(heading, ...rows);
    tooltip.hidden = false;

    // Beside the crosshair, flipped to the left near the right edge, and kept inside the chart.
    const bounds = element.getBoundingClientRect();
    const anchorX = over.offsetLeft + x;
    const anchorY = over.offsetTop + (chart.cursor.top ?? 0);
    const width = tooltip.offsetWidth;
    const height = tooltip.offsetHeight;
    const left = anchorX + 12 + width <= bounds.width ? anchorX + 12 : Math.max(0, anchorX - 12 - width);
    const top = Math.min(Math.max(0, anchorY - height / 2), Math.max(0, bounds.height - height));
    tooltip.style.transform = `translate(${Math.round(left)}px, ${Math.round(top)}px)`;
  }

  function hideHover(element) {
    const tooltip = element.querySelector(".throughput-chart__tooltip");
    if (tooltip) {
      tooltip.hidden = true;
    }

    const crosshair = element.querySelector(".throughput-chart__crosshair");
    if (crosshair) {
      crosshair.hidden = true;
    }
  }

  function chartSize(element) {
    const rect = element.getBoundingClientRect();
    return {
      width: Math.max(320, Math.floor(rect.width)),
      height: Math.max(200, Math.floor(rect.height)),
    };
  }

  function timeScaleRange(model, timestamps) {
    const min = read(model, "WindowStartUnixSeconds") ?? timestamps[0] ?? 0;
    const max = read(model, "WindowEndUnixSeconds") ?? timestamps[timestamps.length - 1] ?? min;
    return { min, max: Math.max(min + 1, max) };
  }

  function chartSeries(model) {
    return (read(model, "Series") ?? []).map((series) => ({
      label: read(series, "Label") ?? "",
      color: token(read(series, "ColorToken") ?? "", fallbackColor),
      visible: read(series, "Visible") !== false,
      values: read(series, "Values") ?? [],
    }));
  }

  function updateDebugAttributes(element, model, timestamps, data) {
    const currentCount = Number.parseInt(element.dataset.renderCount ?? "0", 10);
    element.dataset.renderCount = `${Number.isFinite(currentCount) ? currentCount + 1 : 1}`;
    element.dataset.windowEnd = `${read(model, "WindowEndUnixSeconds") ?? ""}`;
    element.dataset.pointCount = `${timestamps.length}`;
    element.dataset.seriesCount = `${Math.max(0, data.length - 1)}`;
  }

  // The y-axis ignores a lone spike far above the rest so ordinary traffic stays readable; values above the
  // axis get a marker at the top edge instead.
  function scaleInfo(timestamps, series) {
    const values = series
      .flatMap((series) => Array.from(series.values))
      .filter((value) => Number.isFinite(value) && value > 0)
      .sort((left, right) => left - right);

    if (values.length === 0) {
      return { max: 1, clipped: [] };
    }

    const max = values[values.length - 1];
    if (values.length === 1) {
      const yMax = niceMax(max * 1.1);
      return { max: yMax, clipped: clippedPoints(timestamps, series, yMax) };
    }

    const p95 = values[Math.floor((values.length - 1) * 0.95)];
    const target = max > p95 * 5 ? p95 * 1.25 : max * 1.05;
    const yMax = niceMax(Math.max(1, target));
    return { max: yMax, clipped: clippedPoints(timestamps, series, yMax) };
  }

  function clippedPoints(timestamps, series, yMax) {
    return series.flatMap((series) =>
      series.values
        .map((value, index) => ({
          timestamp: timestamps[index],
          value,
          label: series.label,
          color: series.color,
        }))
        .filter((point) => Number.isFinite(point.timestamp) && Number.isFinite(point.value) && point.value > yMax),
    );
  }

  function renderClipMarkers(element, clipped, timestamps, chart) {
    element.querySelector(".throughput-chart__clip-layer")?.remove();
    if (clipped.length === 0) {
      return;
    }

    const bounds = plotBounds(element, chart);
    const start = timestamps[0] ?? 0;
    const end = timestamps[timestamps.length - 1] ?? start;
    const span = Math.max(1, end - start);
    const layer = document.createElement("div");
    layer.className = "throughput-chart__clip-layer";
    layer.setAttribute("aria-hidden", "true");

    const timestampCounts = new Map();
    for (const point of clipped) {
      const stackIndex = timestampCounts.get(point.timestamp) ?? 0;
      timestampCounts.set(point.timestamp, stackIndex + 1);
      const marker = document.createElement("span");
      marker.className = "throughput-chart__clip-marker";
      marker.title = `${point.label}: ${point.value.toLocaleString()}, above the axis`;
      marker.style.backgroundColor = point.color;
      marker.style.left = `${xPosition(point.timestamp, start, span, bounds, chart)}px`;
      marker.style.top = `${bounds.top + 2 + stackIndex * 10}px`;
      layer.appendChild(marker);
    }

    element.appendChild(layer);
  }

  function plotBounds(element, chart) {
    if (chart?.over) {
      return {
        left: chart.over.offsetLeft,
        top: chart.over.offsetTop,
        width: chart.over.offsetWidth,
        height: chart.over.offsetHeight,
      };
    }

    return {
      left: 40,
      top: 14,
      width: Math.max(1, element.getBoundingClientRect().width - 58),
      height: Math.max(1, element.getBoundingClientRect().height - 38),
    };
  }

  function xPosition(timestamp, start, span, bounds, chart) {
    if (chart?.valToPos) {
      const position = chart.valToPos(timestamp, "x");
      if (Number.isFinite(position)) {
        return bounds.left + position;
      }
    }

    return bounds.left + bounds.width * ((timestamp - start) / span);
  }

  function niceMax(value) {
    const exponent = Math.floor(Math.log10(value));
    const magnitude = 10 ** exponent;
    const normalized = value / magnitude;
    const steps = [1, 1.25, 1.5, 2, 2.5, 5, 7.5, 10];
    return (steps.find((step) => normalized <= step) ?? 10) * magnitude;
  }

  const compact = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });

  function formatCount(value) {
    return Math.abs(value) >= 1000 ? compact.format(value) : value.toLocaleString();
  }

  function formatTime(unixSeconds) {
    return new Date(unixSeconds * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  }

  // Colours come from theme tokens, so redraw when the theme changes.
  function redrawAll() {
    for (const [element, model] of models) {
      if (element.isConnected) {
        render(element, model);
      } else {
        models.delete(element);
      }
    }
  }

  document.addEventListener("sheddueller:themechange", redrawAll);
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", redrawAll);

  (window.sheddueller ??= {}).throughputChart = { render, destroy };
})();
