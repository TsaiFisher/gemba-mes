'use strict';

/**
 * 最小 IndexedDB 替身（只實作 offline-queue.js 真正用到的那幾個 API）。
 *
 * 為什麼自己寫而不是裝 fake-indexeddb：這個環境沒有外網裝不了新套件，
 * 而且受測的是**佇列邏輯**（排序、狀態轉移、HTTP 狀態的處理、中斷後的可重送性），
 * 不是 IndexedDB 本身。替身只要在這些行為上與真實 API 同語意即可。
 *
 * 刻意保留的真實特性：
 *   - open 是非同步的（onupgradeneeded → onsuccess），所以 ready() 真的有等待語意
 *   - put 以 keyPath 覆寫同鍵記錄（clientId 去重靠的就是這件事）
 *   - index('status').getAll(only(v)) 只回傳該狀態的記錄
 */

class FakeStore {
    constructor(keyPath) {
        this.keyPath = keyPath;
        this.records = new Map();
        this.indexes = new Set();
    }
    createIndex(name) { this.indexes.add(name); }
}

class FakeDb {
    constructor() { this.stores = new Map(); }
    get objectStoreNames() {
        const names = [...this.stores.keys()];
        return { contains: n => names.includes(n) };
    }
    createObjectStore(name, opts) {
        const store = new FakeStore(opts.keyPath);
        this.stores.set(name, store);
        return store;
    }
    transaction(name) {
        const store = this.stores.get(name);
        return {
            objectStore: () => ({
                put:    rec => request(() => { store.records.set(rec[store.keyPath], rec); return rec; }),
                delete: key => request(() => { store.records.delete(key); return undefined; }),
                get:    key => request(() => store.records.get(key)),
                getAll: () => request(() => [...store.records.values()]),
                index:  () => ({
                    getAll: range => request(() =>
                        [...store.records.values()].filter(r => r.status === range.value)),
                }),
            }),
        };
    }
}

// 非同步回呼：真實 IndexedDB 的 onsuccess 不會同步觸發，
// 同步觸發會讓「忘了 await」的缺陷在測試裡意外通過。
function request(work) {
    const req = {};
    setTimeout(() => {
        try { req.onsuccess?.({ target: { result: work() } }); }
        catch (e) { req.onerror?.(e); }
    }, 0);
    return req;
}

/** 安裝替身到 global，回傳可清空資料的控制介面。 */
function installFakeIndexedDb() {
    let db = null;

    global.indexedDB = {
        open() {
            const req = {};
            setTimeout(() => {
                const fresh = db === null;
                if (fresh) db = new FakeDb();
                if (fresh) req.onupgradeneeded?.({ target: { result: db } });
                req.onsuccess?.({ target: { result: db } });
            }, 0);
            return req;
        },
    };
    global.IDBKeyRange = { only: value => ({ value }) };

    return {
        clear() { db?.stores.forEach(s => s.records.clear()); },
        records(storeName = 'offlineQueue') {
            return db ? [...db.stores.get(storeName).records.values()] : [];
        },
    };
}

module.exports = { installFakeIndexedDb };
