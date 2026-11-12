'use strict';

/**
 * telemetry.js 單元測試。
 *
 * 最重要的一條是**包住 fetch 不得改變任何行為**：api.js 與 offline-queue.js 的每一次請求都經過它，
 * 回應被吃掉、例外被吞掉、或例外型別變了（AbortError 變成別的），斷線保護就會整個判斷錯——
 * 而那種錯不會有任何訊息。其餘是彙總與送出規則。
 *
 * 每個測試以 new Function 注入一個獨立的假 window（自己的 EventTarget 與 fetch），
 * 免得多次載入在真的 jsdom window 上疊出好幾份監聽器。
 */

const fs   = require('fs');
const path = require('path');

const scriptSrc = fs.readFileSync(path.resolve(__dirname, '..', 'telemetry.js'), 'utf-8');

function load(fetchImpl) {
    localStorage.clear();
    sessionStorage.clear();
    const target = new EventTarget();
    const win = {
        fetch: fetchImpl,
        addEventListener: target.addEventListener.bind(target),
        dispatchEvent: target.dispatchEvent.bind(target),
    };
    new Function('window', scriptSrc)(win);   // eslint-disable-line no-new-func
    return win;
}

const ok = (status = 200) => ({ ok: status >= 200 && status < 300, status });
const buffer = () => JSON.parse(localStorage.getItem('mesTelemetryBuf') || '[]');

beforeEach(() => {
    Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
});

describe('包住 fetch：只量時間，不改行為', () => {

    test('回應原樣交回（同一個物件）', async () => {
        const res = ok(201);
        const win = load(jest.fn(async () => res));
        expect(await win.fetch('/api/mobile/reports', { method: 'POST' })).toBe(res);
    });

    test('例外原樣擲出——AbortError 必須還是 AbortError，api.js 靠它判斷逾時', async () => {
        const abort = new DOMException('aborted', 'AbortError');
        const win = load(jest.fn(async () => { throw abort; }));
        await expect(win.fetch('/api/mobile/reports')).rejects.toBe(abort);
    });

    test('參數原樣傳給真正的 fetch（含 signal）', async () => {
        const real = jest.fn(async () => ok());
        const win = load(real);
        const ctrl = new AbortController();
        const init = { method: 'PATCH', body: '{}', signal: ctrl.signal };
        await win.fetch('/api/mobile/sessions/abc/complete', init);
        expect(real).toHaveBeenCalledWith('/api/mobile/sessions/abc/complete', init);
    });

    test('非 /api/ 的請求不量', async () => {
        const win = load(jest.fn(async () => ok()));
        await win.fetch('/mobile/css/mobile.css');
        await win.mesTelemetry.flush();
        expect(buffer().filter(e => e.kind === 'ApiStats')).toHaveLength(0);
    });
});

describe('API 統計', () => {

    test('依端點彙總，編號片段換成 {id}；5xx 與連不上算失敗、4xx 不算', async () => {
        const replies = [ok(200), ok(503), ok(400)];
        const real = jest.fn(async (url) => {
            if (url === ENDPOINT_TELEMETRY) return { ok: false, status: 503 };   // 先不讓它送走，好看緩衝
            const r = replies.shift();
            if (!r) throw new TypeError('Failed to fetch');
            return r;
        });
        const win = load(real);
        await win.fetch('/api/mobile/sessions/3f2a9c1e-0000-4000-8000-000000000001/complete');
        await win.fetch('/api/mobile/sessions/7b1e4d22-0000-4000-8000-000000000002/complete');
        await win.fetch('/api/mobile/sessions/9c9c9c9c-0000-4000-8000-000000000003/complete');
        await expect(win.fetch('/api/mobile/sessions/aaaa1111-0000-4000-8000-000000000004/complete')).rejects.toThrow();
        await win.mesTelemetry.flush();

        const stats = buffer().filter(e => e.kind === 'ApiStats');
        expect(stats).toHaveLength(1);
        expect(stats[0].endpoint).toBe('/api/mobile/sessions/{id}/complete');
        expect(stats[0].count).toBe(4);
        expect(stats[0].failures).toBe(2);       // 503 ＋ 連不上
    });

    test('409 計成衝突、不算失敗——那是別人同時改了同一張單，不是故障', async () => {
        const real = jest.fn(async (url) => url === ENDPOINT_TELEMETRY ? { ok: false, status: 503 } : ok(409));
        const win = load(real);
        await win.fetch('/api/mobile/reports');
        await win.fetch('/api/mobile/reports');
        await win.mesTelemetry.flush();
        const s = buffer().find(e => e.kind === 'ApiStats');
        expect(s).toMatchObject({ endpoint: '/api/mobile/reports', count: 2, conflicts: 2, failures: 0 });
    });

    test('路徑正規化：帶數字的片段（機台代號）也換掉，查詢字串去掉', () => {
        const win = load(jest.fn(async () => ok()));
        expect(win.mesTelemetry.normalizeEndpoint('/api/mobile/machines/M01/status-event?x=1'))
            .toBe('/api/mobile/machines/{id}/status-event');
    });
});

const ENDPOINT_TELEMETRY = '/api/telemetry/events';

describe('頁面錯誤', () => {

    test('同一則錯誤在同一批裡只累加次數', () => {
        const win = load(jest.fn(async () => ok()));
        const ev = () => Object.assign(new Event('error'), { message: 'x is not defined', filename: '/mobile/mfg/reports.page.js', lineno: 3 });
        win.dispatchEvent(ev());
        win.dispatchEvent(ev());
        const errs = buffer().filter(e => e.kind === 'Error');
        expect(errs).toHaveLength(1);
        expect(errs[0].count).toBe(2);
    });

    test('資源載入失敗（沒有 message）不收', () => {
        const win = load(jest.fn(async () => ok()));
        win.dispatchEvent(new Event('error'));
        expect(buffer()).toHaveLength(0);
    });
});

describe('送出', () => {

    test('伺服器收下（2xx）才清掉緩衝；送不出去留著下一輪', async () => {
        let accept = false;
        const posts = [];
        const win = load(jest.fn(async (url, init) => {
            if (url === ENDPOINT_TELEMETRY) { posts.push(JSON.parse(init.body)); return accept ? ok(204) : ok(429); }
            return ok();
        }));
        win.dispatchEvent(Object.assign(new Event('error'), { message: 'boom' }));

        await win.mesTelemetry.flush();
        expect(buffer()).toHaveLength(1);         // 429：留著

        accept = true;
        await win.mesTelemetry.flush();
        expect(buffer()).toHaveLength(0);
        expect(posts.at(-1).deviceId).toBeTruthy();
        expect(posts.at(-1).events[0]).toMatchObject({ kind: 'Error', message: 'boom', count: 1 });
    });

    test('斷線時不送；恢復連線記一筆斷線事件並送出', async () => {
        const posts = [];
        const win = load(jest.fn(async (url, init) => {
            if (url === ENDPOINT_TELEMETRY) posts.push(JSON.parse(init.body));
            return ok(204);
        }));
        Object.defineProperty(window.navigator, 'onLine', { value: false, configurable: true });
        win.dispatchEvent(new Event('offline'));
        await win.mesTelemetry.flush();
        expect(posts).toHaveLength(0);

        Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
        win.dispatchEvent(new Event('online'));
        for (let i = 0; i < 20 && posts.length === 0; i++) await new Promise(r => setTimeout(r, 5));
        expect(posts.flatMap(p => p.events).some(e => e.kind === 'Offline' && e.durationMs >= 0)).toBe(true);
    });
});
