/**
 * auth.js — 手機版登入狀態管理
 */

// 登入頁：執行登入
async function login() {
    const username = document.getElementById('username')?.value?.trim();
    const password = document.getElementById('password')?.value;
    const btn = document.querySelector('button[data-action="login"], button[onclick="login()"]');
    const errorEl = document.getElementById('error');

    if (!username || !password) {
        if (errorEl) { errorEl.textContent = t('login.err_empty'); errorEl.classList.remove('d-none'); }
        return;
    }

    setLoading(btn, true);
    if (errorEl) errorEl.classList.add('d-none');

    const data = await apiCall('POST', '/api/mobile/auth/login', { username, password });

    setLoading(btn, false);

    if (!data) return; // apiCall 已處理錯誤（含 Toast）

    if (data.success) {
        await completeLogin(data.user);
    } else if (data.requiresTwoFactor) {
        showTwoFactorStep();
    } else if (errorEl) {
        errorEl.textContent = data.message || t('login.err_failed');
        errorEl.classList.remove('d-none');
    }
}

// 兩階段驗證（2FA）：帳密正確後輸入驗證器 App 的 6 位數驗證碼
async function verifyTwoFactor() {
    const code = document.getElementById('twoFactorCode')?.value?.trim();
    const btn = document.querySelector('button[data-action="verifyTwoFactor"]');
    const errorEl = document.getElementById('error');

    if (!code) {
        if (errorEl) { errorEl.textContent = t('login.err_code'); errorEl.classList.remove('d-none'); }
        return;
    }

    setLoading(btn, true);
    if (errorEl) errorEl.classList.add('d-none');

    const data = await apiCall('POST', '/api/mobile/auth/login-2fa', { code });

    setLoading(btn, false);

    if (!data) return; // apiCall 已處理錯誤（含 Toast）

    if (data.success) {
        await completeLogin(data.user);
    } else if (errorEl) {
        errorEl.textContent = data.message || t('login.err_verify');
        errorEl.classList.remove('d-none');
    }
}

// 兩階段驗證：放棄輸入驗證碼，返回帳密輸入畫面重新登入
function cancelTwoFactor() {
    const codeEl = document.getElementById('twoFactorCode');
    if (codeEl) codeEl.value = '';
    document.getElementById('step-2fa')?.classList.add('d-none');
    document.getElementById('step-credentials')?.classList.remove('d-none');
    document.getElementById('error')?.classList.add('d-none');
}

function showTwoFactorStep() {
    document.getElementById('step-credentials')?.classList.add('d-none');
    document.getElementById('step-2fa')?.classList.remove('d-none');
    document.getElementById('error')?.classList.add('d-none');
    document.getElementById('twoFactorCode')?.focus();
}

async function completeLogin(user) {
    const { id, name, displayName, activeRoleId, plantId, language, roles } = user;
    // 前一個人可能沒登出就離開（session 逾時），他的派工清單還在 SW 快取裡。
    await clearUserDataCaches();
    rememberUser({ id, name, displayName, activeRoleId, plantId, roles });
    // 依使用者管理設定的語系初始化手機介面語言
    localStorage.setItem('mobileLang', language || 'zh-TW');
    window.location.href = '/mobile/home.html';
}

// ── 斷線時沿用的身分快取 ─────────────────────────────────────────────────────
// sessionStorage 在 PWA 冷啟動（從主畫面圖示開）時是空的，所以斷線時要靠
// localStorage 這一份才認得人。v30 起讀取一律要連線（斷線保護，mobile-pwa.md §5.2），
// 這份身分只剩一個用途：斷線中重開 App，佇列仍知道「現在是誰」，恢復連線後
// 只送本人排的項目（offline-queue.js 的排入者比對）——沒有它，比對兩邊有一邊是 null 就放行。
//
// ⚠️ 沿用上限對齊伺服器的 session 長度（Program.cs：Security:SessionHours，預設 8 小時滑動）。
//    離線時不能比線上寬鬆：線上超過這個時間就得重新登入，離線也一樣。
//    伺服器端若調短 SessionHours，這裡要跟著改（手機端讀不到那個設定值）。
const OFFLINE_USER_KEY    = 'mobileUserOffline';
const OFFLINE_USER_MAX_MS = 8 * 60 * 60 * 1000;

