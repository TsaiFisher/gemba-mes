// 自 stock.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
    let scanner   = null;
    let scanModal = null;
    let cameraOpen = false;
    let uomDecimals = {};

    // 模式切換 → 更新 label / placeholder
    document.querySelectorAll('input[name="queryMode"]').forEach(radio => {
        radio.addEventListener('change', () => {
            const isLoc = document.getElementById('modeLocation').checked;
            document.getElementById('inputLabel').textContent =
                isLoc ? t('stock.label_location') : t('stock.label_product');
            document.getElementById('productInput').placeholder =
                isLoc ? t('stock.ph_location') : t('stock.ph_product');
            document.getElementById('productInput').value = '';
            document.getElementById('resultSection').classList.add('u-display-none');
            document.getElementById('emptyState').classList.add('u-display-none');
        });
    });

    (async () => {
        const user = await requireAuth();
        if (!user) return;

        uomDecimals = await getUomDecimalsMap();

        scanModal = new bootstrap.Modal(document.getElementById('scannerModal'));
        scanner = new BarcodeScanner({
            onScan: async code => {
                document.getElementById('productInput').value = code;
                if (cameraOpen) { scanner.stopCamera(); cameraOpen = false; }
                scanModal.hide();
                await queryStock();
            }
        });

        document.getElementById('productInput').addEventListener('keydown', e => {
            if (e.key === 'Enter') queryStock();
        });
    })();

    function openScanner() {
        scanModal.show();
        scanner.startCamera('scanModalReader');
        cameraOpen = true;
    }

    document.getElementById('scannerModal').addEventListener('hidden.bs.modal', () => {
        scanner.stopCamera();
        cameraOpen = false;
    });

    async function queryStock() {
        const code  = document.getElementById('productInput').value.trim();
        const isLoc = document.getElementById('modeLocation').checked;
        if (!code) { showToast(isLoc ? t('stock.err_location') : t('stock.err_product'), 'warning'); return; }

        document.getElementById('resultSection').classList.add('u-display-none');
        document.getElementById('emptyState').classList.add('u-display-none');

        if (isLoc) {
            await queryByLocation(code);
        } else if (!(await queryByBox(code))) {
            await queryByProduct(code);
        }
    }

    // 掃到箱子上的固定 QR（箱號，container-tracking.md §9）→ 顯示箱子目前裝的品號、批號與各儲位數量。
    // 不是箱號或是空箱（404）回 false，照原樣當品號查。直接用 fetch：apiGet 遇 404 會跳錯誤 Toast。
    async function queryByBox(code) {
        let box;
        try {
            const r = await fetch(`/api/warehouse/container-unit/${encodeURIComponent(code)}`, { credentials: 'include' });
            if (!r.ok) return false;
            box = await r.json();
        } catch (e) { return false; }

        const prod = await apiGet(`/api/warehouse/product/${encodeURIComponent(box.productCode)}`).catch(() => null);
        const uom  = prod?.stockUom ?? '';
        const total = box.locations.reduce((s, l) => s + (l.qty ?? 0), 0);
        document.getElementById('prodCode').textContent  = box.productCode;
        document.getElementById('prodName').textContent  = tf('stock.box_of', box.containerUnitId, box.lotNo);
        document.getElementById('totalQty').textContent  = formatQty(total, uom, uomDecimals);
        document.getElementById('totalUom').textContent  = uom;
        document.getElementById('totalSection').style.display = '';
        document.getElementById('detailHeader').innerHTML = `<i class="bi bi-geo-alt me-1"></i>${t('stock.header_by_loc')}`;

        document.getElementById('locationRows').innerHTML = box.locations.map(l => `
            <div class="d-flex justify-content-between align-items-center py-2 border-bottom">
                <div>
                    <div class="fw-semibold small">${escHtml(l.locationCode)}</div>
                    <div class="text-muted u-font-size-p75rem">${t('stock.wh')}${escHtml(l.whCode || '—')} | ${t('stock.lot')}${escHtml(box.lotNo)}</div>
                </div>
                <div class="text-end">
                    <div class="fw-bold text-success">${formatQty(l.qty, uom, uomDecimals)}</div>
                    <div class="text-muted small">${uom}</div>
                </div>
            </div>`).join('');

        document.getElementById('resultSection').classList.remove('u-display-none');
        return true;
    }

    function escHtml(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    async function queryByProduct(code) {
        const data = await apiGet(`/api/warehouse/stock/${encodeURIComponent(code)}`);
        if (!data || !data.length) {
            document.getElementById('emptyState').classList.remove('u-display-none');
            return;
        }
        // 補查品項 UOM（stock endpoint 無此欄位）；本查詢固定為單一品號，故所有列共用同一 UOM，加總安全。
        const prod = await apiGet(`/api/warehouse/product/${encodeURIComponent(code)}`).catch(() => null);
        const uom  = prod?.stockUom ?? data[0].uomCode ?? '';

        const total = data.reduce((s, r) => s + (r.qtyOnHand ?? 0), 0);
        document.getElementById('prodCode').textContent  = code;
        document.getElementById('prodName').textContent  = '';
        document.getElementById('totalQty').textContent  = formatQty(total, uom, uomDecimals);
        document.getElementById('totalUom').textContent  = uom;
        document.getElementById('totalSection').style.display = '';
        document.getElementById('detailHeader').innerHTML = `<i class="bi bi-geo-alt me-1"></i>${t('stock.header_by_loc')}`;

        document.getElementById('locationRows').innerHTML = data.map(r => `
            <div class="d-flex justify-content-between align-items-center py-2 border-bottom">
                <div>
                    <div class="fw-semibold small">${r.locationCode}</div>
                    <div class="text-muted u-font-size-p75rem">${t('stock.wh')}${r.whCode ?? '—'}${r.lotNo ? ` | ${t('stock.lot')}${r.lotNo}` : ''}</div>
                </div>
                <div class="text-end">
                    <div class="fw-bold text-success">${formatQty(r.qtyOnHand, uom, uomDecimals)}</div>
                    <div class="text-muted small">${uom}</div>
                </div>
            </div>`).join('');

        document.getElementById('resultSection').classList.remove('u-display-none');
    }

    async function queryByLocation(code) {
        const data = await apiGet(`/api/warehouse/stock-by-location/${encodeURIComponent(code)}`);
        if (!data || !data.length) {
            document.getElementById('emptyState').classList.remove('u-display-none');
            return;
        }
        document.getElementById('prodCode').textContent   = code;
        document.getElementById('prodName').textContent   = t('stock.loc_detail');
        document.getElementById('totalSection').style.display = 'none';
        document.getElementById('detailHeader').innerHTML = `<i class="bi bi-box me-1"></i>${t('stock.header_by_prod')}`;

        document.getElementById('locationRows').innerHTML = data.map(r => `
            <div class="d-flex justify-content-between align-items-center py-2 border-bottom">
                <div>
                    <div class="fw-semibold small">${r.productCode}</div>
                    <div class="text-muted u-font-size-p75rem">${r.productName}${r.lotNo ? ` | ${t('stock.lot')}${r.lotNo}` : ''}</div>
                </div>
                <div class="text-end">
                    <div class="fw-bold text-success">${formatQty(r.qtyOnHand, r.uomCode, uomDecimals)}</div>
                    <div class="text-muted small">${r.uomCode}</div>
                </div>
            </div>`).join('');

        document.getElementById('resultSection').classList.remove('u-display-none');
    }
