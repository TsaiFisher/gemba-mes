// 庫存盤點：三種模式共用一頁。
// 1) 依清冊盤點：選一張 PC 建立的清冊 → 逐項輸入初盤量 → 批次送出。
// 2) 自由盤點：可連續掃多個儲位/品號，累積成待送出清單，最後一次送出
//    （後端依「使用者＋廠別」自動找/建 WithoutSheet 清冊，取代舊版單一儲位即送出即定案的 cycle-count）。
// 3) 複盤：列出待複盤（非本人初盤）的項目，輸入複盤量後批次送出。
let mode = 'sheet';

// ── 模式一：依清冊 ──────────────────────────────────────────────────────────
let currentSheetId = null;
let sheetItems = [];

// ── 模式二：自由盤點 ────────────────────────────────────────────────────────
let currentLoc = null;
let currentWhCode = null;
let stockRows = [];   // 目前掃描儲位下的品項 [{productCode, qtyOnHand, uomCode, lotNo}]
let cartLines = [];   // 已加入待送出清單（尚未送出）[{whCode, locationCode, productCode, lotNo, qty, uomCode}]

// ── 模式三：複盤 ────────────────────────────────────────────────────────────
let recountItems = [];

let scanner  = null;
let scanModal = null;
let uomDecimals = {};

(async () => {
    const user = await requireAuth();
    if (!user) return;

    uomDecimals = await getUomDecimalsMap();
    await offlineQueue.ready();
    scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
    scanner = new BarcodeScanner({
        onScan: code => {
            document.getElementById('locInput').value = code;
            scanModal.hide();
            scanner.stopCamera();
            loadLocation();
        }
    });

    await loadSheets();
})();

// 離開頁面時若還有未送出的自由盤點清單，提醒使用者避免漏送。
window.addEventListener('beforeunload', e => {
    if (cartLines.length > 0) {
        e.preventDefault();
        e.returnValue = '';
    }
});

// ── 模式切換 ─────────────────────────────────────────────────────────────────

function switchMode(e, el) {
    mode = el.dataset.mode;
    document.querySelectorAll('#modeTabs .nav-link').forEach(b => b.classList.toggle('active', b === el));
    document.getElementById('modeSheet').classList.toggle('u-display-none', mode !== 'sheet');
    document.getElementById('modeFree').classList.toggle('u-display-none', mode !== 'free');
    document.getElementById('modeRecount').classList.toggle('u-display-none', mode !== 'recount');
    document.getElementById('resultCard').classList.add('u-display-none');

    if (mode === 'sheet') loadSheets();
    else if (mode === 'recount') loadRecountPending();
}

// ── 送出：連不上就排進佇列，連線後自動送出（斷線保護）─────────────────────────
// 後端以 clientId 去重（SyncLog），佇列重送不會多建明細。
// ⚠️ 帳面量 BookQty 取的是**送達伺服器當下**的庫存（2026-09-25 裁決），
//    斷線期間若有進出，差異會包含那段期間的異動。
// 有明細失敗（例如斷線期間別人已覆盤）時，佇列在待同步清單留黃字，那幾行要重盤。
async function submitCount(btn, url, lines, labelKey) {
    setLoading(btn, true);
    const r = await submitOrQueue({ url, body: { lines }, priority: 5, label: tf(labelKey, lines.length) });
    setLoading(btn, false);
    if (r.queued) showToast(t('count.offline'), 'warning');
    return r;
}

// ══════════════════════ 模式一：依清冊盤點 ══════════════════════════════════

async function loadSheets(quietWhenEmpty = false) {
    const result = await apiGet('/api/mobile/inv/count/sheets');
    if (!result) return;

    const sel = document.getElementById('sheetSelect');
    const list = result.data || [];
    sel.innerHTML = `<option value="">${t('count.sheet_ph')}</option>` +
        list.map(s => `<option value="${s.sheetId}">${s.sheetId}（${t('count.pending_items')} ${s.pendingCount}/${s.totalCount}）</option>`).join('');

    if (!list.length && !quietWhenEmpty) showToast(t('count.no_sheets'), 'warning');
}

async function onSheetChange(e, sel) {
    currentSheetId = sel.value || null;
    if (!currentSheetId) {
        document.getElementById('sheetItemsCard').classList.add('u-display-none');
        document.getElementById('btnSubmitSheet').classList.add('u-display-none');
        return;
    }
    await loadSheetItems();
}

async function reloadSheetItems() { await loadSheetItems(); }

