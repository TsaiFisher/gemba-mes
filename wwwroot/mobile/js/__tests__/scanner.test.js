'use strict';

/**
 * scanner.js 鍵盤楔型（keyboard wedge）單元測試
 * （manual-test-plan §12.4「手機其餘 JS 零覆蓋」）。
 *
 * 相機掃描需要真實裝置，這裡不碰；測的是**掃描槍**那條路，也就是現場實際天天在用的那條。
 * 它的兩種失效都沒有錯誤訊息：
 *   ① 條碼字元漏進聚焦中的 input → 畫面出現一串亂碼，使用者以為自己按錯；
 *   ② 掃了沒反應 → 使用者重掃，於是同一筆被送出兩次。
 * 兩者都取決於「相鄰字元間距」這個時間判準，人工測試靠手打根本重現不了掃描槍的速度。
 */

const fs   = require('fs');
const path = require('path');

const scriptSrc = fs.readFileSync(path.resolve(__dirname, '..', 'scanner.js'), 'utf-8');

let now;   // 由測試控制的時鐘：掃描槍與人手的差別只有間距

function makeScanner(opts = {}) {
    const listeners = [];
    const fakeWindow = {
        addEventListener: (type, fn, capture) => listeners.push({ type, fn, capture }),
        removeEventListener: (type, fn) => {
            const i = listeners.findIndex(l => l.type === type && l.fn === fn);
            if (i >= 0) listeners.splice(i, 1);
        },
    };
    const fakeDate = { now: () => now };

    const Ctor = new Function('window', 'document', 'Date', 'navigator', 'console',
        `${scriptSrc} return BarcodeScanner;`)(
        fakeWindow, document, fakeDate, {}, console);

    const scanned = [];
    const sc = new Ctor({ onScan: (code) => scanned.push(code), ...opts });
    return { sc, scanned, listeners, fakeWindow };
}

/** 送一個 keydown，gap 是距離上一鍵的毫秒數。 */
function key(listeners, k, gap) {
    now += gap;
    const e = { key: k, preventDefault: jest.fn() };
    listeners.filter(l => l.type === 'keydown').forEach(l => l.fn(e));
    return e;
}

/** 用固定間距刷一整串條碼（含結尾 Enter）。 */
function scan(listeners, code, gap) {
    const events = [...code].map(ch => key(listeners, ch, gap));
    events.push(key(listeners, 'Enter', gap));
    return events;
}

beforeEach(() => { now = 1_000_000; });

describe('掃描槍輸入', () => {
    test('快速連續字元＋Enter＝一次掃描', () => {
        const { scanned, listeners } = makeScanner();

        scan(listeners, 'WO2026000001', 10);

        expect(scanned).toEqual(['WO2026000001']);
    });

    test('條碼字元不得落入聚焦中的 input——否則畫面會多出一串亂碼', () => {
        const { listeners } = makeScanner();

        const events = scan(listeners, 'ABC123', 10);

        // 第一個字元無從判斷來源，放行；之後只要間距在閾值內就攔下。
        expect(events[0].preventDefault).not.toHaveBeenCalled();
        expect(events[1].preventDefault).toHaveBeenCalled();
        expect(events[5].preventDefault).toHaveBeenCalled();
        // 結尾 Enter 也要攔，否則會順便觸發表單送出
        expect(events[6].preventDefault).toHaveBeenCalled();
    });

    test('低於長度門檻的字串不算掃描——避免人手打幾個字加 Enter 被誤判', () => {
        const { scanned, listeners } = makeScanner({ wedgeMinLength: 5 });

        scan(listeners, 'AB', 10);

        expect(scanned).toEqual([]);
    });

    test('門檻與間距可由系統參數覆寫', () => {
        const { scanned, listeners } = makeScanner({ wedgeMinLength: 3, wedgeIntervalThresholdMs: 200 });

        scan(listeners, 'X1Y', 150);   // 150ms 在放寬後的閾值內

        expect(scanned).toEqual(['X1Y']);
    });
});

describe('人工輸入', () => {
    test('間距過大時清空暫存並讓字元進 input', () => {
        const { scanned, listeners } = makeScanner();

        const a = key(listeners, 'A', 10);
        const b = key(listeners, 'B', 500);   // 人手打字
        key(listeners, 'Enter', 500);

        expect(a.preventDefault).not.toHaveBeenCalled();
        expect(b.preventDefault).not.toHaveBeenCalled();
        expect(scanned).toEqual([]);
    });

    test('慢慢打滿長度也不會被當成掃描', () => {
        const { scanned, listeners } = makeScanner();

        scan(listeners, 'ABCDEFGH', 300);

        expect(scanned).toEqual([]);
    });

    test('空的 Enter 不觸發任何事——現場很常直接按 Enter 送出表單', () => {
        const { scanned, listeners } = makeScanner();

        const e = key(listeners, 'Enter', 10);

        expect(scanned).toEqual([]);
        expect(e.preventDefault).not.toHaveBeenCalled();
    });
});

describe('楔型監聽的開關', () => {
    test('預設自動啟用，且以 capture 階段監聽（要搶在 input 之前）', () => {
        const { listeners } = makeScanner();
        const l = listeners.find(x => x.type === 'keydown');
        expect(l).toBeDefined();
        expect(l.capture).toBe(true);
    });

    test('可指定不自動啟用', () => {
        const { listeners } = makeScanner({ wedgeAutoEnable: false });
        expect(listeners.filter(x => x.type === 'keydown')).toHaveLength(0);
    });

    test('重複 enable 不會掛上第二個監聽（否則同一次掃描會送出兩筆）', () => {
        const { sc, listeners } = makeScanner();

        sc.enableWedge();
        sc.enableWedge();

        expect(listeners.filter(x => x.type === 'keydown')).toHaveLength(1);
    });

    test('destroy 之後不再回呼', () => {
        const { sc, scanned, listeners } = makeScanner();

        sc.destroy();
        scan(listeners, 'WO2026000001', 10);

        expect(scanned).toEqual([]);
    });
});

describe('onScan 的例外隔離', () => {
    test('呼叫端擲例外不得把掃描器本身弄壞', () => {
        // 一次失敗就讓後續掃描全部失效的話，現場只會發現「掃描器突然不能用了」。
        let calls = 0;
        const { sc, listeners } = makeScanner({
            onScan: () => { calls++; if (calls === 1) throw new Error('頁面處理失敗'); },
        });
        const spy = jest.spyOn(console, 'error').mockImplementation(() => {});

        expect(() => scan(listeners, 'CODE12345', 10)).not.toThrow();
        scan(listeners, 'CODE67890', 10);

        expect(calls).toBe(2);
        spy.mockRestore();
        sc.destroy();
    });
});
