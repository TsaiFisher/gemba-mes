'use strict';

/**
 * auth.js 單元測試（manual-test-plan §12.4「手機其餘 JS 零覆蓋」）。
 *
 * 登入流程的失效方式幾乎都是「畫面沒反應」而不是錯誤訊息：
 * 兩階段驗證的分支漏掉，使用者只會看到帳密輸入框再次出現；
 * completeLogin 少存一個欄位，後面每一頁的權限判斷都會靜默退化。
 * 這些人工測試抽樣看不出來——看起來就只是「又要再登入一次」。
 *
 * 注入假 window／storage 的理由與 api.test.js 相同（jsdom 的 location 改不掉）。
 */

const fs   = require('fs');
const path = require('path');

const scriptSrc = fs.readFileSync(path.resolve(__dirname, '..', 'auth.js'), 'utf-8');

function fakeStorage() {
    const store = new Map();
    return {
        getItem: (k) => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, String(v)),
        removeItem: (k) => store.delete(k),
        clear: () => store.clear(),
    };
}

/** apiCall 的替身：依序回傳排好的回應，並記錄呼叫。
 *  requireAuth 不走 apiCall（要分得出「連不上」與「未登入」），它的 /me 由 meFetch 決定。 */
function loadAuth(responses = [], { meFetch, caches, indexedDB, windowExtras = {} } = {}) {
    const calls = [];
    const apiCall = jest.fn((method, url, body) => {
        calls.push({ method, url, body });
        return Promise.resolve(responses.length ? responses.shift() : {});
    });

    const fakeWindow = { location: { href: '' }, ...windowExtras };
    const session = fakeStorage();
    const local   = fakeStorage();

    const fetch = jest.fn(meFetch || (() => Promise.reject(new TypeError('fetch 未設定'))));
    const showToast = jest.fn();
    // api.js 的清理函式（本身有 api.test.js 守）；這裡只驗 auth.js 在對的時機呼叫它。
    const clearOfflineUserData = jest.fn(() => local.removeItem('mobileUserOffline'));

    const factory = new Function(
        'window', 'document', 'sessionStorage', 'localStorage', 'apiCall', 'setLoading', 't',
        'fetch', 'showToast', 'tf', 'clearOfflineUserData', 'caches', 'indexedDB',
        `${scriptSrc}
         return { login, verifyTwoFactor, cancelTwoFactor, showTwoFactorStep, completeLogin,
                  requireAuth, logout };`
    );
    const auth = factory(fakeWindow, document, session, local, apiCall, () => {}, (k) => k,
                         fetch, showToast, (k) => k, clearOfflineUserData, caches, indexedDB);
    return { auth, apiCall, calls, fakeWindow, session, local, fetch, showToast, clearOfflineUserData };
}

function loginPageDom() {
    document.body.innerHTML = `
        <div id="step-credentials">
            <input id="username" /><input id="password" type="password" />
            <button data-action="login">登入</button>
        </div>
        <div id="step-2fa" class="d-none">
            <input id="twoFactorCode" />
            <button data-action="verifyTwoFactor">驗證</button>
        </div>
        <div id="error" class="d-none"></div>`;
}

beforeEach(() => loginPageDom());

const err = () => document.getElementById('error');

