/*
 * crud-panel.js — ProjectZ 共用 CRUD 側面板腳本
 *
 * 取代每個 Index.cshtml 各自手抄的 openPanel / submitPanel / refreshList /
 * submitApproval / withdrawApproval / deleteItem（重複 40+ 份、易漂移）。
 * 由 Views/Shared/_CrudPanelScripts.cshtml 載入；i18n 字串一律走 window.i18n.t，
 * 不得在此檔硬編碼任何 CJK 字面（建置期 R4 規則會把關）。
 *
 * DOM 契約（沿用既有 Index 版面，零行為變更）：
 *   #sidePanel / #sidePanelLabel / #sidePanelBody  — offcanvas
 *   #successToast                                   — 成功提示
 *   input[name="__RequestVerificationToken"]        — Anti-Forgery token
 */
(function () {
    'use strict';

    // 主文件 per-request CSP nonce：由 _CrudPanelScripts.cshtml 以 data-nonce 注入本 script 標籤。
    // execScripts 重建的 inline <script> 必須帶「主文件」nonce 才能在 script-src 'nonce-…'（無
    // unsafe-inline）下執行；面板 AJAX 子請求 HTML 內的 nonce 屬於另一請求，與主文件 CSP 不符，
    // 故不可沿用，必須在此取主文件 nonce。currentScript 僅在本檔同步載入時有效，於此擷取。
    var CSP_NONCE = (document.currentScript && document.currentScript.dataset.nonce) || '';

    function t(key, arg) {
        var s = (window.i18n && typeof window.i18n.t === 'function')
            ? window.i18n.t(key) : key;
        return arg === undefined ? s : String(s).replace('{0}', arg);
    }

    var _offcanvas = null, _toast = null;
    function offcanvas() {
        if (!_offcanvas) _offcanvas = new bootstrap.Offcanvas(document.getElementById('sidePanel'));
        return _offcanvas;
    }
    function toast() {
        if (!_toast) _toast = new bootstrap.Toast(document.getElementById('successToast'));
        return _toast;
    }
    function token() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
    }

    // 畫面渲染當下的廠別（由 _CrudPanelScripts.cshtml 寫進 DOM）。
    // 找不到就回空字串——伺服端把空字串當「不知道」而放行，不會把還沒接上的
    // 畫面變成錯誤。
    function plantContext() {
        return document.getElementById('__PlantContext')?.dataset.plant || '';
    }

    // 收斂瀏覽器原生 HTML5 驗證：面板 HTML 經 innerHTML 注入後，
    // 對每個 <form> 補 novalidate（停掉瀏覽器原生泡泡——其文字依瀏覽器/OS
    // 語言、與 App culture 無關），再重新 parse jQuery unobtrusive，
    // 使驗證改走伺服端在地化的 data-val-* 訊息（DataAnnotations + resx）。
    // 註：module-validation.js 監看的是 #crudPanelBody，本面板為 #sidePanelBody，
    // 故必須由本檔自行處理，不能依賴其 MutationObserver。
    // target 可為單一 <form>，或含多個 <form> 的容器（如面板 body）。
    // 對外曝露為 window.armValidation：凡用 JS 動態 append input 列（addLine 等）者，
    // 須於插入 DOM 後呼叫，使新列的 data-val-* 也被 unobtrusive 重新 parse
    // （否則動態列僅靠伺服端兜底、缺即時前端驗證 — 見 consistency-framework.md Phase F1）。
    function armValidation(target) {
        var scope = target || document;
        var forms = (scope.tagName === 'FORM')
            ? [scope]
            : Array.prototype.slice.call(scope.querySelectorAll('form'));
        forms.forEach(function (f) { f.setAttribute('novalidate', 'novalidate'); });
        if (window.jQuery && jQuery.validator && jQuery.validator.unobtrusive) {
            forms.forEach(function (f) {
                jQuery(f).removeData('validator').removeData('unobtrusiveValidation');
                jQuery.validator.unobtrusive.parse(f);
            });
        }
    }

    // 銷毀容器內已被初始化的 Bootstrap 元件，避免 innerHTML 覆寫導致內存洩漏
    function destroyComponents(container) {
        if (!container) return;
        container.querySelectorAll('.modal').forEach(function (el) {
            try {
                if (window.bootstrap && bootstrap.Modal) {
                    bootstrap.Modal.getInstance(el)?.dispose();
                }
            } catch (e) { }
        });
        container.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function (el) {
            try {
                if (window.bootstrap && bootstrap.Tooltip) {
                    bootstrap.Tooltip.getInstance(el)?.dispose();
                }
            } catch (e) { }
        });
    }

    // innerHTML 不會執行 <script>，需手動重建並掛載
    function execScripts(container) {
        container.querySelectorAll('script').forEach(function (old) {
            var s = document.createElement('script');
            // 複製 src（外部腳本）或 inline 內容；後者需補主文件 nonce 才不被 CSP 擋下。
            if (old.src) {
                s.src = old.src;
            } else {
                s.textContent = old.textContent;
            }
            if (CSP_NONCE) s.nonce = CSP_NONCE;
            document.head.appendChild(s).remove();
        });
    }

    function openPanel(url, label) {
        document.getElementById('sidePanelLabel').textContent = label;
        document.querySelectorAll('[data-moved-modal]').forEach(function (el) {
            try { bootstrap.Modal.getInstance(el)?.dispose(); } catch (e) { }
            el.remove();
        });
        fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
            .then(function (r) { if (!r.ok) throw r.status; return r.text(); })
            .then(function (html) {
                var body = document.getElementById('sidePanelBody');
                destroyComponents(body);
                body.innerHTML = html;
                execScripts(body);
                armValidation(body);
                offcanvas().show();
            })
            .catch(function (status) {
                var body = document.getElementById('sidePanelBody');
                var msg = status === 404 ? t('RecordNotFound') : t('LoadFailed');
                body.innerHTML = '<div class="alert alert-warning m-3">' + msg + '</div>';
                offcanvas().show();
            });
    }

    function hidePanel() { offcanvas().hide(); }

    function refreshList() {
        offcanvas().hide();
        // 延遲 800ms 讓 toast 動畫完成後再 reload（與 deleteItem/postAction 一致）
        setTimeout(function () { window.location.reload(); }, 800);
    }

    // 成功 toast。訊息是**可選**的，而且第一個參數不保證是字串——
    // 這個函式同時是 data-action dispatcher 的目標，dispatcher 以 (event, element) 呼叫，
    // 所以非字串一律當「沒有訊息」處理（清空，避免顯示上一次的殘留）。
    //
    // ⚠️ 先前的版本簽章是 toastSuccess()，完全不理會傳進來的東西：
    // 呼叫端寫 toastSuccess(result.message) 也照樣只彈出一個空白 toast——
    // 圖示在、文字沒有，看起來像「成功了但系統說不出話」。全站有 6 個呼叫端這樣傳，
    // 包含工單放行與報表排程的立即執行，而且沒有任何錯誤訊號。2026-09-14 Wave R 實測撞到。
    function toastSuccess(msg) {
        var el = document.getElementById('successToastMsg');
        if (el) el.textContent = (typeof msg === 'string') ? msg : '';
        toast().show();
    }

    // 被 StalePlantContextFilter 擋下（畫面開在 A 廠、送出時已在 B 廠）時，讓使用者一鍵切回 A 廠
    // 並照原內容再送一次。原本只能照訊息重新整理——面板關閉、填的內容全部消失（2026-11-11 驗收 C3）。
    // 只切回「畫面開啟時的那個廠」：資料會落在使用者填表時看到的廠別，不會被改建到別廠。
    // 只重送一次（retried）：切回失敗或又被擋，就顯示訊息停下，不會無限重試。
    function offerSwitchBack(form, submitter, result, retried) {
        if (retried || result.code !== 'PLANT_CONTEXT_CHANGED' || !result.renderedPlant) return false;
        if (!confirm((result.message || '') + '\n\n' + t('PlantContextSwitchBackConfirm', result.renderedPlant))) return true;
        var body = new URLSearchParams();
        body.set('plantId', result.renderedPlant);
        body.set('__RequestVerificationToken', token());
        fetch('/UserSettings/SwitchPlant', {
            method: 'POST', body: body,
            headers: { 'X-Requested-With': 'XMLHttpRequest', 'RequestVerificationToken': token() }
        })
            .then(function () { submitPanel(form, submitter, true); })
            .catch(function (err) {
                console.error('switch plant back failed:', err);
                alert(result.message || t('OperationFailed'));
            });
        return true;
    }

    function submitPanel(form, submitter, retried) {
        // dispatcher 以 (event, element) 呼叫時第三個參數不是 true，一律當成第一次送出
        retried = retried === true;
        var fd = new FormData(form);
        if (submitter?.name) fd.set(submitter.name, submitter.value);
        fetch(form.action, {
            method: 'POST', body: fd,
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                'RequestVerificationToken': token(),
                // 這個畫面是在哪個廠別渲染的。StalePlantContextFilter 會比對它與
                // 送出當下的作用廠別——不一致就擋下來，因為 PlantId 是在 SaveChanges
                // 當下才填的，不擋的話單據會被建到**送出當下**的那個廠，而且沒有提示。
                // 觸發路徑：電腦開著 A 廠的表單 → 手機把廠別切到 B → 回電腦按存檔。
                'X-Plant-Context': plantContext()
            }
        })
            .then(function (r) { return r.text(); })
            .then(function (html) {
                // 成功契約（雙格式相容）：
                // ① <script> 格式：Content("<script>...refreshList...</script>")
                // ② JSON 格式：JsonOk / JsonFail → { success, message }
                // ③ Partial HTML：驗證失敗時回傳 _Form partial，重新顯示表單
                var trimmed = html.replace(/^\s+/, '');
                var head = trimmed.slice(0, 7).toLowerCase();

                // 格式①：<script> 成功
                if (head === '<script' && html.indexOf('refreshList') !== -1) {
                    toastSuccess();
                    refreshList();
                    return;
                }

                // 格式②：JSON { success, message }
                if (head === '{"succ' || head === '{"erro' || (trimmed.charAt(0) === '{')) {
                    try {
                        var result = JSON.parse(trimmed);
                        if (result && typeof result.success !== 'undefined') {
                            if (result.success) {
                                toastSuccess();
                                refreshList();
                            } else if (!offerSwitchBack(form, submitter, result, retried)) {
                                alert(result.message || t('OperationFailed'));
                            }
                            return;
                        }
                    } catch (e) { /* 不是合法 JSON，fall through */ }
                }

                // 格式③：Partial HTML（驗證失敗）
                var body = document.getElementById('sidePanelBody');
                destroyComponents(body);
                body.innerHTML = trimmed;
                body.scrollTop = 0;
                execScripts(body);
                armValidation(body);
            })
            .catch(function (err) { console.error('submitPanel error:', err); });
        return false;  // 阻止瀏覽器原生 form 提交，確保只走 AJAX 單次送出
    }

    function postAction(url, okReload) {
        fetch(url, {
            method: 'POST',
            headers: { 'RequestVerificationToken': token(), 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                if (d.success) {
                    offcanvas().hide();
                    toastSuccess();
                    if (okReload) setTimeout(function () { location.reload(); }, 800);
                } else {
                    alert(d.message);
                }
            })
            .catch(function () { alert(t('OperationFailed')); });
    }

    function submitApproval(id, controller) {
        if (!confirm(t('ConfirmSubmitApproval'))) return;
        postAction('/' + controller + '/Submit/' + encodeURIComponent(id), true);
    }

    function withdrawApproval(id, controller) {
        if (!confirm(t('ConfirmWithdraw'))) return;
        postAction('/' + controller + '/Withdraw/' + encodeURIComponent(id), true);
    }

    /** 通用刪除：deleteUrl 為 Delete action 基底（不含 id），id 會被 encode 後附加。 */
    function deleteItem(deleteUrl, id, confirmMsg) {
        if (!confirm(confirmMsg || t('ConfirmDelete', id))) return;
        fetch(deleteUrl.replace(/\/$/, '') + '/' + encodeURIComponent(id), {
            method: 'POST',
            headers: { 'RequestVerificationToken': token(), 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                if (d.success) { toastSuccess(); setTimeout(function () { location.reload(); }, 800); }
                else alert(d.message);
            });
    }

    // ── data-action 相容層（P2 CSP 回補 B0）────────────────────────────────
    // dispatcher（data-action.js）一律以 (event, el) 呼叫全域函式，參數從
    // el.dataset 讀取。下列 window 曝露採「雙呼叫慣例」：第一參數為 Event ⇒ 新式
    // （讀 dataset）；否則維持舊式位置參數。
    // 註（2026-06-27）：880 個 inline handler 存量已全數回補、CSP 已翻為 nonce-only
    // （見 .claude/reviews/csp-inline-handler-backfill-plan.md 結案）。舊式位置參數分支
    // 已無生產呼叫端（僅 Jest 測試引用），但對「程式化開面板」仍為便利 API，且完整退場
    // 需重寫測試、效益邊際，故刻意保留雙呼叫慣例，不退場。
    function isEvt(a) {
        return typeof Event !== 'undefined' && a instanceof Event;
    }

    window.armValidation = armValidation;
    window.execScripts = execScripts;
    window.hidePanel = hidePanel;
    window.closeSidePanel = hidePanel;   // alias：各 _Form 中的「關閉」按鈕使用此名稱（無參數，天然相容）
    window.refreshList = refreshList;     // 無參數，天然相容 dispatcher
    window.toastSuccess = toastSuccess;   // 可選訊息參數；非字串（如 dispatcher 的 event）視為無訊息

    // openPanel：新式 data-url / data-label（label 缺則取元素文字）；舊式 (url, label)
    window.openPanel = function (a, b) {
        if (isEvt(a)) {
            openPanel(b.dataset.url, b.dataset.label || b.textContent.trim());
            return false;   // 對齊舊 inline「openPanel(...); return false;」：阻止 <a href="#"> 跳頂
        }
        return openPanel(a, b);
    };

    // submitPanel：新式 el 即 form、submitter 取 event.submitter；舊式 (form, submitter)
    window.submitPanel = function (a, b) {
        if (isEvt(a)) {
            return submitPanel(b, a.submitter);
        }
        return submitPanel(a, b);
    };

    // confirmSubmitPanel：先 confirm（訊息取 data-confirm），確認後走 submitPanel 的 AJAX 契約。
    //
    // 給「面板裡的動作表單」用（送審／核准／揀料／過帳…）。這些表單原本只掛 confirmAction，
    // 而 confirmAction 只負責問，問完仍是瀏覽器原生送出——伺服端回的是 JSON，
    // 於是整頁導到一段 {"success":...} 原始文字：成功沒有 toast、失敗沒有訊息，
    // 單據狀態卻已經變了（人工驗收 W2-F3）。
    window.confirmSubmitPanel = function (e, el) {
        if (el.dataset.confirm && !confirm(el.dataset.confirm)) return false;
        return submitPanel(el, e && e.submitter);
    };

    // deleteItem：新式 data-url（Delete 基底）+ data-id；舊式 (deleteUrl, id)
    window.deleteItem = function (a, b) {
        if (isEvt(a)) {
            return deleteItem(b.dataset.url, b.dataset.id, b.dataset.confirm);
        }
        return deleteItem(a, b, arguments[2]);
    };

    // submitApproval / withdrawApproval：新式 data-id + data-controller；舊式 (id, controller)
    window.submitApproval = function (a, b) {
        if (isEvt(a)) {
            return submitApproval(b.dataset.id, b.dataset.controller);
        }
        return submitApproval(a, b);
    };
    window.withdrawApproval = function (a, b) {
        if (isEvt(a)) {
            return withdrawApproval(b.dataset.id, b.dataset.controller);
        }
        return withdrawApproval(a, b);
    };
})();
