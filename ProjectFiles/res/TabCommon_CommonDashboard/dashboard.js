// ═══════════════════════════════════════════════════════════════
// Power Monitoring Dashboard - JavaScript
// ═══════════════════════════════════════════════════════════════

// Global state
let dashboardData = null;
let refreshInterval = null;
let energyChart = null;
let loadingTimeout = null;
let energyTrendState = {
    fullData: [],
    activeRange: "24hours",
    periodSize: "hour",
    availableRanges: []
};

// ═══════════════════════════════════════════════════════════════
// Configuration
// ═══════════════════════════════════════════════════════════════
const CONFIG = {
    dataUrl: 'Dashboard_Data.json', // URL to fetch JSON data
    refreshIntervalMs: 500, // Refresh every 500ms for faster updates
    enableAutoRefresh: true
};

const ENERGY_RANGE_CONFIG = {
    '24hours': { label: '24 Hours', hours: 24 },
    '7days': { label: '7 Days', hours: 7 * 24 },
    '30days': { label: '30 Days', hours: 30 * 24 }
};

// ═══════════════════════════════════════════════════════════════
// Initialize Dashboard
// ═══════════════════════════════════════════════════════════════
function initDashboard() {
    console.log('Initializing dashboard...');
    
    // Load initial data
    loadDashboardData();
    
    // Setup auto-refresh if enabled
    if (CONFIG.enableAutoRefresh) {
        refreshInterval = setInterval(loadDashboardData, CONFIG.refreshIntervalMs);
    }
    
    // Setup event listeners
    setupEventListeners();
}

// ═══════════════════════════════════════════════════════════════
// Load Dashboard Data
// ═══════════════════════════════════════════════════════════════
function loadDashboardData() {
    fetch(CONFIG.dataUrl)
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            return response.json();
        })
        .then(data => {
            console.log('Dashboard data loaded:', data);
            dashboardData = data;
            updateDashboard(data);
        })
        .catch(error => {
            console.error('Error loading dashboard data:', error);
            showError('Failed to load dashboard data');
            hideLoadingOverlay(); // Hide overlay on error
        });
}

// ═══════════════════════════════════════════════════════════════
// Update Dashboard with Data
// ═══════════════════════════════════════════════════════════════
function updateDashboard(data) {
    dashboardData = data;
    
    // Check if data is in loading state
    if (data && data.isLoading === true) {
        console.log('Dashboard is loading...');
        showLoadingOverlay();
        // Set safety timeout to hide overlay after 10 seconds (fail-safe)
        if (loadingTimeout) clearTimeout(loadingTimeout);
        loadingTimeout = setTimeout(hideLoadingOverlay, 10000);
        return; // Don't update UI until loading is complete
    }
    
    // Loading complete - hide overlay and update UI
    hideLoadingOverlay();
    
    updateHeader(data);
    updateKPICards(data);
    initializeEnergyTrend(data.energyTrend);
    updateAlarms(data);
    updatePQEvents(data);
}

// ═══════════════════════════════════════════════════════════════
// Update Header
// ═══════════════════════════════════════════════════════════════
function updateHeader(data) {
    // Update breadcrumb with org node path
    const breadcrumb = document.getElementById('breadcrumb');
    if (data.orgNodePath) {
        const pathParts = data.orgNodePath.split('/').filter(p => p);
        breadcrumb.innerHTML = pathParts.map((part, index) => {
            const icon = index === 0 ? '<i class="fa-solid fa-building"></i>' : (index === pathParts.length - 1 ? '<i class="fa-solid fa-location-dot"></i>' : '<i class="fa-solid fa-industry"></i>');
            return `<span class="breadcrumb-item">${icon} ${part}</span>`;
        }).join('<span>›</span>');
    }
    
    // Update location title
    const locationTitle = document.getElementById('locationTitle');
    const orgName = data.orgNodeName || 'Dashboard';
    locationTitle.innerHTML = `<i class="fa-solid fa-location-dot"></i> ${orgName} Dashboard<span class="status-badge">
        <span style="width: 8px; height: 8px; background: white; border-radius: 50%; display: inline-block;"></span>
        Live
    </span>`;
    
    // Update last updated time
    const lastUpdated = document.getElementById('lastUpdated');
    lastUpdated.textContent = `Last updated: ${data.lastUpdated || 'Unknown'}`;
    
}

