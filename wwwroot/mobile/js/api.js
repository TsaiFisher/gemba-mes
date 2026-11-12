/**
 * api.js — 手機版共用 fetch wrapper
 * 自動帶 Cookie、統一處理 401 導回登入頁、統一錯誤 Toast
 */

/** apiCall 的 callOpts.networkFailure：連不上時回這個哨兵而不是 null（並且不跳 Toast），讓呼叫端分辨「連不上」與「被拒」。 */
const API_NETWORK_FAILED = Symbol('apiNetworkFailed');
/** 視同「連不上」的閘道狀態碼（見 apiCall）。offline-queue.js 的 flush 也用同一份。 */
const GATEWAY_STATUSES = [502, 503, 504];

/**
 * 等多久沒有回應就當成連不上（只用在 callOpts.networkFailure 的呼叫：讀取，以及帶 clientId、會轉排隊的寫入）。
 *
 * 訊號弱到「請求送得出去、回應回不來」時，fetch 本身不會逾時——瀏覽器要等好幾分鐘，
 * 按鈕就一直轉圈，那一筆既沒送達也沒排隊（2026-10-21 混沌測試 MobileChaosTests 抓到）。
 * 只對這兩類加：讀取重來沒有副作用；轉排隊的寫入帶 clientId，伺服器即使其實做完了，
 * 重送也只會拿回同一份結果。其他寫入（簽核等）不帶 clientId，逾時後讓人重按可能做兩次，維持原樣。
 * 與 offline-queue.js 的 OQ_REQUEST_TIMEOUT_MS 同值。
 */
const API_TIMEOUT_MS = 20_000;

/**
 * 403 的提示文字。廠別失效／角色被移除不是「沒有權限」，而是要重新選擇——
 * 原本一律顯示「權限不足」，使用者不知道切換廠別就能恢復（2026-11-11 筆電驗收 A4）。
 * 代碼由 UserPreferenceMiddleware 給；文字用手機自己的字典（手機語言可以與帳號語系不同）。
 */
function forbiddenMessage(body) {
    const code = body?.error?.code;
    if (code === 'PLANT_CONTEXT_INVALID') return t('common.plant_invalid');
    if (code === 'ACTIVE_ROLE_REVOKED')   return t('common.role_revoked');
    return t('common.forbidden');
}

async function apiCall(method, url, body = null, callOpts = {}) {
    const opts = {
        method,
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' }
    };
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase())) {
        try {
            opts.headers['X-Plant-Context'] = JSON.parse(sessionStorage.getItem('mobileUser') || '{}').plantId || '';
        } catch { opts.headers['X-Plant-Context'] = ''; }
    }
    if (body) opts.body = JSON.stringify(body);

    // 逾時只用在重來安全的呼叫（見 API_TIMEOUT_MS）；中止後 fetch 擲出 AbortError，走下面「連不上」的處理
    let timer = null;
    if (callOpts.networkFailure && typeof AbortController === 'function') {
        const ctrl = new AbortController();
        opts.signal = ctrl.signal;
        timer = setTimeout(() => ctrl.abort(), API_TIMEOUT_MS);
    }

    try {
        const res = await fetch(url, opts);

        // 登入類端點的 401 代表「憑證/驗證碼錯誤」，不是「session 過期」——
        // 不可比照其他端點強制清空 session 並導回登入頁（本身就在登入頁，
        // 會讓使用者看不到任何錯誤訊息，誤以為畫面卡住又跳回帳密輸入畫面）。
        const isAuthEndpoint = url.startsWith('/api/mobile/auth/login');
        if (res.status === 401 && !isAuthEndpoint) {
            sessionStorage.clear();
            // 伺服器說 session 已失效——離線沿用的身分也要一起作廢（見 auth.js offlineUser）
            clearOfflineUserData();
            window.location.href = '/mobile/index.html';
            return null;
        }

        // 閘道類錯誤（Tunnel／反向代理回的 502／503／504）＝請求沒到 app 或 app 正在重啟，
        // 跟「連不上」是同一件事：經 Cloudflare Tunnel 時伺服器停掉，手機拿到的是 502 不是網路例外。
        // 只在呼叫端要分辨「連不上」時（networkFailure）才這樣回；重送由 clientId 去重保護。
        if (callOpts.networkFailure && GATEWAY_STATUSES.includes(res.status)) return API_NETWORK_FAILED;

        if (res.status === 403) {
            showToast(forbiddenMessage(await res.json().catch(() => ({}))), 'danger');
            return null;
        }

        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            // 手機 API 的錯誤是 { success:false, error:{ code, message } }，倉庫端點才是 { message }。
            // 原本只讀 err.message，手機端的錯誤原因（派工狀態不允許、機台不可用…）一律只顯示「操作失敗 400」。
            showToast(err.error?.message || err.message || tf('common.op_failed', res.status), 'danger');
            return null;
        }

        return res.status === 204 ? {} : await res.json();
    } catch (e) {
        if (callOpts.networkFailure) return API_NETWORK_FAILED;
        showToast(t('common.net_error'), 'danger');
        return null;
    } finally {
        if (timer) clearTimeout(timer);
    }
}

