// 自 machine-status.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    // ── 狀態 Meta（label 在渲染時用 t() 取得）────────────────────────────────
    const STATUS_META = {
        Available:      { key: 'machine.status.available',   cls: 'status-Available'     },
        Standby:        { key: 'machine.status.standby',     cls: 'status-Standby'       },
        BreakDown:      { key: 'machine.status.breakdown',   cls: 'status-BreakDown'     },
        Maintenance:    { key: 'machine.status.maintenance', cls: 'status-Maintenance'   },
        // 這三個原本都指向 machine.status.available，於是外修中／改良中／已廢棄的機台
        // 在手機上顯示「正常（可用）」——不是漏翻譯，是給現場錯誤的資訊。
        ExternalRepair: { key: 'machine.status.externalrepair', cls: 'status-ExternalRepair'},
        Modification:   { key: 'machine.status.modification',   cls: 'status-Modification'  },
        Loaned:         { key: 'machine.status.loaned',      cls: 'status-Loaned'        },
        Retired:        { key: 'machine.status.retired',        cls: 'status-Retired'       },
    };
    function getStatusLabel(status) {
        const m = STATUS_META[status];
        return m ? t(m.key) : status;
    }

    // ── 各狀態可執行的事件（event code → i18n key）────────────────────────
    const EVENT_DEFS = {
        DI: { icon: 'bi-tools',            style: 'danger-event'  },
        HI: { icon: 'bi-wrench-adjustable',style: 'warning-event' },
        II: { icon: 'bi-pause-circle',     style: 'info-event'    },
        EO: { icon: 'bi-gear',             style: 'info-event'    },
        FO: { icon: 'bi-box-arrow-right',  style: 'info-event'    },
        CO: { icon: 'bi-truck',            style: 'warning-event' },
        GO: { icon: 'bi-trash3',           style: 'danger-event'  },
        DO: { icon: 'bi-check-circle',     style: 'success-event' },
        CI: { icon: 'bi-check-circle',     style: 'success-event' },
        HO: { icon: 'bi-check-circle',     style: 'success-event' },
        FI: { icon: 'bi-box-arrow-in-left',style: 'success-event' },
    };
    // 各狀態可執行的事件代碼清單（label 由 t('machine.event.XX') 提供）
    const EVENTS_BY_STATUS_KEYS = {
        Available:      ['DI','HI','II','EO','FO','CO','GO'],
        Standby:        ['CI','DI','HI'],
        BreakDown:      ['DO','HI','CO','GO'],
        Maintenance:    ['HO','DI','GO'],
        ExternalRepair: ['CI','GO'],
        Modification:   ['CI','GO'],
        Loaned:         ['FI'],
        Retired:        [],
    };

    let currentMachineCode = null;
    let currentStatus      = null;
    let selectedEvent      = null;
    let scanner = null;
    let scanModal = null;
    let affectModal = null;

    // ── 初始化 ────────────────────────────────────────────────────────────────
    (async () => {
        const user = await requireAuth();
        if (!user) return;

        scanModal   = new bootstrap.Modal(document.getElementById('scannerModal'));
        affectModal = new bootstrap.Modal(document.getElementById('affectModal'));

        // 鍵盤掃描槍
        scanner = new BarcodeScanner({
            onScan: code => {
                document.getElementById('machineCodeInput').value = code;
                scanModal.hide();
                scanner.stopCamera();
                loadMachine();
            }
        });

        // URL 參數預填
        const params = new URLSearchParams(location.search);
        if (params.get('machine')) {
            document.getElementById('machineCodeInput').value = params.get('machine');
            await loadMachine();
        }
    })();

    // ── 掃描 ──────────────────────────────────────────────────────────────────
    function openScanner() {
        scanModal.show();
        scanner.startCamera('scanReader');
    }

    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => {
        scanner.stopCamera();
    });

    // ── 載入機台 ──────────────────────────────────────────────────────────────
    async function loadMachine() {
        const code = document.getElementById('machineCodeInput').value.trim().toUpperCase();
        if (!code) { showToast(t('machine.err_code'), 'warning'); return; }

        const data = await apiGet(`/api/mobile/machines/${encodeURIComponent(code)}`);
        if (!data) return;

        const m = data.data;
        currentMachineCode = m.machineCode;
        currentStatus      = m.status;

        document.getElementById('machineName').textContent  = m.machineName || m.machineCode;
        document.getElementById('machineCode').textContent  = m.machineCode;
        document.getElementById('workCenter').textContent   = m.workCenterCode || '—';

        const meta = STATUS_META[m.status] || { key: null, cls: '' };
        const badge = document.getElementById('statusBadge');
        badge.textContent  = meta.key ? t(meta.key) : m.status;
        badge.className    = `machine-status-badge ${meta.cls}`;

        renderEventButtons(m.status);

        document.getElementById('scanSection').style.display   = 'none';
        document.getElementById('detailSection').style.display = 'block';
    }

    // ── 事件按鈕（label 由 t('machine.event.XX') 取得）────────────────────
    function renderEventButtons(status) {
        const codes     = EVENTS_BY_STATUS_KEYS[status] || [];
        const container = document.getElementById('eventButtons');
        container.innerHTML = '';
        document.getElementById('noEvents').classList.toggle('d-none', codes.length > 0);

        codes.forEach(code => {
            const def   = EVENT_DEFS[code] || { icon: 'bi-circle', style: '' };
            const label = t('machine.event.' + code) || code;
            const btn   = document.createElement('button');
            btn.className = `btn btn-outline-secondary event-btn ${def.style}`;
            btn.innerHTML = `<i class="bi ${def.icon} me-2"></i>${label}<span class="event-code">${code}</span>`;
            btn.addEventListener('click', () => selectEvent({ code, label, icon: def.icon }));
            container.appendChild(btn);
        });
    }

    function selectEvent(evt) {
        selectedEvent = evt;
        const confirmLabel = `${t('common.confirm')}: ${evt.label} (${evt.code})`;
        document.getElementById('selectedEventLabel').textContent = confirmLabel;
        document.getElementById('eventNote').value    = '';
        document.getElementById('sourceDocId').value  = '';
        document.getElementById('confirmForm').classList.remove('u-display-none');
        document.getElementById('confirmForm').scrollIntoView({ behavior: 'smooth' });
    }

    function cancelEvent() {
        selectedEvent = null;
        document.getElementById('confirmForm').classList.add('u-display-none');
    }

    // ── 提交 ─────────────────────────────────────────────────────────────────
    async function submitEvent() {
        if (!selectedEvent || !currentMachineCode) return;

        const btn = document.getElementById('btnConfirm');
        setLoading(btn, true);

        const body = {
            eventType:   selectedEvent.code,
            note:        document.getElementById('eventNote').value.trim() || null,
            sourceDocId: document.getElementById('sourceDocId').value.trim() || null,
            clientId:    crypto.randomUUID(),
            clientTimestamp: new Date().toISOString()
        };

        const apiUrl = `/api/mobile/machines/${encodeURIComponent(currentMachineCode)}/status-event`;

        const { queued, result } = await submitOrQueue({
            url:      apiUrl,
            body,
            priority: 1,  // 機台狀態最優先（mobile-pwa.md §7.4 斷線行為）
            label:    tf('machine.queue_label', currentMachineCode, selectedEvent.code)
        });
        setLoading(btn, false);
        if (queued) {
            showToast(t('machine.offline'), 'warning');
            cancelEvent();
            return;
        }
        if (!result) return;

        const d = result.data || {};
        showToast(result.message || t('machine.updated'), 'success');

        // 若有影響派工，顯示 modal 提示
        const affected = (d.affectedScheduled || 0) + (d.affectedInProgress || 0);
        if (affected > 0) {
            // 標題原本是固定字「張派工受影響」——量詞前面沒有數字（2026-10-05 手機實測）。
            document.getElementById('affectTitle').textContent = tf('machine.affected_n', affected);
            // targetStatus 是列舉碼（Breakdown…），顯示前轉成狀態名稱；字典沒有就照原碼。
            const statusKey  = 'machine.status.' + String(d.targetStatus || '').toLowerCase();
            const statusText = t(statusKey) !== statusKey ? t(statusKey) : (d.targetStatus || '');
            document.getElementById('affectBody').innerHTML = `
                <p class="mb-1">${tf('machine.affect_intro', statusText)}</p>
                <ul class="mb-0">
                    ${d.affectedScheduled  ? `<li>${tf('machine.affect_sched', d.affectedScheduled)}</li>` : ''}
                    ${d.affectedInProgress ? `<li>${tf('machine.affect_run', d.affectedInProgress)}</li>` : ''}
                </ul>`;
            affectModal.show();
        } else {
            resetScan();
        }
    }

    // ── 重置 ─────────────────────────────────────────────────────────────────
    function resetScan() {
        currentMachineCode = null;
        currentStatus      = null;
        selectedEvent      = null;
        document.getElementById('machineCodeInput').value  = '';
        document.getElementById('scanSection').style.display   = 'block';
        document.getElementById('detailSection').style.display = 'none';
        document.getElementById('confirmForm').classList.add('u-display-none');
    }