// ═══════════════════════════════════════════════════════════════
// Update KPI Cards
// ═══════════════════════════════════════════════════════════════
function updateKPICards(data) {
    // Active Devices
    if (data.activeDevices) {
        document.getElementById('activeDevicesValue').textContent = data.activeDevices.value;
        document.getElementById('activeDevicesSubtext').textContent = data.activeDevices.subtext || 'No data';
        updateTrend('activeDevicesTrend', data.activeDevices.change, data.activeDevices.trend);
    }
    
    // Energy Today
    if (data.energyToday) {
        const energyValue = typeof data.energyToday.value === 'number' 
            ? data.energyToday.value.toFixed(2) 
            : data.energyToday.value;
        document.getElementById('energyValue').textContent = energyValue;
        document.getElementById('energySubtext').textContent = 
            data.energyToday.unit ? `${data.energyToday.unit} consumed` : data.energyToday.subtext;
        updateTrend('energyTrend', data.energyToday.change, data.energyToday.trend);
    }
    
    // Active Alarms
    if (data.activeAlarms) {
        document.getElementById('alarmsValue').textContent = data.activeAlarms.value;
        document.getElementById('alarmsSubtext').textContent = data.activeAlarms.subtext || 'No data';
    }
    
    // Power Quality
    if (data.powerQuality) {
        const pqValue = data.powerQuality.value || 0;
        const pqUnit = data.powerQuality.unit || '';
        document.getElementById('pqValue').textContent = pqUnit === 'Events' ? pqValue : `${pqValue}%`;
        document.getElementById('pqSubtext').textContent = data.powerQuality.subtext || 'No data';
        updateTrend('pqTrend', data.powerQuality.change, data.powerQuality.trend);
    }
}

// ═══════════════════════════════════════════════════════════════
// Update Trend Display
// ═══════════════════════════════════════════════════════════════
function updateTrend(elementId, change, trend) {
    const element = document.getElementById(elementId);
    if (!element) return;
    
    // Remove existing trend classes
    element.classList.remove('up', 'down', 'neutral');
    
    // Add new trend class
    if (trend) {
        element.classList.add(trend);
    }
    
    // Set trend icon and text
    let icon = '⟳';
    if (trend === 'up') icon = '↑';
    else if (trend === 'down') icon = '↓';
    
    element.textContent = `${icon} ${change || 'No change'}`;
}

// ═══════════════════════════════════════════════════════════════
// Update Energy Chart
// ═══════════════════════════════════════════════════════════════
function initializeEnergyTrend(trendPayload) {
    var fallbackRange = '24hours';
    if (trendPayload && trendPayload.defaultRange && ENERGY_RANGE_CONFIG.hasOwnProperty(trendPayload.defaultRange)) {
        fallbackRange = trendPayload.defaultRange;
    }

    var previousSelection = energyTrendState.activeRange || fallbackRange;
    var candidateRange = ENERGY_RANGE_CONFIG.hasOwnProperty(previousSelection) ? previousSelection : fallbackRange;

    var fullData = [];
    if (trendPayload && Array.isArray(trendPayload.data)) {
        fullData = trendPayload.data;
    }

    var periodSize = 'hour';
    if (trendPayload && trendPayload.periodSize) {
        periodSize = trendPayload.periodSize;
    }

    var availableRanges = [];
    if (trendPayload && Array.isArray(trendPayload.availableRanges)) {
        availableRanges = trendPayload.availableRanges.filter(function(range) {
            return ENERGY_RANGE_CONFIG.hasOwnProperty(range);
        });
    }

    if (availableRanges.length === 0) {
        availableRanges = Object.keys(ENERGY_RANGE_CONFIG);
    }

    energyTrendState.fullData = fullData;
    energyTrendState.periodSize = periodSize;
    energyTrendState.availableRanges = availableRanges;
    energyTrendState.activeRange = availableRanges.indexOf(candidateRange) >= 0
        ? candidateRange
        : availableRanges[0];

    updateEnergyRangeButtons();
    renderEnergyTrend();
}

