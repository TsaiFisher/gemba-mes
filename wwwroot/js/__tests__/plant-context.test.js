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