describe('login', () => {
    test('帳號或密碼留白就地顯示錯誤，不打後端', async () => {
        const { auth, apiCall } = loadAuth();
        document.getElementById('username').value = '';

        await auth.login();

        expect(apiCall).not.toHaveBeenCalled();
        expect(err().classList.contains('d-none')).toBe(false);
        expect(err().textContent).toBe('login.err_empty');
    });

    test('帳號前後空白要修掉——現場用掃描槍刷帳號常帶尾隨空白', async () => {
        const { auth, calls } = loadAuth([{ success: true, user: { id: 'u1' } }]);
        document.getElementById('username').value = '  worker01  ';
        document.getElementById('password').value = 'pw';

        await auth.login();

        expect(calls[0].body.username).toBe('worker01');
    });

    test('成功登入寫入 session 並導向首頁', async () => {
        const { auth, fakeWindow, session, local } = loadAuth([{
            success: true,
            user: { id: 'u1', name: 'w1', displayName: '作業員', activeRoleId: 'OP',
                    plantId: 'P1', language: 'en-US', roles: ['OP'] },
        }]);
        document.getElementById('username').value = 'w1';
        document.getElementById('password').value = 'pw';

        await auth.login();

        expect(JSON.parse(session.getItem('mobileUser'))).toEqual({
            id: 'u1', name: 'w1', displayName: '作業員', activeRoleId: 'OP', plantId: 'P1', roles: ['OP'],
        });
        expect(local.getItem('mobileLang')).toBe('en-US');
        expect(fakeWindow.location.href).toBe('/mobile/home.html');
    });

    test('後端說要 2FA 時切到驗證碼步驟，而不是停在原畫面', async () => {
        // 少了這個分支，使用者按下登入之後畫面完全沒變——看起來就是「按了沒反應」。
        const { auth, fakeWindow } = loadAuth([{ success: false, requiresTwoFactor: true }]);
        document.getElementById('username').value = 'w1';
        document.getElementById('password').value = 'pw';

        await auth.login();

        expect(document.getElementById('step-credentials').classList.contains('d-none')).toBe(true);
        expect(document.getElementById('step-2fa').classList.contains('d-none')).toBe(false);
        expect(fakeWindow.location.href).toBe('');   // 尚未登入，不得導頁
    });

    test('帳密錯誤顯示後端訊息', async () => {
        const { auth } = loadAuth([{ success: false, message: '帳號或密碼錯誤' }]);
        document.getElementById('username').value = 'w1';
        document.getElementById('password').value = 'bad';

        await auth.login();

        expect(err().textContent).toBe('帳號或密碼錯誤');
        expect(err().classList.contains('d-none')).toBe(false);
    });

    test('apiCall 回 null（已自行提示）時不再重複顯示錯誤', async () => {
        const { auth } = loadAuth([null]);
        document.getElementById('username').value = 'w1';
        document.getElementById('password').value = 'pw';

        await auth.login();

        expect(err().classList.contains('d-none')).toBe(true);
    });
});

describe('兩階段驗證', () => {
    test('驗證碼留白就地提示，不打後端', async () => {
        const { auth, apiCall } = loadAuth();
        await auth.verifyTwoFactor();
        expect(apiCall).not.toHaveBeenCalled();
        expect(err().textContent).toBe('login.err_code');
    });

    test('驗證成功走同一條 completeLogin', async () => {
        const { auth, session, fakeWindow } = loadAuth([{ success: true, user: { id: 'u9' } }]);
        document.getElementById('twoFactorCode').value = '123456';

        await auth.verifyTwoFactor();

        expect(JSON.parse(session.getItem('mobileUser')).id).toBe('u9');
        expect(fakeWindow.location.href).toBe('/mobile/home.html');
    });

    test('取消 2FA 要清掉已輸入的驗證碼——留著會在下一次登入被誤送', async () => {
        const { auth } = loadAuth();
        document.getElementById('twoFactorCode').value = '999999';
        auth.showTwoFactorStep();

        auth.cancelTwoFactor();

        expect(document.getElementById('twoFactorCode').value).toBe('');
        expect(document.getElementById('step-credentials').classList.contains('d-none')).toBe(false);
        expect(document.getElementById('step-2fa').classList.contains('d-none')).toBe(true);
    });
});

/** /api/mobile/auth/me 的回應替身 */
const meOk     = (body) => () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });
const meStatus = (status) => () => Promise.resolve({ ok: false, status, json: () => Promise.resolve({}) });
const offline  = () => Promise.reject(new TypeError('Failed to fetch'));

const USER = { id: 'u1', name: 'w1', displayName: 'W', activeRoleId: 'OP', plantId: 'P1', roles: ['OP'] };
const HOUR = 60 * 60 * 1000;

function seedOffline(local, user, ageMs) {
    local.setItem('mobileUserOffline', JSON.stringify({ user, verifiedAt: Date.now() - ageMs }));
}

