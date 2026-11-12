// 自 ipqc.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    let allDispatches = [];

    (async () => {
        const user = await requireAuth();
        if (!user) return;
        await loadDispatches();
    })();

    async function loadDispatches() {
        document.getElementById('dispatchList').innerHTML =
            `<div class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm me-1"></div>${t('common.loading')}</div>`;
        const data = await apiGet('/api/mobile/dispatches?status=InProgress');
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
            el.innerHTML = `<div class="text-center text-muted py-3"><i class="bi bi-inbox me-1"></i>${t('ipqc.no_data')}</div>`;
            return;
        }
        el.innerHTML = items.map((d, i) => `
            <div class="card dispatch-item mb-2 px-3 py-2" data-action="openProcess" data-idx="${allDispatches.indexOf(d)}">
                <div class="d-flex justify-content-between small">
                    <span class="fw-semibold">${d.workOrderId}</span>
                    <span class="badge bg-success">${t('dispatch.status.inprogress')}</span>
                </div>
                <div class="small text-muted">
                    ${t('ipqc.op_seq')} ${d.operationSeq ?? '—'} &nbsp;|&nbsp; ${t('ipqc.machine')}：${d.machineCode ?? '—'}
                </div>
                <div class="d-flex gap-2 flex-wrap mt-1">
                    <span class="small text-muted">${t('ipqc.reported')}：${d.qtyDone ?? 0} / ${d.quantity ?? 0}</span>
                </div>
                <div class="mt-1">
                    <span class="badge bg-warning text-dark small">
                        <i class="bi bi-clipboard2-pulse me-1"></i>${t('ipqc.start_btn')}
                    </span>
                </div>
            </div>`).join('');
    }

    function openProcess(e, el) {
        const idx = +el.dataset.idx;
        const d = allDispatches[idx];
        const params = new URLSearchParams({
            dispatchId:   d.dispatchId,
            workOrderId:  d.workOrderId ?? '',
            machineCode:  d.machineCode ?? '',
            operationSeq: d.operationSeq ?? '',
            qty:          d.quantity ?? 1
        });
        window.location.href = `/mobile/mfg/ipqc-process.html?${params}`;
    }
