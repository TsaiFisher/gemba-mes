const fs = require('fs');
const path = require('path');
const source = fs.readFileSync(path.join(__dirname, '..', 'plant-context.js'), 'utf8');

beforeEach(() => {
    document.body.innerHTML = '<meta name="mes-plant-context" content="A"><form method="post" action="/StockAdjustment/Create"></form>';
    global.Headers = class {
        constructor(headers = {}) { this.values = new Map(Object.entries(headers).map(([k,v]) => [k.toLowerCase(),v])); }
        has(key) { return this.values.has(key.toLowerCase()); }
        set(key, value) { this.values.set(key.toLowerCase(), value); }
        get(key) { return this.values.get(key.toLowerCase()); }
    };
    window.fetch = jest.fn().mockResolvedValue({ ok: true });
});

test('captures rendered plant, covers POST forms and keeps explicit request context', async () => {
    const transport = window.fetch;
    new Function(source)();
    document.querySelector('meta').content = 'B';
    await window.fetch('/StockAdjustment/Create', { method: 'POST' });
    expect(transport.mock.calls[0][1].headers.get('X-Plant-Context')).toBe('A');
    expect(document.querySelector('input[name="__PlantContext"]').value).toBe('A');
    await window.fetch('/StockAdjustment/Create', { method: 'POST', headers: { 'X-Plant-Context': 'original' } });
    expect(transport.mock.calls[1][1].headers.get('X-Plant-Context')).toBe('original');
});

test('does not send plant information to another origin or add headers to GET', async () => {
    const transport = window.fetch;
    new Function(source)();
    await window.fetch('https://other.example/write', { method: 'POST' });
    expect(transport.mock.calls[0][1].headers).toBeUndefined();
    await window.fetch('/StockAdjustment');
    expect(transport.mock.calls[1][1]).toBeUndefined();
});

// ── 跨廠擋下（409 PLANT_CONTEXT_CHANGED）→ 切回原廠別並照原內容重送 ─────────────────
// 2026-11-12 只做在 crud-panel.js；11/15 重測倉庫畫面（自帶 submitPanel）完全沒生效（B3～B5），
// 所以改由每頁都載入的 fetch 包裝處理，任何送出程式都套得到。
describe('switch back to the rendered plant on 409', () => {
    const blocked = { success: false, message: '畫面在 P1 開啟', code: 'PLANT_CONTEXT_CHANGED', renderedPlant: 'P1' };
    function res(status, body) {
        return { status, ok: status >= 200 && status < 300, clone() { return this; }, json: () => Promise.resolve(body) };
    }
    beforeEach(() => {
        document.body.innerHTML = '<meta name="mes-plant-context" content="P1"><input name="__RequestVerificationToken" value="tok">';
        global.Response = class { constructor(b, init) { this.status = init.status; this.ok = false; this._b = b; } json() { return Promise.resolve(JSON.parse(this._b)); } clone() { return this; } };
        window.i18n = { t: k => k === 'PlantContextSwitchBackConfirm' ? '切回 {0}？' : k };
    });

    test('confirm → switches back to the rendered plant, then resends the same body once', async () => {
        const transport = window.fetch = jest.fn()
            .mockResolvedValueOnce(res(409, blocked))
            .mockResolvedValueOnce(res(200, {}))
            .mockResolvedValueOnce(res(200, { success: true }));
        window.confirm = jest.fn(() => true);
        new Function(source)();
        const fd = { marker: 'ACC6-W2' };
        const final = await window.fetch('/Warehouse/Create', { method: 'POST', body: fd });
        expect(await final.json()).toEqual({ success: true });
        expect(transport.mock.calls.map(c => c[0])).toEqual(['/Warehouse/Create', '/UserSettings/SwitchPlant', '/Warehouse/Create']);
        expect(transport.mock.calls[1][1].body.get('plantId')).toBe('P1');
        expect(transport.mock.calls[1][1].body.get('__RequestVerificationToken')).toBe('tok');
        expect(transport.mock.calls[2][1].body).toBe(fd);
        expect(window.confirm.mock.calls[0][0]).toContain('切回 P1？');
    });

    test('cancel → no switch, no resend; caller gets 409 with a short not-saved message', async () => {
        const transport = window.fetch = jest.fn().mockResolvedValueOnce(res(409, blocked));
        window.confirm = jest.fn(() => false);
        new Function(source)();
        const final = await window.fetch('/Warehouse/Create', { method: 'POST', body: {} });
        expect(transport).toHaveBeenCalledTimes(1);
        expect(final.status).toBe(409);
        expect((await final.json()).message).toBe('PlantContextNotSaved');
    });

    test('still blocked after switching → asks only once and hands back the 409', async () => {
        const transport = window.fetch = jest.fn()
            .mockResolvedValueOnce(res(409, blocked))
            .mockResolvedValueOnce(res(200, {}))
            .mockResolvedValueOnce(res(409, blocked));
        window.confirm = jest.fn(() => true);
        new Function(source)();
        const final = await window.fetch('/Warehouse/Create', { method: 'POST', body: {} });
        expect(transport).toHaveBeenCalledTimes(3);
        expect(window.confirm).toHaveBeenCalledTimes(1);
        expect(final.status).toBe(409);
    });

    test('other 409s (missing snapshot, concurrency) pass straight through without asking', async () => {
        window.fetch = jest.fn().mockResolvedValueOnce(res(409, { success: false, message: 'x', code: 'PLANT_CONTEXT_MISSING' }));
        window.confirm = jest.fn(() => true);
        new Function(source)();
        const final = await window.fetch('/Warehouse/Create', { method: 'POST', body: {} });
        expect(window.confirm).not.toHaveBeenCalled();
        expect(final.status).toBe(409);
    });
});
