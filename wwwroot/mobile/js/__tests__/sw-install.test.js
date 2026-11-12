'use strict';

/**
 * sw.js 的 install：預快取不可全有全無，但離線替代頁一定要在。
 *
 * 2026-09-27 手機實測：原本 `cache.addAll(STATIC_ASSETS).catch(console.warn)`，安裝當下
 * 網路抖一下 → 整批作廢 → catch 吞掉 → SW 照樣啟用，手機上一個預快取都沒有，
 * 離線開沒去過的頁面得到 Chrome 原生「HTTP 503」（連 offline.html 都拿不到）。
 * 而且直到下次 bump 都不會重試。
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const swSrc = fs.readFileSync(path.resolve(__dirname, '..', '..', 'sw.js'), 'utf-8');

/** 載入 sw.js，回傳 install 處理器與觀測點。failing：add 時要失敗的網址。 */
function loadSw(failing = []) {
    const handlers = {};
    const stored   = new Set();
    const warn     = jest.fn();
    const self = {
        addEventListener: (type, fn) => { handlers[type] = fn; },
        skipWaiting: jest.fn(() => Promise.resolve()),
        clients: { claim: () => Promise.resolve() },
        registration: {},
    };
    const modes = new Set();
    // v28 起 install 傳的是 new Request(url, { cache: 'reload' })，不是字串。
    // 沙箱原本沒有 Request，三支測試從那時起一直是紅的（2026-10-12 補上）。
    class Request { constructor(url, init) { this.url = url; this.cache = init?.cache; } }
    const cache = {
        add: jest.fn(req => {
            const url = typeof req === 'string' ? req : req.url;
            modes.add(typeof req === 'string' ? 'default' : req.cache);
            return failing.includes(url)
                ? Promise.reject(new TypeError(`Failed to fetch ${url}`))
                : (stored.add(url), Promise.resolve());
        }),
    };
    const context = vm.createContext({
        self,
        caches: { open: jest.fn(() => Promise.resolve(cache)) },
        console: { warn, log: () => {}, error: () => {} },
        URL, Request,
    });
    vm.runInContext(swSrc, context);

    const install = () => {
        let pending;
        handlers.install({ waitUntil: p => { pending = p; } });
        return pending;
    };
    const constant = name => vm.runInContext(name, context);
    return { install, stored, self, warn, constant, modes };
}

test('全部成功時每個預快取檔都進快取，並立即接管', async () => {
    const sw = loadSw();
    await sw.install();

    expect([...sw.stored].sort()).toEqual([...sw.constant('STATIC_ASSETS')].sort());
    expect(sw.self.skipWaiting).toHaveBeenCalled();
    expect(sw.warn).not.toHaveBeenCalled();
});

test('預快取一律跳過瀏覽器 HTTP 快取——否則 bump 版本名照樣收到舊檔（v28，P-5 改版送不到）', async () => {
    const sw = loadSw();
    await sw.install();

    expect([...sw.modes]).toEqual(['reload']);
});

test('其中一檔失敗時其餘照樣進快取，離線替代頁也在', async () => {
    const sw = loadSw(['/mobile/mfg/reports.html']);
    await sw.install();   // 不可 reject：一檔失敗不該讓整個 SW 裝不起來

    const expected = sw.constant('STATIC_ASSETS').filter(u => u !== '/mobile/mfg/reports.html');
    expect([...sw.stored].sort()).toEqual([...expected].sort());
    expect(sw.stored.has(sw.constant('OFFLINE_PAGE'))).toBe(true);
    expect(sw.self.skipWaiting).toHaveBeenCalled();
    // 失敗要留下看得到的訊號，且點名是哪一檔。
    expect(sw.warn).toHaveBeenCalledWith(expect.any(String), ['/mobile/mfg/reports.html']);
});

test('離線替代頁失敗時 install 必須失敗，讓瀏覽器下次重裝', async () => {
    const sw = loadSw(['/mobile/offline.html']);

    await expect(sw.install()).rejects.toThrow('offline.html');
    // 沒有替代頁的 SW 不可接管：接管後離線導覽只剩原生 503。
    expect(sw.self.skipWaiting).not.toHaveBeenCalled();
});
