// 登入頁邏輯（自 index.html 內聯 <script> 外部化，因 CSP script-src 改用 nonce，
// 靜態 HTML 無法帶 per-request nonce，故內聯 script/handler 一律外部化）。

// 已登入者直接跳首頁
(async () => {
    const data = await fetch('/api/mobile/auth/me', { credentials: 'include' })
        .then(r => r.ok ? r.json() : null).catch(() => null);
    if (data?.id) window.location.href = '/mobile/home.html';
})();

// 綁定登入按鈕（原 onclick="login()"）
document.getElementById('loginBtn')?.addEventListener('click', login);

// 語言切換按鈕（右上角）：CSP 不允許 inline handler，於此綁定
document.querySelectorAll('[data-lang-toggle]').forEach(el => el.addEventListener('click', toggleLang));
