'use strict';

/**
 * offline-queue.js 單元測試
 *
 * 為什麼要有這一支：手機端在此之前是**零自動化覆蓋**——UITests 一支都沒碰 /mobile，
 * jest 只測了桌機的四支腳本。而離線佇列正是人工驗收 5-4 唯一真正要驗的東西，
 * 它的失效方式全都是「安靜的」：資料留在手機裡沒送出、或送出後被錯誤地標成完成。
 * 人工測試只能抽樣幾筆，抽不到的那些看起來跟成功一模一樣。
 *
 * 這裡釘的是佇列的**可重送性**：任何一次中斷之後，沒成功的那些必須還留在 pending。
 * 一旦某條路徑誤把未送出的項目標成 sent 或 error，那筆資料就再也不會被送出去了，
 * 而現場只會發現「這筆報工不見了」。
 */

const fs   = require('fs');
const path = require('path');
const { installFakeIndexedDb } = require('./idb-stub');

const scriptSrc = fs.readFileSync(
    path.resolve(__dirname, '..', 'offline-queue.js'), 'utf-8');

let idb;
let queue;

beforeAll(() => {
    idb = installFakeIndexedDb();
    // Node 18+ 的 globalThis.crypto 是唯讀 getter，覆寫不會生效；缺的時候才補。
    if (typeof globalThis.crypto?.randomUUID !== 'function')
        global.crypto = { randomUUID: () => require('crypto').randomUUID() };
    // 檔案結尾會建立 window.offlineQueue 單例；class 本身因 'use strict' 留在 eval 作用域內，
    // 所以取用單例而不是重新 new，這也更貼近真實頁面（各頁共用同一個佇列）。
    // submitOrQueue 是頁面直接呼叫的全域函式；strict eval 的宣告留在 eval 作用域裡，這裡把它帶出來。
    eval(scriptSrc + '\n;globalThis.submitOrQueue = submitOrQueue;');  // eslint-disable-line no-eval
    queue = window.offlineQueue;
});

beforeEach(async () => {
    await queue.ready();
    idb.clear();
    setOnline(true);
    global.fetch = jest.fn();
});

afterEach(() => { jest.restoreAllMocks(); });

function setOnline(value) {
    Object.defineProperty(window.navigator, 'onLine', { value, configurable: true });
}

/** 依序回應的 fetch 替身；每次呼叫吐出 responses 的下一個。 */
function respondWith(...responses) {
    let i = 0;
    global.fetch = jest.fn(async () => {
        const r = responses[Math.min(i++, responses.length - 1)];
        if (r instanceof Error) throw r;
        return {
            ok: r.status >= 200 && r.status < 300,
            status: r.status,
            json: async () => r.body ?? {},
        };
    });
}

const enqueue = (overrides = {}) => queue.enqueue({
    url: '/api/mobile/reports',
    method: 'POST',
    body: { clientId: overrides.clientId ?? 'c1', qtyOk: 5 },
    ...overrides,
});

// ════════════════════════════════════════════════════════════════════════════

describe('enqueue', () => {

    test('沿用 body 自帶的 clientId——後端去重靠的就是它，前端不得另生一個', async () => {
        await enqueue({ clientId: 'client-supplied' });
        const [rec] = idb.records();
        expect(rec.clientId).toBe('client-supplied');
        expect(rec.status).toBe('pending');
    });

    test('body 沒帶 clientId 時自己補一個，否則同一筆會被送兩次', async () => {
        await queue.enqueue({ url: '/x', body: { qtyOk: 1 } });
        const [rec] = idb.records();
        expect(rec.clientId).toEqual(
            expect.stringMatching(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/));
    });

    test('clientId 與 clientTimestamp 一定寫進送出的 body——伺服端去重靠它', async () => {
        // 上架／下架的 body 原本沒有 clientId：佇列自己生一個當 IndexedDB 鍵，
        // 卻從沒送到伺服器，「已處理、回應在路上斷線」後的重送就被當成新的一筆。
        await queue.enqueue({ url: '/api/warehouse/put-away', method: 'POST', body: { docNo: 'GR1', qty: 3 } });
        const [rec] = idb.records();
        expect(rec.body.clientId).toBe(rec.clientId);
        expect(typeof rec.body.clientTimestamp).toBe('string');
        expect(rec.body).toMatchObject({ docNo: 'GR1', qty: 3 });

        respondWith({ status: 200 });
        await queue.flush();
        expect(JSON.parse(global.fetch.mock.calls[0][1].body).clientId).toBe(rec.clientId);
    });

    test('同一個 clientId 再次入列會覆寫而非產生第二筆', async () => {
        await enqueue({ clientId: 'dup' });
        await enqueue({ clientId: 'dup' });
        expect(idb.records()).toHaveLength(1);
    });
});

