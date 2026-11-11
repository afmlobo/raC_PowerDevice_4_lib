// Power Quality Events Dashboard - Multi-Device Aggregated View
// Fetches pq-events-dashboard-data.json and renders ECharts visualizations

let dashboardData = null;
const charts = {};

// Initialize dashboard
async function initializeDashboard() {
    try {
        showLoading(true);
        
        // Fetch dashboard data
        const response = await fetch('./pq-events-dashboard-data.json');
        if (!response.ok) throw new Error('Failed to load PQ events data');
        
        dashboardData = await response.json();
        
        // Update header info
        updateHeaderInfo();
        
        // Check if data exists
        if (!dashboardData.summary || dashboardData.summary.length === 0) {
            document.getElementById('noData').style.display = 'block';
            document.querySelector('.summary-section').style.display = 'none';
        } else {
            document.querySelector('.summary-section').style.display = 'block';
            document.getElementById('noData').style.display = 'none';
            
            // Populate summary table
            populateSummaryTable(dashboardData.summary);
            
            // Initialize charts
            initializeCharts();
        }
        
        // Populate events table
        populateEventsTable(dashboardData.events);
        
        // Set up periodic refresh (every 5 minutes)
        setInterval(refreshDashboard, 300000);
        
        showLoading(false);
    } catch (error) {
        console.error('Dashboard initialization failed:', error);
        showLoading(false);
    }
}

function updateHeaderInfo() {
    // Update breadcrumb and location title
    const orgName = dashboardData.orgName || 'PQ Events Dashboard';
    const breadcrumb = document.getElementById('breadcrumb');
    const locationTitle = document.getElementById('locationTitle');
    
    // Parse org path for breadcrumb
    if (orgName && orgName.includes('/')) {
        const pathParts = orgName.split('/');
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${pathParts.join(' > ')}</span>`;
        const nodeName = pathParts[pathParts.length - 1];
        locationTitle.innerHTML = `<i class="fa-solid fa-chart-column"></i> ${nodeName} PQ Events Dashboard`;
    } else {
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${orgName}</span>`;
        locationTitle.innerHTML = `<i class="fa-solid fa-chart-column"></i> ${orgName}`;
    }
    
    // Update original header elements
    document.getElementById('timeRange').textContent = dashboardData.timeRange || 'Initializing...';
    document.getElementById('lastUpdate').textContent = `Last Update: ${dashboardData.lastUpdate}`;
    document.getElementById('aggregationText').textContent = `${dashboardData.deviceCount} Devices Aggregated`;
}

function populateSummaryTable(summary) {
    const tbody = document.querySelector('#summaryTable tbody');
    tbody.innerHTML = '';
    
    summary.forEach((item, index) => {
        const trendChartId = `trend-${index}`;
        const row = document.createElement('tr');
        
        // Check if any events of this type have waveforms
        const hasWaveformForType = dashboardData.events.some(e => 
            e.eventType === item.eventType && e.hasWaveform
        );
        const waveformIndicator = hasWaveformForType ? 
            '<span class="waveform-icon" title="Waveforms available for this event type">⚡</span>' : '';
        
        row.innerHTML = `
            <td>${item.eventType} ${waveformIndicator}</td>
            <td>${item.subEvent}</td>
            <td style="font-weight: bold; color: #667eea;">${item.count}</td>
            <td>${new Date(item.latestTime).toLocaleString()}</td>
            <td>${item.avgDuration.toFixed(2)}</td>
            <td><div id="${trendChartId}" class="trend-chart"></div></td>
            <td><button class="action-btn" onclick="filterByEventType('${item.eventType}')">View</button></td>
        `;
        tbody.appendChild(row);
        
        // Initialize trend chart
        initializeTrendChart(trendChartId, item.eventType);
    });
    
    // Update last updated time
    document.getElementById('last-updated').textContent = `Last updated: ${new Date(dashboardData.lastUpdate).toLocaleTimeString()}`;
}

function initializeTrendChart(chartId, eventType) {
    // Filter events for this type
    const typeEvents = dashboardData.events.filter(e => e.eventType === eventType);
    
    // Group by date
    const eventsByDate = {};
    typeEvents.forEach(event => {
        const date = event.timestamp.split(' ')[0];
        eventsByDate[date] = (eventsByDate[date] || 0) + 1;
    });
    
    // Create chart data
    const dates = Object.keys(eventsByDate).sort();
    const data = dates.map(date => [new Date(date).getTime(), eventsByDate[date]]);
    
    const chart = echarts.init(document.getElementById(chartId));
    const option = {
        animation: false,
        grid: { top: 2, right: 5, bottom: 2, left: 5 },
        xAxis: { type: 'time', show: false },
        yAxis: { type: 'value', show: false, min: 0 },
        series: [{
            data: data,
            type: 'line',
            smooth: true,
            symbol: 'circle',
            symbolSize: 3,
            lineStyle: { color: '#667eea', width: 2 },
            itemStyle: { color: '#667eea' },
            areaStyle: {
                color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                    { offset: 0, color: 'rgba(102, 126, 234, 0.3)' },
                    { offset: 1, color: 'rgba(102, 126, 234, 0.1)' }
                ])
            }
        }]
    };
    
    chart.setOption(option);
}

function initializeCharts() {
    // Chart 1: Event Frequency Over Time
    initializeEventFrequencyChart();
    
    // Chart 2: Event Duration Analysis
    initializeEventDurationChart();
    
    // Chart 3: Event Types Distribution
    initializeEventTypesChart();
}

