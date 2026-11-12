/* 使用手冊全文搜尋
   ------------------------------------------------------------------
   索引是隨手冊一起送的靜態檔（search-index.<lang>.json，由
   DocSearchIndexBuilder 產生、DocSearchIndexFacts 守門）。沒有資料庫、
   沒有推論服務、不需要登入——手冊本身就是靜態檔，搜尋不該比它更難用。

   三件事做錯都不會報錯：
   1. 索引與手冊不同步 → 搜不到新章節。守在 CI（逐位元比對），不在這裡。
   2. 搜錯語系 → 中英同頁並存，搜「Rate」會撈出中文塊。故索引分語系兩檔，
      且每次搜尋都重讀 <html data-doc-lang>，不快取語系。
   3. 結果連結掉了 ?lang= → 英文讀者點進去看到中文。doc-i18n.js 的
      decorateLinks 只處理 DOM 裡既有的連結，動態產生的結果連結它看不到，
      所以這裡自己補。

   CSP：script-src 'self'（無 unsafe-inline），故本檔為外部檔；
   UI 一律以 DOM API 建立，樣式走 doc-search.css 的 class，不寫 style 屬性。 */
(function () {
    'use strict';

    /* 手冊根的 URL。各頁以不同深度載入本檔（doc-search.js / ../doc-search.js），
       用 currentScript 的 src 反推，比讓每頁自己宣告根路徑可靠——
       後者只要有一頁寫錯，那一頁的搜尋結果全部 404，而其他頁完全正常。 */
    var ROOT = (function () {
        var s = document.currentScript;
        if (!s || !s.src) return '';
        return s.src.replace(/\/doc-search\.js(\?.*)?$/, '');
    })();

    var INDEX_FORMAT = 1;      /* 認得的索引格式版號，與 DocSearchIndexBuilder.FormatVersion 對齊 */
    var MAX_RESULTS = 20;
    var SNIPPET_BEFORE = 30;
    var SNIPPET_AFTER = 90;
    var QUERY_PARAM = 'dsq';   /* 跳轉後供落地高亮用 */

    var TEXT = {
        placeholder: { zh: '搜尋手冊…', en: 'Search the manual...' },
        empty: { zh: '沒有符合的內容', en: 'No matching content' },
        loading: { zh: '載入索引中…', en: 'Loading index...' },
        failed: { zh: '索引載入失敗', en: 'Could not load the index' },
        hint: { zh: '鍵入以搜尋，Esc 關閉', en: 'Type to search, Esc to close' }
    };

    var indexCache = {};       /* lang -> {chunks, loading, failed} */
    var input, panel, box;
    var results = [];
    var activeIndex = -1;

    function currentLang() {
        return document.documentElement.getAttribute('data-doc-lang') === 'en' ? 'en' : 'zh';
    }

    function t(key) { return TEXT[key][currentLang()]; }

    /* ── 索引載入 ───────────────────────────────────────────────── */

    function loadIndex(lang) {
        if (indexCache[lang]) return indexCache[lang].promise;

        var entry = { chunks: null, failed: false };
        entry.promise = fetch(ROOT + '/search-index.' + lang + '.json', { credentials: 'same-origin' })
            .then(function (resp) {
                if (!resp.ok) throw new Error('HTTP ' + resp.status);
                return resp.json();
            })
            .then(function (data) {
                /* 手冊是靜態檔：使用者的瀏覽器可能拿著快取住的舊 JS 配新索引。
                   欄位含義變了還硬讀，結果是搜尋悄悄回傳垃圾，不如明說載入失敗。 */
                if (!data || data.V !== INDEX_FORMAT) throw new Error('index format ' + (data && data.V));

                entry.chunks = data.C.map(function (c) {
                    return { u: c.U, a: c.A, h: c.H, text: c.T, lowerText: c.T.toLowerCase(), lowerHead: c.H.toLowerCase() };
                });
                return entry.chunks;
            })
            .catch(function (err) {
                entry.failed = true;
                if (window.console) window.console.warn('手冊搜尋索引載入失敗：', err);
                return null;
            });

        indexCache[lang] = entry;
        return entry.promise;
    }

    /* ── 比對與排序 ─────────────────────────────────────────────── */

    /* 中文不切詞：CJK 沒有詞邊界，整串 indexOf 比任何切詞都準。
       英文以空白切成多個詞，全部命中才算（AND）——這是使用者對搜尋框的預設期待。 */
    function termsOf(query) {
        var q = query.trim().toLowerCase();
        if (!q) return [];
        return q.indexOf(' ') < 0 ? [q] : q.split(/\s+/).filter(function (x) { return x.length > 0; });
    }

    function search(chunks, query) {
        var terms = termsOf(query);
        if (terms.length === 0) return [];

        var whole = query.trim().toLowerCase();
        var hits = [];

        for (var i = 0; i < chunks.length; i++) {
            var c = chunks[i];
            var score = 0;
            var firstAt = -1;
            var matchedAll = true;

            for (var j = 0; j < terms.length; j++) {
                var inHead = c.lowerHead.indexOf(terms[j]);
                var inText = c.lowerText.indexOf(terms[j]);

                if (inHead < 0 && inText < 0) { matchedAll = false; break; }

                /* 標題命中權重高於內文：專有名詞多半就是章節名，
                   而使用者搜一個畫面名時要的是那個畫面的章節，不是別處順帶提到它的段落。 */
                if (inHead >= 0) score += 3;
                if (inText >= 0) {
                    score += 1;
                    if (firstAt < 0 || inText < firstAt) firstAt = inText;
                }
            }

            if (!matchedAll) continue;

            if (terms.length > 1 && c.lowerText.indexOf(whole) >= 0) score += 2;
            if (c.a === '') score += 0.5;   /* 頁首塊通常是該頁總覽，同分時略優先 */

            hits.push({ chunk: c, score: score, at: firstAt < 0 ? 0 : firstAt, term: terms[0] });
        }

        hits.sort(function (a, b) { return b.score - a.score || a.chunk.h.localeCompare(b.chunk.h); });
        return hits.slice(0, MAX_RESULTS);
    }

    /* ── 呈現 ───────────────────────────────────────────────────── */

    function snippetOf(hit) {
        var text = hit.chunk.text;
        var start = Math.max(0, hit.at - SNIPPET_BEFORE);

        /* 起點別切在詞中間。手冊內文含路徑與檔名（「路徑：/mobile/reports.html」），
           固定往前退 N 字會切出「…orts.html」這種看起來像亂碼的開頭。
           往後找最近的分隔符即可——中文本來就處處可切，這條只對西文字串有實際作用，
           且最多前進到命中位置，不會把命中詞自己切掉。 */
        if (start > 0) {
            var limit = Math.min(start + 14, hit.at);
            for (var i = start; i < limit; i++) {
                if (/[\s|、，。：；]/.test(text.charAt(i))) { start = i + 1; break; }
            }
        }

        var end = Math.min(text.length, hit.at + SNIPPET_AFTER);
        var body = text.slice(start, end).replace(/\n+/g, ' ');
        return (start > 0 ? '…' : '') + body + (end < text.length ? '…' : '');
    }

    /* 高亮用 DOM 節點組出來，不拼 HTML 字串——手冊內文含 < > & 與各種符號，
       拼字串就得自己做跳脫，漏一處就是一個注入點。 */
    function appendHighlighted(target, text, terms) {
        var lower = text.toLowerCase();
        var cursor = 0;

        while (cursor < text.length) {
            var bestAt = -1;
            var bestLen = 0;

            for (var i = 0; i < terms.length; i++) {
                var at = lower.indexOf(terms[i], cursor);
                if (at >= 0 && (bestAt < 0 || at < bestAt)) { bestAt = at; bestLen = terms[i].length; }
            }

            if (bestAt < 0) { target.appendChild(document.createTextNode(text.slice(cursor))); return; }

            if (bestAt > cursor) target.appendChild(document.createTextNode(text.slice(cursor, bestAt)));

            var mark = document.createElement('mark');
            mark.className = 'doc-search-hit';
            mark.textContent = text.slice(bestAt, bestAt + bestLen);
            target.appendChild(mark);

            cursor = bestAt + bestLen;
        }
    }

    function hrefOf(chunk, query) {
        var url = ROOT + '/' + chunk.u;
        var params = [];
        if (currentLang() === 'en') params.push('lang=en');
        params.push(QUERY_PARAM + '=' + encodeURIComponent(query));
        return url + '?' + params.join('&') + (chunk.a ? '#' + chunk.a : '');
    }

    function renderMessage(message) {
        panel.textContent = '';
        var div = document.createElement('div');
        div.className = 'doc-search-message';
        div.textContent = message;
        panel.appendChild(div);
        openPanel();
    }

    function render(query) {
        var terms = termsOf(query);
        panel.textContent = '';
        activeIndex = -1;

        if (results.length === 0) { renderMessage(t('empty')); return; }

        var list = document.createElement('ul');
        list.className = 'doc-search-list';

        results.forEach(function (hit, i) {
            var li = document.createElement('li');
            li.className = 'doc-search-item';

            var a = document.createElement('a');
            a.className = 'doc-search-link';
            a.setAttribute('href', hrefOf(hit.chunk, query));
            a.setAttribute('data-doc-search-index', String(i));

            var head = document.createElement('span');
            head.className = 'doc-search-heading';
            appendHighlighted(head, hit.chunk.h, terms);

            var snip = document.createElement('span');
            snip.className = 'doc-search-snippet';
            appendHighlighted(snip, snippetOf(hit), terms);

            a.appendChild(head);
            a.appendChild(snip);
            li.appendChild(a);
            list.appendChild(li);
        });

        panel.appendChild(list);
        openPanel();
    }

    function openPanel() { box.classList.add('doc-search-open'); }

    function closePanel() {
        box.classList.remove('doc-search-open');
        activeIndex = -1;
    }

    function setActive(next) {
        var links = panel.querySelectorAll('.doc-search-link');
        if (links.length === 0) return;

        if (activeIndex >= 0 && links[activeIndex]) links[activeIndex].classList.remove('doc-search-active');

        activeIndex = (next + links.length) % links.length;
        links[activeIndex].classList.add('doc-search-active');
        links[activeIndex].scrollIntoView({ block: 'nearest' });
    }

    /* ── 查詢流程 ───────────────────────────────────────────────── */

    function runSearch() {
        var query = input.value;
        if (query.trim().length === 0) { closePanel(); return; }

        var lang = currentLang();
        var cached = indexCache[lang];

        if (cached && cached.chunks) {
            results = search(cached.chunks, query);
            render(query);
            return;
        }

        if (!cached || !cached.failed) renderMessage(t('loading'));

        loadIndex(lang).then(function (chunks) {
            if (input.value !== query) return;          /* 使用者已經改了字，這次結果作廢 */
            if (!chunks) { renderMessage(t('failed')); return; }
            results = search(chunks, query);
            render(query);
        });
    }

    /* ── 落地高亮（從搜尋結果點進來的那一頁） ───────────────────── */

    function highlightLanding() {
        var query;
        try {
            query = new URLSearchParams(window.location.search).get(QUERY_PARAM);
        } catch (e) { return; }

        if (!query) return;

        var terms = termsOf(query);
        if (terms.length === 0) return;

        var anchor = window.location.hash ? window.location.hash.slice(1) : '';
        var heading = anchor ? document.getElementById(anchor) : null;
        var main = document.querySelector('main.content');
        if (!main) return;

        /* 範圍限定在該段落（h2 到下一個 h2 之間）。整頁高亮的話，
           使用者要找的詞會淹沒在幾十個同樣被標起來的字裡。 */
        var nodes = [];

        /* 標題本身要納入：使用者常常搜的就是章節名（「欄位說明」「操作步驟」），
           而那個詞往往只出現在 h2 裡、內文一次都沒有——只掃內文的話，
           跳過去之後一個字也不會被標起來，看起來像高亮壞了。 */
        if (heading) nodes.push(heading);

        var node = heading ? heading.nextElementSibling : main.firstElementChild;
        while (node) {
            if (node.tagName === 'H2') break;
            nodes.push(node);
            node = node.nextElementSibling;
        }

        var highlighted = 0;
        nodes.forEach(function (el) {
            var walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, null);
            var texts = [];
            while (walker.nextNode()) texts.push(walker.currentNode);

            texts.forEach(function (textNode) {
                var text = textNode.nodeValue;
                var lower = text.toLowerCase();
                var found = terms.some(function (term) { return lower.indexOf(term) >= 0; });
                if (!found) return;

                var frag = document.createDocumentFragment();
                appendHighlighted(frag, text, terms);
                textNode.parentNode.replaceChild(frag, textNode);
                highlighted++;
            });
        });

        var target = heading || main.querySelector('.doc-search-hit');
        if (!target) return;

        /* 捲兩次，第二次在 load 之後。
           手冊每頁都有 loading="lazy" 的畫面截圖，DOMContentLoaded 當下它們還沒有高度；
           這時捲過去，等圖片載入把版面撐開，目標就被推到畫面外——實測需要捲 962px 的頁面
           只捲到 343px，而使用者看到的是「跳了，但跳錯地方」。

           ⚠️ 「使用者有沒有自己捲過」不能用捲動位置差來判斷：瀏覽器在我們之後還會
           自己做一次 fragment 跳轉（URL 帶著 #anchor），那次位移看起來與使用者捲動
           一模一樣，於是補捲永遠被跳過——這個判斷錯法實測就是讓修正完全沒生效的原因。
           要問的是「使用者有沒有操作」，所以聽的是輸入事件，不是位置。 */
        var userMoved = false;
        ['wheel', 'touchstart', 'keydown', 'pointerdown'].forEach(function (ev) {
            window.addEventListener(ev, function () { userMoved = true; }, { passive: true, once: true });
        });

        target.scrollIntoView({ block: 'start' });

        /* 只在 load 補捲一次還不夠：loading="lazy" 的截圖**不算在 load 裡**，
           要等捲到附近才開始載，所以補捲之後它們才撐開版面，目標又被推下去
           （2026-11-09 nightly 與本機 5 次中 1 次停在 579px／視窗 720）。
           改成落地後幾秒內，內容高度一變就重新對齊；使用者一操作就停手。 */
        var SETTLE_MS = 4000;
        var settleUntil = Date.now() + SETTLE_MS;
        function realign() {
            if (!userMoved && Date.now() < settleUntil) target.scrollIntoView({ block: 'start' });
        }
        if (window.ResizeObserver) {
            var ro = new ResizeObserver(realign);
            ro.observe(main);
            setTimeout(function () { ro.disconnect(); }, SETTLE_MS);
        }
        if (document.readyState === 'complete') realign();
        else window.addEventListener('load', realign);
    }

    /* ── UI 組建 ────────────────────────────────────────────────── */

    function build() {
        var navbar = document.querySelector('nav.navbar');
        if (!navbar) return;

        var buttons = navbar.querySelector('.d-flex');
        if (!buttons) return;

        box = document.createElement('div');
        box.className = 'doc-search';

        input = document.createElement('input');
        input.className = 'form-control form-control-sm doc-search-input';
        input.setAttribute('type', 'search');
        input.setAttribute('autocomplete', 'off');
        input.setAttribute('aria-label', t('placeholder'));
        input.setAttribute('placeholder', t('placeholder'));

        panel = document.createElement('div');
        panel.className = 'doc-search-panel';

        box.appendChild(input);
        box.appendChild(panel);
        buttons.insertBefore(box, buttons.firstChild);

        input.addEventListener('input', runSearch);
        input.addEventListener('focus', function () {
            loadIndex(currentLang());               /* 先暖機，使用者打完第一個字時索引通常已就緒 */
            if (input.value.trim()) openPanel();
        });

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { closePanel(); input.blur(); return; }
            if (e.key === 'ArrowDown') { e.preventDefault(); setActive(activeIndex + 1); return; }
            if (e.key === 'ArrowUp') { e.preventDefault(); setActive(activeIndex - 1); return; }
            if (e.key === 'Enter') {
                var links = panel.querySelectorAll('.doc-search-link');
                var target = activeIndex >= 0 ? links[activeIndex] : links[0];
                if (target) { e.preventDefault(); window.location.href = target.getAttribute('href'); }
            }
        });

        document.addEventListener('click', function (e) {
            if (!box.contains(e.target)) closePanel();
        });

        /* 「/」聚焦搜尋框，是搜尋框的通用慣例；在輸入中的欄位裡不搶鍵。 */
        document.addEventListener('keydown', function (e) {
            if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
            var tag = e.target && e.target.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || (e.target && e.target.isContentEditable)) return;
            e.preventDefault();
            input.focus();
        });

        /* 語系切換沒有對外事件可掛，而 docLangToggle 的 click 監聽順序不由我們決定；
           直接盯 data-doc-lang 的變化，不必假設是誰改的、何時改的。 */
        new MutationObserver(function () {
            input.setAttribute('placeholder', t('placeholder'));
            input.setAttribute('aria-label', t('placeholder'));
            if (input.value.trim()) runSearch(); else closePanel();
        }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-doc-lang'] });
    }

    document.addEventListener('DOMContentLoaded', function () {
        build();
        highlightLanding();
    });
})();
