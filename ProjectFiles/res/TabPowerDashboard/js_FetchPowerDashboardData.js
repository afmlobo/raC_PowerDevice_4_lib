document.addEventListener('DOMContentLoaded', function() {
  const deviceName = document.body.getAttribute('data-device-name');
  const loadingDiv = document.getElementById('loading');
  const chartsGrid = document.getElementById('charts-grid');
  const currentFileDiv = document.getElementById('currentFile');

  // Check if critical DOM elements exist
  if (!deviceName) {
      console.error('deviceName is not defined');
      if (loadingDiv) {
          loadingDiv.style.display = 'block';
          loadingDiv.textContent = 'Error: Device name is not defined';
      }
      return;
  }

  if (!loadingDiv || !chartsGrid || !currentFileDiv) {
      console.error('Required DOM elements missing:', { loading: !!loadingDiv, chartsGrid: !!chartsGrid, currentFile: !!currentFileDiv });
      if (loadingDiv) {
          loadingDiv.style.display = 'block';
          loadingDiv.textContent = 'Error: Dashboard initialization failed';
      }
      return;
  }

  // Initialize ECharts instances
  let chart1, chart2, chart3, chart4;
  try {
      chart1 = echarts.init(document.getElementById('chart1'));
      chart2 = echarts.init(document.getElementById('chart2'));
      chart3 = echarts.init(document.getElementById('chart3'));
      chart4 = echarts.init(document.getElementById('chart4'));
  } catch (error) {
      console.error('Error initializing ECharts:', error);
      loadingDiv.style.display = 'block';
      loadingDiv.textContent = 'Error: Failed to initialize charts';
      return;
  }

  // Common chart configurations
  const commonOptions = {
      tooltip: {
          trigger: 'axis',
          axisPointer: { type: 'cross' },
          renderMode: 'richText',
          backgroundColor: 'rgba(255, 255, 255, 0.9)',
          borderColor: '#D8D8D8',
          borderWidth: 1,
          padding: [8, 12],
          textStyle: {
              color: '#3F3F3F',
              fontSize: 12
          },
          confine: true
      },
      grid: {
          top: 100,
          bottom: 80,
          left: '5%',
          right: '5%',
          containLabel: true
      },
      dataZoom: [
          { type: 'inside', start: 0, end: 100 },
          { type: 'slider', start: 0, end: 100, bottom: 30, height: 20 }
      ]
  };

  // Default options for each chart
  const option1 = {
      ...commonOptions,
      title: { 
          text: 'Voltage and Current', 
          left: 'center', 
          padding: [10, 0],
          textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
      },
      legend: { 
          data: ['Voltage', 'Current'], 
          left: 'center', 
          top: 40, 
          padding: [10, 0],
          textStyle: { color: '#3F3F3F' }
      },
      xAxis: { 
          type: 'category', 
          data: [], 
          axisLine: { lineStyle: { color: '#D8D8D8' } },
          axisLabel: { color: '#3F3F3F' }
      },
      yAxis: [
          { 
              type: 'value', 
              name: '', 
              position: 'left', 
              axisLabel: { formatter: '{value} V', color: '#3F3F3F' },
              axisLine: { lineStyle: { color: '#D8D8D8' } },
              splitLine: { lineStyle: { color: '#D8D8D8' } }
          },
          { 
              type: 'value', 
              name: '', 
              position: 'right', 
              axisLabel: { formatter: '{value} A', color: '#3F3F3F' },
              axisLine: { lineStyle: { color: '#D8D8D8' } },
              splitLine: { show: false }
          }
      ],
      series: [
          { 
              name: 'Voltage', 
              type: 'line', 
              data: [], 
              yAxisIndex: 0,
              lineStyle: { width: 2, color: '#475CA7' },
              emphasis: { focus: 'series' }
          },
          { 
              name: 'Current', 
              type: 'line', 
              data: [], 
              yAxisIndex: 1,
              lineStyle: { width: 2, color: '#FF6F61' },
              emphasis: { focus: 'series' }
          }
      ]
  };

  const option2 = {
      ...commonOptions,
      title: { 
          text: 'Power Metrics', 
          left: 'center', 
          padding: [10, 0],
          textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
      },
      legend: { 
          data: ['Total_kW', 'Total_kVAR', 'Total_kVA'], 
          left: 'center', 
          top: 40, 
          padding: [10, 0],
          textStyle: { color: '#3F3F3F' }
      },
      xAxis: { 
          type: 'category', 
          data: [], 
          axisLine: { lineStyle: { color: '#D8D8D8' } },
          axisLabel: { color: '#3F3F3F' }
      },
      yAxis: { 
          type: 'value',
          axisLabel: { color: '#3F3F3F' },
          axisLine: { lineStyle: { color: '#D8D8D8' } },
          splitLine: { lineStyle: { color: '#D8D8D8' } }
      },
      series: [
          { 
              name: 'Total_kW', 
              type: 'line', 
              data: [],
              lineStyle: { width: 2, color: '#475CA7' },
              emphasis: { focus: 'series' }
          },
          { 
              name: 'Total_kVAR', 
              type: 'line', 
              data: [],
              lineStyle: { width: 2, color: '#6A9C78' },
              emphasis: { focus: 'series' }
          },
          { 
              name: 'Total_kVA', 
              type: 'line', 
              data: [],
              lineStyle: { width: 2, color: '#FFB347' },
              emphasis: { focus: 'series' }
          }
      ]
  };

  const option3 = {
      ...commonOptions,
      title: { 
          text: 'Frequency', 
          left: 'center', 
          padding: [10, 0],
          textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
      },
      legend: { 
          data: ['Frequency'], 
          left: 'center', 
          top: 40, 
          padding: [10, 0],
          textStyle: { color: '#3F3F3F' }
      },
      xAxis: { 
          type: 'category', 
          data: [], 
          axisLine: { lineStyle: { color: '#D8D8D8' } },
          axisLabel: { color: '#3F3F3F' }
      },
      yAxis: { 
          type: 'value',
          name: '',
          nameLocation: 'middle',
          nameGap: 30,
          axisLabel: { color: '#3F3F3F' },
          axisLine: { lineStyle: { color: '#D8D8D8' } },
          splitLine: { lineStyle: { color: '#D8D8D8' } }
      },
      series: [
          { 
              name: 'Frequency', 
              type: 'line', 
              data: [],
              lineStyle: { width: 2, color: '#475CA7' },
              emphasis: { focus: 'series' }
          }
      ]
  };

  const option4 = {
      ...commonOptions,
      title: { 
          text: 'Voltage Unbalance (%) vs Current Unbalance (%)', 
          left: 'center', 
          padding: [10, 0],
          textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
      },
      legend: { 
          data: ['Voltage Unbalance (%)', 'Current Unbalance (%)'], 
          left: 'center', 
          top: 40, 
          padding: [10, 0],
          textStyle: { color: '#3F3F3F' }
      },
      xAxis: { 
          type: 'category', 
          data: [], 
          axisLine: { lineStyle: { color: '#D8D8D8' } },
          axisLabel: { color: '#3F3F3F' }
      },
      yAxis: [
          { 
              type: 'value', 
              name: '', 
              position: 'left', 
              axisLabel: { formatter: '{value} %', color: '#3F3F3F' },
              axisLine: { lineStyle: { color: '#D8D8D8' } },
              splitLine: { lineStyle: { color: '#D8D8D8' } }
          },
          { 
              type: 'value', 
              name: '', 
              position: 'right', 
              axisLabel: { formatter: '{value} %', color: '#3F3F3F' },
              axisLine: { lineStyle: { color: '#D8D8D8' } },
              splitLine: { show: false }
          }
      ],
      series: [
          { 
              name: 'Voltage Unbalance (%)', 
              type: 'line', 
              data: [], 
              yAxisIndex: 0,
              lineStyle: { width: 2, color: '#475CA7' },
              emphasis: { focus: 'series' }
          },
          { 
              name: 'Current Unbalance (%)', 
              type: 'line', 
              data: [], 
              yAxisIndex: 1,
              lineStyle: { width: 2, color: '#FF6F61' },
              emphasis: { focus: 'series' }
          }
      ]
  };

  // Set initial options
  chart1.setOption(option1);
  chart2.setOption(option2);
  chart3.setOption(option3);
  chart4.setOption(option4);

  // Function to update chart data
  function updateChartData(filteredData) {
      if (!loadingDiv || !chartsGrid) {
          console.error('Required DOM elements missing in updateChartData:', { loading: !!loadingDiv, chartsGrid: !!chartsGrid });
          return;
      }

      if (!filteredData.xAxis || filteredData.xAxis.length === 0) {
          loadingDiv.style.display = 'block';
          loadingDiv.textContent = '';
          chartsGrid.style.display = 'none';
          currentFileDiv.textContent = '';
          return;
      }

      loadingDiv.style.display = 'none';
      chartsGrid.style.display = 'grid';
      currentFileDiv.textContent = `Currently Loaded: ${filteredData.fileName}`;

      chart1.setOption({
          xAxis: { data: filteredData.xAxis },
          series: [
              { name: 'Voltage', data: filteredData.voltage },
              { name: 'Current', data: filteredData.current }
          ]
      });
      chart2.setOption({
          xAxis: { data: filteredData.xAxis },
          series: [
              { name: 'Total_kW', data: filteredData.total_kW },
              { name: 'Total_kVAR', data: filteredData.total_kVAR },
              { name: 'Total_kVA', data: filteredData.total_kVA }
          ]
      });
      chart3.setOption({
          xAxis: { data: filteredData.xAxis },
          series: [
              { name: 'Frequency', data: filteredData.frequency }
          ]
      });
      chart4.setOption({
          xAxis: { data: filteredData.xAxis },
          series: [
              { name: 'Voltage Unbalance (%)', data: filteredData.voltageUnbalance },
              { name: 'Current Unbalance (%)', data: filteredData.currentUnbalance }
          ]
      });
  }

  // Function to filter data based on selected time range
  function filterData(data, filterType, startDate, endDate) {
      if (filterType === 'all' || !filterType) return data;

      const now = new Date();
      let filteredData = { ...data };

      if (filterType === '4hr') {
          const fourHoursAgo = new Date(now.getTime() - 4 * 60 * 60 * 1000);
          filteredData.xAxis = data.xAxis.filter((timestamp) => new Date(timestamp) >= fourHoursAgo);
          filteredData.voltage = data.voltage.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.current = data.current.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.total_kW = data.total_kW.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.total_kVAR = data.total_kVAR.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.total_kVA = data.total_kVA.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.frequency = data.frequency.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.voltageUnbalance = data.voltageUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
          filteredData.currentUnbalance = data.currentUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= fourHoursAgo);
      } else if (filterType === 'day') {
          const oneDayAgo = new Date(now.getTime() - 24 * 60 * 60 * 1000);
          filteredData.xAxis = data.xAxis.filter((timestamp) => new Date(timestamp) >= oneDayAgo);
          filteredData.voltage = data.voltage.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.current = data.current.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.total_kW = data.total_kW.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.total_kVAR = data.total_kVAR.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.total_kVA = data.total_kVA.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.frequency = data.frequency.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.voltageUnbalance = data.voltageUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
          filteredData.currentUnbalance = data.currentUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneDayAgo);
      } else if (filterType === 'week') {
          const oneWeekAgo = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
          filteredData.xAxis = data.xAxis.filter((timestamp) => new Date(timestamp) >= oneWeekAgo);
          filteredData.voltage = data.voltage.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.current = data.current.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.total_kW = data.total_kW.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.total_kVAR = data.total_kVAR.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.total_kVA = data.total_kVA.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.frequency = data.frequency.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.voltageUnbalance = data.voltageUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
          filteredData.currentUnbalance = data.currentUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneWeekAgo);
      } else if (filterType === 'month') {
          const oneMonthAgo = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000);
          filteredData.xAxis = data.xAxis.filter((timestamp) => new Date(timestamp) >= oneMonthAgo);
          filteredData.voltage = data.voltage.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.current = data.current.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.total_kW = data.total_kW.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.total_kVAR = data.total_kVAR.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.total_kVA = data.total_kVA.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.frequency = data.frequency.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.voltageUnbalance = data.voltageUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
          filteredData.currentUnbalance = data.currentUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneMonthAgo);
      } else if (filterType === 'year') {
          const oneYearAgo = new Date(now.getTime() - 365 * 24 * 60 * 60 * 1000);
          filteredData.xAxis = data.xAxis.filter((timestamp) => new Date(timestamp) >= oneYearAgo);
          filteredData.voltage = data.voltage.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.current = data.current.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.total_kW = data.total_kW.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.total_kVAR = data.total_kVAR.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.total_kVA = data.total_kVA.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.frequency = data.frequency.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.voltageUnbalance = data.voltageUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
          filteredData.currentUnbalance = data.currentUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= oneYearAgo);
      } else if (filterType === 'custom') {
          filteredData.xAxis = data.xAxis.filter((timestamp) => {
              const date = new Date(timestamp);
              return date >= startDate && date <= endDate;
          });
          filteredData.voltage = data.voltage.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.current = data.current.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.total_kW = data.total_kW.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.total_kVAR = data.total_kVAR.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.total_kVA = data.total_kVA.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.frequency = data.frequency.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.voltageUnbalance = data.voltageUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
          filteredData.currentUnbalance = data.currentUnbalance.filter((_, index) => new Date(data.xAxis[index]) >= startDate && new Date(data.xAxis[index]) <= endDate);
      }
      return filteredData;
  }

  // Function to apply the selected filter
  function applyFilter() {
      const filterType = document.getElementById('timeFilter').value;
      const startDate = document.getElementById('startDate').value ? new Date(document.getElementById('startDate').value) : null;
      const endDate = document.getElementById('endDate').value ? new Date(document.getElementById('endDate').value) : null;

      if (filterType === 'custom' && (!startDate || !endDate)) {
          loadingDiv.style.display = 'block';
          loadingDiv.textContent = 'Error: Please select both start and end dates for custom range';
          chartsGrid.style.display = 'none';
          return;
      }

      fetch(`${deviceName}_PowerDashboardData.json?t=${Date.now()}`)
          .then(response => {
              if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
              return response.json();
          })
          .then(data => {
              const filteredData = filterData(data, filterType, startDate, endDate);
              updateChartData(filteredData);
          })
          .catch(error => {
              console.error('Error fetching or filtering data:', error);
              loadingDiv.style.display = 'block';
              loadingDiv.textContent = '';
              chartsGrid.style.display = 'none';
              currentFileDiv.textContent = '';
          });
  }

  // Show/hide custom date inputs
  const timeFilter = document.getElementById('timeFilter');
  const customDateRange = document.getElementById('customDateRange');
  if (timeFilter && customDateRange) {
      timeFilter.addEventListener('change', function() {
          customDateRange.style.display = this.value === 'custom' ? 'flex' : 'none';
      });
  } else {
      console.error('Filter controls missing:', { timeFilter: !!timeFilter, customDateRange: !!customDateRange });
  }

  // Apply filter on button click
  const applyFilterButton = document.getElementById('applyFilter');
  if (applyFilterButton) {
      applyFilterButton.addEventListener('click', applyFilter);
  } else {
      console.error('Apply filter button missing');
  }

  // Initial data load
  fetch(`${deviceName}_PowerDashboardData.json?t=${Date.now()}`)
      .then(response => {
          if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
          return response.json();
      })
      .then(data => {
          updateChartData(data);
      })
      .catch(error => {
          console.error('Error fetching initial data:', error);
          loadingDiv.style.display = 'block';
          loadingDiv.textContent = '';
          chartsGrid.style.display = 'none';
          currentFileDiv.textContent = '';
      });

  // Resize charts on window resize
  window.addEventListener('resize', () => {
      if (chart1) chart1.resize();
      if (chart2) chart2.resize();
      if (chart3) chart3.resize();
      if (chart4) chart4.resize();
  });
});