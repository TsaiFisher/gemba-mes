// Service Worker — SmartFactory MES 手機版（mobile-pwa.md §9 Phase E1 / §13.5）
// 策略：靜態資源 Cache-First；派工列表 Cache-First（5 分鐘）；API 寫入 Network-Only。

'use strict';

// ⚠️ 改動 STATIC_ASSETS 內任何檔案（含 i18n.js 字典、各頁 CSS/HTML）後必須 bump 版本號，
//    否則 Cache-First 會讓既有裝置一直吃舊檔（症狀：新增的 i18n key 顯示成 key 本身）。
// v9：機台事件 i18n 補 EO/GO/CO + 主檔快取不再把認證重導變成網路錯誤。
// v10：手機端 i18n 全面接上字典（sessions/reports 原本整頁沒有任何 data-i18n），
//      i18n.js 與多支預快取的 HTML 都動過，不 bump 的話舊裝置會繼續拿到中文版面。
// **改了預快取清單裡的檔案（i18n.js 就在裡面）就一定要 bump**，否則舊 SW 會繼續
// 供應舊版 JS，畫面照樣顯示 machine.event.EO 這種原始鍵，而且完全查不出原因。
// v11：導覽改走 handleNavigate（ignoreSearch ＋ 離線替代頁），新增 offline.html
//      與 /js/data-action.js 進預快取，i18n.js 補 offline.* 三鍵。
// v13：待我簽核（唯讀）——i18n.js 補 approval.* 與 home.approval，home.html 加固定入口。
//      ⚠️ approval/list.html **刻意不進 STATIC_ASSETS**：簽核是絕對不能離線做的事
//      （排隊期間單據可能被撤回、被別人簽掉、步驟已推進，ProcessApproval 一律 return false），
//      不預快取就會落到 handleNavigate 的離線替代頁——那正是我們要的行為。
// v14：手機可以簽核了（限有 IApprovalSummaryProvider 的單別）。i18n.js 補一批 approval.*，
//      list.html 多了簽核面板，list.page.js 多了摘要與核准／駁回。
//      ⚠️ **上面 v13 那條「不預快取」的決定不但保留，現在更重要**——多了寫入動作之後，
//      離線可用等於讓人簽下一張幾小時後才會失敗的單。offline-queue.js 也已明文
//      排除 /api/mobile/approval/（enqueue 直接擲出，有 jest 釘住）。
// v15：IPQC 製程檢驗的「不良」警告橫幅修好了。它的初始隱藏是 HTML class 上的
//      u-display-none（帶 !important），而 page.js 用 style.display='block' 想打開它，
//      蓋不過去——**這條橫幅從上線以來一次都沒出現過**。改成 classList 切。
//      ⚠️ 動到 wwwroot/mobile 下的檔就必須 bump 這個版本號，否則裝置吃的是舊快取，
//      修好了也看不到（這正是 v10 那次 i18n 修完沒生效的原因）。
// v16：manifest 的 start_url 由 `/mobile` 改成 `/mobile/index.html`。
//      ⚠️ 兩個獨立的理由，兩個都會讓「從主畫面圖示啟動」在離線時整個垮掉：
//      ① **作用域**：SW 註冊的 scope 是 `/mobile/`，而作用域比對是字串前綴——
//         `/mobile`（沒有尾斜線）**不在** `/mobile/` 底下，那一次導覽根本不經過這支 SW，
//         離線時使用者看到的是瀏覽器原生錯誤頁，連我們自己的 offline.html 都到不了。
//      ② **快取鍵**：預快取存的是 `/mobile/index.html`，`handleNavigate` 的 ignoreSearch
//         只忽略查詢字串、不忽略路徑，所以 `/mobile` 必定 miss；線上靠 Program.cs 的 302
//         轉到 index.html 才看起來正常，離線沒有那一跳。
//      manifest.json 不在 STATIC_ASSETS 裡，但它會被 fetch 處理器當一般靜態資源
//      Cache-First 收進 CACHE_NAME——**所以改它一樣要 bump**，否則既有裝置的
//      啟動網址永遠是舊的那一個。檔頭上面那句「改 STATIC_ASSETS 內的檔案才要 bump」
//      涵蓋不到執行期被收進來的檔，這是同一個陷阱的第二個入口。
// v17：三件一起——①每一頁補 apple-touch-icon（iOS 不讀 manifest 的圖示、也不吃 SVG，
//      沒有這一行主畫面上就是一張白底或頁面縮圖）；②manifest 補 192／512 PNG；
//      ③home.page.js 的離線佇列初始化搬到 requireAuth() 之前（原本離線時徽章不會出現）。
//      預快取清單裡的 HTML 有 7 頁被動到，home.page.js 則是執行期才被收進 CACHE_NAME 的，
//      兩種都要靠 bump 才換得掉。
// v18：離線冷啟動不再整頁空白——auth.js 的 requireAuth() 連不上伺服器時沿用最近一次
//      線上確認過的身分（上限同 session 長度 8 小時）；offline-queue.js 每筆記下排入者，
//      換人登入後不代送；mfg/list 的待同步徽章改讀 IndexedDB（原本讀一個沒人寫的 sessionStorage）。
//      同版另含：佇列一律把 clientId 寫進 body（上下架／盤點端點據此去重）、409 不再當成
//      「已送出」（並發衝突時那筆根本沒存進去）、盤點可離線排隊。
//      auth.js／api.js／offline-queue.js／i18n.js／mfg/list.html 全在預快取清單裡。
// v19：①預快取不再全有全無（見 install 處理器）；②mfg/reports.page.js 自 2026-08-12 雙語化起
//      就有語法錯誤，整支不執行——報工頁永遠轉圈、按鈕全死。它不在 STATIC_ASSETS，
//      是執行期被收進 CACHE_NAME 的，裝置上那份壞檔只有 bump 才換得掉。
// v20：i18n.js 新增 report.err_qty_negative、reports.page.js 送出前擋負數。v19 可能已裝到測試手機上，
//      同版本號不會換檔，畫面會顯示原始鍵名。
// v21：安全盤點修正——auth.js 登入／登出清掉派工與機台快取、登出清草稿與已送出紀錄；
//      ipqc-process.page.js 依派工取規格、規格上下限唯讀、以伺服器判定顯示合格與否。
//      登出前先同步、本人還有未送出的資料要先確認；首頁佇列只列本人的項目（i18n.js 補兩鍵）；
//      超過 24 小時未同步提醒；上工遇機台狀態閘口 Warning 顯示警示；api.js 顯示手機 API 的錯誤原因。
// v22：手機 setLang() 已登入時改 POST /api/mobile/auth/language（寫回帳號偏好）。
// v23：i18n.js 改用 Core 版（引擎＋登入／首頁／共用字串），模組字典拆到 i18n.modules.js 並加進預快取。
// v24：2026-10-05 離線實測回饋——
//      ①預快取補齊：原本只有 MFG 4 頁的 HTML，各頁 *.page.js 一支都沒有（執行期才收進快取），
//        沒先連線開過的頁面離線時版面出得來、腳本拿不到＝整頁沒反應。現在除簽核外每一頁的
//        HTML／page.js／CSS 都進預快取（守門：MobileServiceWorkerTests 逐頁比對）。
//      ②派工與機台查詢改「網路優先、離線才用快取」。原本是快取優先（派工 5 分、機台 30 分），
//        連線時也吐舊資料：手機改了機台狀態再掃一次仍顯示「正常」、離線下工同步後派工頁還留著
//        已結束的上工——登出再登入會清快取，所以「重登就好了」。
//      ③機台異動彈窗標題補數字；ipqc-process 規格查詢不再 400。
// v25：料件作業四頁（工單發退料／備料作業／委外收料／領料退料申請）＋共用 material-common.js；
//      上工頁多一顆「領料／退料」、倉儲作業選單多三張卡。委外收料只能連線（同時產生應付發票）。
// v26：生產準備看板帶出跨日延續的派工（preparation.page.js 標「延續」，字典補 sched.badge_carry_over）。
// v27：派工新增「已取消」狀態（工單作廢、暫停後未產出即結案）——list／schedule／schedule-query 三頁
//      狀態對照與 i18n.modules.js 補 dispatch.status.cancelled。
// v28：2026-10-09 Android 實測 P-5「改版送不到」——bump 版本名後手機確實裝了新 SW，但約 80 個
//      預快取檔只有 home.html 打到伺服器，其餘全由瀏覽器 HTTP 快取供應舊檔（靜態檔只有
//      ETag／Last-Modified、沒有 Cache-Control，瀏覽器依啟發式新鮮度直接用）。install 改用
//      cache:'reload' 一律跳過 HTTP 快取；伺服器端 /mobile 另加 Cache-Control: no-cache。
//      同版修 ipqc-process.page.js 首件檢驗送出網址（原本多了 /mfg，永遠 404）。
// v29：2026-10-10 領料／退料額度——requisition.page.js 改用伺服器帶下的可申請／可退量、只列領料（Pull）用料、
//      委外派工整頁停用；i18n.modules.js 補 mreq.outsource／requestable／returnable／over_req。
// v30：2026-10-12「離線作業」改為「斷線保護」（mobile-pwa.md §5.2）——
//      ①派工／機台查詢不再於連不上時回快取（mes-dispatch-v1／mes-master-v1 由 activate 清掉），
//        api.js 也拿掉「上次那一份」：讀取一律要連線，連不上就說「需要連線」。
//      ②offline-queue.js：伺服器連不到（onLine 仍為 true）也排隊、每分鐘重試、送出帶 X-Client-Sent-At
//        讓伺服器記發生時間；排隊超過 30 分鐘改為待確認。首頁待同步清單多「確認送出／捨棄」。
// v31：2026-10-12 委外收料當場驗收（mfg-domain.md §19.7 B2）——outsource-receive 頁多「不良數」欄與提示，
//      i18n.modules.js 補 osr.fail_qty／osr.fail_invalid／osr.inspect_hint。
// v32：2026-10-13 工序委外收回不入庫（mfg-domain.md §19.7 C）——outsource-receive 頁工序委外時不顯示儲位、多 opHint 提示，
//      i18n.modules.js 補 osr.op_hint。
// v33：2026-10-13 已備料的料先從備料區扣（工單發料頁可拆多個儲位、自動拆「備料區＋其他儲位」），
//      issue.page.js 改版；i18n.modules.js 補 mslip.staged_first／add_split／remove_split。
// v34：2026-10-20 自由盤點輸入儲位後毫無反應——stockcount.page.js 讀 /api/warehouse/location 回應用了
//      PascalCase（LocationCode／WhCode／WhName），實際是 camelCase，判斷恆為 undefined 直接 return。
// v35：2026-10-21 弱網逾時——api.js 讀取與會轉排隊的寫入 20 秒沒回應就當成連不上（寫入轉排隊），
//      offline-queue.js 補送每筆 20 秒逾時；原本卡在一筆不回的請求上整個佇列停擺。
// v36：2026-10-21 現場遙測——新增 telemetry.js（每頁第一個載入：頁面錯誤、API 耗時分桶、斷線多久、
//      未送出佇列，每分鐘送 /api/telemetry/events），23 頁 HTML 都加了這一行。
// v37：2026-10-21 telemetry.js 的 API 統計多計 409 衝突次數（conflicts）。
// v38：2026-10-27 料件頁儲位庫存量標示由「可用」改「在手」、揀料頁「可用庫存（各儲位）」改「各儲位庫存」
//      （i18n.modules.js mat.available／mat.no_stock／pick.avail_stock、inv/pick.html）——那個數字一直是儲位在手量。
// v39：2026-10-19 只有圖示的按鈕加上文字（95 個，驗收 P-7）——各頁 HTML／i18n 有改。
// v40：2026-10-27 切換廠別後，沒有個人語言的帳號改用新廠的預設語言（home.page.js 讀 /plants/switch 回傳的 language／followsPlant）。
// v41：2026-10-27 待簽核清單的單別改顯示名稱（approval/list.page.js 讀 documentTypeLabel）。
// v42：2026-11-03 庫存查詢可掃箱號（inv/stock.page.js 先試 /api/warehouse/container-unit，i18n 補 stock.box_of）。
// v43：2026-11-04 出庫下架可掃箱號、可下架有批號的庫存（inv/pick.page.js、pick.html，i18n 補 pick.lot／box_of／box_other_product）。
// v44：2026-11-04 入庫上架可指定裝入箱號（inv/putaway.page.js、putaway.html，i18n 補 putaway.box_label／box_ph）。
// v46：2026-11-06 廠別寫入帶畫面開啟時的廠別快照（js/api.js、js/offline-queue.js 送 X-Plant-Context；security-review §5 #3）。
// v47：2026-11-06 清單搜尋後點選選錯筆（inv/pick、putaway、iqc、mfg/fpi、ipqc 的 data-idx 改用完整清單位置）。
// v48：2026-11-09 盤點送出最後一張清冊後不再跳「無進行中的盤點清冊」警告（inv/stockcount）。
// v49：2026-11-12 筆電驗收修正：廠別失效提示並開切換面板、回到前景重抓廠別、待同步時間依語言、手機切換角色（home.html／home.page.js／js/api.js／i18n.modules.js）。
const CACHE_NAME = 'mes-mobile-v49';