function renderEnergyTrend() {
    var rangeKey = energyTrendState.activeRange;
    var filteredSeries = getFilteredEnergyTrendData(rangeKey);
    renderEnergyChart(filteredSeries, rangeKey);
    updateEnergyChartTitle(filteredSeries, rangeKey);
}

function setEnergyRange(rangeKey) {
    if (!ENERGY_RANGE_CONFIG.hasOwnProperty(rangeKey)) {
        return;
    }

    if (energyTrendState.availableRanges.indexOf(rangeKey) === -1) {
        return;
    }

    if (energyTrendState.activeRange === rangeKey) {
        return;
    }

    energyTrendState.activeRange = rangeKey;
    updateEnergyRangeButtons();
    renderEnergyTrend();
}

function updateEnergyRangeButtons() {
    var buttons = document.querySelectorAll('.range-btn[data-range]');
    Array.prototype.forEach.call(buttons, function(button) {
        var range = button.getAttribute('data-range');
        var isAvailable = energyTrendState.availableRanges.indexOf(range) >= 0;

        if (!isAvailable) {
            button.disabled = true;
            button.classList.remove('active');
            return;
        }

        button.disabled = false;

        if (range === energyTrendState.activeRange) {
            button.classList.add('active');
        } else {
            button.classList.remove('active');
        }
    });
}

function getFilteredEnergyTrendData(rangeKey) {
    var allData = energyTrendState.fullData || [];

    if (!Array.isArray(allData) || allData.length === 0) {
        return [];
    }

    if (!ENERGY_RANGE_CONFIG.hasOwnProperty(rangeKey)) {
        return allData.slice();
    }

    var hours = ENERGY_RANGE_CONFIG[rangeKey].hours;
    if (!hours || hours <= 0 || energyTrendState.periodSize !== 'hour') {
        return allData.slice();
    }

    var lastItem = allData[allData.length - 1];
    if (!lastItem) {
        return allData.slice();
    }

    var lastDate = parsePeriodToDate(lastItem.period);
    if (!lastDate) {
        return allData.slice();
    }

    var cutoff = new Date(lastDate.getTime() - hours * 60 * 60 * 1000);

    var filtered = allData.filter(function(item) {
        var itemDate = parsePeriodToDate(item.period);
        return itemDate && itemDate >= cutoff;
    });

    if (filtered.length === 0) {
        var fallbackCount = Math.min(allData.length, Math.max(1, Math.round(hours)));
        return allData.slice(allData.length - fallbackCount);
    }

    return filtered;
}

function parsePeriodToDate(period) {
    if (!period) {
        return null;
    }

    var normalized = period.replace(' ', 'T');
    var parsed = new Date(normalized);

    if (isNaN(parsed.getTime())) {
        return null;
    }

    return parsed;
}

function pad2(value) {
    return value < 10 ? '0' + value : String(value);
}

function formatPeriodForTitle(period) {
    var date = parsePeriodToDate(period);
    if (!date) {
        return '';
    }

    var month = pad2(date.getMonth() + 1);
    var day = pad2(date.getDate());
    var hours = pad2(date.getHours());
    var minutes = pad2(date.getMinutes());

    return month + '-' + day + ' ' + hours + ':' + minutes;
}

function formatAxisTick(period, useTimeOnly) {
    var date = parsePeriodToDate(period);
    if (!date) {
        return '';
    }

    if (useTimeOnly) {
        return pad2(date.getHours()) + ':' + pad2(date.getMinutes());
    }

    return pad2(date.getMonth() + 1) + '-' + pad2(date.getDate());
}

