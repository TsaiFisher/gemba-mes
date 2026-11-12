// 領料申請／退料（現場，從上工頁進入）：列出本派工所屬工單的用料。
//  - 領料申請：建領料單（MR）送出成待核准；倉庫核准後在「工單發退料」揀料過帳。
//  - 退料：建退料單（MX）；料交給倉庫，倉庫在「工單發退料 → 退料」收進儲位。退料量不得超過已發未退。
// 額度（可申請／可退）由伺服器算好帶下來（只算廠內工序、扣掉未過帳的單），畫面只是先擋一次；
// 委外派工整頁停用（料走委外發料／委外退料）。兩者都可離線排隊（伺服器送達當下才驗）。
const dispatchId = new URLSearchParams(location.search).get('dispatchId');
let reqKind = 'requisition';
let materials = [];
let isOutsource = false;
const qtys = {};   // productCode → qty

(async () => {
    const user = await requireAuth();
    if (!user) return;
    await offlineQueue.ready();
    if (!dispatchId) { showToast(t('session.err_no_dispatch'), 'danger'); return; }
    await loadMaterials();
})();

async function loadMaterials() {
    const data = await apiGet(`/api/mobile/material/dispatches/${encodeURIComponent(dispatchId)}/materials`);
    if (!data?.data) { document.getElementById('matRows').innerHTML = ''; return; }
    document.getElementById('woInfo').textContent = `${t('session.work_order')}：${data.data.workOrderId}`;
    materials = data.data.materials || [];
    isOutsource = !!data.data.isOutsource;
    render();
}

function onReqKindChange(e, el) {
    reqKind = el.value;
    Object.keys(qtys).forEach(k => delete qtys[k]);
    render();
}

function render() {
    const isRet = reqKind === 'return';
    document.getElementById('kindHint').textContent = t(isRet ? 'mreq.hint_ret' : 'mreq.hint_req');
    document.getElementById('btnReqLabel').textContent = t(isRet ? 'mreq.submit_ret' : 'mreq.submit_req');
    document.getElementById('btnReq').disabled = isOutsource;
    if (isOutsource) {
        document.getElementById('matRows').innerHTML = `<div class="alert alert-warning small my-2">${t('mreq.outsource')}</div>`;
        return;
    }
    // 退料只列還可退的料；領料只列領料（Pull）用料且還有可申請量的
    const limitOf = m => isRet ? m.returnableQty : m.requestableQty;
    const rows = materials.filter(m => limitOf(m) > 0);
    if (rows.length === 0) {
        document.getElementById('matRows').innerHTML = `<div class="text-center text-muted py-3">${t(isRet ? 'mreq.none_ret' : 'mreq.none_req')}</div>`;
        return;
    }
    document.getElementById('matRows').innerHTML = rows.map(m => `
        <div class="mat-line">
            <div class="d-flex justify-content-between">
                <span class="mat-product">${matEsc(m.productCode)}</span>
                <span class="badge bg-light text-dark border">${matEsc(m.issueMethod)}</span>
            </div>
            <div class="small text-muted">${t('mreq.required')} ${m.requiredQty} · ${t('mreq.issued')} ${m.issuedQty} · ${t('mreq.returned')} ${m.returnedQty} ${matEsc(m.uomCode || '')}</div>
            <div class="small fw-medium">${t(isRet ? 'mreq.returnable' : 'mreq.requestable')} ${limitOf(m)}</div>
            <div class="d-flex align-items-center gap-2 mt-2">
                <label class="small text-muted">${t(isRet ? 'mreq.qty_ret' : 'mreq.qty_req')}</label>
                <input type="number" class="form-control form-control-sm qty-input" min="0" step="any" inputmode="decimal"
                       max="${limitOf(m)}" value="${qtys[m.productCode] ?? ''}"
                       data-action-change="onReqQty" data-product="${matEsc(m.productCode)}">
            </div>
        </div>`).join('');
}

function onReqQty(e, el) { qtys[el.dataset.product] = parseFloat(el.value) || 0; }

async function submitRequest() {
    const isRet = reqKind === 'return';
    const lines = Object.entries(qtys).filter(([, q]) => q !== 0).map(([productCode, qty]) => ({ productCode, qty }));
    if (lines.some(l => l.qty < 0)) { showToast(t('report.err_qty_negative'), 'danger'); return; }
    if (lines.length === 0) { showToast(t('mreq.nothing'), 'warning'); return; }
    const limit = p => { const m = materials.find(x => x.productCode === p); return (isRet ? m?.returnableQty : m?.requestableQty) ?? 0; };
    const over = lines.find(l => l.qty > limit(l.productCode));
    if (over) { showToast(tf(isRet ? 'mreq.over_ret' : 'mreq.over_req', over.productCode), 'warning'); return; }
    const btn = document.getElementById('btnReq');
    setLoading(btn, true);
    const res = await matSubmit(`/api/mobile/material/dispatches/${encodeURIComponent(dispatchId)}/${isRet ? 'return' : 'requisition'}`,
        { lines, remarks: document.getElementById('reqRemarks').value.trim() || null, ...matNewClientFields() },
        { allowQueue: true, label: t(isRet ? 'mreq.kind_ret' : 'mreq.kind_req') });
    setLoading(btn, false);
    if (!res.ok) return;
    if (!res.queued) showToast(res.result?.message || t('mreq.done'), 'success');
    Object.keys(qtys).forEach(k => delete qtys[k]);
    document.getElementById('reqRemarks').value = '';
    await loadMaterials();
}
