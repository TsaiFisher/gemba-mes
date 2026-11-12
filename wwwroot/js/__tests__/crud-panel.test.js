'use strict';

/**
 * crud-panel.js 單元測試
 *
 * 測試目標：覆蓋本次 session 中暴露的三類 JS bug
 *   1. closeSidePanel 未定義 → 按鈕失效
 *   2. submitPanel 未 return false → 雙重提交
 *   3. submitPanel 不認 JSON 回應 → 黑螢幕 / 操作無回饋
 *
 * jsdom 限制說明：
 *   jsdom 不實作 location.reload()，但 refreshList() 在呼叫 reload 前
 *   先呼叫 offcanvas().hide()，因此 mockOffcanvasHide 被呼叫等價於
 *   refreshList() 已觸發，可作為「成功後重載列表」的代理指標。
 */

// 受測腳本一律用 require 載入，不用 readFileSync + eval：eval 進來的程式碼不會經過
// jest 的轉換管線，istanbul 插不了樁，覆蓋率會恆為 0%。這些是瀏覽器腳本、沒有
// module.exports，但公開函式都掛在 window 上，require 執行完同樣拿得到。

// ── 共用狀態 ─────────────────────────────────────────────────────────────────
let mockToastShow, mockOffcanvasHide, mockOffcanvasShow;
let originalConsoleError;

function setupDom() {
    // 抑制 jsdom navigation "not implemented" 雜訊
    originalConsoleError = console.error;
    console.error = (...args) => {
        const msg = args[0];
        if (msg instanceof Error && msg.type === 'not implemented') return;
        if (typeof msg === 'string' && msg.includes('not implemented')) return;
        originalConsoleError(...args);
    };

    document.body.innerHTML = `
        <div id="sidePanel">
            <h5 id="sidePanelLabel"></h5>
            <div id="sidePanelBody"></div>
        </div>
        <div id="successToast"><span id="successToastMsg"></span></div>
        <input name="__RequestVerificationToken" value="csrf-token" />
    `;

    mockToastShow     = jest.fn();
    mockOffcanvasHide = jest.fn();
    mockOffcanvasShow = jest.fn();

    // Bootstrap stub
    window.bootstrap = {
        Offcanvas: jest.fn().mockImplementation(() => ({
            show: mockOffcanvasShow,
            hide: mockOffcanvasHide,
        })),
        Toast: jest.fn().mockImplementation(() => ({
            show: mockToastShow,
        })),
        Modal:   { getInstance: jest.fn().mockReturnValue(null) },
        Tooltip: { getInstance: jest.fn().mockReturnValue(null) },
    };

    // crud-panel.js 的 IIFE 會快取 offcanvas/toast 實例，每個 test 都要拿到全新的
    // bootstrap mock，故先清 module registry 再 require（等同原本每次重新執行腳本）。
    jest.resetModules();
    require('../crud-panel.js');
}

function teardownDom() {
    console.error = originalConsoleError;
}

function makeForm(action = 'http://localhost/Test/Save') {
    return Object.assign(document.createElement('form'), { action });
}

function mockFetch(responseText) {
    global.fetch = jest.fn(() =>
        Promise.resolve({ text: () => Promise.resolve(responseText) })
    );
}