// ⚠️ 只剩殼層這一個快取。若日後再新增 cache，**一定要同時加進 activate 的保留清單**——
//    activate 的清理是「保留清單以外一律刪」，看不到的快取名就等於沒被保留，
//    每次 bump 都會被整個刪掉（v24 以前 MASTER_CACHE 就是這樣）。

// 導覽在「網路連不上且快取沒有這一頁」時的最後防線（見 handleNavigate）。
const OFFLINE_PAGE = '/mobile/offline.html';

const STATIC_ASSETS = [
    '/mobile/index.html',
    '/mobile/index.page.js',
    '/mobile/home.html',
    '/mobile/home.page.js',
    OFFLINE_PAGE,
    // data-action 委派器：每一頁的按鈕都靠它，漏了預快取則冷啟動離線時整頁按鈕失效。
    '/js/data-action.js',
    '/mobile/css/mobile.css',
    '/mobile/css/index.css',
    '/mobile/js/i18n.js',
    '/mobile/js/i18n.modules.js',
    '/mobile/js/nav.js',
    '/mobile/js/telemetry.js',
    '/mobile/js/api.js',
    '/mobile/js/auth.js',
    '/mobile/js/scanner.js',
    '/mobile/js/offline-queue.js',
    '/mobile/js/libs/html5-qrcode.min.js',
    '/mobile/icons/icon.svg',
    '/lib/bootstrap/dist/css/bootstrap.min.css',
    '/lib/bootstrap/dist/js/bootstrap.bundle.min.js',
    '/lib/bootstrap-icons/font/bootstrap-icons.min.css',

    // ── 各作業頁：HTML＋腳本＋樣式三件一組 ──────────────────────────────────
    // ⚠️ 三件缺一都不行：只有 HTML 時離線版面出得來、腳本 503 → 按鈕全死、資料不載入，
    //    看起來像「頁面打開了但壞掉」（2026-10-05 實測：原本只有 MFG 4 頁的 HTML，page.js 一支都沒有）。
    //    新增手機頁面時三件都要加；MobileServiceWorkerTests 會逐頁比對 HTML 引用的本機資源。
    // ⚠️ approval/list.html **刻意不放**（見檔頭 v13／v14：簽核絕不能離線做）。
    '/mobile/mfg/list.html',            '/mobile/mfg/list.page.js',            '/mobile/css/mfg-list.css',
    '/mobile/mfg/schedule.html',        '/mobile/mfg/schedule.page.js',        '/mobile/css/mfg-schedule.css',
    '/mobile/mfg/schedule-query.html',  '/mobile/mfg/schedule-query.page.js',  '/mobile/css/mfg-schedule-query.css',
    '/mobile/mfg/sessions.html',        '/mobile/mfg/sessions.page.js',        '/mobile/css/mfg-sessions.css',
    '/mobile/mfg/reports.html',         '/mobile/mfg/reports.page.js',         '/mobile/css/mfg-reports.css',
    '/mobile/mfg/machine-status.html',  '/mobile/mfg/machine-status.page.js',  '/mobile/css/mfg-machine-status.css',
    '/mobile/mfg/preparation.html',     '/mobile/mfg/preparation.page.js',     '/mobile/css/mfg-preparation.css',
    '/mobile/mfg/fpi.html',             '/mobile/mfg/fpi.page.js',             '/mobile/css/mfg-fpi.css',
    '/mobile/mfg/ipqc.html',            '/mobile/mfg/ipqc.page.js',            '/mobile/css/mfg-ipqc.css',
    '/mobile/mfg/ipqc-process.html',    '/mobile/mfg/ipqc-process.page.js',    '/mobile/css/mfg-ipqc-process.css',
    '/mobile/inv/list.html',            '/mobile/inv/list.page.js',            '/mobile/css/inv-list.css',
    '/mobile/inv/pick.html',            '/mobile/inv/pick.page.js',            '/mobile/css/inv-pick.css',
    '/mobile/inv/putaway.html',         '/mobile/inv/putaway.page.js',         '/mobile/css/inv-putaway.css',
    '/mobile/inv/stock.html',           '/mobile/inv/stock.page.js',           '/mobile/css/inv-stock.css',
    '/mobile/inv/stockcount.html',      '/mobile/inv/stockcount.page.js',      '/mobile/css/inv-stockcount.css',
    '/mobile/inv/iqc.html',             '/mobile/inv/iqc.page.js',             '/mobile/css/inv-iqc.css',
    '/mobile/workorderexecution/list.html', '/mobile/workorderexecution/list.page.js',
    // 料件作業（2026-10-05）：四頁共用一支腳本與一份樣式
    '/mobile/js/material-common.js', '/mobile/css/inv-material.css',
    '/mobile/inv/issue.html',             '/mobile/inv/issue.page.js',
    '/mobile/inv/staging.html',           '/mobile/inv/staging.page.js',
    '/mobile/inv/outsource-receive.html', '/mobile/inv/outsource-receive.page.js',
    '/mobile/mfg/requisition.html',       '/mobile/mfg/requisition.page.js',
];

