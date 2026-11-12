/**
 * mobile/js/i18n.js — 手機 PWA 多語支援（Core：引擎＋登入／首頁／共用字串）
 *
 * 語言偏好儲存於 localStorage.mobileLang（zh-TW 或 en-US）。
 * setLang() 已登入時寫回帳號偏好（/api/mobile/auth/language），未登入時只同步桌機語系 cookie。
 * 頁面用 data-i18n="key" 標記可翻譯文字（含 <title>）；applyI18n() 在 DOMContentLoaded 後呼叫。
 * 部署專案的模組字串放在自己的檔案，以 registerI18n() 併入——本檔與 Core 保持一致。
 */

// ── 字典 ──────────────────────────────────────────────────────────────────────
const STRINGS = {
    'zh-TW': {
        // ── auth ──
        'auth.logout_unsent': '還有 {0} 筆資料尚未同步。登出後會留在這台裝置上，要等你再次登入才會送出。確定要登出嗎？',
        // ── common ──
        'common.back': '返回首頁',
        'common.back_prev': '返回',
        'common.cancel': '取消',
        'common.clear': '清除',
        'common.close': '關閉',
        'common.confirm': '確認',
        'common.forbidden': '權限不足，無法執行此操作',
        'common.lang_switch': '切換語言',
        'common.lang_toggle': 'EN',
        'common.loading': '載入中…',
        'common.logout': '登出',
        'common.net_error': '網路錯誤，請確認連線後重試',
        'common.no_data': '無資料',
        'common.ok': '確定',
        'common.op_failed': '操作失敗（{0}）',
        'common.processing': '處理中…',
        'common.refresh': '重新整理',
        'common.retry': '重試',
        'common.save': '儲存',
        'common.scan': '掃描',
        'common.search': '查詢',
        'common.submit': '送出',
        // ── home ──
        'home.no_perm': '目前帳號尚未設定手機模組權限',
        'home.no_perm_hint': '請聯繫系統管理員',
        'home.page_title': 'SmartFactory MES — 首頁',
        'home.title': '請選擇作業功能',
        // ── login ──
        'login.2fa_back': '返回重新輸入帳密',
        'login.2fa_btn': '驗證',
        'login.2fa_code': '驗證碼',
        'login.2fa_code_ph': '請輸入驗證碼',
        'login.2fa_desc': '請輸入驗證器 App 顯示的 6 位數驗證碼',
        'login.btn': '登入',
        'login.err_code': '請輸入驗證碼',
        'login.err_empty': '請輸入帳號與密碼',
        'login.err_failed': '登入失敗',
        'login.err_verify': '驗證失敗',
        'login.page_title': 'SmartFactory MES — 登入',
        'login.password': '密碼',
        'login.password_ph': '請輸入密碼',
        'login.subtitle': '製造執行系統',
        'login.username': '帳號',
        'login.username_ph': '請輸入帳號',
    },
    'en-US': {
        // ── auth ──
        'auth.logout_unsent': '{0} item(s) have not been synced. They will stay on this device and be sent the next time you sign in. Sign out anyway?',
        // ── common ──
        'common.back': 'Home',
        'common.back_prev': 'Back',
        'common.cancel': 'Cancel',
        'common.clear': 'Clear',
        'common.close': 'Close',
        'common.confirm': 'Confirm',
        'common.forbidden': 'You do not have permission for this action.',
        'common.lang_switch': 'Switch Language',
        'common.lang_toggle': '中',
        'common.loading': 'Loading…',
        'common.logout': 'Logout',
        'common.net_error': 'Network error. Check the connection and try again.',
        'common.no_data': 'No data',
        'common.ok': 'OK',
        'common.op_failed': 'Operation failed ({0})',
        'common.processing': 'Processing…',
        'common.refresh': 'Refresh',
        'common.retry': 'Retry',
        'common.save': 'Save',
        'common.scan': 'Scan',
        'common.search': 'Search',
        'common.submit': 'Submit',
        // ── home ──
        'home.no_perm': 'No mobile module permissions assigned',
        'home.no_perm_hint': 'Please contact your system administrator',
        'home.page_title': 'SmartFactory MES — Home',
        'home.title': 'Select operation',
        // ── login ──
        'login.2fa_back': 'Back to login',
        'login.2fa_btn': 'Verify',
        'login.2fa_code': 'Verification Code',
        'login.2fa_code_ph': 'Enter verification code',
        'login.2fa_desc': 'Enter the 6-digit code from your authenticator app',
        'login.btn': 'Login',
        'login.err_code': 'Enter the verification code',
        'login.err_empty': 'Enter your username and password',
        'login.err_failed': 'Login failed',
        'login.err_verify': 'Verification failed',
        'login.page_title': 'SmartFactory MES — Sign in',
        'login.password': 'Password',
        'login.password_ph': 'Enter password',
        'login.subtitle': 'Manufacturing Execution System',
        'login.username': 'Username',
        'login.username_ph': 'Enter username',
    }
};

