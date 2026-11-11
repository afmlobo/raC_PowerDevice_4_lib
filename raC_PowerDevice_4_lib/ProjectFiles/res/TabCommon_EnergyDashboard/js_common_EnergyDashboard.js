// Energy Dashboard - Dynamic Data Consumer
// Fetches energy-dashboard-data.json and renders ECharts visualizations

let dashboardData = null;
const charts = {};

// Initialize all charts
async function initializeDashboard() {
    try {
        showLoading(true);
        
        // Fetch dashboard data
        const response = await fetch('./energy-dashboard-data.json');
        if (!response.ok) throw new Error('Failed to load dashboard data');
        
        dashboardData = await response.json();
        
        // Update header info
        updateHeaderInfo();
        
        // Update KPIs
        updateKPIs();
        
        // Initialize charts
        initializeCharts();
        
        // Set up resize listener
        window.addEventListener('resize', onWindowResize);
        
        // Refresh data periodically (every 5 minutes)
        setInterval(refreshDashboard, 300000);
        
        showLoading(false);
    } catch (error) {
        console.error('Dashboard initialization failed:', error);
        showLoading(false);
    }
}

function updateHeaderInfo() {
    // Update breadcrumb and location title
    const orgName = dashboardData.orgName || 'Energy Dashboard';
    const breadcrumb = document.getElementById('breadcrumb');
    const locationTitle = document.getElementById('locationTitle');
    
    // Parse org path for breadcrumb
    if (orgName && orgName.includes('/')) {
        const pathParts = orgName.split('/');
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${pathParts.join(' > ')}</span>`;
        const nodeName = pathParts[pathParts.length - 1];
        locationTitle.innerHTML = `<i class="fa-solid fa-location-dot"></i> ${nodeName} Energy Dashboard`;
    } else {
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${orgName}</span>`;
        locationTitle.innerHTML = `<i class="fa-solid fa-location-dot"></i> ${orgName}`;
    }
    
    // Update time range
    const timeRange = document.getElementById('timeRange');
    if (timeRange) {
        timeRange.textContent = dashboardData.timeRange || 'Initializing...';
    }
    
    // Update last updated time
    const lastUpdated = document.getElementById('lastUpdated');
    if (lastUpdated) {
        lastUpdated.textContent = `Last Update: ${dashboardData.lastUpdate || new Date().toLocaleTimeString()}`;
    }
    
    // Update device count
    const deviceCount = document.getElementById('deviceCount');
    if (deviceCount) {
        deviceCount.textContent = `${dashboardData.deviceCount || 0} Devices Aggregated`;
    }
}

function updateKPIs() {
    const kpis = dashboardData.kpis || {};
    
    // Format and update KPI values
    const formatKPI = (value) => {
        if (!value && value !== 0) return '--';
        if (value >= 1000000) return (value / 1000000).toFixed(1) + 'M';
        if (value >= 1000) return (value / 1000).toFixed(1) + 'K';
        return Math.round(value).toString();
    };
    
    document.getElementById('kpiTotalEnergy').textContent = `${formatKPI(kpis.totalEnergy)}`;
    document.getElementById('kpiPeakDemand').textContent = `${formatKPI(kpis.peakDemand)}`;
    document.getElementById('kpiAvgPF').textContent = `${(kpis.avgPowerFactor || 0).toFixed(3)}`;
    document.getElementById('kpiReactiveEnergy').textContent = `${formatKPI(kpis.reactiveEnergy)}`;
    document.getElementById('kpiApparentEnergy').textContent = `${formatKPI(kpis.apparentEnergy)}`;
    
    // Update trends
    document.getElementById('kpiEnergyTrend').textContent = '↑ 12.5%';
    document.getElementById('kpiDemandTrend').textContent = '↓ 3.2%';
    document.getElementById('kpiPFTrend').textContent = '↑ 0.02';
    document.getElementById('kpiReactiveTrend').textContent = '↓ 8.1%';
    document.getElementById('kpiApparentTrend').textContent = '↑ 10.3%';
}

function initializeCharts() {
    // Chart 1: Energy Consumption Trend
    initializeEnergyTrendChart();
    
    // Chart 2: Device-wise Energy Distribution
    initializeDeviceEnergyChart();
    
    // Chart 3: Demand Profile
    initializeDemandChart();
    
    // Chart 4: Active vs Reactive Energy
    initializeActiveReactiveChart();
    
    // Chart 5: Power Factor Analysis
    initializePowerFactorChart();
    
    // Chart 6: Device Status Distribution
    initializeStatusChart();
}

