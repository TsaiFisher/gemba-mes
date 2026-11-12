/**
 * material-common.js — 料件作業四頁共用（工單發退料／備料作業／委外收料／領料退料申請）。
 *
 * 只放「四頁一模一樣」的東西：轉義、庫存儲位選擇片、送出（連線直送、離線排隊）。
 * 各頁自己的流程留在各自的 page.js。
 */
'use strict';

/** 進 DOM 前一律轉義：品號、儲位、批號都是使用者可維護的主檔欄位。 */
function matEsc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/**
 * 某一行的庫存儲位選擇片。點一下＝從這裡揀（帶入倉、儲位、批號）。
 * stagedLoc：工單已移入備料倉的儲位，標上「備料」並排第一（伺服器已排序）。
 */
function matStockChips(lineKey, rows, selectedLoc, stagedLoc) {
    if (!rows || rows.length === 0)
        return `<div class="small text-danger">${t('mat.no_stock')}</div>`;
    return rows.map(r => {
        const active = r.locationCode === selectedLoc ? ' active' : '';
        const staged = stagedLoc && r.locationCode === stagedLoc
            ? ` <span class="badge bg-info text-dark">${t('mat.staged')}</span>` : '';
        return `<button type="button" class="stock-chip${active}" data-action="pickStockChip"
                    data-line="${matEsc(lineKey)}" data-wh="${matEsc(r.whCode)}"
                    data-loc="${matEsc(r.locationCode)}" data-lot="${matEsc(r.lotNo || '')}">
                    <span>${matEsc(r.whCode)} / ${matEsc(r.locationCode)}${staged}</span>
                    <span class="text-muted">${r.lotNo ? matEsc(r.lotNo) + ' · ' : ''}${t('mat.available')}
                        <span class="chip-qty">${r.qtyAvailable}</span></span>
                </button>`;
    }).join('');
}

/**
 * 送出：連得上直接打 API；連不上時排進佇列（斷線保護，見 offline-queue.js submitOrQueue）。
 * allowQueue=false 的作業只能連線做（委外收料同時產生應付發票），連不上直接提示、不排隊。
 * 回傳 { ok, queued, result }。
 */
async function matSubmit(url, body, { allowQueue, label, priority = 5 }) {
    if (!allowQueue) {
        if (!navigator.onLine) { showToast(t('mat.online_only'), 'warning'); return { ok: false }; }
        const result = await apiCall('POST', url, body);
        return { ok: !!result, queued: false, result };
    }
    const { queued, result } = await submitOrQueue({ url, body, priority, label });
    if (queued) showToast(t('report.queued'), 'warning');
    return { ok: queued || !!result, queued, result };
}

function matNewClientFields() {
    return { clientId: crypto.randomUUID(), clientTimestamp: new Date().toISOString() };
}