describe('requireAuth / logout', () => {
    test('requireAuth 只留白名單欄位進 session', async () => {
        const { auth, session, local } = loadAuth([], { meFetch: meOk({ ...USER, passwordHash: '不該被存下來' }) });

        const user = await auth.requireAuth();

        expect(user.id).toBe('u1');
        expect(JSON.parse(session.getItem('mobileUser'))).not.toHaveProperty('passwordHash');
        // 離線沿用的那一份同樣只能有白名單欄位
        expect(JSON.parse(local.getItem('mobileUserOffline')).user).not.toHaveProperty('passwordHash');
    });

    test('401 清掉 session 與離線身分並導回登入頁', async () => {
        const { auth, local, fakeWindow } = loadAuth([], { meFetch: meStatus(401) });
        seedOffline(local, USER, HOUR);

        await expect(auth.requireAuth()).resolves.toBeNull();

        expect(fakeWindow.location.href).toBe('/mobile/index.html');
        // 伺服器說 session 已失效——離線身分若還留著，斷網後就又「登入」回來了
        expect(local.getItem('mobileUserOffline')).toBeNull();
    });

    test('離線冷啟動：沿用最近一次線上確認過的身分，頁面得以初始化', async () => {
        const { auth, session, local, fakeWindow } = loadAuth([], { meFetch: offline });
        seedOffline(local, USER, 2 * HOUR);   // sessionStorage 是空的＝從主畫面圖示冷啟動

        const user = await auth.requireAuth();

        expect(user).toEqual(USER);
        // offline-queue.js 從 sessionStorage 讀廠別與排入者，冷啟動時必須補上
        expect(JSON.parse(session.getItem('mobileUser'))).toEqual(USER);
        expect(fakeWindow.location.href).toBe('');
    });

    test('伺服器 5xx 視同連不上，同樣沿用', async () => {
        const { auth, local } = loadAuth([], { meFetch: meStatus(503) });
        seedOffline(local, USER, HOUR);
        await expect(auth.requireAuth()).resolves.toEqual(USER);
    });

    test('離線身分超過 session 長度（8 小時）就不沿用——離線不能比線上寬鬆', async () => {
        const { auth, local, showToast } = loadAuth([], { meFetch: offline });
        seedOffline(local, USER, 8 * HOUR + 1000);

        await expect(auth.requireAuth()).resolves.toBeNull();
        expect(showToast).toHaveBeenCalledWith('common.net_error', 'danger');
    });

    test('離線且從沒登入過：回 null，不導頁（離線時登入頁也開不起來）', async () => {
        const { auth, fakeWindow } = loadAuth([], { meFetch: offline });
        await expect(auth.requireAuth()).resolves.toBeNull();
        expect(fakeWindow.location.href).toBe('');
    });

    test('登出清空 session、語系偏好與離線身分', async () => {
        const { auth, session, local, fakeWindow } = loadAuth([{}]);
        session.setItem('mobileUser', '{}');
        local.setItem('mobileLang', 'en-US');
        seedOffline(local, USER, HOUR);

        await auth.logout();

        expect(session.getItem('mobileUser')).toBeNull();
        expect(local.getItem('mobileLang')).toBeNull();
        // 登出後斷網還認得人，等於沒登出
        expect(local.getItem('mobileUserOffline')).toBeNull();
        expect(fakeWindow.location.href).toBe('/mobile/index.html');
    });
});

// ── 換人時的裝置端資料（2026-09-29 安全盤點）────────────────────────────────
// sw.js 的派工／機台快取鍵只有網址、不分使用者；登出原本只清 localStorage，
// 共用手機上下一個人看到的是前一個人的派工清單，IndexedDB 裡的草稿也還在。

/** Cache API 替身：只記名稱。 */
function fakeCaches(names) {
    const set = new Set(names);
    return {
        keys: () => Promise.resolve([...set]),
        delete: (n) => Promise.resolve(set.delete(n)),
        names: () => [...set].sort(),
    };
}

/** 只實作 auth.js 用到的 IndexedDB：open（不帶版本）、多 store 交易、clear、getAll、delete。 */
function fakeQueueDb({ drafts = [], queue = [] }) {
    const stores = {
        inspectionDrafts: new Map(drafts.map(d => [d.draftKey, d])),
        offlineQueue:     new Map(queue.map(q => [q.clientId, q])),
    };
    const later = (fn) => setTimeout(fn, 0);
    const db = {
        objectStoreNames: { contains: (n) => n in stores },
        close: jest.fn(),
        transaction() {
            let pending = 0;
            const tx = {};
            const settle = () => { if (pending === 0) later(() => tx.oncomplete?.()); };
            tx.objectStore = (name) => ({
                clear: () => { stores[name].clear(); return {}; },
                delete: (k) => { stores[name].delete(k); return {}; },
                getAll: () => {
                    const req = {};
                    pending++;
                    later(() => {
                        req.onsuccess?.({ target: { result: [...stores[name].values()] } });
                        pending--; settle();
                    });
                    return req;
                },
            });
            later(settle);
            return tx;
        },
    };
    return {
        indexedDB: { open: () => { const req = {}; later(() => req.onsuccess?.({ target: { result: db } })); return req; } },
        stores,
        db,
    };
}