function updateEnergyChartTitle(series, rangeKey) {
    var chartTitle = document.getElementById('chartTitle');
    if (!chartTitle) {
        return;
    }

    var rangeConfig = ENERGY_RANGE_CONFIG[rangeKey] || null;
    var rangeLabel = rangeConfig ? rangeConfig.label : 'Selected Range';

    if (!series || series.length === 0) {
        chartTitle.innerHTML = '<i class="fa-solid fa-bolt"></i> Energy Consumption Trend (' + rangeLabel + ')';
        return;
    }

    var firstLabel = formatPeriodForTitle(series[0].period);
    var lastLabel = formatPeriodForTitle(series[series.length - 1].period);
    var subtitle = firstLabel && lastLabel ? firstLabel + ' → ' + lastLabel : '';

    chartTitle.innerHTML = subtitle
        ? '<i class="fa-solid fa-bolt"></i> Energy Consumption Trend (' + rangeLabel + ' | ' + subtitle + ')'
        : '<i class="fa-solid fa-bolt"></i> Energy Consumption Trend (' + rangeLabel + ')';
}

function renderEnergyChart(series, rangeKey) {
    const chartContainer = document.getElementById('chartContainer');

    if (!chartContainer) {
        return;
    }

    if (!Array.isArray(series) || series.length === 0) {
        chartContainer.innerHTML = '<div class="no-data-message">No energy trend data available</div>';
        if (energyChart) {
            energyChart.dispose();
            energyChart = null;
        }
        return;
    }

    const energyValues = series.map(item => {
        if (!item) {
            return 0;
        }
        const numericValue = typeof item.energy === 'number' ? item.energy : parseFloat(item.energy);
        return isFinite(numericValue) ? numericValue : 0;
    });

    const maxValue = Math.max(...energyValues);

    if (!isFinite(maxValue) || maxValue <= 0) {
        chartContainer.innerHTML = '<div class="no-data-message">No energy consumption recorded in this range</div>';
        if (energyChart) {
            energyChart.dispose();
            energyChart = null;
        }
        return;
    }

    // Aggregate data based on range for better visualization
    let aggregatedData = aggregateDataByRange(series, rangeKey);

    // Check if echarts is available
    if (typeof echarts === 'undefined') {
        console.error('ECharts library not loaded!');
        chartContainer.innerHTML = '<div class="no-data-message">Chart library not loaded</div>';
        return;
    }

    // Initialize chart only once
    if (!energyChart) {
        chartContainer.innerHTML = '';
        energyChart = echarts.init(chartContainer);
        
        // Handle window resize once
        window.addEventListener('resize', function() {
            if (energyChart) {
                energyChart.resize();
            }
        });
    }

    const useTimeOnly = rangeKey && ENERGY_RANGE_CONFIG[rangeKey] ? ENERGY_RANGE_CONFIG[rangeKey].hours <= 24 : false;
    
    // Prepare data for ECharts
    const xAxisData = aggregatedData.map(item => item.label);
    const seriesData = aggregatedData.map(item => item.value);

    // Calculate bar width based on data points
    const dataPointCount = aggregatedData.length;
    let barWidth;
    if (dataPointCount <= 24) {
        barWidth = '60%';
    } else if (dataPointCount <= 48) {
        barWidth = '50%';
    } else if (dataPointCount <= 100) {
        barWidth = '40%';
    } else {
        barWidth = '30%';
    }

    // Configure ECharts option
    const option = {
        backgroundColor: 'transparent',
        grid: {
            left: '3%',
            right: '4%',
            bottom: '5%',
            top: '15%',
            containLabel: true
        },
        tooltip: {
            trigger: 'axis',
            axisPointer: {
                type: 'shadow'
            },
            formatter: function(params) {
                const param = params[0];
                return param.name + '<br/>' +
                       '<strong style="color: #2196f3;">Energy: ' + param.value.toFixed(2) + ' kWh</strong>';
            },
            backgroundColor: 'rgba(255, 255, 255, 0.95)',
            borderColor: '#2196f3',
            borderWidth: 2,
            textStyle: {
                color: '#1e293b',
                fontSize: 13
            },
            padding: 12
        },
        xAxis: {
            type: 'category',
            data: xAxisData,
            axisLine: {
                lineStyle: {
                    color: '#e0e0e0',
                    width: 2
                }
            },
            axisLabel: {
                color: '#64748b',
                fontSize: 11,
                fontWeight: 500,
                rotate: dataPointCount > 48 ? 45 : 0,
                interval: dataPointCount <= 24 ? 'auto' : Math.max(0, Math.floor(dataPointCount / 12) - 1)
            },
            axisTick: {
                show: false
            }
        },
        yAxis: {
            type: 'value',
            name: 'Energy (kWh)',
            nameTextStyle: {
                color: '#64748b',
                fontSize: 12,
                fontWeight: 600,
                padding: [0, 0, 10, 0]
            },
            axisLine: {
                show: false
            },
            axisTick: {
                show: false
            },
            axisLabel: {
                color: '#64748b',
                fontSize: 11,
                formatter: function(value) {
                    return value.toFixed(1);
                }
            },
            splitLine: {
                lineStyle: {
                    color: '#f0f0f0',
                    type: 'dashed'
                }
            }
        },
        series: [{
            name: 'Energy',
            type: 'bar',
            data: seriesData,
            barWidth: barWidth,
            itemStyle: {
                color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                    { offset: 0, color: '#2196f3' },
                    { offset: 1, color: '#1976d2' }
                ]),
                borderRadius: [4, 4, 0, 0]
            },
            emphasis: {
                itemStyle: {
                    color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                        { offset: 0, color: '#1e88e5' },
                        { offset: 1, color: '#1565c0' }
                    ])
                }
            },
            animationDuration: 1000,
            animationEasing: 'cubicOut'
        }],
        dataZoom: dataPointCount > 50 ? [
            {
                type: 'inside',
                start: 0,
                end: 100
            },
            {
                start: 0,
                end: 100,
                height: 20,
                bottom: 5,
                handleIcon: 'path://M10.7,11.9v-1.3H9.3v1.3c-4.9,0.3-8.8,4.4-8.8,9.4c0,5,3.9,9.1,8.8,9.4v1.3h1.3v-1.3c4.9-0.3,8.8-4.4,8.8-9.4C19.5,16.3,15.6,12.2,10.7,11.9z M13.3,24.4H6.7V23h6.6V24.4z M13.3,19.6H6.7v-1.4h6.6V19.6z',
                handleSize: '80%',
                handleStyle: {
                    color: '#2196f3'
                },
                textStyle: {
                    color: '#64748b',
                    fontSize: 10
                },
                borderColor: '#e0e0e0'
            }
        ] : []
    };

    // Set the option
    energyChart.setOption(option, false);
}