// ── 核心函式 ──────────────────────────────────────────────────────────────────

/**
 * 部署專案註冊自己的字串（例：各模組畫面）。在本檔之後載入、DOMContentLoaded 之前呼叫即可；
 * 同一個 key 後註冊者覆蓋。Core 只放登入／首頁／共用字串，模組字典不進本檔，兩邊才不會分岔。
 */
function registerI18n(dict) {
    for (const [lang, entries] of Object.entries(dict)) {
        STRINGS[lang] = Object.assign(STRINGS[lang] || {}, entries);
    }
}

/** 取得目前語言（zh-TW 或 en-US）。 */
function getLang() {
    return localStorage.getItem('mobileLang') || 'zh-TW';
}

/** 翻譯指定 key；找不到時回傳 key 本身（便於除錯）。 */
function t(key) {
    return lookup(key) ?? key;
}

/** 翻譯並代入參數：t 值中的 {0}、{1}… 依序換成 args。 */
function tf(key, ...args) {
    return t(key).replace(/\{(\d+)\}/g, (m, i) => (args[i] !== undefined ? args[i] : m));
}

/** 查字典；查無此 key 回傳 null（供 applyI18n 保留 HTML 既有文字用）。 */
function lookup(key) {
    const lang = getLang();
    return STRINGS[lang]?.[key] ?? STRINGS['zh-TW']?.[key] ?? null;
}

/**
 * 套用翻譯到整個頁面。
 *   data-i18n="key"             → element.textContent
 *   data-i18n-placeholder="key" → element.placeholder
 *   data-i18n-title="key"       → element.title
 *   data-i18n-html="key"        → element.innerHTML（慎用，僅靜態內容）
 */
function applyI18n() {
    // 更新 <html lang>
    document.documentElement.lang = getLang();

    // 查無 key 時一律「不覆寫」，保留 HTML 既有文字（各頁預設為中文）——
    // 舊版 SW 快取或字典漏 key 時，畫面才不會出現 "dispatch.op_section" 這種原始鍵。
    document.querySelectorAll('[data-i18n]').forEach(el => {
        const val = lookup(el.dataset.i18n);
        if (val !== null) el.textContent = val;
    });
    document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
        const val = lookup(el.dataset.i18nPlaceholder);
        if (val !== null) el.placeholder = val;
    });
    document.querySelectorAll('[data-i18n-title]').forEach(el => {
        const val = lookup(el.dataset.i18nTitle);
        if (val !== null) el.title = val;
    });
    document.querySelectorAll('[data-i18n-html]').forEach(el => {
        const val = lookup(el.dataset.i18nHtml);
        if (val !== null) el.innerHTML = val;
    });

    // 語言切換按鈕顯示「可切換到的語言」
    document.querySelectorAll('[data-lang-toggle]').forEach(el => {
        el.textContent = t('common.lang_toggle');
    });
}

/**
 * 切換語言並重新載入頁面。
 * 已登入：POST /api/mobile/auth/language，同時寫 culture cookie 與帳號偏好
 * （推播與下次登入的預設語系讀帳號偏好；只寫 cookie 的話推播永遠是管理員設定的語系）。
 * 未登入（登入頁）：API 回 401／轉址 → 退回 GET /UserSettings/SetLanguage，只寫 cookie。
 * 失敗一律忽略，不影響手機端切換。
 */
async function setLang(lang) {
    localStorage.setItem('mobileLang', lang);

    try {
        const res = await fetch('/api/mobile/auth/language', {
            method: 'POST',
            credentials: 'same-origin',
            redirect: 'manual',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ culture: lang })
        });
        if (!res.ok) {
            const returnUrl = encodeURIComponent(location.pathname + location.search);
            await fetch(`/UserSettings/SetLanguage?culture=${lang}&returnUrl=${returnUrl}`,
                { credentials: 'same-origin' });
        }
    } catch { /* 忽略 */ }

    location.reload();
}

/** 切換語言（zh-TW ↔ en-US）。 */
function toggleLang() {
    setLang(getLang() === 'zh-TW' ? 'en-US' : 'zh-TW');
}

// 頁面載入時自動套用
document.addEventListener('DOMContentLoaded', applyI18n);
