// 自 ipqc-process.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    // ── URL 參數 ──────────────────────────────────────────────────────────────
    const params      = new URLSearchParams(location.search);
    const dispatchId  = params.get('dispatchId')  ?? '';
    const workOrderId = params.get('workOrderId') ?? '';
    const machineCode = params.get('machineCode') ?? '';
    const operationSeq = params.get('operationSeq') ?? '';
    const lotQty      = parseFloat(params.get('qty') ?? '1') || 1;

    // 檢驗別：FPI（首件檢查）走 dispatch-aware 端點以放行 PendingFPI；預設 IPQC（製程巡檢）
    const orderType   = (params.get('orderType') ?? 'IPQC').toUpperCase();
    const isFpi       = orderType === 'FPI';
    const backUrl     = isFpi ? '/mobile/mfg/fpi.html' : '/mobile/mfg/ipqc.html';

    // 品號與批量由伺服器依派工決定（ipqc/spec 帶 dispatchId）。原本這裡拿工單號頂替品號，
    // 品號專屬規格因此從未顯示過；送出時伺服器也只認派工上的品號。
    const productCode = params.get('productCode') ?? '';

    let calcSampleSize = 5;
    let specData       = null;
    let customCount    = 0;

    // ── 初始化 ────────────────────────────────────────────────────────────────
    (async () => {
        const user = await requireAuth();
        if (!user) return;

        if (!dispatchId) {
            showToast(t('ipqc_proc.err_no_dispatch'), 'warning');
            setTimeout(() => window.location.href = backUrl, 1500);
            return;
        }

        // FPI 模式調整標題/返回/文案/送出鈕（IPQC 維持 HTML 預設）
        if (isFpi) {
            document.title = t('ipqc_proc.fpi_title');
            const back = document.querySelector('nav.navbar [data-action="historyBack"]');
            // 返回鍵改走 historyBack（js/nav.js）：設 fallback 而非 href，
            // 才不會在硬跳轉時多推一筆歷史，造成上層頁的 history.back() 卡在迴圈。
            if (back) back.dataset.fallback = backUrl;
            const brand = document.querySelector('.navbar-brand');
            if (brand) brand.innerHTML = `<i class="bi bi-clipboard2-check me-2"></i>${t('ipqc_proc.fpi_title')}`;
            const banner = document.querySelector('.std-banner');
            if (banner) banner.innerHTML =
                t('ipqc_proc.fpi_rule');
            const btn = document.getElementById('btnSubmit');
            if (btn) btn.innerHTML = `<i class="bi bi-check-circle me-2"></i>${t('ipqc_proc.fpi_submit')}`;
        }

        document.getElementById('lblDispatch').textContent = `${workOrderId} — ${t('ipqc.op_seq')} ${operationSeq || '—'}`;
        document.getElementById('lblMachine').textContent  = `${t('ipqc_proc.machine_pre')}：${machineCode || '—'}`;

        await loadSpec();
    })();

    // ── 載入規格 ──────────────────────────────────────────────────────────────
    async function loadSpec() {
        const data = await apiGet(
            `/api/mobile/inv/ipqc/spec?dispatchId=${encodeURIComponent(dispatchId)}`);
        specData = data?.data ?? null;

        if (specData) {
            const vl = specData.verificationLevel ?? 4;
            document.getElementById('vlSelect').value = vl;
            calcSampleSize = specData.sampleSize ?? 5;
            updateCalcN(calcSampleSize);
        } else {
            await recalcN();
        }

        renderSpecItems(specData?.items ?? []);
        document.getElementById('sampleQty').value = calcSampleSize;
    }

    // ── VL 重算 ───────────────────────────────────────────────────────────────
    async function recalcN() {
        const vl   = parseInt(document.getElementById('vlSelect').value) || 4;
        const data = await apiGet(
            `/api/mobile/inv/iqc/sample-size?lotSize=${lotQty}&vl=${vl}`);
        calcSampleSize = data?.data?.sampleSize ?? calcSampleSize;
        updateCalcN(calcSampleSize);
        document.getElementById('sampleQty').value = calcSampleSize;
    }

    function updateCalcN(n) {
        document.getElementById('calcN').textContent = n;
        document.getElementById('nHint').textContent = n;
    }

    // ── 規格項目渲染 ──────────────────────────────────────────────────────────
    function renderSpecItems(items) {
        const container = document.getElementById('specItemRows');
        if (!items.length) {
            container.innerHTML = `
                <div class="text-center text-muted small py-2">
                    <i class="bi bi-info-circle me-1"></i>${t('ipqc_proc.no_spec')}
                </div>`;
            return;
        }
        container.innerHTML = items.map((it, i) => specRowHtml(i, it)).join('');
    }

    function specRowHtml(idx, it = null) {
        const seqLabel  = it ? it.seqNo : `C${++customCount}`;
        const itemName  = it?.itemName  ?? '';
        const uomCode   = it?.uomCode   ?? '';
        const lsl       = it?.lowerSpec  != null ? it.lowerSpec  : '';
        const tgt       = it?.targetValue != null ? it.targetValue : '';
        const usl       = it?.upperSpec  != null ? it.upperSpec  : '';
        const specItemId = it?.specItemId ?? '';
        // 規格項目的名稱／單位／上下限唯讀：伺服器判定只認規格主檔，
        // 讓人改了畫面上的上下限、卻得到與畫面不同的判定，只會更混亂。自訂項目才可以填。
        const ro = it ? 'readonly' : '';

        return `<div class="spec-row p-2 mb-2" data-idx="${idx}" data-spec-item-id="${specItemId}">
            <div class="d-flex justify-content-between align-items-center mb-1">
                <span class="small fw-semibold">${seqLabel}.
                    <input type="text" class="spec-name form-control-plaintext d-inline w-auto small u-border-bottom-1px-dashed-aaa u-min-width-100px"
                          
                           value="${esc(itemName)}" placeholder="${t('ipqc_proc.item_name_ph')}" ${ro}>
                </span>
                <input type="text" class="spec-uom form-control form-control-sm u-width-64px"
                       value="${esc(uomCode)}" placeholder="${t('ipqc_proc.uom_ph')}" ${ro}>
            </div>
            <div class="row g-1 mb-1">
                <div class="col-4">
                    <label class="form-label form-label-sm mb-0 text-muted">LSL</label>
                    <input type="number" class="form-control form-control-sm spec-lsl"
                           value="${lsl}" step="any" placeholder="${t('ipqc_proc.lsl')}" ${ro}>
                </div>
                <div class="col-4">
                    <label class="form-label form-label-sm mb-0 text-muted">Target</label>
                    <input type="number" class="form-control form-control-sm spec-tgt"
                           value="${tgt}" step="any" placeholder="${t('ipqc_proc.target_ph')}" ${ro}>
                </div>
                <div class="col-4">
                    <label class="form-label form-label-sm mb-0 text-muted">USL</label>
                    <input type="number" class="form-control form-control-sm spec-usl"
                           value="${usl}" step="any" placeholder="${t('ipqc_proc.usl')}" ${ro}>
                </div>
            </div>
            <div class="d-flex align-items-center gap-2">
                <div class="flex-grow-1">
                    <label class="form-label form-label-sm mb-0 fw-semibold">${t('ipqc_proc.measured')}</label>
                    <input type="number" class="form-control spec-measured" step="any"
                           inputmode="decimal" placeholder="${t('ipqc_proc.value_ph')}"
                           data-action-input="checkConform">
                </div>
                <div class="spec-conform-badge text-center u-min-width-40px u-font-size-1p1rem" title="${t('ipqc_proc.conform')}">—</div>
            </div>
        </div>`;
    }

    function addCustomRow() {
        const container = document.getElementById('specItemRows');
        // 若目前是「無規格」提示，先清空
        if (container.querySelector('.text-muted')) container.innerHTML = '';
        const div = document.createElement('div');
        div.innerHTML = specRowHtml(Date.now());
        container.appendChild(div.firstChild);
    }

    function checkConform(e, input) {
        const row   = input.closest('.spec-row');
        // 空白上下限是「不限」。原本寫 parseFloat(...) ?? null，但 parseFloat('') 是 NaN 不是 null，
        // `val >= NaN` 恆為 false——自訂項目只填一邊上下限，量測值怎麼填都顯示 ✗。
        const lsl   = parseFloat(row.querySelector('.spec-lsl')?.value);
        const usl   = parseFloat(row.querySelector('.spec-usl')?.value);
        const val   = parseFloat(input.value);
        const badge = row.querySelector('.spec-conform-badge');

        if (isNaN(val)) {
            badge.textContent = '—'; badge.className = 'spec-conform-badge text-center';
        } else {
            const ok = (isNaN(lsl) || val >= lsl) && (isNaN(usl) || val <= usl);
            badge.textContent = ok ? '✓' : '✗';
            badge.className   = `spec-conform-badge text-center fw-bold ${ok ? 'conform-ok' : 'conform-ng'}`;
        }
        updateResult();
    }

    function esc(str) {
        return String(str ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    // ── 結果預覽 ──────────────────────────────────────────────────────────────
    function updateResult() {
        const fail    = parseFloat(document.getElementById('failQty').value)   || 0;
        // 與伺服器同一條規則（MobileInspectionJudge.Judge）：不良數 > 0 或任一量測超規格即不合格。
        const outOfSpec = !!document.querySelector('#specItemRows .conform-ng');
        const preview = document.getElementById('resultPreview');
        const warn    = document.getElementById('failWarn');
        // 🚨 warn 的初始隱藏來自 HTML 上的 u-display-none（帶 !important），
        //    style.display='block' 蓋不過它——這條警告橫幅在修掉之前**從來沒出現過**。
        //    一律 classList 切（R14）。
        if (fail > 0 || outOfSpec) {
            preview.className = 'result-fail fw-bold';
            preview.textContent = t('ipqc_proc.fail');
            warn.classList.remove('u-display-none');
        } else {
            preview.className = 'result-pass fw-bold';
            preview.textContent = t('ipqc_proc.pass');
            warn.classList.add('u-display-none');
        }
    }

    // ── 送出 ──────────────────────────────────────────────────────────────────
    async function submitIpqc() {
        const sampleQty = parseFloat(document.getElementById('sampleQty').value) || 0;
        const failQty   = parseFloat(document.getElementById('failQty').value)   || 0;
        const vl        = parseInt(document.getElementById('vlSelect').value)    || 4;

        if (sampleQty <= 0) { showToast(t('ipqc_proc.err_sample'), 'warning'); return; }
        if (sampleQty < calcSampleSize) {
            if (!confirm(tf('iqc.confirm_under', sampleQty, calcSampleSize))) return;
        }

        // 收集規格量測項目
        const items = [];
        document.querySelectorAll('#specItemRows .spec-row').forEach((row, i) => {
            const name = row.querySelector('.spec-name')?.value?.trim();
            if (!name) return;
            const measured = parseFloat(row.querySelector('.spec-measured')?.value);
            const lsl      = parseFloat(row.querySelector('.spec-lsl')?.value);
            const tgt      = parseFloat(row.querySelector('.spec-tgt')?.value);
            const usl      = parseFloat(row.querySelector('.spec-usl')?.value);
            const ok = isNaN(measured)
                ? null
                : (isNaN(lsl) || measured >= lsl) && (isNaN(usl) || measured <= usl);

            items.push({
                specItemId:    row.dataset.specItemId || null,
                seqNo:         i + 1,
                itemName:      name,
                uomCode:       row.querySelector('.spec-uom')?.value?.trim() || null,
                lowerSpec:     isNaN(lsl) ? null : lsl,
                targetValue:   isNaN(tgt) ? null : tgt,
                upperSpec:     isNaN(usl) ? null : usl,
                measuredValue: isNaN(measured) ? null : measured,
                isConforming:  ok
            });
        });

        const btn = document.getElementById('btnSubmit');
        setLoading(btn, true);

        let res;
        if (isFpi) {
            // FPI：走 dispatch-aware 端點（clientId 幂等）；Pass→派工 ReadyToStart 放行、Fail→OnHold 重調
            res = await apiCall('POST', '/api/mobile/inspection-orders', {
                dispatchId:      dispatchId,
                orderType:       1,                       // InspectionOrderType.FPI（無字串 enum converter，傳數字）
                failQty:         Math.round(failQty),
                actualSampleQty: Math.round(sampleQty),
                clientId:        crypto.randomUUID(),
                clientTimestamp: new Date().toISOString(),
                items
            });
        } else {
            res = await apiCall('POST', '/api/mobile/inv/ipqc', {
                inspectionType:    'IPQC',
                sourceDocId:       dispatchId,
                productCode:       productCode,
                lotSize:           lotQty,
                sampleSize:        calcSampleSize,
                verificationLevel: vl,
                actualSampleQty:   sampleQty,
                failQty,
                status:            failQty >= 1 ? 'Fail' : 'Pass',
                remarks:           document.getElementById('remarks').value.trim() || null,
                items
            });
        }
        setLoading(btn, false);
        if (!res) return;

        // 以伺服器的判定為準：量測超規格時即使不良數填 0 也是不合格。
        const isPass = res.data?.status ? res.data.status === 'Pass' : failQty === 0;
        const okMsg  = isFpi ? t('ipqc_proc.fpi_pass') : t('ipqc_proc.ipqc_pass');
        const ngMsg  = isFpi ? t('ipqc_proc.fpi_fail') : t('ipqc_proc.ipqc_fail');
        showToast(res.message ?? (isPass ? okMsg : ngMsg), isPass ? 'success' : 'danger');

        setTimeout(() => window.location.href = backUrl, 1800);
    }
