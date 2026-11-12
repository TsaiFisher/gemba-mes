// 工單發退料（倉庫）：發料＝從庫存儲位揀料出庫；退料＝把現場退回的料收進指定儲位。
// 一次完成「揀料確認＋過帳」（POST /api/mobile/material/slips/{id}/post），可離線排隊。
let slipKind = 'issue';
let current = null;          // 目前開啟的單據（伺服器回傳的明細）
const picks = {};            // lineNo → [{ wh, loc, lot, qty }]（發料可拆多個儲位：備料區不夠的部分從其他儲位領）
let scanner = null, scanModal = null, scanTarget = null, scanBox = false;

(async () => {
    const user = await requireAuth();
    if (!user) return;
    await offlineQueue.ready();
    scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
    scanner = new BarcodeScanner({ onScan: code => applyScannedLocation(code) });
    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => scanner.stopCamera());
    await loadSlips();
})();

function onKindChange(e, el) {
    slipKind = el.value;
    backToList();
    loadSlips();
}

async function loadSlips() {
    const box = document.getElementById('slipList');
    const data = await apiGet(`/api/mobile/material/slips?kind=${slipKind}`);
    const list = data?.data || [];
    if (list.length === 0) { box.innerHTML = `<div class="text-center text-muted py-3">${t('mslip.none')}</div>`; return; }
    box.innerHTML = list.map(s => `
        <div class="border rounded p-2 mb-2 slip-item" data-action="openSlip" data-id="${matEsc(s.slipId)}">
            <div class="d-flex justify-content-between">
                <span class="fw-semibold">${matEsc(s.slipId)}</span>
                <span class="badge bg-secondary">${t('mslip.cat.' + s.category)}</span>
            </div>
            <div class="small text-muted">${matEsc(s.sourceDocId || '—')} · ${tf('mslip.lines', s.lineCount)}${s.targetSupplierCode ? ' · ' + matEsc(s.targetSupplierCode) : ''}</div>
        </div>`).join('');
}

async function openSlip(e, el) {
    const data = await apiGet(`/api/mobile/material/slips/${encodeURIComponent(el.dataset.id)}`);
    if (!data?.data) return;
    current = data.data;
    Object.keys(picks).forEach(k => delete picks[k]);
    current.lines.forEach(l => { picks[l.lineNo] = defaultPicks(l); });
    document.getElementById('listCard').classList.add('u-display-none');
    document.getElementById('detailCard').classList.remove('u-display-none');
    document.getElementById('detailTitle').textContent = `${current.slipId} · ${t('mslip.cat.' + current.category)}`;
    document.getElementById('detailMeta').textContent =
        `${t('mslip.source')}：${current.sourceDocId || '—'}${current.targetSupplierCode ? ' · ' + current.targetSupplierCode : ''}`;
    document.getElementById('btnPostLabel').textContent = t(current.isReturn ? 'mslip.post_return' : 'mslip.post_issue');
    renderLines();
}

/**
 * 預設揀料：單上已指定的位置；沒有的話發料取第一個有庫存的儲位（已備料的排第一）。
 * 已備料而備料儲位不夠這一行時，自動拆成「備料儲位（可用量）＋下一個有庫存的儲位（不足的部分）」——
 * 伺服器規定已備料的料要先從備料區扣，不夠的部分才可從其他儲位領。
 */
function defaultPicks(l) {
    if (current.isReturn || l.locationCode || !l.stock || !l.stock.length)
        return [{ wh: l.whCode || '', loc: l.locationCode || '', lot: l.lotNo || '', qty: l.qty }];
    const first = l.stock[0];
    const staged = l.stagedLocation && first.locationCode === l.stagedLocation;
    if (!staged || first.qtyAvailable >= l.qty || l.stock.length < 2)
        return [{ wh: first.whCode, loc: first.locationCode, lot: first.lotNo || '', qty: l.qty }];
    const next = l.stock[1];
    return [
        { wh: first.whCode, loc: first.locationCode, lot: first.lotNo || '', qty: first.qtyAvailable },
        { wh: next.whCode,  loc: next.locationCode,  lot: next.lotNo || '',  qty: +(l.qty - first.qtyAvailable).toFixed(6) },
    ];
}

