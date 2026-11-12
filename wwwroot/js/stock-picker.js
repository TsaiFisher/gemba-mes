// 庫存挑選視窗（INV 單據明細行的「選擇庫存」）。
//
// 由 Modules/INV/Views/Shared/_StockPickerModal.cshtml 載入。所有顯示文字都從
// #stockPickerMsgs 的 data-* 讀，本檔不得出現任何中文字面（Cx7）。
//
// 呼叫端只需要在按鈕上宣告目標欄位：
//   <button data-action="lookupStock"
//           data-wh-field="FromWhCode" data-loc-field="FromLocationCode">
//
// 動作名保留 lookupStock（原本那支只刷新下拉的函式已移除）：R34 以
// lookup/remove/add 前綴判定「明細行編輯動作」，那類小圖示鈕不必附文字。
// 欄位名各畫面不同（FromWhCode / WhCode…），所以由按鈕帶而不是寫死在這裡。
(function () {
    'use strict';

    var target = null;    // { tr, whField, locField }

    function msgs() {
        var el = document.getElementById('stockPickerMsgs');
        return el ? el.dataset : {};
    }

    function tbody() { return document.getElementById('stockPickerTbody'); }

    function row(html, cls) {
        return '<tr><td colspan="5" class="text-center ' + (cls || 'text-muted') + ' py-3">' +
               esc(html) + '</td></tr>';
    }

    // 庫存資料來自 API，但儲位／批號都是使用者可輸入的主檔欄位——一律轉義後再進 DOM。
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    window.lookupStock = function (e, btn) {
        var tr = btn.closest('tr');
        if (!tr) return;

        var productInput = tr.querySelector('[name*="ProductCode"]');
        var productCode = productInput ? productInput.value : '';
        if (!productCode) {
            // 沒品號就沒有庫存可挑。沿用各畫面既有的提示鍵，由按鈕帶入。
            if (btn.dataset.noProductMsg) alert(btn.dataset.noProductMsg);
            return;
        }

        target = {
            tr: tr,
            whField: btn.dataset.whField || 'WhCode',
            locField: btn.dataset.locField || 'LocationCode',
            // 選填：明細有批號欄時一併帶入（發退料單）。沒宣告就不動批號——
            // 既有四張 INV 單據的批號另有自己的規則，不可被這裡覆寫。
            lotField: btn.dataset.lotField || null,
        };

        var modalEl = document.getElementById('stockPickerModal');
        if (!modalEl) return;
        // ⚠️ 面板（offcanvas）內直接開 modal 會灰屏凍結，先搬到 body。
        if (modalEl.parentElement !== document.body) {
            document.body.appendChild(modalEl);
            modalEl.setAttribute('data-moved-modal', '1');
        }
        // getOrCreateInstance 而非自己快取：crud-panel 每次開面板都會清掉
        // [data-moved-modal] 的殘留節點再重建，快取住的實例會指向已被移除的節點。
        var inst = bootstrap.Modal.getOrCreateInstance(modalEl);

        var m = msgs();
        var label = document.getElementById('stockPickerProduct');
        if (label) label.textContent = (m.product || '') + ' ' + productCode;
        tbody().innerHTML = row(m.loading || '...');
        inst.show();

        fetch('/api/warehouse/stock/' + encodeURIComponent(productCode))
            .then(function (r) { if (!r.ok) throw r.status; return r.json(); })
            .then(function (rows) { render(rows); })
            .catch(function () { tbody().innerHTML = row(m.failed || 'error', 'text-danger'); });
        showReserved(productCode);
    };

    // 廠級保留（訂單／工單，不落儲位）。下表的可用量只扣儲位鎖量，看不到這種保留——
    // 選了之後出貨才被擋（驗收 X-8）。查不到或失敗就不顯示，不影響挑選。
    function showReserved(productCode) {
        var box = document.getElementById('stockPickerReserved');
        if (!box) return;
        box.classList.add('d-none');
        box.textContent = '';
        fetch('/api/warehouse/stock-reservations/' + encodeURIComponent(productCode))
            .then(function (r) { if (!r.ok) throw r.status; return r.json(); })
            .then(function (d) {
                if (!d || !d.total || d.total <= 0) return;
                var span = document.getElementById('stockPickerMsgs');
                var label = function (t) {
                    return (span && span.getAttribute('data-src-' + String(t).toLowerCase())) || t;
                };
                var parts = (d.sources || []).slice(0, 5).map(function (s) {
                    return label(s.type) + ' ' + s.docId + ' ' + s.qty;
                });
                if ((d.sources || []).length > 5) parts.push('…');
                var fmt = (msgs().reserved || '{0}: {1}');
                box.textContent = fmt.replace('{0}', d.total).replace('{1}', parts.join(' / '));
                box.classList.remove('d-none');
            })
            .catch(function () { /* 只是提示，失敗不打擾 */ });
    }

    function render(rows) {
        var m = msgs();
        if (!rows || rows.length === 0) {
            tbody().innerHTML = row(m.empty || '-');
            return;
        }
        tbody().innerHTML = rows.map(function (r) {
            return '<tr>' +
                '<td>' + esc(r.whCode) + '</td>' +
                '<td>' + esc(r.locationCode) + '</td>' +
                '<td>' + esc(r.lotNo) + '</td>' +
                // 只顯示儲位在手量：預留是「廠 × 品號」層級、不記儲位，原本的預留量／可用量讀的是恆為 0 的欄位（2026-10-27）
                '<td class="text-end fw-semibold">' + esc(r.qtyOnHand) + '</td>' +
                '<td class="text-end">' +
                  '<button type="button" class="btn btn-sm btn-primary" data-action="pickStockRow" ' +
                    'data-wh="' + esc(r.whCode) + '" data-loc="' + esc(r.locationCode) + '" ' +
                    'data-lot="' + esc(r.lotNo) + '">' +
                    esc(m.select || 'OK') +
                  '</button>' +
                '</td></tr>';
        }).join('');
    }

    window.pickStockRow = function (e, btn) {
        if (!target) return;
        var whSel  = target.tr.querySelector('[name*="' + target.whField + '"]');
        var locSel = target.tr.querySelector('[name*="' + target.locField + '"]');

        // 先設倉庫再觸發 change：儲位選項是由倉庫篩出來的，順序顛倒的話
        // 接著設的儲位值會在選項重建時被清掉（看起來像「選了沒反應」）。
        if (whSel && whSel.value !== btn.dataset.wh) {
            whSel.value = btn.dataset.wh;
            whSel.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (locSel) {
            locSel.value = btn.dataset.loc;
            locSel.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (target.lotField) {
            var lotInput = target.tr.querySelector('[name*="' + target.lotField + '"]');
            if (lotInput) lotInput.value = btn.dataset.lot || '';
        }
        var modalEl = document.getElementById('stockPickerModal');
        if (modalEl) bootstrap.Modal.getOrCreateInstance(modalEl).hide();
        if (locSel) locSel.focus();
    };
})();
