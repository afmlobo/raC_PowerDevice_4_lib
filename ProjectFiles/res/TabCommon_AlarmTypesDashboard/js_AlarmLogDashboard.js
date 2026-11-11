// Alarm Log Dashboard JavaScript
// Auto-polls AlarmLog_Data.json every 5 seconds and updates charts/table
// Time range filtering handled by Optix NetLogic, not by this frontend

let currentData = null;
let alarmsByTypeChart = null;
let severityDistributionChart = null;
let alarmTrendChart = null;

document.addEventListener('DOMContentLoaded', function() {
    // Initialize charts
    alarmsByTypeChart = echarts.init(document.getElementById('alarmsByTypeChart'));
    severityDistributionChart = echarts.init(document.getElementById('severityDistributionChart'));
    alarmTrendChart = echarts.init(document.getElementById('alarmTrendChart'));

    // Set up window resize listener for responsive charts
    window.addEventListener('resize', function() {
        if (alarmsByTypeChart) alarmsByTypeChart.resize();
        if (severityDistributionChart) severityDistributionChart.resize();
        if (alarmTrendChart) alarmTrendChart.resize();
    });

    // Initial load and then poll every 5 seconds
    updateDashboard();
    setInterval(updateDashboard, 5000);
});

function updateDashboard() {
    // Fetch the aggregated alarm data JSON
    fetch('AlarmLog_Data.json?t=' + new Date().getTime())
        .then(response => response.json())
        .then(data => {
            currentData = data;
            updateHeaderInfo(data);
            updateKPIs(data);
            updateCharts(data);
            updateTable(data);
            updateLastUpdatedTime(data.lastUpdated);
        })
        .catch(error => {
            console.log('Waiting for alarm data...', error);
        });
}

function updateHeaderInfo(data) {
    // Update breadcrumb with organization path
    const orgPath = data.orgNodePath || 'Organization';
    const breadcrumb = document.getElementById('breadcrumb');
    
    if (orgPath && orgPath.includes('/')) {
        const pathParts = orgPath.split('/');
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${pathParts.join(' > ')}</span>`;
    } else {
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${orgPath}</span>`;
    }
    
    // Update time range
    const timeRangeDesc = (data.timeRange && data.timeRange.description) ? data.timeRange.description : 'Current Period';
    document.getElementById('timeRange').textContent = `📅 ${timeRangeDesc}`;
    
    // Update last updated timestamp
    const lastUpdatedElement = document.getElementById('lastUpdated');
    if (data.lastUpdated) {
        lastUpdatedElement.textContent = `Last Update: ${formatTime(data.lastUpdated)}`;
    } else {
        lastUpdatedElement.textContent = `Last Update: ${formatTime(new Date().toISOString())}`;
    }
    
    // Update device count
    let deviceCount = data.deviceCount || data.monitoredDevices || 0;
    
    // If deviceCount is 0, try to count unique devices from recent alarms
    if (deviceCount === 0 && data.recentAlarms && data.recentAlarms.length > 0) {
        const uniqueDevices = new Set(data.recentAlarms.map(alarm => alarm.device));
        deviceCount = uniqueDevices.size;
    }
    
    document.getElementById('deviceCount').textContent = `${deviceCount} Device${deviceCount !== 1 ? 's' : ''} Aggregated`;
}

function updateKPIs(data) {
    // Get time range description
    const timeRangeDesc = (data.timeRange && data.timeRange.description) ? data.timeRange.description : 'Selected Period';

    // Total Alarms KPI
    const totalAlarms = data.totalAlarms || { value: 0, change: '0%', trend: 'neutral', subtext: 'No data' };
    document.getElementById('totalAlarms').textContent = totalAlarms.value || 0;
    document.getElementById('totalAlarmsSubtext').textContent = timeRangeDesc;
    
    const trendClass = totalAlarms.trend === 'up' ? 'trend-up' : totalAlarms.trend === 'down' ? 'trend-down' : 'trend-neutral';
    document.getElementById('totalAlarmsTrend').innerHTML = `<span class="${trendClass}">${totalAlarms.trend === 'up' ? '↑' : '↓'} ${totalAlarms.change}</span>`;

    // Severity breakdown
    const severity = data.alarmsBySeverity || { critical: 0, warning: 0, info: 0, total: 0 };
    document.getElementById('criticalCount').textContent = severity.critical || 0;
    document.getElementById('warningCount').textContent = severity.warning || 0;
    document.getElementById('infoCount').textContent = severity.info || 0;

    // Top alarm device
    const topDevice = data.topAlarmDevice || { device: 'N/A', count: 0 };
    document.getElementById('topDevice').textContent = topDevice.device || 'N/A';
    document.getElementById('topDeviceCount').textContent = (topDevice.count || 0) + ' alarms';
}

