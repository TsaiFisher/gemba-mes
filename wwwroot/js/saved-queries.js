// 常用查詢（§7.9 L2）的前端行為。
//
// 搭配 Modules/RPS/Views/Shared/_SavedQueries.cshtml 使用，走 data-action 委派
// （見 wwwroot/js/data-action.js）——本檔不註冊任何 inline handler，
// 頁面因此不需要 CSP 的 'unsafe-inline'。
//
// ⚠️ 本檔刻意不含任何顯示文字：所有訊息由 partial 以 data-msgs（JSON）傳入。
//    在這裡寫死中文會同時違反 Cx7，而且英文語系下會直接漏字。
(function () {
    'use strict';

    // partial 可能被同一頁重複引入（理論上不會，但防呆）；重複載入時不重複定義。
    if (window.__rpsSavedQueriesLoaded) return;
    window.__rpsSavedQueriesLoaded = true;

    function root(el) {
        return el.closest('.rps-saved-queries');
    }

    function msgs(box) {
        try { return JSON.parse(box.dataset.msgs || '{}'); }
        catch (e) { return {}; }
    }

    function token(box) {
        var input = box.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    // 存檔時要送出的查詢條件，來自報表畫面自己的 GET 查詢表單。
    // 只取該報表白名單內的欄位（伺服器端會再過濾一次，這裡是為了不送多餘資料）。
    function collectFilters(box) {
        var keys = (box.dataset.filterKeys || '').split(',').filter(function (k) { return k; });
        var form = document.querySelector('form[method="get"], form[method="GET"]');
        var out = {};
        if (!form) return out;

        keys.forEach(function (key) {
            var field = form.querySelector('[name="' + key + '"]');
            if (field && field.value) out[key] = field.value;
        });
        return out;
    }

    function setMessage(box, text, isError) {
        var target = box.querySelector('.rps-sq-msg');
        if (!target) return;
        target.textContent = text || '';
        target.className = 'rps-sq-msg small mt-1 ' + (isError ? 'text-danger' : 'text-success');
    }

    // ── 清單 ────────────────────────────────────────────────────────────

    window.rpsLoadSavedQueries = function (e, el) {
        var box = root(el);
        if (!box) return;
        var list = box.querySelector('.rps-sq-list');
        var m = msgs(box);

        fetch(box.dataset.listUrl + '?toolKey=' + encodeURIComponent(box.dataset.toolKey), {
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(function (r) { if (!r.ok) throw r.status; return r.json(); })
            .then(function (items) { renderList(box, list, items, m); })
            .catch(function () {
                list.replaceChildren(textItem(m.loadFailed, 'text-danger'));
            });
    };

    function textItem(text, cls) {
        var li = document.createElement('li');
        var span = document.createElement('span');
        span.className = 'dropdown-item-text small ' + (cls || 'text-muted');
        span.textContent = text || '';
        li.appendChild(span);
        return li;
    }

    function renderList(box, list, items, m) {
        if (!items || items.length === 0) {
            list.replaceChildren(textItem(m.empty));
            return;
        }

        var frag = document.createDocumentFragment();
        items.forEach(function (item) {
            var li = document.createElement('li');
            var row = document.createElement('div');
            row.className = 'd-flex align-items-center gap-2 px-2 py-1';

            // §7.9-3：沒權限／已失效的項目**照樣列出**，只是禁用並標示原因。
            // 靜默隱藏會被使用者當成「我存的查詢不見了」來報修。
            var usable = item.isAuthorized && item.isValid;

            var link = document.createElement('a');
            link.className = 'flex-grow-1 text-decoration-none' + (usable ? '' : ' disabled text-muted');
            link.textContent = item.name;
            if (usable) {
                link.href = box.dataset.runUrl + '/' + item.id;
            } else {
                link.setAttribute('aria-disabled', 'true');
                var badge = document.createElement('span');
                badge.className = 'badge bg-secondary ms-2';
                badge.textContent = item.isAuthorized ? m.invalid : m.noPermission;
                link.appendChild(badge);
            }

            var del = document.createElement('button');
            del.type = 'button';
            del.className = 'btn btn-sm btn-link text-danger p-0';
            del.setAttribute('data-action', 'rpsDeleteSavedQuery');
            del.setAttribute('data-id', item.id);
            del.setAttribute('aria-label', m.delete);
            del.innerHTML = '<i class="bi bi-trash"></i>';

            row.appendChild(link);
            row.appendChild(del);
            li.appendChild(row);
            frag.appendChild(li);
        });

        list.replaceChildren(frag);
    }

    // ── 存檔 ────────────────────────────────────────────────────────────

    window.rpsToggleSaveForm = function (e, el) {
        var box = root(el);
        if (!box) return;
        var form = box.querySelector('.rps-sq-form');
        form.classList.toggle('d-none');
        setMessage(box, '', false);
        if (!form.classList.contains('d-none')) {
            var name = form.querySelector('.rps-sq-name');
            if (name) name.focus();
        }
        return false;
    };

    window.rpsSubmitSaveQuery = function (e, el) {
        var box = root(el);
        if (!box) return;
        var m = msgs(box);
        var nameInput = box.querySelector('.rps-sq-name');
        var periodSel = box.querySelector('.rps-sq-period');

        var body = new FormData();
        body.append('toolKey', box.dataset.toolKey);
        body.append('name', nameInput ? nameInput.value : '');
        if (periodSel) body.append('period', periodSel.value);
        body.append('__RequestVerificationToken', token(box));

        var filters = collectFilters(box);
        Object.keys(filters).forEach(function (key) {
            body.append('filters[' + key + ']', filters[key]);
        });

        fetch(box.dataset.saveUrl, { method: 'POST', body: body })
            .then(function (r) { if (!r.ok) throw r.status; return r.json(); })
            .then(function (result) {
                if (result.success) {
                    setMessage(box, m.saved, false);
                    if (nameInput) nameInput.value = '';
                } else {
                    // 伺服器回的是訊息鍵而非文字，才能依使用者語系顯示。
                    setMessage(box, m[result.message] || m.loadFailed, true);
                }
            })
            .catch(function () { setMessage(box, m.loadFailed, true); });

        return false;
    };

    window.rpsDeleteSavedQuery = function (e, el) {
        var box = root(el);
        if (!box) return;
        var m = msgs(box);
        if (!window.confirm(m.deleteConfirm)) return false;

        var body = new FormData();
        body.append('id', el.dataset.id);
        body.append('__RequestVerificationToken', token(box));

        fetch(box.dataset.deleteUrl, { method: 'POST', body: body })
            .then(function (r) { if (!r.ok) throw r.status; return r.json(); })
            .then(function () { window.rpsLoadSavedQueries(e, el); })
            .catch(function () { /* 清單下次開啟時會重新載入 */ });

        return false;
    };
})();