function initializeEventFrequencyChart() {
    const container = document.getElementById('eventFrequencyChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const chartData = dashboardData.eventFrequencyChart || {};
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'cross' }
        },
        grid: { left: '3%', right: '4%', bottom: '15%', containLabel: true },
        xAxis: {
            type: 'category',
            boundaryGap: false,
            data: chartData.timestamps || [],
            axisLabel: { rotate: 45, fontSize: 10 }
        },
        yAxis: {
            type: 'value',
            name: 'Event Count'
        },
        series: [
            {
                name: 'Events',
                type: 'line',
                smooth: true,
                data: chartData.counts || [],
                itemStyle: { color: '#667eea' },
                areaStyle: { opacity: 0.3, color: '#667eea' }
            }
        ],
        dataZoom: [
            {
                type: 'slider',
                show: true,
                xAxisIndex: [0],
                start: 0,
                end: 100,
                height: 20,
                bottom: 5,
                textStyle: { color: '#999' }
            },
            {
                type: 'inside',
                xAxisIndex: [0],
                start: 0,
                end: 100
            }
        ]
    };
    
    chart.setOption(option);
    charts.frequency = chart;
}

function initializeEventDurationChart() {
    const container = document.getElementById('eventDurationChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const chartData = dashboardData.eventDurationChart || {};
    
    const option = {
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'shadow' }
        },
        grid: { left: '3%', right: '4%', bottom: '3%', containLabel: true },
        xAxis: {
            type: 'category',
            data: chartData.eventTypes || [],
            axisLabel: { rotate: 45, fontSize: 10 }
        },
        yAxis: { type: 'value', name: 'Duration (ms)' },
        series: [
            {
                name: 'Avg Duration',
                type: 'bar',
                data: (chartData.durations || []).map(v => v.toFixed(2)),
                itemStyle: { color: '#667eea' }
            }
        ]
    };
    
    chart.setOption(option);
    charts.duration = chart;
}

function initializeEventTypesChart() {
    const container = document.getElementById('eventTypesChart');
    if (!container) return;
    
    const chart = echarts.init(container);
    const chartData = dashboardData.eventTypesChart || {};
    
    const chartDataArray = (chartData.data || []).map(d => ({
        value: d.value,
        name: d.name,
        itemStyle: { color: ['#667eea', '#764ba2', '#f093fb', '#4facfe', '#00f2fe'][
            (chartData.data || []).indexOf(d)
        ]}
    }));
    
    const option = {
        tooltip: {
            trigger: 'item',
            formatter: '{b}: {c} events ({d}%)'
        },
        legend: { 
            bottom: 40, 
            left: 'center',
            padding: [10, 0, 0, 0]
        },
        grid: { bottom: 80 },
        series: [
            {
                type: 'pie',
                radius: ['40%', '60%'],
                center: ['50%', '38%'],
                avoidLabelOverlap: false,
                itemStyle: {
                    borderRadius: 10,
                    borderColor: '#fff',
                    borderWidth: 2
                },
                label: {
                    show: true,
                    formatter: '{b}\n{d}%'
                },
                emphasis: {
                    label: { show: true, fontSize: 16, fontWeight: 'bold' }
                },
                data: chartDataArray
            }
        ]
    };
    
    chart.setOption(option);
    charts.types = chart;
}

function populateEventsTable(events) {
    const tbody = document.querySelector('#eventTable tbody');
    tbody.innerHTML = '';
    
    if (!events || events.length === 0) {
        tbody.innerHTML = '<tr><td colspan="9" style="text-align: center; padding: 20px;">No events found</td></tr>';
        return;
    }
    
    events.forEach(event => {
        const row = document.createElement('tr');
        row.innerHTML = `
            <td>${event.recordId}</td>
            <td>${event.deviceName}</td>
            <td>${event.eventType}</td>
            <td>${event.subEvent}</td>
            <td>${event.timestamp}</td>
            <td>${event.duration}</td>
            <td>${event.tripPoint}</td>
            <td>${event.minOrMax}</td>
            <td>${event.hasWaveform ? '<span class="waveform-message">⚡ Waveform Available</span>' : ''}</td>
        `;
        tbody.appendChild(row);
    });
}

function filterByEventType(eventType) {
    const tbody = document.querySelector('#eventTable tbody');
    tbody.innerHTML = '';
    
    const filtered = dashboardData.events.filter(e => e.eventType === eventType);
    
    filtered.forEach(event => {
        const row = document.createElement('tr');
        row.innerHTML = `
            <td>${event.recordId}</td>
            <td>${event.deviceName}</td>
            <td>${event.eventType}</td>
            <td>${event.subEvent}</td>
            <td>${event.timestamp}</td>
            <td>${event.duration}</td>
            <td>${event.tripPoint}</td>
            <td>${event.minOrMax}</td>
            <td>${event.hasWaveform ? '<span class="waveform-message">⚡ Waveform Available</span>' : ''}</td>
        `;
        tbody.appendChild(row);
    });
    
    // Scroll to events table
    document.querySelector('.events-table-container').scrollIntoView({ behavior: 'smooth' });
}

async function refreshDashboard() {
    try {
        const response = await fetch('./pq-events-dashboard-data.json?' + new Date().getTime());
        if (response.ok) {
            dashboardData = await response.json();
            updateHeaderInfo();
            
            if (dashboardData.summary && dashboardData.summary.length > 0) {
                populateSummaryTable(dashboardData.summary);
                initializeCharts();
            }
            
            populateEventsTable(dashboardData.events);
        }
    } catch (error) {
        console.error('Failed to refresh dashboard:', error);
    }
}

function showLoading(show) {
    const loading = document.getElementById('loading');
    if (loading) {
        loading.style.display = show ? 'block' : 'none';
    }
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', initializeDashboard);