// ═══════════════════════════════════════════════════════════════
// Aggregate Data by Range
// ═══════════════════════════════════════════════════════════════
function aggregateDataByRange(series, rangeKey) {
    if (!series || series.length === 0) {
        return [];
    }

    const dataPointCount = series.length;
    
    // For Live and 24 hours: show hourly data (no aggregation)
    if (rangeKey === 'live' || rangeKey === '24hours' || dataPointCount <= 48) {
        return series.map(item => ({
            label: formatAxisTick(item.period, true),
            value: typeof item.energy === 'number' ? item.energy : parseFloat(item.energy) || 0,
            period: item.period
        }));
    }
    
    // For 7 days: aggregate by 3-hour blocks (56 data points)
    if (rangeKey === '7days' && dataPointCount > 48) {
        return aggregateByHours(series, 3);
    }
    
    // For 30 days: aggregate by day (30 data points)
    if (rangeKey === '30days' && dataPointCount > 168) {
        return aggregateByDay(series);
    }
    
    // Default: return as-is with proper labels
    return series.map(item => ({
        label: formatAxisTick(item.period, false),
        value: typeof item.energy === 'number' ? item.energy : parseFloat(item.energy) || 0,
        period: item.period
    }));
}

function aggregateByHours(series, hourBlock) {
    const aggregated = [];
    
    for (let i = 0; i < series.length; i += hourBlock) {
        const chunk = series.slice(i, i + hourBlock);
        const sum = chunk.reduce((acc, item) => {
            const val = typeof item.energy === 'number' ? item.energy : parseFloat(item.energy) || 0;
            return acc + val;
        }, 0);
        
        const firstPeriod = chunk[0].period;
        const date = parsePeriodToDate(firstPeriod);
        const label = date ? (pad2(date.getMonth() + 1) + '-' + pad2(date.getDate()) + ' ' + pad2(date.getHours()) + ':00') : '';
        
        aggregated.push({
            label: label,
            value: sum,
            period: firstPeriod
        });
    }
    
    return aggregated;
}

