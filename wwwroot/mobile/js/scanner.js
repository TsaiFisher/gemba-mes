/**
 * scanner.js — 條碼掃描共用模組（mobile-pwa.md §6）
 *
 * 支援三種輸入模式（優先序）：
 *   1. BarcodeDetector API（原生，Chrome/Android）
 *   2. Html5Qrcode（相機，跨平台 fallback）
 *   3. 鍵盤楔型（Keyboard Wedge）— 外接掃描槍模擬鍵盤輸入
 *
 * 使用方式：
 *   const sc = new BarcodeScanner({ onScan: code => doSomething(code) });
 *   sc.startCamera('reader-div-id');  // 啟動相機掃描
 *   sc.stopCamera();
 *   sc.enableWedge();   // 全域掃描槍監聽（預設已啟動）
 *   sc.disableWedge();
 */

'use strict';

class BarcodeScanner {
    /**
     * @param {object} opts
     * @param {function(string):void} opts.onScan          - 掃描成功回呼（傳入條碼字串）
     * @param {number}  [opts.wedgeMinLength=5]            - 判定為掃描槍的最少字元數
     * @param {number}  [opts.wedgeIntervalThresholdMs=80] - 相鄰字元間距上限（ms），可由系統參數覆寫
     * @param {boolean} [opts.wedgeAutoEnable=true]        - 初始化時自動啟動楔型監聽
     */
    constructor(opts = {}) {
        this._onScan  = opts.onScan || (() => {});
        this._minLen  = opts.wedgeMinLength ?? 5;
        this._maxGap  = opts.wedgeIntervalThresholdMs ?? 80;
        this._wedgeOn = false;

        this._html5Scanner = null;
        this._isScanning   = false;

        // 鍵盤楔型狀態
        this._buffer    = '';
        this._lastKeyAt = 0;
        this._wedgeFn   = this._onKeyDown.bind(this);

        if (opts.wedgeAutoEnable !== false) this.enableWedge();
    }

    // ── 相機掃描 ─────────────────────────────────────────────────────────────

    /**
     * 啟動相機掃描（BarcodeDetector 優先，fallback Html5Qrcode）
     * @param {string} containerId - 渲染視窗的 div id
     * @param {object} [html5Opts] - Html5Qrcode 設定覆寫
     */
    async startCamera(containerId, html5Opts = {}) {
        if (this._isScanning) return;
        this._isScanning = true;

        if (this._tryBarcodeDetector(containerId)) return;
        await this._startHtml5Qrcode(containerId, html5Opts);
    }

    stopCamera() {
        if (this._html5Scanner) {
            this._html5Scanner.stop().catch(() => {}).finally(() => {
                this._html5Scanner.clear().catch(() => {});
                this._html5Scanner = null;
            });
        }
        this._isScanning = false;
    }

    // ── BarcodeDetector API ───────────────────────────────────────────────────

    _tryBarcodeDetector(containerId) {
        if (!('BarcodeDetector' in window)) return false;

        const container = document.getElementById(containerId);
        if (!container) return false;

        container.innerHTML = '<video class="u-width-100pct u-border-radius-8px" id="_sc_video" playsinline autoplay muted></video>';
        const video = document.getElementById('_sc_video');

        const detector = new BarcodeDetector({
            formats: ['code_128', 'qr_code', 'ean_13', 'data_matrix']
        });

        navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } })
            .then(stream => {
                video.srcObject = stream;
                this._bdStream = stream;
                this._bdActive = true;
                this._bdLoop(detector, video);
            })
            .catch(() => {
                // 相機權限被拒，清除並 fallback
                container.innerHTML = '';
                this._isScanning = false;
                this._startHtml5Qrcode(containerId);
            });

        return true;
    }

    _bdLoop(detector, video) {
        if (!this._bdActive) return;
        detector.detect(video).then(codes => {
            if (codes.length > 0) {
                const code = codes[0].rawValue;
                this._emit(code);
            }
        }).catch(() => {}).finally(() => {
            if (this._bdActive) requestAnimationFrame(() => this._bdLoop(detector, video));
        });
    }

    _stopBdStream() {
        this._bdActive = false;
        if (this._bdStream) {
            this._bdStream.getTracks().forEach(t => t.stop());
            this._bdStream = null;
        }
    }

    // ── Html5Qrcode fallback ──────────────────────────────────────────────────

    async _startHtml5Qrcode(containerId, opts = {}) {
        if (typeof Html5Qrcode === 'undefined') {
            console.error('[Scanner] Html5Qrcode 未載入');
            return;
        }
        const sc = new Html5Qrcode(containerId);
        this._html5Scanner = sc;

        const config = Object.assign({
            fps: 10,
            qrbox: { width: 250, height: 150 },
            formatsToSupport: [
                Html5QrcodeSupportedFormats.CODE_128,
                Html5QrcodeSupportedFormats.QR_CODE,
                Html5QrcodeSupportedFormats.EAN_13
            ]
        }, opts);

        try {
            await sc.start(
                { facingMode: 'environment' },
                config,
                (decoded) => { this._emit(decoded); },
                () => {}
            );
        } catch (e) {
            console.error('[Scanner] Html5Qrcode 啟動失敗', e);
            this._isScanning = false;
        }
    }

    // ── Keyboard Wedge（外接掃描槍）──────────────────────────────────────────
    // 規格見 mobile-pwa.md §6.3：capture 階段監聽 keydown、一致性間距判定

    enableWedge() {
        if (this._wedgeOn) return;
        window.addEventListener('keydown', this._wedgeFn, true); // capture
        this._wedgeOn = true;
    }

    disableWedge() {
        if (!this._wedgeOn) return;
        window.removeEventListener('keydown', this._wedgeFn, true);
        this._wedgeOn = false;
    }

    _onKeyDown(e) {
        const now = Date.now();
        const gap = now - this._lastKeyAt;
        this._lastKeyAt = now;

        if (e.key === 'Enter') {
            const candidate = this._buffer;
            this._buffer = '';

            // 長度門檻 + 必需由非 Enter 字元構成
            if (candidate.length >= this._minLen) {
                e.preventDefault(); // 阻止 Enter 落入聚焦 input
                this._emit(candidate);
            }
            return;
        }

        if (e.key.length === 1) {
            if (gap > this._maxGap && this._buffer.length > 0) {
                // 間距過大 → 視為人工輸入，清除暫存並讓字元進 input
                this._buffer = '';
                return;
            }
            this._buffer += e.key;

            // 若是掃描槍輸入（間距一致且短），阻止字元落入聚焦 input
            // 判定：暫存已有字元且此鍵間距在閾值內
            if (this._buffer.length > 1 && gap <= this._maxGap) {
                e.preventDefault();
            }
        } else {
            // 非列印字元（Shift、Ctrl…）→ 若緩衝非空且間距小，繼續；否則清除
            if (gap > this._maxGap) this._buffer = '';
        }
    }

    // ── 共用 emit ────────────────────────────────────────────────────────────

    _emit(code) {
        if (!code) return;
        try { this._onScan(code); } catch (e) { console.error('[Scanner] onScan 錯誤', e); }
    }

    destroy() {
        this.stopCamera();
        this._stopBdStream();
        this.disableWedge();
    }
}
