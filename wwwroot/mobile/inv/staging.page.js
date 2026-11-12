// 備料作業：已核准的草稿 → 確認（鎖庫存，只能連線）；已確認 → 逐行選來源儲位 → 移入備料倉（可離線排隊）。
let current = null;
const picks = {};   // lineNo → { wh, loc }

(async () => {
    const user = await requireAuth();
    if (!user) return;
    await offlineQueue.ready();
    await loadOrders();
})();

async function loadOrders() {
    const box = document.getElementById('orderList');
    const data = await apiGet('/api/mobile/material/staging');
    const list = data?.data || [];
    if (list.length === 0) { box.innerHTML = `<div class="text-center text-muted py-3">${t('stg.none')}</div>`; return; }
    box.innerHTML = list.map(o => `
        <div class="border rounded p-2 mb-2 slip-item" data-action="openOrder" data-id="${matEsc(o.stagingId)}">
            <div class="d-flex justify-content-between">
                <span class="fw-semibold">${matEsc(o.stagingId)}</span>
                <span class="badge ${o.status === 'Confirmed' ? 'bg-primary' : 'bg-secondary'}">${t('stg.st.' + o.status)}</span>
            </div>
            <div class="small text-muted">${matEsc(o.sourceId || '—')} → ${matEsc(o.stagingWhCode || '—')} / ${matEsc(o.stagingLocationCode || '—')} · ${tf('mslip.lines', o.lineCount)}</div>
        </div>`).join('');
}

async function openOrder(e, el) {
    const data = await apiGet(`/api/mobile/material/staging/${encodeURIComponent(el.dataset.id)}`);
    if (!data?.data) return;
    current = data.data;
    Object.keys(picks).forEach(k => delete picks[k]);
    current.lines.forEach(l => {
        const first = !l.locationCode && l.stock && l.stock.length ? l.stock[0] : null;
        picks[l.lineNo] = { wh: l.whCode || first?.whCode || '', loc: l.locationCode || first?.locationCode || '' };
    });
    document.getElementById('listCard').classList.add('u-display-none');
    document.getElementById('detailCard').classList.remove('u-display-none');
    document.getElementById('detailTitle').textContent = `${current.stagingId} · ${t('stg.st.' + current.status)}`;
    document.getElementById('detailMeta').textContent =
        `${current.sourceId || '—'} → ${current.stagingWhCode || '—'} / ${current.stagingLocationCode || '—'}`;
    const confirmed = current.status === 'Confirmed';
    document.getElementById('btnConfirm').classList.toggle('u-display-none', confirmed);
    document.getElementById('btnStage').classList.toggle('u-display-none', !confirmed);
    renderLines();
}

function renderLines() {
    const confirmed = current.status === 'Confirmed';
    document.getElementById('lineRows').innerHTML = current.lines.map(l => `
        <div class="mat-line">
            <div class="d-flex justify-content-between">
                <span class="mat-product">${matEsc(l.productCode)}</span>
                <span class="small text-muted">${t('stg.required')} ${l.requiredQty} ${matEsc(l.uomCode || '')}</span>
            </div>
            ${confirmed ? `<div class="mt-1">${matStockChips(String(l.lineNo), l.stock, picks[l.lineNo].loc, null)}</div>` : ''}
        </div>`).join('');
}

function pickStockChip(e, el) {
    const p = picks[el.dataset.line];
    p.wh = el.dataset.wh; p.loc = el.dataset.loc;
    renderLines();
}

function backToList() {
    current = null;
    document.getElementById('detailCard').classList.add('u-display-none');
    document.getElementById('listCard').classList.remove('u-display-none');
}

async function confirmOrder() {
    if (!current) return;
    if (!navigator.onLine) { showToast(t('mat.online_only'), 'warning'); return; }
    const btn = document.getElementById('btnConfirm');
    setLoading(btn, true);
    const res = await apiCall('POST', `/api/mobile/material/staging/${encodeURIComponent(current.stagingId)}/confirm`, {});
    setLoading(btn, false);
    if (!res) return;
    showToast(res.message || t('stg.confirmed'), 'success');
    await openOrder(null, { dataset: { id: current.stagingId } });
}

async function stageOrder() {
    if (!current) return;
    const lines = current.lines.map(l => ({ lineNo: l.lineNo, whCode: picks[l.lineNo].wh || null, locationCode: picks[l.lineNo].loc || null }));
    if (lines.some(l => !l.locationCode)) { showToast(t('mslip.need_source'), 'warning'); return; }
    const btn = document.getElementById('btnStage');
    setLoading(btn, true);
    const res = await matSubmit(`/api/mobile/material/staging/${encodeURIComponent(current.stagingId)}/stage`,
        { lines, ...matNewClientFields() }, { allowQueue: true, label: `${t('stg.title')} ${current.stagingId}` });
    setLoading(btn, false);
    if (!res.ok) return;
    if (!res.queued) showToast(res.result?.message || t('stg.staged'), 'success');
    backToList();
    loadOrders();
}

window.addEventListener('offlinequeue:flushed', () => { if (!current) loadOrders(); });