const keyOf = (lineNo, idx) => `${lineNo}:${idx}`;
function entryOf(key) { const [n, i] = key.split(':'); return picks[n]?.[+i]; }

function renderLines() {
    document.getElementById('lineRows').innerHTML = current.lines.map(l => {
        const entries = picks[l.lineNo];
        const blocks = entries.map((p, idx) => {
            const key = keyOf(l.lineNo, idx);
            const where = current.isReturn
                ? `<div class="input-group input-group-sm mt-1">
                       <input type="text" class="form-control" value="${matEsc(p.loc)}" placeholder="${t('mslip.return_loc_ph')}"
                              data-action-change="onLocInput" data-line="${key}">
                       <button class="btn btn-outline-secondary" data-action="openLocScanner" data-line="${key}"><i class="bi bi-upc-scan"></i></button>
                   </div>`
                : `<div class="mt-1">${matStockChips(key, l.stock, p.loc, l.stagedLocation)}</div>
                   <div class="input-group input-group-sm mt-1">
                       <input type="text" class="form-control" value="${matEsc(p.box || '')}" placeholder="${t('mslip.box_ph')}"
                              data-action-change="onBoxInput" data-line="${key}">
                       <button class="btn btn-outline-secondary" data-action="openBoxScanner" data-line="${key}"><i class="bi bi-upc-scan"></i></button>
                   </div>
                   ${p.box ? `<div class="small text-success mt-1" data-box-info>${tf('mslip.box_of', matEsc(p.box), matEsc(p.lot), matEsc(p.loc))}</div>` : ''}`;
            return `<div class="pick-entry${idx > 0 ? ' border-top pt-2 mt-2' : ''}" data-entry="${key}">
                ${where}
                <div class="d-flex align-items-center gap-2 mt-2">
                    <label class="small text-muted">${t(current.isReturn ? 'mslip.qty_return' : 'mslip.qty_pick')}</label>
                    <input type="number" class="form-control form-control-sm qty-input" min="0" step="any" inputmode="decimal"
                           value="${p.qty}" data-action-change="onQtyInput" data-line="${key}">
                    ${idx > 0 ? `<button type="button" class="btn btn-sm btn-outline-danger" data-action="removePickEntry" data-line="${key}"
                                    aria-label="${t('mslip.remove_split')}"><i class="bi bi-x-lg"></i></button>` : ''}
                </div>
            </div>`;
        }).join('');
        const stagedHint = !current.isReturn && l.stagedLocation
            ? `<div class="small text-info mt-1" data-staged-hint>${tf('mslip.staged_first', l.stagedLocation)}</div>` : '';
        const addSplit = !current.isReturn && l.stock && l.stock.length > 1
            ? `<button type="button" class="btn btn-sm btn-link px-0 mt-1" data-action="addPickEntry" data-line="${l.lineNo}">
                   <i class="bi bi-plus-lg"></i> ${t('mslip.add_split')}</button>` : '';
        return `<div class="mat-line" data-line-no="${l.lineNo}">
            <div class="d-flex justify-content-between">
                <span class="mat-product">${matEsc(l.productCode)}</span>
                <span class="small text-muted">${t('mslip.requested')} ${l.qty} ${matEsc(l.uomCode || '')}</span>
            </div>
            ${stagedHint}
            ${blocks}
            ${addSplit}
        </div>`;
    }).join('');
}

function pickStockChip(e, el) {
    const p = entryOf(el.dataset.line);
    p.wh = el.dataset.wh; p.loc = el.dataset.loc; p.lot = el.dataset.lot; p.box = '';
    renderLines();
}
function onLocInput(e, el) { const p = entryOf(el.dataset.line); p.loc = el.value.trim(); p.wh = ''; }
async function onBoxInput(e, el) { await applyBox(el.dataset.line, el.value.trim()); }