describe('簽核絕對不進佇列', () => {

    // 為什麼要有這一組：佇列的語意是「晚一點送出，結果一樣」，簽核不是——
    // 排隊期間單據可能被撤回、被別人簽掉、或步驟已推進，而伺服端的 ProcessApproval
    // 對這些一律回 false。真的排進去的話，使用者會在幾小時後收到一則「簽核失敗」，
    // 而且完全不知道當時發生了什麼事。

    test('簽核路徑入列會擲出，而不是靜靜略過', async () => {
        // 靜靜略過是最壞的失敗：呼叫端以為排進去了，使用者也以為簽好了。
        await expect(queue.enqueue({
            url: '/api/mobile/approval/REQ-1/decide',
            method: 'POST',
            body: { clientId: 'a1', action: 'approve' },
        })).rejects.toThrow(/refusing to queue/);

        expect(idb.records()).toHaveLength(0);
    });

    test('比對的是前綴，不是完整路徑', async () => {
        // 簽核路徑帶 requestId，寫成完整比對的話這道守門一次都不會生效——
        // 「看起來有寫、實際上從沒擋過」是最難發現的一種。
        await expect(queue.enqueue({
            url: '/api/mobile/approval/另一個完全不同的ID/decide',
            method: 'POST',
            body: { clientId: 'a2' },
        })).rejects.toThrow(/refusing to queue/);
    });

    test('其他路徑照常入列', async () => {
        // 反面對照。少了這一支，一個「什麼都擋」的實作也會讓上面兩支變綠，
        // 而那種實作的症狀是整個離線功能失效。
        await enqueue({ clientId: 'normal' });
        expect(idb.records()).toHaveLength(1);
    });
});

describe('flush 的送出判定', () => {

    test('200 → sent', async () => {
        await enqueue();
        respondWith({ status: 200 });
        const r = await queue.flush();
        expect(r).toMatchObject({ sent: 1, failed: 0 });
        expect(idb.records()[0].status).toBe('sent');
    });

    test('409 不是「已去重」：並發衝突代表這筆沒存進去，必須留著重送', async () => {
        // 原本 409 一律標 sent。報工／機台狀態撞上 xmin 並發衝突時伺服端回的正是 409
        // CONCURRENCY_CONFLICT——**那筆沒有存進去**，佇列卻當它送好了，離線報工就這樣消失。
        // 會進佇列的端點重送時回的是 2xx（SyncLog 重放），不是 409。
        await enqueue();
        respondWith({ status: 409, body: { error: { code: 'CONCURRENCY_CONFLICT', message: '已被修改' } } });
        const r = await queue.flush();

        expect(r.sent).toBe(0);
        const [rec] = idb.records();
        expect(rec.status).toBe('pending');
        expect(rec.attempts).toBe(1);
    });

    test('並發衝突連撞三輪才交給人處理，不會無限重送', async () => {
        await enqueue();
        respondWith({ status: 409, body: { error: { code: 'CONCURRENCY_CONFLICT', message: '已被修改' } } });
        await queue.flush();
        await queue.flush();
        await queue.flush();

        const [rec] = idb.records();
        expect(rec.status).toBe('error');
        expect(rec.errorMsg).toBe('已被修改');
    });

    test('其他 409（狀態衝突）直接標 error，不假裝送好了', async () => {
        await enqueue();
        respondWith({ status: 409, body: { error: { code: 'SESSION_ALREADY_OPEN', message: '已有進行中的上工記錄' } } });
        await queue.flush();
        expect(idb.records()[0]).toMatchObject({ status: 'error', errorMsg: '已有進行中的上工記錄' });
    });

    test('倉庫端點的錯誤格式是 { success, message }，訊息也要留下來', async () => {
        await enqueue({ url: '/api/warehouse/put-away' });
        respondWith({ status: 400, body: { success: false, message: '上架量超過可上架量' } });
        await queue.flush();
        expect(idb.records()[0].errorMsg).toBe('上架量超過可上架量');
    });

    test('批次端點部分明細失敗：算送出，但留下黃字說明，且同步結果回報 partial', async () => {
        await enqueue({ url: '/api/mobile/inv/count/first-count' });
        respondWith({ status: 200, body: {
            message: '已提交 1/2 筆盤點',
            data: [{ success: true }, { success: false, message: '此明細已覆盤，初盤量不可再修改' }],
        } });
        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 1, partial: 1 });
        const [rec] = idb.records();
        expect(rec.status).toBe('sent');
        expect(rec.warnMsg).toBe('已提交 1/2 筆盤點 (此明細已覆盤，初盤量不可再修改)');
    });

    test('全部成功的批次不留黃字', async () => {
        await enqueue({ url: '/api/mobile/inv/count/first-count' });
        respondWith({ status: 200, body: { message: 'ok', data: [{ success: true }] } });
        await queue.flush();
        expect(idb.records()[0].warnMsg).toBeNull();
    });

    test('4xx 標成 error 且不重試，錯誤訊息要留下來給人看', async () => {
        await enqueue();
        respondWith({ status: 422, body: { error: { message: '工單已完工' } } });
        const r = await queue.flush();
        expect(r).toMatchObject({ sent: 0, failed: 1 });
        const [rec] = idb.records();
        expect(rec.status).toBe('error');
        expect(rec.attempts).toBe(1);
        expect(rec.errorMsg).toBe('工單已完工');
    });

    test('錯誤回應不是 JSON 時退回 HTTP 狀態碼，不得整個 flush 炸掉', async () => {
        await enqueue();
        global.fetch = jest.fn(async () => ({
            ok: false, status: 500, json: async () => { throw new Error('not json'); },
        }));
        await queue.flush();
        expect(idb.records()[0].errorMsg).toBe('HTTP 500');
    });
});

