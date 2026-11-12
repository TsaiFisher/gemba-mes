/**
 * @jest-environment node
 */
'use strict';

/**
 * sw.js 的 API 請求一律不介入（Network-Only，v30）。
 *
 * 歷程：v24 以前派工／機台是快取優先（連線時也回舊值）；v24–v29 改成網路優先、連不上才回快取。
 * 2026-10-10 決定「離線作業」改為「斷線保護」：連不上時回最近一次的回應，只有開過的那幾筆、
 * 而且是舊的，現場看了會以為手機可以離線作業。讀取一律要連線，連不上由 api.js 說「需要連線」。
 *
 * 這裡釘的是：SW 對 /api/ 一律不呼叫 respondWith（交給瀏覽器直接打網路），
 * 以及 activate 會把舊版留下的 mes-dispatch-v1／mes-master-v1 清掉（裡面是前一個人的派工清單）。
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const swSrc = fs.readFileSync(path.resolve(__dirname, '..', '..', 'sw.js'), 'utf-8');

function loadSw(cacheNames = []) {
    const handlers = {};
    const deleted  = [];
    const context = vm.createContext({
        self: { addEventListener: (t, fn) => { handlers[t] = fn; }, registration: {},
                clients: { claim: () => Promise.resolve() } },
        caches: {
            open: () => Promise.resolve({ match: () => Promise.resolve(undefined), put: () => Promise.resolve() }),
            match: () => Promise.resolve(undefined),
            keys: () => Promise.resolve(cacheNames),
            delete: name => { deleted.push(name); return Promise.resolve(true); },
        },
        fetch: () => Promise.reject(new TypeError('Failed to fetch')),
        console: { warn: () => {}, log: () => {}, error: () => {} },
        URL, Response, Headers,
    });
    vm.runInContext(swSrc, context);

    const intercepts = url => {
        let called = false;
        handlers.fetch({ request: { url, method: 'GET', mode: 'cors' }, respondWith: () => { called = true; } });
        return called;
    };
    const activate = async () => {
        let p;
        handlers.activate({ waitUntil: x => { p = x; } });
        await p;
        return deleted;
    };
    const constant = name => vm.runInContext(name, context);
    return { intercepts, activate, constant };
}

test.each([
    ['/api/mobile/dispatches?dispatchId=D1'],
    ['/api/mobile/machines/M01'],
    ['/api/mobile/modules'],
    ['/api/warehouse/put-away/pending'],
])('%s：SW 不介入，讀取一律走網路（連不上就是連不上，不回舊資料）', (p) => {
    const sw = loadSw();
    expect(sw.intercepts('https://h' + p)).toBe(false);
});

test('靜態檔照樣由 SW 供應（殼層預快取不受影響）', () => {
    const sw = loadSw();
    expect(sw.intercepts('https://h/mobile/js/api.js')).toBe(true);
});

test('activate 清掉舊版的派工／主檔快取，只留目前的殼層', async () => {
    const sw = loadSw(['mes-mobile-v29', 'mes-dispatch-v1', 'mes-master-v1']);
    const current = sw.constant('CACHE_NAME');

    const deleted = await sw.activate();

    expect(deleted.sort()).toEqual(['mes-dispatch-v1', 'mes-master-v1', 'mes-mobile-v29']);
    expect(deleted).not.toContain(current);
});
