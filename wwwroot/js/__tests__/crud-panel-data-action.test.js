'use strict';

/**
 * crud-panel.js × data-action 相容層測試（P2 CSP 回補 B0）
 *
 * 測試目標：驗證共用 helper 同時相容「舊式 inline 位置參數」與「新式 dispatcher
 * (event, el) 讀 dataset」兩種呼叫慣例——確保存量 inline handler 未遷移期間
 * 仍可運作，且新畫面的 data-action 委派亦正確帶參。
 *
 *   1. 舊式（位置參數）：openPanel(url,label) / submitPanel(form,submitter) /
 *      deleteItem(url,id) / submitApproval(id,ctrl) 行為不變
 *   2. 新式（event, el）：自 el.dataset 讀 url/label/id/controller
 *   3. openPanel 新式 label 缺省時退回元素文字
 *   4. 與 data-action.js dispatcher 整合：data-action 觸發後正確帶 dataset
 */

// 受測腳本一律用 require 載入，不用 readFileSync + eval：eval 進來的程式碼不會經過
// jest 的轉換管線，istanbul 插不了樁，覆蓋率會恆為 0%。這些是瀏覽器腳本、沒有
// module.exports，但公開函式都掛在 window 上，require 執行完同樣拿得到。

let mockToastShow, mockOffcanvasHide, mockOffcanvasShow, originalConsoleError;