describe('中斷之後，沒送出的必須還能再送', () => {

    test('401 停止同步，且該筆維持 pending——標成 error 就再也送不出去了', async () => {
        await enqueue();
        respondWith({ status: 401 });
        const r = await queue.flush();
        expect(r).toMatchObject({ sent: 0, failed: 0 });
        expect(idb.records()[0].status).toBe('pending');
    });

    test('網路例外維持 pending', async () => {
        await enqueue();
        respondWith(new Error('Failed to fetch'));
        await queue.flush();
        expect(idb.records()[0].status).toBe('pending');
    });

    test('請求一直不回應：逾時放下、維持 pending，而且不會卡住之後的補送', async () => {
        // 訊號弱到「送得出去、回不來」：fetch 永遠不 resolve，只有 abort 時才擲出。
        // 原本沒有逾時，flush 永遠不結束，_flushing 一直是 true——之後的 'online' 與每分鐘重試全被擋掉。
        // 只攔下 20 秒那一個計時器手動觸發（jest 假計時器會連 IndexedDB 替身的非同步一起凍住）。
        const realSetTimeout = global.setTimeout;
        const timeouts = [];
        global.setTimeout = (fn, ms, ...args) =>
            ms === 20_000 ? (timeouts.push(fn), timeouts.length) : realSetTimeout(fn, ms, ...args);
        try {
            await enqueue({ clientId: 'hang' });
            global.fetch = jest.fn((url, opts) => new Promise((_, reject) => {
                opts.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
            }));
            const flushing = queue.flush();
            for (let i = 0; i < 200 && timeouts.length === 0; i++) await new Promise(r => realSetTimeout(r, 1));
            expect(timeouts).toHaveLength(1);            // 補送那一筆有掛上逾時
            timeouts[0]();                               // 20 秒到
            await flushing;
            expect(idb.records()[0].status).toBe('pending');
        } finally {
            global.setTimeout = realSetTimeout;
        }

        respondWith({ status: 200 });                    // 訊號回來了：下一輪照常送，沒有被「正在送」擋掉
        await queue.flush();
        expect(idb.records()[0].status).toBe('sent');
    });

    test('離線時一筆都不送', async () => {
        await enqueue();
        setOnline(false);
        respondWith({ status: 200 });
        const r = await queue.flush();
        expect(r).toMatchObject({ sent: 0, failed: 0 });
        expect(global.fetch).not.toHaveBeenCalled();
        expect(idb.records()[0].status).toBe('pending');
    });

    test('送到一半又斷線：已送的算送了，剩下的維持 pending', async () => {
        await enqueue({ clientId: 'a', priority: 1 });
        await enqueue({ clientId: 'b', priority: 2 });
        global.fetch = jest.fn(async () => {
            setOnline(false);   // 第一筆送完就斷線
            return { ok: true, status: 200, json: async () => ({}) };
        });
        const r = await queue.flush();

        expect(r.sent).toBe(1);
        const byId = Object.fromEntries(idb.records().map(x => [x.clientId, x.status]));
        expect(byId).toEqual({ a: 'sent', b: 'pending' });
    });
});