function initializeEnergyTrendChart() {
    const container = document.getElementById('energyTrendChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.energyTrendChart || {};
    
    // Extract device names dynamically from JSON keys
    const deviceNames = new Set();
    const colorMap = {};
    const colors = ['#5470c6', '#91cc75', '#fac858', '#ee6666', '#73c0de', '#f0a732', '#a93838', '#6b5b95'];
    let colorIndex = 0;
    
    Object.keys(data).forEach(key => {
        if (key !== 'timestamps') {
            const match = key.match(/^(.+)_(kWh|kVARh)$/);
            if (match) {
                const deviceName = match[1];
                if (!deviceNames.has(deviceName)) {
                    deviceNames.add(deviceName);
                    colorMap[deviceName] = colors[colorIndex % colors.length];
                    colorIndex++;
                }
            }
        }
    });
    
    const deviceArray = Array.from(deviceNames).sort();
    const legendData = [];
    const series = [];
    
    // Build series for kWh data (left axis)
    deviceArray.forEach(deviceName => {
        const kwhKey = `${deviceName}_kWh`;
        const seriesName = `${deviceName} (kWh)`;
        legendData.push(seriesName);
        
        if (data[kwhKey]) {
            series.push({
                name: seriesName,
                type: 'line',
                smooth: true,
                data: data[kwhKey].map(v => v.toFixed(2)),
                itemStyle: { color: colorMap[deviceName] },
                areaStyle: { opacity: 0.3 }
            });
        }
    });
    
    // Build series for kVARh data (right axis)
    deviceArray.forEach(deviceName => {
        const kvarhKey = `${deviceName}_kVARh`;
        const seriesName = `${deviceName} (kVARh)`;
        legendData.push(seriesName);
        
        if (data[kvarhKey]) {
            series.push({
                name: seriesName,
                type: 'line',
                smooth: true,
                yAxisIndex: 1,
                data: data[kvarhKey].map(v => v.toFixed(2)),
                itemStyle: { color: colorMap[deviceName] },
                lineStyle: { type: 'dashed' }
            });
        }
    });
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'cross' }
        },
        legend: {
            data: legendData,
            top: 10
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            boundaryGap: false,
            data: data.timestamps || [],
            axisLabel: { rotate: 45, fontSize: 10 }
        },
        yAxis: [
            {
                type: 'value',
                name: 'Active (kWh)',
                position: 'left',
                axisLabel: { formatter: '{value}' }
            },
            {
                type: 'value',
                name: 'Reactive (kVARh)',
                position: 'right',
                axisLabel: { formatter: '{value}' }
            }
        ],
        series: series
    };
    
    chart.setOption(option);
    charts.energyTrend = chart;
}