// 預快取分兩段，原因是 2026-09-27 手機實測：
//   原本是 `cache.addAll(STATIC_ASSETS).catch(console.warn)`。addAll 全有全無——任一檔抓失敗
//   整批作廢——而 catch 又把失敗吞掉，於是 SW 照樣啟用、手機上**一個預快取都沒有**，
//   而且直到下次 bump 都不會重試。症狀是離線開沒去過的頁面得到 Chrome 原生的「HTTP 503」
//   （handleNavigate 連 offline.html 都拿不到，只剩最後那個空的 503），看起來像伺服器壞了。
//   實測只是安裝當下網路抖了一下（經 Cloudflare Tunnel 剛建好時）；重裝就好。
//
//   ① 離線替代頁**必須**在：失敗就讓 install 失敗，瀏覽器下次 register() 時自動重裝。
//   ② 其餘逐檔盡力而為：少一兩個只會退化成「要先連線開過一次」，不會拖垮整批。
//   ③ 一律 cache:'reload'（v28）：預設模式會先問瀏覽器 HTTP 快取，bump 版本名照樣收到舊檔。
self.addEventListener('install', event => {
    event.waitUntil((async () => {
        const cache = await caches.open(CACHE_NAME);
        const fresh = url => new Request(url, { cache: 'reload' });
        await cache.add(fresh(OFFLINE_PAGE));

        const rest = STATIC_ASSETS.filter(url => url !== OFFLINE_PAGE);
        const results = await Promise.allSettled(rest.map(url => cache.add(fresh(url))));
        const failed = rest.filter((_, i) => results[i].status === 'rejected');
        if (failed.length) console.warn('[SW] 預快取未完成，這些檔要等連線使用時才補進快取：', failed);

        await self.skipWaiting();
    })());
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(
                keys.filter(k => k !== CACHE_NAME)
                     .map(k => caches.delete(k))
            ))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', event => {
    const { request } = event;
    if (request.method !== 'GET') return; // 寫入 API 完全不介入（mobile-pwa.md §13.5）

    const url = new URL(request.url);

    // API 一律 Network-Only（v30）：認證、寫入不快取，**讀取也不快取**。
    // v24–v29 的派工／機台查詢在連不上時回最近一次的回應——只有開過的那幾筆、而且是舊的，
    // 現場看了會以為手機可以離線作業。斷線保護只負責送出（offline-queue.js）。
    if (url.pathname.startsWith('/api/')) return;

    // 頁面導覽 — 與其他靜態資源分開處理，原因見 handleNavigate。
    if (request.mode === 'navigate') {
        event.respondWith(handleNavigate(request));
        return;
    }

    // 靜態資源 — Cache-First
    event.respondWith(
        caches.match(request)
            .then(cached => cached || fetch(request).then(resp => {
                if (resp.ok) {
                    const clone = resp.clone();
                    caches.open(CACHE_NAME).then(c => c.put(request, clone));
                }
                return resp;
            }))
            // 子資源離線且未快取時，fetch 會 reject。沒有這個 catch 的話 respondWith 收到
            // 一個 rejected promise，瀏覽器把它變成 net::ERR_FAILED——對頁面而言等同
            // 「SW 不存在」，看起來像離線功能整個沒生效。回一個 503 讓呼叫端能自己處理。
            .catch(() => new Response('', { status: 503, statusText: 'Offline' }))
    );
});