async function loadSheetItems() {
    if (!currentSheetId) return;
    const result = await apiGet(`/api/mobile/inv/count/${encodeURIComponent(currentSheetId)}/items?onlyPending=true`);
    if (!result) return;

    sheetItems = result.data || [];
    const el = document.getElementById('sheetItemRows');
    document.getElementById('sheetItemsCard').classList.remove('u-display-none');

    if (!sheetItems.length) {
        el.innerHTML = `<div class="p-3 text-muted small text-center">${t('common.no_data')}</div>`;
        document.getElementById('btnSubmitSheet').classList.add('u-display-none');
        return;
    }

    el.innerHTML = sheetItems.map((it, idx) => `
        <div class="product-row p-3 border-bottom" id="sheet-row-${idx}">
            <div class="d-flex justify-content-between align-items-center mb-1">
                <div>
                    <div class="fw-semibold small">${it.productCode}</div>
                    <div class="text-muted u-font-size-p75rem">${it.whCode} ${it.locationCode}${it.lotNo ? ' / ' + it.lotNo : ''}</div>
                </div>
                <div class="text-end">
                    <div class="text-muted u-font-size-p75rem">${t('count.item_book_qty')}${it.bookQty}</div>
                </div>
            </div>
            <div class="d-flex align-items-center gap-2">
                <label class="small text-muted u-white-space-nowrap">${t('count.actual_label')}</label>
                <input type="number" class="form-control count-input btn-mobile"
                       id="sheet-qty-${idx}" value="${it.bookQty}" min="0" step="any" inputmode="decimal">
            </div>
        </div>`).join('');

    document.getElementById('btnSubmitSheet').classList.remove('u-display-none');
}

async function submitSheetCount(e, btn) {
    const lines = sheetItems.map((it, idx) => ({
        itemId: it.itemId,
        qty: parseFloat(document.getElementById(`sheet-qty-${idx}`).value) || 0
    }));
    if (!lines.length) return;

    const { queued, result } = await submitCount(btn, '/api/mobile/inv/count/first-count', lines, 'count.queue_label');
    if (queued) {
        // 已排入的就從畫面拿掉，避免同一批被再按一次（再按會變成第二筆排隊）
        document.getElementById('sheetItemsCard').classList.add('u-display-none');
        document.getElementById('btnSubmitSheet').classList.add('u-display-none');
        return;
    }
    if (!result) return;

    showResult(result, result.data);
    await loadSheetItems();
    // 剛盤完最後一張清冊，清單變空是預期結果：不要再跳「目前無進行中的盤點清冊」黃色警告——
    // 它會蓋過剛才的成功提示，現場看起來像送出失敗（2026-11-09 nightly MobileStockCountE2ETests 抓到）
    await loadSheets(true);
}

// ══════════════════════ 模式二：自由盤點（累積清單） ═════════════════════════

async function loadLocation() {
    let loc = document.getElementById('locInput').value.trim().toUpperCase();
    if (!loc) { showToast(t('count.err_loc'), 'warning'); return; }

    // 掃到箱號（container-tracking.md §9）＝只盤這一箱：換成箱子所在儲位，品項只留箱子的品號＋批號。
    // 不是箱號（404）就當儲位代碼。
    let box = null;
    try {
        const r = await fetch(`/api/warehouse/container-unit/${encodeURIComponent(loc)}`, { credentials: 'include' });
        if (r.ok) box = await r.json();
    } catch (err) { /* 連不上就當儲位 */ }
    if (box) {
        if (box.locations.length !== 1) { showToast(tf('count.box_multi_loc', box.containerUnitId), 'warning'); return; }
        loc = box.locations[0].locationCode;
    }

    const locData = await apiGet(`/api/warehouse/location/${encodeURIComponent(loc)}`);
    // 回應是 ASP.NET Core 預設的 camelCase。原本讀 locData.LocationCode（PascalCase）恆為 undefined，
    // 這裡直接 return——自由盤點輸入儲位後畫面毫無反應，從未能走到下一步（2026-10-20 UI 測試發現）。
    if (!locData?.locationCode) return;

    currentLoc    = locData.locationCode;
    currentWhCode = locData.whCode;

    const stocks = await apiGet(`/api/warehouse/stock-by-location/${encodeURIComponent(loc)}`);
    stockRows = Array.isArray(stocks) ? stocks : [];
    if (box) stockRows = stockRows.filter(s => s.productCode === box.productCode && (s.lotNo || '') === box.lotNo);

    renderProductRows();
    document.getElementById('locTitle').textContent = `${currentLoc}（${locData.whName || locData.whCode}）`
        + (box ? ` · ${tf('count.box_title', box.containerUnitId)}` : '');
    document.getElementById('stepLoc').style.display   = 'none';
    document.getElementById('stepCount').classList.remove('u-display-none');
}

