// 自 pick.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    const TYPE_LABELS = {
        PI: t('type.PI'), OI: t('type.OI'),
        PF: t('type.PF'), OT: t('type.OT'),
        IB: t('type.IB'), LR: t('type.LR'),
        BR: t('type.BR'), IL: t('type.IL'),
        CI: t('type.CI'), SR: t('type.SR'),
        CR: t('type.CR'), SO: t('type.SO'),
        IJ: t('type.IJ'), GS: t('type.GS'),
        GR: t('type.GR'), NO: t('type.NO'),
    };

    let allItems = [];
    let selected = null;
    let pickLot  = '';   // 這次下架的批號：點有批號的儲位列、或掃箱號時帶入；手打儲位＝無批號
    let scanner  = null;
    let scanModal = null;

    (async () => {
        const user = await requireAuth();
        if (!user) return;

        await offlineQueue.ready();
        scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
        scanner   = new BarcodeScanner({
            onScan: code => {
                scanModal.hide();
                scanner.stopCamera();
                applyLocationOrBox(code);
            }
        });

        await loadPending();
    })();

    async function loadPending() {
        document.getElementById('pendingList').innerHTML =
            `<div class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm me-1"></div>${t('common.loading')}</div>`;
        const data = await apiGet('/api/warehouse/pending-outbound');
        allItems = Array.isArray(data) ? data : (data?.items ?? []);
        renderList(allItems);
    }

    function searchDocs() {
        const kw = document.getElementById('docSearch').value.toLowerCase().trim();
        renderList(kw ? allItems.filter(i =>
            i.docNo?.toLowerCase().includes(kw) ||
            i.productCode?.toLowerCase().includes(kw)) : allItems);
    }

    // data-idx 必須是在完整清單（點選時讀的那一份）裡的位置，不是過濾後的位置——
    // 否則搜尋過後點選會選到別筆（2026-11-06 修正：曾導致手機下架選錯單據）。
    function renderList(items) {
        const el = document.getElementById('pendingList');
        if (!items.length) {
            el.innerHTML = `<div class="text-center text-muted py-3"><i class="bi bi-inbox me-1"></i>${t('pick.no_data')}</div>`;
            return;
        }
        el.innerHTML = items.map((it, idx) => `
            <div class="card pending-item mb-2 px-3 py-2" data-action="selectItem" data-idx="${allItems.indexOf(it)}">
                <div class="d-flex justify-content-between small">
                    <span class="fw-semibold">${it.docNo}</span>
                    <span class="badge bg-danger-subtle text-danger">${TYPE_LABELS[it.typeCode] ?? it.typeCode}</span>
                </div>
                <div class="small text-muted">${it.productCode} &nbsp;|&nbsp; ${t('pick.need_pre')}<strong class="text-danger">${it.pending ?? it.stockQty}</strong> ${it.stockUomCode}</div>
            </div>`).join('');
    }

    async function selectItem(e, el) {
        const idx = +el.dataset.idx;
        selected = allItems[idx];
        document.getElementById('selDocNo').textContent   = selected.docNo;
        document.getElementById('selProduct').textContent = selected.productCode;
        document.getElementById('selNeed').textContent    = `${selected.pending ?? selected.stockQty} ${selected.stockUomCode}`;
        document.getElementById('selUom').textContent     = selected.stockUomCode;
        document.getElementById('pickQty').value          = selected.pending ?? selected.stockQty;
        document.getElementById('stepDoc').style.display  = 'none';
        document.getElementById('stepPick').classList.remove('u-display-none');

        // 查詢各儲位可用庫存
        const stockEl = document.getElementById('stockRows');
        stockEl.innerHTML = t('pick.querying');
        const data = await apiGet(`/api/warehouse/stock/${encodeURIComponent(selected.productCode)}`);
        if (!data?.length) {
            stockEl.innerHTML = `<span class="text-muted">${t('pick.no_stock')}</span>`;
        } else {
            // stock API 不含 uomCode，用選中品項的 stockUomCode
            const uom = selected?.stockUomCode ?? '';
            stockEl.innerHTML = data.map(s => `
                <div class="stock-row d-flex justify-content-between align-items-center py-1 u-cursor-pointer u-border-radius-6px u-padding-4px-6px"
                    
                     data-action="selectLocation" data-loc-code="${escHtml(s.locationCode)}" data-lot="${escHtml(s.lotNo || '')}" data-qty="${s.qtyAvailable}">
                    <span class="text-primary fw-semibold">${escHtml(s.locationCode)}${s.lotNo ? ` <span class="text-muted fw-normal">${t('pick.lot')}${escHtml(s.lotNo)}</span>` : ''}</span>
                    <span class="fw-semibold">${s.qtyOnHand} ${uom}
                        <i class="bi bi-arrow-right-circle ms-1 text-primary u-font-size-p8rem"></i>
                    </span>
                </div>`).join('');
        }
    }

    function selectLocation(e, el) {
        const locCode = el.dataset.locCode;
        const availQty = parseFloat(el.dataset.qty) || 0;
        document.getElementById('locationCode').value = locCode;
        setPickLot(el.dataset.lot || '');
        // 建議數量 = min(可用, 需下架)
        const need = parseFloat(document.getElementById('pickQty').value) || 0;
        if (availQty > 0 && need > 0)
            document.getElementById('pickQty').value = Math.min(availQty, need);
    }

    function resetStep() {
        selected = null;
        document.getElementById('stepDoc').style.display  = 'block';
        document.getElementById('stepPick').classList.add('u-display-none');
        document.getElementById('locationCode').value     = '';
        setPickLot('');
    }

    function setPickLot(lot, boxId) {
        pickLot = lot || '';
        document.getElementById('pickLotInfo').textContent =
            !pickLot ? '' : boxId ? tf('pick.box_of', boxId, pickLot) : t('pick.lot') + pickLot;
    }

    // 儲位欄手動改了：照原樣當儲位、不帶批號；但掃到的若是箱號就換成箱子所在儲位＋批號
    function onLocationInput(e, inp) { applyLocationOrBox(inp.value.trim()); }

    // 掃箱號（container-tracking.md §9）：整箱下架——帶出箱子所在儲位、批號，數量＝min(箱內, 需下架)。
    // 不是箱號（404）就當儲位代碼。直接 fetch 而非 apiGet：404 是正常結果，不該跳錯誤 Toast。
    async function applyLocationOrBox(code) {
        const locInp = document.getElementById('locationCode');
        let box = null;
        if (code) {
            try {
                const r = await fetch(`/api/warehouse/container-unit/${encodeURIComponent(code)}`, { credentials: 'include' });
                if (r.ok) box = await r.json();
            } catch (err) { /* 連不上就當儲位 */ }
        }
        if (!box) { locInp.value = code; setPickLot(''); return; }
        if (!selected || box.productCode !== selected.productCode) {
            locInp.value = '';
            setPickLot('');
            showToast(tf('pick.box_other_product', box.containerUnitId, box.productCode), 'warning');
            return;
        }
        const loc = box.locations[0];
        locInp.value = loc.locationCode;
        setPickLot(box.lotNo, box.containerUnitId);
        const need = parseFloat(selected.pending ?? selected.stockQty) || 0;
        document.getElementById('pickQty').value = need > 0 ? Math.min(loc.qty, need) : loc.qty;
    }

    function escHtml(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    async function submitPick() {
        const loc = document.getElementById('locationCode').value.trim();
        const qty = parseFloat(document.getElementById('pickQty').value) || 0;
        if (!loc)  { showToast(t('pick.err_loc'), 'warning'); return; }
        if (qty <= 0) { showToast(t('pick.err_qty'), 'warning'); return; }

        const body = {
            typeCode: selected.typeCode,
            docNo:    selected.docNo,
            lineNo:   selected.lineNo,
            picks: [{ locationCode: loc, lotNo: pickLot, qty }]
        };

        const btn = document.getElementById('btnPick');
        setLoading(btn, true);

        const { queued, result } = await submitOrQueue({ url: '/api/warehouse/pick', body,
            priority: 3, label: tf('pick.queue_label', selected.productCode, loc) });
        setLoading(btn, false);
        if (queued) {
            showToast(t('pick.offline'), 'warning');
            resetStep();
            return;
        }
        if (!result) return;
        showToast(t('pick.success'), 'success');
        resetStep();
        await loadPending();
    }

    function openLocScanner() {
        scanModal.show();
        scanner.startCamera('reader');
    }
    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => scanner.stopCamera());