describe('送出順序', () => {

    test('priority 小的先送（機台狀態 1 要早於報工 5）', async () => {
        await enqueue({ clientId: 'report',  priority: 5 });
        await enqueue({ clientId: 'machine', priority: 1 });
        respondWith({ status: 200 });

        await queue.flush();
        const order = global.fetch.mock.calls.map(c => JSON.parse(c[1].body).clientId);
        expect(order).toEqual(['machine', 'report']);
    });

    test('同 priority 依入列時間先進先出', async () => {
        await enqueue({ clientId: 'first' });
        await new Promise(r => setTimeout(r, 2));
        await enqueue({ clientId: 'second' });
        respondWith({ status: 200 });

        await queue.flush();
        const order = global.fetch.mock.calls.map(c => JSON.parse(c[1].body).clientId);
        expect(order).toEqual(['first', 'second']);
    });
});

describe('併發與重試', () => {

    test('同時觸發兩次 flush 只會真的送一次——否則同一筆會被送兩遍', async () => {
        await enqueue();
        respondWith({ status: 200 });
        await Promise.all([queue.flush(), queue.flush()]);
        expect(global.fetch).toHaveBeenCalledTimes(1);
    });

    test('retryFailed 把 error 轉回 pending 並清掉舊的錯誤訊息', async () => {
        await enqueue();
        respondWith({ status: 422, body: { error: { message: '壞掉了' } } });
        await queue.flush();

        const count = await queue.retryFailed();
        expect(count).toBe(1);
        const [rec] = idb.records();
        expect(rec.status).toBe('pending');
        expect(rec.errorMsg).toBeNull();
    });

    test('clearSent 只清掉已送出的，pending 與 error 不動', async () => {
        await enqueue({ clientId: 'ok' });
        await enqueue({ clientId: 'bad' });
        respondWith({ status: 200 }, { status: 422 });
        await queue.flush();

        await queue.clearSent();
        expect(idb.records().map(r => r.clientId)).toEqual(['bad']);
    });
});

