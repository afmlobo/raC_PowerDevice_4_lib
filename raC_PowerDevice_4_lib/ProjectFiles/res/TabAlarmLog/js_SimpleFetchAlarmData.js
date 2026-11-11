document.addEventListener('DOMContentLoaded', function() {
    if (!document.body) {
        console.error('Document body is null');
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) loadingDiv.textContent = 'Error: Document body not found';
        return;
    }

    const deviceName = document.body.getAttribute('data-device-name');
    const loadingDiv = document.getElementById('loading');
    const statsContainer = document.getElementById('alarmStats');
    const tableBody = document.getElementById('alarmTable');
    const barChartDiv = document.getElementById('barChart');

    if (!loadingDiv || !statsContainer || !tableBody || !barChartDiv) {
        console.error('Required DOM elements missing:', { 
            loadingDiv: !!loadingDiv, 
            statsContainer: !!statsContainer, 
            tableBody: !!tableBody, 
            barChartDiv: !!barChartDiv 
        });
        if (loadingDiv) loadingDiv.textContent = 'Error: Dashboard initialization failed';
        return;
    }

    const barChart = echarts.init(barChartDiv);

    fetch(`${deviceName}_AlarmLogData.json?t=${Date.now()}`)
        .then(response => {
            if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
            return response.json();
        })
        .then(data => {
            const alarmData = data;
            if (!alarmData.alarms || alarmData.alarms.length === 0) {
                loadingDiv.textContent = 'No data available';
                return;
            }

            function updateStats() {
                const alarms = alarmData.alarms;

                // Group alarms by Alarm Type
                const alarmCounts = alarms.reduce((acc, alarm) => {
                    acc[alarm.alarmTypeDescription] = (acc[alarm.alarmTypeDescription] || 0) + 1;
                    return acc;
                }, {});

                const alarmTypes = Object.keys(alarmCounts);

                // Clear existing tiles
                statsContainer.innerHTML = '';

                // Create a tile for each alarm type
                alarmTypes.forEach(alarmType => {
                    const tile = document.createElement('div');
                    tile.className = 'stat-card';

                    const title = document.createElement('h3');
                    title.textContent = alarmType;

                    const value = document.createElement('div');
                    value.className = 'stat-value';
                    value.textContent = alarmCounts[alarmType];

                    tile.appendChild(title);
                    tile.appendChild(value);
                    statsContainer.appendChild(tile);
                });
            }

            function updateBarChart() {
                const alarms = alarmData.alarms;

                // Group alarms by Alarm Type
                const alarmCounts = alarms.reduce((acc, alarm) => {
                    acc[alarm.alarmTypeDescription] = (acc[alarm.alarmTypeDescription] || 0) + 1;
                    return acc;
                }, {});

                const alarmTypes = Object.keys(alarmCounts);
                const alarmCountsData = alarmTypes.map(alarmType => alarmCounts[alarmType]);

                const option = {
                    title: {
                        text: 'Alarm Count by Type',
                        left: 'center',
                        textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
                    },
                    tooltip: {
                        trigger: 'axis',
                        backgroundColor: 'rgba(255, 255, 255, 0.9)',
                        borderColor: '#D8D8D8',
                        borderWidth: 1,
                        padding: [5, 10],
                        textStyle: { color: '#3F3F3F', fontSize: 12 },
                        confine: true,
                        formatter: function (params) {
                            let tooltipContent = params[0].axisValueLabel + '<br/>';
                            params.forEach(param => {
                                tooltipContent += `${param.marker} ${param.seriesName}: <strong>${param.value}</strong><br/>`;
                            });
                            return tooltipContent;
                        }
                    },
                    grid: {
                        left: '25%',
                        right: '10%',
                        top: '15%',
                        bottom: '10%'
                    },
                    xAxis: {
                        type: 'value',
                        name: 'Alarm Count',
                        nameTextStyle: { color: '#3F3F3F' },
                        axisLabel: { color: '#3F3F3F' },
                        axisLine: { lineStyle: { color: '#D8D8D8' } },
                        splitLine: { lineStyle: { color: '#D8D8D8' } }
                    },
                    yAxis: {
                        type: 'category',
                        data: alarmTypes,
                        axisLine: { lineStyle: { color: '#D8D8D8' } },
                        axisLabel: {
                            fontSize: 12,
                            color: '#3F3F3F'
                        }
                    },
                    series: [{
                        name: 'Alarm Count',
                        type: 'bar',
                        data: alarmCountsData,
                        itemStyle: {
                            color: '#475CA7'
                        }
                    }]
                };

                barChart.setOption(option);
            }

            function updateTable() {
                tableBody.innerHTML = alarmData.alarms.map(alarm => `
                    <tr>
                        <td>${alarm.id}</td>
                        <td>${alarm.timestamp}</td>
                        <td>${alarm.alarmTypeDescription}</td>
                        <td>${alarm.alarmCode}</td>
                        <td>${alarm.alarmCodeDescription}</td>
                    </tr>
                `).join('');
            }

            function updateDashboard() {
                updateStats();
                updateBarChart();
                updateTable();
            }

            // Initial update
            updateDashboard();

            // Handle window resize
            window.addEventListener('resize', () => {
                barChart.resize();
            });

            loadingDiv.style.display = 'none';
        })
        .catch(error => {
            console.error('Error fetching data:', error);
            loadingDiv.textContent = 'Data not loaded';
        });
});
