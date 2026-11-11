document.addEventListener('DOMContentLoaded', function() {
  const deviceName = document.body.getAttribute('data-device-name');
  if (!deviceName) {
    const loading = document.getElementById('loading');
    if (loading) loading.textContent = 'Error: Device name is not defined';
    return;
  }

  let lastTimestamp = null;
  let chart = null;
  let isNoData = false;
  let currentEventFilter = localStorage.getItem('itiCurveFilter') || 'IEEE1159';
  let cachedChartData = null;

  const visibilityState = {
    IEEE: true,
    UserDefined: true,
    ITICurve: true
  };

  const chartDiv = document.getElementById('chart');

  function setupEventFilter() {
    const headerDiv = document.getElementById('event-filter');
    if (!headerDiv) return;

    document.querySelectorAll('input[name="eventType"]').forEach(radio => {
      radio.addEventListener('change', function() {
        currentEventFilter = this.value;
        localStorage.setItem('itiCurveFilter', this.value);
        if (cachedChartData) {
          updateChartWithFilter(cachedChartData);
        }
      });
    });
  }

  function attachGroupedButtons() {
    const btnIEEE = document.getElementById('btnIEEE');
    const btnUser = document.getElementById('btnUserDefined');

    if (btnIEEE) {
      btnIEEE.addEventListener('click', function () {
        visibilityState.IEEE = !visibilityState.IEEE;
        btnIEEE.classList.toggle('active', visibilityState.IEEE);
        if (cachedChartData) updateChartWithFilter(cachedChartData);
      });
    }
    if (btnUser) {
      btnUser.addEventListener('click', function () {
        visibilityState.UserDefined = !visibilityState.UserDefined;
        btnUser.classList.toggle('active', visibilityState.UserDefined);
        if (cachedChartData) updateChartWithFilter(cachedChartData);
      });
    }
  }

  function groupOverlappingPoints(scatterData) {
    const pointMap = new Map();
    scatterData.forEach(point => {
      const key = `${point.Cycles.toFixed(4)}_${point.Percentage.toFixed(4)}`;
      if (!pointMap.has(key)) {
        pointMap.set(key, {
          original: point,
          count: 0,
          events: []
        });
      }
      const entry = pointMap.get(key);
      entry.count++;
      entry.events.push({
        SubEventDisplay: point.SubEventDisplay || 'N/A',
        EventType: point.EventType || 'N/A',
        Timestamp: point.Timestamp || 'N/A'
      });
    });
    return Array.from(pointMap.values());
  }

  function parseEventInfo(point) {
    if (!point) return { type: 'Other', voltage: 'Unknown' };

    let eventType = 'Other';
    let voltage = 'Unknown';

    if (point.EventType) {
      const typeStr = String(point.EventType).toLowerCase();
      if (typeStr.includes('sag')) eventType = 'Sag';
      else if (typeStr.includes('swell')) eventType = 'Swell';
      else if (typeStr.includes('interruption')) eventType = 'Interruption';
      else if (typeStr.includes('overvoltage')) eventType = 'Overvoltage';
      else if (typeStr.includes('undervoltage')) eventType = 'Undervoltage';
      else if (typeStr.includes('transient')) eventType = 'Transient';
      else if (typeStr.includes('harmonic')) eventType = 'Harmonic';
      else if (typeStr.includes('imbalance')) eventType = 'Imbalance';
    }

    if (point.SubEventDisplay) {
      const voltageMatch = String(point.SubEventDisplay).match(/[Vv][1-3]/);
      voltage = voltageMatch ? voltageMatch[0].toUpperCase() : 'Unknown';
    }

    return { type: eventType, voltage };
  }

  function generateEventSummary(scatterData) {
    const summary = {
      total: scatterData.length,
      byType: {},
      byVoltage: {},
      grid: {}
    };

    scatterData.forEach(point => {
      const { type, voltage } = parseEventInfo(point);
      summary.byType[type] = (summary.byType[type] || 0) + 1;
      summary.byVoltage[voltage] = (summary.byVoltage[voltage] || 0) + 1;
      const gridKey = `${voltage}_${type}`;
      summary.grid[gridKey] = (summary.grid[gridKey] || 0) + 1;
    });

    return summary;
  }

  function createSummaryStatsHTML(summary) {
    const voltages = ['V1', 'V2', 'V3'].filter(v => summary.byVoltage[v]);
    const types = Object.keys(summary.byType).sort();

    let html = `<div style="padding: 0;">
      <div style="font-weight: 700; margin-bottom: 10px; font-size: 12px; color: #FF6F61; border-bottom: 1px solid #F0F0F0; padding-bottom: 8px;">Total Events: ${summary.total}</div>
      <table style="font-size: 11px; border-collapse: collapse; width: 100%;">
        <tr style="border-bottom: 2px solid #E8E8E8;">
          <td style="padding: 6px 4px; font-weight: 700; color: #333333; width: 35px;"></td>`;
    types.forEach(type => {
      html += `<td style="padding: 6px 4px; font-weight: 700; color: #333333; text-align: center; width: 50px; font-size: 10px;">${type}</td>`;
    });
    html += `</tr>`;

    voltages.forEach(voltage => {
      html += `<tr style="border-bottom: 1px solid #F0F0F0;">
        <td style="padding: 6px 4px; font-weight: 700; color: #475CA7; font-size: 11px;">${voltage}</td>`;
      types.forEach(type => {
        const count = summary.grid[`${voltage}_${type}`] || 0;
        html += `<td style="padding: 6px 4px; text-align: center; color: #555555; font-size: 11px;">${count}</td>`;
      });
      html += `</tr>`;
    });

    html += `</table></div>`;
    return html;
  }

  function buildSagSwellSummary(data) {
    const summary = {
      V1: { sag: 0, swell: 0 },
      V2: { sag: 0, swell: 0 },
      V3: { sag: 0, swell: 0 }
    };

    function processGroup(arr) {
      (arr || []).forEach(p => {
        const phase = p.SubEventCode === 1 ? 'V1' : p.SubEventCode === 2 ? 'V2' : 'V3';
        const eventType = String(p.EventType || '').toLowerCase();
        
        if (eventType.includes('swell')) {
          summary[phase].swell++;
        } else if (eventType.includes('sag')) {
          summary[phase].sag++;
        }
      });
    }

    processGroup(data.points['IEEE1159'] || []);
    processGroup(data.points['UserDefined'] || []);

    const v1Sag = document.getElementById('v1-sag');
    const v1Swell = document.getElementById('v1-swell');
    const v2Sag = document.getElementById('v2-sag');
    const v2Swell = document.getElementById('v2-swell');
    const v3Sag = document.getElementById('v3-sag');
    const v3Swell = document.getElementById('v3-swell');

    if (v1Sag) v1Sag.textContent = summary.V1.sag;
    if (v1Swell) v1Swell.textContent = summary.V1.swell;
    if (v2Sag) v2Sag.textContent = summary.V2.sag;
    if (v2Swell) v2Swell.textContent = summary.V2.swell;
    if (v3Sag) v3Sag.textContent = summary.V3.sag;
    if (v3Swell) v3Swell.textContent = summary.V3.swell;
  }

  function initializeITICurve(data) {
    const loadingDiv = document.getElementById('loading');

    if (lastTimestamp === data.LastRefreshTimestamp) {
      return;
    }
    lastTimestamp = data.LastRefreshTimestamp;
    cachedChartData = data;

    if (!document.getElementById('event-filter') || !document.getElementById('event-filter').style.backgroundColor) {
      setupEventFilter();
    }

    bindHeaderInfo(data);
    buildSagSwellSummary(data);
    updateChartWithFilter(data);

    if (loadingDiv) loadingDiv.style.display = 'none';
  }

  function bindHeaderInfo(data) {
    const dev = document.getElementById('info-device');
    const range = document.getElementById('info-range');
    const ieee = document.getElementById('info-ieee-count');
    const user = document.getElementById('info-user-count');
    const total = document.getElementById('info-total-count');

    if (dev) dev.textContent = data.deviceName || '';
    if (range) range.textContent = data.timeRange || '';
    if (data.summary) {
      if (ieee) ieee.textContent = data.summary.IEEE1159_Count || 0;
      if (user) user.textContent = data.summary.UserDefined_Count || 0;
      if (total) total.textContent = data.summary.Total_Count || 0;
    }
  }

  function updateChartWithFilter(data) {
    const loadingDiv = document.getElementById('loading');
    const containerDiv = document.getElementById('container');

    if (!data.points || Object.keys(data.points).length === 0) {
      isNoData = true;
      if (loadingDiv) {
        loadingDiv.textContent = 'No data available';
        loadingDiv.style.fontSize = '16px';
        loadingDiv.style.color = '#3F3F3F';
        loadingDiv.style.textAlign = 'center';
      }
      if (containerDiv) containerDiv.style.display = 'none';
      return;
    }

    isNoData = false;
    if (loadingDiv) loadingDiv.style.display = 'none';
    if (containerDiv) containerDiv.style.display = 'block';

    if (!chart) {
      chart = echarts.init(chartDiv, null, { renderer: 'canvas', useDirtyRect: false });
      window.addEventListener('resize', () => chart && chart.resize());
    }

    let ieeeV1 = [], ieeeV2 = [], ieeeV3 = [];
    let userV1 = [], userV2 = [], userV3 = [];

    if (visibilityState.IEEE) {
      const ieeePoints = (data.points['IEEE1159'] || []).filter(p => p.Cycles > 0);
      ieeePoints.forEach(p => {
        const v = [p.Cycles, p.Percentage, p.Timestamp, p.SubEventDisplay, p.EventType];
        if (p.SubEventCode === 1) ieeeV1.push(v);
        else if (p.SubEventCode === 2) ieeeV2.push(v);
        else if (p.SubEventCode === 3) ieeeV3.push(v);
      });
    }

    if (visibilityState.UserDefined) {
      const userPoints = (data.points['UserDefined'] || []).filter(p => p.Cycles > 0);
      userPoints.forEach(p => {
        const v = [p.Cycles, p.Percentage, p.Timestamp, p.SubEventDisplay, p.EventType];
        if (p.SubEventCode === 1) userV1.push(v);
        else if (p.SubEventCode === 2) userV2.push(v);
        else if (p.SubEventCode === 3) userV3.push(v);
      });
    }

    let filteredScatterData = [];
    if (visibilityState.IEEE) {
      filteredScatterData = filteredScatterData.concat((data.points['IEEE1159'] || []).filter(p => p.Cycles > 0));
    }
    if (visibilityState.UserDefined) {
      filteredScatterData = filteredScatterData.concat((data.points['UserDefined'] || []).filter(p => p.Cycles > 0));
    }

    const baseSeries = [];

    if (visibilityState.ITICurve) {
      baseSeries.push(
        {
          name: 'ITI Upper',
          data: [[0.01, 500], [0.06, 200], [0.11, 140], [0.11, 120], [1.00, 120], [30.00, 120], [30.00, 110], [600.00, 110], [4000.00, 110], [10000000, 110]],
          type: 'line',
          smooth: false,
          lineStyle: { color: '#475CA7', width: 1.5 },
          showSymbol: false
        },
        {
          name: 'ITI Nominal',
          data: [[0.01, 100], [10000000, 100]],
          type: 'line',
          smooth: false,
          lineStyle: { color: '#6A9C78', width: 1, type: 'dashed' },
          showSymbol: false
        },
        {
          name: 'ITI Lower 1',
          data: [[3, 0], [3, 60], [15, 60], [15, 70]],
          type: 'line',
          smooth: false,
          lineStyle: { color: '#FF6F61', width: 1.5 },
          showSymbol: false
        },
        {
          name: 'ITI Lower 2',
          data: [[0.01, 80], [30, 80], [600, 80], [600, 90], [4000, 90], [10000000, 90]],
          type: 'line',
          smooth: false,
          lineStyle: { color: '#EF4444', width: 1.5 },
          showSymbol: false
        },
        {
          name: 'ITI Lower 3',
          data: [[1, 0], [1, 40], [1, 70], [20, 70], [20, 80]],
          type: 'line',
          smooth: false,
          lineStyle: { color: '#EAB308', width: 1.5 },
          showSymbol: false
        }
      );
    }

    if (visibilityState.IEEE) {
      baseSeries.push(
        {
          name: 'IEEE V1',
          type: 'scatter',
          data: ieeeV1,
          symbol: 'circle',
          symbolSize: 10,
          itemStyle: { color: '#e74c3c' }
        },
        {
          name: 'IEEE V2',
          type: 'scatter',
          data: ieeeV2,
          symbol: 'circle',
          symbolSize: 10,
          itemStyle: { color: '#27ae60' }
        },
        {
          name: 'IEEE V3',
          type: 'scatter',
          data: ieeeV3,
          symbol: 'circle',
          symbolSize: 10,
          itemStyle: { color: '#3498db' }
        }
      );
    }

    if (visibilityState.UserDefined) {
      baseSeries.push(
        {
          name: 'User V1',
          type: 'scatter',
          data: userV1,
          symbol: 'triangle',
          symbolSize: 12,
          itemStyle: { color: '#c0392b' }
        },
        {
          name: 'User V2',
          type: 'scatter',
          data: userV2,
          symbol: 'triangle',
          symbolSize: 12,
          itemStyle: { color: '#229954' }
        },
        {
          name: 'User V3',
          type: 'scatter',
          data: userV3,
          symbol: 'triangle',
          symbolSize: 12,
          itemStyle: { color: '#2874a6' }
        }
      );
    }

    const option = {
      tooltip: {
        trigger: 'item',
        formatter: function (params) {
          if (!params || !params.value) return '';
          const v = params.value;
          const cycles = v[0];
          const pct = v[1];
          const ts = v[2];
          const sub = v[3];
          const type = v[4];
          let res = '<strong>Power Quality Event</strong><br/>';
          res += 'Cycles: ' + cycles.toFixed(2) + '<br/>';
          res += 'Voltage Deviation: ' + pct.toFixed(2) + '%<br/>';
          if (sub) res += 'Sub Event: ' + sub + '<br/>';
          if (type) res += 'Event Type: ' + type + '<br/>';
          if (ts) res += 'Timestamp: ' + ts;
          return res;
        },
        backgroundColor: '#E8E8E8',
        textStyle: { color: '#3F3F3F', fontSize: 11 },
        borderColor: '#475CA7',
        borderWidth: 1
      },
      grid: {
        borderColor: '#D8D8D8',
        containLabel: true
      },
      xAxis: {
        name: 'Cycles',
        type: 'log',
        min: 0.01,
        nameTextStyle: { color: '#3F3F3F' },
        axisLabel: {
          color: '#3F3F3F',
          formatter: function (value) { return value.toFixed(2); }
        },
        axisLine: { lineStyle: { color: '#919191' } },
        axisTick: {
          show: true,
          alignWithLabel: true,
          interval: 0,
          lineStyle: { color: '#D8D8D8' }
        },
        splitLine: {
          lineStyle: { color: '#D8D8D8', type: 'dashed' }
        }
      },
      yAxis: {
        name: '% Nominal Voltage Deviation',
        nameRotate: 90,
        nameLocation: 'middle',
        nameGap: 50,
        type: 'value',
        interval: 20,
        min: 0,
        max: 500,
        nameTextStyle: { color: '#3F3F3F' },
        axisLabel: { color: '#3F3F3F' },
        axisLine: { lineStyle: { color: '#919191' } },
        splitLine: {
          lineStyle: { color: '#D8D8D8', type: 'dashed' }
        }
      },
      series: baseSeries,
      graphic: visibilityState.ITICurve ? [
        {
          type: 'text',
          left: '50%',
          top: '50%',
          z: -1,
          style: {
            text: 'Prohibited Region',
            fill: '#3F3F3F',
            fontSize: 16,
            textAlign: 'center'
          }
        },
        {
          type: 'text',
          left: '45%',
          bottom: '20%',
          z: -1,
          style: {
            text: 'No Damage Region',
            fill: '#3F3F3F',
            fontSize: 14,
            textAlign: 'center'
          }
        },
        {
          type: 'text',
          left: '13%',
          bottom: '35%',
          z: -1,
          style: {
            text: 'No Interruption In Function Region',
            fill: '#3F3F3F',
            fontSize: 14,
            textAlign: 'center'
          }
        }
      ] : []
    };

    chart.setOption(option, true);

    const statusDiv = document.getElementById('last-updated');
    if (statusDiv && data.LastRefreshTimestamp) {
      statusDiv.textContent = `Last updated: ${new Date(data.LastRefreshTimestamp).toLocaleTimeString()}`;
    }
  }

  function fetchData() {
    const jsonFile = `${deviceName}_ITICurveData.json`;
    const loadingDiv = document.getElementById('loading');

    if (!isNoData && loadingDiv) loadingDiv.textContent = 'Loading...';

    fetch(jsonFile)
      .then(response => {
        if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
        return response.json();
      })
      .then(data => {
        initializeITICurve(data);
      })
      .catch(error => {
        isNoData = true;
        if (loadingDiv) {
          loadingDiv.textContent = 'Error: Failed to load data';
          loadingDiv.style.fontSize = '16px';
          loadingDiv.style.color = '#3F3F3F';
          loadingDiv.style.textAlign = 'center';
        }
        const containerDiv = document.getElementById('container');
        if (containerDiv) containerDiv.style.display = 'none';
      });
  }

  attachGroupedButtons();
  fetchData();
});