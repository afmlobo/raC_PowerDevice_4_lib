document.addEventListener('DOMContentLoaded', function() {
    if (!document.body) {
        console.error('Document body is null');
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) loadingDiv.textContent = 'Error: Document body not found';
        return;
    }

    const deviceName = document.body.getAttribute('data-device-name') || 'PM5000_PUNE';
    const loadingDiv = document.getElementById('loading');
    const statsContainer = document.getElementById('eventStats');
    const tableBody = document.getElementById('eventTable');
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

    fetch(`${deviceName}_EventLogData.json?t=${Date.now()}`)
        .then(response => {
            if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
            return response.json();
        })
        .then(data => {
            const eventData = data;
            if (!eventData.events || eventData.events.length === 0) {
                loadingDiv.textContent = 'No data available';
                return;
            }

            function updateStats() {
                const events = eventData.events;

                // Group events by Event Type
                const eventCounts = events.reduce((acc, event) => {
                    acc[event.eventType] = (acc[event.eventType] || 0) + 1;
                    return acc;
                }, {});

                const eventTypes = Object.keys(eventCounts);

                // Clear existing tiles
                statsContainer.innerHTML = '';

                // Create a tile for each event type
                eventTypes.forEach(eventType => {
                    const tile = document.createElement('div');
                    tile.className = 'stat-card';

                    const title = document.createElement('h3');
                    title.textContent = eventType;

                    const value = document.createElement('div');
                    value.className = 'stat-value';
                    value.textContent = eventCounts[eventType];

                    tile.appendChild(title);
                    tile.appendChild(value);
                    statsContainer.appendChild(tile);
                });
            }

            function updateBarChart() {
                const events = eventData.events;

                // Group events by Event Type
                const eventCounts = events.reduce((acc, event) => {
                    acc[event.eventType] = (acc[event.eventType] || 0) + 1;
                    return acc;
                }, {});

                const eventTypes = Object.keys(eventCounts);
                const eventCountsData = eventTypes.map(eventType => eventCounts[eventType]);

                const option = {
                    title: {
                        text: 'Event Count by Type',
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
                    xAxis: {
                        type: 'category',
                        data: eventTypes,
                        axisLine: { lineStyle: { color: '#D8D8D8' } },
                        axisLabel: {
                            rotate: 45,
                            fontSize: 12,
                            color: '#3F3F3F'
                        }
                    },
                    yAxis: {
                        type: 'value',
                        name: 'Event Count',
                        nameTextStyle: { color: '#3F3F3F' },
                        axisLabel: { color: '#3F3F3F' },
                        axisLine: { lineStyle: { color: '#D8D8D8' } },
                        splitLine: { lineStyle: { color: '#D8D8D8' } }
                    },
                    series: [{
                        name: 'Event Count',
                        type: 'bar',
                        data: eventCountsData,
                        itemStyle: {
                            color: '#475CA7'
                        }
                    }]
                };

                barChart.setOption(option);
            }

            function updateTable() {
                tableBody.innerHTML = eventData.events.map(event => `
                    <tr>
                        <td>${event.id}</td>
                        <td>${event.timestamp}</td>
                        <td>${event.eventType}</td>
                        <td>${event.generalCode}</td>
                        <td>${event.infoCode}</td>
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
            loadingDiv.textContent = 'Data Not Loaded';
        });
});