function rememberUser(safeUser) {
    sessionStorage.setItem('mobileUser', JSON.stringify(safeUser));
    try {
        localStorage.setItem(OFFLINE_USER_KEY, JSON.stringify({ user: safeUser, verifiedAt: Date.now() }));
    } catch { /* 私密瀏覽等情況寫不進去：只是失去離線沿用，不影響線上 */ }
}

function forgetUser() {
    sessionStorage.clear();
    clearOfflineUserData();   // api.js：離線身分＋各頁「上次那一份」
}

// ── 換人時要清掉的裝置端資料 ─────────────────────────────────────────────────
// 共用手機上，登出只清 localStorage 是不夠的（2026-09-29 安全盤點）：
//  - sw.js 的派工（5 分鐘）與機台（30 分鐘）快取**鍵只有網址**，不分使用者——
//    下一個人登入後看到的是前一個人的派工清單，離線時更是一直沿用。
//  - IndexedDB 裡前一個人的報工草稿與已送出紀錄，開發者工具打得開就讀得到。
//
// 只清「資料」快取，不清殼層（CACHE_NAME）：預快取只在 SW 安裝時做一次，
// 清掉之後沒開過的頁面就不能離線開，要等下次 bump 才會補回來。
// 用前綴比對而不是抄 sw.js 的完整名稱：版本號一改，抄的那份就對不上了。
//
// 待同步（pending／held／error）的項目**不清**：那是還沒進伺服器的報工，清掉就是資料遺失。
// offline-queue.js 已經擋掉「排入者 ≠ 送出者」，會留到原排入者登入後再送。
const USER_DATA_CACHE_RE = /^mes-(dispatch|master)-/;
const OQ_DB             = 'mes-offline-queue';

async function clearUserDataCaches() {
    try {
        if (typeof caches === 'undefined') return;   // 非安全來源（http 區網）沒有 Cache API
        const names = await caches.keys();
        await Promise.all(names.filter(n => USER_DATA_CACHE_RE.test(n)).map(n => caches.delete(n)));
    } catch { /* 清不掉只是退回原本的行為，不擋登入登出 */ }
}

/**
 * 連線中就先同步一次，再數目前登入者自己還沒送出的項目（pending＋error）。
 * 直接讀 IndexedDB 而不是 window.offlineQueue：登出鈕在好幾頁上，不是每一頁都載了 offline-queue.js。
 * 歸屬規則與 offline-queue.js 相同（userId 為 null 的舊項目算自己的）。讀不到一律回 0，不擋登出。
 */
async function flushAndCountMyUnsent() {
    try {
        if (window.offlineQueue && typeof navigator !== 'undefined' && navigator.onLine)
            await window.offlineQueue.flush();
    } catch { /* 同步失敗就照實數剩下的 */ }

    let me = null;
    try { me = JSON.parse(sessionStorage.getItem('mobileUser') || '{}').id || null; } catch { }

    return new Promise(resolve => {
        try {
            if (typeof indexedDB === 'undefined') return resolve(0);
            const req = indexedDB.open(OQ_DB);
            req.onerror   = () => resolve(0);
            req.onsuccess = e => {
                const db = e.target.result;
                try {
                    if (!db.objectStoreNames.contains('offlineQueue')) { db.close(); return resolve(0); }
                    const all = db.transaction(['offlineQueue'], 'readonly').objectStore('offlineQueue').getAll();
                    all.onsuccess = ev => {
                        const n = (ev.target.result || []).filter(r =>
                            (r.status === 'pending' || r.status === 'error' || r.status === 'held')
                            && (!r.userId || !me || r.userId === me)).length;
                        db.close();
                        resolve(n);
                    };
                    all.onerror = () => { db.close(); resolve(0); };
                } catch { db.close(); resolve(0); }
            };
        } catch { resolve(0); }
    });
}

