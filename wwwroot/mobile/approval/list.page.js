// 待我簽核。自 list.html 外部化——CSP 的 script-src 走 nonce，靜態 HTML 帶不了 nonce。
//
// ⚠️ 這一頁**刻意用 DOM API 逐格建，不用 innerHTML**：單號與申請人姓名都是使用者可控的資料，
//    而手機端沒有共用的跳脫函式（各頁各寫一個 esc()）。少寫一個跳脫的代價是 XSS，
//    而 textContent 連跳脫這件事都不必想。

(async () => {
    const user = await requireAuth();
    if (!user) return;
    await load();
})();

async function load() {
    const loading = document.getElementById('loading');
    const empty   = document.getElementById('emptyState');
    const errBox  = document.getElementById('errorState');
    const list    = document.getElementById('list');

    list.replaceChildren();
    errBox.classList.add('d-none');
    empty.classList.add('d-none');
    loading.classList.remove('d-none');

    const data = await apiCall('GET', '/api/mobile/approval/pending');
    loading.classList.add('d-none');

    // apiCall 在 401 時已自行導向登入頁，回 null；其餘失敗也回 null。
    // 這裡不能把 null 當成「沒有待簽核」——那會讓連線失敗看起來像事情都辦完了。
    if (!data) {
        document.getElementById('errorText').textContent = t('approval.load_failed');
        errBox.classList.remove('d-none');
        return;
    }

    const items = data.items || [];
    if (items.length === 0) { empty.classList.remove('d-none'); return; }

    items.forEach(it => list.appendChild(card(it)));
}

function card(it) {
    const wrap = document.createElement('div');
    wrap.className = 'card shadow-sm';

    const body = document.createElement('div');
    body.className = 'card-body py-2 px-3';

    const head = document.createElement('div');
    head.className = 'd-flex justify-content-between align-items-center mb-1';

    const type = document.createElement('span');
    type.className = 'badge bg-secondary';
    // 單別名稱由伺服端給（documentTypeLabel），與桌機簽核畫面共用 IApprovalDocumentTypeLabels 一份；
    // 手機不另存譯名字典，否則同一張單兩邊叫法會分岔。舊伺服器沒有這個欄位時退回原始型別名。
    type.textContent = it.documentTypeLabel || it.documentType;
    head.appendChild(type);

    if (it.stepSequence != null) {
        const step = document.createElement('span');
        step.className = 'badge bg-light text-dark border';
        step.textContent = t('approval.step_fmt').replace('{0}', it.stepSequence);
        head.appendChild(step);
    }
    body.appendChild(head);

    const docNo = document.createElement('div');
    docNo.className = 'fw-semibold text-break';
    docNo.textContent = it.documentId;
    body.appendChild(docNo);

    const meta = document.createElement('div');
    meta.className = 'text-muted small';
    meta.textContent = (it.requester || '') + ' · ' + fmtDate(it.createdAt);
    body.appendChild(meta);

    if (it.canDecideOnMobile) {
        // 手機簽得了的單別：主要動作是「查看並簽核」，桌機連結降為次要。
        // 順序刻意如此——把桌機連結擺在前面會讓人以為手機還是不能簽。
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-primary btn-sm mt-2 w-100';
        btn.dataset.action    = 'reviewApproval';
        btn.dataset.requestId = it.id;
        btn.textContent = t('approval.review');
        body.appendChild(btn);
    }

    if (it.detailsUrl) {
        const a = document.createElement('a');
        a.className = it.canDecideOnMobile
            ? 'btn btn-link btn-sm mt-1 w-100'
            : 'btn btn-outline-primary btn-sm mt-2 w-100';
        a.href = it.detailsUrl;
        // 🚨 一定要另開分頁。直接導覽會把 PWA 從手機殼帶到桌機頁，
        //    而 standalone 模式沒有網址列，使用者只剩 OS 手勢能回來。
        a.target = '_blank';
        a.rel = 'noopener';
        a.textContent = t('approval.open_desktop');
        body.appendChild(a);
    } else if (!it.canDecideOnMobile) {
        // 沒有登錄連結的單別要**明講**，不能只是不給按鈕。
        // 桌機原本就是「點了靜靜沒反應」，那個症狀看起來像畫面壞了。
        const note = document.createElement('div');
        note.className = 'text-muted small fst-italic mt-2';
        note.textContent = t('approval.no_link');
        body.appendChild(note);
    }

    if (!it.canDecideOnMobile) {
        // 為什麼這張單在手機上簽不了要**說出來**。只是少一顆按鈕的話，
        // 使用者會以為是自己的權限問題或畫面壞了，而那兩件事的處理方式完全不同。
        const why = document.createElement('div');
        why.className = 'text-muted small fst-italic mt-1';
        why.textContent = t('approval.desktop_only');
        body.appendChild(why);
    }

    wrap.appendChild(body);
    return wrap;
}

