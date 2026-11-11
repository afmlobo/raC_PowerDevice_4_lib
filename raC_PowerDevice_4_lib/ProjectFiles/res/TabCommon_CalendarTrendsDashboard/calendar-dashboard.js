// Global variables
let calendarData = null;
let selectedDay = null;
let viewMode = 'sparkline';
let dailyCharts = {};
let detailChart = null;
let selectedDeviceIndex = 0; // Track selected device

// Initialize the dashboard when DOM is ready
document.addEventListener('DOMContentLoaded', function() {
    createStatusDisplay();
    loadData();
    setupEventListeners();
});

// Create status display element if it doesn't exist
function createStatusDisplay() {
    // Only create once
    if (document.getElementById('statusDisplay')) return;
    
    const statusDiv = document.createElement('div');
    statusDiv.id = 'statusDisplay';
    statusDiv.style.cssText = `
        padding: 8px 12px;
        border-radius: 6px;
        font-size: 12px;
        display: none;
        align-items: center;
        gap: 6px;
        white-space: nowrap;
        margin-left: auto;
    `;
    
    // Append to body first as fallback, will be moved to device-selector-card when shown
    document.body.appendChild(statusDiv);
}

// Global variable to track status timeout
let statusTimeout = null;

// Show status message on page
function showStatus(message, type = 'info') {
    const statusDiv = document.getElementById('statusDisplay');
    if (!statusDiv) return;
    
    // Clear previous timeout if any
    if (statusTimeout) {
        clearTimeout(statusTimeout);
        statusTimeout = null;
    }
    
    // Move status div to device-selector-card if it exists
    const deviceCard = document.querySelector('.device-selector-card');
    if (deviceCard && statusDiv.parentElement !== deviceCard) {
        deviceCard.appendChild(statusDiv);
    }
    
    statusDiv.innerHTML = message;
    statusDiv.style.display = 'flex';
    
    // Set colors based on type
    switch(type) {
        case 'error':
            statusDiv.style.background = '#fee';
            statusDiv.style.color = '#c33';
            statusDiv.style.borderLeft = '4px solid #c33';
            break;
        case 'warning':
            statusDiv.style.background = '#ffe';
            statusDiv.style.color = '#993';
            statusDiv.style.borderLeft = '4px solid #993';
            break;
        case 'success':
            statusDiv.style.background = '#efe';
            statusDiv.style.color = '#3c3';
            statusDiv.style.borderLeft = '4px solid #3c3';
            break;
        default:
            statusDiv.style.background = '#eef';
            statusDiv.style.color = '#33c';
            statusDiv.style.borderLeft = '4px solid #33c';
    }
}

// Setup event listeners
function setupEventListeners() {
    document.getElementById('trendViewBtn').addEventListener('click', function() {
        setViewMode('sparkline');
    });

    document.getElementById('valuesViewBtn').addEventListener('click', function() {
        setViewMode('values');
    });

    document.getElementById('closeDetailsBtn').addEventListener('click', function() {
        closeDetails();
    });
}

// Load data from JSON file
function loadData() {
    showStatus('<i class="fa-solid fa-hourglass-half"></i> Loading calendar data...', 'info');
    
    fetch('CalendarTrends_Data.json')
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            return response.json();
        })
        .then(data => {
            calendarData = data;
            
            // Check if devices exist
            if (!calendarData.devices || calendarData.devices.length === 0) {
                // No devices, but still render with metadata
                renderDashboardWithNoDevices();
                showStatus('<i class="fa-solid fa-triangle-exclamation"></i> No devices found in data', 'warning');
            } else {
                processData();
                renderDashboard();
                showStatus('<i class="fa-solid fa-circle-check"></i> Calendar data loaded successfully', 'success');
            }
        })
        .catch(error => {
            console.error('Error loading data:', error);
            showStatus('<i class="fa-solid fa-triangle-exclamation"></i> Warning: ' + error.message, 'warning');
            // Still render dashboard with empty state
            renderEmptyDashboard();
        });
}

