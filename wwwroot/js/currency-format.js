// 統一依幣別小數位數設定 number input 的 step、捨入金額、產生顯示字串。
// 對稱於 uom-step.js。
//   (1) Razor 注入 window.currencyDecimals = { CurrencyCode: dp, ... }
//   (2) currencyDp(code)              → 取得位數（找不到時預設 2）
//   (3) currencyStepOf(code)          → 取得 step 字串
//   (4) applyCurrencyStep(input, code)→ 設 step
//   (5) roundHalfAway(value, dp)      → 對齊 C# MidpointRounding.AwayFromZero
//   (6) fmtMoney(value, code)         → 千分位 + 對應位數字串
//   (7) fetchCurrencyDp(code)         → 未快取時呼叫 API（保留擴充用）
//   (8) roundInputToCurrency(el,code) → 依幣別 dp 收斂單一輸入框的顯示值
//       並對多幣別表單(.line-price + 同列 .line-currency)自動掛 change 收斂
(function () {
    if (!window.currencyDecimals) window.currencyDecimals = {};

    const DEFAULT_DP = 2;

    window.currencyDp = function (code) {
        if (code && window.currencyDecimals[code] != null) return window.currencyDecimals[code];
        return DEFAULT_DP;
    };

    // spinner step 一律 "any"：HTML number input 的 step 同時決定上下三角形增減量與
    // 允許輸入精度；回精確小數（如 "0.00000001"）會使 spinner 每按一次只 ±0.00000001。
    // 改回 "any" → spinner ±1、仍允許小數；精度由 fmtMoney/roundHalfAway + 伺服端
    // RoundAsync 把關。對齊 uom-step.js 與 C# CurrencyStepHelper.GetStep。見 R9。
    window.currencyStepOf = function (code) {
        return "any";
    };

    window.applyCurrencyStep = function (inputEl, code) {
        if (!inputEl) return;
        inputEl.step = "any";
    };

    // 與 C# Math.Round(value, dp, MidpointRounding.AwayFromZero) 一致。
    // JavaScript 的 Math.round 對負數採用「往上」（toward +∞），
    // 此處特別處理負數以對齊後端。
    window.roundHalfAway = function (value, dp) {
        if (!isFinite(value)) return 0;
        var f = Math.pow(10, dp);
        return (value >= 0 ? Math.floor(value * f + 0.5) : -Math.floor(-value * f + 0.5)) / f;
    };

    window.fmtMoney = function (value, code) {
        var dp = window.currencyDp(code);
        var v  = window.roundHalfAway(value || 0, dp);
        return v.toLocaleString(undefined, { minimumFractionDigits: dp, maximumFractionDigits: dp });
    };

    window.fetchCurrencyDp = async function (code) {
        if (!code) return DEFAULT_DP;
        if (window.currencyDecimals[code] != null) return window.currencyDecimals[code];
        return DEFAULT_DP; // 預留：未來若加 /api/currency/{code}/decimals 可在此擴充
    };

    // 依幣別小數位數,即時收斂單一輸入框的顯示值（與 calcLine/伺服端 RoundAsync 對齊）。
    // 空字串/非數字不動（避免把未填欄位強塞 0）。
    window.roundInputToCurrency = function (inputEl, code) {
        if (!inputEl) return;
        var raw = (inputEl.value || "").trim();
        if (raw === "") return;
        var v = parseFloat(raw);
        if (!isFinite(v)) return;
        var dp = window.currencyDp(code);
        inputEl.value = window.roundHalfAway(v, dp).toFixed(dp);
    };

    // 多幣別表單:編輯金額(.line-price)變更時,依「該列幣別(.line-currency)」dp 收斂顯示值。
    // 僅在該列存在 .line-currency（= per-row 多幣別）時作用 → 自動涵蓋 PUR/ORD SalesOrder,
    // 並略過無 per-row 幣別的單一幣別表單（其精度仍由 calcLine + 伺服端 RoundAsync 保證）。
    // 用 capture 階段:確保收斂發生在該欄 inline onchange="calcLine(...)" 之前,
    // 使「顯示單價」與「計算金額」同以收斂後的單價為準。spinner 維持 ±1（step="any"）。
    document.addEventListener("change", function (e) {
        var el = e.target;
        if (!(el instanceof HTMLInputElement) || !el.classList.contains("line-price")) return;
        var row = el.closest("tr");
        var ccySel = row && row.querySelector(".line-currency");
        if (!ccySel) return;
        window.roundInputToCurrency(el, ccySel.value || "");
    }, true);
})();
