// 委外收料：選（或掃描）委外採購單 → 逐行填實收量／批號（簡化模式另填入庫儲位）→ 確認收料。
// 只能連線送出：收料會同時產生應付發票，結果必須當場知道（伺服器端以 clientId 防連按兩次）。
let current = null;
const inputs = {};   // opoLineNo → { qty, lot, loc }
let scanner = null, scanModal = null, scanTarget = null;

(async () => {
    const user = await requireAuth();
    if (!user) return;
    scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
    scanner = new BarcodeScanner({ onScan: code => applyScan(code) });
    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => scanner.stopCamera());
    document.getElementById('opoSearch').addEventListener('keydown', ev => { if (ev.key === 'Enter') searchOpos(); });
    await Promise.all([searchOpos(), loadApproved()]);
})();

async function loadApproved() {
    const data = await apiGet('/api/mobile/material/outsource-receipts/approved');
    const list = data?.data || [];
    document.getElementById('approvedCard').classList.toggle('u-display-none', list.length === 0);
    document.getElementById('approvedList').innerHTML = list.map(r => `
        <div class="d-flex justify-content-between align-items-center border rounded p-2 mb-2">
            <div><div class="fw-semibold">${matEsc(r.outsourceReceiptId)}</div>
                 <div class="small text-muted">${matEsc(r.outsourcePurchaseOrderId)} · ${matEsc(r.supplierCode)}</div></div>
            <button class="btn btn-sm btn-primary" data-action="confirmApproved" data-id="${matEsc(r.outsourceReceiptId)}">${t('osr.confirm')}</button>
        </div>`).join('');
}

async function searchOpos() {
    const q = document.getElementById('opoSearch').value.trim();
    const box = document.getElementById('opoList');
    const data = await apiGet('/api/mobile/material/outsource-orders' + (q ? '?q=' + encodeURIComponent(q) : ''));
    const list = data?.data || [];
    if (list.length === 0) { box.innerHTML = `<div class="text-center text-muted py-3">${t('osr.none')}</div>`; return; }
    // 掃到的單號剛好唯一命中就直接打開
    if (q && list.length === 1 && list[0].outsourcePurchaseOrderId === q) { await openOpo(null, { dataset: { id: q } }); return; }
    box.innerHTML = list.map(o => `
        <div class="border rounded p-2 mb-2 slip-item" data-action="openOpo" data-id="${matEsc(o.outsourcePurchaseOrderId)}">
            <div class="d-flex justify-content-between">
                <span class="fw-semibold">${matEsc(o.outsourcePurchaseOrderId)}</span>
                <span class="small text-muted">${o.expectedDeliveryDate ? matEsc(o.expectedDeliveryDate) : ''}</span>
            </div>
            <div class="small text-muted">${matEsc(o.supplierCode)} ${matEsc(o.supplierName || '')} · ${t('osr.remaining')} ${o.remainingQty}</div>
        </div>`).join('');
}

async function openOpo(e, el) {
    const data = await apiGet(`/api/mobile/material/outsource-orders/${encodeURIComponent(el.dataset.id)}`);
    if (!data?.data) return;
    current = data.data;
    Object.keys(inputs).forEach(k => delete inputs[k]);
    current.lines.forEach(l => { inputs[l.lineNo] = { qty: l.remainingQty, lot: '', loc: '', fail: 0 }; });
    document.getElementById('listCard').classList.add('u-display-none');
    document.getElementById('detailCard').classList.remove('u-display-none');
    document.getElementById('detailTitle').textContent = current.outsourcePurchaseOrderId;
    document.getElementById('detailMeta').textContent = `${current.supplierCode} ${current.supplierName || ''}`;
    document.getElementById('locHint').classList.toggle('u-display-none', !current.requiresLocation);
    document.getElementById('inspectHint').classList.toggle('u-display-none', !current.inspectAtReceipt || current.isOperation);
    document.getElementById('opHint').classList.toggle('u-display-none', !current.isOperation);
    document.getElementById('isFinal').checked = false;
    renderLines();
}

