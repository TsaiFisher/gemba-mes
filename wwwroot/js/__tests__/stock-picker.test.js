'use strict';

/**
 * stock-picker.js · 庫存挑選視窗
 *
 * 背景：四張 INV 單據的明細行都有一顆放大鏡、標題寫「選擇庫存」，但原本按下去
 * 只是把可用量塞進儲位下拉再 focus，沒有任何挑選視窗；跨廠調撥那支連塞下拉都沒做
 * （fetch 完把結果整包丟掉），實際症狀就是「按了沒反應」。
 *
 * 這裡守的是三件在瀏覽器裡才會炸、測不到就只能靠人工點的事：
 *  ① 面板（offcanvas）內開 modal 前必須把節點搬到 body，否則灰屏凍結；
 *  ② 回填順序必須「先倉庫、派發 change、再儲位」——儲位選項是由倉庫篩出來的，
 *     順序顛倒時儲位值會在選項重建時被清掉，看起來又是「選了沒反應」；
 *  ③ 儲位／批號是使用者可輸入的主檔欄位，進 DOM 前一律轉義。
 */

let modalShown, modalHidden;

beforeAll(() => {
    global.bootstrap = {
        Modal: {
            getOrCreateInstance: () => ({
                show: () => { modalShown = true; },
                hide: () => { modalHidden = true; },
            }),
        },
    };
    require('../stock-picker.js');   // 定義 window.lookupStock / window.pickStockRow
});

beforeEach(() => {
    modalShown = modalHidden = false;
    document.body.innerHTML = `
        <span id="stockPickerMsgs" data-loading="載入中" data-empty="沒有庫存"
              data-failed="載入失敗" data-select="選取" data-product="品號"></span>
        <div id="panel">
          <table><tbody><tr id="row">
            <td><select name="Lines[0].ProductCode"><option value="P001" selected>P001</option></select></td>
            <td><select name="Lines[0].FromWhCode"><option value=""></option><option value="W1">W1</option></select></td>
            <td><select name="Lines[0].FromLocationCode"><option value=""></option><option value="A-01">A-01</option></select></td>
            <td><button id="btn" data-action="lookupStock"
                        data-wh-field="FromWhCode" data-loc-field="FromLocationCode"></button></td>
          </tr></tbody></table>
          <div class="modal" id="stockPickerModal">
            <div id="stockPickerProduct"></div>
            <!-- tbody 必須包在 table 內，否則 jsdom 會把它整個丟掉（解析規則同瀏覽器） -->
            <table><tbody id="stockPickerTbody"></tbody></table>
          </div>
        </div>`;
});

afterEach(() => {
    document.body.innerHTML = '';
    jest.restoreAllMocks();
});

const STOCK = [
    { whCode: 'W1', locationCode: 'A-01', lotNo: 'L1', qtyOnHand: 10, qtyReserved: 2, qtyAvailable: 8 },
];

function mockFetch(rows) {
    global.fetch = jest.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve(rows) }));
}

const flush = () => new Promise(r => setTimeout(r, 0));

test('開啟前把 modal 搬到 body —— 留在 offcanvas 內會灰屏凍結', async () => {
    mockFetch(STOCK);
    const modalEl = document.getElementById('stockPickerModal');
    expect(modalEl.parentElement.id).toBe('panel');

    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    expect(modalEl.parentElement).toBe(document.body);
    expect(modalEl.getAttribute('data-moved-modal')).toBe('1');
    expect(modalShown).toBe(true);
});

test('每列只顯示儲位在手量 —— 不再顯示恆為 0 的預留量與等於在手的可用量', async () => {
    mockFetch(STOCK);
    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    const cells = [...document.querySelectorAll('#stockPickerTbody tr:first-child td')].map(td => td.textContent.trim());
    expect(cells.slice(0, 4)).toEqual(['W1', 'A-01', 'L1', '10']);
    expect(cells).toHaveLength(5);   // 倉庫、儲位、批號、在手、選取鈕
});

test('沒有品號時不開視窗', async () => {
    mockFetch(STOCK);
    document.querySelector('[name="Lines[0].ProductCode"]').value = '';
    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    expect(modalShown).toBe(false);
    expect(global.fetch).not.toHaveBeenCalled();
});

test('回填先設倉庫並派發 change，再設儲位', async () => {
    mockFetch(STOCK);
    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    const order = [];
    const whSel  = document.querySelector('[name="Lines[0].FromWhCode"]');
    const locSel = document.querySelector('[name="Lines[0].FromLocationCode"]');
    whSel.addEventListener('change', () => order.push('wh-change'));
    locSel.addEventListener('change', () => order.push('loc-change'));

    const pick = document.querySelector('[data-action="pickStockRow"]');
    expect(pick).not.toBeNull();
    window.pickStockRow(null, pick);

    expect(whSel.value).toBe('W1');
    expect(locSel.value).toBe('A-01');
    // 倉庫的 change 必須先發生：儲位選項是它篩出來的
    expect(order).toEqual(['wh-change', 'loc-change']);
    expect(modalHidden).toBe(true);
});

test('查無庫存時顯示訊息而不是空白表格', async () => {
    mockFetch([]);
    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    expect(document.getElementById('stockPickerTbody').textContent).toContain('沒有庫存');
});

test('儲位與批號一律轉義後才進 DOM', async () => {
    mockFetch([{ whCode: 'W1', locationCode: '<img src=x onerror=alert(1)>', lotNo: '"L1"',
                 qtyOnHand: 1, qtyReserved: 0, qtyAvailable: 1 }]);
    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    const tb = document.getElementById('stockPickerTbody');
    expect(tb.querySelector('img')).toBeNull();
    expect(tb.textContent).toContain('<img src=x onerror=alert(1)>');
});

test('API 失敗時顯示錯誤而不是停在載入中', async () => {
    global.fetch = jest.fn(() => Promise.resolve({ ok: false, status: 500 }));
    window.lookupStock(null, document.getElementById('btn'));
    await flush();

    expect(document.getElementById('stockPickerTbody').textContent).toContain('載入失敗');
});

test('按鈕宣告 data-lot-field 時一併帶入批號；沒宣告就不動批號', async () => {
    // 發退料單的明細有批號欄：只帶倉庫／儲位時，批號空白過帳會找不到那一批庫存。
    const tr = document.getElementById('row');
    tr.insertAdjacentHTML('beforeend', '<td><input name="Lines[0].LotNo" value="OLD"></td>');
    const lot = document.querySelector('[name="Lines[0].LotNo"]');

    mockFetch(STOCK);
    window.lookupStock(null, document.getElementById('btn'));
    await flush();
    window.pickStockRow(null, document.querySelector('[data-action="pickStockRow"]'));
    expect(lot.value).toBe('OLD');

    document.getElementById('btn').dataset.lotField = 'LotNo';
    window.lookupStock(null, document.getElementById('btn'));
    await flush();
    window.pickStockRow(null, document.querySelector('[data-action="pickStockRow"]'));
    expect(lot.value).toBe('L1');
});
