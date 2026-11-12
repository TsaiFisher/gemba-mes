// 授權狀態頁（SECURITY-OPERATIONS.md §18.15.3）：VM 授權的「確認授權」——MES 本身不連外，
// 由這個瀏覽器分頁開啟授權伺服器的確認頁，確認頁以 postMessage 傳回已簽章的回覆，
// 這裡再送給 /License/CheckinResponse（伺服器端驗簽，驗不過不寫入）。所有顯示文字取自 data- 屬性（已在地化）。
(function () {
    'use strict';

    document.querySelectorAll('[data-lic-copy]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var src = document.querySelector(btn.getAttribute('data-lic-copy'));
            if (!src || !navigator.clipboard) return;
            navigator.clipboard.writeText(src.value).then(function () {
                var old = btn.innerHTML;
                btn.textContent = btn.getAttribute('data-lic-copied');
                setTimeout(function () { btn.innerHTML = old; }, 1500);
            });
        });
    });

    var btn = document.getElementById('licCheckinBtn');
    var form = document.getElementById('licCheckinForm');
    var status = document.getElementById('licCheckinStatus');
    if (!btn || !form || !status) return;

    var origin = btn.getAttribute('data-origin');

    function show(text, cls) {
        status.textContent = text;
        status.classList.remove('u-display-none', 'alert-info', 'alert-success', 'alert-danger');
        status.classList.add(cls);
    }

    btn.addEventListener('click', function () {
        // 不加 noopener：確認頁要用 window.opener.postMessage 把回覆傳回這一頁
        window.open(btn.getAttribute('data-url'), '_blank');
        show(btn.getAttribute('data-waiting'), 'alert-info');
    });

    window.addEventListener('message', function (e) {
        if (e.origin !== origin || !e.data || e.data.kind !== 'sf-lease-response' || typeof e.data.text !== 'string') return;
        var fd = new FormData(form);
        fd.set('text', e.data.text);
        fetch(form.action, { method: 'POST', body: fd, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                show(j.message, j.success ? 'alert-success' : 'alert-danger');
                if (j.success) setTimeout(function () { location.reload(); }, 1500);
            })
            .catch(function () { show(btn.getAttribute('data-failed'), 'alert-danger'); });
    });
})();
