document.addEventListener('DOMContentLoaded', function () {
    if (!document.body) {
        console.error('Document body is null');
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) loadingDiv.innerHTML = '\nError: Document body not found\n';
        return;
    }

    const deviceName = document.body.getAttribute('data-device-name');
    const chartContainer = document.getElementById('chartContainer');
    const loadingDiv = document.getElementById('loading');

    // Metadata card elements
    const fileNameEl = document.getElementById('fileName');
    const captureDateEl = document.getElementById('captureDate');
    const captureTimeEl = document.getElementById('captureTime');
    const eventTypeEl = document.getElementById('eventType');
    const totalCyclesEl = document.getElementById('totalCycles');
    const segmentCyclesEl = document.getElementById('segmentCycles');
    const maxVoltageEl = document.getElementById('maxVoltage');
    const maxCurrentEl = document.getElementById('maxCurrent');
    const frequencyEl = document.getElementById('frequency');
    const fileSizeEl = document.getElementById('fileSize');
    const archivedDateEl = document.getElementById('archivedDate');
    const chartFilenameEl = document.getElementById('chartFilename');

    if (!chartContainer || !loadingDiv) {
        console.error('Required DOM elements missing');
        if (loadingDiv) loadingDiv.innerHTML = '\nError: Dashboard initialization failed\n';
        return;
    }

    fetch(`${deviceName}_WaveformViewerData.json?t=${Date.now()}`)
        .then(response => {
            if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
            return response.json();
        })
        .then(waveformData => {
            if (!waveformData || !Array.isArray(waveformData.headers) || !Array.isArray(waveformData.rows) || waveformData.headers.length < 9) {
                loadingDiv.innerHTML = '\nNo valid waveform data available\n';
                console.error('Invalid or empty waveform data:', waveformData);
                return;
            }

            const metadata = waveformData.metadata || {};

            // Populate metadata cards
            if (fileNameEl) fileNameEl.textContent = metadata.databaseFileName || waveformData.fileName || 'Unknown';
            if (captureDateEl) captureDateEl.textContent = metadata.databaseCaptureDate || 'N/A';
            if (captureTimeEl) captureTimeEl.textContent = metadata.databaseCaptureTime || metadata.captureTimestamp || 'N/A';
            if (eventTypeEl) {
                const eventType = metadata.databaseEventType || 'N/A';
                eventTypeEl.textContent = eventType;
                const eventCard = eventTypeEl.closest('.metadata-card');
                if (eventCard) {
                    if (eventType.toLowerCase().includes('transient')) {
                        eventCard.style.borderLeftColor = '#ff6b6b';
                        eventCard.style.borderLeftWidth = '4px';
                    } else if (eventType.toLowerCase().includes('surge')) {
                        eventCard.style.borderLeftColor = '#ffa500';
                        eventCard.style.borderLeftWidth = '4px';
                    } else if (eventType.toLowerCase().includes('sag') || eventType.toLowerCase().includes('swell')) {
                        eventCard.style.borderLeftColor = '#4ecdc4';
                        eventCard.style.borderLeftWidth = '4px';
                    }
                }
            }
            if (totalCyclesEl) totalCyclesEl.textContent = metadata.totalCycles || metadata.databaseCycles || '0';
            if (segmentCyclesEl) segmentCyclesEl.textContent = `${metadata.segmentCycles || '0'} cycles per segment`;
            if (maxVoltageEl) {
                const voltage = metadata.databaseMaxVoltage || parseFloat(metadata.maxVoltage) || 0;
                maxVoltageEl.textContent = `${voltage.toFixed(2)} V`;
            }
            if (maxCurrentEl) {
                const current = metadata.databaseMaxCurrent || parseFloat(metadata.maxCurrent) || 0;
                maxCurrentEl.textContent = `${current.toFixed(2)} A`;
            }
            if (frequencyEl) frequencyEl.textContent = metadata.frequency || 'N/A';
            if (fileSizeEl) {
                const sizeKB = metadata.databaseFileSize || 0;
                if (sizeKB > 1024) {
                    fileSizeEl.textContent = `${(sizeKB / 1024).toFixed(2)} MB`;
                } else {
                    fileSizeEl.textContent = `${sizeKB} KB`;
                }
            }
            if (archivedDateEl) archivedDateEl.textContent = metadata.databaseArchivedDate || 'N/A';
            if (chartFilenameEl) {
                chartFilenameEl.textContent = `${metadata.databaseFileName || waveformData.fileName || 'Unknown'} - ${metadata.databaseCaptureDate || ''} ${metadata.databaseCaptureTime || ''}`;
            }

            // Initialize ECharts
            const waveformChart = echarts.init(chartContainer, null, { renderer: 'canvas', devicePixelRatio: window.devicePixelRatio });

            // Prepare data
            const labels = waveformData.rows.map(row => row[0]); // Time (ms)
            const vaData = waveformData.rows.map(row => parseFloat(row[1]));
            const vbData = waveformData.rows.map(row => parseFloat(row[2]));
            const vcData = waveformData.rows.map(row => parseFloat(row[3]));
            const vnData = waveformData.rows.map(row => parseFloat(row[4]));
            const iaData = waveformData.rows.map(row => parseFloat(row[5]));
            const ibData = waveformData.rows.map(row => parseFloat(row[6]));
            const icData = waveformData.rows.map(row => parseFloat(row[7]));
            const inData = waveformData.rows.map(row => parseFloat(row[8]));

            const maxV = Math.max(...vaData, ...vbData, ...vcData, ...vnData, 1);
            const maxI = Math.max(...iaData, ...ibData, ...icData, ...inData, 1);
            const vScale = maxV * 1.1;
            const iScale = maxI * 1.1;

            // ECharts configuration with color fix
            const option = {
                color: ['#FF5733', '#33FF57', '#3357FF', '#9966FF', '#FF5733', '#33FF57', '#3357FF', '#9966FF'],
                animation: true,
                tooltip: {
                    trigger: 'axis',
                    backgroundColor: 'rgba(50, 50, 50, 0.95)',
                    borderColor: '#777',
                    borderWidth: 1,
                    textStyle: { color: '#fff' },
                    formatter: function (params) {
                        const timestamp = params[0].name;
                        let result = `\nTime: ${timestamp} ms\n`;
                        params.forEach(param => {
                            const unit = param.seriesName.startsWith('V') ? 'V' : 'A';
                            result += `\n ${param.seriesName}: ${param.value.toFixed(3)} ${unit} \n`;
                        });
                        return result;
                    }
                },
                legend: {
                    data: ['Va', 'Vb', 'Vc', 'Vn', 'Ia', 'Ib', 'Ic', 'In'],
                    top: '2%',
                    textStyle: { fontSize: 11, color: '#3F3F3F' },
                    padding: [10, 0, 0, 0],
                    selectedMode: 'multiple',
                    itemGap: 16,
                    icon: 'roundRect'
                },
                grid: { left: '3%', right: '4%', bottom: '15%', top: '12%', containLabel: true },
                dataZoom: [
                    { type: 'inside', xAxisIndex: 0, start: 0, end: 100, zoomOnMouseWheel: true, moveOnMouseWheel: false, zoomLock: false },
                    { type: 'slider', xAxisIndex: 0, start: 0, end: 100, height: 35, bottom: 10, zoomOnMouseWheel: false, moveOnMouseMove: true, brushSelect: false, handleSize: '120%', handleStyle: { color: '#5470c6', borderColor: '#5470c6' } }
                ],
                xAxis: { type: 'category', data: labels, axisLabel: { show: false }, axisTick: { show: false } },
                yAxis: [
                    {
                        type: 'value',
                        name: 'Voltage (V)',
                        position: 'left',
                        min: -vScale,
                        max: vScale,
                        nameTextStyle: { color: '#3F3F3F', fontWeight: 'bold', fontSize: 12 },
                        axisLabel: { formatter: value => value.toFixed(0) + ' V', color: '#3F3F3F', fontSize: 11 },
                        splitLine: { lineStyle: { type: 'dashed', opacity: 0.3 } }
                    },
                    {
                        type: 'value',
                        name: 'Current (A)',
                        position: 'right',
                        min: -iScale,
                        max: iScale,
                        nameTextStyle: { color: '#3F3F3F', fontWeight: 'bold', fontSize: 12 },
                        axisLabel: { formatter: value => value.toFixed(0) + ' A', color: '#3F3F3F', fontSize: 11 },
                        splitLine: { show: false }
                    }
                ],
                series: [
                    { name: 'Va', type: 'line', yAxisIndex: 0, data: vaData, smooth: true, showSymbol: false, lineStyle: { color: '#FF5733', width: 1 } },
                    { name: 'Vb', type: 'line', yAxisIndex: 0, data: vbData, smooth: true, showSymbol: false, lineStyle: { color: '#33FF57', width: 1 } },
                    { name: 'Vc', type: 'line', yAxisIndex: 0, data: vcData, smooth: true, showSymbol: false, lineStyle: { color: '#3357FF', width: 1 } },
                    { name: 'Vn', type: 'line', yAxisIndex: 0, data: vnData, smooth: true, showSymbol: false, lineStyle: { color: '#9966FF', width: 1 } },
                    { name: 'Ia', type: 'line', yAxisIndex: 1, data: iaData, smooth: true, showSymbol: false, lineStyle: { color: '#FF5733', width: 1, type: 'dashed' } },
                    { name: 'Ib', type: 'line', yAxisIndex: 1, data: ibData, smooth: true, showSymbol: false, lineStyle: { color: '#33FF57', width: 1, type: 'dashed' } },
                    { name: 'Ic', type: 'line', yAxisIndex: 1, data: icData, smooth: true, showSymbol: false, lineStyle: { color: '#3357FF', width: 1, type: 'dashed' } },
                    { name: 'In', type: 'line', yAxisIndex: 1, data: inData, smooth: true, showSymbol: false, lineStyle: { color: '#9966FF', width: 1, type: 'dashed' } }
                ]
            };

            waveformChart.setOption(option);

            waveformChart.on('dataZoom', function (params) {
                console.log('Zoom event:', params);
            });

            window.addEventListener('resize', function () {
                waveformChart.resize();
            });

            loadingDiv.style.display = 'none';
            console.log('Waveform viewer initialized successfully');
        })
        .catch(error => {
            console.error('Error fetching waveform data:', error);
            loadingDiv.innerHTML = '\nError loading waveform data\n';
        });
});
``