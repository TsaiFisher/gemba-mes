'use strict';

/**
 * api.js 單元測試（manual-test-plan §12.4「手機其餘 JS 零覆蓋」）。
 *
 * 為什麼從 api.js 開始：手機端每一支畫面的每一次後端呼叫都經過 apiCall，
 * 而它的錯誤處理全都是「安靜的」——回 null，由呼叫端自行放棄。
 * 一旦 401 分支判斷錯，症狀是「登入頁一直跳回登入頁」而不是任何錯誤訊息：
 * 這正是手機 2FA 登入迴圈那個缺陷的形狀（登入端點的 401 代表「帳密／驗證碼錯誤」，
 * 被當成 session 過期就會清掉 session 再導回登入頁）。
 *
 * ── 為什麼用 new Function 而不是 eval ──
 * 要斷言「有沒有導頁」就得攔截 window.location.href 的寫入，而 jsdom 的
 * window.location 是 [Unforgeable]：delete 不掉、defineProperty 也會被拒絕。
 * 所以把整支腳本包進一個函式、由參數注入假的 window，這樣不必為了測試去改產品程式。
 */

const fs   = require('fs');
const path = require('path');

const scriptSrc = fs.readFileSync(path.resolve(__dirname, '..', 'api.js'), 'utf-8');

/** 每個測試都重新載入一次腳本，避免 _uomDecimalsPromise 這種模組級快取跨測試殘留。 */
function loadApi() {
    const fakeWindow = { location: { href: '' } };
    const store = new Map();
    const fakeSession = {
        getItem: (k) => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, String(v)),
        removeItem: (k) => store.delete(k),
        clear: () => store.clear(),
    };
    const toasts = [];

    const factory = new Function(
        'window', 'document', 'sessionStorage', 'fetch', 'bootstrap', 't', 'tf', '__toasts',
        `${scriptSrc}
         // showToast 是同檔宣告，外面覆寫全域也蓋不到它——包一層記錄再呼叫原本的，
         // 這樣連「真的有插進 DOM 嗎」都一併驗到。
         const __origToast = showToast;
         showToast = function (m, ty) { __toasts.push({ msg: m, type: ty }); return __origToast(m, ty); };
         return { apiCall, formatQty, setLoading, showToast, getUomDecimalsMap,
                  apiGet, clearOfflineUserData };`
    );

    const api = factory(
        fakeWindow, document, fakeSession,
        (...a) => global.fetch(...a),
        { Toast: class { show() {} } },
        (key) => key,
        (key, ...args) => `${key}:${args.join(',')}`,
        toasts
    );
    return { api, fakeWindow, fakeSession, toasts };
}

beforeEach(() => {
    document.body.innerHTML = '<div id="toast-container"></div>';
});

const jsonRes = (status, body, ok) => ({
    status,
    ok: ok ?? (status >= 200 && status < 300),
    json: () => Promise.resolve(body),
});
const mockFetch = (res) => { global.fetch = jest.fn(() => Promise.resolve(res)); };

describe('apiCall 的回應處理', () => {
    test('2xx 回傳解析後的 JSON', async () => {
        const { api } = loadApi();
        mockFetch(jsonRes(200, { id: 7 }));
        await expect(api.apiCall('GET', '/api/x')).resolves.toEqual({ id: 7 });
    });

    test('204 回傳空物件而不是 null——呼叫端以 null 判定失敗，回 null 會把成功當成失敗', async () => {
        const { api } = loadApi();
        mockFetch(jsonRes(204, undefined));
        await expect(api.apiCall('DELETE', '/api/x')).resolves.toEqual({});
    });

    test('403 提示無權限並回 null，且不動 session', async () => {
        const { api, fakeSession, toasts } = loadApi();
        fakeSession.setItem('mobileUser', '{}');
        mockFetch(jsonRes(403, {}));

        await expect(api.apiCall('GET', '/api/x')).resolves.toBeNull();
        expect(toasts[0].type).toBe('danger');
        expect(fakeSession.getItem('mobileUser')).toBe('{}');
    });

    // 2026-11-11 筆電驗收 A4：廠別失效時只顯示「權限不足」，使用者不知道切換廠別就能恢復
    test.each([
        ['PLANT_CONTEXT_INVALID', 'common.plant_invalid'],
        ['ACTIVE_ROLE_REVOKED',   'common.role_revoked'],
        ['SOMETHING_ELSE',        'common.forbidden'],
    ])('403 依錯誤代碼 %s 提示 %s', async (code, key) => {
        const { api, toasts } = loadApi();
        mockFetch(jsonRes(403, { success: false, message: 'x', error: { code, message: 'x' } }));
        await expect(api.apiCall('GET', '/api/x')).resolves.toBeNull();
        expect(toasts).toEqual([{ msg: key, type: 'danger' }]);
    });

    test('4xx 顯示後端訊息；後端沒給訊息時退回帶狀態碼的通用訊息', async () => {
        const { api, toasts } = loadApi();
        mockFetch(jsonRes(400, { message: '數量必須大於 0' }));
        await expect(api.apiCall('POST', '/api/x', { a: 1 })).resolves.toBeNull();
        expect(toasts[0].msg).toBe('數量必須大於 0');
        expect(document.querySelector('.toast-body').textContent).toBe('數量必須大於 0');

        mockFetch({ status: 500, ok: false, json: () => Promise.reject(new Error('not json')) });
        await expect(api.apiCall('POST', '/api/x')).resolves.toBeNull();
        expect(toasts[1].msg).toContain('500');
    });

    test('手機 API 的錯誤封包 { error: { code, message } } 也要顯示原因', async () => {
        // 原本只讀 err.message，手機端所有錯誤原因（狀態不允許、機台不可用…）都只剩「操作失敗 400」
        const { api, toasts } = loadApi();
        mockFetch(jsonRes(400, { success: false, error: { code: 'MACHINE_UNAVAILABLE', message: '機台目前為「故障停機」' } }));
        await expect(api.apiCall('POST', '/api/mobile/sessions', {})).resolves.toBeNull();
        expect(toasts[0].msg).toBe('機台目前為「故障停機」');
    });

    test('網路中斷時提示且不擲例外——手機常態在弱網下，例外會讓整頁停住', async () => {
        const { api, toasts } = loadApi();
        global.fetch = jest.fn(() => Promise.reject(new TypeError('Failed to fetch')));

        await expect(api.apiCall('GET', '/api/x')).resolves.toBeNull();
        expect(toasts[0].msg).toBe('common.net_error');
    });
});

