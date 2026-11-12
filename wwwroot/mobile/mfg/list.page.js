// 自 list.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    // STATUS_LABELS 在 applyI18n 後才能用 t()，改為函數
    function getStatusLabel(status) {
        const MAP = {
            Scheduled:       { key: 'dispatch.status.scheduled',   cls: 'bg-secondary' },
            SetupInProgress: { key: 'dispatch.status.setup',       cls: 'bg-warning text-dark' },
            ReadyToStart:    { key: 'dispatch.status.ready',       cls: 'bg-info text-dark' },
            InProgress:      { key: 'dispatch.status.inprogress',  cls: 'bg-success' },
            OnHold:          { key: 'dispatch.status.onhold',      cls: 'bg-danger' },
            Closed:          { key: 'dispatch.status.closed',      cls: 'bg-dark' },
            Cancelled:       { key: 'dispatch.status.cancelled',   cls: 'bg-light text-muted border' },
        };
        const entry = MAP[status] || { key: null, cls: 'bg-light text-dark' };
        return { text: entry.key ? t(entry.key) : status, cls: entry.cls };
    }

    let refreshTimer = null;
    let scanner = null;
    let scanModal = null;

    // ── 初始化 ────────────────────────────────────────────────────────────────
    (async () => {
        // 佇列是純本地資料，讀它不需要認證——排在 requireAuth() 前面，
        // 否則認證失敗時「還有幾筆沒送出」就看不到（同 home.page.js 的 v17 修正）。
        updateSyncBadge();

        const user = await requireAuth();
        if (!user) return;

        scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));

        // 鍵盤掃描槍 → 填入機台欄位
        scanner = new BarcodeScanner({
            onScan: code => {
                document.getElementById('machineInput').value = code;
                loadDispatches();
                if (scanModal) scanModal.hide();
            }
        });

        // 從 URL 參數還原機台
        const params = new URLSearchParams(location.search);
        if (params.get('machine')) {
            document.getElementById('machineInput').value = params.get('machine');
        }

        await applyOpPermissions();
        await loadDispatches();
    })();

    // ── 作業功能捷徑：依角色手機權限顯示（mobile-pwa.md §6，後端另有二次驗證）────
    async function applyOpPermissions() {
        const wrap = document.getElementById('opShortcuts');
        if (!wrap) return;
        const data = await apiGet('/api/mobile/modules/Mfg/actions');
        // 取不到權限（離線/錯誤）時保守隱藏整區，避免顯示無權限的入口
        const allowed = new Set(data?.allowedActions || []);
        let visible = 0;
        wrap.querySelectorAll('[data-op]').forEach(col => {
            if (allowed.has(col.dataset.op)) visible++;
            else col.classList.add('d-none');
        });
        if (visible === 0) wrap.closest('.container-fluid').classList.add('d-none');
    }

    // ── 派工列表載入 ──────────────────────────────────────────────────────────
    async function loadDispatches() {
        document.getElementById('loadingState').classList.remove('d-none');
        document.getElementById('emptyState').classList.add('d-none');
        document.getElementById('dispatchList').innerHTML = '';

        const machineId = document.getElementById('machineInput').value.trim();
        const status    = document.getElementById('statusFilter').value;

        let url = '/api/mobile/dispatches?';
        if (machineId) url += `machineId=${encodeURIComponent(machineId)}&`;
        if (status)    url += `status=${encodeURIComponent(status)}&`;

        const data = await apiGet(url);
        document.getElementById('loadingState').classList.add('d-none');
        if (!data) return;

        const list = data.data || [];
        if (list.length === 0) {
            document.getElementById('emptyState').classList.remove('d-none');
            return;
        }

        const container = document.getElementById('dispatchList');
        list.forEach(d => container.appendChild(buildCard(d)));
    }

    function buildCard(d) {
        const st  = getStatusLabel(d.status);
        const pct = d.quantity > 0 ? Math.min(100, Math.round((d.qtyDone / d.quantity) * 100)) : 0;
        const scheduledStart = d.scheduledStart
            ? new Date(d.scheduledStart).toLocaleString(getLang(), { month:'2-digit', day:'2-digit', hour:'2-digit', minute:'2-digit' })
            : '—';

        const col = document.createElement('div');
        col.className = `card mb-3 dispatch-card status-${d.status}`;
        col.innerHTML = `
            <div class="card-body py-2 px-3">
                <div class="d-flex justify-content-between align-items-start mb-1">
                    <div>
                        <span class="fw-bold small">${d.workOrderId}</span>
                        <span class="text-muted small ms-2">${t('ipqc.op_seq')} ${d.operationSeq ?? '—'}</span>
                    </div>
                    <span class="badge badge-status ${st.cls}">${st.text}</span>
                </div>
                <div class="small text-muted mb-1">
                    <i class="bi bi-cpu me-1"></i>${d.machineCode || '—'}
                    <span class="ms-3"><i class="bi bi-calendar3 me-1"></i>${scheduledStart}</span>
                </div>
                <div class="d-flex align-items-center gap-2">
                    <div class="progress flex-grow-1 u-height-6px">
                        <div class="progress-bar bg-success" data-pct-width="${pct}"></div>
                    </div>
                    <small class="text-muted">${d.qtyDone ?? 0}/${d.quantity ?? 0}</small>
                </div>
            </div>`;
        col.addEventListener('click', () => goToSession(d));
        return col;
    }

    function goToSession(d) {
        const params = new URLSearchParams({ dispatchId: d.dispatchId, status: d.status });
        location.href = `/mobile/mfg/sessions.html?${params}`;
    }

    // ── 機台掃描 ──────────────────────────────────────────────────────────────
    function openMachineScanner() {
        scanModal.show();
        scanner.startCamera('reader');
    }

    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => {
        scanner.stopCamera();
    });

    function clearMachine() {
        document.getElementById('machineInput').value = '';
        loadDispatches();
    }

    function scheduleRefresh() {
        clearTimeout(refreshTimer);
        refreshTimer = setTimeout(loadDispatches, 600);
    }

    // ── 離線佇列同步 ──────────────────────────────────────────────────────────
    // 待同步數讀全站共用的 IndexedDB 佇列（js/offline-queue.js）。
    // ⚠️ 這裡原本讀 sessionStorage.offlineQueue，但**全站沒有任何程式寫它**——
    //    報工、機台狀態、上下架都排進 IndexedDB——所以這頁的徽章從來不會出現，
    //    'online' 時的 flush 也一筆都沒送過。送出交給佇列自己的 online 處理器，
    //    這裡只在它送完之後更新畫面。
    async function updateSyncBadge() {
        const badge = document.getElementById('syncBadge');
        if (!badge || !window.offlineQueue) return;
        const pending = (await offlineQueue.getPending().catch(() => [])).length;
        document.getElementById('syncCount').textContent = pending;
        badge.classList.toggle('d-none', pending === 0);
    }

    window.addEventListener('offlinequeue:flushed', async () => {
        await updateSyncBadge();
        loadDispatches();
    });
