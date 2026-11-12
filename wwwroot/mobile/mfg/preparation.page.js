// 自 preparation.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
(function () {
    function isoDate(d) { return d.toISOString().slice(0, 10); }
    const today    = new Date();
    const tomorrow = new Date(today);
    tomorrow.setDate(tomorrow.getDate() + 1);

    function setActiveBtn(id) {
        ['btnToday','btnTomorrow','btnCustom'].forEach(b => {
            const el = document.getElementById(b);
            el.className = b === id
                ? 'btn btn-primary btn-sm'
                : 'btn btn-outline-' + (b === 'btnCustom' ? 'secondary' : 'primary') + ' btn-sm';
        });
    }

    window.selectDate = function (e, el) {
        const mode = el.dataset.when;
        const customRow = document.getElementById('customDateRow');
        if (mode === 'today') {
            customRow.classList.add('u-display-none');
            document.getElementById('inputPrepDate').value = isoDate(today);
            setActiveBtn('btnToday');
            loadPrep();
        } else if (mode === 'tomorrow') {
            customRow.classList.add('u-display-none');
            document.getElementById('inputPrepDate').value = isoDate(tomorrow);
            setActiveBtn('btnTomorrow');
            loadPrep();
        } else {
            customRow.classList.remove('u-display-none');
            setActiveBtn('btnCustom');
        }
    };

    document.getElementById('inputCustomDate').addEventListener('change', function () {
        document.getElementById('inputPrepDate').value = this.value;
        loadPrep();
    });

    // 預設明日
    document.getElementById('inputPrepDate').value = isoDate(tomorrow);

    function esc(s) {
        return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function fmtTime(iso) {
        if (!iso) return '—';
        const d = new Date(iso);
        return (d.getMonth() + 1) + '/' + d.getDate() + ' '
             + String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    }

    function stagingBadge(row) {
        if (!row.materialCount) return '';
        const s = row.stagingStatuses || [];
        if (s.length && s.every(x => x === 'Staged'))
            return `<span class="badge bg-success badge-sm">${t('prep.staged')}</span>`;
        if (s.some(x => x !== 'Staged'))
            return `<span class="badge bg-warning text-dark badge-sm">${t('prep.staging')}</span>`;
        return `<span class="badge bg-secondary badge-sm">${t('prep.not_staged')}</span>`;
    }

    function renderPrep(rows) {
        const content = document.getElementById('prepContent');
        const empty   = document.getElementById('emptyState');
        content.innerHTML = '';

        if (!rows.length) { empty.classList.remove('d-none'); return; }
        empty.classList.add('d-none');

        const byMachine = {};
        rows.forEach(r => {
            const k = r.machineCode || t('prep.unassigned');
            (byMachine[k] = byMachine[k] || []).push(r);
        });

        const frag = document.createDocumentFragment();
        Object.entries(byMachine).forEach(([machine, items]) => {
            const sec = document.createElement('div');
            sec.className = 'machine-section';
            sec.innerHTML = `<span class="machine-label"><i class="bi bi-gear me-1"></i>${esc(machine)}</span>`;

            items.forEach(r => {
                const conflict = r.toolingConflict;
                const late     = !conflict && r.late;
                const mat      = !conflict && !r.late && r.materialRisk;
                const cls      = conflict ? 'conflict' : late ? 'flag-late' : mat ? 'flag-mat' : '';

                const toolingHtml = r.toolingCode
                    ? `<div class="mt-1"><span class="tooling-chip ${conflict ? 'conflict' : ''}"><i class="bi bi-tools me-1"></i>${esc(r.toolingCode)}</span></div>`
                    : '';

                let flags = '';
                if (conflict)       flags += `<span class="badge badge-sm me-1 u-background-6f42c1"><i class="bi bi-exclamation-triangle me-1"></i>${t('prep.legend_tooling')}</span>`;
                if (r.carryOver)    flags += `<span class="badge bg-info text-dark badge-sm me-1">${t('sched.badge_carry_over')}</span>`;
                if (r.late)         flags += `<span class="badge bg-danger badge-sm me-1">${t('sched.badge_late')}</span>`;
                if (r.materialRisk) flags += `<span class="badge bg-warning text-dark badge-sm me-1">${t('sched.badge_mat')}</span>`;
                flags += stagingBadge(r);

                const setupTxt = r.setupMin > 0 ? `${t('prep.setup_pre')} ${r.setupMin} min · ` : '';

                const card = document.createElement('div');
                card.className = `prep-card ${cls}`;
                card.innerHTML = `
                    <div class="d-flex justify-content-between align-items-start">
                        <div>
                            <div class="fw-semibold">${esc(r.workOrderId)}</div>
                            <div class="text-muted small">${esc(r.operationName || String(r.operationSeq))}</div>
                        </div>
                        <div class="text-end ms-2 small">
                            <div class="fw-semibold">${r.qty}</div>
                            <div class="text-muted">${t('prep.qty')}</div>
                        </div>
                    </div>
                    <div class="time-range mt-1">${setupTxt}${fmtTime(r.scheduledStart)} → ${fmtTime(r.scheduledEnd)}</div>
                    ${toolingHtml}
                    ${flags ? `<div class="mt-1 d-flex flex-wrap gap-1">${flags}</div>` : ''}
                `;
                sec.appendChild(card);
            });

            frag.appendChild(sec);
        });
        content.appendChild(frag);
    }

    window.loadPrep = async function () {
        document.getElementById('loadingState').classList.remove('d-none');
        document.getElementById('prepContent').innerHTML = '';
        document.getElementById('emptyState').classList.add('d-none');

        const date = document.getElementById('inputPrepDate').value;
        try {
            const data = await apiGet(
                '/api/mfg/preparation?date=' + encodeURIComponent(date));
            renderPrep(data?.rows || []);
            document.getElementById('lastRefreshText').textContent =
                t('sched.last_refresh') + new Date().toLocaleTimeString(getLang());
        } catch {
            document.getElementById('prepContent').innerHTML =
                `<div class="alert alert-danger">${t('sched.load_fail')}</div>`;
        } finally {
            document.getElementById('loadingState').classList.add('d-none');
        }
    };

    loadPrep();
    setInterval(loadPrep, 45000);
}());
