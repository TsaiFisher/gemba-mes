// 自 sessions.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    // 值是字典鍵而不是字面文字：模組載入時 applyI18n 還沒跑，
    // 此處若先取 t() 會在切語言後留下前一個語言的字（list.page.js 踩過同一個坑）。
    const STATUS_LABELS = {
        Scheduled:        'dispatch.status.scheduled',
        SetupInProgress:  'dispatch.status.setup',
        ReadyToStart:     'dispatch.status.ready',
        InProgress:       'dispatch.status.inprogress',
    };
    const STATUS_BADGE = {
        Scheduled:        'bg-secondary',
        SetupInProgress:  'bg-warning text-dark',
        ReadyToStart:     'bg-info text-dark',
        InProgress:       'bg-success',
    };

    const params      = new URLSearchParams(location.search);
    const dispatchId  = params.get('dispatchId');
    let   dispatchData = null;
    let   sessionId    = sessionStorage.getItem(`session_${dispatchId}`) || null;
    let   timerInterval = null;
    let   startedAt    = sessionStorage.getItem(`sessionStart_${dispatchId}`)
                         ? new Date(sessionStorage.getItem(`sessionStart_${dispatchId}`))
                         : null;
    let   scanner = null;
    let   scanModal = null;

    // ── 初始化 ────────────────────────────────────────────────────────────────
    (async () => {
        const user = await requireAuth();
        if (!user) return;

        if (!dispatchId) {
            showToast(t('session.err_no_dispatch'), 'danger');
            return;
        }

        scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
        scanner = new BarcodeScanner({
            onScan: code => {
                document.getElementById('sessionMachine').value = code;
                scanModal.hide();
                scanner.stopCamera();
            }
        });

        await loadDispatch();
        await loadStaging();
        restoreSessionUI();
        await showRequisitionEntry();
    })();

    async function loadDispatch() {
        const url  = `/api/mobile/dispatches?dispatchId=${dispatchId}`;
        const data = await apiGet(url);
        if (!data) return;
        const list = data.data || [];
        dispatchData = list.find(d => d.dispatchId === dispatchId) || null;
        if (!dispatchData) { showToast(t('session.err_not_found'), 'danger'); return; }
        adoptServerSession(dispatchData.myOpenSession);
        await dropSessionIfCompletionQueued();
        renderDispatchInfo();
        renderStepUI();
    }

    // ── 上工紀錄對帳 ─────────────────────────────────────────────────────────
    // sessionId 原本只存在 sessionStorage：離線「完工並報工」排進佇列、同步後伺服器已下工，
    // 這裡卻還留著，畫面照樣出「完工」鈕，一按就是「此上工記錄已下工」；登出再登入清掉 sessionStorage
    // 才恢復（2026-10-05 實測）。現在以伺服器回的 myOpenSession 為準（讀取一律連線，見 api.js apiGet）。
    function adoptServerSession(srv) {
        if (srv && srv.sessionId) {
            sessionId = srv.sessionId;
            startedAt = srv.startAt ? new Date(srv.startAt) : startedAt;
            sessionStorage.setItem(`session_${dispatchId}`, sessionId);
            if (startedAt) sessionStorage.setItem(`sessionStart_${dispatchId}`, startedAt.toISOString());
        } else {
            forgetSession();
        }
    }

    /** 這次上工的「完工」已經排在離線佇列裡（還沒送出）：本機視同已下工，不再顯示完工鈕。 */
    async function dropSessionIfCompletionQueued() {
        if (!sessionId || !window.offlineQueue) return;
        const pending = await offlineQueue.getPending().catch(() => []);
        if (pending.some(r => r.url === `/api/mobile/sessions/${sessionId}/complete`)) forgetSession();
    }

    function forgetSession() {
        sessionId = null;
        startedAt = null;
        sessionStorage.removeItem(`session_${dispatchId}`);
        sessionStorage.removeItem(`sessionStart_${dispatchId}`);
    }

    function renderDispatchInfo() {
        const d = dispatchData;
        const statusKey = STATUS_LABELS[d.status];
        document.getElementById('dispatchStatus').textContent = statusKey ? t(statusKey) : d.status;
        document.getElementById('dispatchStatus').className = `badge ${STATUS_BADGE[d.status] || 'bg-secondary'}`;
        const pct = d.quantity > 0 ? Math.min(100, Math.round((d.qtyDone / d.quantity) * 100)) : 0;
        document.getElementById('dispatchInfo').innerHTML = `
            <div class="info-row"><span class="text-muted small">${t('session.work_order')}</span><span class="fw-semibold">${d.workOrderId}</span></div>
            <div class="info-row"><span class="text-muted small">${t('session.operation')}</span><span>${d.operationSeq ?? '—'}</span></div>
            <div class="info-row"><span class="text-muted small">${t('session.machine')}</span><span>${d.machineCode || '—'}</span></div>
            <div class="info-row">
                <span class="text-muted small">${t('session.progress')}</span>
                <span>${d.qtyDone ?? 0} / ${d.quantity ?? 0}（${pct}%）</span>
            </div>`;
    }

    function renderStepUI() {
        const st = dispatchData?.status;
        const setupDone = (st === 'ReadyToStart' || st === 'InProgress');
        const inProgress = (st === 'InProgress');

        // Step 1 — Setup
        if (setupDone) {
            document.getElementById('panelSetup').classList.add('phase-done');
            document.getElementById('btnSetupStart').classList.add('d-none');
            document.getElementById('btnSetupDone').classList.add('d-none');
        } else if (st === 'SetupInProgress') {
            document.getElementById('btnSetupStart').classList.add('d-none');
            document.getElementById('btnSetupDone').classList.remove('d-none');
        }

        // Step 2 — Session
        if (inProgress && sessionId) {
            document.getElementById('btnStart').classList.add('d-none');
            document.getElementById('btnStop').classList.remove('d-none');
            document.getElementById('btnGoReport').classList.remove('d-none');
            startTimer();
        } else if (inProgress) {
            // 已在生產中但本頁沒 sessionId（可能由別頁開工）
            document.getElementById('btnStart').textContent = t('session.continue_report');
            document.getElementById('btnGoReport').classList.remove('d-none');
        }
    }

    function restoreSessionUI() {
        if (sessionId && startedAt) startTimer();
    }

    // ── Step 1：備料 ──────────────────────────────────────────────────────────

    // 備料單狀態：已開的單 + 尚未備妥的用料。沒有待備料件時不顯示建單按鈕，
    // 避免現場對著空單一直按（後端也會擋，但不該讓人白按一次）。
    async function loadStaging() {
        const el = document.getElementById('stagingStatus');
        const btn = document.getElementById('btnCreateStaging');
        if (!el) return;

        const res = await apiGet(`/api/mobile/dispatches/${dispatchId}/staging`);
        if (!res || !res.data) { el.textContent = t('session.staging_fail'); return; }

        const orders  = res.data.orders  || [];
        const pending = res.data.pending || [];

        let html = '';
        if (orders.length) {
            html += orders.map(o =>
                `<div>${t('session.staging_order')} <span class="fw-semibold">${o.stagingId}</span>
                 <span class="badge bg-secondary">${o.status}</span>
                 ${o.stagingWhCode ? '→ ' + o.stagingWhCode : ''}</div>`).join('');
        }
        if (pending.length) {
            html += `<div class="text-warning mt-1">${tf('session.staging_pending', pending.length)}</div>`;
            html += pending.map(p =>
                `<div class="small">${p.productCode} × ${p.requiredQty} ${p.uomCode || ''}</div>`).join('');
        } else if (!orders.length) {
            html += `<div class="text-success">${t('session.staging_ok')}</div>`;
        }
        el.innerHTML = html;
        if (btn) btn.classList.toggle('d-none', pending.length === 0);
    }

    async function doCreateStaging() {
        const btn = document.getElementById('btnCreateStaging');
        setLoading(btn, true);
        const body = { clientId: crypto.randomUUID(), clientTimestamp: new Date().toISOString() };
        const result = await apiCall('POST', `/api/mobile/dispatches/${dispatchId}/staging-order`, body);
        setLoading(btn, false);
        if (!result) return;
        showToast(tf('session.staging_created', result.data?.stagingId || ''), 'success');
        await loadStaging();
    }

    async function doSetupStart() {
        const btn = document.getElementById('btnSetupStart');
        setLoading(btn, true);
        // 模具不再由現場輸入，後端一律沿用排程指定的 Dispatch.ToolingCode
        const body = {
            clientId: crypto.randomUUID(),
            clientTimestamp: new Date().toISOString()
        };
        const result = await apiCall('POST', `/api/mobile/dispatches/${dispatchId}/setup/start`, body);
        setLoading(btn, false);
        if (!result) return;
        showToast(t('session.setup_started'), 'success');
        btn.classList.add('d-none');
        document.getElementById('btnSetupDone').classList.remove('d-none');
        dispatchData.status = 'SetupInProgress';
        renderDispatchInfo();
    }

    async function doSetupDone() {
        const btn = document.getElementById('btnSetupDone');
        setLoading(btn, true);
        const body = { clientId: crypto.randomUUID(), clientTimestamp: new Date().toISOString() };
        const result = await apiCall('PATCH', `/api/mobile/dispatches/${dispatchId}/setup/done`, body);
        setLoading(btn, false);
        if (!result) return;
        showToast(t('session.setup_finished'), 'success');
        document.getElementById('panelSetup').classList.add('phase-done');
        document.getElementById('btnSetupDone').classList.add('d-none');
        dispatchData.status = 'ReadyToStart';
        renderDispatchInfo();
    }

    // ── Step 2：開工 / 下工 ────────────────────────────────────────────────────
    async function doStart() {
        const btn = document.getElementById('btnStart');
        setLoading(btn, true);
        const machine = document.getElementById('sessionMachine').value.trim() || null;
        const body = {
            dispatchId,
            machineId: machine,
            clientId: crypto.randomUUID(),
            clientTimestamp: new Date().toISOString()
        };
        const result = await apiCall('POST', '/api/mobile/sessions', body);
        setLoading(btn, false);
        if (!result) return;

        sessionId = result.data?.sessionId;
        startedAt = new Date();
        sessionStorage.setItem(`session_${dispatchId}`, sessionId);
        sessionStorage.setItem(`sessionStart_${dispatchId}`, startedAt.toISOString());

        btn.classList.add('d-none');
        document.getElementById('btnStop').classList.remove('d-none');
        document.getElementById('btnGoReport').classList.remove('d-none');
        dispatchData.status = 'InProgress';
        renderDispatchInfo();
        startTimer();
        // 機台狀態閘口為 Warning 時照常上工，但要讓現場知道機台狀態可能沒更新
        const warn = result.data?.machineWarning;
        if (warn) showToast(tf('session.machine_warn', t('machine.status.' + warn.toLowerCase())), 'warning');
        else showToast(t('session.started_ok'), 'success');
    }

    // 完工＝結束本次上工並結算工時。這裡不帶數量（產出於報工頁回報），
    // 若還要回報最後一批數量，可在報工頁按「完工並報工」。
    async function doStop() {
        if (!sessionId) { showToast(t('session.err_no_session'), 'danger'); return; }
        if (!confirm(t('session.confirm_complete'))) return;
        const btn = document.getElementById('btnStop');
        setLoading(btn, true);
        const body = { clientId: crypto.randomUUID(), clientTimestamp: new Date().toISOString() };
        const url  = `/api/mobile/sessions/${sessionId}/complete`;

        // 斷線下工：與報工頁「完工並報工」同一個端點、同一個佇列，連線後自動送出。
        // 伺服器依 clientTimestamp 回推，下工時間記的是按下的這一刻（見 MobileOccurredAt.cs）。
        const { queued, result } = await submitOrQueue({ url, method: 'PATCH', body, priority: 5, label: t('session.complete') });
        setLoading(btn, false);
        if (queued) {
            afterSessionEnded(btn);
            showToast(t('report.queued'), 'warning');
            return;
        }
        if (!result) return;

        afterSessionEnded(btn);
        showToast(t('session.completed_ok'), 'success');
    }

    function afterSessionEnded(btn) {
        stopTimer();
        forgetSession();
        btn.classList.add('d-none');
        document.getElementById('btnGoReport').classList.add('d-none');
        document.getElementById('btnStart').classList.remove('d-none');
        document.getElementById('btnStart').innerHTML =
            `<i class="bi bi-play-circle me-2"></i>${t('session.start_again')}`;
    }

    async function showRequisitionEntry() {
        const data = await apiGet('/api/mobile/modules/Mfg/actions');
        const allowed = new Set(data?.allowedActions || []);
        document.getElementById('btnRequisition').classList.toggle('d-none', !allowed.has('Requisition'));
    }

    function goRequisition() {
        location.href = `/mobile/mfg/requisition.html?dispatchId=${encodeURIComponent(dispatchId)}`;
    }

    function goReport() {
        if (!sessionId) { showToast(t('session.err_start_first'), 'warning'); return; }
        location.href = `/mobile/mfg/reports.html?dispatchId=${dispatchId}&sessionId=${sessionId}`;
    }

    // ── 計時器 ────────────────────────────────────────────────────────────────
    function startTimer() {
        document.getElementById('timerArea').classList.remove('d-none');
        clearInterval(timerInterval);
        timerInterval = setInterval(() => {
            if (!startedAt) return;
            const sec = Math.floor((Date.now() - startedAt.getTime()) / 1000);
            const h = String(Math.floor(sec / 3600)).padStart(2, '0');
            const m = String(Math.floor((sec % 3600) / 60)).padStart(2, '0');
            const s = String(sec % 60).padStart(2, '0');
            document.getElementById('timer').textContent = `${h}:${m}:${s}`;
        }, 1000);
    }

    function stopTimer() {
        clearInterval(timerInterval);
        document.getElementById('timerArea').classList.add('d-none');
    }

    // ── 機台掃描 ──────────────────────────────────────────────────────────────
    function openMachineScanner() {
        scanModal.show();
        scanner.startCamera('reader');
    }

    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => {
        scanner.stopCamera();
    });

    // 佇列送完（例如離線排的下工剛同步）後重新整理：按鈕狀態由 loadDispatch 依伺服器現況重畫。
    window.addEventListener('offlinequeue:flushed', e => {
        if ((e.detail?.sent || 0) > 0) location.reload();
    });