function fmtDate(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    const p = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}


// ════════════════════════════════════════════════════════════════════════════
// 簽核面板
//
// ⚠️ 這一段同樣**全部用 DOM API 建**，理由與檔頭那段一樣：摘要裡的每一個字串
//    （單號、供應商名稱、備註、品號）都是使用者可控的資料。innerHTML 少跳脫一處就是 XSS。
// ════════════════════════════════════════════════════════════════════════════

let currentRequestId = null;

/** data-action 進入點：卡片上的「查看並簽核」。 */
async function reviewApproval(e, el) {
    const requestId = el.dataset.requestId;
    if (!requestId) return;

    const data = await apiCall('GET', `/api/mobile/approval/${encodeURIComponent(requestId)}/summary`);
    if (!data) return;                     // apiCall 已處理 401／錯誤提示

    currentRequestId = requestId;
    renderSummary(data);
    modal().show();
}

function modal() {
    return bootstrap.Modal.getOrCreateInstance(document.getElementById('reviewModal'));
}

function renderSummary(d) {
    const bodyEl = document.getElementById('reviewBody');
    const actions = document.getElementById('reviewActions');
    bodyEl.replaceChildren();

    document.getElementById('reviewDocNo').textContent = d.documentId || '';

    if (!d.supported) {
        // 兩種都走到這裡，但**訊息必須不同**：一種是「這個單別還沒做」，
        // 另一種是「這張單不見了或不在你的廠」——處理方式完全不同。
        const p = document.createElement('div');
        p.className = 'alert alert-warning small mb-0';
        p.textContent = d.unavailable ? t('approval.unavailable') : t('approval.desktop_only');
        bodyEl.appendChild(p);
        actions.classList.add('d-none');   // 看不到內容就不給簽
        return;
    }
    actions.classList.remove('d-none');

    // ── 金額 ────────────────────────────────────────────────────────────────
    // ⚠️ null 是「這個單別沒有金額概念」（例如請購單），**不是 0**。
    //    兩者顯示成一樣的話，「金額 0 元的採購單」與請購單在畫面上就分不出來了。
    const amt = document.createElement('div');
    amt.className = 'mb-3';
    if (d.amountLocal == null) {
        amt.className += ' text-muted fst-italic small';
        amt.textContent = t('approval.no_amount');
    } else {
        amt.className += ' fs-4 fw-bold';
        amt.textContent = `${d.currencyCode || ''} ${Number(d.amountLocal).toLocaleString()}`.trim();
    }
    bodyEl.appendChild(amt);

    // ── 主檔欄位 ────────────────────────────────────────────────────────────
    const rows = [];
    if (d.plantId) rows.push({ label: t('approval.plant'), value: d.plantId });
    (d.rows || []).forEach(r => rows.push(r));

    if (rows.length) {
        const dl = document.createElement('dl');
        dl.className = 'row small mb-3';
        rows.forEach(r => {
            const dt = document.createElement('dt');
            dt.className = 'col-4 text-muted fw-normal';
            dt.textContent = r.label;
            const dd = document.createElement('dd');
            dd.className = 'col-8 mb-1';
            dd.textContent = r.value;
            dl.append(dt, dd);
        });
        bodyEl.appendChild(dl);
    }

    // ── 明細 ────────────────────────────────────────────────────────────────
    const lines = d.lines || [];
    if (lines.length) {
        const h = document.createElement('div');
        h.className = 'fw-semibold small mb-1';
        h.textContent = t('approval.lines');
        bodyEl.appendChild(h);

        const ul = document.createElement('ul');
        ul.className = 'list-group list-group-flush mb-2';
        lines.forEach(l => {
            const li = document.createElement('li');
            li.className = 'list-group-item px-0 py-1 small d-flex justify-content-between';
            const left = document.createElement('span');
            left.className = 'text-break me-2';
            left.textContent = l.description;
            const right = document.createElement('span');
            right.className = 'text-nowrap text-muted';
            const qty = l.qty == null ? '' : `${Number(l.qty).toLocaleString()} ${l.uomCode || ''}`.trim();
            const amount = l.amount == null ? '' : Number(l.amount).toLocaleString();
            right.textContent = [qty, amount].filter(Boolean).join(' · ');
            li.append(left, right);
            ul.appendChild(li);
        });
        bodyEl.appendChild(ul);

        // 有截斷就**明講還有幾筆**。靜靜只顯示前 20 筆，會讓人以為整張單就這麼多。
        if (d.totalLineCount > lines.length) {
            const more = document.createElement('div');
            more.className = 'text-muted small fst-italic';
            more.textContent = t('approval.more_lines_fmt')
                .replace('{0}', d.totalLineCount - lines.length);
            bodyEl.appendChild(more);
        }
    }

    // ── 桌機連結（永遠給，摘要本來就只是摘要）─────────────────────────────
    if (d.detailsUrl) {
        const a = document.createElement('a');
        a.className = 'btn btn-link btn-sm px-0';
        a.href = d.detailsUrl;
        a.target = '_blank';               // 見卡片那段：不得把 PWA 帶離手機殼
        a.rel = 'noopener';
        a.textContent = t('approval.open_desktop');
        bodyEl.appendChild(a);
    }

    document.getElementById('reviewComment').value = '';
}

