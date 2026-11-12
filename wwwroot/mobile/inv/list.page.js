// 自 list.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
// 狀態篩選
document.querySelectorAll('input[name="invFilter"]').forEach(r => {
    r.addEventListener('change', () => {
        const val = r.value;
        document.querySelectorAll('[data-filter]').forEach(col => {
            col.style.display = (val === 'all' || col.dataset.filter === val) ? '' : 'none';
        });
    });
});

(async () => {
    const user = await requireAuth();
    if (!user) return;

    await offlineQueue.ready();

    const [inData, outData, matCounts] = await Promise.all([
        apiGet('/api/warehouse/pending-inbound'),
        apiGet('/api/warehouse/pending-outbound'),
        apiGet('/api/mobile/material/counts')
    ]);
    showMaterialCounts(matCounts?.data);

    const inCount  = Array.isArray(inData)  ? inData.length  : '?';
    const outCount = Array.isArray(outData) ? outData.length : '?';

    document.getElementById('pendingInCount').textContent  = inCount;
    document.getElementById('pendingOutCount').textContent = outCount;
    document.getElementById('putawayBadge').textContent    = inCount;
    document.getElementById('pickBadge').textContent       = outCount;

    const pending = await offlineQueue.getPending();
    const invPending = pending.filter(p => p.url.startsWith('/api/warehouse/'));
    if (invPending.length > 0) {
        document.getElementById('syncCount').textContent = invPending.length;
        document.getElementById('syncBadge').classList.remove('d-none');
    }
})();

// 料件三張卡的待辦數（/api/mobile/material/counts；與各頁清單同一份查詢）。
// 沒有該作業權限的項目伺服器回 null：不顯示數字，而不是顯示 0（0 會讓人以為「沒事做」）。
function showMaterialCounts(c) {
    if (!c) return;
    const put = (id, text) => {
        const el = document.getElementById(id);
        if (!el || text == null) return;
        el.textContent = text;
        el.classList.remove('d-none');
    };
    if (c.slipIssue != null) put('slipBadge', tf('mslip.badge_fmt', c.slipIssue, c.slipReturn ?? 0));
    put('stagingBadge', c.staging);
    put('osrBadge', c.outsource);
}
