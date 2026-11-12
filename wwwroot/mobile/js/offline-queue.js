/**
 * offline-queue.js — 斷線保護：送出時連不上就先存在手機，恢復後自動補送（mobile-pwa.md §5.2）
 *
 * ⚠️ 這**不是**離線作業（2026-10-10 決定）：讀取一律要連線（api.js apiGet），佇列只負責
 *    「按下送出的那幾秒剛好斷線」。所以排隊超過 OQ_AUTO_SEND_MS 就不再自動送出，改由本人確認——
 *    斷了半小時以上，現場狀況多半已經變了（派工被調、別人補登過），不該在背後默默送出去。
 *
 * 架構要點：
 *  - 寫入操作連不上時存入 IndexedDB offlineQueue store，不依賴 sessionStorage
 *    （navigator.onLine 為 false，或 Wi-Fi 有訊號但連不到伺服器——見 submitOrQueue）
 *  - 網路恢復（window 'online' 事件）與每分鐘定期檢查時自動 flush，依 priority 排序（機台狀態 > 報工）
 *  - 送出時帶 X-Client-Sent-At：伺服器據此把下工／報工／機台狀態記在**發生時間**，不是送達時間
 *  - clientId 去重：同 ID 第二次送後端回 200（idempotent），前端移出佇列
 *  - 衝突（4xx）：標記 error 並通知，不重試（需人工處理）
 *  - Cookie 認證：不需 JWT refresh，Cookie 隨請求自動帶上
 *
 * 使用：
 *   const q = new OfflineQueue();
 *   await q.ready();                        // 等 DB open
 *   await q.enqueue({ url, method, body, priority, label });
 *   // 網路恢復自動 flush，也可手動：
 *   await q.flush(statusCallback);
 *   const items = await q.getPending();     // UI 顯示待同步列表
 */

'use strict';

const OQ_DB_NAME    = 'mes-offline-queue';
const OQ_DB_VERSION = 2;          // v2：加入 inspectionDrafts store（Phase H）
const OQ_STORE      = 'offlineQueue';
const DRAFT_STORE   = 'inspectionDrafts';

/**
 * 目前作用中的廠別代碼；讀不到一律回 null（＝「不知道」，不是「沒有廠別」）。
 *
 * 來源是 sessionStorage.mobileUser — auth.js 的 requireAuth() 每頁進來都會用
 * /api/mobile/auth/me 的回應刷新它，所以這裡**不另外打 API**：同一個事實多一個
 * 取得管道，就多一份會漂掉的狀態，而漂掉的症狀是「擋錯人」或「該擋沒擋」。
 */
function currentPlantId() {
    return currentUserField('plantId');
}

/**
 * 目前登入者（id／name）。同一個來源、同樣「讀不到就是 null」的語意。
 *
 * 佇列送出時帶的是**當下**的 Cookie，而不是排入那個人的——共用手機上
 * A 離線排了報工、交班後 B 登入，一連線就會把 A 的報工記在 B 名下，
 * 而且伺服端完全無從分辨。auth.js 讓離線冷啟動也認得人之後，這條路更容易走到。
 */
function currentUser() {
    const id = currentUserField('id');
    return id ? { id, name: currentUserField('name') || id } : null;
}

function currentUserField(field) {
    try {
        return JSON.parse(sessionStorage.getItem('mobileUser') || '{}')[field] || null;
    } catch {
        return null;
    }
}

/**
 * 本人的未送出項目放了多久要提醒。資料留在手機上越久，被別人翻到、或手機遺失時帶走的機會越大；
 * 而且過了一天還沒送出，多半是卡住了（錯誤、換了廠），不是單純沒網路。
 */
const OQ_STALE_MS = 24 * 60 * 60 * 1000;

/**
 * 排隊多久以內才自動送出。超過就轉成 held（待確認），本人在首頁的待同步清單按「確認送出」或「捨棄」。
 * 伺服器另有 24 小時上限（MobileOccurredAt.MaxBackdate），超過一律拒收、要到桌機補登。
 */
