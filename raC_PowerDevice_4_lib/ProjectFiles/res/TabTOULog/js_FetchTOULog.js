document.addEventListener('DOMContentLoaded', function() {
    if (!document.body) {
        console.error('Document body is null');
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) loadingDiv.textContent = 'Error: Document body not found';
        return;
    }

    const deviceName = document.body.getAttribute('data-device-name');
    const loadingDiv = document.getElementById('loading');
    const tableBody = document.querySelector('#eventTable tbody');
    const chartDivs = [
        'lineChart1', 'lineChart2', 'lineChart3', 'lineChart4',
        'lineChart5', 'lineChart6', 'lineChart7', 'lineChart8', 'lineChart9'
    ].map(id => document.getElementById(id));

    if (!loadingDiv || !tableBody || chartDivs.some(div => !div)) {
        console.error('Required DOM elements missing:', {
            loadingDiv: !!loadingDiv,
            tableBody: !!tableBody,
            chartDivs: chartDivs.map((div, i) => ({ id: `lineChart${i+1}`, found: !!div }))
        });
        if (loadingDiv) loadingDiv.textContent = 'Error: Dashboard initialization failed';
        return;
    }

    const charts = chartDivs.map(div => echarts.init(div));
    let rawData = [];

    function populateTable(data) {
        tableBody.innerHTML = data.map(event => `
            <tr>
                <td>${event.Record_Number}</td>
                <td>${event.Start_Date}</td>
                <td>${event.End_Date}</td>
                <td>${event.Off_Peak_GWh_Net || ''}</td>
                <td>${event.Off_Peak_kWh_Net || ''}</td>
                <td>${event.Off_Peak_kW_Demand || ''}</td>
                <td>${event.Mid_Peak_GWh_Net || ''}</td>
                <td>${event.Mid_Peak_kWh_Net || ''}</td>
                <td>${event.Mid_Peak_kW_Demand || ''}</td>
                <td>${event.On_Peak_GWh_Net || ''}</td>
                <td>${event.On_Peak_kWh_Net || ''}</td>
                <td>${event.On_Peak_kW_Demand || ''}</td>
                <td>${event.Off_Peak_GVARh_Net || ''}</td>
                <td>${event.Off_Peak_kVARh_Net || ''}</td>
                <td>${event.Off_Peak_kVAR_Demand || ''}</td>
                <td>${event.Mid_Peak_GVARh_Net || ''}</td>
                <td>${event.Mid_Peak_kVARh_Net || ''}</td>
                <td>${event.Mid_Peak_kVAR_Demand || ''}</td>
                <td>${event.On_Peak_GVARh_Net || ''}</td>
                <td>${event.On_Peak_kVARh_Net || ''}</td>
                <td>${event.On_Peak_kVAR_Demand || ''}</td>
                <td>${event.Off_Peak_GVAh_Net || ''}</td>
                <td>${event.Off_Peak_kVAh_Net || ''}</td>
                <td>${event.Off_Peak_kVA_Demand || ''}</td>
                <td>${event.Mid_Peak_GVAh_Net || ''}</td>
                <td>${event.Mid_Peak_kVAh_Net || ''}</td>
                <td>${event.Mid_Peak_kVA_Demand || ''}</td>
                <td>${event.On_Peak_GVAh_Net || ''}</td>
                <td>${event.On_Peak_kVAh_Net || ''}</td>
                <td>${event.On_Peak_kVA_Demand || ''}</td>
            </tr>
        `).join('');
    }

    const commonOptions = {
        tooltip: {
            trigger: 'axis',
            backgroundColor: 'rgba(255, 255, 255, 0.9)',
            borderColor: '#D8D8D8',
            borderWidth: 1,
            padding: [5, 10],
            textStyle: { color: '#3F3F3F', fontSize: 12 },
            confine: true,
            formatter: function(params) {
                let tooltipContent = params[0].axisValueLabel + '<br/>';
                params.forEach(param => {
                    tooltipContent += `${param.marker} ${param.seriesName}: <strong>${param.value || '-'}</strong><br/>`;
                });
                return tooltipContent;
            }
        },
        xAxis: {
            type: 'category',
            data: rawData.map(event => event.Start_Date),
            axisLine: { lineStyle: { color: '#D8D8D8' } },
            axisLabel: { color: '#3F3F3F' }
        },
        yAxis: {
            type: 'value',
            splitNumber: 10,
            axisLabel: { color: '#3F3F3F' },
            axisLine: { lineStyle: { color: '#D8D8D8' } },
            splitLine: { lineStyle: { color: '#D8D8D8' } }
        },
        dataZoom: [{ type: 'inside', start: 0, end: 100 }],
        legend: { top: 'bottom', textStyle: { color: '#3F3F3F' } }
    };

    const chartConfigs = [
        {
            title: 'kW Demand',
            legend: ['Off Peak kW Demand', 'Mid Peak kW Demand', 'On Peak kW Demand'],
            series: [
                { name: 'Off Peak kW Demand', key: 'Off_Peak_kW_Demand', color: '#475CA7' },
                { name: 'Mid Peak kW Demand', key: 'Mid_Peak_kW_Demand', color: '#6A9C78' },
                { name: 'On Peak kW Demand', key: 'On_Peak_kW_Demand', color: '#FF6F61' }
            ]
        },
        {
            title: 'kVAR Demand',
            legend: ['Off Peak kVAR Demand', 'Mid Peak kVAR Demand', 'On Peak kVAR Demand'],
            series: [
                { name: 'Off Peak kVAR Demand', key: 'Off_Peak_kVAR_Demand', color: '#475CA7' },
                { name: 'Mid Peak kVAR Demand', key: 'Mid_Peak_kVAR_Demand', color: '#6A9C78' },
                { name: 'On Peak kVAR Demand', key: 'On_Peak_kVAR_Demand', color: '#FF6F61' }
            ]
        },
        {
            title: 'kVA Demand',
            legend: ['Off Peak kVA Demand', 'Mid Peak kVA Demand', 'On Peak kVA Demand'],
            series: [
                { name: 'Off Peak kVA Demand', key: 'Off_Peak_kVA_Demand', color: '#475CA7' },
                { name: 'Mid Peak kVA Demand', key: 'Mid_Peak_kVA_Demand', color: '#6A9C78' },
                { name: 'On Peak kVA Demand', key: 'On_Peak_kVA_Demand', color: '#FF6F61' }
            ]
        },
        {
            title: 'kWh Net',
            legend: ['Off Peak kWh Net', 'Mid Peak kWh Net', 'On Peak kWh Net'],
            series: [
                { name: 'Off Peak kWh Net', key: 'Off_Peak_kWh_Net', color: '#475CA7' },
                { name: 'Mid Peak kWh Net', key: 'Mid_Peak_kWh_Net', color: '#6A9C78' },
                { name: 'On Peak kWh Net', key: 'On_Peak_kWh_Net', color: '#FF6F61' }
            ]
        },
        {
            title: 'kVARh Net',
            legend: ['Off Peak kVARh Net', 'Mid Peak kVARh Net', 'On Peak kVARh Net'],
            series: [
                { name: 'Off Peak kVARh Net', key: 'Off_Peak_kVARh_Net', color: '#475CA7' },
                { name: 'Mid Peak kVARh Net', key: 'Mid_Peak_kVARh_Net', color: '#6A9C78' },
                { name: 'On Peak kVARh Net', key: 'On_Peak_kVARh_Net', color: '#FF6F61' }
            ]
        },
        {
            title: 'kVAh Net',
            legend: ['Off Peak kVAh Net', 'Mid Peak kVAh Net', 'On Peak kVAh Net'],
            series: [
                { name: 'Off Peak kVAh Net', key: 'Off_Peak_kVAh_Net', color: '#475CA7' },
                { name: 'Mid Peak kVAh Net', key: 'Mid_Peak_kVAh_Net', color: '#6A9C78' },
                { name: 'On Peak kVAh Net', key: 'On_Peak_kVAh_Net', color: '#FF6F61' }
            ]
        },
        {
            title: 'GWh Net',
            legend: ['Off Peak GWh Net', 'Mid Peak GWh Net', 'On Peak GWh Net'],
            series: [
                { name: 'Off Peak GWh Net', key: 'Off_Peak_GWh_Net', color: '#475CA7' },
                { name: 'Mid Peak GWh Net', key: 'Mid_Peak_GWh_Net', color: '#6A9C78' },
                { name: 'On Peak GWh Net', key: 'On_Peak_GWh_Net', color: '#FF6F61' }
            ]
        },
        {
            title: 'GVARh Net',
            legend: ['Off Peak GVARh Net', 'Mid Peak GVARh Net', 'On Peak GVARh Net'],
            series: [
                { name: 'Off Peak GVARh Net', key: 'Off_Peak_GVARh_Net', color: '#475CA7' },
                { name: 'Mid Peak GVARh Net', key: 'Mid_Peak_GVARh_Net', color: '#6A9C78' },
                { name: 'On Peak GVARh Net', key: 'On_Peak_GVARh_Net', color: '#FF6F61' }
            ]
        },
        {
            title: 'GVAh Net',
            legend: ['Off Peak GVAh Net', 'Mid Peak GVAh Net', 'On Peak GVAh Net'],
            series: [
                { name: 'Off Peak GVAh Net', key: 'Off_Peak_GVAh_Net', color: '#475CA7' },
                { name: 'Mid Peak GVAh Net', key: 'Mid_Peak_GVAh_Net', color: '#6A9C78' },
                { name: 'On Peak GVAh Net', key: 'On_Peak_GVAh_Net', color: '#FF6F61' }
            ]
        }
    ];

    function renderCharts(data) {
        chartConfigs.forEach((config, index) => {
            const option = {
                ...commonOptions,
                title: {
                    text: config.title,
                    left: 'center',
                    textStyle: { fontSize: 18, fontWeight: 'normal', color: '#3F3F3F' }
                },
                legend: { ...commonOptions.legend, data: config.legend },
                xAxis: { ...commonOptions.xAxis, data: data.map(event => event.Start_Date) },
                series: config.series.map(series => ({
                    name: series.name,
                    type: 'line',
                    smooth: true,
                    data: data.map(event => event[series.key]),
                    lineStyle: { width: 2, color: series.color },
                    emphasis: { focus: 'series' }
                }))
            };
            charts[index].setOption(option);
        });
    }

    fetch(`${deviceName}_TOULogData.json?t=${Date.now()}`)
        .then(response => {
            if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
            return response.json();
        })
        .then(data => {
            rawData = data;
            if (!Array.isArray(rawData) || rawData.length === 0) {
                loadingDiv.textContent = 'No valid data available';
                return;
            }

            populateTable(rawData);
            renderCharts(rawData);

            window.addEventListener('resize', () => {
                charts.forEach(chart => chart.resize());
            });

            loadingDiv.style.display = 'none';
        })
        .catch(error => {
            console.error('Error fetching data:', error);
            loadingDiv.textContent = 'Data not loaded';
        });
});
