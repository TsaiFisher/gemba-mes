// 自 putaway.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    const TYPE_LABELS = {
        PI: t('type.PI'), OI: t('type.OI'),
        PF: t('type.PF'), OT: t('type.OT'),
        IB: t('type.IB'), LR: t('type.LR'),
        CI: t('type.CI'), SR: t('type.SR'),
        GR: t('type.GR'), NO: t('type.NO'),
    };

    let allItems = [];
    let selected = null;
    let scanner  = null;
    let scanModal = null;

    (async () => {
        const user = await requireAuth();
        if (!user) return;

        await offlineQueue.ready();
        scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
        scanner   = new BarcodeScanner({
            onScan: code => {
                document.getElementById('locationCode').value = code;
                scanModal.hide();
                scanner.stopCamera();
            }
        });

        await loadPending();
    })();

    async function loadPending() {
        document.getElementById('pendingList').innerHTML =
            `<div class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm me-1"></div>${t('common.loading')}</div>`;
        const data = await apiGet('/api/warehouse/pending-inbound');
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
            el.innerHTML = `<div class="text-center text-muted py-3"><i class="bi bi-inbox me-1"></i>${t('putaway.no_data')}</div>`;
            return;
        }
        el.innerHTML = items.map((it, idx) => `
            <div class="card pending-item mb-2 px-3 py-2" data-action="selectItem" data-idx="${allItems.indexOf(it)}">
                <div class="d-flex justify-content-between small">
                    <span class="fw-semibold">${it.docNo}</span>
                    <span class="badge bg-primary-subtle text-primary">${TYPE_LABELS[it.typeCode] ?? it.typeCode}</span>
                </div>
                <div class="small text-muted">${it.productCode} &nbsp;|&nbsp; ${t('putaway.pending_pre')}<strong class="text-primary">${it.pending ?? it.stockQty}</strong> ${it.stockUomCode}</div>
            </div>`).join('');
    }

    function selectItem(e, el) {
        const idx = +el.dataset.idx;
        selected = allItems[idx];
        document.getElementById('selDocNo').textContent   = selected.docNo;
        document.getElementById('selProduct').textContent = selected.productCode;
        document.getElementById('selPending').textContent = `${selected.pending ?? selected.stockQty} ${selected.stockUomCode}`;
        document.getElementById('selUom').textContent     = selected.stockUomCode;
        document.getElementById('putQty').value           = selected.pending ?? selected.stockQty;
        document.getElementById('stepDoc').style.display     = 'none';
        document.getElementById('stepPutAway').classList.remove('u-display-none');
    }

    function resetStep() {
        selected = null;
        document.getElementById('stepDoc').style.display     = 'block';
        document.getElementById('stepPutAway').classList.add('u-display-none');
        document.getElementById('locationCode').value        = '';
    }

    async function submitPutAway() {
        const loc = document.getElementById('locationCode').value.trim();
        const qty = parseFloat(document.getElementById('putQty').value) || 0;
        if (!loc)  { showToast(t('putaway.err_loc'), 'warning'); return; }
        if (qty <= 0) { showToast(t('putaway.err_qty'), 'warning'); return; }

        const body = {
            typeCode:     selected.typeCode,
            // docType：同一個 TypeCode 可能來自不同單別（委外收貨與進貨單都是 OI）
            docType:      selected.docType,
            docNo:        selected.docNo,
            lineNo:       selected.lineNo,
            locationCode: loc,
            qty,
            containerUnitId: document.getElementById('boxId').value.trim() || null
        };

        const btn = document.getElementById('btnPutAway');
        setLoading(btn, true);

        const { queued, result } = await submitOrQueue({ url: '/api/warehouse/put-away', body,
            priority: 3, label: tf('putaway.queue_label', selected.productCode, loc) });
        setLoading(btn, false);
        if (queued) {
            showToast(t('putaway.offline'), 'warning');
            resetStep();
            return;
        }
        if (!result) return;
        // 裝了箱子時伺服器訊息帶著產生的批號，照原文顯示
        showToast(body.containerUnitId && result.message ? result.message : t('putaway.success'), 'success');
        document.getElementById('boxId').value = '';
        resetStep();
        await loadPending();
    }

    function openLocScanner() {
        scanModal.show();
        scanner.startCamera('reader');
    }
    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => scanner.stopCamera());
