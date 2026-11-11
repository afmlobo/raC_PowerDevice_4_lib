document.addEventListener('DOMContentLoaded', function () {
    const deviceName = document.body.getAttribute('data-device-name');
    if (!deviceName) {
        console.error('deviceName is not defined');
        document.body.innerHTML = '<h1>Error: Device name is not defined</h1>';
        return;
    }

    var dataInterval = null;
    var currentTab = localStorage.getItem('activeTab') || "voltages";
    var chartTypes = { voltages: "bar", currents: "bar", power: "bar" };
    var maxRetries = 5;
    var retryDelay = 1000;

    var voltagesChart = echarts.init(document.getElementById('voltages-chart'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var currentsChart = echarts.init(document.getElementById('currents-chart'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var powerPieChart = echarts.init(document.getElementById('power-pie-chart'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var powerBarChart = echarts.init(document.getElementById('power-bar-chart'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var frequencyGauge = echarts.init(document.getElementById('frequency-gauge'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var avgFrequencyGauge = echarts.init(document.getElementById('avg-frequency-gauge'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var gaugeL1PF = echarts.init(document.getElementById('gauge-l1-pf'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var gaugeL2PF = echarts.init(document.getElementById('gauge-l2-pf'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var gaugeL3PF = echarts.init(document.getElementById('gauge-l3-pf'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var gaugeTotalPF = echarts.init(document.getElementById('gauge-total-pf'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });

    var statusDot = document.getElementById('status-dot');

    // Initialize gauge options once
    const pfGaugeOption = {
        series: [{
            type: 'gauge',
            min: 0,
            max: 100,
            progress: { show: true },
            axisLine: {
                lineStyle: {
                    width: 10,
                    color: [
                        [0.8, '#475CA7'], // --state-good
                        [0.9, '#475CA7'], // --state-good
                        [1, '#475CA7']    // --state-good
                    ]
                }
            },
            pointer: { show: true },
            axisTick: { show: false },
            splitLine: { show: false },
            axisLabel: { show: false },
            detail: {
                valueAnimation: true,
                formatter: '{value}%',
                fontSize: 16,
                offsetCenter: [0, '50%'],
                color: '#475CA7' // --data-fg
            },
            data: [{ value: 0 }]
        }]
    };
    gaugeL1PF.setOption(pfGaugeOption);
    gaugeL2PF.setOption(pfGaugeOption);
    gaugeL3PF.setOption(pfGaugeOption);
    gaugeTotalPF.setOption(pfGaugeOption);

    const freqGaugeOption = {
        series: [{
            type: 'gauge',
            min: 55,
            max: 65,
            progress: { show: true },
            axisLine: {
                lineStyle: {
                    width: 10,
                    color: [
                        [1, '#475CA7'] // --state-good only
                    ]
                }
            },
            pointer: { show: true },
            axisTick: { show: false },
            splitLine: { show: false },
            axisLabel: { show: true, distance: 5, color: '#3F3F3F' }, // --label-fg
            detail: {
                valueAnimation: true,
                formatter: '{value} Hz',
                fontSize: 16,
                offsetCenter: [0, '50%'],
                color: '#475CA7' // --data-fg
            },
            data: [{ value: 0 }]
        }]
    };
    frequencyGauge.setOption(freqGaugeOption);
    avgFrequencyGauge.setOption(freqGaugeOption);

    function resizeCharts() {
        voltagesChart.resize();
        currentsChart.resize();
        powerPieChart.resize();
        powerBarChart.resize();
        frequencyGauge.resize();
        avgFrequencyGauge.resize();
        gaugeL1PF.resize();
        gaugeL2PF.resize();
        gaugeL3PF.resize();
        gaugeTotalPF.resize();
    }

    function switchTab(tabName) {
        document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
        document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
        const selectedTab = document.querySelector(`.tab[data-tab="${tabName}"]`);
        const selectedContent = document.getElementById(tabName);
        if (selectedTab && selectedContent) {
            selectedTab.classList.add('active');
            selectedContent.classList.add('active');
            currentTab = tabName;
            localStorage.setItem('activeTab', currentTab);
            resizeCharts();
        }
    }

    switchTab(currentTab);

    document.querySelectorAll('.tab').forEach(tab => {
        tab.addEventListener('click', () => {
            const tabName = tab.getAttribute('data-tab');
            switchTab(tabName);
            updateCharts();
        });
    });

    document.getElementById('toggle-voltages').addEventListener('click', () => {
        chartTypes.voltages = chartTypes.voltages === 'bar' ? 'line' : 'bar';
        document.getElementById('toggle-voltages').textContent = chartTypes.voltages === 'bar' ? 'Switch to Line' : 'Switch to Bar';
        updateVoltagesChart();
    });

    document.getElementById('toggle-currents').addEventListener('click', () => {
        chartTypes.currents = chartTypes.currents === 'bar' ? 'line' : 'bar';
        document.getElementById('toggle-currents').textContent = chartTypes.currents === 'bar' ? 'Switch to Line' : 'Switch to Bar';
        updateCurrentsChart();
    });

    document.getElementById('toggle-power').addEventListener('click', () => {
        chartTypes.power = chartTypes.power === 'bar' ? 'line' : 'bar';
        document.getElementById('toggle-power').textContent = chartTypes.power === 'bar' ? 'Switch to Line' : 'Switch to Bar';
        updatePowerBarChart();
    });

    function fetchData(retryCount = 0) {
        const jsonFile = `${deviceName}_RealTimeVIFPowerData.json`;
        fetch(jsonFile)
            .then(response => {
                if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
                return response.json();
            })
            .then(data => {
                updateDashboard(data);
                document.getElementById('last-updated').textContent = `Last updated: ${new Date(data.lastRefreshTimestamp).toLocaleTimeString()}`;
                statusDot.classList.remove('good', 'error');
                statusDot.classList.add('good');
                if (!dataInterval) dataInterval = setInterval(fetchData, 1000);
            })
            .catch(error => {
                console.error('Error fetching data:', error);
                if (retryCount < maxRetries) {
                    setTimeout(() => fetchData(retryCount + 1), retryDelay);
                } else {
                    statusDot.classList.remove('good');
                    statusDot.classList.add('error');
                    document.getElementById('last-updated').textContent = `Last updated: Failed to load data`;
                }
            });
    }

    function updateDashboard(data) {
        updateSummary(data);
        updateGauges(data);
        updateCharts(data);
        updateFooter(data);
    }

    function updateSummary(data) {
        document.getElementById('metrics-field').innerHTML = `
            <div class="metric-card"><div class="metric-label">Total kW</div><div class="metric-value">${data.Total_kW.toFixed(2)}</div><div class="metric-unit">kW</div></div>
            <div class="metric-card"><div class="metric-label">Total kVAR</div><div class="metric-value">${data.Total_kVAR.toFixed(2)}</div><div class="metric-unit">kVAR</div></div>
            <div class="metric-card"><div class="metric-label">Total kVA</div><div class="metric-value">${data.Total_kVA.toFixed(2)}</div><div class="metric-unit">kVA</div></div>
            <div class="metric-card"><div class="metric-label">Total True PF</div><div class="metric-value">${data.Total_True_PF.toFixed(2)}</div><div class="metric-unit">%</div></div>
            <div class="metric-card"><div class="metric-label">Avg V_L-N</div><div class="metric-value">${data.Avg_V_N_Volts.toFixed(2)}</div><div class="metric-unit">V</div></div>
            <div class="metric-card"><div class="metric-label">Avg V_L-L</div><div class="metric-value">${data.Avg_VL_VL_Volts.toFixed(2)}</div><div class="metric-unit">V</div></div>
            <div class="metric-card"><div class="metric-label">Avg Amps</div><div class="metric-value">${data.Avg_Amps.toFixed(2)}</div><div class="metric-unit">A</div></div>
            <div class="metric-card"><div class="metric-label">Avg Frequency</div><div class="metric-value">${data.Avg_Frequency_Hz.toFixed(2)}</div><div class="metric-unit">Hz</div></div>
        `;
    }

    function updateGauges(data) {
        // Update gauge values
        gaugeL1PF.setOption({ series: [{ data: [{ value: data.L1_True_PF }] }] });
        gaugeL2PF.setOption({ series: [{ data: [{ value: data.L2_True_PF }] }] });
        gaugeL3PF.setOption({ series: [{ data: [{ value: data.L3_True_PF }] }] });
        gaugeTotalPF.setOption({ series: [{ data: [{ value: data.Total_True_PF }] }] });

        // Update lead/lag indicators
        document.getElementById('lead-lag-l1').textContent = data.L1_PF_Lead_Lag_Indicator === 1 ? 'Leading' : 'Lagging';
        document.getElementById('lead-lag-l2').textContent = data.L2_PF_Lead_Lag_Indicator === 1 ? 'Leading' : 'Lagging';
        document.getElementById('lead-lag-l3').textContent = data.L3_PF_Lead_Lag_Indicator === 1 ? 'Leading' : 'Lagging';
        document.getElementById('lead-lag-total').textContent = data.Total_PF_Lead_Lag_Indicator === 1 ? 'Leading' : 'Lagging';

        // Update frequency gauges
        frequencyGauge.setOption({ series: [{ data: [{ value: data.Frequency_Hz }] }] });
        avgFrequencyGauge.setOption({ series: [{ data: [{ value: data.Avg_Frequency_Hz }] }] });
        document.getElementById('freq-value').textContent = data.Frequency_Hz.toFixed(2);
        document.getElementById('avg-freq-value').textContent = data.Avg_Frequency_Hz.toFixed(2);
    }

    function updateCharts(data) {
        updateVoltagesChart(data);
        updateCurrentsChart(data);
        updatePowerCharts(data);
    }

    function updateVoltagesChart(data) {
        if (!data) return;
        const labels = ['V1_N', 'V2_N', 'V3_N', 'VN_G', 'Avg V_N', 'V1_V2', 'V2_V3', 'V3_V1', 'Avg V_L-L'];
        const values = [
            data.V1_N_Volts, data.V2_N_Volts, data.V3_N_Volts, data.VN_G_Volts, data.Avg_V_N_Volts,
            data.V1_V2_Volts, data.V2_V3_Volts, data.V3_V1_Volts, data.Avg_VL_VL_Volts
        ];
        voltagesChart.setOption({
            grid: { left: '5%', right: '5%', bottom: '10%', top: '10%', containLabel: true },
            tooltip: { trigger: 'axis', formatter: '{b}: {c} V' },
            xAxis: { type: 'category', data: labels, axisLabel: { rotate: 45, interval: 0, color: '#3F3F3F' } },
            yAxis: { type: 'value', axisLabel: { formatter: '{value} V', color: '#3F3F3F' } },
            series: [{
                data: values,
                type: chartTypes.voltages,
                name: 'Voltage',
                itemStyle: { color: '#475CA7' },
                areaStyle: chartTypes.voltages === 'line' ? { color: 'rgba(71, 92, 167, 0.3)' } : undefined,
                smooth: chartTypes.voltages === 'line' ? 0.2 : undefined
            }]
        });
        document.querySelector('#voltages-table tbody').innerHTML = labels.map((label, i) => `
            <tr><td>${label}</td><td>${values[i].toFixed(2)}</td></tr>
        `).join('');
    }

    function updateCurrentsChart(data) {
        if (!data) return;
        const labels = ['I1', 'I2', 'I3', 'I4', 'Avg Amps'];
        const values = [data.I1_Amps, data.I2_Amps, data.I3_Amps, data.I4_Amps, data.Avg_Amps];
        currentsChart.setOption({
            grid: { left: '5%', right: '5%', bottom: '10%', top: '10%', containLabel: true },
            tooltip: { trigger: 'axis', formatter: '{b}: {c} A' },
            xAxis: { type: 'category', data: labels, axisLabel: { rotate: 45, interval: 0, color: '#3F3F3F' } },
            yAxis: { type: 'value', axisLabel: { formatter: '{value} A', color: '#3F3F3F' } },
            series: [{
                data: values,
                type: chartTypes.currents,
                name: 'Current',
                itemStyle: { color: '#475CA7' },
                areaStyle: chartTypes.currents === 'line' ? { color: 'rgba(71, 92, 167, 0.3)' } : undefined,
                smooth: chartTypes.currents === 'line' ? 0.2 : undefined
            }]
        });
        document.querySelector('#currents-table tbody').innerHTML = labels.map((label, i) => `
            <tr><td>${label}</td><td>${values[i].toFixed(2)}</td></tr>
        `).join('');
    }

    function updatePowerCharts(data) {
        if (!data) return;
        const pieData = [
            { value: data.L1_kW, name: 'Series-1' },
            { value: data.L2_kW, name: 'Series-2' },
            { value: data.L3_kW, name: 'Series-3' },
            { value: 0, name: 'Series-4' }, // Placeholder for Series-4
            { value: 0, name: 'Series-5' }  // Placeholder for Series-5
        ];
        powerPieChart.setOption({
            tooltip: { trigger: 'item', formatter: '{b}: {c} kW ({d}%)' },
            legend: {
                orient: 'horizontal', // Horizontal legend
                bottom: 0, // Place legend at the bottom
                data: ['Series-1', 'Series-2', 'Series-3', 'Series-4', 'Series-5'],
                textStyle: { color: '#3F3F3F', fontSize: 10 },
                itemHeight: 10,
                itemWidth: 10
            },
            series: [{
                type: 'pie',
                radius: '50%',
                data: pieData,
                label: {
                    show: true,
                    position: 'inside', // Place labels inside the slices
                    formatter: '{d}%', // Show percentage inside
                    fontSize: 12,
                    color: '#000' // Black text for visibility
                },
                itemStyle: {
                    color: params => {
                        const colorList = [
                            '#4682B4', // Steel Blue for Series-1
                            '#3CB371', // Medium Sea Green for Series-2
                            '#FFD700', // Gold for Series-3
                            '#FF6347', // Tomato for Series-4
                            '#9370DB'  // Medium Purple for Series-5
                        ];
                        return colorList[params.dataIndex];
                    }
                },
                emphasis: { itemStyle: { shadowBlur: 10, shadowOffsetX: 0, shadowColor: 'rgba(0, 0, 0, 0.5)' } }
            }]
        });

        const barLabels = ['Total kW', 'Total kVAR', 'Total kVA'];
        const barValues = [data.Total_kW, data.Total_kVAR, data.Total_kVA];
        powerBarChart.setOption({
            grid: { left: '5%', right: '5%', bottom: '10%', top: '10%', containLabel: true },
            tooltip: { trigger: 'axis', formatter: '{b}: {c}' },
            xAxis: { type: 'category', data: barLabels, axisLabel: { rotate: 45, interval: 0, color: '#3F3F3F' } },
            yAxis: { type: 'value', axisLabel: { formatter: '{value}', color: '#3F3F3F' } },
            series: [{
                data: barValues,
                type: chartTypes.power,
                name: 'Power',
                itemStyle: { color: '#475CA7' },
                areaStyle: chartTypes.power === 'line' ? { color: 'rgba(71, 92, 167, 0.3)' } : undefined,
                smooth: chartTypes.power === 'line' ? 0.2 : undefined
            }]
        });

        document.querySelector('#power-table tbody').innerHTML = `
            <tr><td>L1 kW</td><td>${data.L1_kW.toFixed(2)}</td></tr>
            <tr><td>L2 kW</td><td>${data.L2_kW.toFixed(2)}</td></tr>
            <tr><td>L3 kW</td><td>${data.L3_kW.toFixed(2)}</td></tr>
            <tr><td>Total kW</td><td>${data.Total_kW.toFixed(2)}</td></tr>
            <tr><td>L1 kVAR</td><td>${data.L1_kVAR.toFixed(2)}</td></tr>
            <tr><td>L2 kVAR</td><td>${data.L2_kVAR.toFixed(2)}</td></tr>
            <tr><td>L3 kVAR</td><td>${data.L3_kVAR.toFixed(2)}</td></tr>
            <tr><td>Total kVAR</td><td>${data.Total_kVAR.toFixed(2)}</td></tr>
            <tr><td>L1 kVA</td><td>${data.L1_kVA.toFixed(2)}</td></tr>
            <tr><td>L2 kVA</td><td>${data.L2_kVA.toFixed(2)}</td></tr>
            <tr><td>L3 kVA</td><td>${data.L3_kVA.toFixed(2)}</td></tr>
            <tr><td>Total kVA</td><td>${data.Total_kVA.toFixed(2)}</td></tr>
        `;
    }

    function updateFooter(data) {
        if (!data) return;
        document.getElementById('footer').innerHTML = `
            Metering Date: ${data.Metering_Date_Stamp} | 
            Time: ${data.Metering_Time_Stamp} | 
            Microseconds: ${data.Metering_Microsecond_Stamp} | 
            Iteration: ${data.Metering_Iteration} | 
            Voltage Rotation: ${data.Voltage_Rotation}
        `;
    }

    window.addEventListener('resize', resizeCharts);
    setTimeout(() => fetchData(), 100);
});