// Chart helpers for RuleHealth. ECharts instances live here, outside Alpine state: Alpine wraps
// state in proxies, which ECharts does not tolerate.
// Look: the 'cerberus' theme (ui/brand/echarts-cerberus.js) supplies fonts, text, axes, grid lines,
// legend and tooltip chrome. Series colours come only from RuleHealthCharts (tokens); tooltip HTML
// uses the .rh-sw / .rh-tt-* classes, never inline colours.
'use strict';

const RHCharts = (() => {
  const instances = new Set();
  const observers = new WeakMap();
  const S = RuleHealthCharts.series;
  const token = RuleHealthCharts.token;
  RuleHealthCharts.registerTheme(echarts);

  // Classification order is fixed: TP, BP, FP, Undetermined, Open (legend, stack, tooltip).
  const CLASSES = [
    { key: 'tp', label: 'TP' },
    { key: 'bp', label: 'BP' },
    { key: 'fp', label: 'FP' },
    { key: 'undetermined', label: 'Undetermined' },
    { key: 'open', label: 'Open' },
  ].map(c => ({ ...c, get color() { return RuleHealthCharts.classificationColor(c.label === 'Open' ? null : c.label); } }));

  // Per-rule lines: slots 1–4 solid, 5–8 dashed, and a marker shape per slot, so identity never
  // rests on colour alone (the palette has four colours, used twice; see rulehealth-theme.css).
  const SLOT_SYMBOLS = ['circle', 'rect', 'triangle', 'diamond', 'emptyCircle', 'emptyRect', 'emptyTriangle', 'emptyDiamond'];

  const esc = text => echarts.format.encodeHTML(String(text ?? ''));
  const pct = (num, den) => (den ? `${Math.round((num / den) * 100)} %` : '—');
  const swatch = cls => `<span class="rh-sw ${cls}"></span>`;

  /** init once per element (cerberus theme, SVG renderer), replace the option, resize with the container. */
  function renderChart(el, option, onClick) {
    if (!el) return null;
    let chart = echarts.getInstanceByDom(el);
    if (!chart) {
      chart = echarts.init(el, 'cerberus', { renderer: 'svg' });
      instances.add(chart);
      const ro = new ResizeObserver(() => chart.resize());
      ro.observe(el);
      observers.set(chart, ro);
    }
    chart.setOption(option, true);
    chart.off('click');
    if (onClick) chart.on('click', onClick);
    return chart;
  }

  /** Dispose charts whose element left the page (view change). */
  function disposeDetached() {
    for (const chart of [...instances]) {
      if (!chart.getDom().isConnected) {
        observers.get(chart)?.disconnect();
        chart.dispose();
        instances.delete(chart);
      }
    }
  }

  // Motion 120–200 ms (brand); tooltips stay inside the chart.
  const base = () => ({ animationDuration: 200, tooltip: { confine: true } });
  const shadowPointer = () => ({ type: 'shadow', shadowStyle: { color: token('--rh-hover') } });
  const legend = () => ({ top: 0, left: 0 });

  /** Stacked segments; the theme draws the 1px --rh-series-gap separator between them. */
  function stackedSeries(parts) {
    return parts.map(p => ({
      name: p.label, type: 'bar', stack: 'total', barMaxWidth: 24,
      itemStyle: { color: p.color },
      emphasis: { focus: 'series' },
      data: p.data,
    }));
  }

  function classRows(row) {
    return CLASSES.map(c => `<tr><td>${swatch(c.key)}${c.label}</td><td style="text-align:right;padding-left:16px">${row[c.key]}</td></tr>`).join('');
  }

  /** Horizontal stacked bar: incidents per rule by classification; absolute or 100 %. */
  function perRuleOption(rows, percent) {
    const ordered = [...rows].reverse(); // ECharts draws category axis bottom-up
    const value = (r, key) => (percent ? (r.total ? (r[key] / r.total) * 100 : 0) : r[key]);
    const parts = CLASSES.map(c => ({ label: c.label, color: c.color, data: ordered.map(r => value(r, c.key)) }));
    return {
      ...base(),
      legend: legend(),
      grid: { left: 8, right: 24, top: 32, bottom: 8, containLabel: true },
      tooltip: {
        ...base().tooltip, trigger: 'axis', axisPointer: shadowPointer(),
        formatter: params => {
          const r = ordered[params[0].dataIndex];
          const n = r.tp + r.bp + r.fp;
          return `<div style="max-width:360px;white-space:normal"><b>${esc(r.name)}</b></div>`
            + `<table style="margin-top:4px;font-variant-numeric:tabular-nums">${classRows(r)}</table>`
            + `<div class="rh-tt-muted" style="margin-top:4px">${r.total} incidents · FP ${pct(r.fp, n)} (${r.fp}/${n})${n < 5 ? ' · low n' : ''}</div>`;
        },
      },
      xAxis: { type: 'value', max: percent ? 100 : null, axisLabel: { formatter: v => (percent ? `${v} %` : v) } },
      yAxis: { type: 'category', data: ordered.map(r => r.label), axisLabel: { width: 280, overflow: 'truncate' } },
      series: stackedSeries(parts).map(s => ({
        ...s,
        // Rules with fewer than 5 classified incidents are greyed (and labelled "low n").
        data: s.data.map((v, i) => (ordered[i].lowN ? { value: v, itemStyle: { opacity: 0.5 } } : v)),
      })),
    };
  }

  /**
   * Scatter: incidents (log) vs noise rate = (BP + FP) / classified. Points at/over the threshold carry
   * the FP colour (the strongest "noise" signal), the rest recede. The tooltip splits BP and FP.
   */
  function noiseOption(points, threshold) {
    const muted = token('--rh-text-3');
    return {
      ...base(),
      grid: { left: 8, right: 24, top: 24, bottom: 8, containLabel: true },
      tooltip: {
        ...base().tooltip, trigger: 'item',
        formatter: p => {
          const r = p.data.row;
          return `<div style="max-width:360px;white-space:normal"><b>${esc(r.name)}</b></div>`
            + `<div style="margin-top:4px">BP + FP ${pct(r.noise, r.n)} (${r.noise}/${r.n} classified)</div>`
            + `<div class="rh-tt-muted">BP ${r.bp} · FP ${r.fp}</div>`
            + `<div class="rh-tt-muted">${r.incidents} incidents in the window</div>`;
        },
      },
      xAxis: { type: 'log', logBase: 10, min: 1, name: 'Incidents (log)', nameLocation: 'middle', nameGap: 28 },
      yAxis: { type: 'value', min: 0, max: 1, name: 'BP + FP rate', nameTextStyle: { align: 'left' }, axisLabel: { formatter: v => `${Math.round(v * 100)} %` } },
      series: [{
        type: 'scatter',
        data: points.map(r => ({
          value: [r.incidents, r.noiseRate], row: r,
          itemStyle: { color: r.noiseRate >= threshold ? S.fp : muted, borderColor: S.gap, borderWidth: 1 },
        })),
        emphasis: { scale: 1.4 },
        markLine: {
          silent: true, symbol: 'none',
          label: { formatter: `Noise threshold ${Math.round(threshold * 100)} %`, position: 'insideEndTop' },
          data: [{ yAxis: threshold }],
        },
      }],
    };
  }

  /** Weekly stacked columns by classification. */
  function weeklyOption(weeks) {
    const parts = CLASSES.map(c => ({ label: c.label, color: c.color, data: weeks.map(w => w[c.key]) }));
    return {
      ...base(),
      legend: legend(),
      grid: { left: 8, right: 16, top: 32, bottom: 8, containLabel: true },
      tooltip: {
        ...base().tooltip, trigger: 'axis', axisPointer: shadowPointer(),
        formatter: params => {
          const w = weeks[params[0].dataIndex];
          return `<b>Week of ${esc(w.label)}</b><table style="margin-top:4px;font-variant-numeric:tabular-nums">${classRows(w)}</table>`
            + `<div class="rh-tt-muted" style="margin-top:4px">${w.total} incidents</div>`;
        },
      },
      xAxis: { type: 'category', data: weeks.map(w => w.label) },
      yAxis: { type: 'value', minInterval: 1 },
      series: stackedSeries(parts),
    };
  }

  /**
   * Multi-line: incidents per rule per time bucket. series = [{ ruleId, name, slot (1-8), data[] }].
   * Crosshair tooltip listing every series, values first.
   */
  function ruleTrendOption(labels, series, unitLabel) {
    return {
      ...base(),
      // Each legend entry shows its slot's marker shape (the theme's square would make slot 1 and 5 identical).
      legend: {
        ...legend(), type: 'scroll', textStyle: { width: 240, overflow: 'truncate' },
        itemWidth: 12, itemHeight: 12,
        data: series.map(s => ({ name: s.name, icon: SLOT_SYMBOLS[s.slot - 1] })),
      },
      grid: { left: 8, right: 16, top: 36, bottom: 8, containLabel: true },
      tooltip: {
        ...base().tooltip, trigger: 'axis',
        axisPointer: { type: 'line', lineStyle: { color: token('--rh-border-strong'), width: 1 } },
        formatter: params => {
          const rows = [...params].sort((a, b) => b.value - a.value).map(p =>
            `<tr><td style="text-align:right;padding-right:10px;font-weight:600;font-variant-numeric:tabular-nums">${p.value}</td>`
            + `<td>${swatch(`is-line s${series[p.seriesIndex].slot}`)}<span class="rh-tt-muted">${esc(p.seriesName)}</span></td></tr>`).join('');
          return `<b>${esc(unitLabel)} ${esc(labels[params[0].dataIndex])}</b><table style="margin-top:4px;max-width:420px">${rows}</table>`;
        },
      },
      xAxis: { type: 'category', boundaryGap: false, data: labels },
      yAxis: { type: 'value', minInterval: 1 },
      series: series.map(s => {
        const color = token(`--rh-s${s.slot}`);
        return {
          name: s.name, type: 'line', data: s.data, ruleId: s.ruleId,
          symbol: SLOT_SYMBOLS[s.slot - 1], symbolSize: 7, showSymbol: true,
          lineStyle: { width: 2, color, type: s.slot > 4 ? 'dashed' : 'solid', cap: 'round', join: 'round' },
          itemStyle: { color, borderColor: color },
          emphasis: { focus: 'series', lineStyle: { width: 2.5 } },
        };
      }),
    };
  }

  /** Horizontal bars: rules per issue category (one series; a rule in two categories counts in both). */
  function categoryOption(groups) {
    const ordered = [...groups].reverse(); // ECharts draws category axis bottom-up
    return {
      ...base(),
      grid: { left: 8, right: 40, top: 8, bottom: 8, containLabel: true },
      tooltip: {
        ...base().tooltip, trigger: 'axis', axisPointer: shadowPointer(),
        formatter: params => {
          const g = ordered[params[0].dataIndex];
          return `<b>${esc(g.name)}</b><div style="margin-top:4px;font-variant-numeric:tabular-nums">`
            + `${g.rows.length} rules · ${g.enabled} enabled, ${g.rows.length - g.enabled} disabled</div>`;
        },
      },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', data: ordered.map(g => g.name), axisLabel: { width: 220, overflow: 'truncate' } },
      series: [{
        name: 'Rules', type: 'bar', barMaxWidth: 24,
        itemStyle: { color: S.accent },
        label: { show: true, position: 'right', color: token('--rh-text-2') },
        data: ordered.map(g => g.rows.length),
      }],
    };
  }

  // Rules per table, health groups (app.js TABLE_HEALTH_GROUPS): colour per group, hatching for the
  // two states that are fine by decision or exception (same hue as their measured counterpart).
  const TABLE_HEALTH_COLOR = {
    absent: () => S.absent, empty: () => S.tableEmpty, error: () => S.error, dead: () => S.tableEmpty,
    manual: () => S.present, ok: () => S.present, unchecked: () => token('--rh-neutral-text'), disabled: () => token('--rh-text-disabled'),
  };
  const hatch = () => ({
    symbol: 'rect', symbolSize: 1, symbolKeepAspect: false, color: S.gap,
    dashArrayX: [1, 0], dashArrayY: [2, 4], rotation: -Math.PI / 4,
  });

  /**
   * Donut: all rules by rule-wide health, same groups/colours/hatching as Rules per table. The total
   * sits in the hole; the legend + counts are an HTML table next to the chart (its table twin).
   */
  function healthPieOption(groups, total) {
    return {
      ...base(),
      tooltip: {
        ...base().tooltip, trigger: 'item',
        formatter: p => `${swatch(`th-${p.data.key}`)}<b>${esc(p.name)}</b>`
          + `<div class="rh-tt-muted" style="margin-top:4px;font-variant-numeric:tabular-nums">${p.value} of ${total} rules (${pct(p.value, total)})</div>`,
      },
      title: {
        text: String(total), subtext: total === 1 ? 'rule' : 'rules', left: 'center', top: 'middle', itemGap: 2,
        textStyle: { fontSize: 28, fontWeight: 800 }, subtextStyle: { fontSize: 12 },
      },
      series: [{
        type: 'pie', radius: ['60%', '86%'], center: ['50%', '50%'], startAngle: 90,
        label: { show: false }, labelLine: { show: false },
        itemStyle: { borderColor: S.gap, borderWidth: 2 },
        emphasis: { scale: true, scaleSize: 4 },
        data: groups.map(g => ({
          name: g.label, value: g.count, key: g.key,
          itemStyle: { color: TABLE_HEALTH_COLOR[g.key](), ...(g.stripes ? { decal: hatch() } : {}) },
        })),
      }],
    };
  }

  /** Horizontal stacked bars: rules per table split by their health on that table; rows come sorted. */
  function tableOption(rows, groups) {
    const ordered = [...rows].reverse(); // ECharts draws category axis bottom-up
    const parts = groups.map(g => ({ ...g, color: TABLE_HEALTH_COLOR[g.key](), data: ordered.map(r => r.health[g.key]) }));
    return {
      ...base(),
      legend: legend(),
      grid: { left: 8, right: 24, top: 32, bottom: 8, containLabel: true },
      tooltip: {
        ...base().tooltip, trigger: 'axis', axisPointer: shadowPointer(),
        formatter: params => {
          const r = ordered[params[0].dataIndex];
          const split = [r.sentinel && `${r.sentinel} Microsoft Sentinel`, r.defender && `${r.defender} Defender XDR`, r.other && `${r.other} other`]
            .filter(Boolean).join(' · ');
          const lines = groups.filter(g => r.health[g.key]).map(g =>
            `<tr><td>${swatch(`th-${g.key}`)}${esc(g.label)}</td><td style="text-align:right;padding-left:16px">${r.health[g.key]}</td></tr>`).join('');
          return `<b>${esc(r.table)}</b><table style="margin-top:4px;font-variant-numeric:tabular-nums">${lines}</table>`
            + `<div class="rh-tt-muted" style="margin-top:4px">${r.enabled} enabled${split ? ` (${split})` : ''} · ${r.disabled} disabled</div>`
            + (r.empty ? '<div class="rh-tt-warn" style="margin-top:4px">Table empty in the last validation</div>' : '');
        },
      },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: {
        type: 'category', data: ordered.map(r => r.table),
        // Empty tables carry a text marker on the label (not colour alone): every rule on them is blind.
        // Both parts are rich-text styles: mixing plain text with a rich segment made ECharts measure
        // the table name in the wrong font, and containLabel then clipped long names.
        axisLabel: {
          formatter: (name, i) => (ordered[i]?.empty ? `{empty|empty ·} {name|${name}}` : `{name|${name}}`),
          rich: {
            name: { fontFamily: token('--font-mono'), fontSize: 11, color: token('--rh-text-2') },
            empty: { color: S.tableEmpty, fontWeight: 700, fontFamily: token('--font-sans'), fontSize: 11 },
          },
        },
      },
      // Only the hatched groups get a decal (aria decals stay off: they would pattern every series).
      series: stackedSeries(parts).map((s, i) => (parts[i].stripes ? { ...s, itemStyle: { ...s.itemStyle, decal: hatch() } } : s)),
    };
  }

  return { CLASSES, renderChart, disposeDetached, perRuleOption, noiseOption, weeklyOption, ruleTrendOption, categoryOption, tableOption, healthPieOption };
})();
