'use strict';

/**
 * i18n.js 單元測試（manual-test-plan §12.4「手機其餘 JS 零覆蓋」）。
 *
 * 字典鍵的中英對稱已由 C# 端的 MobileI18nKeyTests 守住，這裡守的是**取用行為**：
 * 查不到 key 時 applyI18n 必須「不覆寫」而不是寫回原始鍵——舊版 Service Worker
 * 快取住舊的 i18n.js 時，畫面會出現 "dispatch.op_section" 這種東西給現場看，
 * 而那正是手機端最容易發生、又最沒有錯誤訊息的一種失效。
 */

const fs   = require('fs');
const path = require('path');

// 引擎＋共用字串（與 Core 一致）之後接 ProjectZ 模組字典，與頁面載入順序相同
const scriptSrc = fs.readFileSync(path.resolve(__dirname, '..', 'i18n.js'), 'utf-8') + '\n' +
    fs.readFileSync(path.resolve(__dirname, '..', 'i18n.modules.js'), 'utf-8');

function fakeStorage(initial = {}) {
    const store = new Map(Object.entries(initial));
    return {
        getItem: (k) => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, String(v)),
        removeItem: (k) => store.delete(k),
        clear: () => store.clear(),
    };
}

function loadI18n(lang) {
    const local = fakeStorage(lang ? { mobileLang: lang } : {});
    const fakeLocation = { pathname: '/mobile/home.html', search: '', reload: jest.fn() };
    const fetchMock = jest.fn(() => Promise.resolve({ ok: true }));

    const factory = new Function(
        'document', 'localStorage', 'location', 'fetch', 'encodeURIComponent',
        `${scriptSrc}
         return { t, tf, lookup, getLang, applyI18n, setLang, toggleLang, STRINGS };`
    );
    const i18n = factory(document, local, fakeLocation, fetchMock, encodeURIComponent);
    return { i18n, local, fakeLocation, fetchMock };
}

describe('getLang / t / tf', () => {
    test('沒設過語系時預設 zh-TW', () => {
        const { i18n } = loadI18n(null);
        expect(i18n.getLang()).toBe('zh-TW');
    });

    test('t 查得到就回譯文，查不到回 key 本身（便於除錯）', () => {
        const { i18n } = loadI18n('zh-TW');
        const someKey = Object.keys(i18n.STRINGS['zh-TW'])[0];

        expect(i18n.t(someKey)).toBe(i18n.STRINGS['zh-TW'][someKey]);
        expect(i18n.t('no.such.key')).toBe('no.such.key');
    });

    test('英文缺 key 時退回中文，而不是回原始鍵', () => {
        // 兩份字典本來就該對稱（C# 守門在管），但退路仍要是「看得懂的字」。
        const { i18n } = loadI18n('en-US');
        const zhOnly = Object.keys(i18n.STRINGS['zh-TW'])
            .find(k => i18n.STRINGS['en-US'][k] === undefined);

        if (zhOnly) expect(i18n.t(zhOnly)).toBe(i18n.STRINGS['zh-TW'][zhOnly]);
        else expect(zhOnly).toBeUndefined();   // 目前完全對稱，這條路走不到
    });

    test('tf 依序代入 {0}{1}，缺參數時保留佔位符而不是印出 undefined', () => {
        const { i18n } = loadI18n('zh-TW');
        i18n.STRINGS['zh-TW']['__test.fmt'] = '{0} 之 {1}';

        expect(i18n.tf('__test.fmt', 'A', 'B')).toBe('A 之 B');
        expect(i18n.tf('__test.fmt', 'A')).toBe('A 之 {1}');
    });
});

describe('applyI18n', () => {
    test('套用 text/placeholder/title/html 四種標記', () => {
        const { i18n } = loadI18n('zh-TW');
        Object.assign(i18n.STRINGS['zh-TW'], {
            '__t.text': '文字', '__t.ph': '提示', '__t.title': '標題', '__t.html': '<b>粗</b>',
        });
        document.body.innerHTML = `
            <span data-i18n="__t.text">舊</span>
            <input data-i18n-placeholder="__t.ph" />
            <span data-i18n-title="__t.title"></span>
            <div data-i18n-html="__t.html"></div>`;

        i18n.applyI18n();

        expect(document.querySelector('[data-i18n]').textContent).toBe('文字');
        expect(document.querySelector('[data-i18n-placeholder]').placeholder).toBe('提示');
        expect(document.querySelector('[data-i18n-title]').title).toBe('標題');
        expect(document.querySelector('[data-i18n-html]').innerHTML).toBe('<b>粗</b>');
    });

    test('查無 key 一律不覆寫，保留 HTML 既有文字', () => {
        // 這是舊版 SW 快取住舊字典時唯一的防線：畫面上寧可是舊的中文，
        // 也不能變成 "dispatch.op_section" 這種原始鍵給現場看。
        const { i18n } = loadI18n('zh-TW');
        document.body.innerHTML = '<span data-i18n="no.such.key">原本的中文</span>';

        i18n.applyI18n();

        expect(document.querySelector('span').textContent).toBe('原本的中文');
    });

    test('同步更新 <html lang>', () => {
        const { i18n } = loadI18n('en-US');
        i18n.applyI18n();
        expect(document.documentElement.lang).toBe('en-US');
    });
});

describe('setLang / toggleLang', () => {
    test('已登入：POST 帳號偏好 API（同時寫 cookie），不再打 GET，並重載', async () => {
        const { i18n, local, fakeLocation, fetchMock } = loadI18n('zh-TW');

        await i18n.setLang('en-US');

        expect(local.getItem('mobileLang')).toBe('en-US');
        expect(fetchMock).toHaveBeenCalledTimes(1);
        const [url, init] = fetchMock.mock.calls[0];
        expect(url).toBe('/api/mobile/auth/language');
        expect(init.method).toBe('POST');
        expect(JSON.parse(init.body)).toEqual({ culture: 'en-US' });
        expect(fakeLocation.reload).toHaveBeenCalled();
    });

    test('未登入（API 非 2xx）：退回 GET 只寫 cookie', async () => {
        const { i18n, fakeLocation, fetchMock } = loadI18n('zh-TW');
        fetchMock.mockImplementationOnce(() => Promise.resolve({ ok: false, status: 401 }));

        await i18n.setLang('en-US');

        expect(fetchMock).toHaveBeenCalledTimes(2);
        expect(fetchMock.mock.calls[1][0]).toContain('/UserSettings/SetLanguage?culture=en-US');
        expect(fakeLocation.reload).toHaveBeenCalled();
    });

    test('同步桌機 cookie 失敗不得影響手機端切換', async () => {
        const { i18n, local, fakeLocation } = loadI18n('zh-TW');
        // 手機常在弱網下切語系；這裡若讓例外冒出來，語系就切不成而且沒有訊息。
        const factoryFetch = jest.fn(() => Promise.reject(new Error('offline')));
        const scoped = new Function('document', 'localStorage', 'location', 'fetch', 'encodeURIComponent',
            `${scriptSrc} return { setLang };`)(
            document, local, fakeLocation, factoryFetch, encodeURIComponent);

        await expect(scoped.setLang('en-US')).resolves.toBeUndefined();
        expect(local.getItem('mobileLang')).toBe('en-US');
        expect(fakeLocation.reload).toHaveBeenCalled();
    });

    test('toggleLang 在兩種語言之間來回', async () => {
        const { i18n, local } = loadI18n('zh-TW');
        await i18n.toggleLang();
        expect(local.getItem('mobileLang')).toBe('en-US');
    });
});