function renderLines() {
    document.getElementById('lineRows').innerHTML = current.lines.map(l => {
        const v = inputs[l.lineNo];
        const done = l.remainingQty <= 0;
        return `<div class="mat-line${done ? ' mat-done' : ''}">
            <div class="d-flex justify-content-between">
                <span class="mat-product">${matEsc(l.productCode)}</span>
                <span class="small text-muted">${matEsc(l.uomCode || '')}</span>
            </div>
            <div class="small text-muted">${t('osr.ordered')} ${l.orderedQty} · ${t('osr.received')} ${l.receivedQty}${l.draftQty > 0 ? ' · ' + t('osr.in_draft') + ' ' + l.draftQty : ''} · <b>${t('osr.remaining')} ${l.remainingQty}</b></div>
            <div class="row g-2 mt-1">
                <div class="col-6"><input type="number" class="form-control form-control-sm" min="0" step="any" inputmode="decimal"
                     value="${v.qty}" placeholder="${t('osr.qty')}" data-action-change="onRecvInput" data-line="${l.lineNo}" data-field="qty"></div>
                <div class="col-6"><input type="text" class="form-control form-control-sm" maxlength="30" value="${matEsc(v.lot)}"
                     placeholder="${t('osr.lot')}" data-action-change="onRecvInput" data-line="${l.lineNo}" data-field="lot"></div>
                ${current.inspectAtReceipt ? `<div class="col-6"><input type="number" class="form-control form-control-sm" min="0" step="1" inputmode="numeric"
                     value="${v.fail}" placeholder="${t('osr.fail_qty')}" aria-label="${t('osr.fail_qty')}" data-action-change="onRecvInput" data-line="${l.lineNo}" data-field="fail"></div>
                     <div class="col-6 small text-muted align-self-center">${t('osr.fail_qty')}</div>` : ''}
                ${current.requiresLocation || (current.inspectAtReceipt && !current.isOperation) ? `<div class="col-12"><div class="input-group input-group-sm">
                     <input type="text" class="form-control" value="${matEsc(v.loc)}" placeholder="${t('mslip.return_loc_ph')}"
                            data-action-change="onRecvInput" data-line="${l.lineNo}" data-field="loc">
                     <button class="btn btn-outline-secondary" data-action="openLocScanner" data-line="${l.lineNo}"><i class="bi bi-upc-scan"></i></button>
                   </div></div>` : ''}
            </div>
        </div>`;
    }).join('');
}

function onRecvInput(e, el) {
    const v = inputs[el.dataset.line];
    if (el.dataset.field === 'qty' || el.dataset.field === 'fail') v[el.dataset.field] = parseFloat(el.value) || 0;
    else v[el.dataset.field] = el.value.trim();
}

function openOpoScanner() { scanTarget = 'opo'; scanModal.show(); scanner.startCamera('reader'); }
function openLocScanner(e, el) { scanTarget = el.dataset.line; scanModal.show(); scanner.startCamera('reader'); }
function applyScan(code) {
    scanModal.hide();
    scanner.stopCamera();
    if (scanTarget === 'opo') { document.getElementById('opoSearch').value = code; searchOpos(); return; }
    if (inputs[scanTarget]) { inputs[scanTarget].loc = code; renderLines(); }
}

function backToList() {
    current = null;
    document.getElementById('detailCard').classList.add('u-display-none');
    document.getElementById('listCard').classList.remove('u-display-none');
}

async function submitReceive() {
    if (!current) return;
    const lines = current.lines
        .map(l => ({ opoLineNo: l.lineNo, qty: inputs[l.lineNo].qty, lotNo: inputs[l.lineNo].lot || null,
                     whCode: null, locationCode: inputs[l.lineNo].loc || null,
                     failQty: current.inspectAtReceipt ? inputs[l.lineNo].fail : null, remaining: l.remainingQty }))
        .filter(l => l.qty !== 0);
    if (lines.some(l => l.qty < 0)) { showToast(t('report.err_qty_negative'), 'danger'); return; }
    if (lines.length === 0) { showToast(t('osr.nothing'), 'warning'); return; }
    if (lines.some(l => l.failQty != null && (l.failQty < 0 || l.failQty > l.qty))) { showToast(t('osr.fail_invalid'), 'warning'); return; }
    if (current.requiresLocation && lines.some(l => !l.locationCode)) { showToast(t('osr.loc_required'), 'warning'); return; }
    // 超收不擋（與桌機一致，可能是廠商多交），但要讓人確認一次
    if (lines.some(l => l.qty > l.remaining) && !confirm(t('osr.over_confirm'))) return;

    const btn = document.getElementById('btnReceive');
    setLoading(btn, true);
    const res = await matSubmit('/api/mobile/material/outsource-receipts', {
        opoId: current.outsourcePurchaseOrderId,
        isFinalReceipt: document.getElementById('isFinal').checked,
        lines: lines.map(({ remaining, ...l }) => l),
        ...matNewClientFields(),
    }, { allowQueue: false });
    setLoading(btn, false);
    if (!res.ok) return;
    showToast(res.result?.message || t('osr.done'), 'success');
    backToList();
    searchOpos();
    loadApproved();
}

async function confirmApproved(e, el) {
    if (!navigator.onLine) { showToast(t('mat.online_only'), 'warning'); return; }
    setLoading(el, true);
    const res = await apiCall('POST', `/api/mobile/material/outsource-receipts/${encodeURIComponent(el.dataset.id)}/confirm`, {});
    setLoading(el, false);
    if (!res) return;
    showToast(res.message || t('osr.done'), 'success');
    loadApproved();
}
