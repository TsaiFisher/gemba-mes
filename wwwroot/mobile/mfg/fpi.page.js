// FPI 首件檢查 — 派工清單（自 fpi.html 分離，CSP script-src 用 nonce，靜態 HTML 無法帶 nonce）。
// 僅列「待首件（PendingFPI）」派工；點擊進入共用量測頁 ipqc-process.html?orderType=FPI。
    let allDispatches = [];

    (async () => {
        const user = await requireAuth();
        if (!user) return;
        await loadDispatches();
    })();

    async function loadDispatches() {
        document.getElementById('dispatchList').innerHTML =
            `<div class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm me-1"></div>${t('common.loading')}</div>`;
        const data = await apiGet('/api/mobile/dispatches?status=PendingFPI');
        allDispatches = data?.data ?? [];
        renderList(allDispatches);
    }

    function searchDispatches() {
        const kw = document.getElementById('dispatchSearch').value.toLowerCase().trim();
        renderList(kw ? allDispatches.filter(d =>
            d.workOrderId?.toLowerCase().includes(kw) ||
            d.machineCode?.toLowerCase().includes(kw)) : allDispatches);
    }

    // data-idx 必須是在完整清單（點選時讀的那一份）裡的位置，不是過濾後的位置——
    // 否則搜尋過後點選會選到別筆（2026-11-06 修正：曾導致手機下架選錯單據）。
    function renderList(items) {
        const el = document.getElementById('dispatchList');
        if (!items.length) {
            el.innerHTML = `<div class="text-center text-muted py-3"><i class="bi bi-check2-circle me-1"></i>${t('fpi.no_data')}</div>`;
            return;
        }
        el.innerHTML = items.map((d, i) => `
            <div class="card dispatch-item mb-2 px-3 py-2" data-action="openProcess" data-idx="${allDispatches.indexOf(d)}">
                <div class="d-flex justify-content-between small">
                    <span class="fw-semibold">${d.workOrderId}</span>
                    <span class="badge bg-primary">${t('dispatch.status.pendingfpi')}</span>
                </div>
                <div class="small text-muted">
                    ${t('ipqc.op_seq')} ${d.operationSeq ?? '—'} &nbsp;|&nbsp; ${t('ipqc.machine')}：${d.machineCode ?? '—'}
                </div>
                <div class="mt-1">
                    <span class="badge bg-primary-subtle text-primary small">
                        <i class="bi bi-clipboard2-check me-1"></i>${t('fpi.start_btn')}
                    </span>
                </div>
            </div>`).join('');
    }

    function openProcess(e, el) {
        const idx = +el.dataset.idx;
        const d = allDispatches[idx];
        const params = new URLSearchParams({
            orderType:    'FPI',
            dispatchId:   d.dispatchId,
            workOrderId:  d.workOrderId ?? '',
            machineCode:  d.machineCode ?? '',
            operationSeq: d.operationSeq ?? '',
            qty:          d.quantity ?? 1
        });
        window.location.href = `/mobile/mfg/ipqc-process.html?${params}`;
    }