function setupDom() {
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
        <div id="successToast"></div>
        <input name="__RequestVerificationToken" value="csrf-token" />
    `;

    mockToastShow = jest.fn();
    mockOffcanvasHide = jest.fn();
    mockOffcanvasShow = jest.fn();

    window.bootstrap = {
        Offcanvas: jest.fn().mockImplementation(() => ({ show: mockOffcanvasShow, hide: mockOffcanvasHide })),
        Toast: jest.fn().mockImplementation(() => ({ show: mockToastShow })),
        Modal:   { getInstance: jest.fn().mockReturnValue(null) },
        Tooltip: { getInstance: jest.fn().mockReturnValue(null) },
    };

    global.confirm = jest.fn(() => true);
    global.alert = jest.fn();

    // 每個 test 都要全新的 IIFE 閉包狀態（offcanvas/toast 快取），故先清 registry。
    jest.resetModules();
    require('../crud-panel.js');
}

function teardownDom() {
    console.error = originalConsoleError;
    document.body.innerHTML = '';
}

function mockFetchText(html) {
    global.fetch = jest.fn(() => Promise.resolve({ ok: true, text: () => Promise.resolve(html) }));
}
function mockFetchJson(obj) {
    global.fetch = jest.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve(obj) }));
}

// ════════════════════════════════════════════════════════════════════════════
// 1. 舊式（位置參數）— 存量 inline handler 未遷移期間仍須運作
// ════════════════════════════════════════════════════════════════════════════
describe('舊式位置參數（向後相容）', () => {
    beforeEach(setupDom);
    afterEach(teardownDom);

    test('openPanel(url, label)：設定標題並以 url 發出 fetch', () => {
        mockFetchText('<div>ok</div>');
        window.openPanel('/Foo/Create', '建立');
        expect(document.getElementById('sidePanelLabel').textContent).toBe('建立');
        expect(global.fetch).toHaveBeenCalledWith('/Foo/Create', expect.any(Object));
    });

    test('submitPanel(form, submitter)：回傳 false 阻止原生提交', () => {
        mockFetchText('{}');
        const form = Object.assign(document.createElement('form'), { action: 'http://localhost/Foo/Save' });
        expect(window.submitPanel(form, null)).toBe(false);
    });

    test('deleteItem(url, id)：confirm 後以 url/id 發出 POST', () => {
        mockFetchJson({ success: true });
        window.deleteItem('/Foo/Delete', 'ID1');
        expect(global.fetch).toHaveBeenCalledWith('/Foo/Delete/ID1', expect.objectContaining({ method: 'POST' }));
    });

    test('submitApproval(id, controller)：POST 至 /Controller/Submit/id', () => {
        mockFetchJson({ success: true });
        window.submitApproval('DOC9', 'PurchaseOrder');
        expect(global.fetch).toHaveBeenCalledWith('/PurchaseOrder/Submit/DOC9', expect.objectContaining({ method: 'POST' }));
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 2. 新式（event, el）— dispatcher 呼叫慣例，自 el.dataset 讀參
// ════════════════════════════════════════════════════════════════════════════
describe('新式 (event, el) 讀 dataset', () => {
    beforeEach(setupDom);
    afterEach(teardownDom);

    function elWith(attrs, text = '') {
        const b = document.createElement('button');
        Object.entries(attrs).forEach(([k, v]) => b.setAttribute(k, v));
        b.textContent = text;
        return b;
    }

    test('openPanel：自 data-url / data-label 取參', () => {
        mockFetchText('<div>ok</div>');
        const el = elWith({ 'data-url': '/Bar/Edit/7', 'data-label': '編輯' });
        window.openPanel(new Event('click'), el);
        expect(document.getElementById('sidePanelLabel').textContent).toBe('編輯');
        expect(global.fetch).toHaveBeenCalledWith('/Bar/Edit/7', expect.any(Object));
    });

    test('openPanel：data-label 缺省時退回元素文字', () => {
        mockFetchText('<div>ok</div>');
        const el = elWith({ 'data-url': '/Bar/Create' }, '新增 Bar');
        window.openPanel(new Event('click'), el);
        expect(document.getElementById('sidePanelLabel').textContent).toBe('新增 Bar');
    });

    test('deleteItem：自 data-url / data-id 取參', () => {
        mockFetchJson({ success: true });
        const el = elWith({ 'data-url': '/Bar/Delete', 'data-id': 'K42' });
        window.deleteItem(new Event('click'), el);
        expect(global.confirm).toHaveBeenCalled();
        expect(global.fetch).toHaveBeenCalledWith('/Bar/Delete/K42', expect.objectContaining({ method: 'POST' }));
    });

    test('submitApproval / withdrawApproval：自 data-id / data-controller 取參', () => {
        mockFetchJson({ success: true });
        const el1 = elWith({ 'data-id': 'D1', 'data-controller': 'SalesOrder' });
        window.submitApproval(new Event('click'), el1);
        expect(global.fetch).toHaveBeenCalledWith('/SalesOrder/Submit/D1', expect.objectContaining({ method: 'POST' }));

        global.fetch.mockClear();
        const el2 = elWith({ 'data-id': 'D2', 'data-controller': 'SalesOrder' });
        window.withdrawApproval(new Event('click'), el2);
        expect(global.fetch).toHaveBeenCalledWith('/SalesOrder/Withdraw/D2', expect.objectContaining({ method: 'POST' }));
    });

    test('submitPanel：el 即 form、submitter 取自 event.submitter，回傳 false', () => {
        mockFetchText('{}');
        const form = Object.assign(document.createElement('form'), { action: 'http://localhost/Bar/Save' });
        const ev = new Event('submit');
        Object.defineProperty(ev, 'submitter', { value: null });
        expect(window.submitPanel(ev, form)).toBe(false);
        expect(global.fetch).toHaveBeenCalledWith('http://localhost/Bar/Save', expect.objectContaining({ method: 'POST' }));
    });
});

// ════════════════════════════════════════════════════════════════════════════
// 3. 與 data-action.js dispatcher 整合（端到端：click → dispatcher → helper）
// ════════════════════════════════════════════════════════════════════════════
describe('dispatcher 整合', () => {
    beforeEach(() => {
        setupDom();
        require('../data-action.js');  // 註冊 document 層級委派
    });
    afterEach(teardownDom);

    test('data-action="openPanel" 的按鈕 click → 以 data-url 發出 fetch', () => {
        mockFetchText('<div>ok</div>');
        document.getElementById('sidePanelBody').innerHTML =
            '<button id="go" data-action="openPanel" data-url="/Baz/Create" data-label="新增">+</button>';
        document.getElementById('go').click();
        expect(global.fetch).toHaveBeenCalledWith('/Baz/Create', expect.any(Object));
        expect(document.getElementById('sidePanelLabel').textContent).toBe('新增');
    });

    test('<a href="#" data-action="openPanel"> click → 阻止預設跳頂（dispatcher preventDefault）', () => {
        // 列表單號連結樣態（B1）：原 inline 為 openPanel(...); return false;
        // 改 data-action 後須靠 openPanel 回傳 false 讓 dispatcher 代為 preventDefault。
        mockFetchText('<div>ok</div>');
        document.getElementById('sidePanelBody').innerHTML =
            '<a id="lnk" href="#" data-action="openPanel" data-url="/Baz/Details/9" data-label="9">9</a>';
        const ev = new Event('click', { bubbles: true, cancelable: true });
        document.getElementById('lnk').dispatchEvent(ev);
        expect(global.fetch).toHaveBeenCalledWith('/Baz/Details/9', expect.any(Object));
        expect(ev.defaultPrevented).toBe(true);
    });

    test('data-action="printPage" 的按鈕 click → 呼叫 window.print（B1b 列印鈕）', () => {
        window.print = jest.fn();
        document.getElementById('sidePanelBody').innerHTML =
            '<button id="pr" data-action="printPage">列印</button>';
        document.getElementById('pr').click();
        expect(window.print).toHaveBeenCalled();
    });
});