/** data-action：核准。 */
function approveApproval() { return decide('approve'); }

/** data-action：駁回。 */
function rejectApproval()  { return decide('reject'); }

async function decide(action) {
    if (!currentRequestId) return;

    // 🚨 離線時**當場擋下並說清楚**。簽核不會進離線佇列（offline-queue.js 會擲出），
    //    但那個例外對使用者是看不見的——他只會看到按鈕沒反應。
    if (!navigator.onLine) { showToast(t('approval.offline'), 'warning'); return; }

    const confirmKey = action === 'approve' ? 'approval.confirm_approve' : 'approval.confirm_reject';
    if (!confirm(t(confirmKey))) return;

    const comment = document.getElementById('reviewComment').value.trim();
    const res = await apiCall('POST', `/api/mobile/approval/${encodeURIComponent(currentRequestId)}/decide`,
                              { action, comment: comment || null });

    modal().hide();

    if (!res) {
        // 409＝這張單的狀態已經變了（被撤回／被別人簽掉／步驟推進）。
        // ⚠️ **這裡刻意不再跳一次 toast**：api.js 在非 2xx 時已經 showToast(err.message)，
        //    而伺服端就是為此才回傳在地化的 message。自己再跳一則會變成兩則疊在一起。
        //    要做的只有重新載入，讓使用者看到「現在」真正的清單。
    } else {
        showToast(t(action === 'approve' ? 'approval.done_approve' : 'approval.done_reject'), 'success');
    }

    currentRequestId = null;
    await load();                          // 兩種情況都要重載：成功則該單消失，衝突則狀態已變
}