describe('廠別界線', () => {

    // 佇列裡沒有任何一個 body 帶廠別——廠別是 flush 當下由伺服端從 ActivePlantId 決定的。
    // 所以「離線排入 → 換了廠 → 恢復連線自動同步」會把這筆記到**別的廠**去，
    // 而使用者從頭到尾沒有做錯任何一步。這一組守的就是這條路徑。
    const setPlant = (id) => {
        if (id === null) sessionStorage.removeItem('mobileUser');
        else sessionStorage.setItem('mobileUser', JSON.stringify({ id: 'u1', plantId: id }));
    };

    beforeEach(() => {
        setPlant(null);
        // 訊息走 i18n（腳本不得寫死中文）。測試自己提供 tf，順便斷言參數有代進去。
        global.tf = (key, ...args) => `${key}|${args.join('|')}`;
    });

    afterEach(() => { setPlant(null); delete global.tf; });

    test('排入佇列時記下當下的廠別', async () => {
        setPlant('P1');
        await enqueue();
        expect(idb.records()[0].plantId).toBe('P1');
    });

    test('切了廠之後那筆不會被送出去，而且訊息說得出是哪兩個廠', async () => {
        setPlant('P1');
        await enqueue();

        setPlant('P2');
        respondWith({ status: 200 });
        const r = await queue.flush();

        // 最重要的一條：**根本沒有發出請求**。
        // 送出去賭伺服端會擋是不夠的——擋得住的那幾條路徑回的是 404，
        // 現場看到的是「同步壞了」，而不是「你切了廠」。
        expect(global.fetch).not.toHaveBeenCalled();
        expect(r).toMatchObject({ sent: 0, failed: 1 });

        const [rec] = idb.records();
        expect(rec.status).toBe('error');
        expect(rec.errorMsg).toBe('home.q_plant_mismatch|P1|P2');
    });

    test('一筆跨廠不會拖累排在它後面的同廠項目', async () => {
        // ⚠️ 跨廠的那筆必須排在**前面**，這支測試才有意義：
        // 它排最後的話，break 與 continue 的結果一模一樣（failed++ 在 break 之前），
        // 測試會兩種寫法都綠。這裡用 priority=1 讓它先被處理——
        // 而 priority 1 正是機台狀態，也正是真的會寫進錯廠的那支端點。
        setPlant('P2');
        await enqueue({ clientId: 'other-plant', priority: 1 });
        setPlant('P1');
        await enqueue({ clientId: 'same-plant', priority: 5 });

        respondWith({ status: 200 });
        const r = await queue.flush();

        // 擋下的那筆若用 break 而不是 continue，後面同廠的項目會一起卡住——
        // 症狀是「有一筆失敗，剩下的永遠不會送」，而且看不出兩者的關聯。
        expect(r).toMatchObject({ sent: 1, failed: 1 });
        expect(global.fetch).toHaveBeenCalledTimes(1);
        expect(JSON.parse(global.fetch.mock.calls[0][1].body).clientId).toBe('same-plant');
    });

    test('舊版排入的項目沒有廠別欄位，一律放行', async () => {
        // ⚠️ 這條是刻意的放行。升級前排進手機裡的項目沒有 plantId，
        // 把它們一律擋掉會讓使用者既有的待同步資料在升級後**全部變成失敗**，
        // 那比原本的問題更糟——而且他們完全不知道發生了什麼事。
        setPlant('P1');
        await enqueue();
        const rec = idb.records()[0];
        delete rec.plantId;              // 模擬舊版寫進去的記錄

        setPlant('P2');
        respondWith({ status: 200 });
        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 1, failed: 0 });
        expect(global.fetch).toHaveBeenCalledTimes(1);
    });

    test('讀不到當下廠別時放行，不把整個佇列鎖死', async () => {
        setPlant('P1');
        await enqueue();

        setPlant(null);                  // sessionStorage 被清（例如剛重開分頁）
        respondWith({ status: 200 });
        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 1, failed: 0 });
    });
});

describe('排入者界線', () => {

    // 佇列送出時帶的是**當下**的 Cookie。共用手機上 A 離線排了報工、交班後 B 登入，
    // 一連線就會把 A 的報工記在 B 名下——伺服端完全分不出來。
    // auth.js 讓離線冷啟動也認得人之後，這條路更容易走到。
    const signIn = (id, name = id) => {
        if (id === null) sessionStorage.removeItem('mobileUser');
        else sessionStorage.setItem('mobileUser', JSON.stringify({ id, name, plantId: 'P1' }));
    };

    beforeEach(() => {
        signIn(null);
        global.tf = (key, ...args) => `${key}|${args.join('|')}`;
    });

    afterEach(() => { signIn(null); delete global.tf; });

    test('排入時記下排入者', async () => {
        signIn('u-a', 'alice');
        await enqueue();
        expect(idb.records()[0]).toMatchObject({ userId: 'u-a', userName: 'alice' });
    });

    test('換人登入後不送出，訊息說得出是誰排的、現在是誰', async () => {
        signIn('u-a', 'alice');
        await enqueue();

        signIn('u-b', 'bob');
        respondWith({ status: 200 });
        const r = await queue.flush();

        expect(global.fetch).not.toHaveBeenCalled();
        expect(r).toMatchObject({ sent: 0, failed: 1 });
        const [rec] = idb.records();
        expect(rec.status).toBe('error');
        expect(rec.errorMsg).toBe('home.q_user_mismatch|alice|bob');
    });

    test('原排入者登回來後重試就送得出去', async () => {
        signIn('u-a', 'alice');
        await enqueue();
        signIn('u-b', 'bob');
        respondWith({ status: 200 });
        await queue.flush();

        signIn('u-a', 'alice');
        await queue.retryFailed();
        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 1, failed: 0 });
    });

    test('舊版排入的項目沒有排入者欄位，一律放行（理由同廠別）', async () => {
        signIn('u-a');
        await enqueue();
        delete idb.records()[0].userId;

        signIn('u-b');
        respondWith({ status: 200 });
        expect(await queue.flush()).toMatchObject({ sent: 1, failed: 0 });
    });
    test('getAllMine 只給本人的項目；別人的只給未送出筆數，不給內容', async () => {
        // 首頁佇列清單原本列出所有人的項目連同說明——換人登入就看得到前一個人報了什麼
        signIn('u-a');
        await enqueue({ clientId: 'a1', label: 'A 的報工' });
        await enqueue({ clientId: 'a2' });
        signIn('u-b');
        await enqueue({ clientId: 'b1', label: 'B 的報工' });
        idb.records().find(r => r.clientId === 'a2').status = 'sent';   // 已送出的不算

        const { mine, others } = await queue.getAllMine();

        expect(mine.map(r => r.clientId)).toEqual(['b1']);
        expect(others).toBe(1);
    });

    test('getAllMine 算出本人超過 24 小時仍未送出的筆數（已送出與別人的不算）', async () => {
        signIn('u-a');
        await enqueue({ clientId: 'old' });
        await enqueue({ clientId: 'oldSent' });
        await enqueue({ clientId: 'fresh' });
        signIn('u-b');
        await enqueue({ clientId: 'otherOld' });
        const dayAgo = Date.now() - 25 * 60 * 60 * 1000;
        for (const r of idb.records()) if (r.clientId !== 'fresh') r.enqueuedAt = dayAgo;
        idb.records().find(r => r.clientId === 'oldSent').status = 'sent';

        signIn('u-a');
        expect((await queue.getAllMine()).stale).toBe(1);
    });
});

