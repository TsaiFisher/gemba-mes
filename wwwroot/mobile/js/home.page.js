// 首頁邏輯（自 home.html 內聯 <script> 外部化，因 CSP script-src 改用 nonce，
// 靜態 HTML 無法帶 per-request nonce，故內聯 script/handler 一律外部化）。

// 綁定登出按鈕（原 onclick="logout()"）
document.getElementById('logoutBtn')?.addEventListener('click', logout);

(async () => {
    const user = await requireAuth();
    if (!user) return;

    document.getElementById('userName').textContent = user.displayName || user.name;

    const data = await apiCall('GET', '/api/mobile/modules');
    if (!data) return;

    const grid = document.getElementById('moduleGrid');
    const modules = data.modules || [];

    if (modules.length === 0) {
        document.getElementById('emptyState').classList.remove('d-none');
        return;
    }

    const colors = ['text-primary', 'text-success', 'text-warning', 'text-info', 'text-danger', 'text-secondary'];

    modules.forEach((m, i) => {
        const color = colors[i % colors.length];
        const col = document.createElement('div');
        col.className = 'col-6 col-md-4 col-lg-3';
        col.innerHTML = `
            <a href="/mobile/${m.moduleKey.toLowerCase()}/list.html"
               class="card module-card shadow-sm p-3">
                <i class="bi ${m.iconClass} module-icon ${color}"></i>
                <span class="module-name">${m.displayName}</span>
            </a>`;
        grid.appendChild(col);
    });
})();

// 語言切換按鈕（右上角）：CSP 不允許 inline handler，於此綁定
document.querySelectorAll('[data-lang-toggle]').forEach(el => el.addEventListener('click', toggleLang));