const OQ_AUTO_SEND_MS = 30 * 60 * 1000;

/** 連得上網路卻還有待送項目時，多久再試一次（伺服器重啟中、廠區網段短暫斷線時 'online' 事件不會發生）。 */
const OQ_RETRY_INTERVAL_MS = 60 * 1000;

/** 佇列送出時帶給伺服器的「手機當下時間」header（見 MobileOccurredAt.cs）。 */
const OQ_SENT_AT_HEADER = 'X-Client-Sent-At';

/**
 * 補送時等多久沒有回應就放下這一筆、結束這一輪（保留 pending，下一輪再送）。
 * 原本沒有逾時：卡在一筆永遠不回的請求上，_flushing 一直是 true，之後的 'online' 與每分鐘重試
 * 全部被「正在送」擋掉，整個佇列停擺到重新整理頁面為止。與 api.js 的 API_TIMEOUT_MS 同值。
 */
const OQ_REQUEST_TIMEOUT_MS = 20_000;

/** 並發衝突（409 CONCURRENCY_CONFLICT）自動重送幾輪後才放棄、交給人處理。 */
const OQ_MAX_CONFLICT_RETRIES = 3;

/**
 * 批次端點的回應裡有沒有「部分明細失敗」；有就回一段給人看的說明，沒有回 null。
 * 形狀是 MobileOkResult：{ data: [{ success, message, ... }], message }。
 * 只看得懂這個形狀——其他端點的 data 不是陣列，一律視為沒有部分失敗。
 */
function describePartialFailure(result) {
    const lines = Array.isArray(result?.data) ? result.data : null;
    if (!lines) return null;
    const bad = lines.filter(l => l && l.success === false);
    if (!bad.length) return null;
    const detail = bad.slice(0, 5).map(l => l.message).filter(Boolean).join('; ');
    return `${result.message || ''}${detail ? ' (' + detail + (bad.length > 5 ? ' ...' : '') + ')' : ''}`;
}

class OfflineQueue {
    constructor() {
        this._db       = null;
        this._ready    = this._open();
        this._flushing = false;

        // 網路恢復自動 flush
        window.addEventListener('online', () => this._autoFlush());
        // 'online' 只在手機自己的網路狀態改變時發生；連得上 Wi-Fi 但伺服器暫時連不到，
        // 恢復時不會有任何事件——定期再試，安靜進行（沒有送出任何東西就不跳通知）。
        if (typeof setInterval === 'function') {
            const timer = setInterval(() => { if (navigator.onLine) this._autoFlush({ quiet: true }); },
                OQ_RETRY_INTERVAL_MS);
            timer?.unref?.();
        }
    }

    // ── 初始化 ────────────────────────────────────────────────────────────────
    ready() { return this._ready; }

    _open() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(OQ_DB_NAME, OQ_DB_VERSION);

            req.onupgradeneeded = e => {
                const db = e.target.result;
                if (!db.objectStoreNames.contains(OQ_STORE)) {
                    const store = db.createObjectStore(OQ_STORE, { keyPath: 'clientId' });
                    store.createIndex('status',   'status',   { unique: false });
                    store.createIndex('priority', 'priority', { unique: false });
                }
                // v2：草稿暫存 store，key = `${userId}:${dispatchId}`（Phase H Draft Autosave）
                if (!db.objectStoreNames.contains(DRAFT_STORE)) {
                    db.createObjectStore(DRAFT_STORE, { keyPath: 'draftKey' });
                }
            };

