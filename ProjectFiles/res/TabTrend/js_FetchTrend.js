/**
 * Single-Device Trend Viewer
 * Displays multi-axis line chart data for multiple tags on a single device
 * Each tag gets its own Y-axis for independent scaling
 */

const TrendViewer = (function () {
    // Configuration
    const CONFIG = {
        dataFile: 'TrendData.json',
        colorPalette: [
            '#5470c6', '#91cc75', '#fac858', '#ee6666', '#73c0de', 
            '#3ba272', '#fc8452', '#9a60b4', '#ea7ccc', '#5fb878',
            '#e87c25', '#b1a595', '#ff9e64', '#24a795', '#80d5fa'
        ]
    };

    // State
    let state = {
        data: null,
        chart: null,
        colorMap: {},
        visibleSeries: {}
    };

    // Initialize
    function init() {
        console.log('Initializing Single-Device Trend Viewer...');
        loadData();
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
                renderChart();
                showLoading(false);
            })
            .catch(error => {
                console.error('Error loading data:', error);
                handleError(`Failed to load trend data: ${error.message}`);
                showLoading(false);
            });
    }

    // Handle Info Messages (no data)
    function handleInfoMessage(infoData) {
        console.log('Info message:', infoData);
        
        const chartContainer = document.getElementById('main-chart');
        if (chartContainer) {
            chartContainer.innerHTML = `
                <div class="no-data-message">
                    <div class="info-icon">ℹ</div>
                    <h3>${infoData.title}</h3>
                    <p>${infoData.message}</p>
                </div>
            `;
        }

        // Clear legend
        const legendContainer = document.getElementById('legend-container');
        if (legendContainer) {
            legendContainer.innerHTML = '<div class="legend-placeholder">Legend will appear here</div>';
        }

        showLoading(false);
    }

    // Render Chart
    function renderChart() {
        const container = document.getElementById('main-chart');
        if (!container || !state.data) return;

        // Check data format
        const isMultiTag = state.data.tags && Array.isArray(state.data.tags) && state.data.tags.length > 0;
        
        if (isMultiTag) {
            renderMultiAxisChart();
        } else if (state.data.points) {
            // Legacy single-tag format
            renderSingleTagChart();
        } else {
            container.innerHTML = '<div class="no-data-message"><h3>No Data Available</h3><p>Select tags and a time range to view trend data.</p></div>';
        }
    }

    // Render Multi-Axis Chart (new format with multiple tags)
    function renderMultiAxisChart() {
        const container = document.getElementById('main-chart');
        if (!container || !state.data) return;

        const hasSeriesData = state.data.series && state.data.series.length > 0;
        const tags = state.data.tags || [];

        if (!hasSeriesData || tags.length === 0) {
            container.innerHTML = '<div class="no-data-message"><h3>No Data in Selected Range</h3><p>Selected tags have no data for the specified time period.</p></div>';
            renderLegend();
            return;
        }

        // Dispose old chart
        if (state.chart) {
            state.chart.dispose();
        }

        const chart = echarts.init(container);
        state.chart = chart;

        // Generate color map
        state.colorMap = {};
        state.visibleSeries = {};
        tags.forEach((tag, index) => {
            state.colorMap[tag] = CONFIG.colorPalette[index % CONFIG.colorPalette.length];
            state.visibleSeries[tag] = true;
        });

        // Group data by timestamp
        const timeSeriesMap = {};
        state.data.series.forEach(point => {
            if (!timeSeriesMap[point.ts]) {
                timeSeriesMap[point.ts] = {};
            }
            timeSeriesMap[point.ts][point.tag] = point.v;
        });

        // Sort timestamps
        const timestamps = Object.keys(timeSeriesMap).sort();

        if (timestamps.length === 0) {
            container.innerHTML = '<div class="no-data-message"><h3>No Data Points</h3><p>No data found in the selected range.</p></div>';
            renderLegend();
            return;
        }

        // Prepare series and Y-axes
        const seriesList = [];
        const yAxisArray = [];
        const axisOffset = 60;

        tags.forEach((tag, index) => {
            const seriesColor = state.colorMap[tag];
            const isLeft = index % 2 === 0;
            const axisIndex = Math.floor(index / 2);

            // Create Y-axis for this tag
            const yAxis = {
                type: 'value',
                name: tag.length > 20 ? tag.substring(0, 17) + '...' : tag,
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
                        color: 'rgba(200, 200, 200, 0.3)',
                        type: 'dashed'
                    }
                }
            };

            yAxisArray.push(yAxis);

            // Create data points for this tag
            const timeValuePairs = timestamps.map(ts => {
                const val = timeSeriesMap[ts][tag];
                return [new Date(ts).getTime(), val !== undefined ? val : null];
            });

            seriesList.push({
                name: tag,
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
                symbolSize: 4
            });
        });

        // Calculate grid margins based on number of tags
        const leftCount = Math.ceil(tags.length / 2);
        const rightCount = Math.floor(tags.length / 2);

        const option = {
            backgroundColor: '#E0E0E0',
            legend: {
                show: false,
                data: tags,
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
                left: leftCount > 0 ? leftCount * 70 : 70,
                right: rightCount > 0 ? rightCount * 70 : 70,
                top: '8%',
                bottom: '15%',
                containLabel: false
            },
            xAxis: {
                type: 'time',
                boundaryGap: false,
                axisLine: {
                    lineStyle: {
                        color: 'rgba(100, 100, 100, 0.5)'
                    }
                },
                axisLabel: {
                    fontSize: 11,
                    color: '#3F3F3F'
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
                    backgroundColor: 'rgba(71, 92, 167, 0.1)',
                    fillerColor: 'rgba(71, 92, 167, 0.2)',
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
            series: seriesList
        };

        chart.setOption(option);
        renderLegend();

        window.addEventListener('resize', () => {
            chart.resize();
        });
    }

    // Render Legend
    function renderLegend() {
        const legendContainer = document.getElementById('legend-container');
        if (!legendContainer || !state.data || !state.data.tags) return;

        const tags = state.data.tags;
        
        if (tags.length === 0) {
            legendContainer.innerHTML = '<div class="legend-placeholder">No tags selected</div>';
            return;
        }

        let legendHtml = '';
        tags.forEach((tag, index) => {
            const seriesColor = state.colorMap[tag];
            const isVisible = state.visibleSeries[tag] !== false;
            const disabledClass = !isVisible ? 'disabled' : '';
            const markerOpacity = isVisible ? 1 : 0.3;
            const textOpacity = isVisible ? 1 : 0.6;

            legendHtml += `
                <div class="legend-item ${disabledClass}" data-tag="${tag}" data-index="${index}">
                    <div class="legend-marker" style="background-color: ${seriesColor}; opacity: ${markerOpacity};"></div>
                    <span style="opacity: ${textOpacity};">${formatDisplayName(tag)}</span>
                </div>
            `;
        });

        legendContainer.innerHTML = legendHtml;

        // Add click handlers for legend items
        document.querySelectorAll('.legend-item').forEach(item => {
            item.addEventListener('click', () => {
                const tag = item.getAttribute('data-tag');
                const index = parseInt(item.getAttribute('data-index'));
                
                state.visibleSeries[tag] = !state.visibleSeries[tag];
                const isVisible = state.visibleSeries[tag];

                // Update legend visual
                const marker = item.querySelector('.legend-marker');
                const text = item.querySelector('span');
                item.classList.toggle('disabled');
                if (marker) marker.style.opacity = isVisible ? '1' : '0.3';
                if (text) text.style.opacity = isVisible ? '1' : '0.6';

                // Update chart
                if (state.chart) {
                    const option = state.chart.getOption();
                    const tags = state.data.tags;
                    const axisOffset = 60;

                    // Count visible axes per side
                    let leftVisibleCount = 0;
                    let rightVisibleCount = 0;

                    tags.forEach((t, idx) => {
                        const isLeft = idx % 2 === 0;
                        if (state.visibleSeries[t] !== false) {
                            if (isLeft) leftVisibleCount++;
                            else rightVisibleCount++;
                        }
                    });

                    // Recalculate positions
                    let leftIndex = 0;
                    let rightIndex = 0;

                    tags.forEach((t, idx) => {
                        const isTagVisible = state.visibleSeries[t] !== false;
                        const isLeft = idx % 2 === 0;

                        if (option.yAxis && option.yAxis[idx]) {
                            option.yAxis[idx].show = isTagVisible;

                            if (isTagVisible) {
                                if (isLeft) {
                                    option.yAxis[idx].offset = leftIndex * axisOffset;
                                    leftIndex++;
                                } else {
                                    option.yAxis[idx].offset = rightIndex * axisOffset;
                                    rightIndex++;
                                }
                            }
                        }

                        if (option.series && option.series[idx]) {
                            option.series[idx].lineStyle = option.series[idx].lineStyle || {};
                            option.series[idx].lineStyle.opacity = isTagVisible ? 1 : 0;
                            option.series[idx].itemStyle = option.series[idx].itemStyle || {};
                            option.series[idx].itemStyle.opacity = isTagVisible ? 1 : 0;
                        }
                    });

                    // Update grid margins
                    option.grid[0].left = leftVisibleCount > 0 ? leftVisibleCount * 70 : 70;
                    option.grid[0].right = rightVisibleCount > 0 ? rightVisibleCount * 70 : 70;

                    state.chart.setOption(option);
                }
            });
        });
    }

    // Legacy single-tag chart
    function renderSingleTagChart() {
        const container = document.getElementById('main-chart');
        const chart = echarts.init(container);
        state.chart = chart;

        const chartData = state.data.points.map(point => {
            return [new Date(point.ts), point.v];
        });

        const option = {
            backgroundColor: '#E0E0E0',
            tooltip: {
                trigger: 'axis',
                formatter: function(params) {
                    const param = params[0];
                    const date = new Date(param.value[0]);
                    return `${date.toLocaleString()}<br/>${state.data.tag}: ${parseFloat(param.value[1]).toFixed(3)}`;
                },
                axisPointer: {
                    type: 'cross',
                    label: {
                        backgroundColor: '#919191'
                    }
                }
            },
            legend: {
                data: [state.data.tag],
                top: 'top',
                textStyle: {
                    color: '#3F3F3F'
                }
            },
            grid: {
                left: '3%',
                right: '4%',
                bottom: '15%',
                top: '15%',
                containLabel: true
            },
            dataZoom: [
                {
                    type: 'inside',
                    start: 0,
                    end: 100,
                    zoomOnMouseWheel: true
                },
                {
                    type: 'slider',
                    start: 0,
                    end: 100,
                    handleSize: '80%',
                    handleStyle: {
                        color: '#475CA7',
                        shadowBlur: 3,
                        shadowColor: 'rgba(71, 92, 167, 0.6)',
                        shadowOffsetX: 2,
                        shadowOffsetY: 2
                    }
                }
            ],
            xAxis: {
                type: 'time',
                boundaryGap: false,
                axisLabel: {
                    color: '#3F3F3F',
                    formatter: function(value) {
                        const date = new Date(value);
                        return date.toLocaleString('en-US', { 
                            month: 'short', 
                            day: 'numeric', 
                            hour: '2-digit', 
                            minute: '2-digit' 
                        });
                    }
                },
                splitLine: { show: false }
            },
            yAxis: {
                type: 'value',
                name: state.data.tag,
                nameTextStyle: { color: '#3F3F3F' },
                axisLabel: { color: '#3F3F3F', formatter: '{value}' },
                splitLine: { lineStyle: { color: '#D8D8D8' } }
            },
            series: [{
                name: state.data.tag,
                type: 'line',
                smooth: true,
                symbol: 'circle',
                symbolSize: 6,
                sampling: 'lttb',
                areaStyle: { opacity: 0.3 },
                data: chartData
            }]
        };

        chart.setOption(option);
        window.addEventListener('resize', () => chart.resize());
    }

    // Utility Functions
    function formatDisplayName(columnName) {
        if (!columnName) return 'Unknown';
        return columnName.replace(/_/g, ' ').replace(/\s+/g, ' ').trim();
    }

    function showLoading(visible) {
        const loadingDiv = document.getElementById('loading-overlay');
        if (loadingDiv) {
            loadingDiv.style.display = visible ? 'flex' : 'none';
        }
    }

    // Show status message in the message container
    function showStatusMessage(message, type = 'info') {
        const container = document.getElementById('message-container');
        if (!container) return;

        container.style.display = 'flex';
        container.className = `message-${type}`;
        container.innerHTML = `
            <div class="message">
                <span class="message-text">${message}</span>
                <button class="message-close" onclick="this.parentElement.parentElement.style.display='none'">✕</button>
            </div>
        `;

        // Auto-hide success messages after 5 seconds
        if (type === 'success') {
            setTimeout(() => {
                container.style.display = 'none';
            }, 5000);
        }
    }

    // Hide status message
    function hideStatusMessage() {
        const container = document.getElementById('message-container');
        if (container) {
            container.style.display = 'none';
        }
    }

    function handleError(message) {
        console.error(message);
        const container = document.getElementById('main-chart');
        if (container) {
            container.innerHTML = `<div class="error-message">⚠️ ${message}</div>`;
        }
        showLoading(false);
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
 