function aggregateByDay(series) {
    const dayMap = {};
    
    series.forEach(item => {
        const date = parsePeriodToDate(item.period);
        if (!date) return;
        
        const dayKey = date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate());
        const energy = typeof item.energy === 'number' ? item.energy : parseFloat(item.energy) || 0;
        
        if (!dayMap[dayKey]) {
            dayMap[dayKey] = {
                sum: 0,
                count: 0,
                date: date
            };
        }
        
        dayMap[dayKey].sum += energy;
        dayMap[dayKey].count += 1;
    });
    
    const aggregated = Object.keys(dayMap).sort().map(dayKey => {
        const data = dayMap[dayKey];
        const label = pad2(data.date.getMonth() + 1) + '-' + pad2(data.date.getDate());
        
        return {
            label: label,
            value: data.sum, // Total energy for the day
            period: dayKey
        };
    });
    
    return aggregated;
}
// ═══════════════════════════════════════════════════════════════
// Update Alarms
// ═══════════════════════════════════════════════════════════════
function updateAlarms(data) {
    // Update alarm counts
    if (data.activeAlarms) {
        document.getElementById('criticalCount').textContent = data.activeAlarms.critical || 0;
        document.getElementById('warningCount').textContent = data.activeAlarms.warning || 0;
        document.getElementById('infoCount').textContent = data.activeAlarms.info || 0;
    }
    
    // Update alarm list
    const alarmList = document.getElementById('alarmList');
    
    if (!data.alarmDetails || data.alarmDetails.length === 0) {
        alarmList.innerHTML = '<div class="empty-state">No active alarms</div>';
        return;
    }
    
    let alarmsHTML = '';
    data.alarmDetails.forEach(alarm => {
        const severityClass = alarm.severity.toLowerCase();
        const severityIcon = getSeverityIcon(alarm.severity);
        
        alarmsHTML += `
            <div class="alarm-item ${severityClass}">
                <div class="alarm-info">
                    <div class="alarm-time">${alarm.timestamp}</div>
                    <div class="alarm-message">${severityIcon} ${alarm.message}</div>
                    <div class="alarm-location">${alarm.device}</div>
                </div>
            </div>
        `;
    });
    
    alarmList.innerHTML = alarmsHTML;
}

// ═══════════════════════════════════════════════════════════════
// Update PQ Events
// ═══════════════════════════════════════════════════════════════
function updatePQEvents(data) {
    const pqList = document.getElementById('pqList');
    
    if (!data.pqEventDetails || data.pqEventDetails.length === 0) {
        pqList.innerHTML = '<div class="empty-state">No power quality events</div>';
        return;
    }
    
    let pqHTML = '';
    data.pqEventDetails.forEach(event => {
        const iconClass = getPQIconClass(event.type);
        const icon = getPQIcon(event.type);
        // Format event type: "Voltage_Swell" → "Voltage Swell"
        const eventName = event.type.replace(/_/g, ' ');
        // Format lastOccurrence timestamp to HH:mm:ss
        const lastOccurrenceTime = formatLastOccurrenceTime(event.lastOccurrence);
        
        pqHTML += `
            <div class="pq-item">
                <div class="pq-type">
                    <div class="pq-icon ${iconClass}">${icon}</div>
                    <div class="pq-details">
                        <div class="pq-name">${eventName}</div>
                        <div class="pq-time">Last 24 hours</div>
                        <div class="pq-occurrence">${lastOccurrenceTime}</div>
                    </div>
                </div>
                <div class="pq-count">${event.count || 0}</div>
            </div>
        `;
    });
    
    pqList.innerHTML = pqHTML;
}

