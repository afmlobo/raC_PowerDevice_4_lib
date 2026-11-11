//13 Aug 2025: Updated to show data for 'Ang' as well
document.addEventListener('DOMContentLoaded', function () {
    const deviceName = document.body.getAttribute('data-device-name');
    if (!deviceName) {
        console.error('deviceName is not defined');
        document.body.innerHTML = '<h1>Error: Device name is not defined</h1>';
        return;
    }

    var harmonicsInterval = null;
    var currentMetric = "No Selection";
    var currentChartType = "bar";
    var hideFirstHarmonic = false;
    var maxRetries = 5;
    var retryDelay = 1000;

    var harmonicsChart = echarts.init(document.getElementById('harmonics-chart'));
    var statusDot = document.getElementById('status-dot');
    var harmonicValuesDiv = document.getElementById('harmonic-values');
    var chartTitle = document.getElementById('chart-title');

    document.getElementById('toggle-view').addEventListener('click', function() {
        currentChartType = currentChartType === 'bar' ? 'line' : 'bar';
        this.textContent = currentChartType === 'bar' ? 'Switch to Line' : 'Switch to Bar';
        fetchHarmonicsData();
    });

    document.getElementById('toggle-first-harmonic').addEventListener('click', function() {
        hideFirstHarmonic = !hideFirstHarmonic;
        this.textContent = hideFirstHarmonic ? 'Show Fundamental' : 'Hide Fundamental';
        fetchHarmonicsData();
    });

    function getRange(metric) {
        if (metric.includes('H1')) return 'DC_to_31';
        if (metric.includes('H2')) return '32_to_63';
        if (metric.includes('H3')) return '64_to_95';
        if (metric.includes('H4')) return '96_to_127';
        return 'Unknown';
    }

    function getOrdinalSuffix(number) {
        if (number === 0) return 'DC';
        const j = number % 10, k = number % 100;
        if (j === 1 && k !== 11) return number + 'st';
        if (j === 2 && k !== 12) return number + 'nd';
        if (j === 3 && k !== 13) return number + 'rd';
        return number + 'th';
    }

    function fetchHarmonicsData(retryCount = 0) {
        const jsonFile = `${deviceName}_HarmonicsData.json`;
        fetch(jsonFile)
            .then(response => {
                if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
                return response.json();
            })
            .then(data => {
                currentMetric = data.metric || "No Selection";
                const lastRefreshTimestamp = data.lastRefreshTimestamp || new Date().toISOString();
                updateHarmonicsChart(data.harmonics, currentMetric);
                updateHarmonicValues(data.harmonics, currentMetric);

                if (currentMetric === "No Selection") {
                    chartTitle.textContent = "Harmonics Chart (No Selection)";
                } else {
                    const range = getRange(currentMetric).replace('_to_', '-');
                    chartTitle.textContent = `${currentMetric} (${range})`;
                }

                document.getElementById('last-updated').textContent = `Last updated: ${new Date(lastRefreshTimestamp).toLocaleTimeString()}`;
                statusDot.classList.remove('good', 'warn', 'error');
                statusDot.classList.add('good');

                if (!harmonicsInterval) {
                    harmonicsInterval = setInterval(() => fetchHarmonicsData(), 3000);
                }
            })
            .catch(error => {
                console.error('Error fetching data:', error);
                if (retryCount < maxRetries) {
                    setTimeout(() => fetchHarmonicsData(retryCount + 1), retryDelay);
                } else {
                    statusDot.classList.remove('good', 'warn');
                    statusDot.classList.add('error');
                    document.getElementById('last-updated').textContent = `Last updated: Failed to load data`;
                    harmonicValuesDiv.innerHTML = '<div>No data available.</div>';
                    harmonicsChart.clear();
                }
            });
    }

    function updateHarmonicValues(harmonicsData, selectedMetric) {
        if (selectedMetric === "No Selection") {
            harmonicValuesDiv.innerHTML = '<div>No metric selected.</div>';
            return;
        }

        let harmonicRange = [];
        let prefix = '';
        // Use the same robust regular expression from updateHarmonicsChart
        const metricPattern = /_(H1|H2|H3|H4)_(RMS|Ang)_(DC_to_31|32_to_63|64_to_95|96_to_127)/;

        if (selectedMetric.includes('H1')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i);
            prefix = selectedMetric.replace(metricPattern, '');
        } else if (selectedMetric.includes('H2')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i + 32);
            prefix = selectedMetric.replace(metricPattern, '');
        } else if (selectedMetric.includes('H3')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i + 64);
            prefix = selectedMetric.replace(metricPattern, '');
        } else if (selectedMetric.includes('H4')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i + 96);
            prefix = selectedMetric.replace(metricPattern, '');
        }

        let htmlContent = '';
        harmonicRange.forEach(num => {
            const order = getOrdinalSuffix(num);
            const key = num === 0 ? `${prefix}_DC` : `${prefix}_${order}`;
            const value = harmonicsData[key];// || '0';
            htmlContent += `<div><span class="label">${order}</span><span class="value">${value}</span></div>`;
            
        });
        harmonicValuesDiv.innerHTML = htmlContent;
    }

    function updateHarmonicsChart(harmonicsData, selectedMetric) {
        if (selectedMetric === "No Selection") {
            harmonicsChart.clear();
            return;
        }

        let harmonicRange = [];
        let prefix = '';
        if (selectedMetric.includes('H1')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i);
            // handle both "_RMS_" and "_Ang_"
            prefix = selectedMetric.replace(/_(H1|H2|H3|H4)_(RMS|Ang)_(DC_to_31|32_to_63|64_to_95|96_to_127)/, '');
        } else if (selectedMetric.includes('H2')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i + 32);
            
            prefix = selectedMetric.replace(/_(H1|H2|H3|H4)_(RMS|Ang)_(DC_to_31|32_to_63|64_to_95|96_to_127)/, '');
        } else if (selectedMetric.includes('H3')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i + 64);
            
            prefix = selectedMetric.replace(/_(H1|H2|H3|H4)_(RMS|Ang)_(DC_to_31|32_to_63|64_to_95|96_to_127)/, '');
        } else if (selectedMetric.includes('H4')) {
            harmonicRange = Array.from({ length: 32 }, (_, i) => i + 96);
            
            prefix = selectedMetric.replace(/_(H1|H2|H3|H4)_(RMS|Ang)_(DC_to_31|32_to_63|64_to_95|96_to_127)/, '');
        }

        let harmonicValues = harmonicRange.map((num) => {
            const order = getOrdinalSuffix(num);
            const key = num === 0 ? `${prefix}_DC` : `${prefix}_${order}`;
            return parseFloat(harmonicsData[key]) || 0;
        });

        if (hideFirstHarmonic) {
            const firstHarmonicIndex = selectedMetric.includes('H1') ? 1 : 0;
            harmonicRange = harmonicRange.filter((_, index) => index !== firstHarmonicIndex);
            harmonicValues = harmonicValues.filter((_, index) => index !== firstHarmonicIndex);
        }

        const displayRange = harmonicRange.map(num => getOrdinalSuffix(num));

        const chartOption = {
            grid: { left: '5%', right: '5%', bottom: '10%', top: '10%', containLabel: true },
            tooltip: {
                trigger: 'axis',
                formatter: (params) => `${params[0].name} Harmonic: <strong>${params[0].value.toFixed(3)}</strong>`            
            },
            dataZoom: [
                { type: 'inside', xAxisIndex: 0, zoomOnMouseWheel: true, start: 0, end: 100 }
            ],
            xAxis: {
                type: 'category',
                data: displayRange,
                axisLine: { lineStyle: { color: '#D8D8D8' } },
                axisLabel: { rotate: 45, interval: 2, color: '#3F3F3F' }
            },
            yAxis: {
                type: 'value',
                axisLabel: { formatter: '{value}', color: '#3F3F3F' },
                axisLine: { lineStyle: { color: '#D8D8D8' } },
                splitLine: { lineStyle: { color: '#D8D8D8' } }
            },
            series: [{
                data: harmonicValues,
                type: currentChartType,
                name: prefix,
                itemStyle: { color: '#475CA7' },
                areaStyle: currentChartType === 'line' ? {
                    color: {
                        type: 'linear',
                        x: 0, y: 0, x2: 0, y2: 1,
                        colorStops: [
                            { offset: 0, color: 'rgba(71, 92, 167, 0.5)' },
                            { offset: 1, color: 'rgba(71, 92, 167, 0.05)' }
                        ]
                    }
                } : undefined,
                smooth: currentChartType === 'line' ? 0.2 : undefined,
                lineStyle: currentChartType === 'line' ? { width: 3 } : undefined
            }]
        };

        harmonicsChart.setOption(chartOption);
    }

    window.addEventListener('resize', () => harmonicsChart.resize());
    setTimeout(() => fetchHarmonicsData(), 1000);
});