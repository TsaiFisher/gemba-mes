// 統一依計量單位小數位數設定 number input 的 step / min。
// 使用方式：
//   (1) 頁面載入時，由 Razor 注入 window.uomDecimals = { UomCode: dp, ... }
//   (2) 呼叫 uomStepOf(uomCode) 取得 step 字串
//   (3) 呼叫 applyUomStep(inputEl, uomCode) 同時設定 step 與 min
//   (4) 使用者切換單位時 async 取得：fetchUomDecimals(uomCode) → 更新 window.uomDecimals 後再 applyUomStep
(function () {
    if (!window.uomDecimals) window.uomDecimals = {};

    // uomStepOf 回「該 UOM 的最小正值」，供 min 用（非 spinner step）。
    // spinner step 一律 "any"（見 applyUomStep）→ 上下三角形 ±1、仍允許小數，
    // 精度由 min + 伺服端 RoundQtyAsync 把關。對齊 C# UomStepHelper。見 R9。
    window.uomStepOf = function (uomCode) {
        var dp = (uomCode && window.uomDecimals[uomCode] != null) ? window.uomDecimals[uomCode] : 0;
        if (dp <= 0) return "1";
        return "0." + "0".repeat(dp - 1) + "1";
    };

    window.applyUomStep = function (inputEl, uomCode, { minPositive = true } = {}) {
        if (!inputEl) return;
        inputEl.step = "any";
        if (minPositive) {
            var minStep = window.uomStepOf(uomCode);
            if (inputEl.min !== "0") inputEl.min = minStep;
        }
    };

    // 依單位查詢小數位數（未快取時自動呼叫 API）
    window.fetchUomDecimals = async function (uomCode) {
        if (!uomCode) return 0;
        if (window.uomDecimals[uomCode] != null) return window.uomDecimals[uomCode];
        try {
            var r = await fetch('/api/uom/' + encodeURIComponent(uomCode) + '/decimals',
                { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            if (!r.ok) return 0;
            var j = await r.json();
            var dp = j && typeof j.decimalPlaces === 'number' ? j.decimalPlaces : 0;
            window.uomDecimals[uomCode] = dp;
            return dp;
        } catch (e) { return 0; }
    };

    // 以單位套用 step 到輸入，內含查詢（給動態新增列、切換產品使用）
    window.applyUomStepAsync = async function (inputEl, uomCode, opts) {
        await window.fetchUomDecimals(uomCode);
        window.applyUomStep(inputEl, uomCode, opts);
    };
})();

// ── 品號可用單位（2026-10-12）────────────────────────────────────────────────
// 單據明細的單位下拉原本列出**所有**啟用單位：選了品號 A（庫存單位 PC），還是挑得到 KG、箱，
// 而庫存類單據的伺服器端從前不換算——挑錯就入錯帳。現在：明細選了品號，單位下拉只留
// 「庫存單位＋與它查得到換算的單位」（/api/uom/product/{code}，規則與伺服器換算同一套）。
//
// 自動套用，不必每張單據各寫一份：
//   - 規則靠欄位名稱：同一列（tr）裡的 `…ProductCode` 與 `select[name$=".UomCode"]`。
//   - 換品號時改成偏好單位：`data-uom-prefer="purchase|sales|stock"`（預設 stock）；
//     `data-uom-stock-only` 只留庫存單位。
//   - 面板載入時已有品號的列也套用，但**保留已存的單位**（就算不在清單裡），不靜默改掉既有資料。
//   - 用 jQuery 委派：品號下拉多半是 select2，它觸發的是 jQuery 事件，原生監聽收不到。
(function () {
    var cache = {};
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    window.fetchProductUoms = function (productCode) {
        if (!productCode) return Promise.resolve(null);
        if (!cache[productCode]) {
            cache[productCode] = fetch('/api/uom/product/' + encodeURIComponent(productCode),
                { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .catch(function () { delete cache[productCode]; return null; });
        }
        return cache[productCode];
    };

    /**
     * 把單位下拉限縮成品號可用的單位。
     * opts.keepCurrent：保留目前值（面板初次載入既有明細時用）；否則換成偏好單位。
     * opts.value：搭配 keepCurrent，指定要保留的值（下拉裡可能還沒有這個選項）。
     * 查不到（品號不存在、網路失敗）就不動，交給伺服器存檔時擋。
     */
    window.restrictUomToProduct = async function (uomSel, productCode, opts) {
        opts = opts || {};
        if (!uomSel || !productCode) return;
        var info = await window.fetchProductUoms(productCode);
        if (!info || !info.uoms || !info.uoms.length) return;

        var cur = opts.value != null ? opts.value : uomSel.value;   // opts.value：要保留的已存單位（選項可能還沒有它）
        var prefer = uomSel.dataset.uomPrefer || 'stock';
        var preferred = info[prefer + 'Uom'];
        if (!preferred || info.uoms.indexOf(preferred) < 0) preferred = info.stockUom || info.uoms[0];

        // data-uom-stock-only：只能用庫存單位的單據（發退料單：工單用料、額度、過帳全以庫存單位計）
        var list = uomSel.dataset.uomStockOnly && info.stockUom ? [info.stockUom] : info.uoms.slice();
        if (uomSel.dataset.uomStockOnly && info.stockUom) preferred = info.stockUom;
        if (opts.keepCurrent && cur && list.indexOf(cur) < 0) list.push(cur);
        var texts = {};
        Array.prototype.forEach.call(uomSel.options, function (o) { texts[o.value] = o.textContent; });
        uomSel.innerHTML = list.map(function (u) {
            return '<option value="' + esc(u) + '">' + esc(texts[u] || u) + '</option>';
        }).join('');

        var next = opts.keepCurrent && cur ? cur : preferred;
        uomSel.value = next;
        uomSel.dataset.uomRestricted = productCode;
        // 單位變了要讓各頁原本的 onUomChange（step／換算）跟著跑
        if (next !== cur) uomSel.dispatchEvent(new Event('change', { bubbles: true }));
    };

    function rowOf(el) { return el.closest('tr, [data-line-row]'); }
    function uomOf(row) { return row ? row.querySelector('select[name$=".UomCode"]') : null; }
    var PRODUCT_SEL = 'select[name$=".ProductCode"], input[name$=".ProductCode"]';

    function onProductChanged(el) {
        var uomSel = uomOf(rowOf(el));
        if (uomSel && el.value) window.restrictUomToProduct(uomSel, el.value);
    }
    if (window.jQuery) {
        window.jQuery(document).on('change', PRODUCT_SEL, function () { onProductChanged(this); });
    } else {
        document.addEventListener('change', function (e) {
            if (e.target.matches && e.target.matches(PRODUCT_SEL)) onProductChanged(e.target);
        });
    }

    // 面板（AJAX 載入）裡已經有品號的明細：保留已存的單位，只把清單限縮
    function sweep(root) {
        (root || document).querySelectorAll(PRODUCT_SEL).forEach(function (p) {
            if (!p.value) return;
            var uomSel = uomOf(rowOf(p));
            if (!uomSel || uomSel.dataset.uomRestricted === p.value) return;
            uomSel.dataset.uomRestricted = p.value;
            window.restrictUomToProduct(uomSel, p.value, { keepCurrent: true });
        });
    }
    document.addEventListener('DOMContentLoaded', function () { sweep(document); });
    if (typeof MutationObserver === 'function') {
        var pending = false;   // 一個畫面更新常是一連串變動，合併成一次掃描
        new MutationObserver(function (muts) {
            if (pending) return;
            for (var i = 0; i < muts.length; i++) {
                if (muts[i].addedNodes.length) {
                    pending = true;
                    setTimeout(function () { pending = false; sweep(document); }, 0);
                    return;
                }
            }
        }).observe(document.documentElement, { childList: true, subtree: true });
    }
})();
