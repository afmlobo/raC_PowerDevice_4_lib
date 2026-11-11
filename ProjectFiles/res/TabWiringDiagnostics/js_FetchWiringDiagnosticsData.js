document.addEventListener('DOMContentLoaded', function () {
    const deviceName = document.body.getAttribute('data-device-name');
    if (!deviceName) {
        console.error('deviceName is not defined');
        document.body.innerHTML = '<h1>Error: Device name is not defined</h1>';
        return;
    }

    var dataInterval = null;
    var maxRetries = 5;
    var retryDelay = 1000;
    var lastData = null;

    var phasorChart = echarts.init(document.getElementById('phasor-chart'), null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });
    var statusDot = document.getElementById('status-dot');

    function resizeCharts() {
        phasorChart.resize();
    }

    function fetchData(retryCount = 0, isInitialLoad = false) {
        const jsonFile = `${deviceName}_WiringDiagnosticsData.json`;
        if (isInitialLoad || retryCount > 0) phasorChart.showLoading();

        fetch(jsonFile)
            .then(response => {
                if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
                return response.json();
            })
            .then(data => {
                if (lastData && JSON.stringify(data) === JSON.stringify(lastData)) {
                    document.getElementById('last-updated').textContent = `Last updated: ${new Date(data.lastRefreshTimestamp).toLocaleTimeString()}`;
                    if (isInitialLoad || retryCount > 0) phasorChart.hideLoading();
                    return;
                }

                lastData = data;
                updateDashboard(data);
                document.getElementById('last-updated').textContent = `Last updated: ${new Date(data.lastRefreshTimestamp).toLocaleTimeString()}`;
                statusDot.classList.remove('good', 'error');
                statusDot.classList.add('good');
                if (isInitialLoad || retryCount > 0) phasorChart.hideLoading();

                if (!dataInterval) dataInterval = setInterval(() => fetchData(0, false), 5000);
            })
            .catch(error => {
                console.error('Error fetching data:', error);
                if (retryCount < maxRetries) {
                    setTimeout(() => fetchData(retryCount + 1, true), retryDelay);
                } else {
                    statusDot.classList.remove('good');
                    statusDot.classList.add('error');
                    document.getElementById('last-updated').textContent = `Last updated: Failed to load data`;
                    phasorChart.hideLoading();
                }
            });
    }

    function updateDashboard(data) {
        updateWiringTests(data);
        updateWiringCorrections(data);
        updatePhasorDiagram(data);
        updatePhasorData(data);
    }

    function getCommandStatusText(value) {
        switch (value) {
            case 0: return { text: 'Command Active', icon: '../Sts_StateRunning.svg' };
            case 1: return { text: 'Input Level Low' };
            case 2: return { text: 'Disabled' };
            case 3: return { text: 'Waiting Command' };
            default: return { text: 'Unknown' };
        }
    }

    function getInputMissingText(value) {
        switch (value) {
            case -1: return { text: 'Test not run', icon: '../Sts_NotReady_16.svg' };
            case 0: return { text: 'Test passed', icon: '../Sts_StateRunning.svg' };
            case 1: return { text: 'Phase 1 missing', icon: '../Faulted_16px.svg' };
            case 2: return { text: 'Phase 2 missing', icon: '../Faulted_16px.svg' };
            case 3: return { text: 'Phase 3 missing', icon: '../Faulted_16px.svg' };
            case 12: return { text: 'Phase 1 and 2 missing', icon: '../Faulted_16px.svg' };
            case 13: return { text: 'Phase 1 and 3 missing', icon: '../Faulted_16px.svg' };
            case 23: return { text: 'Phase 2 and 3 missing', icon: '../Faulted_16px.svg' };
            case 123: return { text: 'All phases missing', icon: '../Faulted_16px.svg' };
            default: return { text: 'Unknown', icon: '../Sts_NotReady_16.svg' };
        }
    }

    function getRangeStatusText(value) {
        switch (value) {
            case 0: return { text: 'Pass', icon: '../Sts_StateRunning.svg' };
            case 1: return { text: 'Fail', icon: '../Faulted_16px.svg' };
            default: return { text: 'Unknown', icon: '../Sts_NotReady_16.svg' };
        }
    }

    function getInputInvertedText(value) {
        switch (value) {
            case -1: return { text: 'Test not run', icon: '../Sts_NotReady_16.svg' };
            case 0: return { text: 'Test passed', icon: '../Sts_StateRunning.svg' };
            case 1: return { text: 'Phase 1 inverted', icon: '../Faulted_16px.svg' };
            case 2: return { text: 'Phase 2 inverted', icon: '../Faulted_16px.svg' };
            case 3: return { text: 'Phase 3 inverted', icon: '../Faulted_16px.svg' };
            case 12: return { text: 'Phase 1 and 2 inverted', icon: '../Faulted_16px.svg' };
            case 13: return { text: 'Phase 1 and 3 inverted', icon: '../Faulted_16px.svg' };
            case 23: return { text: 'Phase 2 and 3 inverted', icon: '../Faulted_16px.svg' };
            case 123: return { text: 'All phases inverted', icon: '../Faulted_16px.svg' };
            default: return { text: 'Unknown', icon: '../Sts_NotReady_16.svg' };
        }
    }

    function getRotationText(value) {
        switch (value) {
            case -1: return { text: 'Test not run', icon: '../Sts_NotReady_16.svg' };
            case 4: return { text: 'Invalid Rotation', icon: '../Faulted_16px.svg' };
            case 5: return { text: 'Out of range', icon: '../Faulted_16px.svg' };
            default:
                if (typeof value === 'string' && /^[1-3]{3}$/.test(value)) {
                    return { 
                        text: `Phase order: ${value.split('').join(' -> ')}`, 
                        icon: value === '123' ? '../Sts_StateRunning.svg' : '../Faulted_16px.svg' 
                    };
                }
                return { text: 'Unknown', icon: '../Sts_NotReady_16.svg' };
        }
    }

    function updateWiringTests(data) {
        const statusWithIcon = (statusObj) => 
            statusObj.icon 
                ? `<img src="${statusObj.icon}" alt="${statusObj.text}"> ${statusObj.text}` 
                : statusObj.text;

        document.getElementById('wiring-tests').innerHTML = `
            <!-- Row 1: Command Status (spans full width) -->
            <div class="metric-item full-width">
                <div class="metric-label">Command Status</div>
                <div class="metric-status">${statusWithIcon(getCommandStatusText(data.Command_Status))}</div>
            </div>
            <!-- Row 2: Current Input Missing | Voltage Input Missing -->
            <div class="two-column-row">
                <div class="metric-item">
                    <div class="metric-label">Current Input Missing</div>
                    <div class="metric-status">${statusWithIcon(getInputMissingText(data.Current_Input_Missing))}</div>
                </div>
                <div class="metric-item">
                    <div class="metric-label">Voltage Input Missing</div>
                    <div class="metric-status">${statusWithIcon(getInputMissingText(data.Voltage_Input_Missing))}</div>
                </div>
            </div>
            <!-- Row 3: L97 to C89 (Range 1) | L85 to C98 (Range 2) | L52 to L95 (Range 3) -->
            <div class="metric-item third-width">
                <div class="metric-label">L97 to C89 (Range 1)</div>
                <div class="metric-status">${statusWithIcon(getRangeStatusText(data.Range1_L97_C89_Status))}</div>
            </div>
            <div class="metric-item third-width">
                <div class="metric-label">L85 to C98 (Range 2)</div>
                <div class="metric-status">${statusWithIcon(getRangeStatusText(data.Range2_L85_C98_Status))}</div>
            </div>
            <div class="metric-item third-width">
                <div class="metric-label">L52 to L95 (Range 3)</div>
                <div class="metric-status">${statusWithIcon(getRangeStatusText(data.Range3_L52_L95_Status))}</div>
            </div>
            <!-- Row 4: Voltage Input Inverted (Range 1) | Current Input Inverted (Range 1) -->
            <div class="two-column-row">
                <div class="metric-item">
                    <div class="metric-label">Voltage Input Inverted (Range 1)</div>
                    <div class="metric-status">${statusWithIcon(getInputInvertedText(data.Range1_Voltage_Input_Inverted))}</div>
                </div>
                <div class="metric-item">
                    <div class="metric-label">Current Input Inverted (Range 1)</div>
                    <div class="metric-status">${statusWithIcon(getInputInvertedText(data.Range1_Current_Input_Inverted))}</div>
                </div>
            </div>
            <!-- Row 5: Voltage Rotation (Range 1) | Current Rotation (Range 1) -->
            <div class="two-column-row">
                <div class="metric-item">
                    <div class="metric-label">Voltage Rotation (Range 1)</div>
                    <div class="metric-status">${statusWithIcon(getRotationText(data.Range1_Voltage_Rotation))}</div>
                </div>
                <div class="metric-item">
                    <div class="metric-label">Current Rotation (Range 1)</div>
                    <div class="metric-status">${statusWithIcon(getRotationText(data.Range1_Current_Rotation))}</div>
                </div>
            </div>
        `;
    }

    function updateWiringCorrections(data) {
        document.getElementById('wiring-corrections').innerHTML = `
            <div class="correction-item">
                <div class="correction-label">Last Command</div>
                <div class="correction-value">No Command</div>
            </div>
            <div class="correction-item">
                <div class="correction-label">Last Command Status</div>
                <div class="correction-value">Rejected see rejection status</div>
            </div>
            <div class="correction-item">
                <div class="correction-label">Rejection Information</div>
                <div class="correction-value">Selected range is incomplete</div>
            </div>
        `;
        document.getElementById('input-mappings').innerHTML = `
            <div class="mapping-item">
                <div class="mapping-label">Input V1 Mapping</div>
                <div class="mapping-value">V1</div>
            </div>
            <div class="mapping-item">
                <div class="mapping-label">Input I1 Mapping</div>
                <div class="mapping-value">I1</div>
            </div>
            <div class="mapping-item">
                <div class="mapping-label">Input V2 Mapping</div>
                <div class="mapping-value">V2</div>
            </div>
            <div class="mapping-item">
                <div class="mapping-label">Input I2 Mapping</div>
                <div class="mapping-value">I2</div>
            </div>
            <div class="mapping-item">
                <div class="mapping-label">Input V3 Mapping</div>
                <div class="mapping-value">V3</div>
            </div>
            <div class="mapping-item">
                <div class="mapping-label">Input I3 Mapping</div>
                <div class="mapping-value">I3</div>
            </div>
        `;
    }

    function updatePhasorDiagram(data) {
        const fixedLength = 100;

        const option = {
            polar: {},
            radiusAxis: {
                min: 0,
                max: fixedLength * 1.2,
                axisLabel: { show: false },
                axisLine: { show: true, lineStyle: { color: '#D8D8D8' } },
                axisTick: { show: false },
                splitLine: { show: true, lineStyle: { color: '#D8D8D8' } }
            },
            angleAxis: {
                type: 'value',
                min: 0,
                max: 360,
                interval: 30,
                clockwise: false,
                axisLabel: { formatter: '{value}°', color: '#3F3F3F' }
            },
            series: [
                { 
                    type: 'line', 
                    coordinateSystem: 'polar', 
                    name: 'V1', 
                    data: [[fixedLength, data.Voltage_Phase_1_Angle], [0, 0]], 
                    itemStyle: { color: '#FF5733' },
                    lineStyle: { width: 3 } 
                },
                { 
                    type: 'line', 
                    coordinateSystem: 'polar', 
                    name: 'I1', 
                    data: [[fixedLength, data.Current_Phase_1_Angle], [0, 0]], 
                    itemStyle: { color: '#FF5733' },
                    lineStyle: { width: 3, type: 'dashed' } 
                },
                { 
                    type: 'line', 
                    coordinateSystem: 'polar', 
                    name: 'V2', 
                    data: [[fixedLength, data.Voltage_Phase_2_Angle], [0, 0]], 
                    itemStyle: { color: '#33FF57' },
                    lineStyle: { width: 3 } 
                },
                { 
                    type: 'line', 
                    coordinateSystem: 'polar', 
                    name: 'I2', 
                    data: [[fixedLength, data.Current_Phase_2_Angle], [0, 0]], 
                    itemStyle: { color: '#33FF57' },
                    lineStyle: { width: 3, type: 'dashed' } 
                },
                { 
                    type: 'line', 
                    coordinateSystem: 'polar', 
                    name: 'V3', 
                    data: [[fixedLength, data.Voltage_Phase_3_Angle], [0, 0]], 
                    itemStyle: { color: '#3357FF' },
                    lineStyle: { width: 3 } 
                },
                { 
                    type: 'line', 
                    coordinateSystem: 'polar', 
                    name: 'I3', 
                    data: [[fixedLength, data.Current_Phase_3_Angle], [0, 0]], 
                    itemStyle: { color: '#3357FF' },
                    lineStyle: { width: 3, type: 'dashed' } 
                }
            ],
            legend: {
                show: true,
                top: 0,
                orient: 'horizontal',
                textStyle: { fontSize: 10, color: '#3F3F3F' },
                padding: [10, 0, 0, 0]
            },
            tooltip: {
                show: true,
                formatter: function (params) {
                    return `${params.seriesName}: Angle ${params.data[1].toFixed(2)}°`;
                }
            }
        };

        phasorChart.setOption(option, { notMerge: false });
    }

    function updatePhasorData(data) {
        document.getElementById('phasor-data').innerHTML = `
            <div class="phasor-item">
                <div class="phasor-label">V1</div>
                <div class="phasor-value">${data.Voltage_Phase_1_Magnitude.toFixed(3)} V</div>
                <div class="phasor-value">${data.Voltage_Phase_1_Angle.toFixed(3)}°</div>
            </div>
            <div class="phasor-item">
                <div class="phasor-label">V2</div>
                <div class="phasor-value">${data.Voltage_Phase_2_Magnitude.toFixed(3)} V</div>
                <div class="phasor-value">${data.Voltage_Phase_2_Angle.toFixed(3)}°</div>
            </div>
            <div class="phasor-item">
                <div class="phasor-label">V3</div>
                <div class="phasor-value">${data.Voltage_Phase_3_Magnitude.toFixed(3)} V</div>
                <div class="phasor-value">${data.Voltage_Phase_3_Angle.toFixed(3)}°</div>
            </div>
            <div class="phasor-item">
                <div class="phasor-label">I1</div>
                <div class="phasor-value">${data.Current_Phase_1_Magnitude.toFixed(3)} A</div>
                <div class="phasor-value">${data.Current_Phase_1_Angle.toFixed(3)}°</div>
            </div>
            <div class="phasor-item">
                <div class="phasor-label">I2</div>
                <div class="phasor-value">${data.Current_Phase_2_Magnitude.toFixed(3)} A</div>
                <div class="phasor-value">${data.Current_Phase_2_Angle.toFixed(3)}°</div>
            </div>
            <div class="phasor-item">
                <div class="phasor-label">I3</div>
                <div class="phasor-value">${data.Current_Phase_3_Magnitude.toFixed(3)} A</div>
                <div class="phasor-value">${data.Current_Phase_3_Angle.toFixed(3)}°</div>
            </div>
        `;
    }

    window.addEventListener('resize', resizeCharts);
    setTimeout(() => fetchData(0, true), 1000);
});