describe('同步完成通知', () => {
    test('flush 結束時發出 offlinequeue:flushed，讓頁面在同步完之後才重抓資料', async () => {
        const seen = [];
        const listener = (e) => seen.push(e.detail);
        window.addEventListener('offlinequeue:flushed', listener);
        try {
            await enqueue();
            respondWith({ status: 200 });
            await queue.flush();
        } finally {
            window.removeEventListener('offlinequeue:flushed', listener);
        }
        expect(seen).toEqual([{ sent: 1, failed: 0, partial: 0, held: 0 }]);
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 斷線保護（2026-10-12，mobile-pwa.md §5.2）
// ════════════════════════════════════════════════════════════════════════════

/** 把某一筆的排入時間往前推（模擬「排隊了多久」）。 */
async function age(clientId, ms) {
    const rec = idb.records().find(r => r.clientId === clientId);
    await queue._put({ ...rec, enqueuedAt: Date.now() - ms });
}
const MIN = 60 * 1000;

describe('送出時帶手機當下時間，讓伺服器記發生時間', () => {
    test('每一筆都帶 X-Client-Sent-At，body 的 clientTimestamp 維持排入當下', async () => {
        await queue.enqueue({ url: '/api/mobile/reports', method: 'POST',
            body: { clientId: 'c1', clientTimestamp: '2026-10-12T01:00:00.000Z' } });
        respondWith({ status: 200 });
        await queue.flush();

        const [, init] = global.fetch.mock.calls[0];
        expect(Date.parse(init.headers['X-Client-Sent-At'])).toBeGreaterThan(Date.now() - 5000);
        expect(JSON.parse(init.body).clientTimestamp).toBe('2026-10-12T01:00:00.000Z');
    });
});

describe('排隊超過 30 分鐘：不自動送，等本人確認', () => {
    test('30 分鐘內照常送；超過的轉 held、不送出、也不算失敗', async () => {
        await enqueue({ clientId: 'fresh' });
        await enqueue({ clientId: 'old' });
        await age('fresh', 29 * MIN);
        await age('old', 31 * MIN);
        respondWith({ status: 200 });

        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 1, failed: 0, held: 1 });
        expect(global.fetch).toHaveBeenCalledTimes(1);
        expect(idb.records().find(x => x.clientId === 'old').status).toBe('held');
    });

    test('held 不會被下一輪自動補送撿回去', async () => {
        await enqueue({ clientId: 'old' });
        await age('old', 31 * MIN);
        respondWith({ status: 200 });
        await queue.flush();
        await queue.flush();

        expect(global.fetch).not.toHaveBeenCalled();
    });

    test('本人確認後照送，排隊再久也一樣（伺服器另有 24 小時上限）', async () => {
        await enqueue({ clientId: 'old' });
        await age('old', 3 * 60 * MIN);
        respondWith({ status: 200 });
        await queue.flush();

        expect(await queue.confirmHeld('old')).toBe(true);
        const r = await queue.flush();

        expect(r.sent).toBe(1);
        expect(idb.records()[0].status).toBe('sent');
    });

    test('重試失敗項目是本人的明確動作，視同確認——舊的 error 重試後不會又被擋成 held', async () => {
        await enqueue({ clientId: 'bad' });
        respondWith({ status: 422 });
        await queue.flush();
        await age('bad', 40 * MIN);

        await queue.retryFailed();
        respondWith({ status: 200 });
        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 1, held: 0 });
    });

    test('捨棄只限 held 與 error；pending 可能正在送，不能從底下抽掉', async () => {
        for (const [clientId, status] of [['old', 'held'], ['bad', 'error'], ['pend', 'pending'], ['done', 'sent']]) {
            await enqueue({ clientId });
            await queue._put({ ...idb.records().find(r => r.clientId === clientId), status });
        }

        expect(await queue.discard('old')).toBe(true);
        expect(await queue.discard('bad')).toBe(true);
        expect(await queue.discard('pend')).toBe(false);
        expect(await queue.discard('done')).toBe(false);
        expect(idb.records().map(r => r.clientId).sort()).toEqual(['done', 'pend']);
    });
});

