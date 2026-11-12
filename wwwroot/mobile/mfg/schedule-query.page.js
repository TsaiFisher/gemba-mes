// 自 schedule-query.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
(function () {
    // 初始化日期範圍：今天起 +14 天
    const today = new Date();
    const end   = new Date(today);
    end.setDate(end.getDate() + 14);
    document.getElementById('inputDateFrom').value = today.toISOString().slice(0, 10);
    document.getElementById('inputDateTo').value   = end.toISOString().slice(0, 10);

    // 載入工作中心下拉
    async function loadWorkCenters() {
        try {
            const data = await apiGet('/api/mfg/work-centers');
            const sel = document.getElementById('selWc');
            (data || []).forEach(wc => {
                const opt = document.createElement('option');
                opt.value = wc.workCenterCode;
                opt.textContent = wc.workCenterCode + (wc.workCenterName ? ' — ' + wc.workCenterName : '');
                sel.appendChild(opt);
            });
        } catch { /* 靜默失敗，下拉保持空 */ }
    }

    function esc(s) {
        return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function fmtTime(iso) {
        if (!iso) return '—';
        const d = new Date(iso);
        return (d.getMonth() + 1) + '/' + d.getDate() + ' '
             + String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    }

    // 狀態顯示沿用派工列表既有字典鍵（dispatch.status.*），避免同一狀態兩套譯名。
    const STATUS_KEY = {
        Pending: 'pending', Scheduled: 'scheduled',
        SetupInProgress: 'setup', ReadyToStart: 'ready',
        InProgress: 'inprogress', Closed: 'closed',
        OnHold: 'onhold', PendingFPI: 'pendingfpi', Cancelled: 'cancelled'
    };
    function statusLabel(st) {
        const k = STATUS_KEY[st];
        return k ? t('dispatch.status.' + k) : (st || '');
    }

    function renderResults(rows) {
        const content = document.getElementById('resultContent');
        const empty   = document.getElementById('emptyState');
        const hint    = document.getElementById('hintState');
        const count   = document.getElementById('resultCount');
        content.innerHTML = '';
        hint.classList.add('d-none');

        if (!rows.length) {
            empty.classList.remove('d-none');
            count.textContent = '';
            return;
        }
        empty.classList.add('d-none');
        count.textContent = tf('query.count', rows.length) + (rows.length >= 200 ? t('query.count_limit') : '');

        const frag = document.createDocumentFragment();
        rows.forEach(r => {
            const flagLate = !r.late ? false : true;
            const flagMat  = r.materialRisk;
            const stCls    = 'st-' + (r.status || 'Scheduled');
            const flagCls  = flagLate ? 'flag-late' : flagMat ? 'flag-mat' : '';
            const progPct  = r.qty > 0 ? Math.min(100, Math.round(r.done / r.qty * 100)) : 0;

            let flags = '';
            if (flagLate) flags += `<span class="badge bg-danger badge-sm me-1">${t('sched.badge_late')}</span>`;
            if (flagMat)  flags += `<span class="badge bg-warning text-dark badge-sm me-1">${t('sched.badge_mat')}</span>`;
            if (r.toolingCode) flags += `<span class="badge bg-secondary badge-sm me-1"><i class="bi bi-tools me-1"></i>${esc(r.toolingCode)}</span>`;

            const card = document.createElement('div');
            card.className = `result-card ${stCls} ${flagCls}`;
            card.innerHTML = `
                <div class="d-flex justify-content-between align-items-start">
                    <div>
                        <div class="fw-semibold">${esc(r.workOrderId)}</div>
                        <div class="text-muted small">${esc(r.batchId || '')} · ${esc(r.productCode || '')}</div>
                        <div class="text-muted small">${esc(r.operationSeq)} ${esc(r.operationName || '')}</div>
                    </div>
                    <div class="text-end ms-2 small">
                        <div class="fw-semibold">${esc(r.machineCode || '—')}</div>
                        <div class="text-muted">${esc(statusLabel(r.status))}</div>
                    </div>
                </div>
                <div class="time-range mt-1">${fmtTime(r.scheduledStart)} → ${fmtTime(r.scheduledEnd)}</div>
                ${flags ? `<div class="mt-1 d-flex flex-wrap gap-1">${flags}</div>` : ''}
                ${r.qty > 0 ? `
                <div class="d-flex justify-content-between small text-muted mt-1">
                    <span>${t('sched.done')} ${r.done}/${r.qty}</span><span>${progPct}%</span>
                </div>
                <div class="prog-bar"><div class="prog-fill" data-pct-width="${progPct}"></div></div>` : ''}
            `;
            frag.appendChild(card);
        });
        content.appendChild(frag);
    }

    window.doSearch = async function () {
        const params = new URLSearchParams();
        const wc   = document.getElementById('selWc').value;
        const mac  = document.getElementById('inputMachine').value.trim();
        const wo   = document.getElementById('inputWo').value.trim();
        const bat  = document.getElementById('inputBatch').value.trim();
        const df   = document.getElementById('inputDateFrom').value;
        const dt   = document.getElementById('inputDateTo').value;

        if (wc)  params.set('workCenterId', wc);
        if (mac) params.set('machineCode', mac);
        if (wo)  params.set('workOrderId', wo);
        if (bat) params.set('batchId', bat);
        if (df)  params.set('dateFrom', df);
        if (dt)  params.set('dateTo', dt);

        document.getElementById('loadingState').classList.remove('d-none');
        document.getElementById('resultContent').innerHTML = '';
        document.getElementById('emptyState').classList.add('d-none');
        document.getElementById('hintState').classList.add('d-none');

        try {
            const data = await apiGet('/api/mobile/schedule-query?' + params.toString());
            renderResults(data?.data || []);
        } catch {
            document.getElementById('resultContent').innerHTML =
                `<div class="alert alert-danger">${t('sched.load_fail')}</div>`;
        } finally {
            document.getElementById('loadingState').classList.add('d-none');
        }
    };

    window.clearForm = function () {
        document.getElementById('selWc').value = '';
        document.getElementById('inputMachine').value = '';
        document.getElementById('inputWo').value = '';
        document.getElementById('inputBatch').value = '';
        document.getElementById('resultContent').innerHTML = '';
        document.getElementById('emptyState').classList.add('d-none');
        document.getElementById('hintState').classList.remove('d-none');
        document.getElementById('resultCount').textContent = '';
    };

    loadWorkCenters();
}());
