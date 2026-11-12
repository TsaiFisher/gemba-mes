'use strict';

/**
 * data-action.js 單元測試
 *
 * 測試目標：驗證全域事件委派 dispatcher 取代 inline on*= 的等義行為
 *   1. data-action 預設綁 click，呼叫全域函式並傳入 (event, el)、this === el
 *   2. data-action-change 只在 change 觸發；事件專屬屬性與裸 data-action 互不干擾
 *   3. 函式回傳 false → dispatcher 代呼 preventDefault（對齊 inline "return false;"）
 *   4. 委派於 document 層級 → 後插入的動態節點無需重綁即生效
 *   5. 參數改由 el.dataset 讀取（取代 onclick="fn('arg')"）
 *   6. 找不到全域函式時警告但不丟例外
 */

// 受測腳本一律用 require 載入，不用 readFileSync + eval：eval 進來的程式碼不會經過
// jest 的轉換管線，istanbul 插不了樁，覆蓋率會恆為 0%。這些是瀏覽器腳本、沒有
// module.exports，但公開函式都掛在 window 上，require 執行完同樣拿得到。
//
// dispatcher 於 document 層級註冊監聽器；真實頁面只載入一次。
// jsdom 的 document 跨 test 持續，故只載入一次即可（多次會疊加監聽器，非真實情境）。
beforeAll(() => {
    require('../data-action.js');
});

afterEach(() => {
    document.body.innerHTML = '';
    // 清掉測試掛上的全域函式，避免互相污染
    ['fooAction', 'changeAction', 'submitAction', 'argAction', 'focusAction'].forEach(n => { delete window[n]; });
    jest.restoreAllMocks();
});

describe('data-action 委派 dispatcher', () => {

    test('data-action 預設綁 click，傳入 (event, el) 且 this === el', () => {
        let received = null;
        window.fooAction = function (e, el) {
            received = { type: e.type, el: el, thisIsEl: this === el };
        };
        document.body.innerHTML = '<button id="b" data-action="fooAction">Go</button>';
        document.getElementById('b').click();

        expect(received).not.toBeNull();
        expect(received.type).toBe('click');
        expect(received.el.id).toBe('b');
        expect(received.thisIsEl).toBe(true);
    });

    test('data-action-change 只在 change 觸發，不被 click 誤呼', () => {
        window.changeAction = jest.fn();
        document.body.innerHTML =
            '<select id="s" data-action-change="changeAction"><option>a</option></select>';
        const sel = document.getElementById('s');

        sel.click();
        expect(window.changeAction).not.toHaveBeenCalled();

        sel.dispatchEvent(new Event('change', { bubbles: true }));
        expect(window.changeAction).toHaveBeenCalledTimes(1);
    });

    test('回傳 false → dispatcher 代呼 preventDefault（對齊 inline return false）', () => {
        window.submitAction = function () { return false; };
        document.body.innerHTML = '<form id="f" data-action-submit="submitAction"></form>';

        const evt = new Event('submit', { bubbles: true, cancelable: true });
        document.getElementById('f').dispatchEvent(evt);
        expect(evt.defaultPrevented).toBe(true);
    });

    test('委派於 document 層級 → 動態插入節點無需重綁即生效', () => {
        window.fooAction = jest.fn();
        // 載入後才插入的節點
        const div = document.createElement('div');
        div.innerHTML = '<button id="late" data-action="fooAction">Late</button>';
        document.body.appendChild(div);

        document.getElementById('late').click();
        expect(window.fooAction).toHaveBeenCalledTimes(1);
    });

    test('參數改由 el.dataset 讀取（取代 onclick="fn(\'arg\')"）', () => {
        let url = null;
        window.argAction = function (e, el) { url = el.dataset.url; };
        document.body.innerHTML =
            '<a id="a" data-action="argAction" data-url="/X/Create">open</a>';
        document.getElementById('a').click();
        expect(url).toBe('/X/Create');
    });

    test('找不到全域函式時警告但不丟例外', () => {
        const warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
        document.body.innerHTML = '<button id="x" data-action="noSuchFn">x</button>';
        expect(() => document.getElementById('x').click()).not.toThrow();
        expect(warn).toHaveBeenCalled();
    });

    test('bindDataActions 為相容用 no-op，存在且可呼叫', () => {
        expect(typeof window.bindDataActions).toBe('function');
        expect(() => window.bindDataActions()).not.toThrow();
    });

    test('data-action-focus 於 capture 階段觸發（focus 不冒泡，取代 onfocus=）', () => {
        let received = null;
        window.focusAction = function (e, el) { received = { type: e.type, id: el.id }; };
        document.body.innerHTML =
            '<input id="q" data-action-focus="focusAction" value="0" max="9" />';
        document.getElementById('q').dispatchEvent(new Event('focus'));  // focus 不 bubble
        expect(received).toEqual({ type: 'focus', id: 'q' });
    });

    test('selectMaxIfZero：值為 0 時帶入 max（取代數量欄 onfocus 自動全選片段）', () => {
        document.body.innerHTML = '<input id="z" value="0" max="7" />';
        const zero = document.getElementById('z');
        zero.select = jest.fn();          // jsdom 對 number/text select 行為有限，僅驗證副作用
        window.selectMaxIfZero(new Event('focus'), zero);
        expect(zero.value).toBe('7');
        expect(zero.select).toHaveBeenCalled();

        document.body.innerHTML = '<input id="nz" value="3" max="7" />';
        const nonZero = document.getElementById('nz');
        window.selectMaxIfZero(new Event('focus'), nonZero);
        expect(nonZero.value).toBe('3');  // 非 0 不變
    });

    test('clearTarget：清空 data-target 指定元素的 innerHTML（取代 onclick="getElementById(X).innerHTML=\'\'"）', () => {
        document.body.innerHTML =
            '<div id="formArea"><input value="x" /></div>' +
            '<button data-action="clearTarget" data-target="formArea">取消</button>';
        document.querySelector('button').dispatchEvent(new Event('click', { bubbles: true }));
        expect(document.getElementById('formArea').innerHTML).toBe('');
    });

    test('clearTarget：目標不存在時不丟例外', () => {
        document.body.innerHTML = '<button data-action="clearTarget" data-target="missing">x</button>';
        expect(() =>
            document.querySelector('button').dispatchEvent(new Event('click', { bubbles: true }))
        ).not.toThrow();
    });
});