// 掃箱號（container-tracking.md §9）：整箱領——帶出箱子所在儲位與批號，數量＝min(箱內, 這一行還沒分配的量)。
// 直接 fetch 而非 apiGet：404（不是箱號、空箱）是正常結果，自己給提示。
async function applyBox(key, code) {
    if (!code) return;
    const p = entryOf(key);
    const l = current.lines.find(x => String(x.lineNo) === key.split(':')[0]);
    let box = null;
    try {
        const r = await fetch(`/api/warehouse/container-unit/${encodeURIComponent(code)}`, { credentials: 'include' });
        if (r.ok) box = await r.json();
    } catch (err) { /* 連不上同 404 */ }
    if (!box) { showToast(tf('mslip.box_not_found', code), 'warning'); renderLines(); return; }
    if (box.productCode !== l.productCode) {
        showToast(tf('pick.box_other_product', box.containerUnitId, box.productCode), 'warning');
        renderLines();
        return;
    }
    const at = box.locations[0];
    const others = picks[l.lineNo].filter(x => x !== p).reduce((a, x) => a + (x.qty || 0), 0);
    p.wh = at.whCode; p.loc = at.locationCode; p.lot = box.lotNo; p.box = box.containerUnitId;
    p.qty = Math.min(at.qty, Math.max(0, +(l.qty - others).toFixed(6)));
    renderLines();
}
function onQtyInput(e, el) { entryOf(el.dataset.line).qty = parseFloat(el.value) || 0; }

/** 從其他儲位補：新增一筆，預設第一個還沒用到的儲位、數量＝這一行還沒分配的量。 */
function addPickEntry(e, el) {
    const l = current.lines.find(x => String(x.lineNo) === el.dataset.line);
    const entries = picks[l.lineNo];
    const used = new Set(entries.map(p => p.loc));
    const s = (l.stock || []).find(r => !used.has(r.locationCode)) || {};
    const rest = Math.max(0, +(l.qty - entries.reduce((a, p) => a + (p.qty || 0), 0)).toFixed(6));
    entries.push({ wh: s.whCode || '', loc: s.locationCode || '', lot: s.lotNo || '', qty: rest });
    renderLines();
}
function removePickEntry(e, el) {
    const [n, i] = el.dataset.line.split(':');
    picks[n].splice(+i, 1);
    renderLines();
}

function openLocScanner(e, el) {
    scanTarget = el.dataset.line;
    scanBox = false;
    scanModal.show();
    scanner.startCamera('reader');
}
function openBoxScanner(e, el) {
    scanTarget = el.dataset.line;
    scanBox = true;
    scanModal.show();
    scanner.startCamera('reader');
}
function applyScannedLocation(code) {
    if (scanBox) {
        scanModal.hide();
        scanner.stopCamera();
        if (scanTarget) applyBox(scanTarget, code);
        return;
    }
    const p = scanTarget ? entryOf(scanTarget) : null;
    if (p) { p.loc = code; p.wh = ''; }
    scanModal.hide();
    scanner.stopCamera();
    renderLines();
}

function backToList() {
    current = null;
    document.getElementById('detailCard').classList.add('u-display-none');
    document.getElementById('listCard').classList.remove('u-display-none');
}

async function submitSlip() {
    if (!current) return;
    // 同一行拆成多個儲位時送多筆同 lineNo，伺服器據此拆行
    const lines = current.lines.flatMap(l => picks[l.lineNo].map(p =>
        ({ lineNo: l.lineNo, whCode: p.wh || null, locationCode: p.loc || null, lotNo: p.lot || null, qty: p.qty })));
    if (lines.some(l => l.qty < 0)) { showToast(t('report.err_qty_negative'), 'danger'); return; }
    if (lines.some(l => l.qty > 0 && !l.locationCode)) { showToast(t(current.isReturn ? 'mslip.need_return_loc' : 'mslip.need_source'), 'warning'); return; }

    const btn = document.getElementById('btnPost');
    setLoading(btn, true);
    const res = await matSubmit(`/api/mobile/material/slips/${encodeURIComponent(current.slipId)}/post`,
        { lines, ...matNewClientFields() },
        { allowQueue: true, label: `${t('mslip.title')} ${current.slipId}` });
    setLoading(btn, false);
    if (!res.ok) return;
    if (!res.queued) showToast(res.result?.message || t('mslip.posted'), 'success');
    backToList();
    loadSlips();
}

window.addEventListener('offlinequeue:flushed', () => { if (!current) loadSlips(); });
