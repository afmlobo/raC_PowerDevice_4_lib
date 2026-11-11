document.addEventListener('DOMContentLoaded', function() {
    if (!document.body) {
        console.error('Document body is null');
        const loadingDiv = document.getElementById('loading');
        if (loadingDiv) loadingDiv.textContent = 'Error: Document body not found';
        return;
    }

    const deviceName = document.body.getAttribute('data-device-name');
    const loadingDiv = document.getElementById('loading');
    const tableContainer = document.getElementById('tableContainer');
    const pagination = document.getElementById('pagination');
    const searchInput = document.getElementById('searchInput');

    if (!loadingDiv || !tableContainer || !pagination || !searchInput) {
        console.error('Required DOM elements missing:', {
            loadingDiv: !!loadingDiv,
            tableContainer: !!tableContainer,
            pagination: !!pagination,
            searchInput: !!searchInput
        });
        if (loadingDiv) loadingDiv.textContent = 'Error: Dashboard initialization failed';
        return;
    }

    let tableData = [];
    let filteredData = [];
    let currentPage = 1;
    const rowsPerPage = 15;
    let sortColumn = null;
    let sortDirection = 'asc';

    function renderTable(data) {
        tableContainer.innerHTML = '';
        if (data.length === 0) {
            tableContainer.innerHTML = '<p>No data found</p>';
            loadingDiv.style.display = 'none';
            return;
        }

        const columns = Object.keys(data[0]);
        const startIndex = (currentPage - 1) * rowsPerPage;
        const pageData = data.slice(startIndex, startIndex + rowsPerPage);

        let html = '<table><thead><tr>';
        columns.forEach(column => {
            html += `<th onclick="sortBy('${column.replace(/'/g, "\\'")}')">${column} ${getSortIndicator(column)}</th>`;
        });
        html += '</tr></thead><tbody>';

        pageData.forEach(row => {
            html += '<tr>';
            columns.forEach(column => {
                html += `<td>${row[column] || ''}</td>`;
            });
            html += '</tr>';
        });

        html += '</tbody></table>';
        tableContainer.innerHTML = html;

        renderPagination(data.length);
        loadingDiv.style.display = 'none';
    }

    function getSortIndicator(column) {
        if (sortColumn !== column) return '';
        return sortDirection === 'asc' ? '↑' : '↓';
    }

    function sortBy(column) {
        if (sortColumn === column) {
            sortDirection = sortDirection === 'asc' ? 'desc' : 'asc';
        } else {
            sortColumn = column;
            sortDirection = 'asc';
        }

        const sortedData = [...filteredData].sort((a, b) => {
            const aVal = a[column];
            const bVal = b[column];

            if (typeof aVal === 'number' && typeof bVal === 'number') {
                return sortDirection === 'asc' ? aVal - bVal : bVal - aVal;
            }

            return sortDirection === 'asc'
                ? String(aVal).localeCompare(String(bVal))
                : String(bVal).localeCompare(String(aVal));
        });

        renderTable(sortedData);
    }

    function renderPagination(totalRows) {
        const totalPages = Math.ceil(totalRows / rowsPerPage);
        let html = `
            <button onclick="changePage(1)" ${currentPage === 1 ? 'disabled' : ''}>First</button>
            <button onclick="changePage(${currentPage - 1})" ${currentPage === 1 ? 'disabled' : ''}>Previous</button>
            <span>Page ${currentPage} of ${totalPages}</span>
            <button onclick="changePage(${currentPage + 1})" ${currentPage === totalPages ? 'disabled' : ''}>Next</button>
            <button onclick="changePage(${totalPages})" ${currentPage === totalPages ? 'disabled' : ''}>Last</button>
        `;
        pagination.innerHTML = html;
    }

    function changePage(page) {
        currentPage = page;
        renderTable(filteredData);
    }

    searchInput.addEventListener('input', function(e) {
        const searchTerm = e.target.value.toLowerCase();
        filteredData = tableData.filter(row =>
            Object.values(row).some(value =>
                String(value).toLowerCase().includes(searchTerm)
            )
        );
        currentPage = 1;
        renderTable(filteredData);
    });

    fetch(`${deviceName}_SetpointLogData.json?t=${Date.now()}`)
        .then(response => {
            if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);
            return response.json();
        })
        .then(data => {
            tableData = data;
            filteredData = [...tableData];
            if (!Array.isArray(tableData) || tableData.length === 0) {
                loadingDiv.textContent = 'No valid data available';
                return;
            }
            renderTable(filteredData);
        })
        .catch(error => {
            console.error('Error fetching data:', error);
            loadingDiv.textContent = 'Data not loaded';
        });
});
