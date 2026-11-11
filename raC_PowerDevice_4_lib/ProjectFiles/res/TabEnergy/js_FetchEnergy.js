var deviceName = document.body.getAttribute('data-device-name');
if (!deviceName) {
  console.error('deviceName is not defined');
  // Stop further execution without using return
  // Optionally, display an error message in the UI
  document.body.innerHTML = '<h1>Error: Device name is not defined</h1>';
} else {
  var jsonFile = `${deviceName}_EnergyData.json`;
  var barChart = echarts.init(document.getElementById('bar-chart'));
  var pfGauge = echarts.init(document.getElementById('pf-gauge'));
  var statusDot = document.getElementById('status-dot');

  // Initial chart configurations
  var barOption = {
    tooltip: {
      trigger: 'axis',
      formatter: (params) => `${params[0].name}: ${params[0].value} ${params[0].dataIndex < 3 ? 'kWh' : params[0].dataIndex === 3 ? 'kW' : 'kVA'}`
    },
    xAxis: {
      type: 'category',
      data: ['Forward (kWh)', 'Reverse (kWh)', 'Net (kWh)', 'kW Demand (kW)', 'kVA Demand (kVA)'],
      axisLine: {
        lineStyle: {
          color: '#A0A0A4'
        }
      },
      axisLabel: {
        rotate: 45,
        color: '#3F3F3F'
      }
    },
    yAxis: {
      type: 'value',
      axisLabel: {
        formatter: '{value}',
        color: '#3F3F3F'
      },
      axisLine: {
        lineStyle: {
          color: '#A0A0A4'
        }
      }
    },
    series: [{
      data: [0, 0, 0, 0, 0],
      type: 'bar',
      itemStyle: {
        color: (params) => ['#475CA7', '#475CA7', '#475CA7', '#475CA7', '#475CA7'][params.dataIndex]
      }
    }]
  };

  var gaugeOption = {
    series: [{
      type: 'gauge',
      center: ['50%', '60%'],
      radius: '70%',
      startAngle: 180,
      endAngle: 0,
      min: -100,
      max: 100,
      splitNumber: 4,
      axisLine: {
        lineStyle: {
          width: 20,
          color: [[1, '#475CA7']]
        }
      },
      pointer: {
        width: 5,
        length: '70%',
        itemStyle: {
          color: '#3F3F3F'
        }
      },
      axisTick: {
        show: false
      },
      splitLine: {
        show: false
      },
      axisLabel: {
        distance: -50,
        fontSize: 12,
        color: '#3F3F3F',
        formatter: function(value) {
          return [100, 50, 0, -50, -100].includes(value) ? `${value}%` : '';
        }
      },
      title: {
        offsetCenter: [0, '40%'],
        fontSize: 14,
        color: '#3F3F3F',
        formatter: 'Demand Power Factor'
      },
      detail: {
        valueAnimation: true,
        offsetCenter: [0, '70%'],
        fontSize: 20,
        color: '#475CA7',
        formatter: function(value) {
          return value.toFixed(2) + '%';
        }
      },
      data: [{
        value: 0,
        name: 'Demand Power Factor'
      }]
    }]
  };

  barChart.setOption(barOption);
  pfGauge.setOption(gaugeOption);

  function fetchEnergyData() {
    statusDot.classList.add('pulse');
    fetch(jsonFile)
      .then(response => {
        if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
        return response.json();
      })
      .then(data => {
        updateDashboard(data);
        statusDot.classList.add('pulse');
      })
      .catch(error => {
        console.error('Error fetching data:', error);
        statusDot.classList.remove('pulse');
      });
  }

  function combineGigaKilo(giga, kilo) {
    const totalKilo = (giga * 1000000) + kilo;
    return totalKilo.toLocaleString('en-US', {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    });
  }

  function updateDashboard(data) {
    var energyData = data.EnergyData || {};
    var lastRefreshTimestamp = data.LastRefreshTimestamp || 'N/A';
    var parsedData = {
      kWh_Fwd: combineGigaKilo(energyData.GWh_Fwd || 0, energyData.kWh_Fwd || 0),
      kWh_Rev: combineGigaKilo(energyData.GWh_Rev || 0, energyData.kWh_Rev || 0),
      kWh_Net: combineGigaKilo(energyData.GWh_Net || 0, energyData.kWh_Net || 0),
      kW_Demand: energyData.kW_Demand || 0,
      kVA_Demand: energyData.kVA_Demand || 0,
      Demand_PF: energyData.Demand_PF || 0,
      kVARh_Fwd: combineGigaKilo(energyData.GVARh_Fwd || 0, energyData.kVARh_Fwd || 0),
      kVARh_Rev: combineGigaKilo(energyData.GVARh_Rev || 0, energyData.kVARh_Rev || 0),
      kVARh_Net: combineGigaKilo(energyData.GVARh_Net || 0, energyData.kVARh_Net || 0),
      kVAh: combineGigaKilo(energyData.GVAh || 0, energyData.kVAh || 0),
      kAh: combineGigaKilo(energyData.GAh || 0, energyData.kAh || 0),
      kVAR_Demand: energyData.kVAR_Demand || 0,
      Demand_Amps: energyData.Demand_Amps || 0,
      Projected_kW_Demand: energyData.Projected_kW_Demand || 0,
      Projected_kVAR_Demand: energyData.Projected_kVAR_Demand || 0,
      Projected_kVA_Demand: energyData.Projected_kVA_Demand || 0,
      Projected_Ampere_Demand: energyData.Projected_Ampere_Demand || 0
    };

    // Update charts
    barChart.setOption({
      series: [{
        data: [
          parseFloat(parsedData.kWh_Fwd.replace(/,/g, '')),
          parseFloat(parsedData.kWh_Rev.replace(/,/g, '')),
          parseFloat(parsedData.kWh_Net.replace(/,/g, '')),
          parsedData.kW_Demand,
          parsedData.kVA_Demand
        ]
      }]
    });

    pfGauge.setOption({
      series: [{
        data: [{
          value: parsedData.Demand_PF,
          name: 'Demand Power Factor'
        }]
      }]
    });

    // Update Main Data
    document.getElementById('forward').textContent = parsedData.kWh_Fwd;
    document.getElementById('reverse').textContent = parsedData.kWh_Rev;
    document.getElementById('net').textContent = parsedData.kWh_Net;
    document.getElementById('kvarh-fwd').textContent = parsedData.kVARh_Fwd;
    document.getElementById('kvarh-rev').textContent = parsedData.kVARh_Rev;
    document.getElementById('kvarh-net').textContent = parsedData.kVARh_Net;

    // Update Demand Group
    document.getElementById('demand-amps').textContent = parsedData.Demand_Amps.toFixed(3);
    document.getElementById('kw-demand').textContent = parsedData.kW_Demand.toFixed(3);
    document.getElementById('kvar-demand').textContent = parsedData.kVAR_Demand.toFixed(3);
    document.getElementById('kva-demand').textContent = parsedData.kVA_Demand.toFixed(3);

    // Update Projection Group
    document.getElementById('proj-amps').textContent = parsedData.Projected_Ampere_Demand.toFixed(3);
    document.getElementById('proj-kw-demand').textContent = parsedData.Projected_kW_Demand.toFixed(3);
    document.getElementById('proj-kvar-demand').textContent = parsedData.Projected_kVAR_Demand.toFixed(3);
    document.getElementById('proj-kva-demand').textContent = parsedData.Projected_kVA_Demand.toFixed(3);

    // Update Apparent Energy & Amp Hours Group
    document.getElementById('kvah').textContent = parsedData.kVAh;
    document.getElementById('kah').textContent = parsedData.kAh;

    // Update Status Bar
    document.getElementById('last-updated').textContent = `Last updated: ${new Date(lastRefreshTimestamp).toLocaleTimeString()}`;
  }

  // Initial fetch and set interval
  fetchEnergyData();
  setInterval(fetchEnergyData, 1000);
}
