'use strict';

/**
 * nav.js 單元測試（manual-test-plan §12.4「手機其餘 JS 零覆蓋」）。
 *
 * historyBack 的整個存在理由就是「不要停在死路」：掃 QR 直接進單頁、
 * PWA 冷啟動、外部連結開啟——這些情況沒有同源上一頁，history.back() 會什麼都不做，
 * 現場看到的是「返回鍵按了沒反應」，而不是任何錯誤。這條路人工測試最容易漏，
 * 因為測試員通常是從首頁一頁一頁點進去的，永遠有上一頁。
 */

const fs   = require('fs');
const path = require('path');

const scriptSrc = fs.readFileSync(path.resolve(__dirname, '..', 'nav.js'), 'utf-8');

/**
 * @param {number} historyLength window.history.length
 * @param {string} referrer      document.referrer
 */
function loadNav(historyLength, referrer) {
    const back = jest.fn();
    const reload = jest.fn();
    const fakeWindow = {
        history: { length: historyLength, back },
        location: { href: '', origin: 'http://localhost', reload },
    };
    const fakeDocument = { referrer };

    const factory = new Function('window', 'document', 'URL', 'location',
        `${scriptSrc}
         return { historyBack: window.historyBack, reloadPage: window.reloadPage };`);
    const nav = factory(fakeWindow, fakeDocument, URL, fakeWindow.location);
    return { nav, fakeWindow, back, reload };
}

const el = (fallback) => (fallback === undefined ? null : { dataset: { fallback } });

describe('historyBack', () => {
    test('有同源上一頁時走瀏覽器返回', () => {
        const { nav, back, fakeWindow } = loadNav(3, 'http://localhost/mobile/home.html');

        expect(nav.historyBack(null, el('/mobile/mfg/list.html'))).toBe(false);

        expect(back).toHaveBeenCalled();
        expect(fakeWindow.location.href).toBe('');
    });

    test('沒有上一頁（PWA 冷啟動）改導 data-fallback', () => {
        const { nav, back, fakeWindow } = loadNav(1, 'http://localhost/mobile/home.html');

        nav.historyBack(null, el('/mobile/mfg/list.html'));

        expect(back).not.toHaveBeenCalled();
        expect(fakeWindow.location.href).toBe('/mobile/mfg/list.html');
    });

    test('上一頁是外部網站（掃 QR 從別的 App 進來）也走 fallback', () => {
        // 這裡若誤判為可返回，使用者會被送回掃碼 App 或搜尋結果頁，等於離開系統。
        const { nav, back, fakeWindow } = loadNav(5, 'https://example.com/qr');

        nav.historyBack(null, el('/mobile/mfg/list.html'));

        expect(back).not.toHaveBeenCalled();
        expect(fakeWindow.location.href).toBe('/mobile/mfg/list.html');
    });

    test('沒有 referrer（直接開網址）走 fallback', () => {
        const { nav, back, fakeWindow } = loadNav(5, '');

        nav.historyBack(null, el('/mobile/inv/count.html'));

        expect(back).not.toHaveBeenCalled();
        expect(fakeWindow.location.href).toBe('/mobile/inv/count.html');
    });

    test('referrer 不是合法 URL 時不得擲例外，退回 fallback', () => {
        const { nav, fakeWindow } = loadNav(5, 'not a url');

        expect(() => nav.historyBack(null, el('/mobile/home.html'))).not.toThrow();
        expect(fakeWindow.location.href).toBe('/mobile/home.html');
    });

    test('沒指定 data-fallback 時退回首頁而不是空字串', () => {
        const { nav, fakeWindow } = loadNav(1, '');

        nav.historyBack(null, null);

        expect(fakeWindow.location.href).toBe('/mobile/home.html');
    });
});

describe('reloadPage', () => {
    test('重新載入目前網址並回 false 阻止預設行為', () => {
        const { nav, reload } = loadNav(1, '');

        expect(nav.reloadPage()).toBe(false);
        expect(reload).toHaveBeenCalled();
    });
});