// ═══════════════════════════════════════════════════════════════
// Helper Functions
// ═══════════════════════════════════════════════════════════════
function getSeverityIcon(severity) {
    const icons = {
        'Critical': '<i class="fa-solid fa-triangle-exclamation" style="color: #d32f2f;"></i>',
        'Warning': '<i class="fa-solid fa-temperature-half" style="color: #f57c00;"></i>',
        'Info': '<i class="fa-solid fa-circle-info" style="color: #1976d2;"></i>'
    };
    return icons[severity] || '<i class="fa-solid fa-circle" style="color: #757575;"></i>';
}

function getPQIconClass(type) {
    if (type && type.toLowerCase().includes('voltage')) return 'voltage';
    if (type && type.toLowerCase().includes('thd')) return 'thd';
    if (type && type.toLowerCase().includes('frequency')) return 'frequency';
    return 'voltage';
}

function getPQIcon(type) {
    if (type && type.toLowerCase().includes('voltage')) return '<i class="fa-solid fa-bolt"></i>';
    if (type && type.toLowerCase().includes('thd')) return '<i class="fa-solid fa-wave-square"></i>';
    if (type && type.toLowerCase().includes('frequency')) return '<i class="fa-solid fa-arrows-rotate"></i>';
    return '<i class="fa-solid fa-bolt"></i>';
}

function formatLastOccurrenceTime(timestamp) {
    if (!timestamp) return '';
    
    try {
        // Parse ISO 8601 format: "2025-12-09T19:24:40.0323517"
        const date = new Date(timestamp);
        
        // Format date as YYYY-MM-DD
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const day = String(date.getDate()).padStart(2, '0');
        const dateStr = `${year}-${month}-${day}`;
        
        // Format time as HH:mm:ss
        const hours = String(date.getHours()).padStart(2, '0');
        const minutes = String(date.getMinutes()).padStart(2, '0');
        const seconds = String(date.getSeconds()).padStart(2, '0');
        const timeStr = `${hours}:${minutes}:${seconds}`;
        
        return `Latest Occurrence: ${dateStr} ${timeStr}`;
    } catch (error) {
        console.warn('Error formatting timestamp:', error);
        return '';
    }
}

// ═══════════════════════════════════════════════════════════════
// Loading Overlay Management
// ═══════════════════════════════════════════════════════════════
function showLoadingOverlay() {
    const overlay = document.getElementById('loadingOverlay');
    if (overlay) {
        overlay.classList.add('show');
    }
}

function hideLoadingOverlay() {
    const overlay = document.getElementById('loadingOverlay');
    if (overlay) {
        overlay.classList.remove('show');
    }
    if (loadingTimeout) {
        clearTimeout(loadingTimeout);
        loadingTimeout = null;
    }
}

// ═══════════════════════════════════════════════════════════════
// Event Handlers
// ═══════════════════════════════════════════════════════════════
function setupEventListeners() {
    const rangeButtons = document.querySelectorAll('.range-btn[data-range]');
    Array.prototype.forEach.call(rangeButtons, button => {
        button.addEventListener('click', () => {
            const rangeKey = button.getAttribute('data-range');
            setEnergyRange(rangeKey);
        });
    });

    console.log('Event listeners setup complete');
}

function showError(message) {
    console.error(message);
    // Could add UI error notification here
}

// ═══════════════════════════════════════════════════════════════
// Start Dashboard on Page Load
// ═══════════════════════════════════════════════════════════════
window.addEventListener('DOMContentLoaded', initDashboard);

// Cleanup on page unload
window.addEventListener('beforeunload', () => {
    if (refreshInterval) {
        clearInterval(refreshInterval);
    }
});