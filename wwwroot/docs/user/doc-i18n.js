/* 使用手冊中英切換
   ------------------------------------------------------------------
   語言來源優先序：網址 ?lang= → localStorage → 預設 zh。
   系統以 HelpDocResolver 依 UI 語系帶入 ?lang=en；使用者亦可用頁內切換鈕。

   本檔以 <script src> 同步載入於 <head>：屬性設定必須早於 body 渲染，
   否則會先閃現中文再跳成英文。DOM 相關套用（title/alt/img）延到
   DOMContentLoaded。CSP 不允許內聯 script，故一律走外部檔。 */
(function () {
    'use strict';

    var KEY = 'docLang';

    function normalize(v) { return v === 'en' ? 'en' : 'zh'; }

    function resolveInitial() {
        var fromUrl = null;
        try {
            fromUrl = new URLSearchParams(window.location.search).get('lang');
        } catch (e) { /* 舊瀏覽器無 URLSearchParams，退回 localStorage */ }

        var lang;
        if (fromUrl) {
            lang = normalize(fromUrl);
            try { window.localStorage.setItem(KEY, lang); } catch (e) { /* 隱私模式 */ }
        } else {
            var stored = null;
            try { stored = window.localStorage.getItem(KEY); } catch (e) { /* ignore */ }
            lang = normalize(stored || 'zh');
        }
        return lang;
    }

    /* 只切換 <html> 上的屬性，顯示與否交給 doc-i18n.css */
    function setHtmlLang(lang) {
        var html = document.documentElement;
        html.setAttribute('data-doc-lang', lang);
        html.setAttribute('lang', lang === 'en' ? 'en' : 'zh-TW');
    }

    /* 無法用 span 包裝的部分（<title>、alt、截圖來源）以 data-* 保存另一語版本。
       首次套用前先把原始（中文）值備份到 data-*-zh，之後可雙向切換。 */
    function applyAttributes(lang) {
        var root = document.documentElement;

        if (root.hasAttribute('data-title-en')) {
            if (!root.hasAttribute('data-title-zh')) {
                root.setAttribute('data-title-zh', document.title);
            }
            document.title = lang === 'en'
                ? root.getAttribute('data-title-en')
                : root.getAttribute('data-title-zh');
        }

        var alts = document.querySelectorAll('[data-alt-en]');
        for (var i = 0; i < alts.length; i++) {
            var el = alts[i];
            if (!el.hasAttribute('data-alt-zh')) {
                el.setAttribute('data-alt-zh', el.getAttribute('alt') || '');
            }
            el.setAttribute('alt', lang === 'en'
                ? el.getAttribute('data-alt-en')
                : el.getAttribute('data-alt-zh'));
        }

        var imgs = document.querySelectorAll('img[data-src-en]');
        for (var j = 0; j < imgs.length; j++) {
            var img = imgs[j];
            if (!img.hasAttribute('data-src-zh')) {
                img.setAttribute('data-src-zh', img.getAttribute('src') || '');
            }
            if (!img.hasAttribute('data-en-fallback')) {
                img.setAttribute('data-en-fallback', '1');
                /* 英文截圖尚未產出（或該畫面當時取不到）時退回中文截圖，
                   而不是留一張破圖——破圖比看到中文介面更難懂。 */
                img.addEventListener('error', function () {
                    var zh = this.getAttribute('data-src-zh');
                    if (zh && this.getAttribute('src') !== zh) this.setAttribute('src', zh);
                });
            }
            var next = lang === 'en'
                ? img.getAttribute('data-src-en')
                : img.getAttribute('data-src-zh');
            if (next && img.getAttribute('src') !== next) {
                img.setAttribute('src', next);
            }
        }
    }

    /* 站內手冊連結沿用目前語言，避免點下一頁又跳回中文。
       只處理相對的 .html 連結（外部連結與返回系統的 "/" 不動）。

       連結本身可能自己就帶參數——教學課程卡是 player.html?flow=…，
       整條課程靠那個參數決定播哪一條。因此只能置換 lang，不能重建整段 query：
       早期版本用 href.split('?')[0] 取 base，等於把 flow 靜默吃掉，
       畫面上看不出異常（連結還在、還能點），只是點進去變成空的課程清單。 */
    function decorateLinks(lang) {
        var links = document.querySelectorAll('a[href$=".html"], a[href*=".html?"]');
        for (var i = 0; i < links.length; i++) {
            var a = links[i];
            var href = a.getAttribute('href');
            if (!href || /^(https?:)?\/\//i.test(href)) continue;

            var hashAt = href.indexOf('#');
            var hash = hashAt >= 0 ? href.slice(hashAt) : '';
            var path = hashAt >= 0 ? href.slice(0, hashAt) : href;
            var qAt = path.indexOf('?');
            var base = qAt >= 0 ? path.slice(0, qAt) : path;

            var keep = [];
            if (qAt >= 0) {
                var parts = path.slice(qAt + 1).split('&');
                for (var j = 0; j < parts.length; j++) {
                    // 濾掉舊的 lang，其餘原封不動——這樣重複套用（切換語言）才不會累積。
                    if (!parts[j] || parts[j] === 'lang' || parts[j].indexOf('lang=') === 0) continue;
                    keep.push(parts[j]);
                }
            }
            if (lang === 'en') keep.push('lang=en');

            a.setAttribute('href', base + (keep.length ? '?' + keep.join('&') : '') + hash);
        }
    }

    function apply(lang) {
        setHtmlLang(lang);
        applyAttributes(lang);
        decorateLinks(lang);
    }

    var current = resolveInitial();
    setHtmlLang(current);   // 同步執行：早於 body 渲染

    document.addEventListener('DOMContentLoaded', function () {
        applyAttributes(current);
        decorateLinks(current);

        var toggle = document.getElementById('docLangToggle');
        if (toggle) {
            toggle.addEventListener('click', function () {
                current = current === 'en' ? 'zh' : 'en';
                try { window.localStorage.setItem(KEY, current); } catch (e) { /* ignore */ }
                apply(current);
            });
        }
    });
})();
