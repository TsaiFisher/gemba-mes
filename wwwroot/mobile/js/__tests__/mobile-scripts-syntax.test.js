'use strict';

/**
 * 手機端每一支 JS 都必須能被解析。
 *
 * 2026-09-27 手機實測才發現：mfg/reports.page.js 自 2026-08-12 雙語化起就有語法錯誤
 * （單引號字串裡塞了 ${t('…')}），整支不執行——報工頁永遠「載入中」、按鈕全死。
 * 六週沒人發現，因為：頁面 HTML 照常出來、沒有伺服器錯誤、data-action.js 找不到
 * 函式只 console.warn；既有 Jest 只載入被測的那幾支，UITests 也沒有走到報工頁。
 *
 * 語法錯誤的代價是「整支檔案一行都不跑」，所以這裡不挑檔，整個 /mobile 全掃。
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const MOBILE_DIR = path.resolve(__dirname, '..', '..');

function collectScripts(dir) {
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            // 第三方壓縮檔不是我們寫的；測試檔本身由 Jest 載入就等於解析過了。
            return (entry.name === 'libs' || entry.name === '__tests__') ? [] : collectScripts(full);
        }
        return entry.name.endsWith('.js') ? [full] : [];
    });
}

const scripts = collectScripts(MOBILE_DIR);

test('掃描範圍沒有縮水', () => {
    // 目錄搬動或過濾條件寫錯時，下面的 test.each 會變成零筆而安靜地全綠。
    expect(scripts.length).toBeGreaterThanOrEqual(25);
    expect(scripts.map(f => path.relative(MOBILE_DIR, f))).toEqual(
        expect.arrayContaining(['sw.js', path.join('mfg', 'reports.page.js')]));
});

test.each(scripts.map(f => [path.relative(MOBILE_DIR, f), f]))('%s 可被解析', (_, file) => {
    const src = fs.readFileSync(file, 'utf-8');
    // 只編譯不執行；手機頁面都是傳統 <script>，用 Script 目標解析與瀏覽器一致。
    expect(() => new vm.Script(src, { filename: file })).not.toThrow();
});
