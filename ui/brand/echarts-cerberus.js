/* ============================================================
   RuleHealth — ECharts theme built from the Cerberus brand tokens.
   Reads CSS custom properties at runtime, so charts can never drift from the CSS.

   Usage (after cerberus-tokens.css, rulehealth-theme.css and echarts are loaded):
     RuleHealthCharts.registerTheme(echarts);          // once, at startup
     const chart = echarts.init(el, 'cerberus');        // every chart
     series: [{ name: 'TP', color: RuleHealthCharts.series.tp, ... }]

   No colors are hard-coded here: every value is a token lookup.
   ============================================================ */
(function () {
  'use strict';

  function token(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }

  // Semantic series colors. Order of classification series is fixed: TP, BP, FP, Undetermined, Open.
  const series = {
    get tp()           { return token('--rh-tp'); },
    get bp()           { return token('--rh-bp'); },
    get fp()           { return token('--rh-fp'); },
    get undetermined() { return token('--rh-undetermined'); },
    get open()         { return token('--rh-open'); },
    get present()      { return token('--rh-v-present-text'); },
    get absent()       { return token('--rh-v-absent-text'); },
    get tableEmpty()   { return token('--rh-v-empty-text'); },
    get error()        { return token('--rh-v-error-text'); },
    get skipped()      { return token('--rh-v-skipped-text'); },
    get accent()       { return token('--rh-accent'); },
    get gap()          { return token('--rh-series-gap'); }
  };

  // Map a classification label (as used in incidents.json) to its color.
  function classificationColor(label) {
    switch (label) {
      case 'TP': return series.tp;
      case 'BP': return series.bp;
      case 'FP': return series.fp;
      case 'Undetermined': return series.undetermined;
      default: return series.open; // null / open
    }
  }

  function buildTheme() {
    const text = token('--rh-text');
    const text2 = token('--rh-text-2');
    const text3 = token('--rh-text-3');
    const border = token('--rh-border');
    const subtle = token('--rh-border-subtle');
    const raised = token('--rh-raised');
    const fontSans = token('--font-sans');
    const fontMono = token('--font-mono');

    const axis = {
      axisLine: { show: true, lineStyle: { color: border } },
      axisTick: { show: false },
      axisLabel: { color: text3, fontFamily: fontSans, fontSize: 11 },
      splitLine: { show: true, lineStyle: { color: subtle } },
      nameTextStyle: { color: text3, fontFamily: fontSans, fontSize: 11 }
    };

    return {
      color: [series.tp, series.bp, series.fp, series.undetermined, series.open, series.accent],
      backgroundColor: 'transparent',
      textStyle: { fontFamily: fontSans, color: text2 },
      title: { textStyle: { color: text, fontFamily: fontSans, fontWeight: 700, fontSize: 14 },
               subtextStyle: { color: text3, fontFamily: fontSans, fontSize: 12 } },
      legend: { textStyle: { color: text2, fontFamily: fontSans, fontSize: 12 },
                icon: 'roundRect', itemWidth: 10, itemHeight: 10, itemGap: 16,
                // Scroll-legend pager (RuleHealth addition): ECharts' defaults are ~1.5:1 on carbon.
                pageTextStyle: { color: text3, fontFamily: fontSans },
                pageIconColor: token('--rh-accent'), pageIconInactiveColor: token('--rh-text-disabled') },
      tooltip: {
        backgroundColor: raised, borderColor: border, borderWidth: 1, padding: [8, 12],
        textStyle: { color: text, fontFamily: fontSans, fontSize: 12 },
        extraCssText: 'border-radius:10px;box-shadow:none;'
      },
      categoryAxis: Object.assign({}, axis, { splitLine: { show: false } }),
      valueAxis: axis,
      logAxis: axis,
      timeAxis: Object.assign({}, axis, { splitLine: { show: false } }),
      bar: { itemStyle: { borderColor: series.gap, borderWidth: 1, borderRadius: 0 } },
      line: { symbol: 'circle', symbolSize: 5, lineStyle: { width: 2 } },
      scatter: { symbolSize: 9, itemStyle: { opacity: 0.9 } },
      markLine: { lineStyle: { color: text3, type: 'dashed' }, label: { color: text3, fontFamily: fontSans } },
      dataZoom: { textStyle: { color: text3 }, borderColor: border },
      // values in tooltips: use fontMono via a formatter where numbers must align
      _fontMono: fontMono
    };
  }

  function registerTheme(echarts) {
    echarts.registerTheme('cerberus', buildTheme());
    return 'cerberus';
  }

  window.RuleHealthCharts = { registerTheme, series, classificationColor, token };
})();