            req.onsuccess = e => { this._db = e.target.result; resolve(); };
            req.onerror   = e => { console.error('[OQ] DB open failed', e); reject(e); };
        });
    }

    /**
     * 這個 URL 是不是「絕對不能離線排隊」的路徑。
     *
     * 目前只有簽核。判斷用 **URL 前綴**而不是完整比對，因為簽核路徑帶了 requestId
     * （/api/mobile/approval/{id}/decide），完整比對永遠不會成立——
     * 那種「守門看起來有寫、實際上一次都沒生效」正是最難發現的一種。
     */
    static isNeverQueued(url) {
        return typeof url === 'string' && url.startsWith('/api/mobile/approval/');
    }

    // ── 加入佇列 ──────────────────────────────────────────────────────────────
    /**
     * @param {object} item
     * @param {string} item.url      - API endpoint（如 '/api/mobile/reports'）
     * @param {string} item.method   - 'POST' | 'PATCH'
     * @param {object} item.body     - 請求 body（含 clientId / clientTimestamp）
     * @param {number} [item.priority=5] - 數字越小優先度越高（機台狀態=1, 報工=5）
     * @param {string} [item.label]  - UI 顯示用描述（如「報工 OK:5 NG:0」）
     */
    async enqueue(item) {
        await this._ready;

        // 🚨 簽核**不得**進佇列。佇列的語意是「晚一點送出，結果一樣」，簽核不是：
        //    排隊期間單據可能被撤回、被別人簽掉、或步驟已推進，而伺服端的
        //    ProcessApproval 對這些一律回 false。真的排進去的話，使用者會在幾小時後
        //    收到一則「簽核失敗」，而且完全不知道當時發生了什麼事——
        //    比當場告訴他「現在沒有網路，簽核請等連線恢復」糟得多。
        //
        //    這裡用擲出例外而不是靜靜略過：靜靜略過的話呼叫端會以為排進去了，
        //    使用者也會以為簽好了，那是最壞的一種失敗。
        //    訊息刻意用英文：這是**開發期的程式錯誤**，不是給使用者看的文案
        //    （擲出後不會顯示在任何畫面上，畫面那邊由 list.page.js 的 navigator.onLine
        //     檢查提早擋下並顯示 approval.offline）。走 t() 反而會在字典裡多一個
        //    永遠不會被渲染的鍵，而 MobileI18nKeyTests 守的正是「面向使用者的中文字面」。
        if (OfflineQueue.isNeverQueued(item.url)) {
            throw new Error(`OfflineQueue: refusing to queue ${item.url}`);
        }

        // clientId **一定要寫進 body**：它是伺服端去重（SyncLog）的鍵。原本 body 沒帶時
        // 只在這裡生一個當 IndexedDB 的鍵，從沒送到伺服器——上架／下架就是這樣，
        // 「已處理、回應在路上斷線」之後的重送會被當成新的一筆再做一次。
        const enqueuedAt = Date.now();
        const clientId   = item.body.clientId || crypto.randomUUID();
        const body = {
            ...item.body,
            clientId,
            clientTimestamp: item.body.clientTimestamp ?? new Date(enqueuedAt).toISOString()
        };

        const record = {
            clientId,
            url:        item.url,
            method:     item.method || 'POST',
            body,
            priority:   item.priority ?? 5,
            label:      item.label   ?? item.url,
            status:     'pending',
            enqueuedAt,
            attempts:   0,
            errorMsg:   null,
            // 排入當下的廠別。佇列裡沒有任何一個 body 帶廠別——廠別是 flush 當下由
            // 伺服端從 ActivePlantId 決定的，所以「排隊期間換了廠」會靜靜地把這筆
            // 記到別的廠去。記下來才有辦法在送出前擋。見 flush() 的比對。
            plantId:    currentPlantId(),
            // 排入者。理由見 currentUser()；比對在 flush()。
            userId:     currentUser()?.id   ?? null,
            userName:   currentUser()?.name ?? null
        };
        return this._put(record);
    }

    // ── 取得待同步列表 ────────────────────────────────────────────────────────
    getPending() {
        return this._getByStatus('pending');
    }

    getAll() {
        return this._getAll();
    }

    /**
     * 目前登入者自己的項目，以及別人還沒送出的筆數（只給數字，不給內容）。
     *
     * 共用手機上，首頁的佇列清單原本列出**所有人**的項目連同說明與錯誤訊息——
     * 換人登入就看得到前一個人報了什麼。歸屬判定與 flush() 的放行規則相同：
     * 任一邊是 null（本欄位加入前排入的舊項目，或讀不到登入者）就視為自己的。
     */
    async getAllMine() {
        const me  = currentUser();
        const all = await this._getAll();
        const isMine = r => !r.userId || !me || r.userId === me.id;
        const mine   = all.filter(isMine);
        const now    = Date.now();
        return {
            mine,
            others: all.filter(r => !isMine(r) && r.status !== 'sent').length,
            // 本人超過 OQ_STALE_MS 仍未送出（pending／error）的筆數，首頁據此提醒
            stale:  mine.filter(r => r.status !== 'sent' && now - r.enqueuedAt > OQ_STALE_MS).length,
        };
    }

    // ── 重試失敗項目 ──────────────────────────────────────────────────────────
    /** 將單筆 error 項目重置為 pending，讓下次 flush 重試 */
    async retryOne(clientId) {
        await this._ready;
        const all = await this._getAll();
        const item = all.find(r => r.clientId === clientId && r.status === 'error');
        if (!item) return;
        // 重試是本人明確的動作：視同確認過，排隊再久也照送（見 OQ_AUTO_SEND_MS）
        await this._put({ ...item, status: 'pending', errorMsg: null, confirmedAt: Date.now() });
    }

    /** 將所有 error 項目重置為 pending */
    async retryFailed() {
        await this._ready;
        const failed = await this._getByStatus('error');
        await Promise.all(failed.map(r => this._put({ ...r, status: 'pending', errorMsg: null, confirmedAt: Date.now() })));
        return failed.length;
    }

    // ── 排隊太久的項目（held）────────────────────────────────────────────────
    /** 本人確認後照送：改回 pending 並記下確認時間，flush 不再因為排太久而擋。 */
    async confirmHeld(clientId) {
        await this._ready;
        const item = (await this._getByStatus('held')).find(r => r.clientId === clientId);
        if (!item) return false;
        await this._put({ ...item, status: 'pending', errorMsg: null, confirmedAt: Date.now() });
        return true;
    }

    /**
     * 本人決定不送：刪掉這一筆（只限 held／error——pending 可能正在送，sent 由 clearSent 清）。
     * 例如斷線時報了工，後來已經在桌機補登過，再送就重複了。
     */
    async discard(clientId) {
        await this._ready;
        const item = (await this._getAll()).find(r => r.clientId === clientId);
        if (!item || (item.status !== 'held' && item.status !== 'error')) return false;
        await this._delete(clientId);
        return true;
    }

    // ── 清除已送出 ────────────────────────────────────────────────────────────
    async clearSent() {
        await this._ready;
        const all = await this._getAll();
        await Promise.all(all.filter(r => r.status === 'sent').map(r => this._delete(r.clientId)));
    }

    // ── Flush（依序發送） ─────────────────────────────────────────────────────
    /**
     * @param {function({sent,failed,skipped}):void} [onProgress] - 進度回呼
     */
    async flush(onProgress) {
        await this._ready;
        // 已經在送：回一份全 0 的結果而不是 undefined——呼叫端一律解構，undefined 會讓它整個擲出
        if (this._flushing) return { sent: 0, failed: 0, partial: 0, held: 0, busy: true };
        this._flushing = true;

        const pending = (await this.getPending()).sort((a, b) => a.priority - b.priority || a.enqueuedAt - b.enqueuedAt);
        const nowPlant = currentPlantId();
        const nowUser  = currentUser();
        let sent = 0, failed = 0, partial = 0, held = 0;
        const now = Date.now();

        for (const item of pending) {
            if (!navigator.onLine) break; // 中途再次斷線就停

            // 廠別不符就**不要送出去賭伺服端會擋**。伺服端擋得住的那幾條路徑
            // 回的是 404 MACHINE_NOT_FOUND / 查不到來源單，畫面上只會出現
            // 「HTTP 404」——現場看到的是同步壞了，而不是「你切了廠」。
            //
            // 舊版沒有 plantId 的資料保留在佇列；伺服器會拒絕缺少上下文的業務寫入。
            // null 代表這筆是本次改動之前排入的
            // （舊版沒有這個欄位），或當下讀不到廠別。把它們一律擋掉，會讓升級後
            // 手機裡既有的待同步資料全部變成失敗——那比原本的問題更糟。
            if (item.plantId && nowPlant && item.plantId !== nowPlant) {
                await this._put({ ...item, status: 'error',
                    errorMsg: tf('home.q_plant_mismatch', item.plantId, nowPlant) });
                failed++;
                if (onProgress) onProgress({ sent, failed, total: pending.length });
                continue;   // 其他同廠的項目照送，不因為一筆就整批停下
            }

            // 排入者 ≠ 目前登入者：同上，**不送**，留給原排入者登入後重試。
            // 放行規則與廠別相同——任一邊是 null（本欄位加入前排入的舊項目）就放行。
            if (item.userId && nowUser && item.userId !== nowUser.id) {
                await this._put({ ...item, status: 'error',
                    errorMsg: tf('home.q_user_mismatch', item.userName || item.userId, nowUser.name) });
                failed++;
                if (onProgress) onProgress({ sent, failed, total: pending.length });
                continue;
            }

            // 排太久：不自動送，等本人確認（理由見 OQ_AUTO_SEND_MS）。不算失敗——它沒有錯，只是需要人看一眼。
            if (!item.confirmedAt && now - item.enqueuedAt > OQ_AUTO_SEND_MS) {
                await this._put({ ...item, status: 'held' });
                held++;
                if (onProgress) onProgress({ sent, failed, total: pending.length });
                continue;
            }

            const ctrl  = typeof AbortController === 'function' ? new AbortController() : null;
            const timer = ctrl ? setTimeout(() => ctrl.abort(), OQ_REQUEST_TIMEOUT_MS) : null;
            try {
                const res = await fetch(item.url, {
                    method:      item.method,
                    credentials: 'include',
                    headers:     { 'Content-Type': 'application/json',
                                   'X-Plant-Context': item.plantId || '',
                                   [OQ_SENT_AT_HEADER]: new Date().toISOString() },
                    body:        JSON.stringify(item.body),
                    signal:      ctrl?.signal
                });

                if (res.ok) {
                    // 2xx＝伺服端處理過（去重的重送也回 2xx 與第一次的結果）。
                    // 批次端點（盤點）逐行回報成敗：有行失敗時**不能**顯示成全部成功，
                    // 但也不能重送——同一個 clientId 只會拿回同一份結果，失敗的那幾行要人重做。
                    const data = await res.json().catch(() => null);
                    const warnMsg = describePartialFailure(data);
                    await this._put({ ...item, status: 'sent', sentAt: Date.now(), warnMsg });
                    sent++;
                    if (warnMsg) partial++;
                } else if (res.status === 409) {
                    // ⚠️ 409 **不是**「已去重」。會進佇列的端點重送時回的是 2xx；
                    //    409 是並發衝突（CONCURRENCY_CONFLICT：xmin 被別人先改，**這筆沒有存進去**）
                    //    或狀態衝突。原本一律標成 sent，離線報工撞上並發衝突就這樣靜靜消失。
                    const err = await res.json().catch(() => ({}));
                    const code = err.error?.code;
                    const attempts = item.attempts + 1;
                    if (code === 'CONCURRENCY_CONFLICT' && attempts < OQ_MAX_CONFLICT_RETRIES) {
                        // 並發衝突是暫時的：body 自足，下一輪重送通常就過了。留 pending。
                        await this._put({ ...item, attempts,
                            errorMsg: err.error?.message || `HTTP ${res.status}` });
                        failed++;
                    } else {
                        await this._put({ ...item, status: 'error', attempts,
                            errorMsg: err.error?.message || err.message || `HTTP ${res.status}` });
                        failed++;
                    }
                } else if ([502, 503, 504].includes(res.status)) {
                    // 閘道錯誤＝伺服器暫時不在（Tunnel 後面的 app 重啟中）：跟網路例外一樣保留 pending、停下，
                    // 等下一輪再送。標成 error 的話這筆就要人手動重試。（不引用 api.js 的 GATEWAY_STATUSES：
                    // 本檔可能比 api.js 先載入、也要能單獨測。）
                    break;
                } else if (res.status === 401) {
                    // Cookie 過期需重新登入，停止 flush
                    console.warn('[OQ] 401 Cookie 過期，停止同步');
                    break;
                } else {
                    const err = await res.json().catch(() => ({}));
                    // 行動端點的錯誤在 err.error.message；倉庫端點（上下架）是 { success, message }
                    await this._put({ ...item, status: 'error', attempts: item.attempts + 1,
                        errorMsg: err.error?.message || err.message || `HTTP ${res.status}` });
                    failed++;
                }
            } catch (e) {
                // 網路錯誤或逾時（AbortError），保留 pending，停止；伺服器若其實處理完了，下一輪重送由 clientId 去重
                break;
            } finally {
                if (timer) clearTimeout(timer);
            }

            if (onProgress) onProgress({ sent, failed, total: pending.length });
        }

        this._flushing = false;
        // 讓頁面在同步完之後才重抓畫面（例如派工列表）。在 'online' 事件裡直接重抓會
        // 跟 flush 賽跑，抓到的是還沒套用佇列的舊資料。
        window.dispatchEvent(new CustomEvent('offlinequeue:flushed', { detail: { sent, failed, partial, held } }));
        return { sent, failed, partial, held };
    }

    // ── 自動 flush（網路恢復） ────────────────────────────────────────────────
    async _autoFlush({ quiet = false } = {}) {
        const pending = await this.getPending().catch(() => []);
        if (pending.length === 0) return;

        if (!quiet) this._notifyFlushStart(pending.length);
        const { sent, failed, partial, held } = await this.flush();
        this._notifyFlushDone(sent, failed, partial, held);
    }

    _notifyFlushStart(count) {
        if (typeof showToast === 'function')
            showToast(tf('home.sync_start', count), 'info');
    }

    _notifyFlushDone(sent, failed, partial = 0, held = 0) {
        if (typeof showToast !== 'function') return;
        if (held > 0)
            showToast(tf('home.sync_held', held), 'warning');
        else if (partial > 0)
            showToast(tf('home.sync_partial', partial), 'warning');
        else if (failed > 0)
            showToast(tf('home.sync_result', sent, failed), 'warning');
        else if (sent > 0)
            showToast(tf('home.sync_done', sent), 'success');
    }

    // ── IndexedDB helpers ─────────────────────────────────────────────────────
    _put(record) {
        return new Promise((resolve, reject) => {
            const tx  = this._db.transaction(OQ_STORE, 'readwrite');
            const req = tx.objectStore(OQ_STORE).put(record);
            req.onsuccess = () => resolve(record);
            req.onerror   = e => reject(e);
        });
    }

    _delete(clientId) {
        return new Promise((resolve, reject) => {
            const tx  = this._db.transaction(OQ_STORE, 'readwrite');
            const req = tx.objectStore(OQ_STORE).delete(clientId);
            req.onsuccess = () => resolve();
            req.onerror   = e => reject(e);
        });
    }

    _getByStatus(status) {
        return new Promise((resolve, reject) => {
            const tx      = this._db.transaction(OQ_STORE, 'readonly');
            const index   = tx.objectStore(OQ_STORE).index('status');
            const req     = index.getAll(IDBKeyRange.only(status));
            req.onsuccess = e => resolve(e.target.result || []);
            req.onerror   = e => reject(e);
        });
    }

    _getAll() {
        return new Promise((resolve, reject) => {
            const tx  = this._db.transaction(OQ_STORE, 'readonly');
            const req = tx.objectStore(OQ_STORE).getAll();
            req.onsuccess = e => resolve(e.target.result || []);
            req.onerror   = e => reject(e);
        });
    }
}