describe('401 的兩種語意（登入迴圈的根因）', () => {
    test('一般端點的 401＝session 過期：清空 session 並導回登入頁', async () => {
        const { api, fakeWindow, fakeSession } = loadApi();
        fakeSession.setItem('mobileUser', '{"id":"u1"}');
        localStorage.setItem('mobileUserOffline', '{"user":{"id":"u1"},"verifiedAt":0}');
        mockFetch(jsonRes(401, {}));

        await expect(api.apiCall('GET', '/api/mobile/mfg/list')).resolves.toBeNull();
        expect(fakeSession.getItem('mobileUser')).toBeNull();
        // 離線沿用的身分也要作廢，否則斷網後又「登入」回來（auth.js offlineUser）
        expect(localStorage.getItem('mobileUserOffline')).toBeNull();
        expect(fakeWindow.location.href).toBe('/mobile/index.html');
    });

    test('登入端點的 401＝帳密或驗證碼錯誤：不得清 session、不得導頁', async () => {
        // 導頁的話使用者看不到任何錯誤訊息，只會看到畫面跳回帳密輸入——
        // 從外面看就是「登入沒反應」。
        const { api, fakeWindow, fakeSession, toasts } = loadApi();
        fakeSession.setItem('mobileUser', 'x');
        mockFetch(jsonRes(401, { message: '帳號或密碼錯誤' }));

        await api.apiCall('POST', '/api/mobile/auth/login', { username: 'a', password: 'b' });

        expect(fakeSession.getItem('mobileUser')).toBe('x');
        expect(fakeWindow.location.href).toBe('');
        expect(toasts[0].msg).toBe('帳號或密碼錯誤');
    });
});

describe('請求本身', () => {
    test('一律帶 cookie，有 body 才序列化', async () => {
        const { api } = loadApi();
        mockFetch(jsonRes(200, {}));

        await api.apiCall('POST', '/api/x', { a: 1 });
        expect(global.fetch.mock.calls[0][1]).toMatchObject({
            method: 'POST',
            credentials: 'include',
            body: JSON.stringify({ a: 1 }),
        });

        await api.apiCall('GET', '/api/x');
        expect(global.fetch.mock.calls[1][1].body).toBeUndefined();
    });
});

describe('UOM 小數位數', () => {
    test('字典只查一次——每頁重複查會在弱網下拖慢每一次數量顯示', async () => {
        const { api } = loadApi();
        mockFetch(jsonRes(200, { PCS: 0 }));

        await api.getUomDecimalsMap();
        await api.getUomDecimalsMap();

        expect(global.fetch).toHaveBeenCalledTimes(1);
    });

    test('依 UOM 取小數位，查無則退回 4 位', () => {
        const { api } = loadApi();
        const map = { PCS: 0, KG: 2 };
        expect(api.formatQty(1234.5678, 'PCS', map)).toBe('1,235');
        expect(api.formatQty(1234.5678, 'KG', map)).toBe('1,234.57');
        expect(api.formatQty(1234.5678, 'UNKNOWN', map)).toBe('1,234.5678');
    });

    test('null 數量視為 0——畫面不該出現 NaN', () => {
        const { api } = loadApi();
        expect(api.formatQty(null, 'PCS', { PCS: 0 })).toBe('0');
    });
});

