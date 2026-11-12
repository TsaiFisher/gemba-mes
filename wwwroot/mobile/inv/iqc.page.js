// 自 iqc.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    let allItems = [];
    let selected = null;
    let calcSampleSize = 0;

    (async () => {
        const user = await requireAuth();
        if (!user) return;
        await loadPending();
    })();

    async function loadPending() {
        document.getElementById('pendingList').innerHTML =
            `<div class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm me-1"></div>${t('common.loading')}</div>`;
        const data = await apiGet('/api/mobile/inv/iqc/pending');
        allItems = data?.data ?? [];
        renderList(allItems);
    }

    function searchItems() {
        const kw = document.getElementById('docSearch').value.toLowerCase().trim();
        renderList(kw ? allItems.filter(i =>
            i.goodsReceiptId?.toLowerCase().includes(kw) ||
            i.productCode?.toLowerCase().includes(kw)) : allItems);
    }

    // data-idx 必須是在完整清單（點選時讀的那一份）裡的位置，不是過濾後的位置——
    // 否則搜尋過後點選會選到別筆（2026-11-06 修正：曾導致手機下架選錯單據）。
    function renderList(items) {
        const el = document.getElementById('pendingList');
        if (!items.length) {
            el.innerHTML = `<div class="text-center text-muted py-3"><i class="bi bi-inbox me-1"></i>${t('iqc.no_data')}</div>`;
            return;
        }
        el.innerHTML = items.map((it, idx) => `
            <div class="card pending-item mb-2 px-3 py-2" data-action="selectItem" data-idx="${allItems.indexOf(it)}">
                <div class="d-flex justify-content-between small">
                    <span class="fw-semibold">${it.goodsReceiptId}</span>
                    <span>${it.sourceKind === 'OutsourceReceipt' ? `<span class="badge bg-info text-dark me-1">${t('iqc.src_outsource')}</span>` : ''}<span class="badge u-background-6f42c1">${it.typeCode}</span></span>
                </div>
                <div class="small text-muted mb-1">
                    ${it.productCode} &nbsp;|&nbsp; ${t('iqc.arrival_pre')}：<strong>${it.qty}</strong> ${it.uomCode}
                </div>
                <div class="d-flex gap-2 flex-wrap mt-1">
                    <span class="badge bg-light text-dark border small">MIL-STD-1916 ${it.vlLabel ?? 'VL-IV'}</span>
                    <span class="badge u-background-e8deff u-color-5a2d82">n = ${it.recommendedSampleSize ?? '—'}</span>
                    <span class="badge u-background-d1fae5 u-color-065f46 u-border-1px-solid-6ee7b7">Ac = 0</span>
                    <span class="badge u-background-fee2e2 u-color-7f1d1d u-border-1px-solid-fca5a5">Re = 1</span>
                </div>
            </div>`).join('');
    }

    function selectItem(e, el) {
        const idx = +el.dataset.idx;
        selected = allItems[idx];

        document.getElementById('selDocNo').textContent  = tf('iqc.line_pre', selected.goodsReceiptId, selected.lineNo);
        document.getElementById('selProduct').textContent = selected.productCode;
        document.getElementById('selQty').textContent    = selected.qty;
        document.getElementById('selUom').textContent    = selected.uomCode;

        // 套用規格建議的 VL
        const vl = selected.verificationLevel ?? 4;
        document.getElementById('vlSelect').value = vl;
        document.getElementById('selVl').textContent = `VL-${vl}`;

        // 計算並顯示應抽樣數
        calcSampleSize = selected.recommendedSampleSize ?? 0;
        showCalcN(calcSampleSize);

        // 重置輸入
        document.getElementById('sampleQty').value = calcSampleSize;
        document.getElementById('failQty').value   = 0;
        document.getElementById('remarks').value   = '';
        updateResult();

        document.getElementById('stepList').style.display = 'none';
        document.getElementById('stepForm').classList.remove('u-display-none');
    }

    function resetForm() {
        selected = null;
        document.getElementById('stepList').style.display = 'block';
        document.getElementById('stepForm').classList.add('u-display-none');
    }

    async function recalcN() {
        if (!selected) return;
        const vl = parseInt(document.getElementById('vlSelect').value) || 4;
        document.getElementById('selVl').textContent = `VL-${vl}`;
        const data = await apiGet(`/api/mobile/inv/iqc/sample-size?lotSize=${selected.qty}&vl=${vl}`);
        if (!data) return;
        calcSampleSize = data.data?.sampleSize ?? calcSampleSize;
        showCalcN(calcSampleSize);
        document.getElementById('sampleQty').value = calcSampleSize;
    }

    function showCalcN(n) {
        document.getElementById('calcN').textContent = tf('iqc.pcs', n);
        document.getElementById('nHint').textContent = n;
    }

    function onSampleChange() { updateResult(); }

    function onFailChange() {
        const fail = parseFloat(document.getElementById('failQty').value) || 0;
        document.getElementById('failWarn').classList.toggle('show', fail > 0);
        updateResult();
    }

    function updateResult() {
        const fail = parseFloat(document.getElementById('failQty').value) || 0;
        const preview = document.getElementById('resultPreview');
        if (fail >= 1) {
            preview.className = 'result-badge-fail fw-bold fs-6';
            preview.textContent = t('iqc.fail_preview');
        } else {
            preview.className = 'result-badge-pass fw-bold fs-6';
            preview.textContent = t('iqc.pass_preview');
        }
    }

    async function submitIqc() {
        const sampleQty = parseFloat(document.getElementById('sampleQty').value) || 0;
        const failQty   = parseFloat(document.getElementById('failQty').value)   || 0;
        const vl        = parseInt(document.getElementById('vlSelect').value) || 4;

        if (sampleQty <= 0) { showToast(t('iqc.err_sample'), 'warning'); return; }
        if (sampleQty < calcSampleSize) {
            if (!confirm(tf('iqc.confirm_under', sampleQty, calcSampleSize))) return;
        }

        const body = {
            goodsReceiptId:    selected.goodsReceiptId,
            lineNo:            selected.lineNo,
            productCode:       selected.productCode,
            lotSize:           selected.qty,
            sampleQty,
            failQty,
            verificationLevel: vl,
            remarks:           document.getElementById('remarks').value.trim() || null
        };

        const btn = document.getElementById('btnSubmit');
        setLoading(btn, true);
        const res = await apiCall('POST', '/api/mobile/inv/iqc', body);
        setLoading(btn, false);
        if (!res) return;

        const isPass = failQty === 0;
        showToast(res.message, isPass ? 'success' : 'danger');
        resetForm();
        await loadPending();
    }
