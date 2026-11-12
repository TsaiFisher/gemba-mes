/**
 * telemetry.js — 現場遙測：把手機上發生、伺服器看不到的事情回報回去（儀表板：報表管理 → 現場遙測）
 *
 * 回報四類（server 端定義見 Modules/RPT/Models/ClientTelemetryEvent.cs）：
 *  - Error／Rejection：頁面腳本錯誤、沒被接住的 Promise 錯誤（同一則在同一批裡累加次數，不重複送）
 *  - ApiStats：各支 /api/ 的呼叫數、耗時分桶（<1s／1–3s／3–10s／>10s）、連不上或 5xx 的次數、409 衝突次數
 *  - Offline：一段斷線斷了多久
 *  - Queue：這支手機上還有幾筆沒送出、最舊的等了多久（斷線保護的佇列，offline-queue.js）
 *
 * ⚠️ 必須是每頁**第一個**載入的腳本：要包住 window.fetch（api.js、offline-queue.js 都用它），
 *    也要在其他腳本出錯前就掛好 onerror。包 fetch 只量時間，不改任何請求或回應。
 *
 * 不送任何請求內容與查詢字串；路徑裡帶數字或很長的片段換成 {id}（伺服器會再做一次）。
 * 先存在 localStorage（最多 200 筆），連得上時每分鐘送一次，離開頁面時用 sendBeacon 補送。
 * 遙測本身出任何錯都只能吞掉——它不能成為讓頁面壞掉的原因。
 */

'use strict';