// Process the data and calculate statistics from time-series data
function processData() {
    if (!calendarData) {
        throw new Error('No calendar data loaded');
    }

    if (!calendarData.devices || calendarData.devices.length === 0) {
        // Return early with empty state - this should be handled by caller
        return;
    }

    // Ensure selected device index is valid
    if (selectedDeviceIndex >= calendarData.devices.length) {
        selectedDeviceIndex = 0;
    }

    // Extract device info from selected device
    const selectedDevice = calendarData.devices[selectedDeviceIndex];
    if (!selectedDevice.dailyData || Object.keys(selectedDevice.dailyData).length === 0) {
        // No data for this device - return early with empty state
        return {
            hasData: false,
            deviceName: selectedDevice.deviceName || 'Device_Unknown',
            tagName: calendarData.selectedTag || 'Data',
            dailyStats: {},
            overallStats: { minAvg: 'N/A', maxAvg: 'N/A', overallAvg: 'N/A', totalDays: 0 }
        };
    }

    // Use selectedTag if available, otherwise use tagName from device
    calendarData.tagName = calendarData.selectedTag || selectedDevice.tagName || 'Data';
    calendarData.deviceName = selectedDevice.deviceName || 'Device_01';

    // Convert devices array structure to dailyStats for compatibility
    calendarData.dailyStats = {};

    // Process each day from the selected device
    Object.keys(selectedDevice.dailyData).forEach(day => {
        const timeSeriesData = selectedDevice.dailyData[day];

        if (!Array.isArray(timeSeriesData) || timeSeriesData.length === 0) {
            return; // Skip empty days
        }

        // Extract values from time-series data
        const values = timeSeriesData.map(point => {
            // Handle different possible data structures
            return point.value !== undefined ? point.value : parseFloat(point);
        }).filter(v => !isNaN(v));

        if (values.length === 0) {
            return; // Skip if no valid values
        }

        // Calculate statistics
        const min = Math.min(...values);
        const max = Math.max(...values);
        const avg = values.reduce((a, b) => a + b, 0) / values.length;

        calendarData.dailyStats[day] = {
            min: min,
            max: max,
            avg: avg,
            readings: values.length,
            trend: values // Use actual values for sparkline
        };
    });

    // Validate we have data
    if (Object.keys(calendarData.dailyStats).length === 0) {
        throw new Error('No valid daily data found after processing');
    }

    // Calculate overall statistics
    const allDays = Object.values(calendarData.dailyStats);
    const allAvgs = allDays.map(d => d.avg).filter(v => !isNaN(v));

    calendarData.overallStats = {
        minAvg: Math.min(...allAvgs).toFixed(3),
        maxAvg: Math.max(...allAvgs).toFixed(3),
        overallAvg: (allAvgs.reduce((a, b) => a + b, 0) / allAvgs.length).toFixed(3),
        totalDays: allDays.length
    };
}

// Render the entire dashboard
function renderDashboard() {
    if (!calendarData) return;

    // Update dashboard header with new structure
    updateHeaderInfo(calendarData);

    // Update header info
    document.getElementById('deviceName').textContent = calendarData.deviceName || 'N/A';
    document.getElementById('tagName').textContent = calendarData.tagName || 'N/A';
    document.getElementById('overallAvg').textContent = calendarData.overallStats.overallAvg;
    document.getElementById('totalDays').textContent = calendarData.overallStats.totalDays + ' days';

    // Update location
    const location = extractLocation(calendarData.selectedNode);
    document.getElementById('locationText').textContent = 'Location: ' + location;

    // Update month/year in header and calendar title
    updateMonthYear();

    // Render device selector if multiple devices
    renderDeviceSelector();

    // Render calendar
    renderCalendar();
}

// Update month and year dynamically
function updateMonthYear() {
    const monthNames = ["January", "February", "March", "April", "May", "June",
                       "July", "August", "September", "October", "November", "December"];
    
    const monthName = monthNames[calendarData.selectedMonth - 1] || monthNames[11];
    const year = calendarData.selectedYear;
    const monthYear = `${monthName} ${year}`;
    
    // Update calendar title
    const calendarTitle = document.querySelector('.calendar-card h2');
    if (calendarTitle) {
        calendarTitle.textContent = monthYear;
    }
}

