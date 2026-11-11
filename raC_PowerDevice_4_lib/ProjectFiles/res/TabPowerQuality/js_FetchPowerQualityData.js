document.addEventListener('DOMContentLoaded', function() {
  const deviceName = document.body.getAttribute('data-device-name');
  if (!deviceName) {
    console.error('deviceName is not defined');
    document.getElementById('loading').textContent = 'Error: Device name is not defined';
    return;
  }

  var maxRetries = 5;
  var retryDelay = 1000;
  var lastData = null;

  // Helper function to get the date range from events
  function getDateRangeFromEvents(events) {
    if (events.length === 0) {
      const now = new Date();
      return { start: new Date(now.getTime() - 24 * 60 * 60 * 1000), end: now };
    }

    const validTimestamps = events
      .map(event => {
        try {
          const ts = new Date(event.Local_Timestamp).getTime();
          return !isNaN(ts) ? ts : null;
        } catch (e) {
          return null;
        }
      })
      .filter(ts => ts !== null);

    if (validTimestamps.length === 0) {
      const now = new Date();
      return { start: new Date(now.getTime() - 24 * 60 * 60 * 1000), end: now };
    }

    const minTimestamp = Math.min(...validTimestamps);
    const maxTimestamp = Math.max(...validTimestamps);
    return { start: new Date(minTimestamp), end: new Date(maxTimestamp) };
  }

  // Helper function to generate a complete date range
  function generateDateRange(start, end) {
    const dates = [];
    let currentDate = new Date(start);
    currentDate.setHours(0, 0, 0, 0);
    end.setHours(23, 59, 59, 999);
    while (currentDate <= end) {
      dates.push(new Date(currentDate).toISOString().split('T')[0]);
      currentDate.setDate(currentDate.getDate() + 1);
    }
    return dates;
  }

  // Helper function to group events by date
  function groupEventsByDate(events, startTime, endTime) {
    const grouped = {};
    const dateRange = generateDateRange(startTime, endTime);
    dateRange.forEach(date => {
      grouped[date] = 0;
    });
    events.forEach(event => {
      // Handle both ISO format and other formats
      let date;
      try {
        const timestamp = new Date(event.Local_Timestamp);
        if (!isNaN(timestamp.getTime())) {
          date = timestamp.toISOString().split('T')[0];
          if (grouped[date] !== undefined) {
            grouped[date]++;
          }
        }
      } catch (e) {
        // Skip events with invalid timestamps
        console.warn('Invalid timestamp:', event.Local_Timestamp);
      }
    });
    return grouped;
  }

  // Process summary data and check for waveforms
  function processSummaryData(data) {
    const summary = {};
    data.forEach(event => {
      if (!summary[event.Event_Type]) {
        summary[event.Event_Type] = {
          count: 0,
          latestTimestamp: null,
          events: [],
          hasWaveform: false
        };
      }
      summary[event.Event_Type].count++;
      summary[event.Event_Type].events.push(event);
      if (event.Association_Timestamp && event.Association_Timestamp !== "0-00-00 00:00:00.000000") {
        summary[event.Event_Type].hasWaveform = true;
      }
      const timestamp = new Date(event.Local_Timestamp);
      if (!summary[event.Event_Type].latestTimestamp || timestamp > new Date(summary[event.Event_Type].latestTimestamp)) {
        summary[event.Event_Type].latestTimestamp = event.Local_Timestamp;
      }
    });

    const { start: startTime, end: endTime } = getDateRangeFromEvents(data);
    Object.keys(summary).forEach(type => {
      const eventsByDate = groupEventsByDate(summary[type].events, startTime, endTime);
      summary[type].trendData = Object.entries(eventsByDate).map(([date, count]) => [new Date(date).getTime(), count]).sort((a, b) => a[0] - b[0]);
    });

    return Object.entries(summary).map(([type, data]) => ({
      type,
      ...data
    })).sort((a, b) => new Date(b.latestTimestamp) - new Date(a.latestTimestamp));
  }

  // Populate summary table with trend charts and waveform icon
  function populateSummaryTable(summaryData) {
    const tbody = document.querySelector('#summaryTable tbody');
    tbody.innerHTML = '';
    summaryData.forEach(event => {
      const trendChartId = `trend-${event.type.replace(/\s+/g, '-')}`;
      const row = document.createElement('tr');
      row.innerHTML = `
        <td>${event.type}${event.hasWaveform ? ' <i class="fas fa-wave-square waveform-icon" title="Has associated waveform"></i>' : ''}</td>
        <td>${event.count}</td>
        <td>${new Date(event.latestTimestamp).toLocaleString()}</td>
        <td><div id="${trendChartId}" class="trend-chart"></div></td>
        <td><button class="view-btn" data-event-type="${event.type}">View</button></td>
      `;
      tbody.appendChild(row);
      const trendChart = echarts.init(document.getElementById(trendChartId));
      const trendOption = {
        animation: false,
        grid: { top: 2, right: 5, bottom: 2, left: 5 },
        xAxis: { type: 'time', show: false },
        yAxis: { type: 'value', show: false, min: 0 },
        series: [{
          data: event.trendData,
          type: 'line',
          symbol: 'circle',
          symbolSize: 4,
          lineStyle: { color: '#4fa3d0', width: 2 },
          itemStyle: { color: '#4fa3d0' },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: 'rgba(79,163,208,0.3)' },
              { offset: 1, color: 'rgba(79,163,208,0.1)' }
            ])
          }
        }],
        dataZoom: [
          { type: 'inside', start: 0, end: 100, zoomOnMouseWheel: true }
        ]
      };
      trendChart.setOption(trendOption);
    });

    // Attach event listeners to View buttons
    document.querySelectorAll('.view-btn').forEach(button => {
      button.addEventListener('click', () => {
        const eventType = button.getAttribute('data-event-type');
        window.filterTable(eventType);
      });
    });
  }

  // Filter main events table by event type
  window.filterTable = function(eventType) {
    const tbody = document.querySelector('#eventTable tbody');
    tbody.innerHTML = '';
    const filteredData = eventType ? lastData.events.filter(event => event.Event_Type === eventType) : lastData.events;
    filteredData.forEach(event => {
      const row = document.createElement('tr');
      const hasWaveform = event.Association_Timestamp && event.Association_Timestamp !== "0-00-00 00:00:00.000000";
      row.innerHTML = `
        <td>${event.Record_Identifier}</td>
        <td>${event.Event_Type}</td>
        <td>${event.Sub_Event}</td>
        <td>${event.Local_Timestamp}</td>
        <td>${event.Event_Duration_mS}</td>
        <td>${event.Trip_Point}</td>
        <td>${event.Min_or_Max}</td>
        <td>${hasWaveform ? '<span class="waveform-message">View in Waveform Viewer tab</span>' : ''}</td>
      `;
      tbody.appendChild(row);
    });
    if (eventType) {
      document.querySelector('.table-container').scrollIntoView({ behavior: 'smooth' });
    }
  };

  // Initialize main charts
  function initializeCharts(summaryData) {
    const charts = {
      frequency: echarts.init(document.getElementById('eventFrequencyChart')),
      duration: echarts.init(document.getElementById('eventDurationChart')),
      voltage: echarts.init(document.getElementById('voltageLevelsChart')),
      types: echarts.init(document.getElementById('eventTypesChart'))
    };
    const options = {
      frequency: {
        title: { text: 'Event Frequency Over Time' },
        tooltip: { trigger: 'axis' },
        xAxis: { type: 'time' },
        yAxis: { type: 'value' },
        series: [{
          name: 'Events',
          type: 'line',
          data: lastData.events.map(event => [new Date(event.Local_Timestamp).getTime(), 1])
        }],
        dataZoom: [
          { type: 'inside', start: 0, end: 100, zoomOnMouseWheel: true }
        ]
      },
      duration: {
        title: { text: 'Event Duration Analysis' },
        tooltip: { trigger: 'axis' },
        grid: { left: 10, right: 10, bottom: 30, top: 50, containLabel: true },
        xAxis: {
          type: 'category',
          data: lastData.events.map(event => event.Event_Type),
          axisLabel: { show: false }
        },
        yAxis: {
          type: 'value',
          axisLabel: {
            margin: 10,
            formatter: function(value) {
              var absVal = Math.abs(value);
              if (absVal >= 1000000) return (value / 1000000).toFixed(value % 1000000 === 0 ? 0 : 1) + 'm';
              if (absVal >= 1000) return (value / 1000).toFixed(value % 1000 === 0 ? 0 : 1) + 'k';
              return value;
            }
          }
        },
        series: [{ name: 'Duration', type: 'bar', data: lastData.events.map(event => event.Event_Duration_mS) }],
        dataZoom: [
          { type: 'inside', start: 0, end: 100, zoomOnMouseWheel: true }
        ]
      },
      voltage: {
        title: { text: 'Voltage Levels' },
        tooltip: { trigger: 'axis' },
        xAxis: { type: 'category', data: lastData.events.map(event => event.Event_Type) },
        yAxis: { type: 'value' },
        series: [{ name: 'Min/Max Value', type: 'scatter', data: lastData.events.map(event => event.Min_or_Max) }],
        dataZoom: [
          { type: 'inside', start: 0, end: 100, zoomOnMouseWheel: true }
        ]
      },
      types: {
        title: { text: 'Event Types Distribution' },
        tooltip: { trigger: 'item' },
        series: [{
          name: 'Event Types',
          type: 'pie',
          radius: '60%',
          data: summaryData.map(data => ({ name: data.type, value: data.count }))
        }]
      }
    };
    Object.keys(charts).forEach(key => charts[key].setOption(options[key]));
  }

  // Initialize dashboard with fetched data
  function initializeDashboard(data) {
    const loadingDiv = document.getElementById('loading');
    if (!data.events || data.events.length === 0) {
      console.warn("No power quality events found in data.");
      loadingDiv.textContent = 'No Power Quality Events Found';
      loadingDiv.style.fontSize = '28px';
      loadingDiv.innerHTML += '<p style="font-size: 16px; margin-top: 10px; color: #666;">There are no events available for the selected time period. Please try adjusting your filter criteria.</p>';
      return;
    }

    loadingDiv.style.display = 'none';
    document.querySelector('.summary-table').style.display = 'table';
    document.querySelector('.dashboard').style.display = 'grid';

    const summaryData = processSummaryData(data.events);
    populateSummaryTable(summaryData);
    initializeCharts(summaryData);
    window.filterTable('');
    window.scrollTo({ top: 0, behavior: 'smooth' });

    document.getElementById('last-updated').textContent = `Last updated: ${new Date(data.LastRefreshTimestamp).toLocaleTimeString()}`;
  }

  // Fetch data with retries
  function fetchData(retryCount = 0) {
    const jsonFile = `${deviceName}_PQLogData.json`;
    document.getElementById('loading').textContent = 'Loading...';

    fetch(jsonFile)
      .then(response => {
        if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
        return response.json();
      })
      .then(data => {
        lastData = data;
        initializeDashboard(data);
      })
      .catch(error => {
        console.error('Error fetching data:', error);
        if (retryCount < maxRetries) {
          setTimeout(() => fetchData(retryCount + 1), retryDelay);
        } else {
          document.getElementById('loading').textContent = 'Error: Failed to load data';
        }
      });
  }
  // Start fetching data
  setTimeout(() => fetchData(0), 1000);
});