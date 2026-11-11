// Energy Dashboard - Single Device Data Consumer
// Fetches EnergyLogData.json and renders ECharts visualizations (Single Device Optimized)

let dashboardData = null;
const charts = {};

// Initialize all charts
async function initializeDashboard() {
    try {
        showLoading(true);
        
        // Fetch dashboard data
        const response = await fetch("./EnergyLogData.json?v=" + Date.now());
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
        
        // Refresh data periodically (every 30 seconds for single device)
        setInterval(refreshDashboard, 30000);
        
        showLoading(false);
    } catch (error) {
        console.error('Dashboard initialization failed:', error);
        showLoading(false);
    }
}

function updateHeaderInfo() {
    // Update device name and time range
    const timeRange = document.getElementById('timeRange');
    if (timeRange) {
        timeRange.textContent = dashboardData.timeRange || 'Last 30 Days';
    }
    
    // Update last updated time
    const lastUpdated = document.getElementById('lastUpdated');
    if (lastUpdated) {
        lastUpdated.textContent = `Last Update: ${dashboardData.lastUpdate || new Date().toLocaleTimeString()}`;
    }
    
    // Update device name badge
    const deviceName = document.getElementById('deviceName');
    if (deviceName) {
        deviceName.textContent = dashboardData.deviceName || 'Device';
    }
    
    // Update location title
    const locationTitle = document.getElementById('locationTitle');
    if (locationTitle) {
        const name = dashboardData.deviceName || 'Device';
        locationTitle.innerHTML = `<i class="fa-solid fa-location-dot"></i> ${name} Energy Dashboard`;
    }
}

function updateKPIs() {
    const kpis = dashboardData.kpis || {};
    
    document.getElementById('kpiTotalEnergy').textContent = `${formatNumber(kpis.totalEnergy)}`;
    document.getElementById('kpiPeakDemand').textContent = `${formatNumber(kpis.peakDemand)}`;
    document.getElementById('kpiAvgPF').textContent = `${(kpis.avgPowerFactor || 0).toFixed(3)}`;
    document.getElementById('kpiReactiveEnergy').textContent = `${formatNumber(kpis.reactiveEnergy)}`;
    document.getElementById('kpiApparentEnergy').textContent = `${formatNumber(kpis.apparentEnergy)}`;
    
    // Update trends (mock values - customize based on comparison logic)
    document.getElementById('kpiEnergyTrend').textContent = '↑ 12.5%';
    document.getElementById('kpiDemandTrend').textContent = '↓ 3.2%';
    document.getElementById('kpiPFTrend').textContent = '↑ 0.02';
    document.getElementById('kpiReactiveTrend').textContent = '↓ 8.1%';
    document.getElementById('kpiApparentTrend').textContent = '↑ 10.3%';
}

function initializeCharts() {
    // Chart 1: Energy Consumption Trend
    initializeEnergyTrendChart();
    
    // Chart 2: Demand Profile
    initializeDemandChart();
    
    // Chart 3: Active vs Reactive Energy
    initializeActiveReactiveChart();
    
    // Chart 4: Power Factor Analysis (Single device)
    initializePowerFactorChart();
    
    // Chart 5: Device Status Distribution
    initializeStatusChart();
}

function initializeEnergyTrendChart() {
    const container = document.getElementById('energyTrendChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const data = dashboardData.energyTrendChart || {};
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'cross' }
        },
        legend: {
            data: ['Device (kWh)',  'Device (kVARh)'],
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
        series: [
            {
                name: 'Device (kWh)',
                type: 'line',
                smooth: true,
                data: (data.device1KWh || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#5470c6' },
                areaStyle: { opacity: 0.3 }
            },
            {
                name: 'Device (kVARh)',
                type: 'line',
                smooth: true,
                yAxisIndex: 1,
                data: (data.device1KVARh || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#fac858' }
            }
        ]
    };
    
    chart.setOption(option);
    charts.energyTrend = chart;
}

// Device Energy Chart removed - not useful for single device
// function initializeDeviceEnergyChart() { ... }

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
    const deviceLabel = (data.labels && data.labels.length > 0) ? data.labels[0] : 'Device';
    const activeEnergy = (data.activeEnergy && data.activeEnergy.length > 0) ? [data.activeEnergy[0]] : [];
    const reactiveEnergy = (data.reactiveEnergy && data.reactiveEnergy.length > 0) ? [data.reactiveEnergy[0]] : [];
    const apparentEnergy = (data.apparentEnergy && data.apparentEnergy.length > 0) ? [data.apparentEnergy[0]] : [];
    
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
            data: [deviceLabel]
        },
        yAxis: { type: 'value', name: 'Energy' },
        series: [
            {
                name: 'Active Energy (kWh)',
                type: 'bar',
                data: activeEnergy.map(v => v.toFixed(2)),
                itemStyle: { color: '#5470c6' }
            },
            {
                name: 'Reactive Energy (kVARh)',
                type: 'bar',
                data: reactiveEnergy.map(v => v.toFixed(2)),
                itemStyle: { color: '#91cc75' }
            },
            {
                name: 'Apparent Energy (kVAh)',
                type: 'bar',
                data: apparentEnergy.map(v => v.toFixed(2)),
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
    
    const toPercent = (value) => {
        const numeric = parseFloat(value);
        if (Number.isNaN(numeric)) return 0;
        return numeric <= 1 ? numeric * 100 : numeric;
    };

    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'line' },
            formatter: function(params) {
                let result = '<strong>' + params[0].axisValue + '</strong><br/>';
                params.forEach(param => {
                    const value = parseFloat(param.value);
                    result += param.marker + ' ' + param.seriesName + ': <strong>' + value.toFixed(1) + '</strong><br/>';
                });
                return result;
            }
        },
        legend: {
            data: ['Device PF', 'Target PF'],
            top: 10
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            data: data.timestamps || [],
            axisLabel: { rotate: 45, fontSize: 10 }
        },
        yAxis: {
            type: 'value',
            name: 'Power Factor (0-100)',
            min: 0,
            max: 100,
            interval: 10
        },
        series: [
            {
                name: 'Device PF',
                type: 'line',
                smooth: true,
                data: (data.device1PF || []).map(v => toPercent(v).toFixed(1)),
                itemStyle: { color: '#5470c6' },
                markLine: {
                    silent: true,
                    lineStyle: { color: '#ee6666', type: 'dashed', width: 1 },
                    label: { show: false }
                }
            },
            {
                name: 'Target PF',
                type: 'line',
                data: (data.targetPF || []).map(v => toPercent(v).toFixed(1)),
                lineStyle: { type: 'dashed', color: '#ee6666', width: 2 },
                itemStyle: { color: '#ee6666' },
                symbol: 'none'
            }
        ]
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
        const response = await fetch('./EnergyLogData.json?' + new Date().getTime());
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