describe('換人時清掉裝置端資料', () => {
    const SHELL = 'mes-mobile-v21';
    const allCaches = () => fakeCaches([SHELL, 'mes-dispatch-v1', 'mes-master-v1']);

    test('登出清掉派工與機台快取，殼層預快取保留（清掉的話沒開過的頁面就不能離線開）', async () => {
        const caches = allCaches();
        const { auth } = loadAuth([{}], { caches });

        await auth.logout();

        expect(caches.names()).toEqual([SHELL]);
    });

    test('登出清掉草稿與已送出紀錄，待同步與失敗的保留（那是還沒進伺服器的報工）', async () => {
        const idb = fakeQueueDb({
            drafts: [{ draftKey: 'u1:D1', formData: {} }],
            queue: [
                { clientId: 'a', status: 'sent' },
                { clientId: 'b', status: 'pending' },
                { clientId: 'c', status: 'error' },
            ],
        });
        const { auth } = loadAuth([{}], { indexedDB: idb.indexedDB });

        await auth.logout();

        expect(idb.stores.inspectionDrafts.size).toBe(0);
        expect([...idb.stores.offlineQueue.keys()].sort()).toEqual(['b', 'c']);
        expect(idb.db.close).toHaveBeenCalled();
    });

    test('登入時也清派工快取——前一個人可能沒登出、只是 session 逾時', async () => {
        const caches = allCaches();
        const { auth, fakeWindow } = loadAuth([], { caches });

        await auth.completeLogin({ id: 'u2', name: 'w2' });

        expect(caches.names()).toEqual([SHELL]);
        expect(fakeWindow.location.href).toBe('/mobile/home.html');
    });

    test('沒有 Cache API（http 區網）與沒有 IndexedDB 時照常登出', async () => {
        const { auth, fakeWindow } = loadAuth([{}]);
        await auth.logout();
        expect(fakeWindow.location.href).toBe('/mobile/index.html');
    });
});

describe('登出前處理還沒送出的資料', () => {
    const mine   = (id, status = 'pending') => ({ clientId: id, status, userId: 'u1' });
    const others = (id) => ({ clientId: id, status: 'pending', userId: 'u2' });

    function setup(queue, windowExtras = {}) {
        const idb = fakeQueueDb({ queue });
        const loaded = loadAuth([{}], { indexedDB: idb.indexedDB, windowExtras });
        loaded.session.setItem('mobileUser', JSON.stringify({ id: 'u1' }));
        return { ...loaded, idb };
    }

    test('本人還有未同步的資料：先問，按取消就不登出', async () => {
        const confirm = jest.fn(() => false);
        const { auth, apiCall, session, fakeWindow } = setup([mine('a'), mine('b', 'error')], { confirm });

        await auth.logout();

        expect(confirm).toHaveBeenCalledWith('auth.logout_unsent');
        expect(apiCall).not.toHaveBeenCalled();
        expect(session.getItem('mobileUser')).not.toBeNull();
        expect(fakeWindow.location.href).toBe('');
    });

    test('按確定照常登出，未同步的資料留著（那是還沒進伺服器的報工）', async () => {
        const { auth, apiCall, idb, fakeWindow } = setup([mine('a')], { confirm: () => true });

        await auth.logout();

        expect(apiCall).toHaveBeenCalledWith('POST', '/api/mobile/auth/logout');
        expect([...idb.stores.offlineQueue.keys()]).toEqual(['a']);
        expect(fakeWindow.location.href).toBe('/mobile/index.html');
    });

    test('只有別人的未同步資料：不算在本人頭上，不問', async () => {
        const confirm = jest.fn(() => false);
        const { auth, fakeWindow } = setup([others('x'), mine('s', 'sent')], { confirm });

        await auth.logout();

        expect(confirm).not.toHaveBeenCalled();
        expect(fakeWindow.location.href).toBe('/mobile/index.html');
    });

    test('連線中先同步一次，送得掉就不必問', async () => {
        const confirm = jest.fn(() => false);
        let idbRef;
        const offlineQueue = {
            flush: jest.fn(async () => { idbRef.stores.offlineQueue.get('a').status = 'sent'; }),
        };
        const loaded = setup([mine('a')], { confirm, offlineQueue });
        idbRef = loaded.idb;

        await loaded.auth.logout();

        expect(offlineQueue.flush).toHaveBeenCalled();
        expect(confirm).not.toHaveBeenCalled();
        expect(loaded.fakeWindow.location.href).toBe('/mobile/index.html');
    });
});