// Render device selector buttons
function renderDeviceSelector() {
    const deviceSelectorContainer = document.getElementById('deviceSelector');
    if (!deviceSelectorContainer) return;

    // Only show if multiple devices
    if (!calendarData.devices || calendarData.devices.length <= 1) {
        deviceSelectorContainer.style.display = 'none';
        return;
    }

    deviceSelectorContainer.style.display = 'flex';
    deviceSelectorContainer.innerHTML = '';

    // Create device buttons
    calendarData.devices.forEach((device, index) => {
        const button = document.createElement('button');
        button.className = 'device-btn';
        button.textContent = device.deviceName;
        button.dataset.deviceIndex = index;

        if (index === selectedDeviceIndex) {
            button.classList.add('active');
        }

        button.addEventListener('click', () => switchDevice(index));
        deviceSelectorContainer.appendChild(button);
    });
}

// Update dashboard header with breadcrumb, period, parameter, and device count
function updateHeaderInfo(data) {
    // Update breadcrumb with organization path
    const nodePath = data.selectedNode || 'Organization';
    const breadcrumb = document.getElementById('breadcrumb');
    
    if (nodePath && nodePath.includes('/')) {
        const pathParts = nodePath.split('/');
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${pathParts.join(' > ')}</span>`;
    } else {
        breadcrumb.innerHTML = `<span class="breadcrumb-item"><i class="fa-solid fa-building"></i> ${nodePath}</span>`;
    }
    
    // Update period (month and year)
    const monthNames = ['January', 'February', 'March', 'April', 'May', 'June',
                        'July', 'August', 'September', 'October', 'November', 'December'];
    const month = data.selectedMonth || new Date().getMonth() + 1;
    const year = data.selectedYear || new Date().getFullYear();
    const monthName = monthNames[month - 1];
    document.getElementById('period').innerHTML = `<i class="fa-solid fa-calendar-days"></i> Period: ${monthName} ${year}`;
    
    // Update parameter (tag name)
    const tagName = data.selectedTag || 'Parameter';
    document.getElementById('parameter').innerHTML = `<i class="fa-solid fa-chart-column"></i> Parameter: ${tagName}`;
    
    // Update device count
    const deviceCount = data.devices ? data.devices.length : 0;
    document.getElementById('deviceCount').textContent = `${deviceCount} Device${deviceCount !== 1 ? 's' : ''}`;
}

// Extract location from node path
function extractLocation(nodePath) {
    if (!nodePath) return 'Unknown';
    const parts = nodePath.split('/');
    return parts.slice(-2).join(', ');
}

// Render the calendar grid
function renderCalendar() {
    const container = document.getElementById('calendarDays');
    container.innerHTML = '';

    const calendar = generateCalendar();

    calendar.forEach(week => {
        week.forEach(day => {
            const dayDiv = document.createElement('div');
            dayDiv.className = 'calendar-day';

            if (day === null) {
                dayDiv.classList.add('invisible');
            } else {
                const stats = calendarData.dailyStats[day];

                if (!stats) {
                    dayDiv.classList.add('no-data');
                    dayDiv.innerHTML = `
                        <div class="day-number">${day}</div>
                        <div class="day-content">
                            <span class="no-data-text">No data</span>
                        </div>
                    `;
                } else {
                    if (selectedDay === day) {
                        dayDiv.classList.add('selected');
                    }

                    const dayId = 'day-' + day;

                    if (viewMode === 'sparkline') {
                        dayDiv.innerHTML = `
                            <div class="day-number">${day}</div>
                            <div class="day-content">
                                <div class="sparkline-container" id="${dayId}"></div>
                                <div class="day-avg">${stats.avg.toFixed(1)}</div>
                            </div>
                        `;
                    } else {
                        dayDiv.innerHTML = `
                            <div class="day-number">${day}</div>
                            <div class="day-content">
                                <div class="day-value">${stats.avg.toFixed(1)}</div>
                                <div class="day-range">${stats.min.toFixed(1)}-${stats.max.toFixed(1)}</div>
                            </div>
                        `;
                    }

                    dayDiv.addEventListener('click', function() {
                        selectDay(day);
                    });
                }
            }

            container.appendChild(dayDiv);
        });
    });

    // Render sparklines after DOM is updated
    if (viewMode === 'sparkline') {
        setTimeout(() => {
            Object.keys(calendarData.dailyStats).forEach(day => {
                renderSparkline('day-' + day, calendarData.dailyStats[day].trend);
            });
        }, 10);
    }
}

// Generate calendar structure
function generateCalendar() {
    const year = calendarData.selectedYear || 2025;
    // selectedMonth should be 1-12, convert to 0-11 for JavaScript Date
    // Use ?? instead of || to handle 0 values correctly
    const selectedMonth = calendarData.selectedMonth ?? 1;
    const month = selectedMonth - 1;

    // Use local date construction to avoid timezone issues
    const firstDayDate = new Date(year, month, 1);
    const firstDay = firstDayDate.getDay();
    const daysInMonth = new Date(year, month + 1, 0).getDate();

    const weeks = [];
    let currentWeek = new Array(7).fill(null);

    // Fill first week
    for (let i = firstDay; i < 7 && (i - firstDay + 1) <= daysInMonth; i++) {
        currentWeek[i] = i - firstDay + 1;
    }
    weeks.push(currentWeek);

    // Fill remaining weeks
    let day = 8 - firstDay;
    while (day <= daysInMonth) {
        currentWeek = new Array(7).fill(null);
        for (let i = 0; i < 7 && day <= daysInMonth; i++) {
            currentWeek[i] = day++;
        }
        weeks.push(currentWeek);
    }

    return weeks;
}

// Render sparkline using ECharts
function renderSparkline(elementId, data) {
    const container = document.getElementById(elementId);
    if (!container || !data || data.length === 0) return;

    // Dispose existing chart if any
    if (dailyCharts[elementId]) {
        dailyCharts[elementId].dispose();
    }

    const chart = echarts.init(container);
    dailyCharts[elementId] = chart;

    const option = {
        grid: {
            left: 0,
            right: 0,
            top: 0,
            bottom: 0
        },
        xAxis: {
            type: 'category',
            show: false,
            data: data.map((_, i) => i)
        },
        yAxis: {
            type: 'value',
            show: false,
            scale: true
        },
        series: [{
            data: data,
            type: 'line',
            smooth: true,
            symbol: 'none',
            lineStyle: {
                color: '#2563eb',
                width: 1.5
            },
            areaStyle: {
                color: {
                    type: 'linear',
                    x: 0,
                    y: 0,
                    x2: 0,
                    y2: 1,
                    colorStops: [{
                        offset: 0,
                        color: 'rgba(37, 99, 235, 0.3)'
                    }, {
                        offset: 1,
                        color: 'rgba(37, 99, 235, 0.05)'
                    }]
                }
            }
        }]
    };

    chart.setOption(option);
}

// Set view mode
function setViewMode(mode) {
    viewMode = mode;

    // Update button states
    document.getElementById('trendViewBtn').classList.toggle('active', mode === 'sparkline');
    document.getElementById('valuesViewBtn').classList.toggle('active', mode === 'values');

    // Update info banner visibility
    document.getElementById('infoBanner').style.display = mode === 'sparkline' ? 'flex' : 'none';

    // Re-render calendar
    renderCalendar();
}

// Select a day and show details
function selectDay(day) {
    selectedDay = day;
    renderCalendar();
    showDetails(day);
}

// Show day details
function showDetails(day) {
    const stats = calendarData.dailyStats[day];
    if (!stats) return;

    const detailsCard = document.getElementById('detailsCard');
    detailsCard.classList.remove('hidden');

    // Update title
    const monthName = new Date(calendarData.selectedYear, calendarData.selectedMonth - 1, 1)
        .toLocaleDateString('en-US', { month: 'long' });
    document.getElementById('detailsTitle').textContent = 
        `${monthName} ${day}, ${calendarData.selectedYear} - Detailed Statistics`;

    // Update stats
    document.getElementById('detailMin').textContent = stats.min.toFixed(3);
    document.getElementById('detailMax').textContent = stats.max.toFixed(3);
    document.getElementById('detailAvg').textContent = stats.avg.toFixed(3);
    document.getElementById('detailVar').textContent = (stats.max - stats.min).toFixed(3);

    // Update info
    const readingsText = `Total Readings: ${stats.readings} (96 readings = 24 hours × 4 samples/hour)`;
    const partialText = stats.readings < 96 ? ' (Partial day)' : '';
    document.getElementById('detailReadings').textContent = readingsText + partialText;
    document.getElementById('detailDataPoints').textContent = `Data Points in Chart: ${stats.trend.length}`;

    // Render detail chart
    renderDetailChart(stats.trend);

    // Scroll to details
    detailsCard.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

// Render detail chart
function renderDetailChart(data) {
    const container = document.getElementById('detailChart');
    if (!container) return;

    // Dispose existing chart
    if (detailChart) {
        detailChart.dispose();
    }

    detailChart = echarts.init(container);

    // Generate time labels (0:00, 6:00, 12:00, 18:00, 24:00)
    const timeLabels = [];
    for (let i = 0; i < data.length; i++) {
        const hour = Math.floor(i / 4);
        const minute = (i % 4) * 15;
        timeLabels.push(`${hour.toString().padStart(2, '0')}:${minute.toString().padStart(2, '0')}`);
    }

    const option = {
        grid: {
            left: 50,
            right: 30,
            top: 30,
            bottom: 50
        },
        tooltip: {
            trigger: 'axis',
            formatter: function(params) {
                const time = timeLabels[params[0].dataIndex];
                const value = params[0].value.toFixed(3);
                return `${time}<br/>Value: ${value}`;
            }
        },
        xAxis: {
            type: 'category',
            boundaryGap: false,
            data: timeLabels,
            axisLabel: {
                formatter: function(value, index) {
                    // Show only every 24th label (every 6 hours)
                    if (index % 24 === 0) {
                        return value.split(':')[0] + ':00';
                    }
                    return '';
                }
            }
        },
        yAxis: {
            type: 'value',
            scale: true,
            axisLabel: {
                formatter: '{value}'
            }
        },
        series: [{
            data: data,
            type: 'line',
            smooth: true,
            symbol: 'none',
            lineStyle: {
                color: '#2563eb',
                width: 2
            },
            areaStyle: {
                color: {
                    type: 'linear',
                    x: 0,
                    y: 0,
                    x2: 0,
                    y2: 1,
                    colorStops: [{
                        offset: 0,
                        color: 'rgba(37, 99, 235, 0.4)'
                    }, {
                        offset: 1,
                        color: 'rgba(37, 99, 235, 0.05)'
                    }]
                }
            }
        }]
    };

    detailChart.setOption(option);
}

// Close details panel
function closeDetails() {
    selectedDay = null;
    document.getElementById('detailsCard').classList.add('hidden');
    renderCalendar();
}

// Switch to different device
function switchDevice(deviceIndex) {
    if (deviceIndex === selectedDeviceIndex) return;
    
    selectedDeviceIndex = deviceIndex;
    selectedDay = null;
    
    try {
        const selectedDevice = calendarData.devices[deviceIndex];
        
        // Check if device has any data
        if (!selectedDevice.dailyData || Object.keys(selectedDevice.dailyData).length === 0) {
            // Device has no data - show no-data state
            showNoDataForDevice(selectedDevice.deviceName);
            return;
        }
        
        processData();
        renderDashboard();
        showStatus(`<i class="fa-solid fa-circle-check"></i> Switched to ${calendarData.deviceName}`, 'success');
    } catch (error) {
        // If processData fails, show no-data state
        showNoDataForDevice(calendarData.devices[deviceIndex].deviceName);
        showStatus(`<i class="fa-solid fa-triangle-exclamation"></i> No data available for ${calendarData.devices[deviceIndex].deviceName}`, 'warning');
    }
}

// Show no-data state for a device
function showNoDataForDevice(deviceName) {
    // CLEAR the old data completely to prevent showing stale data
    calendarData.dailyStats = {};
    calendarData.overallStats = { minAvg: 'N/A', maxAvg: 'N/A', overallAvg: 'N/A', totalDays: 0 };
    
    // Update header with device name but N/A values
    document.getElementById('deviceName').textContent = deviceName || 'Unknown Device';
    document.getElementById('tagName').textContent = 'N/A';
    document.getElementById('overallAvg').textContent = 'N/A';
    document.getElementById('totalDays').textContent = '0 days';
    
    // Show status message
    showStatus(`<i class="fa-solid fa-triangle-exclamation"></i> No data available for ${deviceName}`, 'warning');
    
    // Clear calendar
    const container = document.getElementById('calendarDays');
    if (container) {
        container.innerHTML = '';
        
        // Generate empty calendar grid with all no-data
        const calendar = generateCalendar();
        calendar.forEach(week => {
            week.forEach(day => {
                const dayDiv = document.createElement('div');
                dayDiv.className = 'calendar-day';

                if (day === null) {
                    dayDiv.classList.add('invisible');
                } else {
                    dayDiv.classList.add('no-data');
                    dayDiv.innerHTML = `
                        <div class="day-number">${day}</div>
                        <div class="day-content">
                            <span class="no-data-text">No data</span>
                        </div>
                    `;
                }
                container.appendChild(dayDiv);
            });
        });
    }
    
    // Close details if open
    closeDetails();
    
    // Update device selector to show active button
    const deviceBtns = document.querySelectorAll('.device-btn');
    deviceBtns.forEach((btn, idx) => {
        if (idx === selectedDeviceIndex) {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }
    });
}

// Render dashboard when no devices exist but metadata is available
function renderDashboardWithNoDevices() {
    // Update header with available metadata
    updateHeaderInfo(calendarData);
    
    // Set N/A for device-specific values
    document.getElementById('deviceName').textContent = 'No Devices';
    document.getElementById('tagName').textContent = calendarData.selectedTag || 'N/A';
    document.getElementById('overallAvg').textContent = 'N/A';
    document.getElementById('totalDays').textContent = '0 days';
    
    // Update location
    const location = extractLocation(calendarData.selectedNode);
    document.getElementById('locationText').textContent = 'Location: ' + location;
    
    // Update month/year
    updateMonthYear();
    
    // Hide device selector
    const deviceSelectorContainer = document.getElementById('deviceSelector');
    if (deviceSelectorContainer) {
        deviceSelectorContainer.parentElement.style.display = 'none';
    }
    
    // Render empty calendar
    renderEmptyCalendar();
}

// Render empty dashboard when data fails to load
function renderEmptyDashboard() {
    // Set placeholder values
    document.getElementById('deviceName').textContent = 'N/A';
    document.getElementById('tagName').textContent = 'N/A';
    document.getElementById('overallAvg').textContent = 'N/A';
    document.getElementById('totalDays').textContent = '0 days';
    document.getElementById('locationText').textContent = 'Location: Unknown';
    
    // Clear calendar
    const container = document.getElementById('calendarDays');
    if (container) {
        container.innerHTML = '<div style="padding: 20px; text-align: center; color: #999;">No data available. Please load data from dashboard controls.</div>';
    }
}

// Render empty calendar grid when no device data exists
function renderEmptyCalendar() {
    const container = document.getElementById('calendarDays');
    if (!container) return;
    
    container.innerHTML = '';
    
    const calendar = generateCalendar();
    calendar.forEach(week => {
        week.forEach(day => {
            const dayDiv = document.createElement('div');
            dayDiv.className = 'calendar-day';

            if (day === null) {
                dayDiv.classList.add('invisible');
            } else {
                dayDiv.classList.add('no-data');
                dayDiv.innerHTML = `
                    <div class="day-number">${day}</div>
                    <div class="day-content">
                        <span class="no-data-text">No data</span>
                    </div>
                `;
            }
            container.appendChild(dayDiv);
        });
    });
}

// Handle window resize
window.addEventListener('resize', function() {
    // Resize all charts
    Object.values(dailyCharts).forEach(chart => {
        if (chart) chart.resize();
    });
    if (detailChart) {
        detailChart.resize();
    }
});