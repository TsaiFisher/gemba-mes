/**
 * @jest-environment jsdom
 */
'use strict';

/**
 * uom-step.js · 品號可用單位（2026-10-12）
 *
 * 單據明細的單位下拉原本列出全部單位：選了品號（庫存單位 PC）還挑得到箱、KG，而庫存類單據的伺服器
 * 從前不換算——挑錯就入錯帳。現在選了品號就只留可用單位（/api/uom/product/{code}）。
 *
 * 守的是：①只列可用單位且依偏好預選；②既有明細保留已存的單位（不靜默改資料）；
 * ③發退料單只留庫存單位；④換品號會觸發單位的 change（各頁的 step／換算靠它）。
 */

const INFO = {
    P1: { stockUom: 'PC', purchaseUom: 'BOX', salesUom: 'DZ', uoms: ['PC', 'BOX', 'DZ'] },
    P2: { stockUom: 'KG', purchaseUom: null, salesUom: null, uoms: ['KG', 'G'] },
};

beforeAll(() => {
    global.fetch = jest.fn(url => {
        const code = decodeURIComponent(url.split('/').pop());
        const body = INFO[code];
        return Promise.resolve({ ok: !!body, json: () => Promise.resolve(body) });
    });
    require('../uom-step.js');
});

const flush = () => new Promise(r => setTimeout(r, 0));
const opts = sel => [...sel.options].map(o => o.value);

function row(product, uom, attrs = '') {
    document.body.innerHTML = `<table><tbody><tr>
        <td><select name="Lines[0].ProductCode"><option value=""></option>
            <option value="P1" ${product === 'P1' ? 'selected' : ''}>P1</option>
            <option value="P2" ${product === 'P2' ? 'selected' : ''}>P2</option></select></td>
        <td><select name="Lines[0].UomCode" ${attrs}>
            ${['', 'PC', 'BOX', 'DZ', 'KG', 'G', 'M'].map(u => `<option value="${u}" ${u === uom ? 'selected' : ''}>${u}</option>`).join('')}
        </select></td></tr></tbody></table>`;
    return { p: document.querySelector('[name$=".ProductCode"]'), u: document.querySelector('[name$=".UomCode"]') };
}

test('只列可用單位，預選庫存單位', async () => {
    const { u } = row('', '');
    await window.restrictUomToProduct(u, 'P1');
    expect(opts(u)).toEqual(['PC', 'BOX', 'DZ']);
    expect(u.value).toBe('PC');
});

test('data-uom-prefer="purchase" 預選採購單位（採購類單據）', async () => {
    const { u } = row('', '', 'data-uom-prefer="purchase"');
    await window.restrictUomToProduct(u, 'P1');
    expect(u.value).toBe('BOX');
});

test('偏好單位沒有設定時退回庫存單位', async () => {
    const { u } = row('', '', 'data-uom-prefer="purchase"');
    await window.restrictUomToProduct(u, 'P2');
    expect(u.value).toBe('KG');
});

test('data-uom-stock-only 只留庫存單位（發退料單）', async () => {
    const { u } = row('', '', 'data-uom-stock-only="1"');
    await window.restrictUomToProduct(u, 'P1');
    expect(opts(u)).toEqual(['PC']);
});

test('既有明細保留已存的單位，就算它不在可用清單裡——不靜默改掉既有資料', async () => {
    const { u } = row('P1', 'M');
    await window.restrictUomToProduct(u, 'P1', { keepCurrent: true });
    expect(opts(u)).toEqual(['PC', 'BOX', 'DZ', 'M']);
    expect(u.value).toBe('M');
});

test('換品號：自動限縮並觸發單位的 change', async () => {
    const { p, u } = row('', '');
    const seen = [];
    u.addEventListener('change', () => seen.push(u.value));
    p.value = 'P2';
    p.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(); await flush();
    expect(opts(u)).toEqual(['KG', 'G']);
    expect(seen).toEqual(['KG']);
});

test('面板載入時已有品號的明細自動限縮，並保留已存單位', async () => {
    document.body.innerHTML = '';
    const { u } = row('P1', 'BOX');   // 動態插入＝MutationObserver 觸發
    await flush(); await flush(); await flush();
    expect(opts(u)).toEqual(['PC', 'BOX', 'DZ']);
    expect(u.value).toBe('BOX');
});

test('查不到品號（或網路失敗）就不動，交給伺服器存檔時擋', async () => {
    const { u } = row('', 'M');
    await window.restrictUomToProduct(u, 'NOPE');
    expect(opts(u)).toContain('M');
    expect(u.value).toBe('M');
});