/** 刪掉報工草稿與已送出的佇列紀錄；待同步的保留（理由見上）。 */
function clearSignedOutQueueData() {
    return new Promise(resolve => {
        try {
            if (typeof indexedDB === 'undefined') return resolve();
            const req = indexedDB.open(OQ_DB);
            req.onerror   = () => resolve();
            req.onsuccess = e => {
                const db = e.target.result;
                try {
                    const names = ['inspectionDrafts', 'offlineQueue'].filter(n => db.objectStoreNames.contains(n));
                    if (!names.length) { db.close(); return resolve(); }
                    const tx = db.transaction(names, 'readwrite');
                    if (names.includes('inspectionDrafts')) tx.objectStore('inspectionDrafts').clear();
                    if (names.includes('offlineQueue')) {
                        const store = tx.objectStore('offlineQueue');
                        store.getAll().onsuccess = ev => (ev.target.result || [])
                            .filter(r => r.status === 'sent')
                            .forEach(r => store.delete(r.clientId));
                    }
                    tx.oncomplete = tx.onerror = tx.onabort = () => { db.close(); resolve(); };
                } catch { db.close(); resolve(); }
            };
        } catch { resolve(); }
    });
}

/** 最近一次線上確認過、且仍在 session 長度內的身分；沒有就回 null。 */
function offlineUser() {
    try {
        const cached = JSON.parse(localStorage.getItem(OFFLINE_USER_KEY) || 'null');
        if (!cached?.user?.id || !(Date.now() - cached.verifiedAt < OFFLINE_USER_MAX_MS)) return null;
        return cached.user;
    } catch {
        return null;
    }
}

// 非登入頁：確認已登入，否則導回登入頁。
// 離線（或連不上伺服器）時改用最近一次線上確認過的身分，讓頁面照常初始化；
// 真正的授權仍在伺服器——離線佇列送出時帶的是當下的 Cookie，而且 offline-queue.js
// 會擋掉「排入的人 ≠ 送出時登入的人」的項目。
async function requireAuth() {
    let res;
    try {
        res = await fetch('/api/mobile/auth/me', { credentials: 'include' });
    } catch {
        res = null;   // 網路層失敗＝離線或連不上，跟 401 是兩回事
    }

    // 不是 apiCall：apiCall 把「連不上」與「未登入」都變成 null，這裡必須分得開——
    // 前者要沿用快取，後者絕對不行（那等於登出後離線還能用）。
    if (!res || res.status >= 500) {
        const cached = offlineUser();
        if (cached) {
            sessionStorage.setItem('mobileUser', JSON.stringify(cached));
            return cached;
        }
        showToast(t('common.net_error'), 'danger');
        return null;
    }

    if (res.status === 401) {
        forgetUser();
        window.location.href = '/mobile/index.html';
        return null;
    }
    if (!res.ok) {
        showToast(tf('common.op_failed', res.status), 'danger');
        return null;
    }

    const { id, name, displayName, activeRoleId, plantId, roles } = await res.json();
    const safeUser = { id, name, displayName, activeRoleId, plantId, roles };
    rememberUser(safeUser);
    return safeUser;
}

// 登出
async function logout() {
    // 還沒送出的資料登出後會留在這台裝置上（換人登入不代送，見 offline-queue.js），
    // 要等本人再登入才會送。能送就先送；送不掉的讓本人知道後再決定要不要登出——
    // 原本登出一律直接走，交班時這些報工就靜靜留在共用手機裡。
    const unsent = await flushAndCountMyUnsent();
    if (unsent > 0 && typeof window.confirm === 'function'
        && !window.confirm(tf('auth.logout_unsent', unsent))) return;

    await apiCall('POST', '/api/mobile/auth/logout');
    forgetUser();   // 連離線身分一起清：登出後離線還認得人，等於沒登出
    await Promise.all([clearUserDataCaches(), clearSignedOutQueueData()]);
    localStorage.removeItem('mobileLang'); // 清除語系偏好，讓下次登入重新從帳號設定套用
    window.location.href = '/mobile/index.html';
}

// Enter 鍵觸發登入
document.addEventListener('DOMContentLoaded', () => {
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        const activeEl = document.activeElement;
        if ((activeEl?.id === 'username' || activeEl?.id === 'password') && typeof login === 'function') {
            login();
        } else if (activeEl?.id === 'twoFactorCode' && typeof verifyTwoFactor === 'function') {
            verifyTwoFactor();
        }
    });
});