// ── Draft Autosave helpers（Phase H，mobile-pwa.md §14.3）────────────────────
// key = `${userId}:${dispatchId}`，每次 change 事件寫入，頁面重載後自動恢復。

OfflineQueue.prototype.saveDraft = function (userId, dispatchId, formData) {
    const draftKey = `${userId}:${dispatchId}`;
    return new Promise((resolve, reject) => {
        this.ready().then(() => {
            const tx  = this._db.transaction(DRAFT_STORE, 'readwrite');
            const req = tx.objectStore(DRAFT_STORE).put({ draftKey, formData, savedAt: Date.now() });
            req.onsuccess = () => resolve();
            req.onerror   = e => reject(e);
        });
    });
};

OfflineQueue.prototype.loadDraft = function (userId, dispatchId) {
    const draftKey = `${userId}:${dispatchId}`;
    return new Promise((resolve, reject) => {
        this.ready().then(() => {
            const tx  = this._db.transaction(DRAFT_STORE, 'readonly');
            const req = tx.objectStore(DRAFT_STORE).get(draftKey);
            req.onsuccess = e => resolve(e.target.result?.formData ?? null);
            req.onerror   = e => reject(e);
        });
    });
};

OfflineQueue.prototype.clearDraft = function (userId, dispatchId) {
    const draftKey = `${userId}:${dispatchId}`;
    return new Promise((resolve, reject) => {
        this.ready().then(() => {
            const tx  = this._db.transaction(DRAFT_STORE, 'readwrite');
            const req = tx.objectStore(DRAFT_STORE).delete(draftKey);
            req.onsuccess = () => resolve();
            req.onerror   = e => reject(e);
        });
    });
};

