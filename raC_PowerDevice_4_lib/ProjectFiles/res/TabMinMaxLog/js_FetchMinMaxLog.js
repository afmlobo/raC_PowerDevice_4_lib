document.addEventListener('DOMContentLoaded', function() {
    if (!document.body) {
        console.error('Document body is null');
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) loadingDiv.textContent = 'Error: Document body not found';
        return;
    }

    const deviceName = document.body.getAttribute('data-device-name');
    const loadingDiv = document.getElementById('loading');
    const minMaxChartDiv = document.getElementById('minMaxChart');
    const tableBody = document.getElementById('parameterTable');
    const controlButtons = document.querySelectorAll('.control-btn');

    if (!loadingDiv || !minMaxChartDiv || !tableBody || !controlButtons.length) {
        console.error('Required DOM elements missing:', {
            loadingDiv: !!loadingDiv,
            minMaxChartDiv: !!minMaxChartDiv,
            tableBody: !!tableBody,
            controlButtons: !!controlButtons.length
        });
        if (loadingDiv) loadingDiv.textContent = 'Error: Dashboard initialization failed';
        return;
    }

    const minMaxChart = echarts.init(minMaxChartDiv);
    let currentType = 'voltage';
    let dashboardData = { parameters: [] };

    function validateDashboardData() {
        console.log('Current dashboardData:', dashboardData);
        if (!dashboardData || !Array.isArray(dashboardData.parameters)) {
            console.error('Invalid dashboard data structure:', dashboardData);
            return false;
        }
        return true;
    }

    function filterParameters(type) {
        const typeFilters = {
            'voltage': param => param.name.toLowerCase().includes('volt'),
            'current': param => param.name.toLowerCase().includes('amp'),
            'power': param => param.name.toLowerCase().includes('kw') ||
                             param.name.toLowerCase().includes('kvar') ||
                             param.name.toLowerCase().includes('kva'),
            'lines': param => ['L1_kW', 'L1_kVAR', 'L1_kVA', 'L2_kW', 'L2_kVAR', 'L2_kVA', 'L3_kW', 'L3_kVAR', 'L3_kVA']
                .includes(param.name)
        };
        return dashboardData.parameters.filter(typeFilters[type] || (() => true));
    }

    function updateChart(type, resetZoom = true) {
        if (!validateDashboardData()) {
            if (loadingDiv) loadingDiv.textContent = 'Invalid data structure';
            return;
        }

        const filteredData = filterParameters(type);
        
        const option = {
            title: {
                text: `${type === 'lines' ? 'L1, L2, L3 Power Ranges' : type.charAt(0).toUpperCase() + type.slice(1)} Parameters Ranges`,
                left: 'center',
                textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
            },
            legend: {
                data: ['Range'],
                top: 30,
                textStyle: { color: '#3F3F3F' }
            },
            tooltip: {
                trigger: 'axis',
                backgroundColor: 'rgba(255, 255, 255, 0.9)',
                borderColor: '#D8D8D8',
                borderWidth: 1,
                padding: [5, 10],
                textStyle: { color: '#3F3F3F', fontSize: 12 },
                confine: true,
                formatter: function(params) {
                    const dataIndex = params[0].dataIndex;
                    const param = filteredData[dataIndex];
                    return `${param.name}<br/>Min: ${param.minValue.toFixed(2)}<br/>Max: ${param.maxValue.toFixed(2)}`;
                }
            },
            grid: {
                left: '3%',
                right: '4%',
                bottom: '3%',
                containLabel: true
            },
            xAxis: {
                type: 'value',
                boundaryGap: [0, 0.01],
                zoomable: true,
                axisLine: { lineStyle: { color: '#D8D8D8' } },
                axisLabel: { color: '#3F3F3F' },
                splitLine: { lineStyle: { color: '#D8D8D8' } }
            },
            yAxis: {
                type: 'category',
                data: filteredData.map(param => param.name),
                axisLine: { lineStyle: { color: '#D8D8D8' } },
                axisLabel: { color: '#3F3F3F' }
            },
            dataZoom: [{
                type: 'inside',
                xAxisIndex: 0,
                filterMode: 'none',
                zoomOnMouseWheel: true,
                moveOnMouseWheel: false
            }],
            series: [{
                name: 'Range',
                type: 'custom',
                renderItem: function(params, api) {
                    const start = api.value(0);
                    const end = api.value(1);
                    const height = api.size([0, 1])[1] * 0.6;
                    const x0 = api.coord([start, params.dataIndex])[0];
                    const y = api.coord([start, params.dataIndex])[1];
                    const x1 = api.coord([end, params.dataIndex])[0];
                    return {
                        type: 'group',
                        children: [{
                            type: 'rect',
                            shape: {
                                x: x0,
                                y: y - height / 2,
                                width: x1 - x0,
                                height: height
                            },
                            style: { fill: '#475CA7' }
                        }]
                    };
                },
                encode: { x: [0, 1], y: 2 },
                data: filteredData.map((param, index) => [
                    param.minValue,
                    param.maxValue,
                    index
                ])
            }]
        };

        if (resetZoom) {
            option.dataZoom[0].start = 0;
            option.dataZoom[0].end = 100;
        }

        minMaxChart.setOption(option, resetZoom);
    }

    function updateTable(type) {
        if (!validateDashboardData()) {
            if (loadingDiv) loadingDiv.textContent = 'Invalid data structure';
            return;
        }

        const filteredData = filterParameters(type);
        tableBody.innerHTML = filteredData.map(param => `
            <tr>
                <td>${param.name}</td>
                <td>${param.minValue.toFixed(2)}</td>
                <td>${param.minTimestamp}</td>
                <td>${param.maxValue.toFixed(2)}</td>
                <td>${param.maxTimestamp}</td>
            </tr>
        `).join('');
    }

    fetch(`${deviceName}_MinMaxLogData.json?t=${Date.now()}`)
        .then(response => {
            if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
            return response.json();
        })
        .then(data => {
            dashboardData = data;
            if (!validateDashboardData()) {
                loadingDiv.textContent = 'No valid data available';
                return;
            }

            controlButtons.forEach(button => {
                button.addEventListener('click', () => {
                    controlButtons.forEach(btn => btn.classList.remove('active'));
                    button.classList.add('active');
                    currentType = button.dataset.type;
                    updateChart(currentType, true);
                    updateTable(currentType);
                });
            });

            updateChart('voltage', true);
            updateTable('voltage');

            window.addEventListener('resize', () => {
                minMaxChart.resize();
            });

            loadingDiv.style.display = 'none';
        })
        .catch(error => {
            console.error('Error fetching data:', error);
            loadingDiv.textContent = 'Data not loaded or No valid data available';
        });
});