function renderProductRows() {
    const el = document.getElementById('productRows');
    if (!stockRows.length) {
        el.innerHTML = `
            <div class="p-3 text-muted small text-center">
                ${t('count.no_stock')}<br>
                <small>${t('count.no_stock_hint')}</small>
            </div>`;
    } else {
        el.innerHTML = stockRows.map((s, idx) => `
            <div class="product-row p-3 border-bottom" id="row-${idx}">
                <div class="d-flex justify-content-between align-items-center mb-1">
                    <div>
                        <div class="fw-semibold small">${s.productCode}</div>
                        <div class="text-muted u-font-size-p75rem">${s.productName ?? ''}${s.lotNo ? ' / ' + s.lotNo : ''}</div>
                    </div>
                    <div class="text-end">
                        <div class="text-muted u-font-size-p75rem">${t('count.sys_label')}${s.qtyOnHand} ${s.uomCode}</div>
                    </div>
                </div>
                <div class="d-flex align-items-center gap-2">
                    <label class="small text-muted u-white-space-nowrap">${t('count.actual_label')}</label>
                    <input type="number" class="form-control count-input btn-mobile"
                           id="qty-${idx}" value="${s.qtyOnHand}"
                           min="0" step="any" inputmode="decimal"
                           data-action-change="updateDiff" data-idx="${idx}" data-onhand="${s.qtyOnHand}" data-uom="${s.uomCode ?? ''}">
                    <span class="small text-muted">${s.uomCode}</span>
                </div>
                <div class="small mt-1" id="diff-${idx}"></div>
            </div>`).join('');
    }
}

function updateDiff(e, inp) {
    const idx = inp.dataset.idx;
    const uom = inp.dataset.uom || '';
    const sysQty = parseFloat(inp.dataset.onhand) || 0;
    const counted = parseFloat(document.getElementById(`qty-${idx}`).value) || 0;
    const diff = counted - sysQty;
    const el = document.getElementById(`diff-${idx}`);
    const row = document.getElementById(`row-${idx}`);
    if (Math.abs(diff) < 0.000001) {
        el.innerHTML = '';
        row.classList.remove('has-diff');
    } else {
        const sign = diff > 0 ? '+' : '';
        el.innerHTML = `<span class="${diff > 0 ? 'diff-pos' : 'diff-neg'}">${t('count.diff_pre')}${sign}${formatQty(diff, uom, uomDecimals)}</span>`;
        row.classList.add('has-diff');
    }
}

function addToCart() {
    if (!stockRows.length) { resetLoc(); return; }

    stockRows.forEach((s, idx) => {
        const qty = parseFloat(document.getElementById(`qty-${idx}`).value) || 0;
        const key = `${currentLoc}|${s.productCode}|${s.lotNo || ''}`;
        const existingIdx = cartLines.findIndex(l => l.key === key);
        const line = {
            key, whCode: currentWhCode, locationCode: currentLoc,
            productCode: s.productCode, lotNo: s.lotNo || null,
            qty, uomCode: s.uomCode
        };
        if (existingIdx >= 0) cartLines[existingIdx] = line;
        else cartLines.push(line);
    });

    showToast(t('count.add_to_cart'), 'success');
    renderCart();
    resetLoc();
}

function renderCart() {
    const card = document.getElementById('cartCard');
    const btn  = document.getElementById('btnSubmitCart');
    document.getElementById('cartCount').textContent = cartLines.length;

    if (!cartLines.length) {
        card.classList.add('u-display-none');
        btn.classList.add('u-display-none');
        return;
    }

    card.classList.remove('u-display-none');
    btn.classList.remove('u-display-none');

    document.getElementById('cartRows').innerHTML = cartLines.map((l, idx) => `
        <div class="d-flex justify-content-between align-items-center p-2 border-bottom">
            <div class="small">
                <div class="fw-semibold">${l.productCode}</div>
                <div class="text-muted u-font-size-p75rem">${l.whCode} ${l.locationCode}${l.lotNo ? ' / ' + l.lotNo : ''} — ${l.qty} ${l.uomCode || ''}</div>
            </div>
            <button class="btn btn-sm btn-outline-danger" data-action="removeCartLine" data-idx="${idx}">
                <i class="bi bi-x"></i>
            </button>
        </div>`).join('');
}

