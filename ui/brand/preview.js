// Specimen script for preview.html. A separate file, not inline: the app's Content-Security-Policy
// (script-src 'self') blocks inline scripts when the specimen is opened through rulehealth serve.
lucide.createIcons();
RuleHealthCharts.registerTheme(echarts);
const S = RuleHealthCharts.series;

// Specimen values only.
const rules = ['Suspicious FOCI token logins', 'Rare scheduled task created', 'Device code with user risk',
               'Unknown process via WinRM', 'Direct send phishing', 'Connect Sync app changes'];
const data = { TP: [3, 1, 6, 0, 2, 1], BP: [21, 9, 4, 7, 2, 1], FP: [9, 14, 1, 5, 1, 0],
               Undetermined: [2, 1, 0, 1, 0, 0], Open: [1, 2, 1, 0, 0, 1] };
const stack = echarts.init(document.getElementById('chart-stack'), 'cerberus');
stack.setOption({
  grid: { left: 8, right: 16, top: 36, bottom: 8, containLabel: true },
  legend: { top: 0, left: 0 },
  tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
  xAxis: { type: 'value' },
  yAxis: { type: 'category', data: rules, inverse: true },
  series: ['TP', 'BP', 'FP', 'Undetermined', 'Open'].map(k => ({
    name: k, type: 'bar', stack: 'total', barMaxWidth: 18,
    color: RuleHealthCharts.classificationColor(k === 'Open' ? null : k), data: data[k]
  }))
});

const scatter = echarts.init(document.getElementById('chart-scatter'), 'cerberus');
scatter.setOption({
  grid: { left: 8, right: 24, top: 16, bottom: 8, containLabel: true },
  tooltip: { formatter: p => `${p.data[2]}<br>${p.data[0]} incidents · FP ${Math.round(p.data[1] * 100)} %` },
  xAxis: { type: 'log', name: 'Incidents', nameLocation: 'middle', nameGap: 26 },
  yAxis: { type: 'value', min: 0, max: 1, axisLabel: { formatter: v => `${v * 100}%` } },
  series: [{
    type: 'scatter', color: S.accent,
    data: [[36, 0.26, rules[0]], [27, 0.58, rules[1]], [12, 0.09, rules[2]], [13, 0.38, rules[3]], [5, 0.2, rules[4]]],
    markLine: { symbol: 'none', data: [{ yAxis: 0.3, label: { position: 'insideEndTop', formatter: 'FP threshold 30 %' } }] }
  }]
});
addEventListener('resize', () => { stack.resize(); scatter.resize(); });