describe('閘道錯誤（Tunnel 後面的伺服器停掉）', () => {
    test.each([502, 503, 504])('%i 留 pending 並停下，不標成要人手動重試的 error', async (status) => {
        await enqueue({ clientId: 'a' });
        await enqueue({ clientId: 'b' });
        respondWith({ status });

        const r = await queue.flush();

        expect(r).toMatchObject({ sent: 0, failed: 0 });
        expect(global.fetch).toHaveBeenCalledTimes(1);
        expect(idb.records().map(x => x.status)).toEqual(['pending', 'pending']);
    });
});

describe('flush 的回傳值', () => {
    test('另一個 flush 正在送時回全 0 的結果而不是 undefined——呼叫端一律解構', async () => {
        await enqueue();
        respondWith({ status: 200 });
        const [a, b] = await Promise.all([queue.flush(), queue.flush()]);

        expect([a, b]).toContainEqual({ sent: 0, failed: 0, partial: 0, held: 0, busy: true });
    });
});

describe('submitOrQueue：連得上直接送，連不上才排隊', () => {
    const NET_FAILED = Symbol('apiNetworkFailed');
    beforeEach(() => {
        global.API_NETWORK_FAILED = NET_FAILED;
        global.apiCall = jest.fn();
    });
    afterEach(() => { delete global.apiCall; delete global.API_NETWORK_FAILED; });

    test('伺服器有回應：照原樣交回，不排隊', async () => {
        apiCall.mockResolvedValue({ success: true });

        const r = await submitOrQueue({ url: '/api/mobile/reports', body: { qtyOk: 1 } });

        expect(r).toEqual({ queued: false, result: { success: true } });
        expect(idb.records()).toHaveLength(0);
    });

    test('被拒（apiCall 回 null）也不排隊——那是被拒，不是沒送到', async () => {
        apiCall.mockResolvedValue(null);

        const r = await submitOrQueue({ url: '/api/mobile/reports', body: { qtyOk: 1 } });

        expect(r.queued).toBe(false);
        expect(idb.records()).toHaveLength(0);
    });

    test('顯示連線中但連不到伺服器：排進佇列，而且沿用第一次送出時的 clientId——回應在路上斷掉時伺服器才認得是同一筆', async () => {
        apiCall.mockResolvedValue(NET_FAILED);

        const r = await submitOrQueue({ url: '/api/warehouse/put-away', body: { qty: 3 }, priority: 3, label: 'x' });

        expect(r.queued).toBe(true);
        const sentBody = apiCall.mock.calls[0][2];
        expect(sentBody.clientId).toBeTruthy();
        expect(sentBody.clientTimestamp).toBeTruthy();
        const [rec] = idb.records();
        expect(rec.clientId).toBe(sentBody.clientId);
        expect(rec.body.clientTimestamp).toBe(sentBody.clientTimestamp);
        expect(rec.priority).toBe(3);
    });

    test('手機自己知道沒網路：不試送，直接排隊', async () => {
        setOnline(false);

        const r = await submitOrQueue({ url: '/api/mobile/reports', body: { qtyOk: 1 } });

        expect(r.queued).toBe(true);
        expect(apiCall).not.toHaveBeenCalled();
    });

    test('簽核仍然不得排隊：連不上時擲出，而不是靜靜排進去', async () => {
        apiCall.mockResolvedValue(NET_FAILED);

        await expect(submitOrQueue({ url: '/api/mobile/approval/R1/decide', body: {} })).rejects.toThrow();
    });
});
