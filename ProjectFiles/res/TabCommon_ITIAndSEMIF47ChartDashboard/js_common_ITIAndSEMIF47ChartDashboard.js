document.addEventListener('DOMContentLoaded', function() {
  let lastTimestamp = null;
  let itiChart = null;
  let frequencyChart = null;

  // Initialize dashboard
  function initializeDashboard(data) {
    const loadingDiv = document.getElementById('loading');
    const chartDiv = document.getElementById('container');

    // Check if data has changed
    if (lastTimestamp === data.LastRefreshTimestamp) {
      console.log('No new data, skipping update');
      return;
    }
    lastTimestamp = data.LastRefreshTimestamp;

    // Filter valid points (cycles > 0)
    const validPoints = (data.points || []).filter(p => p.cycles > 0);

    // Handle no-data case
    if (!data.points || validPoints.length === 0) {
      console.warn("No ITI curve data found for the selected period");
      loadingDiv.textContent = 'No power quality voltage events found for the selected period';
      loadingDiv.className = 'empty-state';
      loadingDiv.style.display = 'flex';
      chartDiv.classList.remove('active');
      chartDiv.style.display = 'none';
      // Still update header info
      updateHeaderInfo(data);
      return;
    }

    // Show dashboard
    loadingDiv.style.display = 'none';
    chartDiv.classList.add('active');
    chartDiv.style.display = 'grid';

    // Update header info
    updateHeaderInfo(data);

    // Update summary stats
    updateSummaryStats(validPoints);

    // Initialize charts
    initializeITICurveChart(validPoints);
    initializeFrequencyChart(validPoints);

    // Populate events table
    populateEventsTable(validPoints);

    // Update timestamp
    if (!document.getElementById('last-updated')) {
      const statusDiv = document.createElement('div');
      statusDiv.id = 'last-updated';
      document.body.appendChild(statusDiv);
    }
    document.getElementById('last-updated').textContent = `Last updated: ${new Date(data.lastUpdate).toLocaleTimeString()}`;
  }

  function updateHeaderInfo(data) {
    // Update breadcrumb
    const orgName = data.orgName || 'Organization';
    const breadcrumb = document.getElementById('breadcrumb');
    
    if (orgName && orgName.includes('/')) {
      const pathParts = orgName.split('/');
      breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${pathParts.join(' > ')}</span>`;
    } else {
      breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${orgName}</span>`;
    }
    
    // Update time range
    const timeRange = document.getElementById('timeRange');
    if (timeRange) {
      timeRange.textContent = data.timeRange || 'Last 7 Days';
    }
    
    // Update last updated time
    const lastUpdated = document.getElementById('lastUpdated');
    if (lastUpdated) {
      lastUpdated.textContent = `Last Update: ${data.lastUpdate || new Date().toLocaleTimeString()}`;
    }
    
    // Update device count
    const deviceCount = document.getElementById('deviceCount');
    if (deviceCount) {
      deviceCount.textContent = `${data.deviceCount || 0} Devices Aggregated`;
    }
  }

  function updateSummaryStats(points) {
    const totalEvents = points.length;
    const avgDuration = points.length > 0 
      ? (points.reduce((sum, p) => sum + (p.timestamp ? 1 : 0), 0) / points.length).toFixed(2)
      : 0;
    const maxDeviation = points.length > 0
      ? Math.max(...points.map(p => p.percentage)).toFixed(2)
      : 0;
    
    const uniqueDevices = new Set(points.map(p => p.deviceName)).size;

    document.getElementById('totalEvents').textContent = totalEvents;
    document.getElementById('avgDuration').textContent = avgDuration;
    document.getElementById('maxDeviation').textContent = maxDeviation + '%';
    document.getElementById('affectedDevices').textContent = uniqueDevices;
  }

  function initializeITICurveChart(points) {
    if (!itiChart) {
      itiChart = echarts.init(document.getElementById('itiChart'), null, {
        renderer: 'canvas',
        useDirtyRect: false
      });
      window.addEventListener('resize', () => itiChart.resize());
    }

    const option = {      backgroundColor: '#E8E8E8',      tooltip: {
        trigger: 'axis',
        formatter: function(params) {
          var result = 'Cycles: ' + params[0].value[0].toFixed(2) + '<br/>';
          params.forEach(function(item) {
            result += item.seriesName + ': ' + item.value[1].toFixed(2) + '%<br/>';
          });
          return result;
        },
        backgroundColor: 'rgba(255, 255, 255, 0.9)',
        textStyle: {
          color: '#3F3F3F'
        }
      },
      grid: {
        borderColor: '#D8D8D8',
        containLabel: true
      },
      xAxis: {
        name: 'Cycles',
        type: 'log',
        min: 0.01,
        nameTextStyle: {
          color: '#3F3F3F'
        },
        axisLabel: {
          color: '#3F3F3F',
          formatter: function(value) {
            return value.toFixed(2);
          }
        },
        axisLine: {
          lineStyle: {
            color: '#919191'
          }
        },
        axisTick: {
          show: true,
          alignWithLabel: true,
          interval: 0,
          lineStyle: {
            color: '#D8D8D8'
          }
        }
      },
      yAxis: {
        name: '% Nominal Voltage Deviation',
        type: 'value',
        interval: 20,
        min: 0,
        max: 500,
        nameTextStyle: {
          color: '#3F3F3F'
        },
        axisLabel: {
          color: '#3F3F3F'
        },
        axisLine: {
          lineStyle: {
            color: '#919191'
          }
        },
        splitLine: {
          lineStyle: {
            color: '#D8D8D8',
            type: 'dashed'
          }
        }
      },
      series: [
        {
          data: [[0.01, 500], [0.06, 200], [0.11, 140], [0.11, 120], [1.00, 120], [30.00, 120], [30.00, 110], [600.00, 110], [4000.00, 110], [10000000, 110]],
          type: 'line',
          smooth: false,
          symbol: 'none',
          tooltip: { show: false },
          lineStyle: {
            color: '#475CA7',
            width: 1.5
          }
        },
        {
          data: [[0.01, 100], [10000000, 100]],
          type: 'line',
          smooth: false,
          symbol: 'none',
          tooltip: { show: false },
          lineStyle: {
            color: '#6A9C78',
            width: 1,
            type: 'dashed'
          }
        },
        {
          data: [[3, 0], [3, 60], [15, 60], [15, 70]],
          type: 'line',
          smooth: false,
          symbol: 'none',
          tooltip: { show: false },
          lineStyle: {
            color: '#FF6F61',
            width: 1.5
          }
        },
        {
          data: [[0.01, 80], [30, 80], [600, 80], [600, 90], [4000, 90], [10000000, 90]],
          type: 'line',
          smooth: false,
          symbol: 'none',
          tooltip: { show: false },
          lineStyle: {
            color: '#EF4444',
            width: 1.5
          }
        },
        {
          data: [[1, 0], [1, 40], [1, 70], [20, 70], [20, 80]],
          type: 'line',
          smooth: false,
          symbol: 'none',
          tooltip: { show: false },
          lineStyle: {
            color: '#EAB308',
            width: 1.5
          }
        },
        {
          symbolSize: 12,
          name: 'Power Quality Events',
          data: points.map(p => [p.cycles, p.percentage]),
          type: 'scatter',
          symbol: 'triangle',
          itemStyle: {
            color: '#475CA7',
            borderWidth: 1,
            borderColor: '#fff'
          }
        }
      ],
      graphic: [
        {
          type: 'text',
          left: '50%',
          top: '50%',
          z: 100,
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
          z: 100,
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
          z: 100,
          style: {
            text: 'No Interruption In Function Region',
            fill: '#3F3F3F',
            fontSize: 14,
            textAlign: 'center'
          }
        }
      ]
    };

    itiChart.setOption(option);
  }

  function initializeFrequencyChart(points) {
    if (!frequencyChart) {
      frequencyChart = echarts.init(document.getElementById('frequencyChart'), null, {
        renderer: 'canvas',
        useDirtyRect: false
      });
      window.addEventListener('resize', () => frequencyChart.resize());
    }

    // Group by date (timestamp is ISO format: YYYY-MM-DDTHH:mm:ss)
    const dateGroups = {};
    points.forEach(p => {
      if (p.timestamp) {
        const date = p.timestamp.split('T')[0];
        dateGroups[date] = (dateGroups[date] || 0) + 1;
      }
    });

    const dates = Object.keys(dateGroups).sort();
    const counts = dates.map(d => dateGroups[d]);

    const option = {
      backgroundColor: '#E8E8E8',
      tooltip: {
        trigger: 'axis',
        backgroundColor: 'rgba(255, 255, 255, 0.95)',
        textStyle: { color: '#333' }
      },
      grid: {
        borderColor: '#D8D8D8',
        containLabel: true
      },
      xAxis: {
        type: 'category',
        data: dates,
        axisLabel: { color: '#666' },
        axisLine: { lineStyle: { color: '#919191' } }
      },
      yAxis: {
        type: 'value',
        name: 'Event Count',
        nameTextStyle: { color: '#333' },
        axisLabel: { color: '#666' },
        splitLine: { lineStyle: { color: '#D8D8D8' } }
      },
      series: [
        {
          data: counts,
          type: 'line',
          smooth: true,
          lineStyle: { color: '#667eea', width: 2 },
          areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: 'rgba(102, 126, 234, 0.3)' },
            { offset: 1, color: 'rgba(102, 126, 234, 0.05)' }
          ])},
          itemStyle: { color: '#667eea' },
          name: 'Events'
        }
      ]
    };

    frequencyChart.setOption(option);
  }

  function populateEventsTable(points) {
    const tbody = document.getElementById('eventsTableBody');
    tbody.innerHTML = '';

    if (points.length === 0) {
      tbody.innerHTML = '<tr><td colspan="8" style="text-align: center; color: #999;">No events found</td></tr>';
      return;
    }

    points.forEach((p, idx) => {
      const row = tbody.insertRow();
      // Timestamp is ISO format (YYYY-MM-DDTHH:mm:ss) - native Date parsing works
      const displayDate = p.timestamp ? new Date(p.timestamp).toLocaleString() : 'N/A';
      row.innerHTML = `
        <td>${p.deviceName}</td>
        <td>${p.eventType}</td>
        <td>${p.subEvent}</td>
        <td>${displayDate}</td>
        <td>${p.cycles * 16.67 ? (p.cycles * 16.67).toFixed(2) : '0.00'}</td>
        <td>${p.cycles.toFixed(2)}</td>
        <td>${p.percentage.toFixed(2)}%</td>
        <td>${p.tripPoint}</td>
      `;
      if (idx % 2 === 0) {
        row.style.backgroundColor = '#fafafa';
      }
    });
  }

  // Fetch data periodically
  function fetchData() {
    const jsonFile = 'iti-chart-data.json';
    document.getElementById('loading').textContent = 'Loading...';

    fetch(jsonFile + '?ts=' + Date.now())
      .then(response => {
        if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
        return response.json();
      })
      .then(data => {
        initializeDashboard(data);
      })
      .catch(error => {
        console.error('Error fetching data:', error);
        document.getElementById('loading').textContent = 'Error: Failed to load data';
        document.getElementById('container').style.display = 'none';
      });
  }

  // Start periodic fetching
  fetchData(); // Initial fetch
  setInterval(fetchData, 30000); // Refresh every 30 seconds
});