// ════════════════════════════════════════════════════════════════════════════
// 1. Export 完整性
// ════════════════════════════════════════════════════════════════════════════
describe('exports', () => {
    beforeEach(setupDom);
    afterEach(teardownDom);

    const required = [
        'openPanel', 'submitPanel', 'hidePanel', 'closeSidePanel',
        'refreshList', 'toastSuccess', 'deleteItem',
        'submitApproval', 'withdrawApproval',
        'armValidation', 'execScripts',
    ];

    required.forEach(name => {
        test(`window.${name} 已 export`, () => {
            expect(typeof window[name]).toBe('function');
        });
    });

    test('closeSidePanel 是 hidePanel 的 alias（同一函式參考）', () => {
        expect(window.closeSidePanel).toBe(window.hidePanel);
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 2. submitPanel — return false 防止原生 form submit
// ════════════════════════════════════════════════════════════════════════════
describe('submitPanel — return false', () => {
    beforeEach(setupDom);
    afterEach(teardownDom);

    test('submitPanel 回傳 false，阻止瀏覽器原生提交', () => {
        global.fetch = jest.fn(() =>
            Promise.resolve({ text: () => Promise.resolve('{}') })
        );
        const result = window.submitPanel(makeForm(), null);
        expect(result).toBe(false);
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 3. submitPanel — JSON 回應格式
//    成功代理：refreshList() → offcanvas().hide() → mockOffcanvasHide 被呼叫
// ════════════════════════════════════════════════════════════════════════════
describe('submitPanel — JSON 回應', () => {
    beforeEach(() => {
        setupDom();
        global.alert = jest.fn();
    });
    afterEach(teardownDom);

    test('JSON success=true → toast + offcanvas 關閉（refreshList 觸發）', async () => {
        mockFetch('{"success":true,"message":"儲存成功。"}');
        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 20));

        expect(mockToastShow).toHaveBeenCalledTimes(1);
        expect(mockOffcanvasHide).toHaveBeenCalledTimes(1);
        expect(global.alert).not.toHaveBeenCalled();
    });

    test('JSON success=false → alert 顯示 message，不關閉面板', async () => {
        mockFetch('{"success":false,"message":"此單據已不存在。"}');
        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 20));

        expect(global.alert).toHaveBeenCalledWith('此單據已不存在。');
        expect(mockOffcanvasHide).not.toHaveBeenCalled();
        expect(mockToastShow).not.toHaveBeenCalled();
    });

    test('JSON success=false 無 message → alert fallback，不 crash', async () => {
        mockFetch('{"success":false}');
        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 20));

        expect(global.alert).toHaveBeenCalled();
        expect(mockOffcanvasHide).not.toHaveBeenCalled();
    });

    test('<script> 格式含 refreshList → toast + offcanvas 關閉', async () => {
        mockFetch('<script>refreshList&&refreshList();</scr' + 'ipt>');
        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 20));

        expect(mockToastShow).toHaveBeenCalledTimes(1);
        expect(mockOffcanvasHide).toHaveBeenCalledTimes(1);
    });

    test('HTML partial（驗證失敗）→ 注入 sidePanelBody，不 toast，不關閉', async () => {
        mockFetch('<form id="sideForm"><div class="text-danger">代碼必填</div></form>');
        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 20));

        expect(mockToastShow).not.toHaveBeenCalled();
        expect(mockOffcanvasHide).not.toHaveBeenCalled();
        expect(document.getElementById('sidePanelBody').innerHTML)
            .toContain('代碼必填');
    });

    test('空白開頭 JSON（success=true）正確解析', async () => {
        mockFetch('  \n  {"success":true,"message":"OK"}');
        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 20));

        expect(mockToastShow).toHaveBeenCalledTimes(1);
        expect(mockOffcanvasHide).toHaveBeenCalledTimes(1);
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 4. openPanel
// ════════════════════════════════════════════════════════════════════════════
describe('openPanel', () => {
    beforeEach(setupDom);
    afterEach(teardownDom);

    test('openPanel 將 label 寫入 #sidePanelLabel', () => {
        global.fetch = jest.fn(() =>
            Promise.resolve({
                ok: true,
                text: () => Promise.resolve('<form></form>'),
            })
        );
        window.openPanel('/Test/Create', '新增測試');
        expect(document.getElementById('sidePanelLabel').textContent).toBe('新增測試');
    });

    test('openPanel 404 → 顯示 alert-warning 區塊', async () => {
        global.fetch = jest.fn(() =>
            Promise.resolve({ ok: false, status: 404 })
        );
        window.openPanel('/Test/NotExist', '測試');
        await new Promise(r => setTimeout(r, 20));

        const body = document.getElementById('sidePanelBody');
        expect(body.innerHTML).toContain('alert-warning');
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 5. deleteItem
// ════════════════════════════════════════════════════════════════════════════
describe('deleteItem', () => {
    beforeEach(() => {
        setupDom();
        global.alert = jest.fn();
    });
    afterEach(teardownDom);

    test('confirm=false → 不發 fetch', () => {
        global.confirm = jest.fn().mockReturnValue(false);
        global.fetch   = jest.fn();
        window.deleteItem('/Test/Delete', 'ID001');
        expect(global.fetch).not.toHaveBeenCalled();
    });

    test('confirm=true + success=true → toast 顯示', async () => {
        global.confirm = jest.fn().mockReturnValue(true);
        global.fetch   = jest.fn(() =>
            Promise.resolve({
                json: () => Promise.resolve({ success: true }),
            })
        );
        window.deleteItem('/Test/Delete', 'ID001');
        await new Promise(r => setTimeout(r, 20));
        expect(mockToastShow).toHaveBeenCalledTimes(1);
    });

    test('confirm=true + success=false → alert message', async () => {
        global.confirm = jest.fn().mockReturnValue(true);
        global.fetch   = jest.fn(() =>
            Promise.resolve({
                json: () => Promise.resolve({ success: false, message: '無法刪除' }),
            })
        );
        window.deleteItem('/Test/Delete', 'ID001');
        await new Promise(r => setTimeout(r, 20));
        expect(global.alert).toHaveBeenCalledWith('無法刪除');
    });

    test('data-action 雙呼叫帶 data-confirm → 用自訂訊息詢問（取代第 3 參數 confirm 訊息）', () => {
        global.confirm = jest.fn().mockReturnValue(false);
        global.fetch   = jest.fn();
        document.body.insertAdjacentHTML('beforeend',
            '<button data-url="/Test/Delete" data-id="ID001" data-confirm="確定刪除序號？">刪除</button>');
        const btn = document.querySelector('[data-confirm]');
        // 模擬 data-action.js dispatcher：以 (event, el) 呼叫
        window.deleteItem(new Event('click'), btn);
        expect(global.confirm).toHaveBeenCalledWith('確定刪除序號？');
        expect(global.fetch).not.toHaveBeenCalled();   // confirm=false → 不送出
    });

    test('舊式第三參數 confirmMsg 仍相容', () => {
        global.confirm = jest.fn().mockReturnValue(false);
        global.fetch   = jest.fn();
        window.deleteItem('/Test/Delete', 'ID001', '自訂訊息');
        expect(global.confirm).toHaveBeenCalledWith('自訂訊息');
    });
});

// ════════════════════════════════════════════════════════════════════════════
// N. toastSuccess — 訊息必須真的進到 span
//
// 這一組防的是 2026-09-14 Wave R 撞到的缺陷：toastSuccess 的簽章是無參數的，
// 呼叫端傳進來的訊息被靜默丟棄，於是 toast 彈出來、勾勾在、文字是空的。
// 全站 6 個呼叫端這樣傳（工單放行、批次、報表排程立即執行），沒有任何錯誤訊號。
//
// ⚠️ 同時它是 data-action dispatcher 的目標，dispatcher 以 (event, element) 呼叫，
//    所以「非字串一律當沒有訊息」這半邊也必須釘住——只驗字串那半邊的話，
//    把判斷寫成 String(msg) 會讓每個 dispatcher 觸發的 toast 顯示 "[object Event]"。
// ════════════════════════════════════════════════════════════════════════════
describe('toastSuccess — 訊息', () => {
    beforeEach(setupDom);
    afterEach(teardownDom);

    const msgEl = () => document.getElementById('successToastMsg');

    test('傳字串時寫進 #successToastMsg 並顯示 toast', () => {
        window.toastSuccess('已排入立即執行');

        expect(msgEl().textContent).toBe('已排入立即執行');
        expect(mockToastShow).toHaveBeenCalled();
    });

    test('不傳參數時不顯示任何文字，但 toast 照常彈出', () => {
        window.toastSuccess();

        expect(msgEl().textContent).toBe('');
        expect(mockToastShow).toHaveBeenCalled();
    });

    test('dispatcher 以 (event, element) 呼叫時不得把 Event 印出來', () => {
        // data-action.js 的呼叫形狀。寫成 String(msg) 會在這裡顯示 "[object Event]"。
        window.toastSuccess(new Event('click'), document.createElement('button'));

        expect(msgEl().textContent).toBe('');
        expect(mockToastShow).toHaveBeenCalled();
    });

    test('前一次的訊息不會殘留到下一次無訊息的呼叫', () => {
        window.toastSuccess('第一次的訊息');
        expect(msgEl().textContent).toBe('第一次的訊息');

        window.toastSuccess();
        expect(msgEl().textContent).toBe('');
    });

    test('span 不存在時不拋例外（未載入 toast 標記的畫面）', () => {
        document.getElementById('successToastMsg').remove();

        expect(() => window.toastSuccess('訊息')).not.toThrow();
        expect(mockToastShow).toHaveBeenCalled();
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 跨廠擋下（409 PLANT_CONTEXT_CHANGED）→ 可切回原廠別並照原內容重送（2026-11-11 驗收 C3）
// 原本只能照訊息重新整理，面板關閉、填的內容全部消失。
// ════════════════════════════════════════════════════════════════════════════
describe('submitPanel — 跨廠擋下切回原廠別', () => {
    const blocked = JSON.stringify({ success: false, message: '畫面在 P1 開啟', code: 'PLANT_CONTEXT_CHANGED', renderedPlant: 'P1' });
    const ok      = '{"success":true}';

    beforeEach(() => {
        setupDom();
        global.alert = jest.fn();
    });
    afterEach(teardownDom);

    function fetchSequence(...bodies) {
        const calls = [];
        global.fetch = jest.fn((url, opts) => {
            calls.push({ url, opts });
            const body = bodies.shift() ?? ok;
            return Promise.resolve({ text: () => Promise.resolve(body) });
        });
        return calls;
    }

    test('同意 → 先切回畫面開啟時的廠別，再照原內容重送一次，成功關閉面板', async () => {
        global.confirm = jest.fn(() => true);
        const calls = fetchSequence(blocked, '', ok);
        const form = makeForm();
        form.appendChild(Object.assign(document.createElement('input'), { name: 'Code', value: 'ACC6-W1' }));

        window.submitPanel(form, null);
        await new Promise(r => setTimeout(r, 30));

        expect(calls.map(c => c.url)).toEqual(['http://localhost/Test/Save', '/UserSettings/SwitchPlant', 'http://localhost/Test/Save']);
        expect(calls[1].opts.body.get('plantId')).toBe('P1');          // 切回的是開啟時的廠，不是目前的廠
        expect(calls[2].opts.body.get('Code')).toBe('ACC6-W1');        // 原內容照送
        expect(mockOffcanvasHide).toHaveBeenCalledTimes(1);
        expect(global.alert).not.toHaveBeenCalled();
    });

    test('取消 → 不切換、不重送、面板保留', async () => {
        global.confirm = jest.fn(() => false);
        const calls = fetchSequence(blocked);

        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 30));

        expect(calls).toHaveLength(1);
        expect(mockOffcanvasHide).not.toHaveBeenCalled();
    });

    test('切回後仍被擋 → 只重試一次，顯示訊息停下', async () => {
        global.confirm = jest.fn(() => true);
        const calls = fetchSequence(blocked, '', blocked);

        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 30));

        expect(calls).toHaveLength(3);
        expect(global.confirm).toHaveBeenCalledTimes(1);
        expect(global.alert).toHaveBeenCalledWith('畫面在 P1 開啟');
    });

    test('缺快照（PLANT_CONTEXT_MISSING）不提供切回，照舊顯示訊息', async () => {
        global.confirm = jest.fn(() => true);
        fetchSequence(JSON.stringify({ success: false, message: '缺少廠別', code: 'PLANT_CONTEXT_MISSING' }));

        window.submitPanel(makeForm(), null);
        await new Promise(r => setTimeout(r, 30));

        expect(global.confirm).not.toHaveBeenCalled();
        expect(global.alert).toHaveBeenCalledWith('缺少廠別');
    });
});