describe('setLoading', () => {
    test('還原時用回原本的 HTML，不會把按鈕留在 spinner 狀態', () => {
        const { api } = loadApi();
        document.body.insertAdjacentHTML('beforeend', '<button id="b">送出</button>');
        const btn = document.getElementById('b');

        api.setLoading(btn, true);
        expect(btn.disabled).toBe(true);
        expect(btn.innerHTML).toContain('spinner-border');

        api.setLoading(btn, false);
        expect(btn.disabled).toBe(false);
        expect(btn.innerHTML).toBe('送出');
    });
});

describe('apiGet：讀取一律要連線（斷線保護，v30 拿掉「上次那一份」）', () => {
    afterEach(() => { localStorage.clear(); });

    function signedIn(id) {
        const ctx = loadApi();
        ctx.fakeSession.setItem('mobileUser', JSON.stringify({ id }));
        return ctx;
    }

    test('連得上就回伺服器的結果，而且不在手機上另存一份', async () => {
        const { api } = signedIn('u1');
        mockFetch(jsonRes(200, { modules: [{ moduleKey: 'Mfg' }] }));

        await expect(api.apiGet('/api/mobile/modules')).resolves.toEqual({ modules: [{ moduleKey: 'Mfg' }] });
        expect(Object.keys(localStorage)).toEqual([]);
    });

    test('連不上伺服器：不拿任何舊資料頂替，提示「需要連線」並回 null', async () => {
        // 舊版存下的那一份就算還在，也不得再被讀出來
        localStorage.setItem('mobileOfflineGet:u1:/api/mobile/modules', JSON.stringify({ data: { stale: true }, savedAt: 0 }));
        const { api, toasts } = signedIn('u1');
        global.fetch = jest.fn(() => Promise.reject(new TypeError('Failed to fetch')));

        await expect(api.apiGet('/api/mobile/modules')).resolves.toBeNull();
        expect(toasts).toEqual([{ msg: 'common.need_connection', type: 'danger' }]);
    });

    test('一頁好幾個查詢都連不上，「需要連線」只提示一次', async () => {
        const { api, toasts } = signedIn('u1');
        global.fetch = jest.fn(() => Promise.reject(new TypeError('Failed to fetch')));

        await api.apiGet('/api/a');
        await api.apiGet('/api/b');
        await api.apiGet('/api/c');

        expect(toasts.filter(t => t.msg === 'common.need_connection')).toHaveLength(1);
    });

    test.each([502, 503, 504])('經 Tunnel 時伺服器停掉回 %i：視同連不上，提示「需要連線」', async (status) => {
        const { api, toasts } = signedIn('u1');
        mockFetch(jsonRes(status, {}));

        await expect(api.apiGet('/api/mobile/modules')).resolves.toBeNull();
        expect(toasts).toEqual([{ msg: 'common.need_connection', type: 'danger' }]);
    });

    test('一般呼叫（沒要求分辨連不上）遇到 502 照舊顯示操作失敗', async () => {
        const { api, toasts } = signedIn('u1');
        mockFetch(jsonRes(502, {}));

        await expect(api.apiCall('POST', '/api/x', {})).resolves.toBeNull();
        expect(toasts).toEqual([{ msg: 'common.op_failed:502', type: 'danger' }]);
    });

    test('伺服器有回應但被拒（403）：照 apiCall 的規則提示無權限，不說成「需要連線」', async () => {
        const { api, toasts } = signedIn('u1');
        mockFetch(jsonRes(403, {}));

        await expect(api.apiGet('/api/mobile/modules/Mfg/actions')).resolves.toBeNull();
        expect(toasts).toEqual([{ msg: 'common.forbidden', type: 'danger' }]);
    });

    test('載入時清掉舊版留在手機上的「上次那一份」——裡面是別人看不到的清單內容', () => {
        localStorage.setItem('mobileOfflineGet:u1:/api/a', '{}');
        localStorage.setItem('mobileOfflineGet:u2:/api/b', '{}');
        localStorage.setItem('mobileLang', 'en-US');

        loadApi();

        expect(Object.keys(localStorage).filter(k => k.startsWith('mobileOfflineGet:'))).toEqual([]);
        expect(localStorage.getItem('mobileLang')).toBe('en-US');
    });

    test('clearOfflineUserData 清掉斷線沿用的身分，其他偏好不動', () => {
        const { api } = signedIn('u1');
        localStorage.setItem('mobileUserOffline', '{}');
        localStorage.setItem('mobileLang', 'en-US');

        api.clearOfflineUserData();

        expect(localStorage.getItem('mobileUserOffline')).toBeNull();
        expect(localStorage.getItem('mobileLang')).toBe('en-US');
    });
});

test('business writes carry the page user plant snapshot', async () => {
    const { api, fakeSession } = loadApi();
    fakeSession.setItem('mobileUser', JSON.stringify({ id: 'review', plantId: 'A' }));
    mockFetch(jsonRes(200, { success: true }));
    await api.apiCall('POST', '/api/mobile/reports', { qty: 1 });
    expect(global.fetch.mock.calls[0][1].headers['X-Plant-Context']).toBe('A');
});
