/* 教學播放器
   ------------------------------------------------------------------
   把手冊既有的內容在執行期組成一堂課，**不產生第三份會漂移的副本**：
     課名／站別序列 ← 模組目錄頁的 .flow-row（id 由 add_flow_ids.py 產生）
     每一站的步驟   ← 該畫面手冊頁的 .step-row（依文件順序全收）
     截圖           ← 該手冊頁的 img.screen-shot（中英各一套）

   四件必須照著做、否則會靜默壞掉的事：

   1. **DOMParser 解出來的文件沒有 base URL。** 相對路徑（img 的 src、站別的 href）
      會對著「播放器這一頁」解析，變成 /docs/user/tutor/img/xxx.png 而 404。
      一律用 new URL(raw, 該頁的絕對 URL) 自己算。**不要注入 <base>**——那會一併
      改掉本頁其餘連結的解析基準。

   2. **CSP 是 style-src 'self' 且完全沒有 nonce。** 內聯 <style> 與 style="…" 都會被擋，
      而被擋的樣子是「版面沒套用」不是報錯。動態尺寸只能用 CSSOM 屬性指派
      （el.style.left = …），那條路不受 style-src 管。

   3. **取單一語系的字面不能只抓第一個 [lang=zh]。** 手冊一句話常被 <b>／<a> 切成好幾段
      zh／en 交錯的 span，只取第一段會唸半句。照 doc-i18n.css 的語意走訪：
      跳過另一語系，其餘（含不在任何 lang span 內的裸文字）全留。

   4. **顯示**用不著自己挑語言：把手冊原本的 zh/en span 對整組搬過來，
      顯示與否由 doc-i18n.css 決定。只有**語音**需要純文字，才用第 3 點的走訪。
      但 doc-i18n.js 的 img 換手是在 DOMContentLoaded 跑的，之後才注入的 img 不會被它處理，
      所以截圖的 src 由本檔自己依語言設定，並在語言變動時重設。
*/
(function () {
    'use strict';

    var HOTSPOTS_URL = 'hotspots.json';

    var state = {
        course: null,       // { id, labelEl, alt, stops: [...] }
        entries: [],        // 播放清單，一格一步
        index: 0,
        speak: false,
        hotspots: null,     // { "pur/goods-receipt.html": { zh: { "3": [x,y,w,h] }, en: {…} } }
        voiceBroken: false,
        auto: false,        // 連續播放
        timer: null,
        utter: null,        // 目前這一格的朗讀（連續播放以「唸完」為節拍）
        gen: 0              // 排程世代，見 schedule()
    };

    var el = {};            // 建好的 DOM 節點

    // ── 語言 ──────────────────────────────────────────────────────────

    function lang() {
        return document.documentElement.getAttribute('data-doc-lang') === 'en' ? 'en' : 'zh';
    }

    /** 依 doc-i18n 的顯示語意取純文字（語音用）。見檔頭第 3 點。 */
    function textOf(node) {
        if (!node) return '';
        var want = lang(), out = [];
        (function walk(n) {
            for (var c = n.firstChild; c; c = c.nextSibling) {
                if (c.nodeType === 3) { out.push(c.nodeValue); continue; }
                if (c.nodeType !== 1) continue;
                var l = c.getAttribute('lang');
                if (l && l !== want) continue;
                walk(c);
            }
        })(node);
        return out.join('').replace(/\s+/g, ' ').trim();
    }

    /** 站內連結沿用目前語言（doc-i18n.js 只處理載入當下就存在的連結）。 */
    function withLang(href) {
        if (lang() !== 'en' || /^(https?:)?\/\//i.test(href)) return href;
        return href.indexOf('?') >= 0 ? href + '&lang=en' : href + '?lang=en';
    }

    // ── 抓頁面 ────────────────────────────────────────────────────────

    var docCache = {};

    function fetchDoc(url) {
        var key = url.href.split('#')[0];
        if (docCache[key]) return docCache[key];
        docCache[key] = fetch(key)
            .then(function (r) {
                if (!r.ok) throw new Error(key + ' → HTTP ' + r.status);
                return r.text();
            })
            .then(function (html) {
                return { doc: new DOMParser().parseFromString(html, 'text/html'), url: new URL(key) };
            });
        return docCache[key];
    }

    // ── 解析流程圖 ────────────────────────────────────────────────────

    /**
     * 逐一走 .flow-steps 的**直接子元素**，而不是 querySelectorAll('.flow-node')。
     * 後者會把 .flow-plus（擇一）與 .flow-cond（分支條件）整個吃掉，於是
     * 「進貨單｜委外收貨｜完工入庫 → 上架入庫」會被讀成三站依序進行——
     * 教出一條系統裡不存在的流程，而且沒有任何訊號。
     */
    function parseStops(row, pageUrl) {
        var steps = row.querySelector('.flow-steps');
        var stops = [], pendingCond = null, altWithPrev = false;

        for (var c = steps.firstElementChild; c; c = c.nextElementSibling) {
            var cls = c.getAttribute('class') || '';
            if (cls.indexOf('flow-plus') >= 0) { altWithPrev = true; continue; }
            if (cls.indexOf('flow-cond') >= 0) { pendingCond = c; continue; }
            if (cls.indexOf('flow-node') < 0) continue;   // flow-arrow 等

            var href = c.getAttribute('href');
            stops.push({
                kind: cls.indexOf('evt') >= 0 ? 'evt' : (cls.indexOf('ext') >= 0 ? 'ext' : 'node'),
                nameEl: c.querySelector('.flow-name'),
                modEl: c.querySelector('.flow-mod'),
                url: href ? new URL(href, pageUrl) : null,
                cond: pendingCond,
                altWithPrev: altWithPrev,
                steps: [],
                images: []
            });
            pendingCond = null;
            altWithPrev = false;
        }
        return stops;
    }

    // ── 解析單一手冊頁的步驟與截圖 ────────────────────────────────────

    /**
     * 依文件順序收集**全部** .step-row（不是只收「作業流程」那一段）。
     * 實測步驟分布：作業流程 37%、查詢 27%、操作步驟 18%，其餘 18% 散在 40 多種標題底下，
     * 照標題挑會靜默漏掉六成。段落標籤取最近的前一個 h2/h3。
     *
     * 步驟↔截圖的對應：手冊的慣例是**截圖跟在它所說明的那一步後面**，所以預設規則是
     * 「同一段落內往後找最近的截圖；找不到就往前找；再找不到用該頁第一張」。
     * 規則本質上是推斷，會有猜不準的個案（例如「儲存草稿」那一步的正確截圖是側欄，
     * 但它後面接的是下一段的截圖），因此 .step-row 可加 data-shot="img/xxx.png" 明確指定，
     * 有指定就以指定為準。
     */
    function parsePage(pageDoc, pageUrl) {
        var main = pageDoc.querySelector('main.content');
        if (!main) return { steps: [], images: [] };

        var items = [], section = null;
        var nodes = main.querySelectorAll('h2, h3, .step-row, img.screen-shot');

        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i], tag = n.tagName;
            if (tag === 'H2' || tag === 'H3') { section = n; items.push({ t: 'head', el: n }); continue; }
            if (tag === 'IMG') { items.push({ t: 'img', el: n }); continue; }
            items.push({ t: 'step', el: n, section: section });
        }

        var firstImg = null;
        for (var a = 0; a < items.length; a++) if (items[a].t === 'img') { firstImg = items[a].el; break; }

        var steps = [];
        for (var k = 0; k < items.length; k++) {
            if (items[k].t !== 'step') continue;

            var img = null;
            var override = items[k].el.getAttribute('data-shot');
            if (override) {
                // 指到頁面上**既有的那個 img 元素**，而不是自己造一個假的：
                // data-src-en（英文截圖）與 alt 都掛在元素上，繞過它等於英文版永遠退回中文截圖。
                img = main.querySelector('img.screen-shot[src="' + override + '"]');
            }
            if (override && !img) {
                // 守門測試會擋掉指到不存在檔案的 data-shot；真的漏網就退回推斷，不要整格沒圖。
                console.warn('data-shot 指到頁面上沒有的截圖：' + override + '（' + pageUrl.href + '）');
            }
            if (!img) {
                for (var f = k + 1; f < items.length && items[f].t !== 'head'; f++) {
                    if (items[f].t === 'img') { img = items[f].el; break; }
                }
                if (!img) for (var b = k - 1; b >= 0; b--) {
                    if (items[b].t === 'img') { img = items[b].el; break; }
                }
                if (!img) img = firstImg;
            }

            steps.push({
                bodyEl: items[k].el.querySelector('div') || items[k].el,
                sectionEl: items[k].section,
                shot: shotOf(img, pageUrl),
                indexInPage: steps.length
            });
        }

        return { steps: steps, firstShot: shotOf(firstImg, pageUrl) };
    }

    /** 把 img 的兩種語系來源都換算成絕對 URL（見檔頭第 1 點）。 */
    function shotOf(img, pageUrl) {
        if (!img) return null;
        var zh = img.getAttribute('data-src-zh') || img.getAttribute('src');
        var en = img.getAttribute('data-src-en') || zh;
        if (!zh) return null;
        return { zh: new URL(zh, pageUrl).href, en: new URL(en, pageUrl).href };
    }

    // ── 組課程 ────────────────────────────────────────────────────────

    function loadCourse(flowId) {
        var mod = flowId.replace(/^flow-/, '').split('-')[0];
        var indexHref = mod === 'main' ? '../index.html' : '../' + mod + '/index.html';
        var indexUrl = new URL(indexHref, location.href);

        return fetchDoc(indexUrl).then(function (src) {
            var row = src.doc.getElementById(flowId);
            if (!row) throw new Error('在 ' + indexHref + ' 找不到課程 ' + flowId);

            var course = {
                id: flowId,
                labelEl: row.querySelector('.flow-row-label'),
                alt: (row.getAttribute('class') || '').indexOf('alt') >= 0,
                stops: parseStops(row, src.url)
            };

            return Promise.all(course.stops.map(function (s) {
                if (!s.url) return null;
                return fetchDoc(s.url).then(function (p) {
                    var parsed = parsePage(p.doc, p.url);
                    s.steps = parsed.steps;
                    s.firstShot = parsed.firstShot;
                }).catch(function (e) {
                    s.error = e.message;
                });
            })).then(function () { return course; });
        });
    }

    /** 把課程攤平成一格一步的播放清單。沒有畫面的外部事件占一格。 */
    function buildEntries(course) {
        var entries = [];
        course.stops.forEach(function (stop, si) {
            if (stop.kind === 'evt' || !stop.url) {
                entries.push({ stop: stop, stopIndex: si, event: true });
                return;
            }
            if (!stop.steps.length) {
                entries.push({ stop: stop, stopIndex: si, empty: true });
                return;
            }
            stop.steps.forEach(function (st, i) {
                entries.push({ stop: stop, stopIndex: si, step: st, stepIndex: i });
            });
        });
        return entries;
    }

    // ── 熱點 ──────────────────────────────────────────────────────────

    function loadHotspots() {
        return fetch(HOTSPOTS_URL)
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });   // 還沒產出座標是正常狀態，不是錯誤
    }

    /**
     * 熱點以「**這張截圖** × **步驟文字裡點名的控制項**」為鍵，不是以步驟序號為鍵。
     *
     * <p>用序號的話，產生座標的工具就得跟播放器一樣會解析步驟、還要跟著推斷步驟配哪張截圖——
     * 同一套規則寫兩遍，而且手冊插入一步就全體位移。改用控制項名稱後，
     * 產生器只要「開這個畫面、找這個選擇器、記下框」，完全不必懂步驟。</p>
     *
     * <p>中英各是獨立的鍵（截圖路徑不同、控制項名稱也不同），所以不需要語系分支。</p>
     */
    function hotspotFor(entry) {
        if (!state.hotspots || !entry.step) return null;

        var shot = entry.step.shot || entry.stop.firstShot;
        if (!shot) return null;
        var boxes = state.hotspots[shotKey(lang() === 'en' ? shot.en : shot.zh)];
        if (!boxes) return null;

        // 手冊的慣例：UI 上的控制項名稱中文用「」、英文用 ""。第一個查得到的優先。
        var text = textOf(entry.step.bodyEl);
        var names = lang() === 'en' ? text.match(/"([^"]+)"/g) : text.match(/「([^」]+)」/g);
        if (!names) return null;

        for (var i = 0; i < names.length; i++) {
            var n = names[i].slice(1, -1);
            if (boxes[n]) return boxes[n];
        }
        return null;
    }

    /** hotspots.json 的鍵：相對 docs/user 的截圖路徑，例如 pur/img/en/purchase-request.png */
    function shotKey(href) {
        var m = String(href).match(/\/docs\/user\/(.+)$/);
        return m ? m[1] : href;
    }

    // ── 畫面 ──────────────────────────────────────────────────────────

    function build(root) {
        root.textContent = '';

        el.track = div('tutor-track');
        el.stage = div('tutor-stage');
        el.img = document.createElement('img');
        el.img.alt = '';
        el.hotspot = div('tutor-hotspot');
        el.hotspot.hidden = true;
        el.stage.appendChild(el.img);
        el.stage.appendChild(el.hotspot);

        el.event = div('tutor-eventcard');
        el.event.hidden = true;

        el.stepline = div('tutor-stepline');

        el.prev = button('btn btn-outline-secondary btn-sm', '上一步', 'Back');
        el.next = button('btn btn-primary btn-sm', '下一步', 'Next');
        el.auto = button('btn btn-outline-secondary btn-sm', '連續播放', 'Auto-play');
        el.voice = button('btn btn-outline-secondary btn-sm', '語音朗讀', 'Narration');
        el.progress = document.createElement('span');
        el.progress.className = 'text-muted small';
        el.open = document.createElement('a');
        el.open.className = 'btn btn-outline-secondary btn-sm ms-auto';
        el.open.appendChild(bi('開啟完整說明', 'Open the full page'));

        el.controls = div('tutor-controls');
        [el.prev, el.next, el.auto, el.voice, el.progress, el.open]
            .forEach(function (c) { el.controls.appendChild(c); });

        el.title = document.createElement('div');
        el.title.className = 'fw-bold';

        root.appendChild(el.title);
        root.appendChild(el.track);
        root.appendChild(el.stage);
        root.appendChild(el.event);
        root.appendChild(el.stepline);
        root.appendChild(el.controls);

        // 連續播放中的任何**手動**移動都會停下來。規則單純才好預期：
        // 若讓「下一步」跳格而不停、「上一步」卻停，使用者無法從行為推得規則。
        el.prev.addEventListener('click', function () { manual(state.index - 1); });
        el.next.addEventListener('click', function () { manual(state.index + 1); });
        el.auto.addEventListener('click', toggleAuto);
        el.voice.addEventListener('click', toggleVoice);

        document.addEventListener('keydown', function (e) {
            if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
            if (e.key === 'ArrowRight') { manual(state.index + 1); }
            else if (e.key === 'ArrowLeft') { manual(state.index - 1); }
        });
    }

    function div(cls) { var d = document.createElement('div'); d.className = cls; return d; }

    /**
     * 只搬**內容**、不搬容器。整個元素 clone 過來會把它在原頁面的樣式一起帶進來——
     * .flow-row-label 在流程圖裡是右對齊的固定寬欄位，直接複製會讓課名飄到播放器右上角；
     * h2/h3 會用手冊的標題字級蓋掉這裡的段落標籤樣式。
     */
    function cloneInto(target, source) {
        if (!source) return;
        for (var c = source.firstChild; c; c = c.nextSibling) target.appendChild(c.cloneNode(true));
    }

    function bi(zh, en) {
        var f = document.createDocumentFragment();
        f.appendChild(span('zh', zh));
        f.appendChild(span('en', en));
        return f;
    }

    function span(l, text) {
        var s = document.createElement('span');
        s.setAttribute('lang', l);
        s.textContent = text;
        return s;
    }

    function button(cls, zh, en) {
        var b = document.createElement('button');
        b.type = 'button';
        b.className = cls;
        b.appendChild(bi(zh, en));
        return b;
    }

    // ── 繪製 ──────────────────────────────────────────────────────────

    function renderTrack() {
        el.track.textContent = '';
        state.course.stops.forEach(function (stop, i) {
            if (i > 0) {
                var sep = document.createElement('span');
                sep.className = 'tutor-track-sep';
                sep.textContent = stop.altWithPrev ? '／' : '→';   // ／ 或 →
                el.track.appendChild(sep);
            }
            var pill = document.createElement('button');
            pill.type = 'button';
            pill.className = 'tutor-track-stop'
                + (i === state.entries[state.index].stopIndex ? ' is-current' : '')
                + (stop.kind === 'evt' ? ' is-event' : '');
            cloneInto(pill, stop.nameEl);
            pill.addEventListener('click', function () { stopAuto(); goStop(i); });
            el.track.appendChild(pill);
        });
    }

    function render() {
        invalidate();       // 必須早於 narrate()：cancel() 會觸發舊 utterance 的 end
        var entry = state.entries[state.index];
        var stop = entry.stop;

        el.title.textContent = '';
        cloneInto(el.title, state.course.labelEl);
        if (state.course.alt) {
            var badge = document.createElement('span');
            badge.className = 'badge bg-warning text-dark ms-2';
            badge.appendChild(bi('替代路徑', 'Alternative'));
            el.title.appendChild(badge);
        }

        renderTrack();

        el.stepline.textContent = '';
        var sec = document.createElement('span');
        sec.className = 'tutor-stepline-section';

        if (entry.event) {
            el.stage.hidden = true;
            el.event.hidden = false;
            el.event.textContent = '';
            var t = div('tutor-eventcard-title');
            cloneInto(t, stop.nameEl);
            el.event.appendChild(t);
            el.event.appendChild(bi(
                '這一步發生在系統之外，沒有對應的畫面。',
                'This happens outside the system, so there is no screen for it.'));
        } else if (entry.empty || stop.error) {
            el.stage.hidden = true;
            el.event.hidden = false;
            el.event.textContent = '';
            el.event.appendChild(bi(
                stop.error ? '載入失敗：' + stop.error : '這一站的手冊頁目前沒有逐步驟說明。',
                stop.error ? 'Could not load: ' + stop.error : 'The manual page for this stop does not have step-by-step instructions yet.'));
        } else {
            el.event.hidden = true;
            el.stage.hidden = false;

            var shot = (entry.step && entry.step.shot) || stop.firstShot;
            setShot(shot);

            if (entry.step.sectionEl) {
                cloneInto(sec, entry.step.sectionEl);
                el.stepline.appendChild(sec);
            }
            var body = document.createElement('div');
            cloneInto(body, entry.step.bodyEl);
            el.stepline.appendChild(body);
        }

        el.open.hidden = !stop.url;
        if (stop.url) el.open.href = withLang(stop.url.href);

        el.prev.disabled = state.index === 0;
        el.next.disabled = state.index === state.entries.length - 1;
        el.progress.textContent = (state.index + 1) + ' / ' + state.entries.length;

        narrate(entry);
        schedule();
    }

    function setShot(shot) {
        if (!shot) { el.img.removeAttribute('src'); el.hotspot.hidden = true; return; }
        var want = lang() === 'en' ? shot.en : shot.zh;
        if (el.img.getAttribute('src') !== want) {
            // 英文截圖尚未產出時退回中文，而不是留一張破圖
            el.img.onerror = function () {
                if (el.img.getAttribute('src') !== shot.zh) el.img.setAttribute('src', shot.zh);
            };
            el.img.setAttribute('src', want);
        }
        fitStage();
        placeHotspot();
    }

    /**
     * 直式（手機）截圖要壓窄，否則一格就高過一個螢幕。
     *
     * 依**實際長寬比**判斷而不是看路徑裡有沒有 mobile：路徑規則會在有人把手機截圖
     * 放到別的模組時靜默失效，而長寬比就是「這張圖是不是手機畫面」本身。
     * 圖還沒載完時量不到尺寸，所以 load 事件也要再叫一次（見啟動段）。
     */
    function fitStage() {
        var w = el.img.naturalWidth, h = el.img.naturalHeight;
        if (!w || !h) return;                 // 還沒載完：維持現狀，load 時再判斷
        el.stage.classList.toggle('is-portrait', h > w);
    }

    /**
     * 熱點座標存的是 0–1 正規化值，乘上**目前 render 尺寸**。
     * 一律用 CSSOM 屬性指派：CSP style-src 沒有 nonce，寫 style 屬性字串會被擋掉（見檔頭第 2 點）。
     */
    function placeHotspot() {
        var box = hotspotFor(state.entries[state.index]);
        if (!box || !el.img.clientWidth) { el.hotspot.hidden = true; return; }

        var w = el.img.clientWidth, h = el.img.clientHeight;
        el.hotspot.style.left = (box[0] * w) + 'px';
        el.hotspot.style.top = (box[1] * h) + 'px';
        el.hotspot.style.width = (box[2] * w) + 'px';
        el.hotspot.style.height = (box[3] * h) + 'px';
        el.hotspot.hidden = false;
    }

    // ── 移動 ──────────────────────────────────────────────────────────

    function go(i) {
        if (i < 0 || i >= state.entries.length) return;
        state.index = i;
        render();
    }

    function goStop(si) {
        for (var i = 0; i < state.entries.length; i++) {
            if (state.entries[i].stopIndex === si) { go(i); return; }
        }
    }

    /** 使用者自己按的移動：先停掉連續播放，再走。 */
    function manual(i) {
        stopAuto();
        go(i);
    }

    // ── 連續播放 ──────────────────────────────────────────────────────
    //
    // 節拍有兩種來源，優先用語音：開了朗讀就等它唸完，因為固定秒數必然跟朗讀打架
    // ——短句乾等、長句被下一格切斷，兩種都比不會自動播放更糟。
    // 沒有語音時才用字數估閱讀時間。

    var GAP_MS = 900;          // 唸完到換下一格之間的停頓
    var SPEECH_MAX_MS = 40000; // 語音的保險上限，見 schedule()

    /** 沒有語音時，這一格該停多久。字數越多停越久，但兩端都夾住。 */
    function dwell(entry) {
        if (!entry.step) return 4000;                       // 外部事件卡：沒有步驟文字
        var n = textOf(entry.step.bodyEl).length;
        var perChar = lang() === 'en' ? 55 : 120;           // 英文以字母計，單位資訊量較低
        return Math.min(15000, Math.max(3000, 2200 + n * perChar));
    }

    /**
     * 排下一格的時間。
     *
     * **世代計數（state.gen）是必要的**：每次排程都可能留下兩個未來會觸發的東西
     * （utterance 的 end 事件、保險用的 setTimeout），而 speechSynthesis.cancel()
     * 在多數瀏覽器也會觸發 end。少了世代檢查，手動切格或關掉語音時舊的回呼會補一腳，
     * 畫面看起來就是「自己跳了兩格」——很難歸因，因為那一刻沒有任何錯誤。
     */
    /** 作廢所有已排定的回呼。render() 一開頭就呼叫，早於任何 speechSynthesis.cancel()。 */
    function invalidate() {
        if (state.timer) { clearTimeout(state.timer); state.timer = null; }
        state.gen++;
    }

    function schedule() {
        if (!state.auto) return;
        if (state.index >= state.entries.length - 1) { stopAuto(); return; }

        var my = state.gen;
        function fire() {
            if (state.auto && my === state.gen) { state.index++; render(); }
        }

        if (state.utter) {
            state.utter.addEventListener('end', function () {
                if (my !== state.gen) return;
                state.timer = setTimeout(fire, GAP_MS);
            });
            // end 不保證會來：分頁切到背景、文字過長時瀏覽器可能靜默中止朗讀。
            // 少了這道保險，連續播放會停在半路而且看不出原因。
            state.timer = setTimeout(fire, SPEECH_MAX_MS);
            return;
        }

        state.timer = setTimeout(fire, dwell(state.entries[state.index]));
    }

    function toggleAuto() {
        if (state.auto) { stopAuto(); return; }
        // 停在最後一格時按下去，從頭播比原地不動有用。
        if (state.index >= state.entries.length - 1) state.index = 0;
        state.auto = true;
        paintAuto();
        render();
    }

    function stopAuto() {
        invalidate();
        if (!state.auto) return;
        state.auto = false;
        paintAuto();
    }

    function paintAuto() {
        el.auto.classList.toggle('btn-primary', state.auto);
        el.auto.classList.toggle('btn-outline-secondary', !state.auto);
    }

    // ── 語音 ──────────────────────────────────────────────────────────

    function voicesFor(l) {
        if (!('speechSynthesis' in window)) return [];
        var pre = l === 'en' ? 'en' : 'zh';
        return speechSynthesis.getVoices().filter(function (v) {
            return v.lang && v.lang.toLowerCase().indexOf(pre) === 0;
        });
    }

    function toggleVoice() {
        // 開關語音會換掉連續播放的節拍來源（語音的 end ↔ 字數估時），所以兩邊都要重排。
        if (state.speak) {
            state.speak = false;
            invalidate();
            stopSpeaking();
            state.utter = null;
            paintVoice();
            schedule();
            return;
        }

        // getVoices() 首次常回空陣列（voiceschanged 才補上），所以不在啟動時就判定；
        // 真的按下去還是沒有可用語音，才停用按鈕並說明原因。
        if (!voicesFor(lang()).length) {
            state.voiceBroken = true;
            paintVoice();
            return;
        }
        state.speak = true;
        paintVoice();
        invalidate();
        narrate(state.entries[state.index]);
        schedule();
    }

    function paintVoice() {
        el.voice.classList.toggle('btn-primary', state.speak);
        el.voice.classList.toggle('btn-outline-secondary', !state.speak);
        el.voice.disabled = state.voiceBroken;
        el.voice.title = state.voiceBroken
            ? (lang() === 'en' ? 'No speech voice is installed on this device.' : '本裝置沒有可用的語音。')
            : '';
    }

    function stopSpeaking() {
        if ('speechSynthesis' in window) speechSynthesis.cancel();
    }

    function narrate(entry) {
        stopSpeaking();
        state.utter = null;                 // 沒有朗讀時，連續播放才會退回用字數估時間
        if (!state.speak || !entry.step) return;
        var text = textOf(entry.step.bodyEl);
        if (!text) return;
        var u = new SpeechSynthesisUtterance(text);
        u.lang = lang() === 'en' ? 'en-US' : 'zh-TW';
        state.utter = u;
        speechSynthesis.speak(u);
    }

    // ── 啟動 ──────────────────────────────────────────────────────────

    function fail(root, zh, en) {
        root.textContent = '';
        var box = div('note-box');
        box.appendChild(bi(zh, en));
        var a = document.createElement('a');
        a.href = withLang('index.html');
        a.className = 'ms-2';
        a.appendChild(bi('前往課程總覽', 'Go to all courses'));
        box.appendChild(a);
        root.appendChild(box);
    }

    document.addEventListener('DOMContentLoaded', function () {
        var root = document.getElementById('tutorApp');
        if (!root) return;

        var flowId;
        try { flowId = new URLSearchParams(location.search).get('flow'); } catch (e) { flowId = null; }

        if (!flowId) {
            fail(root,
                '網址沒有指定要播放的課程。',
                'The address does not say which course to play.');
            return;
        }

        Promise.all([loadCourse(flowId), loadHotspots()]).then(function (r) {
            state.course = r[0];
            state.hotspots = r[1];
            state.entries = buildEntries(state.course);

            if (!state.entries.length) {
                fail(root,
                    '這條課程沒有可播放的內容。',
                    'This course has nothing to play.');
                return;
            }

            build(root);
            paintVoice();
            paintAuto();
            render();

            // 順序要緊：fitStage 會改 stage 寬度，熱點座標是拿那個寬度換算的。
            el.img.addEventListener('load', function () { fitStage(); placeHotspot(); });
            window.addEventListener('resize', placeHotspot);

            // 語言切換是 doc-i18n.js 改 <html data-doc-lang>；監看它比互相呼叫乾淨。
            new MutationObserver(function () {
                setShot(null);            // 強制重設 src，否則同一張圖不會換語系
                render();
                paintVoice();
            }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-doc-lang'] });

            if ('speechSynthesis' in window) {
                speechSynthesis.addEventListener('voiceschanged', function () {
                    if (state.voiceBroken && voicesFor(lang()).length) {
                        state.voiceBroken = false;
                        paintVoice();
                    }
                });
            }
            window.addEventListener('pagehide', function () { stopAuto(); stopSpeaking(); });
        }).catch(function (e) {
            fail(root,
                '載入課程失敗：' + e.message,
                'Could not load the course: ' + e.message);
        });
    });
})();
