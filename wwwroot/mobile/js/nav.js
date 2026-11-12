// 手機版導覽共用行為（data-action 委派用，見 /js/data-action.js）。
//
// historyBack：返回上一頁；無同源上一頁時（直接開網址、掃 QR 進入、PWA 冷啟動）
//              退回 data-fallback 指定的頁面，避免停在死路。
//   用法：<button data-action="historyBack" data-fallback="/mobile/mfg/list.html">
//
// reloadPage：重新載入目前網址。offline.html 的「重試」用它——該頁是 sw.js 對原網址
//             的替代回應，location 仍是原本那一頁，所以 reload 重試的正是使用者要去的頁。

window.historyBack = function (e, el) {
    var fallback = (el && el.dataset && el.dataset.fallback) || '/mobile/home.html';
    var sameOriginReferrer = false;
    try {
        sameOriginReferrer = !!document.referrer &&
            new URL(document.referrer).origin === location.origin;
    } catch (err) {
        sameOriginReferrer = false;
    }

    if (window.history.length > 1 && sameOriginReferrer) {
        window.history.back();
    } else {
        window.location.href = fallback;
    }
    return false;   // 阻止 <a href> 預設跳轉（dispatcher 慣例）
};

window.reloadPage = function () {
    window.location.reload();
    return false;
};