(function () {
    const ENDPOINT      = '/api/telemetry/events';
    const BUF_KEY       = 'mesTelemetryBuf';
    const DEVICE_KEY    = 'mesDeviceId';
    const OFFLINE_KEY   = 'mesTelemetryOfflineSince';
    const QUEUE_KEY     = 'mesTelemetryLastQueue';
    const MAX_BUFFER    = 200;
    const FLUSH_MS      = 60 * 1000;
    const MAX_PER_POST  = 50;

    const realFetch = typeof window.fetch === 'function' ? window.fetch.bind(window) : null;
    let appVersion = null;
    let apiStats = {};          // endpoint → { count, totalMs, maxMs, u1, u3, u10, o10, fail }

    function safe(fn) { try { return fn(); } catch { return undefined; } }

    function deviceId() {
        return safe(() => {
            let id = localStorage.getItem(DEVICE_KEY);
            if (!id) {
                id = (crypto.randomUUID && crypto.randomUUID()) || String(Date.now()) + Math.random().toString(16).slice(2);
                localStorage.setItem(DEVICE_KEY, id);
            }
            return id;
        }) || null;
    }

    function readBuffer() { return safe(() => JSON.parse(localStorage.getItem(BUF_KEY) || '[]')) || []; }
    function writeBuffer(buf) { safe(() => localStorage.setItem(BUF_KEY, JSON.stringify(buf.slice(-MAX_BUFFER)))); }

    function pagePath() { return safe(() => location.pathname) || null; }

    function connection() {
        const c = safe(() => navigator.connection);
        return c ? { netType: c.effectiveType || null, rttMs: typeof c.rtt === 'number' ? c.rtt : null } : {};
    }

    /** 路徑只留 path、去查詢字串，帶數字或很長的片段換成 {id}——不換的話每張派工都是不同的一列。 */
    function normalizeEndpoint(url) {
        const path = safe(() => new URL(url, location.href).pathname) || String(url).split(/[?#]/)[0];
        return path.split('/').map(s => (s.length > 24 || /\d/.test(s)) ? '{id}' : s).join('/');
    }

    /** 加進緩衝；同種類、同訊息、同頁的錯誤只累加次數。 */
    function push(ev) {
        const buf = readBuffer();
        const page = ev.page ?? pagePath();   // 先定下頁面再比對：拿還沒補頁面的 ev 去比，永遠比不到
        if (ev.kind === 'Error' || ev.kind === 'Rejection') {
            const same = buf.find(b => b.kind === ev.kind && b.message === ev.message && b.page === page);
            if (same) { same.count = (same.count || 1) + 1; writeBuffer(buf); return; }
        }
        buf.push({ count: 1, at: new Date().toISOString(), ...connection(), ...ev, page });
        writeBuffer(buf);
    }

    // ── 1. 頁面錯誤 ──────────────────────────────────────────────────────────
    window.addEventListener('error', e => safe(() => {
        // 資源載入失敗（img／script 404）也會進來，但沒有 message；只收腳本錯誤
        if (!e.message) return;
        const where = e.filename ? ` @ ${normalizeEndpoint(e.filename)}:${e.lineno || 0}:${e.colno || 0}` : '';
        push({ kind: 'Error', message: String(e.message).slice(0, 500),
               detail: ((e.error && e.error.stack) || '').slice(0, 2000) + where });
    }));
    window.addEventListener('unhandledrejection', e => safe(() => {
        const r = e.reason;
        push({ kind: 'Rejection', message: String((r && r.message) || r).slice(0, 500),
               detail: String((r && r.stack) || '').slice(0, 2000) });
    }));

    // ── 2. API 耗時（包 fetch，只量時間）───────────────────────────────────────
    function record(url, ms, failed, conflict) {
        const ep = normalizeEndpoint(url);
        const s = apiStats[ep] || (apiStats[ep] = { count: 0, totalMs: 0, maxMs: 0, u1: 0, u3: 0, u10: 0, o10: 0, fail: 0, conflict: 0 });
        s.count++; s.totalMs += ms; s.maxMs = Math.max(s.maxMs, ms);
        if (ms < 1000) s.u1++; else if (ms < 3000) s.u3++; else if (ms < 10000) s.u10++; else s.o10++;
        if (failed) s.fail++;
        if (conflict) s.conflict++;   // 409：別人同時改了同一張單（伺服器重試後仍衝突），不算故障
    }

    if (realFetch) {
        window.fetch = function (input, init) {
            const url = typeof input === 'string' ? input : (input && input.url) || '';
            const isApi = url.indexOf('/api/') >= 0 && url.indexOf(ENDPOINT) < 0;
            if (!isApi) return realFetch(input, init);
            const t0 = performance.now();
            return realFetch(input, init).then(
                res => { safe(() => record(url, Math.round(performance.now() - t0), res.status >= 500, res.status === 409)); return res; },
                err => { safe(() => record(url, Math.round(performance.now() - t0), true)); throw err; });
        };
    }

    // ── 3. 斷線多久（跨頁面重新載入也要接得起來：起點存 sessionStorage）──────────────
    window.addEventListener('offline', () => safe(() => {
        if (!sessionStorage.getItem(OFFLINE_KEY)) sessionStorage.setItem(OFFLINE_KEY, String(Date.now()));
    }));
    window.addEventListener('online', () => safe(() => {
        const since = Number(sessionStorage.getItem(OFFLINE_KEY));
        sessionStorage.removeItem(OFFLINE_KEY);
        if (since > 0) push({ kind: 'Offline', durationMs: Date.now() - since });
        flush();
    }));

    // ── 4. 手機上還沒送出的佇列 ─────────────────────────────────────────────────
    async function queueSnapshot() {
        const q = window.offlineQueue;
        if (!q || typeof q.getAll !== 'function') return;
        await q.ready();
        const unsent = (await q.getAll()).filter(r => r.status !== 'sent');
        const pending = unsent.length;
        const last = Number(safe(() => localStorage.getItem(QUEUE_KEY)) || 0);
        // 有未送出時每輪都報（儀表板看得到它還在）；歸零時報一次 0，儀表板才知道清掉了
        if (pending === 0 && last === 0) return;
        const oldest = unsent.reduce((m, r) => Math.min(m, r.enqueuedAt || Date.now()), Date.now());
        push({ kind: 'Queue', queuePending: pending, queueOldestMinutes: Math.floor((Date.now() - oldest) / 60000) });
        safe(() => localStorage.setItem(QUEUE_KEY, String(pending)));
    }

    function drainApiStats() {
        const ep = Object.keys(apiStats);
        if (!ep.length) return;
        const buf = readBuffer();
        for (const k of ep) {
            const s = apiStats[k];
            buf.push({ kind: 'ApiStats', at: new Date().toISOString(), page: pagePath(), endpoint: k, ...connection(),
                       count: s.count, durationMs: s.totalMs, maxMs: s.maxMs,
                       under1s: s.u1, under3s: s.u3, under10s: s.u10, over10s: s.o10, failures: s.fail, conflicts: s.conflict });
        }
        apiStats = {};
        writeBuffer(buf);
    }

    function envelope(events) {
        return JSON.stringify({ deviceId: deviceId(), appVersion, events });
    }

    let flushing = false;
    async function flush() {
        if (flushing || !realFetch) return;
        flushing = true;
        try {
            drainApiStats();
            await safe(queueSnapshot);
            if (!navigator.onLine) return;
            let buf = readBuffer();
            while (buf.length) {
                const batch = buf.slice(0, MAX_PER_POST);
                const body = envelope(batch);
                const res = await realFetch(ENDPOINT, {
                    // keepalive 的本文上限 64KB，超過時整個請求直接失敗——大批（含堆疊）就不帶
                    method: 'POST', credentials: 'include', keepalive: body.length < 60000,
                    headers: { 'Content-Type': 'application/json' }, body,
                }).catch(() => null);
                // 送不出去（斷線、限流、伺服器忙）就留著下一輪；收下了（2xx）才移出
                if (!res || !res.ok) return;
                buf = readBuffer().slice(batch.length);
                writeBuffer(buf);
            }
        } catch { /* 遙測不得影響頁面 */ }
        finally { flushing = false; }
    }

    // 離開頁面：fetch 可能來不及，改用 sendBeacon（不等回應；送出就清掉，最多丟一批）
    function beacon() {
        safe(() => {
            drainApiStats();
            const buf = readBuffer();
            if (!buf.length || !navigator.onLine || !navigator.sendBeacon) return;
            const batch = buf.slice(0, MAX_PER_POST);
            if (navigator.sendBeacon(ENDPOINT, new Blob([envelope(batch)], { type: 'application/json' })))
                writeBuffer(buf.slice(batch.length));
        });
    }
    window.addEventListener('pagehide', beacon);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') beacon(); });

    safe(() => {
        const timer = setInterval(flush, FLUSH_MS);
        timer?.unref?.();
    });

    // 版號＝Service Worker 的快取名稱（mes-mobile-vNN）；沒有 SW（被封鎖、第一次載入）就是 null
    safe(() => caches.keys().then(keys => { appVersion = keys.find(k => k.startsWith('mes-mobile-v')) || null; }).catch(() => {}));

    // 給測試與除錯用：手動送一輪、看緩衝
    window.mesTelemetry = { flush, buffer: readBuffer, normalizeEndpoint };
})();