// ── 送出：連得上就直接送，連不上就排隊 ─────────────────────────────────────
/**
 * 頁面的寫入一律走這裡（簽核除外——它不得排隊，見 isNeverQueued）。
 *
 * 原本各頁只看 navigator.onLine：Wi-Fi 有訊號但伺服器連不到（廠區網段斷了、伺服器重啟中）時
 * onLine 照樣是 true，直接送出失敗就只跳「網路錯誤」，**按下去的那筆就沒了**——斷線保護真正要防的
 * 正是這種幾秒鐘的斷訊。現在 fetch 本身失敗也排進佇列；同一個 clientId 重送由伺服器去重，
 * 所以就算其實已經送到、只是回應在路上斷掉，也不會記兩次。
 *
 * 伺服器有回應（含 4xx）就照原樣交回，不排隊：那是「被拒」，不是「沒送到」。
 *
 * @returns {Promise<{queued: boolean, result: any}>} queued＝已排隊；否則 result 是 apiCall 的回傳（null＝失敗，已跳過提示）
 */
async function submitOrQueue({ url, method = 'POST', body, priority = 5, label }) {
    // 第一次直接送就要帶 clientId：回應在路上斷掉、轉排隊重送時，伺服器才認得出是同一筆。
    // clientTimestamp＝按下送出的時間，伺服器據此回推發生時間（見 MobileOccurredAt.cs）。
    body = { ...body,
             clientId:        body?.clientId || crypto.randomUUID(),
             clientTimestamp: body?.clientTimestamp || new Date().toISOString() };
    if (navigator.onLine) {
        const result = await apiCall(method, url, body, { networkFailure: true });
        if (result !== API_NETWORK_FAILED) return { queued: false, result };
    }
    await window.offlineQueue.enqueue({ url, method, body, priority, label });
    return { queued: true, result: null };
}

// ── 全域單例 ──────────────────────────────────────────────────────────────────
// 各頁面透過 window.offlineQueue 共用同一佇列（同源頁面共用 IndexedDB）
window.offlineQueue = new OfflineQueue();
