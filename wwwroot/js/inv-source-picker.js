// Shared source-doc picker utilities for INV module reversal forms.
// User-facing strings go through window.i18n (emitted by _JsLocalization.cshtml);
// falls back to the key itself if i18n is not yet loaded — never hardcode CJK here.
function invT(key) {
    return (window.i18n && typeof window.i18n.t === 'function')
        ? window.i18n.t(key) : key;
}

window.lookupSourceLines = async function (sourceTypeCode, entityCode) {
    let url = `/api/warehouse/outstanding-source-lines?sourceTypeCode=${encodeURIComponent(sourceTypeCode)}`;
    if (entityCode) url += `&entityCode=${encodeURIComponent(entityCode)}`;
    try {
        const resp = await fetch(url);
        if (!resp.ok) { alert(invT('InvSourceQueryFailed')); return []; }
        const lines = await resp.json();
        if (!lines.length) { alert(invT('InvNoOpenSourceLines')); return []; }
        return lines;
    } catch { alert(invT('InvSourceQueryFailed')); return []; }
};

window.showSourcePicker = function (lines, onSelect) {
    const existing = document.getElementById('sourcePickerModal');
    if (existing) existing.remove();
    const html = `<div class="modal fade" id="sourcePickerModal" tabindex="-1">
        <div class="modal-dialog modal-lg">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">${invT('InvSelectSourceLine')}</h5>
                    <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                </div>
                <div class="modal-body p-0">
                    <table class="table table-sm table-hover mb-0">
                        <thead class="table-light">
                            <tr><th>${invT('InvColDocNo')}</th><th>#</th><th>${invT('InvColProduct')}</th><th class="text-end">${invT('InvColOrigQty')}</th><th class="text-end text-primary">${invT('InvColRemaining')}</th><th>${invT('InvColUom')}</th><th></th></tr>
                        </thead>
                        <tbody>
                            ${lines.map(l => `<tr>
                                <td>${l.docNo}</td>
                                <td>${l.lineNo}</td>
                                <td>${l.productCode}</td>
                                <td class="text-end">${l.qty}</td>
                                <td class="text-end fw-semibold text-primary">${l.remaining}</td>
                                <td>${l.uomCode ?? ''}</td>
                                <td><button type="button" class="btn btn-sm btn-primary btn-pick"
                                    data-val='${JSON.stringify(l)}'>${invT('InvActionSelect')}</button></td>
                            </tr>`).join('')}
                        </tbody>
                    </table>
                </div>
            </div>
        </div>
    </div>`;
    document.body.insertAdjacentHTML('beforeend', html);
    const modalEl = document.getElementById('sourcePickerModal');
    const modal = new bootstrap.Modal(modalEl);
    modalEl.addEventListener('click', e => {
        if (e.target.classList.contains('btn-pick')) {
            onSelect(JSON.parse(e.target.dataset.val));
            modal.hide();
        }
    });
    modalEl.addEventListener('hidden.bs.modal', () => modalEl.remove());
    modal.show();
};

window.fillSourceLine = function (tr, sel) {
    const srcId   = tr.querySelector('.line-source-id');
    const srcLine = tr.querySelector('.line-source-line');
    if (srcId)   srcId.value   = sel.docNo;
    if (srcLine) srcLine.value = sel.lineNo;
    const prodSel = tr.querySelector('.product-select');
    if (prodSel) {
        prodSel.value = sel.productCode;
        // 若 select2 啟用，先以 jQuery 同步 select2 顯示（trigger 只到 jQuery handler，不到原生委派）
        if (window.jQuery && jQuery.fn.select2) {
            jQuery(prodSel).val(sel.productCode).trigger('change');
        }
        // 再派發「會冒泡的原生 change」以觸發 data-action document 委派（onProductChange）。
        // 必須 bubbles:true 且為原生事件——jQuery trigger('change') 不會到達 document.addEventListener。
        prodSel.dispatchEvent(new Event('change', { bubbles: true }));
    }
    const qtyInp = tr.querySelector('.line-qty');
    if (qtyInp) qtyInp.value = sel.remaining;
};