/**
 * 導覽請求（換頁、PWA 冷啟動）的處理。與一般靜態資源差在兩點：
 *
 * 1. **比對時忽略查詢字串**。`/mobile/mfg/reports.html?dispatchId=1&sessionId=2` 與快取裡的
 *    `/mobile/mfg/reports.html` 是同一份靜態檔，參數是頁面自己讀 `location.search` 用的。
 *    預設的 `caches.match(request)` 把整個 URL（含 query）當鍵，於是**只要帶參數就必然 miss**。
 *    實際會踩到的四條路徑：sessions→reports、mfg list→sessions、ipqc→ipqc-process、fpi→ipqc-process，
 *    全都是現場最常走的動線。
 *
 * 2. **一定要有東西可回**。導覽 fetch 失敗若不接住，使用者看到的是瀏覽器原生錯誤頁，
 *    上面完全沒有「你的報工還在佇列裡」這個訊息，現場只會以為資料不見了。
 *
 * 快取寫入時刻意存**去掉查詢字串**的網址：同一頁不同參數只留一份，
 * 否則掃十張派工單就會在快取裡堆十份同樣的 HTML。
 */
async function handleNavigate(request) {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(request, { ignoreSearch: true });
    if (cached) return cached;

    try {
        const resp = await fetch(request);
        // redirected 代表伺服器把它導去登入頁——把登入頁存在原網址下，
        // 之後離線開該頁就會永遠看到登入畫面，比沒有快取更難查。
        if (resp.ok && !resp.redirected) {
            const key = new URL(request.url).pathname;
            cache.put(key, resp.clone());
        }
        return resp;
    } catch {
        return (await cache.match(OFFLINE_PAGE)) ||
            new Response('', { status: 503, statusText: 'Offline' });
    }
}

// ── Web Push（Phase E5）────────────────────────────────────────────────────────
self.addEventListener('push', event => {
    if (!event.data) return;
    let payload;
    try { payload = event.data.json(); } catch { payload = { title: '新通知', body: event.data.text(), url: '/mobile/home.html' }; }

    event.waitUntil(
        self.registration.showNotification(payload.title || 'SmartFactory MES', {
            body:    payload.body  || '',
            icon:    '/mobile/icons/icon.svg',
            badge:   '/mobile/icons/icon.svg',
            data:    { url: payload.url || '/mobile/home.html' },
            vibrate: [200, 100, 200],
            tag:     'mes-push',
            renotify: true
        })
    );
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url || '/mobile/home.html';
    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true }).then(list => {
            for (const client of list) {
                if (client.url.includes('/mobile/') && 'focus' in client) {
                    client.navigate(url);
                    return client.focus();
                }
            }
            return clients.openWindow(url);
        })
    );
});