function initializeDeviceEnergyChart() {
    const container = document.getElementById('deviceEnergyChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.deviceEnergyChart || {};
    const devices = data.devices || [];
    
    const chartData = devices.map(d => ({
        value: d.value,
        name: d.name,
        itemStyle: {
            color: ['#5470c6', '#91cc75', '#fac858', '#ee6666', '#73c0de'][devices.indexOf(d)]
        }
    }));
    
    const option = {
        tooltip: {
            trigger: 'item',
            formatter: '{b}: {c} kWh ({d}%)'
        },
        legend: { 
            bottom: 0, 
            left: 'center',
            type: 'scroll',
            pageIconColor: '#5470c6',
            pageTextStyle: { color: '#333' }
        },
        series: [
            {
                type: 'pie',
                radius: ['40%', '65%'],
                center: ['50%', '45%'],
                avoidLabelOverlap: true,
                itemStyle: {
                    borderRadius: 10,
                    borderColor: '#fff',
                    borderWidth: 2
                },
                label: {
                    show: true,
                    formatter: '{b}\n{d}%',
                    fontSize: 11,
                    overflow: 'truncate',
                    width: 80
                },
                labelLine: {
                    show: true,
                    length: 15,
                    length2: 10
                },
                labelLayout: {
                    hideOverlap: true
                },
                emphasis: {
                    label: { show: true, fontSize: 14, fontWeight: 'bold' }
                },
                data: chartData
            }
        ]
    };
    
    chart.setOption(option);
    charts.deviceEnergy = chart;
}

function initializeDemandChart() {
    const container = document.getElementById('demandChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.demandChart || {};
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'shadow' }
        },
        legend: {
            data: ['kW Demand', 'kVAR Demand', 'kVA Demand', 'Projected kW'],
            top: 10
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            data: data.timestamps || [],
            axisLabel: { rotate: 45, fontSize: 10 }
        },
        yAxis: { type: 'value', name: 'Demand' },
        series: [
            {
                name: 'kW Demand',
                type: 'bar',
                data: (data.kWDemand || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#5470c6' }
            },
            {
                name: 'kVAR Demand',
                type: 'bar',
                data: (data.kVARDemand || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#91cc75' }
            },
            {
                name: 'kVA Demand',
                type: 'bar',
                data: (data.kVADemand || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#fac858' }
            },
            {
                name: 'Projected kW',
                type: 'line',
                data: (data.projectedKWDemand || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#ee6666' },
                lineStyle: { type: 'dashed' }
            }
        ]
    };
    
    chart.setOption(option);
    charts.demand = chart;
}

function initializeActiveReactiveChart() {
    const container = document.getElementById('activeReactiveChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.activeReactiveChart || {};
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'shadow' }
        },
        legend: {
            data: ['Active Energy (kWh)', 'Reactive Energy (kVARh)', 'Apparent Energy (kVAh)'],
            top: 10
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            data: data.labels || []
        },
        yAxis: { type: 'value', name: 'Energy' },
        series: [
            {
                name: 'Active Energy (kWh)',
                type: 'bar',
                data: (data.activeEnergy || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#5470c6' }
            },
            {
                name: 'Reactive Energy (kVARh)',
                type: 'bar',
                data: (data.reactiveEnergy || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#91cc75' }
            },
            {
                name: 'Apparent Energy (kVAh)',
                type: 'bar',
                data: (data.apparentEnergy || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#fac858' }
            }
        ]
    };
    
    chart.setOption(option);
    charts.activeReactive = chart;
}

function initializePowerFactorChart() {
    const container = document.getElementById('powerFactorChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.powerFactorChart || {};
    
    // Extract device names dynamically from JSON keys (exclude timestamps and targetPF)
    const deviceNames = [];
    const colorMap = {};
    const colors = ['#5470c6', '#91cc75', '#fac858', '#ee6666', '#73c0de', '#f0a732', '#a93838', '#6b5b95'];
    let colorIndex = 0;
    
    Object.keys(data).forEach(key => {
        if (key !== 'timestamps' && key !== 'targetPF') {
            deviceNames.push(key);
            colorMap[key] = colors[colorIndex % colors.length];
            colorIndex++;
        }
    });
    
    deviceNames.sort();
    
    // Build legend data with all devices (including those with zero values)
    const legendData = deviceNames.map(name => name + ' PF');
    if (data.targetPF && data.targetPF.length > 0) {
        legendData.push('Target PF');
    }
    
    const series = [];
    
    // Build series for each device PF (show all devices, even with zero data)
    deviceNames.forEach(deviceName => {
        series.push({
            name: deviceName + ' PF',
            type: 'line',
            smooth: true,
            data: (data[deviceName] || []).map(v => (v / 100).toFixed(3)), // Convert from 0-100 to 0-1 scale
            itemStyle: { color: colorMap[deviceName] }
        });
    });
    
    // Add target PF line if it has data
    if (data.targetPF && data.targetPF.length > 0) {
        series.push({
            name: 'Target PF',
            type: 'line',
            data: data.targetPF.map(v => (v / 100).toFixed(3)), // Convert from 0-100 to 0-1 scale
            lineStyle: { type: 'dashed', color: '#ee6666' },
            itemStyle: { color: '#ee6666' }
        });
    }
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'line' },
            formatter: function(params) {
                let result = params[0].axisValue + '<br/>';
                params.forEach(param => {
                    const value = parseFloat(param.value);
                    result += param.marker + param.seriesName + ': ' + value.toFixed(3) + ' (' + (value * 100).toFixed(1) + '%)<br/>';
                });
                return result;
            }
        },
        legend: {
            data: legendData,
            top: 10,
            type: 'scroll'
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            data: data.timestamps || [],
            axisLabel: { rotate: 45, fontSize: 10 }
        },
        yAxis: {
            type: 'value',
            name: 'PF',
            min: 0,
            max: 1.0,
            axisLabel: { formatter: '{value}' }
        },
        series: series
    };
    
    chart.setOption(option);
    charts.powerFactor = chart;
}

function initializeStatusChart() {
    const container = document.getElementById('statusChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.statusChart || {};
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'shadow' }
        },
        legend: {
            data: ['Status 1', 'Status 2', 'Status 3', 'Status 4'],
            top: 10
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            data: data.labels || []
        },
        yAxis: {
            type: 'value',
            name: 'Count'
        },
        series: [
            {
                name: 'Status 1',
                type: 'bar',
                stack: 'total',
                data: data.status1 || [],
                itemStyle: { color: '#5470c6' }
            },
            {
                name: 'Status 2',
                type: 'bar',
                stack: 'total',
                data: data.status2 || [],
                itemStyle: { color: '#91cc75' }
            },
            {
                name: 'Status 3',
                type: 'bar',
                stack: 'total',
                data: data.status3 || [],
                itemStyle: { color: '#fac858' }
            },
            {
                name: 'Status 4',
                type: 'bar',
                stack: 'total',
                data: data.status4 || [],
                itemStyle: { color: '#ee6666' }
            }
        ]
    };
    
    chart.setOption(option);
    charts.status = chart;
}

function onWindowResize() {
    Object.values(charts).forEach(chart => {
        if (chart) chart.resize();
    });
}

async function refreshDashboard() {
    try {
        const response = await fetch('./energy-dashboard-data.json?' + new Date().getTime());
        if (response.ok) {
            dashboardData = await response.json();
            updateHeaderInfo();
            updateKPIs();
            initializeCharts();
        }
    } catch (error) {
        console.error('Failed to refresh dashboard:', error);
    }
}

function showLoading(show) {
    const loading = document.getElementById('loading');
    if (loading) {
        loading.classList.toggle('active', show);
    }
}

function formatNumber(value) {
    if (!value && value !== 0) return '--';
    if (value >= 1000000) return (value / 1000000).toFixed(1) + 'M';
    if (value >= 1000) return (value / 1000).toFixed(1) + 'K';
    return Math.round(value).toString();
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', initializeDashboard);
