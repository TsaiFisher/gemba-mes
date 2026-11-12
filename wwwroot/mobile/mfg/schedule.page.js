// 自 schedule.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
(function () {
    // 初始日期：今天 + 7 天
    const today = new Date();
    const defTo = new Date(today);
    defTo.setDate(defTo.getDate() + 7);
    document.getElementById('inputDateTo').value = defTo.toISOString().split('T')[0];

    document.getElementById('inputDateTo').addEventListener('change', loadSchedule);
    document.getElementById('chkLateOnly').addEventListener('change', renderSchedule);

    let _data = [];

    function fmtTime(iso) {
        if (!iso) return '';
        const d = new Date(iso);
        return d.getMonth() + 1 + '/' + d.getDate() + ' '
             + String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    }

    // 狀態顯示沿用派工列表既有字典鍵（dispatch.status.*），避免同一狀態兩套譯名。
    const STATUS_KEY = {
        Pending: 'pending', Scheduled: 'scheduled',
        SetupInProgress: 'setup', ReadyToStart: 'ready',
        InProgress: 'inprogress', Closed: 'closed',
        OnHold: 'onhold', PendingFPI: 'pendingfpi', Cancelled: 'cancelled'
    };
    function statusLabel(st) {
        const k = STATUS_KEY[st];
        return k ? t('dispatch.status.' + k) : (st || '');
    }

    function renderSchedule() {
        const lateOnly = document.getElementById('chkLateOnly').checked;
        const content  = document.getElementById('scheduleContent');
        const empty    = document.getElementById('emptyState');
        content.innerHTML = '';

        const filtered = _data.map(wc => ({
            ...wc,
            data: wc.data.map(m => ({
                ...m,
                data: m.data.filter(d => !lateOnly || d.late)
            })).filter(m => m.data.length > 0)
        })).filter(wc => wc.data.length > 0);

        if (filtered.length === 0) { empty.classList.remove('d-none'); return; }
        empty.classList.add('d-none');

        filtered.forEach(wc => {
            const wcDiv = document.createElement('div');
            wcDiv.innerHTML = `<div class="wc-header"><i class="bi bi-cpu me-1"></i>${wc.group || t('sched.unknown_wc')}</div>`;

            wc.data.forEach(machine => {
                const machineDiv = document.createElement('div');
                machineDiv.className = 'machine-section';
                machineDiv.innerHTML = `<span class="machine-label"><i class="bi bi-gear me-1"></i>${machine.label}</span>`;

                machine.data.forEach(d => {
                    const progPct = d.qty > 0 ? Math.min(100, Math.round(d.done / d.qty * 100)) : 0;
                    const stClass = `st-${d.st || 'Scheduled'}`;
                    const flagClass = d.late ? 'flag-late' : (d.matRisk ? 'flag-matRisk' : '');

                    let flags = '';
                    if (d.late)    flags += `<span class="badge bg-danger badge-flag me-1">${t('sched.badge_late')}</span>`;
                    if (d.matRisk) flags += `<span class="badge bg-warning text-dark badge-flag me-1">${t('sched.badge_mat')}</span>`;

                    const card = document.createElement('div');
                    card.className = `dispatch-card ${stClass} ${flagClass}`;
                    card.innerHTML = `
                        <div class="d-flex justify-content-between align-items-start">
                            <div>
                                <div class="fw-semibold">${d.wo}</div>
                                <div class="time-range">${fmtTime(d.timeRange?.[0])} → ${fmtTime(d.timeRange?.[1])}</div>
                            </div>
                            <div class="text-end ms-2">
                                <div class="small">${statusLabel(d.st)}</div>
                                <div class="text-muted small">${d.wh} min</div>
                            </div>
                        </div>
                        ${flags ? `<div class="mt-1">${flags}</div>` : ''}
                        ${d.qty > 0 ? `
                        <div class="d-flex justify-content-between small text-muted mt-1">
                            <span>${t('sched.done')} ${d.done}/${d.qty}</span>
                            <span>${progPct}%</span>
                        </div>
                        <div class="prog-bar"><div class="prog-fill" data-pct-width="${progPct}"></div></div>` : ''}
                    `;
                    machineDiv.appendChild(card);
                });

                wcDiv.appendChild(machineDiv);
            });

            content.appendChild(wcDiv);
        });
    }

    async function loadSchedule() {
        document.getElementById('loadingState').classList.remove('d-none');
        document.getElementById('scheduleContent').innerHTML = '';
        document.getElementById('emptyState').classList.add('d-none');

        const dateTo = document.getElementById('inputDateTo').value;
        try {
            const json = await apiGet('/api/mobile/schedule?dateTo=' + encodeURIComponent(dateTo));
            _data = json?.data || [];
            document.getElementById('lastRefreshText').textContent =
                t('sched.last_refresh') + new Date().toLocaleTimeString(getLang());
            renderSchedule();
        } catch (e) {
            document.getElementById('scheduleContent').innerHTML =
                `<div class="alert alert-danger">${t('sched.load_fail')}</div>`;
        } finally {
            document.getElementById('loadingState').classList.add('d-none');
        }
    }

    // 🚨 navbar 的重新整理鈕是 data-action="loadSchedule"，而 data-action.js 找的是
    //    **window[name]**。這個檔整份包在 IIFE 裡，所以 loadSchedule 從來不是全域——
    //    那顆按鈕自 2026 年外部化以來一直是死的：按下去只在 console 留一行
    //    「[data-action] 找不到全域函式」，畫面上完全沒有反應，也沒有任何訊息。
    //    （由 MobileDataActionResolutionTests 在 2026-09-09 抓到。）
    //
    //    修法是明確掛上全域，而不是拆掉 IIFE——IIFE 擋住的是這一頁十幾個
    //    模組層變數（_data、篩選狀態…）外洩到 window 去撞別頁的同名變數。
    window.loadSchedule = loadSchedule;

    // 自動輪詢 45 秒
    loadSchedule();
    setInterval(loadSchedule, 45000);
}());
