/**
 * help_dashboard.js
 * Dynamic markdown renderer for PowerMonitor Advanced Dashboard Help.
 * Fetches DASHBOARD_SCREENS_SECTION.md, renders via marked.js,
 * builds sidebar nav from H2 headings with Font Awesome icons,
 * and wires search, lightbox, scroll-tracking.
 */

(function () {
    'use strict';

    // Font Awesome icon classes mapped to heading keywords
    var SECTION_ICONS = {
        'organization tree': 'fa-solid fa-sitemap',
        'common dashboard': 'fa-solid fa-gauge',
        'energy dashboard': 'fa-solid fa-bolt',
        'energy log': 'fa-solid fa-bolt',
        'pqevents': 'fa-solid fa-wave-square',
        'pq events': 'fa-solid fa-wave-square',
        'iti and semi': 'fa-solid fa-chart-line',
        'iti & semi': 'fa-solid fa-chart-line',
        'alarm log': 'fa-solid fa-bell',
        'calendar trend': 'fa-solid fa-calendar-days',
        'trend viewer': 'fa-solid fa-chart-area',
        'reports': 'fa-solid fa-file-pdf',
        'using dashboard': 'fa-solid fa-arrows-rotate',
        'help': 'fa-solid fa-circle-question'
    };

    var DEFAULT_ICON = 'fa-solid fa-file-lines';

    // DOM refs
    var mdRendered = document.getElementById('mdRendered');
    var loadingSpinner = document.getElementById('loadingSpinner');
    var sidebarNav = document.getElementById('sidebarNav');
    var searchInput = document.getElementById('searchInput');
    var searchClear = document.getElementById('searchClear');
    var noResults = document.getElementById('noResults');
    var mainContent = document.getElementById('mainContent');
    var lightbox = document.getElementById('lightbox');
    var lightboxImg = document.getElementById('lightboxImg');
    var backToTop = document.getElementById('backToTop');
    var btnScrollTop = document.getElementById('btnScrollTop');

    // ===== FETCH MARKDOWN =====
    function loadMarkdown() {
        var xhr = new XMLHttpRequest();
        xhr.open('GET', 'DASHBOARD_SCREENS_SECTION.md', true);
        xhr.onreadystatechange = function () {
            if (xhr.readyState === 4) {
                if (xhr.status === 200 || xhr.status === 0) {
                    renderMarkdown(xhr.responseText);
                } else {
                    loadingSpinner.textContent = 'Failed to load documentation (Status: ' + xhr.status + ')';
                }
            }
        };
        xhr.onerror = function () {
            loadingSpinner.textContent = 'Failed to load documentation file.';
        };
        xhr.send();
    }

    // ===== RENDER =====
    function renderMarkdown(mdText) {
        if (typeof marked.setOptions === 'function') {
            marked.setOptions({ breaks: false, gfm: true });
        }

        var html;
        if (typeof marked.parse === 'function') {
            html = marked.parse(mdText);
        } else {
            html = marked(mdText);
        }

        loadingSpinner.style.display = 'none';
        mdRendered.innerHTML = html;

        addHeadingIds();
        buildSidebarNav();
        wireImageLightbox();
        highlightActionRows();
    }

    // ===== ADD IDs TO H2 =====
    function addHeadingIds() {
        var headings = mdRendered.querySelectorAll('h2');
        for (var i = 0; i < headings.length; i++) {
            var slug = headings[i].textContent.trim()
                .toLowerCase()
                .replace(/[^a-z0-9\s-]/g, '')
                .replace(/\s+/g, '-')
                .replace(/-+/g, '-')
                .replace(/^-|-$/g, '');
            headings[i].id = slug;
        }
    }

    // ===== BUILD SIDEBAR NAV =====
    function buildSidebarNav() {
        var headings = mdRendered.querySelectorAll('h2');
        var frag = document.createDocumentFragment();

        for (var i = 0; i < headings.length; i++) {
            var text = headings[i].textContent.trim();
            var slug = headings[i].id;

            // Pick icon
            var iconClass = DEFAULT_ICON;
            var lower = text.toLowerCase();
            for (var key in SECTION_ICONS) {
                if (SECTION_ICONS.hasOwnProperty(key) && lower.indexOf(key) !== -1) {
                    iconClass = SECTION_ICONS[key];
                    break;
                }
            }

            // Section number
            var numMatch = text.match(/^(\d+\.\d+)\s/);
            var sectionNum = numMatch ? numMatch[1] : '';
            var label = numMatch ? text.replace(numMatch[0], '') : text;

            var a = document.createElement('a');
            a.className = 'nav-item';
            a.href = '#' + slug;
            a.setAttribute('data-section', slug);

            if (sectionNum) {
                var numSpan = document.createElement('span');
                numSpan.className = 'nav-num';
                numSpan.textContent = sectionNum;
                a.appendChild(numSpan);
            }

            var icon = document.createElement('i');
            icon.className = iconClass;
            a.appendChild(icon);

            var labelSpan = document.createElement('span');
            labelSpan.textContent = label;
            a.appendChild(labelSpan);

            frag.appendChild(a);
        }

        sidebarNav.insertBefore(frag, noResults);
        wireNavClicks();
    }

    // ===== HIGHLIGHT ACTION/INTERACTION ROWS =====
    function highlightActionRows() {
        var rows = mdRendered.querySelectorAll('tbody tr');
        for (var i = 0; i < rows.length; i++) {
            var firstCell = rows[i].querySelector('td');
            if (firstCell) {
                var val = firstCell.textContent.trim().toLowerCase();
                if (val === 'action' || val === 'interaction') {
                    rows[i].classList.add('action-row');
                }
            }
        }
    }

    // ===== IMAGE LIGHTBOX =====
    function wireImageLightbox() {
        var images = mdRendered.querySelectorAll('img');
        for (var i = 0; i < images.length; i++) {
            images[i].addEventListener('click', function () {
                lightboxImg.src = this.src;
                lightbox.classList.add('open');
            });
        }
    }

    function closeLightbox() {
        lightbox.classList.remove('open');
    }

    lightbox.addEventListener('click', closeLightbox);
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') closeLightbox();
    });

    // ===== NAV CLICKS =====
    function wireNavClicks() {
        var items = sidebarNav.querySelectorAll('.nav-item');
        for (var i = 0; i < items.length; i++) {
            items[i].addEventListener('click', function (e) {
                e.preventDefault();
                var id = this.getAttribute('href').substring(1);
                var el = document.getElementById(id);
                if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
            });
        }
    }

    // ===== SCROLL: active nav + back-to-top =====
    function updateActiveNav() {
        var items = sidebarNav.querySelectorAll('.nav-item');
        var headings = mdRendered.querySelectorAll('h2');
        var scrollTop = mainContent.scrollTop;
        var current = '';

        for (var i = 0; i < headings.length; i++) {
            if (scrollTop >= headings[i].offsetTop - 80) {
                current = headings[i].id;
            }
        }

        for (var j = 0; j < items.length; j++) {
            if (items[j].getAttribute('data-section') === current) {
                items[j].classList.add('active');
            } else {
                items[j].classList.remove('active');
            }
        }
    }

    mainContent.addEventListener('scroll', function () {
        updateActiveNav();
        if (mainContent.scrollTop > 300) {
            backToTop.classList.add('visible');
        } else {
            backToTop.classList.remove('visible');
        }
    });

    backToTop.addEventListener('click', function () {
        mainContent.scrollTo({ top: 0, behavior: 'smooth' });
    });

    btnScrollTop.addEventListener('click', function () {
        mainContent.scrollTo({ top: 0, behavior: 'smooth' });
    });

    // ===== SEARCH =====
    searchInput.addEventListener('input', function () {
        var query = this.value.toLowerCase().trim();
        if (query.length > 0) {
            searchClear.classList.add('visible');
        } else {
            searchClear.classList.remove('visible');
        }

        var items = sidebarNav.querySelectorAll('.nav-item');
        var found = 0;

        for (var i = 0; i < items.length; i++) {
            var navText = items[i].textContent.toLowerCase();
            var sectionId = items[i].getAttribute('data-section');
            var sectionText = '';

            if (sectionId) {
                var sectionEl = document.getElementById(sectionId);
                if (sectionEl) {
                    var content = sectionEl.textContent;
                    var next = sectionEl.nextElementSibling;
                    while (next && next.tagName !== 'H2') {
                        content += ' ' + next.textContent;
                        next = next.nextElementSibling;
                    }
                    sectionText = content.toLowerCase();
                }
            }

            if (!query || navText.indexOf(query) !== -1 || sectionText.indexOf(query) !== -1) {
                items[i].classList.remove('hidden');
                found++;
            } else {
                items[i].classList.add('hidden');
            }
        }

        noResults.style.display = (query && found === 0) ? 'block' : 'none';
    });

    searchClear.addEventListener('click', function () {
        searchInput.value = '';
        searchInput.dispatchEvent(new Event('input'));
        searchInput.focus();
    });

    // ===== INIT =====
    loadMarkdown();

})();
