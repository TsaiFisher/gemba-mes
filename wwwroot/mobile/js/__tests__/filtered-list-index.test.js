/**
 * 搜尋過濾後的清單，點選必須選到畫面上那一筆。
 *
 * 2026-11-06 缺陷：pick／putaway／iqc／fpi／ipqc 五頁的 renderList(items) 以過濾後陣列的位置
 * 寫 data-idx，點選時卻讀「完整清單[idx]」——先搜尋單號再點，會選到別張單（手機下架下到錯的單）。
 * 修法是 data-idx 寫完整清單裡的位置（allItems.indexOf(it)）。這支測試掃所有手機頁面：
 * 只要頁面「把過濾結果丟進 renderList」，它的 data-idx 就不得是 map 的原始索引。
 */
const fs = require('fs');
const path = require('path');

const mobileRoot = path.join(__dirname, '..', '..');

function pageFiles(dir) {
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => {
        const full = path.join(dir, e.name);
        if (e.isDirectory()) return e.name === '__tests__' || e.name === 'lib' ? [] : pageFiles(full);
        return e.name.endsWith('.page.js') ? [full] : [];
    });
}

test('filtered lists must not use the filtered position as the selection index', () => {
    const offenders = [];
    for (const file of pageFiles(mobileRoot)) {
        const src = fs.readFileSync(file, 'utf8');
        // 有「過濾後重畫」才有這個風險
        if (!/renderList\(\s*kw\s*\?\s*\w+\.filter\(/.test(src)) continue;
        // map((x, idx) => ... data-idx="${idx}"：原始索引
        const m = src.match(/\.map\(\(\s*\w+\s*,\s*(\w+)\s*\)\s*=>[\s\S]*?data-idx="\$\{(\w+)\}"/);
        if (m && m[1] === m[2]) offenders.push(path.relative(mobileRoot, file));
    }
    expect(offenders).toEqual([]);
});
