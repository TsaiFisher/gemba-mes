// 自 index.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
        (async () => {
            const data = await fetch('/api/mobile/auth/me', { credentials: 'include' })
                .then(r => r.ok ? r.json() : null).catch(() => null);
            if (data?.id) window.location.href = '/mobile/home.html';
        })();

        if ('serviceWorker' in navigator) {
            navigator.serviceWorker.register('/mobile/sw.js', { scope: '/mobile/' })
                .catch(e => console.warn('[SW] 註冊失敗', e));
        }
