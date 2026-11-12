// 自 reports.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    const params     = new URLSearchParams(location.search);
    const dispatchId = params.get('dispatchId');
    const sessionId  = params.get('sessionId');

    let dispatchData  = null;
    let reportHistory = [];   // 本頁報工紀錄（記憶體）
    let scanner  = null;
    let scanModal = null;
    let containerManualVisible = false;

    // ── 初始化 ────────────────────────────────────────────────────────────────
    (async () => {
        const user = await requireAuth();
        if (!user) return;

        if (!sessionId) { showToast(t('report.err_no_session'), 'danger'); return; }

        scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));

        // 鍵盤掃描槍 → 填入容器號
        scanner = new BarcodeScanner({
            onScan: code => {
                applyContainer(code);
                scanModal.hide();
                scanner.stopCamera();
            }
        });

        updateOnlineStatus();
        window.addEventListener('online',  updateOnlineStatus);
        window.addEventListener('offline', updateOnlineStatus);

        await offlineQueue.ready();
        await loadDispatch();
    })();

    async function loadDispatch() {
        const data = await apiGet(`/api/mobile/dispatches?dispatchId=${dispatchId}`);
        if (!data) return;
        const list = data.data || [];
        dispatchData = list.find(d => d.dispatchId === dispatchId) || null;
        if (dispatchData) renderProgress();
    }

    function renderProgress() {
        const d = dispatchData;
        const pct = d.quantity > 0 ? Math.min(100, Math.round((d.qtyDone / d.quantity) * 100)) : 0;
        const remaining = Math.max(0, (d.quantity ?? 0) - (d.qtyDone ?? 0));
        document.getElementById('progressInfo').innerHTML = `
            <div class="d-flex justify-content-between mb-1">
                <span class="small fw-semibold">${d.workOrderId} — ${t('report.op_pre')} ${d.operationSeq ?? '—'}</span>
                <span class="small text-muted">${tf('report.pct_done', pct)}</span>
            </div>
            <div class="progress mb-1 u-height-8px">
                <div class="progress-bar bg-success" data-pct-width="${pct}"></div>
            </div>
            <div class="d-flex justify-content-between small text-muted">
                <span>${t('report.done_pre')}：${d.qtyDone ?? 0} pcs</span>
                <span>${t('report.remain_pre')}：${remaining} pcs</span>
                <span>${t('report.total_pre')}：${d.quantity ?? 0} pcs</span>
            </div>`;
    }

    // ── 不良原因下拉（代碼維護 ng_reason；無資料時整組隱藏）─────────────────────
    let ngReasonLoaded = false;
    async function loadNgReasons() {
        if (ngReasonLoaded) return;
        ngReasonLoaded = true;
        const result = await apiGet('/api/mobile/code-entries?category=ng_reason');
        const items = result?.data || [];
        const sel = document.getElementById('ngReasonCode');
        if (!items.length) return;   // 未維護代碼 → 維持隱藏，不擋報工
        const zh = (document.documentElement.lang || 'zh-TW').startsWith('zh');
        sel.innerHTML = `<option value="">${t('report.reason_none')}</option>` + items.map(i =>
            `<option value="${i.code}">${i.code} — ${zh ? (i.nameZh || i.nameEn || '') : (i.nameEn || i.nameZh || '')}</option>`).join('');
        sel.dataset.hasOptions = '1';
        syncNgReasonVisibility();
    }
    function syncNgReasonVisibility() {
        const qtyNg = parseFloat(document.getElementById('qtyNg').value) || 0;
        const sel = document.getElementById('ngReasonCode');
        document.getElementById('ngReasonGroup').classList.toggle(
            'd-none', !(qtyNg > 0 && sel.dataset.hasOptions === '1'));
    }
    document.getElementById('qtyNg').addEventListener('input', () => { loadNgReasons(); syncNgReasonVisibility(); });

    // ── 報工提交 ──────────────────────────────────────────────────────────────
    // 完工並報工：回報這一批後結束本次上工（走 sessions/{id}/complete，一個交易同時報工＋下工）。
    // 未填數量也可完工——本次產出可能已在前幾次報工回報完畢。
    async function submitComplete() {
        if (!confirm(t('report.confirm_complete'))) return;
        await submitReport(true);
    }

    async function submitReport(complete) {
        // 不可用 parseInt：輸入框是 step="any"，公斤等帶小數的單位 12.5 會被靜默截成 12
        const qtyOk = parseFloat(document.getElementById('qtyOk').value) || 0;
        const qtyNg = parseFloat(document.getElementById('qtyNg').value) || 0;
        const container = document.getElementById('containerId').value.trim() || null;

        // input 的 min="0" 只在表單送出時才擋，這頁不走表單；離線時更要先擋，否則會排進佇列才被伺服器退回
        if (qtyOk < 0 || qtyNg < 0) {
            showToast(t('report.err_qty_negative'), 'warning');
            return;
        }
        if (qtyOk <= 0 && qtyNg <= 0 && complete !== true) {
            showToast(t('report.err_qty'), 'warning');
            return;
        }

        const clientId = crypto.randomUUID();
        const ngSel = document.getElementById('ngReasonCode');
        const body = {
            sessionId,
            qtyOk,
            qtyNg,
            containerId: container,
            ngReasonCode: (qtyNg > 0 && ngSel.value) ? ngSel.value : null,
            clientId,
            clientTimestamp: new Date().toISOString()
        };

        const btn = document.getElementById(complete === true ? 'btnComplete' : 'btnSubmit');
        setLoading(btn, true);

        // 完工走 sessions/{id}/complete（報工＋下工同一交易），一般報工維持 /reports
        const url    = complete === true ? `/api/mobile/sessions/${sessionId}/complete` : '/api/mobile/reports';
        const method = complete === true ? 'PATCH' : 'POST';

        // 連不上（斷網或伺服器暫時連不到）：存入 IndexedDB 佇列，連線後自動補送（斷線保護）
        const { queued, result } = await submitOrQueue({
            url,
            method,
            body,
            priority: 5,
            label:    (complete === true ? t('session.complete') : t('report.title'))
                      + ` OK:${qtyOk} NG:${qtyNg}`
        });
        if (queued) {
            setLoading(btn, false);
            addHistory({ qtyOk, qtyNg, container, clientId, status: 'pending', time: new Date() });
            resetForm();
            updateSyncBadge();
            showToast(t('report.queued'), 'warning');
            if (complete === true) {
                // 離線「完工並報工」：本機也要當作已下工。原本只有線上那條路清 sessionStorage，
                // 回到上工頁仍顯示「完工」鈕，同步後一按就是「此上工記錄已下工」（2026-10-05 實測）。
                sessionStorage.removeItem(`session_${dispatchId}`);
                sessionStorage.removeItem(`sessionStart_${dispatchId}`);
                setTimeout(() => location.href = '/mobile/mfg/list.html', 1500);
            }
            return;
        }

        setLoading(btn, false);
        if (!result) return;

        addHistory({ qtyOk, qtyNg, container, clientId, status: 'sent', time: new Date() });
        resetForm();

        // 重整派工進度
        if (dispatchData) {
            dispatchData.qtyDone = (dispatchData.qtyDone ?? 0) + qtyOk;
            renderProgress();
        }

        if (complete === true) {
            // 本次上工已結束，session 失效，退回派工清單
            sessionStorage.removeItem(`session_${dispatchId}`);
            sessionStorage.removeItem(`sessionStart_${dispatchId}`);
            showToast(t('report.complete_ok'), 'success');
            setTimeout(() => location.href = '/mobile/mfg/list.html', 1500);
            return;
        }

        const isComplete = (dispatchData && dispatchData.qtyDone >= dispatchData.quantity);
        if (isComplete) {
            showToast(t('report.ok_closed'), 'success');
            setTimeout(() => location.href = '/mobile/mfg/list.html', 2000);
        } else {
            showToast(t('report.ok'), 'success');
        }
    }

    function resetForm() {
        document.getElementById('qtyOk').value = '0';
        document.getElementById('qtyNg').value = '0';
        clearContainer();
    }

    // ── 容器掃描 ──────────────────────────────────────────────────────────────
    function toggleContainerInput() {
        containerManualVisible = !containerManualVisible;
        const area = document.getElementById('containerManualArea');
        if (containerManualVisible) {
            area.classList.remove('d-none');
            document.getElementById('containerScanArea').classList.add('d-none');
        } else {
            area.classList.add('d-none');
            document.getElementById('containerScanArea').classList.remove('d-none');
        }
    }

    function openContainerScanner() {
        scanModal.show();
        scanner.startCamera('reader');
    }

    function applyContainer(code) {
        document.getElementById('containerId').value = code;
        document.getElementById('containerDisplay').textContent = tf('report.container_pre', code);
        if (!containerManualVisible) {
            document.getElementById('containerScanArea').innerHTML = `
                <i class="bi bi-check-circle-fill fs-4 text-success"></i>
                <div><div class="fw-semibold small text-success">${code}</div>
                <div class="text-muted u-font-size-p75rem">${t('report.container_change')}</div></div>`;
        }
    }

    function clearContainer() {
        document.getElementById('containerId').value = '';
        document.getElementById('containerDisplay').textContent = t('report.container_scan');
        document.getElementById('containerScanArea').classList.remove('d-none');
        document.getElementById('containerManualArea').classList.add('d-none');
        containerManualVisible = false;
        document.getElementById('containerScanArea').innerHTML = `
            <i class="bi bi-upc-scan fs-4 text-muted"></i>
            <div><div class="fw-semibold small" id="containerDisplay">${t('report.container_scan')}</div>
            <div class="text-muted u-font-size-p75rem">${t('report.container_manual')}</div></div>`;
        document.getElementById('containerScanArea').onclick = toggleContainerInput;
    }

    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => {
        scanner.stopCamera();
    });

    // ── 報工歷史 ──────────────────────────────────────────────────────────────
    function addHistory(item) {
        reportHistory.unshift(item);
        renderHistory();
    }

    function renderHistory() {
        const list = document.getElementById('historyList');
        document.getElementById('historyCount').textContent = reportHistory.length;
        if (reportHistory.length === 0) {
            list.innerHTML = `<div class="text-muted small text-center py-2">${t('report.history_empty')}</div>`;
            return;
        }
        list.innerHTML = reportHistory.map(h => {
            // 不可命名為 t：會遮蔽 i18n 的 t()，下面的待同步徽章一呼叫就 TypeError。
            const timeText = h.time instanceof Date ? h.time.toLocaleTimeString('zh-TW', { hour:'2-digit', minute:'2-digit', second:'2-digit' }) : '—';
            const isPending = h.status === 'pending';
            return `<div class="history-item ps-2 mb-2 small ${isPending ? 'opacity-75' : ''}">
                <span class="fw-semibold text-success">OK: ${h.qtyOk}</span>
                <span class="ms-2 text-danger">NG: ${h.qtyNg}</span>
                ${h.container ? `<span class="ms-2 text-muted">${h.container}</span>` : ''}
                <span class="ms-2 text-muted">${timeText}</span>
                ${isPending ? `<span class="badge bg-warning text-dark ms-1 u-font-size-p65rem">${t('report.pending_badge')}</span>` : ''}
            </div>`;
        }).join('');
    }

    // ── 離線狀態 UI ───────────────────────────────────────────────────────────
    function updateOnlineStatus() {
        const offline = !navigator.onLine;
        document.getElementById('offlineAlert').classList.toggle('d-none', !offline);
        updateSyncBadge();
    }

    async function updateSyncBadge() {
        try {
            const pending = await offlineQueue.getPending();
            const badge = document.getElementById('syncBadge');
            if (!badge) return;
            if (pending.length > 0) {
                badge.querySelector('#syncCount').textContent = pending.length;
                badge.classList.remove('d-none');
            } else {
                badge.classList.add('d-none');
            }
        } catch {}
    }