// ── 讀取一律要連線（斷線保護，mobile-pwa.md §5.2）────────────────────────
// 原本這裡在連不上時拿「上次那一份」頂替（localStorage 依使用者存每個網址的最後回應）。
// 2026-10-10 決定拿掉：只存得到「連線時開過的頁面」，資料不完整又已過時，
// 而且讓人以為手機可以離線作業——實際上派工、庫存、待檢單都可能早已不同。
// 現在連不上就直接說「需要連線」；斷線保護只負責**送出**（offline-queue.js）。
const OFFLINE_GET_PREFIX = 'mobileOfflineGet:';   // 舊版留下的快取鍵，只剩清除用途

async function apiGet(url) {
    const data = await apiCall('GET', url, null, { networkFailure: true });
    if (data === API_NETWORK_FAILED) {
        noteNeedConnection();
        return null;
    }
    return data;
}

/** 同一頁只提示一次「需要連線」，避免一頁三四個查詢連跳三四個 Toast。 */
let _needConnectionShown = false;
function noteNeedConnection() {
    if (_needConnectionShown) return;
    _needConnectionShown = true;
    showToast(t('common.need_connection'), 'danger');
}

/** 登出／session 失效時清掉離線沿用的身分，以及舊版留在手機上的「上次那一份」。 */
function clearOfflineUserData() {
    try {
        localStorage.removeItem('mobileUserOffline');
        purgeLegacyLastKnown();
    } catch { }
}

/** 舊版存的各頁回應（含別人看不到的清單內容），升級後沒有任何讀取點了，載入時順手清掉。 */
function purgeLegacyLastKnown() {
    try {
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const k = localStorage.key(i);
            if (k && k.startsWith(OFFLINE_GET_PREFIX)) localStorage.removeItem(k);
        }
    } catch { }
}
purgeLegacyLastKnown();

// 依計量單位小數位數格式化數量顯示（對齊桌面版 IUomService.GetAllDecimalPlacesAsync 規範）。
// 全站共用同一份 UOM 小數位數字典，快取避免每頁重複查詢。
let _uomDecimalsPromise = null;
async function getUomDecimalsMap() {
    if (!_uomDecimalsPromise) {
        _uomDecimalsPromise = apiGet('/api/uom/decimals').then(m => m || {});
    }
    return _uomDecimalsPromise;
}

function formatQty(qty, uomCode, decimalsMap) {
    const n = qty ?? 0;
    const dp = (uomCode && decimalsMap && decimalsMap[uomCode] != null) ? decimalsMap[uomCode] : 4;
    return n.toLocaleString('zh-TW', { maximumFractionDigits: dp });
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const id = `toast-${Date.now()}`;
    const html = `
        <div id="${id}" class="toast align-items-center text-bg-${type} border-0" role="alert">
            <div class="d-flex">
                <div class="toast-body">${message}</div>
                <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button>
            </div>
        </div>`;
    container.insertAdjacentHTML('beforeend', html);
    const toastEl = document.getElementById(id);
    const toast = new bootstrap.Toast(toastEl, { delay: 3000 });
    toast.show();
    toastEl.addEventListener('hidden.bs.toast', () => toastEl.remove());
}

function setLoading(btn, loading) {
    if (!btn) return;
    if (loading) {
        btn.disabled = true;
        btn.dataset.originalHtml = btn.innerHTML;
        btn.innerHTML = `<span class="spinner-border spinner-border-sm me-1"></span>${t('common.processing')}`;
    } else {
        btn.disabled = false;
        btn.innerHTML = btn.dataset.originalHtml || btn.innerHTML;
    }
}

// 進度條寬度套用：取代 inline style="width:N%"（CSP style-src 已移除 unsafe-inline）。
// CSSOM 屬性設值 el.style.width= 不受 style-src 管制；以 MutationObserver 處理非同步渲染的內容。
(function () {
    function applyPctWidths(root) {
        (root || document).querySelectorAll('[data-pct-width]').forEach(function (el) {
            if (el.dataset.pwApplied) return;
            el.style.width = el.getAttribute('data-pct-width') + '%';
            el.dataset.pwApplied = '1';
        });
    }
    document.addEventListener('DOMContentLoaded', function () { applyPctWidths(document); });
    new MutationObserver(function () { applyPctWidths(document); })
        .observe(document.documentElement, { childList: true, subtree: true });
})();
