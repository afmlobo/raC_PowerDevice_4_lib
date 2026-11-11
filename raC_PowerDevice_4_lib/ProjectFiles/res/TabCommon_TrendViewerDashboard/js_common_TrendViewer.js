/**
 * Multi-Device Trend Viewer Dashboard
 * Displays multi-axis line chart data for multiple devices and tags
 * Each tag gets its own Y-axis for independent scaling
 */

const TrendViewer = (function () {
    // Configuration
    const CONFIG = {
        dataFile: 'MultiDeviceTagTrend_Data.json',
        updateInterval: 10000, // 10 seconds
        colorPalette: [
            '#5470c6', '#91cc75', '#fac858', '#ee6666', '#73c0de', 
            '#3ba272', '#fc8452', '#9a60b4', '#ea7ccc', '#5fb878',
            '#e87c25', '#b1a595', '#ff9e64', '#24a795', '#80d5fa'
        ]
    };

    // State
    let state = {
        data: null,
        charts: {},
        colorMap: {},
        lastUpdate: new Date(),
        visibleSeries: {} // Track which series are visible
    };

    // Initialize
    function init() {
        console.log('Initializing Trend Viewer...');
        setupEventListeners();
        loadData();
    }

    // Setup Event Listeners
    function setupEventListeners() {
        // No chart type toggles needed for multi-axis line chart
    }

    // Load Data from JSON File
    function loadData() {
        showLoading(true);

        fetch(CONFIG.dataFile)
            .then(response => {
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                return response.json();
            })
            .then(data => {
                console.log('Data loaded:', data);
                
                // Check if it's an info message (no data available)
                if (data.type === 'info') {
                    handleInfoMessage(data);
                    return;
                }

                state.data = data;
                state.lastUpdate = new Date();
                renderDashboard();
                showLoading(false);
            })
            .catch(error => {
                console.warn('Could not load trend data file:', error.message);
                // File likely doesn't exist yet — show friendly info instead of error
                handleInfoMessage({
                    type: 'info',
                    title: 'No Devices or Tags Selected',
                    message: 'Select one or more devices and tags, then click Load to display trend data.'
                });
            });
    }

    // Handle Info Messages (no data)
    function handleInfoMessage(infoData) {
        console.log('Info message:', infoData);
        
        const messageDiv = document.querySelector('.no-data-message') || document.createElement('div');
        messageDiv.className = 'no-data-message';
        messageDiv.innerHTML = `
            <div style="text-align: center; padding: 40px;">
                <i class="fa-solid fa-circle-info" style="font-size: 48px; color: #667eea; margin-bottom: 16px;"></i>
                <h3>${infoData.title}</h3>
                <p style="margin-top: 8px;">${infoData.message}</p>
            </div>
        `;

        const chartContainer = document.getElementById('mainTrendChart');
        if (chartContainer) {
            chartContainer.innerHTML = '';
            chartContainer.appendChild(messageDiv);
        }

        updateTimeInfo(new Date(), 'No data loaded', '');
        updateSummary(0, 0, 0, 0, 0);
        showLoading(false);
    }

    // Render Dashboard
    function renderDashboard() {
        try {
            // Update header info
            updateHeaderInfo();

            // Generate color map for series
            generateColorMap();

            // Update summary cards
            const seriesWithDataCount = state.data.seriesInfo ? 
                state.data.seriesInfo.filter(s => s.hasData !== false).length : 
                Object.keys(groupSeriesByKey()).length;
            
            updateSummary(
                state.data.devices.length,
                state.data.tags.length,
                state.data.count,
                seriesWithDataCount,
                state.data.seriesInfo ? state.data.seriesInfo.length : seriesWithDataCount
            );

            // Render charts
            updateMainChart();
            updateDeviceContributionChart();
            updateTagDistributionChart();

            // Update time info
            const startDate = new Date(state.data.start);
            const endDate = new Date(state.data.end);
            const timeRange = `${formatDate(startDate)} to ${formatDate(endDate)}`;
            updateTimeInfo(state.lastUpdate, timeRange, `${state.data.devices.length} device(s), ${state.data.tags.length} tag(s)`);
        } catch (error) {
            console.error('Error rendering dashboard:', error);
            handleError(`Failed to render dashboard: ${error.message}`);
        }
    }

    // Update Header Information
    function updateHeaderInfo() {
        const breadcrumb = document.getElementById('breadcrumb');
        if (breadcrumb) {
            breadcrumb.innerHTML = `
                <span class="breadcrumb-item"><i class="fa-solid fa-chart-line"></i> Trend Analysis</span>
                <span class="breadcrumb-item" style="opacity: 0.7;">→</span>
                <span class="breadcrumb-item">${state.data.devices.length} Device(s), ${state.data.tags.length} Tag(s)</span>
            `;
        }
    }

    // Generate Color Map for Series
    function generateColorMap() {
        state.colorMap = {};
        state.visibleSeries = {};

        // Use seriesInfo if available (preferred), otherwise fall back to series data
        if (state.data.seriesInfo && state.data.seriesInfo.length > 0) {
            state.data.seriesInfo.forEach((info, index) => {
                state.colorMap[info.seriesKey] = CONFIG.colorPalette[index % CONFIG.colorPalette.length];
                state.visibleSeries[info.seriesKey] = true; // All series visible by default
            });
        } else if (state.data.series && state.data.series.length > 0) {
            const uniqueSeries = new Set(state.data.series.map(s => s.seriesKey));
            let index = 0;
            uniqueSeries.forEach(seriesKey => {
                state.colorMap[seriesKey] = CONFIG.colorPalette[index % CONFIG.colorPalette.length];
                state.visibleSeries[seriesKey] = true;
                index++;
            });
        }
    }

    // Group Series by Key
    function groupSeriesByKey() {
        const grouped = {};
        
        if (state.data.series) {
            state.data.series.forEach(point => {
                if (!grouped[point.seriesKey]) {
                    grouped[point.seriesKey] = [];
                }
                grouped[point.seriesKey].push(point);
            });
        }

        return grouped;
    }

    // Update Main Trend Chart (Multi-Axis Line Chart)
    function updateMainChart() {
        const container = document.getElementById('mainTrendChart');
        if (!container || !state.data) {
            return;
        }

        // Check if we have any series info (even without data)
        const hasSeriesInfo = state.data.seriesInfo && state.data.seriesInfo.length > 0;
        const hasSeriesData = state.data.series && state.data.series.length > 0;

        if (!hasSeriesInfo && !hasSeriesData) {
            container.innerHTML = '<div class="no-data-message">No trend data available. Select devices and tags to display data.</div>';
            renderLegend(); // Still render legend to show selected items
            return;
        }

        // Dispose old chart
        if (state.charts.mainChart) {
            state.charts.mainChart.dispose();
        }

        const chart = echarts.init(container);
        state.charts.mainChart = chart;

        // Group data by timestamp
        const timeSeriesMap = {};
        if (hasSeriesData) {
            state.data.series.forEach(point => {
                if (!timeSeriesMap[point.ts]) {
                    timeSeriesMap[point.ts] = {};
                }
                timeSeriesMap[point.ts][point.seriesKey] = point.v;
            });
        }

        // Sort timestamps
        const timestamps = Object.keys(timeSeriesMap).sort();

        // If no timestamps (no data), show message but still render legend
        if (timestamps.length === 0) {
            container.innerHTML = '<div class="no-data-message" style="text-align: center; padding: 40px;"><i class="fa-solid fa-chart-line" style="font-size: 48px; color: #667eea; margin-bottom: 16px; opacity: 0.5;"></i><h3>No Data in Selected Range</h3><p style="margin-top: 8px; opacity: 0.7;">Selected tags have no data for the specified time period.</p></div>';
            renderLegend();
            return;
        }

        // Calculate time range for smart axis label formatting
        const startTs = new Date(timestamps[0]).getTime();
        const endTs = new Date(timestamps[timestamps.length - 1]).getTime();
        const timeRangeMs = endTs - startTs;

        // Get series keys from seriesInfo (includes all selected) or fall back to series data
        const seriesInfoList = hasSeriesInfo ? state.data.seriesInfo : 
            Array.from(new Set(state.data.series.map(s => s.seriesKey))).map(key => ({
                seriesKey: key,
                hasData: true
            }));

        // Prepare series data - only include series that have data
        const seriesList = [];
        const yAxisArray = [];
        const axisOffset = 60;
        
        // Filter to only series with data for charting
        const seriesWithData = seriesInfoList.filter(info => info.hasData !== false);
        
        seriesWithData.forEach((info, index) => {
            const seriesKey = info.seriesKey;
            
            // Get color for this series from colorMap or generate one
            const seriesColor = state.colorMap[seriesKey] || CONFIG.colorPalette[index % CONFIG.colorPalette.length];
            
            // Determine axis position (alternate left/right)
            const isLeft = index % 2 === 0;
            const axisIndex = Math.floor(index / 2);
            
            // Create Y-axis for this series with matching color
            const yAxis = {
                type: 'value',
                name: seriesKey.length > 20 ? seriesKey.substring(0, 17) + '...' : seriesKey,
                nameLocation: 'middle',
                nameGap: 45,
                nameRotate: isLeft ? 90 : -90,
                nameTextStyle: {
                    color: seriesColor,
                    fontSize: 10,
                    fontWeight: 500
                },
                position: isLeft ? 'left' : 'right',
                offset: axisIndex * axisOffset,
                axisLine: {
                    show: true,
                    lineStyle: {
                        color: seriesColor,
                        width: 2
                    }
                },
                axisLabel: {
                    fontSize: 9,
                    color: seriesColor,
                    formatter: (value) => {
                        if (Math.abs(value) >= 1000) {
                            return (value / 1000).toFixed(1) + 'k';
                        }
                        return value.toFixed(1);
                    }
                },
                splitLine: {
                    show: index === 0,
                    lineStyle: {
                        color: 'rgba(200, 200, 200, 0.2)',
                        type: 'dashed'
                    }
                }
            };
            
            yAxisArray.push(yAxis);
            
            // Add series linked to its Y-axis with explicit color
            // For time axis, data must be [timestamp, value] pairs
            const timeValuePairs = timestamps.map(ts => {
                const val = timeSeriesMap[ts][seriesKey];
                return [new Date(ts).getTime(), val !== undefined ? val : null];
            });
            
            seriesList.push({
                name: seriesKey,
                type: 'line',
                yAxisIndex: index,
                smooth: true,
                data: timeValuePairs,
                lineStyle: { 
                    width: 2,
                    color: seriesColor
                },
                itemStyle: {
                    color: seriesColor,
                    borderColor: seriesColor
                },
                symbol: 'circle',
                symbolSize: 4,
                show: true
            });
        });

        // Get all series keys for legend (including those without data)
        const allSeriesKeys = seriesInfoList.map(info => info.seriesKey);

        const option = {
            backgroundColor: 'transparent',
            legend: {
                show: false,
                data: allSeriesKeys,
                selected: state.visibleSeries
            },
            tooltip: {
                trigger: 'axis',
                axisPointer: {
                    type: 'cross'
                },
                backgroundColor: 'rgba(50, 50, 50, 0.9)',
                borderColor: '#667eea',
                textStyle: { color: '#fff', fontSize: 12 },
                formatter: (params) => {
                    if (!params || params.length === 0) return '';
                    
                    // Format the timestamp from data
                    const timestamp = params[0].data[0];
                    const date = new Date(timestamp);
                    const formattedDate = date.toLocaleString('en-US', {
                        month: 'short', day: 'numeric', year: 'numeric',
                        hour: '2-digit', minute: '2-digit'
                    });
                    
                    let result = `<strong style="font-size: 13px;">${formattedDate}</strong><br/>`;
                    params.forEach(param => {
                        const value = param.data[1];
                        if (value !== null && value !== undefined) {
                            result += `<div style="margin: 4px 0; display: flex; justify-content: space-between; gap: 20px;">
                                <span style="color: ${param.color};">● ${param.seriesName}</span>
                                <strong>${value.toFixed(2)}</strong>
                            </div>`;
                        }
                    });
                    return result;
                }
            },
            grid: {
                left: seriesWithData.length > 1 ? Math.ceil(seriesWithData.length / 2) * 70 : 70,
                right: seriesWithData.length > 1 ? Math.floor(seriesWithData.length / 2) * 70 : 70,
                top: '8%',
                bottom: '15%',
                containLabel: false,
                backgroundColor: 'transparent',
                borderColor: 'transparent'
            },
            xAxis: {
                type: 'time',
                boundaryGap: false,
                axisLine: {
                    lineStyle: {
                        color: 'rgba(100, 100, 100, 0.3)'
                    }
                },
                axisLabel: {
                    fontSize: 11,
                    color: 'var(--label-fg)'
                },
                splitLine: {
                    show: false
                }
            },
            yAxis: yAxisArray,
            dataZoom: [
                {
                    type: 'slider',
                    show: true,
                    xAxisIndex: [0],
                    bottom: 30,
                    start: 0,
                    end: 100,
                    height: 20,
                    backgroundColor: 'rgba(102, 126, 234, 0.1)',
                    fillerColor: 'rgba(102, 126, 234, 0.2)',
                    borderColor: '#ccc',
                    textStyle: {
                        color: '#3F3F3F'
                    }
                },
                {
                    type: 'inside',
                    xAxisIndex: [0],
                    start: 0,
                    end: 100
                }
            ],
            series: seriesList,
            textStyle: {
                color: 'var(--label-fg)'
            }
        };

        chart.setOption(option);
        renderLegend();
        window.addEventListener('resize', () => {
            chart.resize();
        });
    }

    // Render Legend
    function renderLegend() {
        const legendContainer = document.getElementById('legendContainer');
        if (!legendContainer) return;

        // Use seriesInfo if available (shows all selected series including those without data)
        const seriesInfoList = state.data && state.data.seriesInfo ? state.data.seriesInfo : [];
        
        // Fallback to series data if no seriesInfo
        let legendItems = [];
        if (seriesInfoList.length > 0) {
            legendItems = seriesInfoList;
        } else if (state.data && state.data.series) {
            const uniqueKeys = Array.from(new Set(state.data.series.map(s => s.seriesKey)));
            legendItems = uniqueKeys.map(key => ({ seriesKey: key, hasData: true, displayName: key }));
        }

        if (legendItems.length === 0) {
            legendContainer.innerHTML = '<div class="no-data-message" style="width: 100%;">No series selected</div>';
            return;
        }

        // Initialize visible series if not already done
        if (Object.keys(state.visibleSeries).length === 0) {
            legendItems.forEach((item, index) => {
                state.visibleSeries[item.seriesKey] = true;
            });
        }

        let legendHtml = '';
        legendItems.forEach((item, index) => {
            const seriesKey = item.seriesKey;
            const hasData = item.hasData !== false;
            const displayName = item.displayName || seriesKey;
            const isVisible = state.visibleSeries[seriesKey] !== false;
            
            // Get color from colorMap or palette
            const seriesColor = state.colorMap[seriesKey] || CONFIG.colorPalette[index % CONFIG.colorPalette.length];
            
            // Style differently for series without data
            const noDataStyle = !hasData ? 'text-decoration: line-through; opacity: 0.6;' : '';
            const noDataSuffix = !hasData ? ' <span style="color: #999; font-size: 10px;">(No Data)</span>' : '';
            const markerOpacity = !hasData ? 0.4 : (isVisible ? 1 : 0.3);
            const textOpacity = !hasData ? 0.6 : (isVisible ? 1 : 0.6);
            const disabledClass = (!isVisible || !hasData) ? 'disabled' : '';
            const cursorStyle = hasData ? 'cursor: pointer;' : 'cursor: default;';
            
            legendHtml += `
                <div class="legend-item ${disabledClass}" data-series="${seriesKey}" data-index="${index}" data-has-data="${hasData}" style="${cursorStyle}">
                    <div class="legend-marker" style="background-color: ${seriesColor}; opacity: ${markerOpacity};"></div>
                    <span style="opacity: ${textOpacity}; ${noDataStyle}">${displayName}${noDataSuffix}</span>
                </div>
            `;
        });

        legendContainer.innerHTML = legendHtml;

        // Add click handlers to legend items (only for items with data)
        document.querySelectorAll('.legend-item').forEach(item => {
            item.addEventListener('click', (e) => {
                const hasData = item.getAttribute('data-has-data') === 'true';
                if (!hasData) return; // Don't toggle series without data
                
                const seriesKey = item.getAttribute('data-series');
                
                state.visibleSeries[seriesKey] = !state.visibleSeries[seriesKey];
                const isVisible = state.visibleSeries[seriesKey];
                
                // Toggle visual state
                const marker = item.querySelector('.legend-marker');
                const text = item.querySelector('span');
                item.classList.toggle('disabled');
                if (marker) marker.style.opacity = isVisible ? '1' : '0.3';
                if (text) text.style.opacity = isVisible ? '1' : '0.6';
                
                // Recalculate and update chart with repositioned axes
                if (state.charts.mainChart) {
                    const chart = state.charts.mainChart;
                    const option = chart.getOption();
                    const axisOffset = 60;
                    
                    // Get all series with data
                    const seriesWithData = (state.data.seriesInfo || []).filter(info => info.hasData !== false);
                    
                    // Count visible axes for grid calculation
                    let leftVisibleCount = 0;
                    let rightVisibleCount = 0;
                    
                    // First pass: count visible axes per side
                    seriesWithData.forEach((info, idx) => {
                        const isSeriesVisible = state.visibleSeries[info.seriesKey] !== false;
                        const isLeft = idx % 2 === 0;
                        if (isSeriesVisible) {
                            if (isLeft) leftVisibleCount++;
                            else rightVisibleCount++;
                        }
                    });
                    
                    // Second pass: recalculate positions for all axes
                    let leftIndex = 0;
                    let rightIndex = 0;
                    
                    seriesWithData.forEach((info, idx) => {
                        const isSeriesVisible = state.visibleSeries[info.seriesKey] !== false;
                        const isLeft = idx % 2 === 0;
                        
                        if (option.yAxis && option.yAxis[idx]) {
                            option.yAxis[idx].show = isSeriesVisible;
                            
                            if (isSeriesVisible) {
                                // Recalculate offset based on visible position
                                if (isLeft) {
                                    option.yAxis[idx].offset = leftIndex * axisOffset;
                                    leftIndex++;
                                } else {
                                    option.yAxis[idx].offset = rightIndex * axisOffset;
                                    rightIndex++;
                                }
                            }
                        }
                        
                        // Update series visibility
                        if (option.series && option.series[idx]) {
                            // ECharts uses lineStyle.opacity for hiding lines
                            option.series[idx].lineStyle = option.series[idx].lineStyle || {};
                            option.series[idx].lineStyle.opacity = isSeriesVisible ? 1 : 0;
                            option.series[idx].itemStyle = option.series[idx].itemStyle || {};
                            option.series[idx].itemStyle.opacity = isSeriesVisible ? 1 : 0;
                        }
                    });
                    
                    // Update grid margins based on visible axes
                    const newGridLeft = leftVisibleCount > 0 ? leftVisibleCount * 70 : 70;
                    const newGridRight = rightVisibleCount > 0 ? rightVisibleCount * 70 : 70;
                    
                    option.grid[0].left = newGridLeft;
                    option.grid[0].right = newGridRight;
                    
                    chart.setOption(option);
                }
            });
        });
    }

    // Update Device Contribution Chart
    function updateDeviceContributionChart() {
        const container = document.getElementById('deviceContributionChart');
        if (!container || !state.data) return;

        if (state.charts.deviceChart) {
            state.charts.deviceChart.dispose();
        }

        const deviceValues = {};
        state.data.series.forEach(point => {
            if (!deviceValues[point.device]) {
                deviceValues[point.device] = 0;
            }
            deviceValues[point.device] += Math.abs(point.v || 0);
        });

        const devices = Object.keys(deviceValues);
        const values = Object.values(deviceValues);

        const chart = echarts.init(container);
        state.charts.deviceChart = chart;

        const option = {
            backgroundColor: 'transparent',
            tooltip: {
                trigger: 'axis',
                axisPointer: { type: 'shadow' },
                backgroundColor: 'rgba(50, 50, 50, 0.9)',
                textStyle: { color: '#fff' }
            },
            grid: {
                left: '5%',
                right: '5%',
                top: '10%',
                bottom: '15%',
                containLabel: true
            },
            xAxis: {
                type: 'category',
                data: devices,
                axisLine: { lineStyle: { color: 'var(--border-color)' } },
                axisLabel: { color: 'var(--label-fg)', fontSize: 12 }
            },
            yAxis: {
                type: 'value',
                axisLine: { lineStyle: { color: 'var(--border-color)' } },
                axisLabel: { color: 'var(--label-fg)', fontSize: 12 },
                splitLine: { lineStyle: { color: 'var(--grouping-box)' } }
            },
            series: [{
                type: 'bar',
                data: values,
                itemStyle: {
                    color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                        { offset: 0, color: '#667eea' },
                        { offset: 1, color: '#764ba2' }
                    ])
                }
            }]
        };

        chart.setOption(option);
    }

    // Update Tag Distribution Chart
    function updateTagDistributionChart() {
        const container = document.getElementById('tagDistributionChart');
        if (!container || !state.data) return;

        if (state.charts.tagChart) {
            state.charts.tagChart.dispose();
        }

        const tagValues = {};
        state.data.series.forEach(point => {
            if (!tagValues[point.tag]) {
                tagValues[point.tag] = 0;
            }
            tagValues[point.tag] += Math.abs(point.v || 0);
        });

        const tags = Object.keys(tagValues);
        const values = Object.values(tagValues);

        const chart = echarts.init(container);
        state.charts.tagChart = chart;

        const option = {
            backgroundColor: 'transparent',
            tooltip: {
                trigger: 'item',
                backgroundColor: 'rgba(50, 50, 50, 0.9)',
                textStyle: { color: '#fff' },
                formatter: '{b}: {c} ({d}%)'
            },
            series: [{
                type: 'pie',
                radius: ['30%', '70%'],
                data: tags.map((tag, idx) => ({
                    value: values[idx],
                    name: tag,
                    itemStyle: {
                        color: CONFIG.colorPalette[idx % CONFIG.colorPalette.length]
                    }
                })),
                label: {
                    color: 'var(--label-fg)',
                    fontSize: 12
                }
            }]
        };

        chart.setOption(option);
    }

    // Utility Functions
    function updateSummary(devices, tags, points, seriesWithData, totalSeries) {
        document.getElementById('selectedDeviceCount').textContent = devices;
        document.getElementById('selectedTagCount').textContent = tags;
        document.getElementById('dataPointCount').textContent = points;
        document.getElementById('plotSeriesCount').textContent = seriesWithData;
        
        // Show "X of Y Series" if some series have no data
        const seriesText = totalSeries && totalSeries !== seriesWithData ? 
            `${seriesWithData}/${totalSeries} Series` : 
            `${seriesWithData} Series`;
        document.getElementById('seriesCount').textContent = seriesText;
    }

    function updateTimeInfo(lastUpdate, timeRange, info) {
        const now = new Date();
        const diffMinutes = Math.floor((now - lastUpdate) / 60000);
        const diffText = diffMinutes < 1 ? 'just now' : `${diffMinutes} min ago`;

        document.getElementById('timeRange').textContent = timeRange;
        document.getElementById('lastUpdated').textContent = `Last Update: ${diffText}`;
    }

    function formatDate(date) {
        return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    }

    function formatTime(date) {
        return date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    }

    // Smart formatter based on time range
    function formatAxisLabel(date, rangeMs) {
        const ONE_DAY = 24 * 60 * 60 * 1000;
        const ONE_WEEK = 7 * ONE_DAY;
        const ONE_MONTH = 30 * ONE_DAY;
        const ONE_YEAR = 365 * ONE_DAY;
        
        if (rangeMs <= ONE_DAY) {
            // Less than 1 day: show time only (HH:MM)
            return date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
        } else if (rangeMs <= ONE_WEEK) {
            // 1-7 days: show day + time (Mon 02:30 PM)
            return date.toLocaleDateString('en-US', { weekday: 'short' }) + ' ' + 
                   date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
        } else if (rangeMs <= ONE_MONTH) {
            // 1 week to 1 month: show date + time (Jan 15 02:30 PM)
            return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }) + ' ' +
                   date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
        } else if (rangeMs <= ONE_YEAR) {
            // 1 month to 1 year: show date only (Jan 15)
            return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
        } else {
            // More than 1 year: show full date (Jan 15, 2025)
            return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
        }
    }

    function showLoading(visible) {
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) {
            loadingDiv.classList.toggle('active', visible);
        }
    }

    function handleError(message) {
        console.error(message);
        const container = document.getElementById('mainTrendChart');
        if (container) {
            container.innerHTML = `<div class="error-message">⚠️ ${message}</div>`;
        }
    }

    // Public API
    return {
        init,
        reload: loadData
    };
})();

// Initialize on page load
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', TrendViewer.init);
} else {
    TrendViewer.init();
}