function removeCartLine(e, btn) {
    cartLines.splice(parseInt(btn.dataset.idx, 10), 1);
    renderCart();
}

function resetLoc() {
    currentLoc = null;
    stockRows = [];
    document.getElementById('locInput').value = '';
    document.getElementById('stepLoc').style.display   = 'block';
    document.getElementById('stepCount').classList.add('u-display-none');
}

async function submitCart(e, btn) {
    if (!cartLines.length) return;
    const lines = cartLines.map(l => ({
        productCode: l.productCode, locationCode: l.locationCode,
        whCode: l.whCode, lotNo: l.lotNo, qty: l.qty
    }));

    const { queued, result } = await submitCount(btn, '/api/mobile/inv/count/first-count', lines, 'count.queue_label');
    if (queued) {
        cartLines = [];
        renderCart();
        return;
    }
    if (!result) return;

    showResult(result, result.data);
    cartLines = [];
    renderCart();
}

function openLocScanner() {
    scanModal.show();
    scanner.startCamera('reader');
}

// ══════════════════════ 模式三：複盤 ══════════════════════════════════════

async function reloadRecount() { await loadRecountPending(); }

async function loadRecountPending() {
    const result = await apiGet('/api/mobile/inv/count/recount-pending');
    if (!result) return;

    recountItems = result.data || [];
    const el = document.getElementById('recountRows');
    const btn = document.getElementById('btnSubmitRecount');

    if (!recountItems.length) {
        el.innerHTML = `<div class="p-3 text-muted small text-center">${t('count.recount_empty')}</div>`;
        btn.classList.add('u-display-none');
        return;
    }

    el.innerHTML = recountItems.map((it, idx) => `
        <div class="product-row p-3 border-bottom" id="recount-row-${idx}">
            <div class="d-flex justify-content-between align-items-center mb-1">
                <div>
                    <div class="fw-semibold small">${it.productCode}</div>
                    <div class="text-muted u-font-size-p75rem">${it.whCode} ${it.locationCode}${it.lotNo ? ' / ' + it.lotNo : ''}</div>
                </div>
                <div class="text-end">
                    <div class="text-muted u-font-size-p75rem">${t('count.item_book_qty')}${it.bookQty}</div>
                    <div class="text-muted u-font-size-p75rem">${t('count.first_qty')}${it.firstCountQty}</div>
                </div>
            </div>
            <div class="d-flex align-items-center gap-2">
                <label class="small text-muted u-white-space-nowrap">${t('count.recount_qty')}</label>
                <input type="number" class="form-control count-input btn-mobile"
                       id="recount-qty-${idx}" value="${it.firstCountQty}" min="0" step="any" inputmode="decimal">
            </div>
        </div>`).join('');

    btn.classList.remove('u-display-none');
}

async function submitRecount(e, btn) {
    const lines = recountItems.map((it, idx) => ({
        itemId: it.itemId,
        qty: parseFloat(document.getElementById(`recount-qty-${idx}`).value) || 0
    }));
    if (!lines.length) return;

    const { queued, result } = await submitCount(btn, '/api/mobile/inv/count/recount', lines, 'count.recount_queue_label');
    if (queued) {
        recountItems = [];
        document.getElementById('recountRows').innerHTML = '';
        document.getElementById('btnSubmitRecount').classList.add('u-display-none');
        return;
    }
    if (!result) return;

    showResult(result, result.data);
    await loadRecountPending();
}

// ── 共用：批次結果顯示 ───────────────────────────────────────────────────────

function showResult(result, lineResults) {
    const okCount = (lineResults || []).filter(r => r.success).length;
    const failCount = (lineResults || []).length - okCount;
    const hasFail = failCount > 0;

    const hdr = document.getElementById('resultHeader');
    const bdy = document.getElementById('resultBody');
    hdr.className = `card-header py-2 ${hasFail ? 'bg-warning' : 'bg-success text-white'}`;
    hdr.innerHTML = `<i class="bi bi-${hasFail ? 'exclamation-triangle' : 'check-circle'} me-1"></i>${result.message || ''}`;
    bdy.innerHTML = hasFail
        ? (lineResults || []).filter(r => !r.success).map(r => `<div>${r.itemId ?? ''}：${r.message}</div>`).join('')
        : `<div class="text-success">${t('count.all_match')}</div>`;

    document.getElementById('resultCard').classList.remove('u-display-none');
    showToast(result.message, hasFail ? 'warning' : 'success');
}
