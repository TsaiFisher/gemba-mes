// 全域 data-action 事件委派 dispatcher（CSP 強化基礎建設）。
//
// 目的：取代 inline 事件處理器（onclick/onchange/onsubmit…），使頁面不再依賴
// CSP 的 'unsafe-inline'。CSP3 一旦於 script-src 加入 nonce-source 即「忽略」
// 'unsafe-inline'，屆時所有 inline on*= 屬性都會失效；改用 data-action 委派後，
// 行為集中於本檔一個 document 層級監聽器，與 nonce 並存無虞。
//
// === 使用方式（/new-screen 產生器已採此慣例）===
//   inline 寫法（舊，待淘汰）          委派寫法（新）
//   ------------------------------    ---------------------------------------
//   onclick="openPanel('/X/Create')"  data-action="openPanel" data-url="/X/Create"
//   onchange="onUomChange(this)"       data-action-change="onUomChange"
//   onsubmit="submitPanel(this,event)  data-action-submit="submitPanel"
//            ;return false;"            （回傳 false 即自動 preventDefault）
//
//   對應的全域函式簽章一律為：function name(event, el) { ... }
//     - this === el === 觸發元素（與舊 inline 的 this 等義）
//     - 需要參數時，從 el.dataset 讀取（如 el.dataset.url、el.dataset.key）
//     - 回傳 false → dispatcher 代為 event.preventDefault()（對齊舊 "return false;"）
//
//   預設 data-action 綁定 click；其他事件用 data-action-{event}（change/input/
//   submit/keyup/blur）。同一元素可並存多種（如 select 同時要 change 與 click）。
(function () {
    'use strict';

    // blur / focus 不會 bubble → 這兩個事件採 capture 階段監聽；其餘走 bubble。
    var EVENTS = ['click', 'change', 'input', 'submit', 'keyup', 'keydown', 'blur', 'focus'];

    function attrFor(type) {
        return 'data-action-' + type;
    }

    function invoke(el, e) {
        // 事件專屬屬性優先；click 額外接受裸 data-action 作為預設。
        var name = el.getAttribute(attrFor(e.type)) ||
                   (e.type === 'click' ? el.getAttribute('data-action') : null);
        if (!name) return;

        var fn = window[name];
        if (typeof fn !== 'function') {
            console.warn('[data-action] 找不到全域函式：' + name +
                         '（請確認已於 @section Scripts 定義 window.' + name + '）');
            return;
        }

        var result = fn.call(el, e, el);
        // 對齊 inline 的 "...; return false;" 慣例（阻止表單送出 / 連結跳轉）。
        if (result === false) {
            e.preventDefault();
        }
    }

    EVENTS.forEach(function (type) {
        var selector = (type === 'click')
            ? '[data-action],[data-action-click]'
            : '[' + attrFor(type) + ']';
        var useCapture = (type === 'blur' || type === 'focus');  // blur / focus 不冒泡
        document.addEventListener(type, function (e) {
            var el = e.target.closest(selector);
            if (el) invoke(el, e);
        }, useCapture);
    });

    // 相容佔位：委派為 document 層級，動態插入的節點無需重綁。
    window.bindDataActions = function () { /* no-op：全域委派已涵蓋動態內容 */ };

    // ── 常用共用小工具（取代常見 inline 片段）──────────────────────────────
    // 取代 onchange="this.form.submit()"（如 pageSize 下拉）。
    window.submitOwnForm = function (e, el) {
        if (el && el.form) el.form.submit();
    };

    // 取代 onclick="window.print()"（列表頁列印鈕）。
    window.printPage = function () {
        window.print();
    };

    // 取代 href="javascript:window.close()"（列印版關閉鈕，javascript: 導覽會撞 CSP script-src）。
    window.closeWindow = function () {
        window.close();
    };

    // 取代 onfocus="if(this.value==='0'){this.value=this.max;this.select()}"
    // （匯入 modal 數量欄聚焦時若為 0 即帶入上限並全選，方便鍵入）。
    window.selectMaxIfZero = function (e, el) {
        if (el && el.value === '0') {
            el.value = el.max;
            el.select();
        }
    };

    // 取代 inline 的「送出/點擊前先 confirm，取消則中止」樣態：
    // 確認訊息放 data-confirm 屬性；使用者取消即回傳 false → dispatcher preventDefault。
    // 表單用 data-action-submit="confirmAction"；按鈕用 data-action="confirmAction"。
    window.confirmAction = function (e, el) {
        if (!confirm(el.dataset.confirm)) return false;
    };

    // 取代 onclick="document.getElementById('X').innerHTML=''"（明細表單區「取消」鈕清空）。
    // 目標元素 id 放 data-target。
    window.clearTarget = function (e, el) {
        var target = document.getElementById(el.dataset.target);
        if (target) target.innerHTML = '';
    };

    // 取代 onclick="history.back()"（錯誤頁「上一頁」鈕）。
    window.historyBack = function () {
        history.back();
    };
})();