function updateCharts(data) {
    // Get time range description from data
    const timeRangeDesc = (data.timeRange && data.timeRange.description) ? data.timeRange.description : 'Selected Period';
    
    // Update chart titles with dynamic time range
    document.getElementById('alarmsByTypeTitle').textContent = `Alarms by Type (${timeRangeDesc})`;
    document.getElementById('severityDistributionTitle').textContent = `Severity Distribution (${timeRangeDesc})`;
    document.getElementById('alarmTrendTitle').textContent = `Alarm Trend (${timeRangeDesc})`;
    
    // Alarms by Type Chart (Bar)
    if (data.alarmsByType && data.alarmsByType.length > 0) {
        const typeNames = data.alarmsByType.map(item => item.type);
        const typeCounts = data.alarmsByType.map(item => item.count);

        alarmsByTypeChart.setOption({
            tooltip: {
                trigger: 'axis',
                axisPointer: { type: 'shadow' }
            },
            xAxis: {
                type: 'value'
            },
            yAxis: {
                type: 'category',
                data: typeNames,
                axisLabel: { fontSize: 11 }
            },
            series: [{
                data: typeCounts,
                type: 'bar',
                itemStyle: { color: '#667eea' }
            }],
            grid: {
                left: '5%',
                right: '3%',
                bottom: '10%',
                top: '10%',
                containLabel: true
            }
        });
    } else {
        alarmsByTypeChart.setOption({
            title: { text: 'No Data', left: 'center', top: 'center' }
        });
    }

    // Severity Distribution Chart (Pie)
    const severity = data.alarmsBySeverity || { critical: 0, warning: 0, info: 0 };
    const severityData = [];
    
    if ((severity.critical || 0) > 0) severityData.push({ value: severity.critical, name: 'Critical', itemStyle: { color: '#d63031' } });
    if ((severity.warning || 0) > 0) severityData.push({ value: severity.warning, name: 'Warning', itemStyle: { color: '#f97316' } });
    if ((severity.info || 0) > 0) severityData.push({ value: severity.info, name: 'Info', itemStyle: { color: '#0984e3' } });

    if (severityData.length > 0) {
        severityDistributionChart.setOption({
            tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
            series: [{
                type: 'pie',
                radius: ['30%', '70%'],
                data: severityData,
                emphasis: { itemStyle: { shadowBlur: 10, shadowOffsetX: 0, shadowColor: 'rgba(0, 0, 0, 0.5)' } }
            }]
        });
    } else {
        severityDistributionChart.setOption({
            title: { text: 'No Data', left: 'center', top: 'center' }
        });
    }

    // Alarm Trend Chart (Line)
    if (data.alarmTrend && data.alarmTrend.data && data.alarmTrend.data.length > 0) {
        const dates = data.alarmTrend.data.map(item => item.date);
        const counts = data.alarmTrend.data.map(item => item.count);

        alarmTrendChart.setOption({
            tooltip: { trigger: 'axis' },
            xAxis: {
                type: 'category',
                data: dates,
                axisLabel: { fontSize: 11 }
            },
            yAxis: {
                type: 'value'
            },
            series: [{
                data: counts,
                type: 'line',
                smooth: true,
                itemStyle: { color: '#667eea' },
                areaStyle: { color: 'rgba(102, 126, 234, 0.2)' }
            }],
            grid: {
                left: '3%',
                right: '3%',
                bottom: '10%',
                top: '10%',
                containLabel: true
            }
        });
    } else {
        alarmTrendChart.setOption({
            title: { text: 'No Data', left: 'center', top: 'center' }
        });
    }
}

function updateTable(data) {
    const container = document.getElementById('alarmTableContainer');
    
    if (!data.recentAlarms || data.recentAlarms.length === 0) {
        container.innerHTML = '<div class="no-data">No alarm data available</div>';
        return;
    }

    let tableHTML = `
        <table class="alarm-table">
            <thead>
                <tr>
                    <th>Timestamp</th>
                    <th>Device</th>
                    <th>Type</th>
                    <th>Code</th>
                    <th>Message</th>
                    <th>Severity</th>
                </tr>
            </thead>
            <tbody>
    `;

    // Limit to 100 rows for performance
    const displayAlarms = data.recentAlarms.slice(0, 100);
    
    displayAlarms.forEach(alarm => {
        const severity = (alarm.severity || 'Info').toLowerCase();
        const severityClass = `severity-${severity}`;
        const badge = `<span class="severity-badge ${severity}">${alarm.severity || 'Info'}</span>`;
        
        tableHTML += `
            <tr>
                <td>${formatTime(alarm.timestamp)}</td>
                <td>${alarm.device}</td>
                <td>${alarm.type}</td>
                <td>${alarm.code}</td>
                <td>${alarm.message}</td>
                <td>${badge}</td>
            </tr>
        `;
    });

    tableHTML += `
            </tbody>
        </table>
    `;

    container.innerHTML = tableHTML;
}

function formatTime(timestamp) {
    try {
        if (!timestamp) return '';

        // Handle DD-MM-YYYY HH:MM:SS format from AlarmLog_Data.json
        const ddmmyyyyMatch = String(timestamp).match(/^(\d{2})-(\d{2})-(\d{4})[ T](\d{2}):(\d{2}):(\d{2})$/);
        let date;

        if (ddmmyyyyMatch) {
            const [, dd, mm, yyyy, hh, min, ss] = ddmmyyyyMatch;
            date = new Date(Number(yyyy), Number(mm) - 1, Number(dd), Number(hh), Number(min), Number(ss));
        } else {
            date = new Date(timestamp);
        }

        if (isNaN(date.getTime())) {
            return String(timestamp);
        }

        return date.toLocaleString('en-US', {
            month: '2-digit',
            day: '2-digit',
            year: '2-digit',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });
    } catch (e) {
        return String(timestamp);
    }
}

function updateLastUpdatedTime(lastUpdated) {
    const element = document.getElementById('lastUpdated');
    if (element) {
        element.textContent = 'Last updated: ' + (lastUpdated || new Date().toLocaleTimeString());
    }
}
