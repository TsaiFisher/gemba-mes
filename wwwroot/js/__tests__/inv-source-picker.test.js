'use strict';

/**
 * inv-source-picker.js · fillSourceLine 守門
 *
 * 回補背景：INV _Form 的明細品號 onchange 已從 inline `onchange="onProductChange(this)"`
 * 改為 data-action 委派（document 層級 addEventListener('change')）。fillSourceLine 在
 * 使用者選來源單列後，需「程式性」變更品號並讓 onProductChange 跑（帶出價格/UOM）。
 *
 * 關鍵不變式：jQuery 的 `.trigger('change')` **不會**到達 document 原生委派（只到 jQuery
 * handler）。因此即使 select2 已載入（走 jQuery 同步顯示分支），fillSourceLine 仍必須額外
 * 派發一個 `bubbles:true` 的原生 change，否則回補後 onProductChange 失聯（價格/UOM 不帶入）。
 */

// 受測腳本一律用 require 載入，不用 readFileSync + eval：eval 進來的程式碼不會經過
// jest 的轉換管線，istanbul 插不了樁，覆蓋率會恆為 0%。這些是瀏覽器腳本、沒有
// module.exports，但公開函式都掛在 window 上，require 執行完同樣拿得到。
beforeAll(() => {
    require('../inv-source-picker.js');  // 定義 window.fillSourceLine 等
});

afterEach(() => {
    document.body.innerHTML = '';
    delete window.jQuery;
    jest.restoreAllMocks();
});

function buildRow() {
    document.body.innerHTML = `
        <table><tbody><tr id="row">
            <td><select class="product-select"><option value="">--</option><option value="P001">P001</option></select></td>
            <td><input class="line-source-id" /><input class="line-source-line" /></td>
            <td><input class="line-qty" /></td>
        </tr></tbody></table>`;
    return document.getElementById('row');
}

test('fillSourceLine 派發冒泡原生 change → 到達 document 委派（無 select2）', () => {
    const tr = buildRow();
    let docHits = 0;
    const onChange = () => docHits++;
    document.addEventListener('change', onChange);

    window.fillSourceLine(tr, { docNo: 'SO-1', lineNo: 3, productCode: 'P001', remaining: 7 });

    document.removeEventListener('change', onChange);
    expect(tr.querySelector('.product-select').value).toBe('P001');
    expect(tr.querySelector('.line-source-id').value).toBe('SO-1');
    expect(tr.querySelector('.line-source-line').value).toBe('3');
    expect(tr.querySelector('.line-qty').value).toBe('7');
    expect(docHits).toBe(1); // onProductChange 委派有被觸發
});

test('即使 select2 已載入（走 jQuery 分支），仍派發原生 change 到達委派（回歸守門）', () => {
    const tr = buildRow();

    // 模擬已載入的 jQuery + select2：jQuery(el).val().trigger() 只到 jQuery handler，不到 document 委派
    const triggerSpy = jest.fn();
    const jqObj = { val: function () { return this; }, trigger: triggerSpy };
    window.jQuery = function () { return jqObj; };
    window.jQuery.fn = { select2: {} };

    let docHits = 0;
    const onChange = () => docHits++;
    document.addEventListener('change', onChange);

    window.fillSourceLine(tr, { docNo: 'SO-9', lineNo: 1, productCode: 'P001', remaining: 2 });

    document.removeEventListener('change', onChange);
    expect(triggerSpy).toHaveBeenCalled();        // select2 顯示同步路徑有走
    expect(docHits).toBe(1);                        // 且原生委派仍被觸發（修正前此處為 0）
    expect(tr.querySelector('.product-select').value).toBe('P001');
});
