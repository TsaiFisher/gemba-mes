# 行動裝置 PWA 規劃草稿（mobile-pwa.md）

> 來源：逆向工程 `/home/dev/ProjectZ-O/m/` 下 15 支現場手機作業程式
> 與 [mfg-domain.md](./mfg-domain.md) 互為對照
> 核心規格已由 Fisher 確認（見 §10），可作為實作依據

---

## ⚠️ 架構決策（定案 — 閱讀全文前必看）

**前端＝原生 JavaScript（無 Vite/Vue）；認證＝Cookie（與桌機共用）；不引入 JWT。**
完整決策見 [§9 Phase E1](#phase-e1pwa-骨架2-3-週) 與 [§11.4](#114-已確認重點架構決策摘要)。

> ### 📍 實作現況對照（2026-06：以實際程式碼為準，覆蓋本檔早期草稿用語）
>
> 本檔早期段落多處寫「輸出至 `wwwroot/pwa/`（或 `wwwroot/mobile/`）」——**已定案並收斂為 `wwwroot/mobile/`**。閱讀內文遇到 `wwwroot/pwa/` 字樣時，一律以下表為準：
>
> | 項目 | 定案現況（live） | 說明 |
> |------|-----------------|------|
> | **PWA 前端目錄** | **`wwwroot/mobile/`** | 28 檔主力實作：`index.html` / `home.html`、`inv/`（iqc/list/pick/putaway/stock/stockcount）、`mfg/`（ipqc/ipqc-process/preparation/machine-status/schedule/sessions/reports…）、`workorderexecution/`、`js/`（`auth.js`、`api.js`、`offline-queue.js`、`i18n.js`、`scanner.js`、`libs/html5-qrcode.min.js`）、`css/mobile.css`、`manifest.json`、`sw.js`、`icons/` |
> | **進入點** | **`/mobile/`** → `Program.cs` 轉址至 `/mobile/index.html` | 桌機走其他路徑進 MVC |
> | **後端 API** | **`Api/Mobile/Mobile*Controller.cs`**（Auth / Inv / Mfg / Push / Module）+ `MobilePermissionController` / `IMobilePermissionService` | 路由族 `/api/mobile/*` |
> | **模組/動作種子** | `MobileModule` / `MobileModuleAction`（`Program.cs` 啟動種子；目前啟用 Mfg、Inv） | QualityInspection / MaterialPicking 預設 `IsActive=false` |
> | **MFG 作業鍵** | `Setup` / `StartWork` / `StopWork` / `Report` / `MachineStatus` / `Inspect` / `ScheduleView` | `ScheduleView` 涵蓋 `schedule.html`、`schedule-query.html`、`preparation.html`；後端 `GET /api/mobile/schedule`、`/schedule-query` 已守門 |
> | **作業入口呈現** | `mfg/list.html` 頂部「作業功能」卡列（圖示＋文字），由 `list.page.js` 依 `GET /api/mobile/modules/Mfg/actions` 過濾；取不到權限時整區隱藏 | 取代原本 navbar 的無文字圖示；`sessions.html` / `reports.html` 須帶 `dispatchId` / `sessionId`，僅能由派工卡進入，不列為捷徑 |
> | **新作業鍵回補** | `Program.cs` 種子區：新增作業鍵時，對「該模組已有任一 `IsAllowed=true`」的角色自動補該鍵並設為允許 | 否則既有部署升級後入口整批消失；既存列（含刻意設 false 者）不覆寫 |
> | **頁面樣式** | **一頁一 CSS 檔**：`/mobile/css/{目錄}-{頁名}.css`（如 `mfg-list.css`、`inv-pick.css`），HTML 內**不得**有 `<style>` 區塊 | CSP `style-src 'self'` 無 `unsafe-inline`（`Program.cs`），內聯 `<style>` 會被整段擋掉、樣式全失效。各頁 class 常同名異義（`.dispatch-card` 在 list/schedule 定義不同），故不併入 `mobile.css` |
> | **返回鍵** | **全站統一** `<button data-action="historyBack" data-fallback="/mobile/…">`，handler 在 `/mobile/js/nav.js`（頁面須載入此檔） | 有同源上一頁走 `history.back()`，否則走 fallback；直接開網址／掃 QR 進入不會卡死。**不得混用硬編 `href` 返回**——硬編返回會 push 一筆新歷史，上層頁的 `history.back()` 就退回剛離開的頁形成迴圈（症狀：排程→排程查詢→返回後再也回不到投產報工）|
> | **導覽列按鈕** | 除 `index.html`（登入）／`home.html`（首頁）外，每頁 navbar 右側依序為 **`<a href="/mobile/home.html">` 回首頁（`bi-house-door`）→ 語言切換（`data-lang-toggle`）→ 登出**；左側為返回鍵 | 回首頁刻意用純 `<a>`（JS 掛掉仍可用），與「上一頁」語意分開：返回是退一層、家是直接回模組選單 |
> | **多語覆蓋** | **每頁都必須載入 `/mobile/js/i18n.js` 並帶語言切換鈕**；靜態文字掛 `data-i18n`，`.page.js` 動態渲染的文字一律走 `t()` / `tf()` | 曾漏掉 `schedule.html` / `schedule-query.html` / `preparation.html` 三頁（整頁硬寫中文、連 i18n.js 都沒載入），症狀是**這幾頁沒有 EN 切換鈕**。狀態譯名共用 `dispatch.status.*`，勿另立一套 |
> | **模組目錄 URL** | `/mobile/mfg`、`/mobile/inv` 於 `Program.cs` 轉址到各自 `list.html`（比照既有 `/mobile`）| 目錄本身無 index 檔，未攔截時會落到 MVC default route 顯示**電腦版首頁**。新增手機模組目錄時要一併補這條轉址 |
> | **SW 快取版本** | 改動 `sw.js` 的 `STATIC_ASSETS` 內任何檔案（HTML／CSS／`i18n.js`）後**必須** bump `CACHE_NAME` | Cache-First 會讓既有裝置續用舊檔；典型症狀是新增的 i18n key 顯示成 key 本身（`dispatch.op_section`）。`applyI18n` 已改為查無 key 就保留 HTML 既有文字 |
> | **`wwwroot/pwa/`（5 檔骨架）** | **早期原型，已移除（2026-10-01）** | INV `PwaController`（`/Pwa/Index` 殼層）與 `wwwroot/pwa/` 已刪除，倉儲作業改走 `wwwroot/mobile/inv/`（上架、揀貨、庫存、盤點）；後端 `/api/warehouse/*` 保留 |
>
> 凡內文「擴展/輸出至 `wwwroot/pwa/`」均應理解為「在 `wwwroot/mobile/` 下新增對應頁面」。離線佇列已實作於 `wwwroot/mobile/js/offline-queue.js`。

> **以下段落為「未採用的 SPA 替代方案」分析，保留供日後若有長離線需求升級時參考，實作請勿據以建立 Vue 工程**：
> §3.1「選項A 獨立 SPA」、§3.2 前端框架比較、§4 JWT 認證、§5.2 的 Workbox/JWT 重放機制、§12.2 Vitest 前端測試、§13 Vue/Pinia/Vite 程式碼、§14 IPQC Vue 元件。
>
> **關鍵差異**：Cookie 方案下 §5.2 的「Workbox 重放時 JWT 過期致命缺陷」**不存在**（Cookie 隨請求自動帶上、sliding expiration 自動續期）。**寫入佇列**仍需要（應用層 IndexedDB 佇列 + `online` 事件／定期同步 + 後端 `clientId` 去重），只是不需要 JWT refresh 那一段。
>
> 📍 **2026-10-12：「離線作業」已改為「斷線保護」**——讀取一律要連線、佇列只負責送出時短暫斷線、伺服器記發生時間。見 [§5.0](#50-定案斷線保護不是離線作業)。

---

## 1. 舊系統現況盤點

### 1.1 技術堆疊
| 項目 | 舊系統 |
|------|--------|
| 框架 | jQuery Mobile 1.4.5 (2014) |
| 後端 | ASP.NET WebForms + WebMethod |
| 打包方式 | Cordova（PhoneGap hybrid app）|
| 條碼掃描 | phonegap-plugin-barcodescanner v6.3.1 |
| 認證 | ASP.NET Session（伺服器端 cookie）|
| 離線能力 | UI 有「離線瀏覽」開關，**但程式碼沒實作** |
| 中文 UI | Hard-coded，無多語 |

### 1.2 功能盤點（舊系統 11 支，新增 1 支）

| 代碼 | 中文 | 模組歸屬 | 主要動作 | 對應 Entity |
|------|------|---------|---------|------------|
| **aps** | 投產 | MFG | 開工 | `MSFB` (上工) |
| **apc** | 完工 | MFG | 報工合格/不良 | `MSFC` (報工) |
| **apq** | 生產檢驗 | MFG | QC 檢驗 | `MSFC` (檢驗欄位) |
| **api** | 生產繳庫 | MFG → INV | 完工入庫 | `II/II_D` |
| **aiq** | IQC | INV | 進料檢驗 | `QIQ` |
| **aii** | 入庫上架 | INV | Put-away | `MSMC` |
| **aio** | 出庫下架 | INV | Pick | `MSMC` |
| **aip** | 備料（隱藏）| INV | Kitting | - |
| **ail** | 使用清冊盤點 | INV | Cycle count with list | - |
| **ain** | 不使用清冊盤點 | INV | Blind count | - |
| **air** | 覆盤 | INV | Re-count | - |
| **aps-setup** ⭐新增 | 開工準備回報 | MFG | Setup 完成回報 | `ProductionSetup`（新增 Entity）|
| **agtr** ⭐新增 | 機台狀態異動 | MFG | 故障/保養/恢復 狀態切換回報 | `MachineStatusOrder`（新增 Entity）|

> **Setup 回報規格**：`RoutingOperation` 的工時分為 Setup 工時（`TM_SETUP`）與單件工時（`TM_UNIT`）。
> 開工前的備料、換模、調機、調色等準備作業完成後，作業員以手機回報 Setup 完成，
> 記錄實際 Setup 起訖時間。後續可分析 Setup 達成率與實際/標準差異，作為改善依據。

> ⚠️ **重要觀察**：手機作業**跨 MFG 與 INV 兩個模組**。手機 PWA 不能歸屬到單一模組底下，需獨立規劃。

### 1.3 共同工作流程
不論哪一支功能，舊系統的操作流程都類似：

```
1. 登入（帳密 + 角色）
2. 掃描關鍵單據條碼（NO_WW、CN_ID 等）
3. 後端查詢回傳該單據的當前狀態與選項
4. 操作員輸入/選擇數量、目的地、不良原因等
5. 提交 → 後端寫入紀錄、回傳成功訊息
6. 繼續下一筆 或 切換功能
```

---

## 2. 為什麼要做成 PWA（而不是繼續 Cordova）

| 維度 | Cordova/hybrid | PWA |
|------|---------------|-----|
| 部署 | 打包 → App Store/MDM 派送 | 開瀏覽器即可 |
| 更新 | 用戶要手動更新 App | 自動更新 |
| 安裝 | 走 App Store | 加到主畫面（A2HS）|
| 維護 | iOS/Android 各一套 + 升級 plugin | 單一程式碼 |
| 條碼 | Native plugin | `BarcodeDetector API` 或 `ZXing-js` |
| 推播 | OneSignal/FCM | Web Push |
| 離線 | 自己刻 | Service Worker |
| 跨平台 | iOS/Android | iOS/Android/Windows/Mac |

**結論**：PWA 對 MES 場景的所有優點都成立，唯一弱點是 iOS 的 Web Push 在 16.4 之後才支援。

---

## 3. 建議的 PWA 架構

> ⚠️ **定案以 §9 Phase E1 / §11.4 為準**：原生 JS、輸出至 `wwwroot/pwa/`、Cookie 認證。
> §3.1「選項A 獨立 SPA」指的是**原始碼分離**（見 §3.1 補充說明），**不是** Vue/Vite SPA；§3.2 的 Vue/React/Vite 框架比較為**未採用替代方案**。

### 3.1 部署模型

**選項 A：獨立 SPA 專案（推薦）**
```
ProjectZ/
├── SmartFactory.MES (現有 MVC 主程式)
│   └── /api/mobile/* (新增 Web API 端點)
└── SmartFactory.MES.Mobile/ (新增 SPA 專案)
    ├── package.json
    ├── public/
    │   ├── manifest.json (PWA 安裝資訊)
    │   └── service-worker.js
    └── src/
        ├── pages/
        ├── api/
        └── stores/
```

> **部署模型補充說明**：
> - PWA 前端（原生 JavaScript，見 § 9 Phase E1 最終決策）輸出至現有的 `wwwroot/pwa/` 或 `wwwroot/mobile/` 目錄（已存在）
> - 桌機 MVC 與 PWA 共用同一 ASP.NET Core 主機、同一 ApplicationDbContext 與服務層
> - 認證走 Cookie（同桌機，見 § 9 Phase E1），資料透過 `/api/mobile/*` 端點存取（若 Phase E3 需長時間離線，可升級為 JWT）
> - **選項 A 的「獨立 SPA 專案」** 是指原始碼分開（`SmartFactory.MES.Mobile/` 獨立開發）、
>   `dotnet build` 後輸出至主專案的 `wwwroot/mobile/`，**不需要另建獨立伺服器**
> - 對外只有一個 URL，手機瀏覽 `/mobile/` 進入 PWA，桌機瀏覽其他路徑進入 MVC

**理由**：
- 前後端解耦，可獨立部署
- 行動端用 Vue/React/Vite 等現代框架，不必綁 Razor
- API 走標準 REST/JSON，與桌機 MVC 後端用同一 ApplicationDbContext 與服務層

**選項 B：MVC 子路由**
```
SmartFactory.MES/
└── /mobile/* (Razor 頁面，響應式)
```
**缺點**：難做離線、開發體驗差、與桌機畫面糾纏

**建議採 A**。

### 3.2 前端框架建議

| 候選 | 推薦度 | 理由 |
|------|--------|------|
| **Vue 3 + Vite + Pinia** | ⭐⭐⭐⭐ | 中文資源多、學習曲線平緩、PWA plugin 成熟 |
| React + Vite | ⭐⭐⭐ | 主流但比 Vue 對小團隊複雜些 |
| Svelte + SvelteKit | ⭐⭐⭐ | 輕量但生態較小 |
| 純 HTML + Alpine.js | ⭐⭐ | 最低門檻但離線能力弱 |

**搭配**：
- UI 庫：**Vant**（行動端 Vue，中文友善）或 **Quasar**
- 條碼掃描：**`@zxing/library`** 或 **原生 BarcodeDetector**（Chrome/Android）
- 狀態：**Pinia**（Vue）
- HTTP：**Axios** + 重送攔截器
- 離線：**Workbox**（Google 官方 SW 工具）

---

## 4. 認證策略

> ⚠️ **未採用 — 替代方案參考**。定案為 **Cookie 認證（與桌機共用）**，見頂部架構決策與 §11.4。
> 本節 JWT + Refresh Token 方案僅在「跨網域」或「>1 小時長離線」需求出現時才需評估升級。

### 4.1 問題
舊系統用 ASP.NET Session。PWA 若**跨網域呼叫 API**或需**>1 小時長離線**，Cookie 不足，才需改 Token-based。
ProjectZ 的 PWA 與桌機同源、同主機，**不跨網域**，且離線多為短暫中斷 → **沿用 Cookie 即可**，以下 JWT 方案保留備查。

### 4.2 建議方案：JWT + Refresh Token

> ⚠️ **未採用 — 替代方案參考**。定案為 Cookie 認證，本節整節僅供日後長離線需求評估時參考。

```
Login Flow:
  POST /api/auth/login
  Body: { username, password }
  Response: {
    accessToken: "eyJ..." (15 分鐘),
    refreshToken: "..." (30 天),
    user: { id, name, plantId, permissions }
  }

每個 API 呼叫帶 Header:
  Authorization: Bearer eyJ...

Access Token 過期時:
  POST /api/auth/refresh
  Body: { refreshToken }
  Response: { accessToken, refreshToken }
```

**儲存位置**：
- `accessToken` → `sessionStorage`（記憶體，關閉瀏覽器即清）
- `refreshToken` → `IndexedDB` 加密儲存（PWA 安裝後仍存在）

**為什麼不直接用桌機 Cookie 認證**：行動端可能跨網域、Cookie 在 PWA 安裝後行為不一致。

### 4.3 與現有 MES 認證的銜接

> ⚠️ **未採用 — 替代方案參考**。**現況是單一套 Cookie 認證，桌機與手機共用**，
> 沒有「兩套並存」這件事：`Api/Mobile/MobileAuthController.cs` 走的是與桌機同一組
> Identity Cookie，專案未安裝 `JwtBearer`。以下編號步驟是升級路徑的假設方案，不是現況描述。
>
> ⚠️ `Api/Mobile/MobileAuthController.cs` 的類別註解仍寫著「待 PWA 推進時將切換至 JWT」，
> **與頂部定案相反**。該檔是 Core 既有檔（不在漂移 baseline 內），就地改會產生新漂移，
> 故留在此處登錄；要修須走 Core PR。

ProjectZ 已有 ASP.NET Identity（看到 `UserManager<ApplicationUser>`），**若日後真的需要升級**，建議：
1. 沿用現有 User 資料表
2. 加裝 JWT 套件（如 `Microsoft.AspNetCore.Authentication.JwtBearer`）
3. 新增 `/api/auth/login`、`/api/auth/refresh` 兩支 controller
4. 桌機端維持 Cookie 認證，行動 API 用 JWT，兩套並存

---

## 5. 離線策略 → 斷線保護（2026-10-12 定案）

### 5.0 定案：斷線保護，不是離線作業

> **本節取代 §5.1–§5.3 的規劃與舊〈實作現況〉。** 那幾節保留作歷程，凡與本節衝突一律以本節為準。

**為什麼改**：2026-10-09／10-10 Android 實測後逐項對照一般定義的「離線作業」（斷網一個班次內仍能開得起來、
看得到作業資料、做完核心作業、連線後正確同步）——作業資料沒有預載（只存「連線時開過的頁面」）、上工／FPI／IPQC／IQC／
料件作業有一半不能離線、伺服器把補送的資料記在送達時間。補到真正的離線作業是中大型專案（離線上工要暫時編號對應、
檢驗只能「待判定」、同步錯誤處理畫面）。決定先把宣稱改成實際能力：**手機版需要連線使用，只保護「送出那幾秒剛好斷線」**。
日後客戶廠區真的需要長時間離線，再照「A 方案」補做。

| 項目 | 定案行為 | 實作 |
|---|---|---|
| 讀取 | **一律要連線**。連不上時提示「需要連線」（`common.need_connection`，同頁只提示一次），不拿舊資料頂替 | `api.js` `apiGet()`（取代 `apiGetOrLastKnown`）；`sw.js` v30 起 `/api/` 全部 Network-Only，`mes-dispatch-v1`／`mes-master-v1` 由 activate 清掉；舊版存在 localStorage 的 `mobileOfflineGet:*` 載入時清除 |
| 送出 | 連得上直接送；**fetch 本身失敗**（`navigator.onLine` 為 false，或 Wi-Fi 有訊號但伺服器連不到）或**閘道錯誤 502／503／504**（經 Cloudflare Tunnel 時伺服器停掉，手機拿到的是 502 不是網路例外）才排隊；佇列 flush 遇閘道錯誤也留 pending 停下。**20 秒沒有回應**也當成連不上（訊號弱到送得出去、回不來；2026-10-21 起，`API_TIMEOUT_MS`／`OQ_REQUEST_TIMEOUT_MS`，只用在讀取與帶 clientId 的寫入）。伺服器有回應（含 4xx、500）照原樣交回、不排隊 | `offline-queue.js` `submitOrQueue()`；第一次直接送就帶 `clientId`／`clientTimestamp`，轉排隊沿用同一個 clientId，伺服器去重 |
| 可排隊的作業 | 報工、完工並報工、下工、機台狀態（priority 1）、上架、下架（3）、盤點初盤／複盤、工單發退料過帳、備料移入、領退料申請 | 呼叫端：`mfg/reports`、`mfg/sessions`、`mfg/machine-status`、`inv/putaway`、`inv/pick`、`inv/stockcount`、`js/material-common.js` |
| 只能連線 | 簽核（`isNeverQueued` 擲出）、上工、FPI／IPQC／IQC、委外收料（同時產生應付發票） | |
| 同一筆同時到達兩次 | 逾時或回應遺失後立刻重送，第一個請求還在伺服器上跑；或兩個分頁的佇列同時補送。後存的撞 `PK_SyncLogs`，業務變更一起回滾，`DbUniqueViolationExceptionFilter` 回**先到者的結果（200）**，不是 409（2026-10-21 前是 409，佇列標成失敗、人會重報） | 整合測試 `MobileDuplicateInFlightTests`；混沌 UI 測試 `MobileChaosTests`／`MobileChaosE2ETests` |
| 並發衝突 | 同一張派工多人同時寫（xmin 對不上）時，上工／下工／報工／完工在**伺服器端**以最新資料重做最多 3 次，仍衝突才回 409 CONCURRENCY_CONFLICT（佇列再重送 3 輪）。2026-10-21 換班尖峰壓測：60 人同時時報工第一次送出四到五成撞 409，修正後 0 | `MobileMfgController.WithConcurrencyRetryAsync`；`deploy/capacity-and-db-connections.md` |
| 自動補送 | `online` 事件＋**每 60 秒**（連得上網路才試；沒送出任何東西就不跳通知） | `OQ_RETRY_INTERVAL_MS`。伺服器重啟、廠區網段短暫斷線時手機的網路狀態沒變，`online` 不會發生 |
| 排隊上限 | 排入超過 **30 分鐘**不再自動送出，轉 `held`（待確認）；首頁待送出清單「確認送出」（記 `confirmedAt` 後照送）或「捨棄」（限 held／error）。「重試」也視同確認 | `OQ_AUTO_SEND_MS`；`confirmHeld()`／`discard()`；held 計入徽章紅色、計入「切廠前須送完」與登出前未送出筆數 |
| 發生時間 | 伺服器記**按下送出的時間**：佇列送出帶 `X-Client-Sent-At`（手機當下時間），排隊時間＝送出−排入（同一支手機的時鐘，偏差抵銷），發生時間＝伺服器現在−排隊時間。沒帶 header（直接連線）照舊記送達時間，不採信手機時鐘 | `Api/Mobile/MobileOccurredAt.cs`＋`MobileMfgController.ResolveOccurredAtAsync`：下工 `EndAt`、完工 `EndAt`＋`ReportedAt`、報工 `ReportedAt`、機台狀態 `EventAt` |
| 合理性檢查 | 不早於上工時間／該機台上一筆事件（夾住）；不產生未來時間；排隊超過 **24 小時拒收** `OCCURRED_TOO_OLD`（改在桌機補登）；回推到別的日期時該日期間須可過帳，否則 `PERIOD_CLOSED` | 庫存／料件端點維持送達時間——過帳日期由庫存服務與期間鎖決定，30 分鐘上限內誤差可忽略 |
| 斷線時沿用身分 | 保留（上限 8 小時）。用途只剩：斷線中重開 App，佇列仍知道「現在是誰」，恢復後只送本人排的項目 | `auth.js` `offlineUser()` |
| 殼層預快取 | 保留。斷線時開得起來、看得到待送出清單與「需要連線」 | `sw.js` `STATIC_ASSETS` |

守門：jest `api.test.js`（apiGet）、`offline-queue.test.js`（held／confirmHeld／discard／X-Client-Sent-At／submitOrQueue）、
`sw-api-network-only.test.js`；xUnit `MobileServiceWorkerTests.API一律不經SW快取_連不上就是連不上`、
`Tests/Mobile/MobileOccurredAtTests`（換算邊界）、`IntegrationTests/Mobile/MobileOccurredAtTests`（下工／報工／完工／上工夾住／24h／關帳期間）、
`MobileMachineStatusPlantScopeTests` 兩支（機台事件時間、不早於上一筆）。

### 5.1 區分「需要離線」與「不需要離線」（規劃，已由 §5.0 取代）

> ⚠️ **下表是需求分級（規劃），不是實作現況。** 最右欄「策略」寫的是「應該怎麼做」，
> 逐條與程式對照見本節末的〈實作現況〉。（~~其中「盤點寫入」目前沒有離線能力~~：2026-09-25 已補，見下表；2026-10-08 更正此句）

| 資料/操作 | 離線需求 | 策略 |
|----------|---------|------|
| 派工清單（我的待辦）| 高 | 預先快取，離線可瀏覽 |
| 上工/報工/盤點寫入 | **最高** | 離線排隊，連線後同步 |
| 庫存即時查詢 | 中 | 快取 5 分鐘 |
| 條碼掃描的「主檔解讀」| 中 | 預先快取常用主檔 |
| 派工排程結果 | 低 | 線上即時查詢 |
| 報表/分析 | 低 | 線上即時 |

> #### 實作現況（2026-08-21 逐檔查證，以程式為準）
>
> | 上表項目 | 現況 | 依據 |
> |---|---|---|
> | 派工清單離線瀏覽 | ✅ 有 | `sw.js` `DISPATCH_CACHE`，Cache-First 5 分鐘（`DISPATCH_MAX_AGE_MS`）；離線且無快取時回一個 `success:false / code:"OFFLINE"` 的 200，不是原生錯誤頁 |
> | 上工／報工離線排隊 | ✅ 有 | `mfg/reports.page.js`、`mfg/machine-status.page.js` 各一處 `enqueue()` |
> | 上架／下架離線排隊 | ✅ 有 | `inv/putaway.page.js`、`inv/pick.page.js` 各一處 `enqueue()`。**2026-09-25 補去重**：原本這兩支端點沒有 `clientId` 去重（佇列自己生的 clientId 只當 IndexedDB 鍵、從沒送到伺服器），「已處理、回應在路上斷線」後的重送會再上／下架一次。現在佇列一律把 `clientId`／`clientTimestamp` 寫進 body，`WarehouseApiController.BeginIdempotentAsync` 把 SyncLog 加進追蹤、**與業務變更同一交易**存檔（業務回滾它也不存在；驗證失敗不佔用 clientId），重送回 200 `duplicate:true` |
> | 盤點離線排隊 | ✅ **2026-09-25 補** | 三種模式（依清冊／自由／複盤）離線時 `enqueue()`。前置：`count/first-count`、`count/recount` 以 clientId 去重（同一次 SaveChanges 寫 SyncLog），重送回第一次結果——原本自由盤點每送一次就多建一筆明細。快照只存**失敗的明細**、camelCase（前端讀 `data[].success`），避開 `ResponseSnapshot` 2000 字元上限。**帳面量 `BookQty` 維持取送達當下的庫存**（使用者裁決）：離線期間有進出時，差異會包含那段期間的異動。部分明細失敗時佇列標「已送出」但留黃字（重送只會拿回同一份結果，失敗那幾行要重盤）。同批修前置缺陷：已覆盤的明細不得再改初盤（手機＋桌機） |
> | 完工、發料／退料／備料離線排隊 | ✅ 有（2026-10 補） | `mfg/sessions.page.js` 完工一處 `enqueue()`；`js/material-common.js` 的 `matSubmit()` 在 `allowQueue: true` 時排隊（`inv/issue`、`inv/staging`、`mfg/requisition`）。**委外收料刻意 `allowQueue: false`**——確認收料同時產生應付發票，只能連線 |
> | **佇列把 409 當成已送出** | ✅ **2026-09-25 修** | 原本 `res.ok \|\| res.status === 409` 一律標 sent，註解說「409＝後端已去重」——但會進佇列的端點重送時回的是 2xx，**409 是 `CONCURRENCY_CONFLICT`（xmin 被別人先改，那筆沒存進去）**。離線報工／機台狀態撞上並發衝突就這樣靜靜消失。現在並發衝突留 pending 自動重送，連撞 3 輪才轉 error；其他 409 直接 error |
> | 庫存即時查詢快取 5 分鐘 | ❌ 沒有 | `sw.js` 只對 `/api/mobile/dispatches`（5 分）與 `/api/mobile/machines*`（30 分）快取；其餘 `/api/*` 一律 Network-Only |
> | 主檔預先快取 | ⚠️ 只有機台 | 見 §5.3 的〈實作現況〉 |
> | **PWA 冷啟動（從主畫面圖示）** | ✅ **2026-09-24 修** | `manifest.json` 的 `start_url` 原為 `/mobile`，**兩個條件同時不成立**：① SW 註冊的 scope 是 `/mobile/`，而作用域是**字串前綴**比對，`/mobile` 少一個尾斜線就**不在**作用域內——那一次導覽根本不經過 SW，離線時是瀏覽器原生錯誤頁，連 `offline.html` 都到不了；② 預快取的鍵是 `/mobile/index.html`，`handleNavigate` 的 `ignoreSearch` 只忽略查詢字串不忽略路徑，`/mobile` 必定 miss。線上靠 `Program.cs:467` 的 302 轉過去，所以**完全看不出問題**。已改為 `/mobile/index.html` 並 bump `CACHE_NAME` 到 `v16`（同日的圖示與順序修正又帶到 `v17`）（manifest 不在 `STATIC_ASSETS` 裡，但會被 fetch 處理器當一般靜態資源收進 `CACHE_NAME`，**不 bump 既有裝置的啟動網址永遠是舊的**）。守門：`MobileServiceWorkerTests.啟動網址必須落在SW作用域內且已預快取` |
> | **iOS 主畫面圖示** | ✅ **2026-09-24 補** | iOS 的「加入主畫面」**不讀 manifest 的 icons**，只認 `<link rel="apple-touch-icon">`，且不支援 SVG——而本專案原本只有一張 SVG。症狀是主畫面上一張白底或頁面縮圖，**桌機、Android、Playwright 全部看不出異常**。已補 `apple-touch-icon-180.png` 並掛在 20 個掛了 manifest 的頁面上，manifest 另補 192／512 PNG（`any` ＋ `maskable`；圖形落在中央安全區內，兩種用途共用同一檔）。守門：`MobilePwaShellTests` 前兩支 |
> | **推播權限請求的時序** | ✅ **2026-09-24 修** | `togglePush()` 原本先 `await` 一次取 VAPID 公鑰再呼叫 `Notification.requestPermission()`。WebKit 要求權限請求落在使用者手勢的有效期內，那一個 await 就足以讓它失效 → **iOS 按了鈴鐺完全沒反應**（沒有對話框、沒有錯誤、沒有 Toast），Android 因手勢有效期較寬鬆而正常——**只壞一半裝置的缺陷不會有人回報成「推播壞了」**。已把 `requestPermission()` 提到第一個 await 之前 |
> | **離線時的待同步徽章** | ✅ **2026-09-24 修** | `home.page.js` 的佇列初始化原本排在 `requireAuth()` 的 early return 之後，離線時根本執行不到 → **「還有幾筆沒送出」在最需要它的時候看不見**。佇列是純本地 IndexedDB，讀它不需要認證，已搬到認證之前 |
>
> 全站 `enqueue()` 的**呼叫端**是上表列出的 7 支（報工、機台狀態、上架、下架、盤點、上下工完工、`material-common.js`）——~~只有上列四處~~（2026-10-08 更正）；`js/offline-queue.js` 是佇列本身的定義，
> `home.page.js` 只讀 `enqueuedAt` 顯示待同步清單，不寫入佇列。
>
> ~~🚨 **上面這些離線能力全部只在「頁面已經開著」時成立。**~~ **2026-09-25 修（SW v18）**。
> 原狀：`requireAuth()` 第一行就打 `/api/mobile/auth/me`，離線必然失敗 → 每一頁
> `if (!user) return;` → **版面出得來、內容一片空白**。預快取解決的是「拿不拿得到 HTML」，
> 擋在前面的是「認不認得這個人」；認得人之後，首頁模組格、作業捷徑權限、待上／下架清單
> 又都是 Network-Only 的 `/api/*`，照樣沒有東西可點。現在：
>
> | 環節 | 作法 | 刻意的邊界 |
> |---|---|---|
> | 認得人 | `requireAuth()` 連不上（網路例外或 5xx）時沿用 localStorage `mobileUserOffline`（最近一次線上確認過的白名單欄位） | 上限 8 小時＝伺服器 session 長度（`Security:SessionHours` 調短時要跟著改）；**401 與登出一律清掉**，離線不得比線上寬鬆 |
> | 有東西可點 | `api.js` 的 `apiGetOrLastKnown()`：線上照常並存一份，`navigator.onLine === false` 才回上次那一份 | 以使用者 id 分鍵；**線上被拒（401／403）絕不拿舊的頂替**；登出／401 一併清 |
> | 不代送 | `offline-queue.js` 每筆記 `userId`，flush 時與目前登入者不同就標 error 不送（同廠別比對） | 舊版項目沒有欄位＝放行 |
> | 徽章 | `mfg/list` 原本讀 `sessionStorage.offlineQueue`——**全站沒有程式寫它**，徽章從沒出現過。改讀 IndexedDB，flush 完由 `offlinequeue:flushed` 事件觸發重抓 | |
>
> 守門：`MobilePwaOfflineTests` 的冷啟動兩支（新分頁＝空 sessionStorage；舊程式上實測紅在
> `#userName` 為空）、jest `auth.test.js`／`api.test.js`／`offline-queue.test.js`。
> ⚠️ 冷啟動仍要**先在線上登入並開過一次**該頁——沒開過的清單離線時一樣沒有資料。

### 5.2 寫入操作的離線佇列

關鍵：作業員在現場掃條碼報工，**不能因為網路斷線就操作失敗**。（2026-10-12 起範圍收窄為「送出那幾秒剛好斷線」，見 §5.0。）

#### ⚠️ Critical Design Issue：Workbox 的 JWT 失效風險（Cookie 方案下不存在）

> **定案 Cookie 認證下本問題不存在**：Cookie 隨請求自動帶上、伺服器 sliding expiration 自動續期，重放離線請求不會因 token 過期被拒。
> 但**離線寫入仍不該交給 Service Worker 重放**——理由見下方對策（應用層佇列 + 主動同步），這部分 Cookie/JWT 皆適用、仍需實作。以下「JWT 過期」段落僅在升級為 JWT 時才相關。

**問題（僅 JWT 方案）**：純粹使用 Workbox Background Sync（Service Worker 層直接重放失敗請求）會遭遇 **JWT 過期的致命缺陷**：
- 離線期間，原本 Request Header 的 Access Token 已過期
- 網路恢復時，Workbox 重放該舊 Request → 後端 401 Unauthorized
- 無法觸發前端的 Token Refresh 機制（Service Worker 層無法攔截應用層邏輯）
- **結果**：累積的報工資料全數報廢

**解法**：改採 **應用層佇列** + **主動同步**

```
寫入流程（應用層佇列）：
  1. 操作員按「提交」
  2. 前端檢查網路
     a. 若線上 → 直接發 API，成功後結束
     b. 若離線 → 進入 Step 3
  3. 操作 Pinia store + IndexedDB：enqueue(endpoint, payload, clientId, clientTimestamp)
  4. UI 立即顯示「已送出（待同步）」
  5. 主程式監聽「online」事件 → 觸發同步流程（見 § 5.2.2）
  6. 同步時先檢查 JWT 是否過期，若過期則先 refresh
  7. 依序發送佇列中的請求
  8. 後端成功回應 → 從 queue 移除 + UI 更新
  9. 衝突或 4xx 錯誤 → 標記為「待處理」，提示作業員
```

#### 5.2.1 應用層佇列設計

```csharp
// 後端 API：離線同步的通用簽名
[HttpPost]
public async Task<IActionResult> ReportWork(ReportWorkRequest req)
{
    // 所有寫入 API 必須接受這三個欄位（去重 + 時序確認）
    var request = new
    {
        clientId = req.ClientId,                    // 前端操作的唯一 ID
        clientTimestamp = req.ClientTimestamp,      // 操作時間戳
        payload = req.Payload                       // 實際請求內容
    };
    
    // 去重邏輯：同一 clientId 第二次來直接回傳第一次結果
    var existing = await _context.SyncLog
        .FirstOrDefaultAsync(s => s.ClientId == req.ClientId);
    if (existing != null)
        return Ok(existing.Response);  // 幂等
    
    // 正式處理
    var result = await _service.ProcessReport(req.Payload);
    
    // 記錄此次同步
    await _context.SyncLog.AddAsync(new SyncLog
    {
        ClientId = req.ClientId,
        ClientTimestamp = req.ClientTimestamp,
        Response = result,
        ProcessedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();
    
    return Ok(result);
}
```

#### 5.2.2 前端同步流程

> ⚠️ **定案 Cookie 認證下無 Token Refresh 步驟**：Cookie 隨請求自動帶上、sliding expiration 自動續期，
> 網路恢復重送佇列**不需**先刷新 Token。以下為原生 JS（無 Vue/Pinia）版本；§13.3/§13.4 的 Pinia/JWT 版屬未採用方案。

**網路恢復觸發**（原生 JS）：
```javascript
// 主程式全域設定（無框架）
window.addEventListener('online', async () => {
  // Cookie 認證：直接依序同步佇列即可（無需刷新 Token）
  await offlineQueue.sync()   // OfflineQueueManager：IndexedDB 取待同步項，逐筆 fetch（Cookie 自動帶上）
})
```
> 若 Phase E3 升級為 JWT（長離線需求），才在 `sync()` 前加「檢查並刷新 Token」一步，並改用 §13.3/§13.4 的攔截器。

**衝突情境處理**：
- 派工被別人關閉 → 後端 400：「派工不在 InProgress 狀態」→ UI 提示「派工已結束」
- 容器被別人領走 → 後端 400：「容器已不存在」→ UI 提示「容器已轉移」
- 操作員有權限被移除 → 後端 403：UI 提示「您的權限已變更，請重新登入」

**裝置端資料的安全界線**（2026-09-29 安全盤點）：佇列與快取在手機上是**明文**，而且使用者改得動——
設計前提是「手機送來的一律不可信」，數量、檢驗判定、作業員、機台／模具全由伺服器重新檢查。
換人時：登入／登出清派工與機台快取、登出清草稿與已送出紀錄、首頁佇列只列本人的項目、
登出前先同步並在本人仍有未送出資料時確認、超過 24 小時未同步提醒。
**未送出的項目刻意保留**（刪掉＝資料遺失）。剩下的「明文可被偵錯工具讀取」屬裝置管理範疇，
給導入企業的建議見 [`deploy/mobile-device-security.md`](../../deploy/mobile-device-security.md)，由企業自行評估。

### 5.3 預先快取的主檔資料

PWA 第一次啟動時下載並存 IndexedDB：
- 自己有權限的派工清單（每 5 分鐘更新）
- 工作中心 / 機台清單
- 模具清單
- 產品主檔（精簡欄位）
- 倉庫 / 儲位清單
- 不良原因代碼

**大小估算**：以 1000 個產品 + 200 機台 + 500 派工 = 約 500 KB JSON，可接受。

> #### 實作現況（2026-08-21 逐檔查證，以程式為準）
>
> **上面這份清單是規劃，六類主檔目前只實作了其中一類半，且不是「第一次啟動時下載存 IndexedDB」，
> 而是 Service Worker 的執行期 Cache-First**（查過才有，沒查過就沒有）。
>
> | 規劃項 | 現況 | 依據 |
> |---|---|---|
> | 派工清單（5 分鐘更新）| ✅ 有 | `sw.js` `DISPATCH_CACHE`，5 分鐘 |
> | 工作中心 / 機台清單 | ⚠️ 只有機台，且只快取查過的 | `handleMasterCache`，30 分鐘，命中條件 `/api/mobile/machines*`；實際端點只有 `GET /api/mobile/machines/{machineCode}`（單筆），沒有「清單」 |
> | 模具清單 | ❌ 沒有 | 無對應端點 |
> | 產品主檔（精簡欄位）| ❌ 沒有 | 無對應端點 |
> | 倉庫 / 儲位清單 | ❌ 沒有 | 無對應端點 |
> | 不良原因代碼 | ❌ 沒有 | 端點 `GET /api/mobile/code-entries` 存在，但**不符合 `sw.js` 的兩個快取前綴**，走 Network-Only |
>
> ⚠️ **順帶查出的缺陷（未修）**：`sw.js` 的主檔快取分支比對
> `url.pathname.startsWith('/api/mobile/master')`，但**全專案沒有任何 `/api/mobile/master*` 端點**
> （`Api/Mobile/` 五支 Controller 皆無）。那一半的判斷式是死路由，永遠不會命中——
> 症狀是「看起來有主檔快取」，實際只有 `machines` 那一半在運作。
> 真要補主檔預快取時，這條要嘛接上端點、要嘛刪掉，別留著當假訊號。

---

## 6. 條碼掃描方案

### 6.1 技術選擇

| 方案 | 優點 | 缺點 |
|------|------|------|
| **BarcodeDetector API**（原生）| 性能好、無需 lib | iOS Safari 不支援、Android Chrome 87+ |
| **@zxing/browser** | 跨平台 | bundle 約 200KB |
| **ZBar.wasm** | 跨平台、識別率高 | bundle 大、初始化慢 |
| **混合（原生優先 + ZXing 後備）** | 最廣相容 | 程式碼較複雜 |

**建議：混合方案**：先試 BarcodeDetector，不支援時降級到 ZXing。

### 6.2 條碼類型支援
從舊系統看，需要支援：
- **Code 128**：派工單號 (NO_WW)
- **QR Code**：容器號 (CN_ID)、工單號
- **EAN-13**：產品條碼（外購品）

### 6.3 UX 設計

**主流：手機相機掃描**
- 點按鈕→開啟相機→自動辨識→自動提交，不需操作員按下確認
- 連續掃描模式：完工報工時可能要連掃 10 個容器

**備用：鍵盤模擬支援（外接掃描槍）**
- 若現場有 USB 掃描槍，可直接掃描 → 模擬鍵盤輸入 → 自動提交
- 實作：全域 `keydown` 監聽，掃描槍發送的字符串以 Enter 結尾後觸發業務邏輯
- 優先級：相機掃描 > 鍵盤模擬 > 手動輸入

**鍵盤模擬去抖判定（Keyboard Wedge Debouncer）**

掃描槍本質是「超高速鍵盤輸入 + Enter 結尾」，與人工打字易衝突。判定演算法：
- 全域 `window` 在 **capture 階段**監聽 `keydown`，維護字元緩衝與每鍵時間戳。
- 認定為掃描槍輸入需**全部成立**：字元長度 ≥ 最小位數（預設 5）、相鄰字元間隔具一致性且小於閾值、以 Enter 結束。
- 命中 → `preventDefault()` 阻止字元落入聚焦輸入框，將解析後條碼直接派發給當前模組解析器；
  未命中（間隔過大）→ 視為人工輸入、不攔截，字元正常進入 input。

> ⚠️ **實作注意**：
> - **閾值需可由系統參數設定，不可寫死 30ms**——藍牙 HID 掃描槍常落在 30–50ms，硬閾值會誤判。判定應更倚賴
>   「相鄰間隔的一致性 + 終止 Enter + 最小長度」，而非單一數字門檻。
> - **攔截時機**：須在 capture 階段攔截，否則 Enter 才判定時字元可能已進入聚焦的 input。穩健做法為全域捕獲緩衝、
>   或將掃描路由到一個常駐隱藏輸入框，避免「攔截落空」。

**手動輸入按鈕**
- 掃描失敗時的 fallback
- 允許操作員手打條碼號

---

## 7. 推薦的功能模組重組

舊系統 11 支程式中，有些可以合併、有些可以拆得更細：

### 7.1 MFG（生產執行）—— 屬於 mfg-domain.md 範圍
| 新功能 | 對應舊 | 說明 |
|--------|--------|------|
| **派工列表** | 新增 | 顯示我能執行的派工，可篩選機台 |
| **開工準備（Setup）** | 新增 | 掃 NO_WW → 回報備料/換模/調機完成 → 記錄 Setup 實際時間 |
| **開工** | aps | 掃 NO_WW（Setup 完成）→ 確認機台/模具 → 開工 |
| **報工** | apc | 掃 NO_WW（已開工）→ 報合格/不良 → 容器號 |
| **生產檢驗** | apq | 對 MSFC 做檢驗註記 |
| **完工繳庫** | api | 完工自動觸發 |
| **機台狀態異動** | agtr（新增）| 掃機台 QR → 選事件類型 → 確認 → 建立 MachineStatusOrder |

### 7.2 INV（倉儲執行）—— 屬於 INV 模組擴充
| 新功能 | 對應舊 | 說明 |
|--------|--------|------|
| **進料檢驗** | aiq | 掃進貨單條碼 → 抽樣 → 判定合格/不良 |
| **入庫上架** | aii | 掃容器/條碼 → 掃目的儲位 → 提交（含委外進貨、委外退料入庫）|
| **出庫下架** | aio | 掃 MaterialSlip 條碼 → 確認揀料位/批號 → 提交（廠內發料/委外送外料/重工發料統一入口）|
| **備料** | aip | 為派工備齊用料 |
| **盤點** | ail/ain/air | 三種合一，用「模式」切換 |

**出庫下架（aio）MaterialSlip 類型支援**：

| Category | 畫面標示 | 目的地顯示 |
|----------|---------|----------|
| InternalIssue | 廠內發料 | 生產線/工位 |
| InternalRequisition | 廠內領料 | 生產線（申請人確認）|
| OutsourceIssue | **委外發料**（送廠商）| 廠商名稱（TargetSupplierCode）|
| ReworkIssue（廠內）| 重工發料 | 重工區 |
| ReworkIssue（委外）| **委外重工發料** | 廠商名稱 |

> 所有類型共用同一套掃描 UI；`Category` 決定畫面標頭顏色與目的地顯示文字。委外相關單以橘色標示提醒作業員「此料出廠」。

**入庫上架（aii）新增觸發來源**：

| 觸發來源 | 說明 |
|---------|------|
| 委外進貨（OUTSOURCE_RECEIPT）| INV_SIMPLE_MODE=關閉時，GoodsReceipt(OI) 過帳後產生待上架清單 |
| OutsourceReturn（委外退料）| 廠商退回的料件需上架入正式倉位 |

#### 7.2.1 料件作業實作現況（2026-10-05）

上表 aio「掃 MaterialSlip 出庫下架」原規劃併在出庫下架；實作為獨立的**工單發退料**頁（出庫下架清單不含發退料單）。
四個畫面共用 `Api/Mobile/MobileMaterialController`（`/api/mobile/material/*`）與 `js/material-common.js`；
**帳務一律走共用服務**（`IMaterialSlipService`／`IOutsourceReceiptService`／`IMaterialStagingService`，桌機同一份），手機端不另寫過帳。

| 畫面 | 作業鍵 | 端點 | 離線 |
|------|--------|------|------|
| `inv/issue.html` 工單發退料 | Inv/MaterialIssue | `GET slips?kind=issue\|return`、`GET slips/{id}`（含各品號有庫存的儲位，已備料儲位排第一）、`POST slips/{id}/post`（揀料確認＋過帳一次完成） | 可排隊（clientId 去重） |
| `inv/staging.html` 備料作業 | Inv/Staging | `GET staging`、`GET staging/{id}`、`POST staging/{id}/confirm`、`POST staging/{id}/stage` | 確認（鎖庫存）只能連線；移入可排隊 |
| `inv/outsource-receive.html` 委外收料 | Inv/OutsourceReceive | `GET outsource-orders[?q]`、`GET outsource-orders/{id}`（可收量）、`POST outsource-receipts`（建單→送審→已核准即確認）、`GET outsource-receipts/approved`、`POST outsource-receipts/{id}/confirm` | **只能連線**（同時產生應付發票）；clientId 只防連按 |
| `mfg/requisition.html` 領料／退料（上工頁進入） | Mfg/Requisition | `GET dispatches/{id}/materials`、`POST dispatches/{id}/requisition`（MR 待核准）、`POST dispatches/{id}/return`（MX 草稿，不得超過已發未退） | 可排隊 |

- 手機可揀的發退料單：MR 以外的草稿、已核准、揀料中、已確認（`MaterialSlipService.CanPickOnMobile`）；未核准的 MR 不出現在清單也不能過帳。
- 手機只回報儲位時由伺服器依儲位補倉庫（發退料與委外收料皆然）。
- 委外收料單價取自委外採購單行 `UnitPrice`、幣別取該行（無則本幣）、匯率取收貨日買入匯率；手機不輸入金額。超收不擋（與桌機一致），畫面要求確認一次。
- 新作業鍵經 Program.cs「新作業鍵回補」自動授權給已有該模組任一作業的角色；要收斂請在手機權限畫面調整。

### 7.3 共用元件
- 登入 / 切換角色
- 個人首頁（待辦數量、近期紀錄）
- 條碼掃描器（全 App 共用）
- 同步狀態指示（網路、待同步筆數）

### 7.4 機台狀態異動（agtr）UX 流程

```
操作員手機操作：
  1. 掃描機台 QR Code → 顯示目前機台狀態（如：正常 Available）
  2. 選擇事件類型（依當前狀態只顯示合法轉換）：
       故障發生 → DI（BreakDown）
       修復完成 → DO / CI（Available）
       開始保養 → HI（Maintenance）
       保養完成 → HO（Available）
       設為待機 → II（Standby）
       外借出廠 → FO（Loaned）
       ……
  3. 輸入備註（選填）、來源單號（選填）
  4. 確認送出 → POST /api/mobile/machines/{id}/status-event
     副作用（後端）：
       a. Machine.CurrentStatus 更新
       b. 若狀態為不可用：現有 Scheduled 派工退回 Pending
       c. 若狀態為不可用：InProgress 派工自動 OnHold
       d. 畫面提示「此機台有 N 張派工受影響，已暫停排程」
```

**離線行為**：機台狀態事件可離線排隊，但網路恢復後**優先同步**（優先於報工），
避免機台已修復但系統仍顯示故障、排程無法指派的情形。

> 可選功能（系統參數）：機台狀態切換為不可用時，自動推送 Web Push 通知給班長。

---

## 8. 與 mfg-domain.md 的接口

mfg-domain.md §8 列了手機 API，本文件補充細節：

### 8.1 API 路由規約
```
/api/mobile/
├── auth/                        (登入、刷新 token、登出)
├── dispatches/                  (派工列表)
│   └── {id}/
│       ├── setup/start          (Setup 開始)
│       ├── setup/done           (Setup 完成)
│       └── priority             (調整優先序)
├── sessions/                    (上工記錄：開工、下工、查詢)
├── reports/                     (報工：合格/不良/容器)
├── machines/                    (機台查詢)
│   └── {id}/
│       └── status-event         (機台狀態切換事件 POST)
├── inventory/                   (上架、下架、盤點)
├── inspection/                  (進料、生產檢驗)
├── schedule/                    (甘特圖資料查詢)
└── master/                      (主檔快照下載：產品、儲位、機台等，含機台當前狀態)
```

### 8.2 統一回應格式
```typescript
type ApiResponse<T> = {
  success: boolean;
  data?: T;
  error?: {
    code: string;        // 例如 "DISPATCH_NOT_FOUND"
    message: string;     // 顯示給操作員的訊息（多語）
    field?: string;      // 若是欄位錯誤
  };
  syncId?: string;       // 用於離線同步去重
};
```

### 8.3 離線同步必要欄位
所有寫入 API 接受 `clientId` + `clientTimestamp`，後端做：
- 去重：同一個 clientId 第二次來，直接回傳第一次結果
- 發生時間（2026-10-12）：佇列送出另帶 header `X-Client-Sent-At`，MFG 的下工／報工／完工／機台狀態以
  「伺服器現在 −（送出 − clientTimestamp）」為業務時間，規則見 §5.0〈發生時間〉〈合理性檢查〉。
  ⚠️ 原本這裡寫「用 clientTimestamp 解決衝突」，實際上伺服器只拿它去重、一律記送達時間，從沒實作過。

---

## 9. 切片實作建議

對應 [mfg-domain.md § 11](./mfg-domain.md#11-下一步建議) 的 Phase E（手機 API + ETA 即時推算），擴展為前端 5 個 Phase。

### 前置條件確認（與後端對齐）

| 階段 | 後端準備 | 前端可啟動時機 |
|------|---------|--------------|
| Phase E1 | — | 立即啟動（技術底層獨立開發） |
| Phase E2 | 後端 `/api/mobile/*` 路由 + 鑑權模組完成 | Phase E1 完成後 |
| Phase E3 | 後端 `SyncLog` Entity + 所有寫入 API 支援 `clientId` 去重（§ 5.2.1） | Phase E2 完成後 |
| Phase E4 | INV 模組 API（與後端 Phase F 並行） | Phase E3 完成後 |
| Phase E5 | 後端 Web Push + ETA 推送 API | Phase E4 完成後 |

---

### Phase E1：PWA 骨架（2-3 週）

**技術棧**（評估選擇原生 JS，詳見附注）
- 框架：原生 JavaScript（無前端建置工具）
- PWA 基礎：manifest、Service Worker（Cache-First + network-only）
- 認證：Cookie（與桌機共用，無需 JWT refresh 邏輯）
- 條碼掃描：BarcodeDetection API（優先）+ Html5Qrcode（已有 wwwroot/pwa/ 範例）+ ZXing fallback
- **鍵盤模擬支援**（本次新增）：全域 `keydown` 監聽，掃描槍字符串自動觸發業務邏輯（§ 6.3）

**交付物**
- 擴展 `wwwroot/pwa/` 結構：新增 MFG 相關頁面（dispatches.html、sessions.html、reports.html）
- PWA manifest：更新或新建（MFG 手機功能專用）
- Service Worker（sw.js）：複製 wwwroot/pwa/sw.js，調整快取策略（派工列表 Cache-First）
- 條碼掃描模組：引用 Html5Qrcode（wwwroot/pwa/app.js 已有實例）
- 認證工作流：複製 wwwroot/mobile/js/auth.js 模式（Cookie + sessionStorage）

**驗收標準**
- `/Pwa` 路由可訪問 dispatches / sessions / reports 頁面（靜態 HTML）
- 登入後 Cookie 自動帶上，刷新頁面無需重新登入
- 條碼掃描（相機 + 鍵盤掃描槍）可識別 Code 128 / QR，觸發業務邏輯
- Service Worker 正常註冊，派工列表快取生效
- PWA install 提示可見（manifest 有效）

> **備註**：此 Phase 不引入 Vue/Vite 前端工程，與現有 ProjectZ 架構一致。Phase E3（離線佇列）複雜度確認後，
> 若手動 DOM 維護成本過高，可評估引入**超輕量宣告式庫**強化 UI 反應式綁定——**首選 Alpine.js**（script-tag 引入、
> 無 build step，符合「無 Vite」決策；勿選 Petite-Vue，其維護度較低）。屆時 `OfflineQueueManager`（離線佇列）
> 仍以原生 JS + IndexedDB 實作，Alpine 只負責把佇列長度/狀態綁定到畫面（如待同步紅點），零手動 DOM。
> **務必**將該庫本地化至 `wwwroot/pwa/js/libs/` 並加入 Service Worker 預快取，避免斷網首載失敗。

---

### Phase E2：投產與報工（3-4 週）

**交付物**
- 畫面：派工列表、Setup 回報、開工（投產）、報工（完工） 四支
- 機台狀態異動畫面：故障(DI) / 保養(HI) / 恢復(CI) 狀態切換回報（§ 13，與投產共用機台 QR 掃描入口）
- API 呼叫：串接後端 `/api/mobile/dispatches/*`、`/api/mobile/sessions/*`、`/api/mobile/reports/*`、`/api/mobile/machines/{id}/status-event`
- 狀態同步：派工列表實時刷新，顯示目前進度

**驗收標準**
- 派工列表可載入、可掃機台 QR 進入派工詳情
- 開工 → 報工 → 完工流程可操作，後端派工狀態正確轉換（Scheduled → InProgress → Closed）
- 機台狀態異動後，派工受影響正確（DI 時 Scheduled 派工 → Pending；InProgress 派工 → OnHold）
- 重複掃描同派工拒絕重複開工（clientId 去重 — § 12.2）
- 超量報工拒絕（後端回 400）
- 線上模式測試通過；機台狀態事件體積小，離線影響低

---

### Phase E3：離線能力（3-4 週）

> 📍 2026-10-12：本 Phase 的「離線瀏覽／Cache-First 派工」已撤回，現行範圍見 §5.0（斷線保護）。下文保留為原始規劃。

**交付物**
- **前端**（Cookie 認證版）：
  - 離線佇列：IndexedDB 記錄待同步操作（操作人/時間戳/endpoint/payload）
  - 本地快取：4 小時內派工資料（Cache-First Strategy）
  - 連線恢復邏輯：`online` 事件觸發 → 依序重送佇列（Cookie 自動帶上，無需主動刷新）
  - 衝突處理：佇列重試邏輯、展示本地失敗操作、clientId 去重避免重複
  - **或可升級為 JWT**（Phase E3 中期評估）：若離線期 > 1 小時，導入 JWT + RefreshToken，後端 `SyncLog` 做去重

- **後端（共用，與認證方式無關）**（mfg-domain.md § 5.2.1-5.2.2）：

- **後端**（mfg-domain.md § 5.2.1-5.2.2）：
  - Entity：`SyncLog`（記錄 clientId + clientTimestamp，用於去重）
  - 所有寫入 API (`POST /api/mobile/sessions`, `/api/mobile/reports` 等) 新增參數：`clientId`、`clientTimestamp`
  - 幂等性：同一 clientId 第二次 POST 回傳第一次結果，DB 不重複寫入
  - HTTP 響應：2xx 成功、409 衝突（佇列項目已存在）、401 認證失效（需重新登入）

- **Service Worker 角色調整**（相對於 § 13.5 的廢棄 Background Sync）：
  - ❌ **不處理寫入**：報工、機台狀態、上工等寫入 API 全部由應用層佇列處理
  - ✅ **僅處理讀取快取**：派工列表、產品/工作中心等主檔 (CacheFirst / StaleWhileRevalidate)

**驗收標準**
- 離線狀態下可執行開工、報工、機台狀態異動等操作，Pinia Store 記錄待同步項目
- IndexedDB 內有 `offlineQueue` 表，操作記錄持久化
- 斷網後執行操作 → 恢復網路 → Pinia `online` 事件觸發 → 自動刷新 JWT → 依序送佇列
- 同一 clientId 的重複操作回傳 409 或第一次結果，DB 無重複
- UI 顯示佇列狀態（待同步 / 已同步 / 同步失敗），失敗項可重試

---

### Phase E4：倉儲作業移植（3-4 週）

**交付物**
- 畫面：上架/下架/盤點/進料檢驗 四支
- 後端 INV 模組 API（由後端 Phase F 負責，與前端並行開發）
- 離線同步擴展（Phase E3 + Phase E4 的倉儲 API）

> **實作現況（2026-08-21）**：`wwwroot/mobile/inv/` 實際有 **6 支頁面**——
> 上列四支（`putaway` / `pick` / `stockcount` / `iqc`）之外，另有 `stock.html`（庫存查詢）
> 與 `list.html`（模組目錄）。**「四支」是當時的規劃數，不是現況數。**
> 另：「離線同步擴展」上架／下架與**盤點**都已接上離線佇列（盤點 2026-09-25 補，見 §5.1〈實作現況〉；2026-10-08 更正原文「盤點沒有離線佇列」）。

**驗收標準**
- 倉儲各畫面可載入、操作；離線佇列包含倉儲寫入操作
- 與 INV 模組整合測試通過

---

### Phase E5：ETA 推送與優化（2-3 週）

**交付物**
- Web Push API 整合：Service Worker 接收推播
- ETA 推送邏輯：後端 § 7.4 計算 ETA 後推送「下一張派工」通知
- 效能優化：Code splitting、Lazy load 組件、資源預載

**驗收標準**
- 推播訊息送出後手機顯示通知、點擊進入派工詳情
- 首頁載入 < 2 秒、各畫面轉換平滑
- Lighthouse 指標 ≥ 90

---

**總估算**：13-18 週（Phase E1-E3 為核心，E4-E5 依後端進度調整）

---

## 10. 已確認規格與決策

> 原「待確認問題」已由 Fisher 審核，以下為定案規格，可直接作為實作依據。

### 設備與環境

**1. 目標裝置**
- Android 手機為主要執行裝置（投產、報工、盤點等所有現場作業）
- iOS 主要作查詢用途（派工列表、進度查詢）
- 平板需求少，不列為優先支援
- 建議最低規格：Android 8.0+ / Chrome 87+；iOS 16.4+（支援 Web Push）

**2. 網路環境**
WiFi 或 4G/5G 皆可能，需支援兩者。離線策略見 §5。

**3. 條碼類型**
主要支援 Code 128、QR Code、EAN-13。
Data Matrix 可能但少見（主要在電子業序號追蹤）；ZXing 已內建支援，混合方案下不需額外成本。

**4. 裝置共用**
裝置一般為個人專屬，但需支援帳號切換（登出→重新登入）。
登出時清除 IndexedDB 內的 Refresh Token，下一位使用者需重新驗證身分。

### 業務行為

**5. 離線有效期**
一般為短暫網路中斷，非系統性長時離線需求。
規格：本地快取保留**最近 4 小時**的派工資料；IndexedDB 寫入佇列不限時間，網路恢復後全數同步。

> **已知限制 — iOS ITP 7 天清除**：iOS/Safari WebKit 隱私政策會在使用者連續 7 天未開啟該 PWA 後，清除其 Cookie
> 與 script 可寫儲存（IndexedDB 亦可能在儲存壓力下被回收）。本系統以 Android 為寫入主力、iOS 僅查詢為輔（§10.1），
> 衝擊已部分緩衝；仍列為已知限制：iOS 上長期未開啟者重開需重新登入，**且不應假設 iOS 端離線佇列能跨越 7 天無使用仍存活**。
>
> **長時離線（>1 小時）暫不擴張範圍**：已評估「30 天雙軌離線 Token + 設備指紋」方案，因與本條「短暫中斷」定案範圍
> 衝突、且增加安全面，**暫緩**。離線同步遇 Cookie 過期（401）時，佇列**持久不丟**、提示重登後再 flush（§5.2.2）。
> 若長離線日後成為真實需求，走 §4.1 / §11.4 既有的 **JWT + RefreshToken 升級路徑**，不另建並行 Token 機制。

**6. 多人同步衝突**
派工拆分後，多人可同時對同一工單的不同機台報工，各自記錄不衝突。
未拆分（同一 Dispatch）時，後端依累計數量驗證，防止總量超過 `QtyTotal`；如實記錄，不強制排斥。

**7. 錯誤更正**
手機端**不提供「修改既有紀錄」的功能**。所有錯誤更正在桌機 Web 畫面操作，並留稽核記錄（原始值、修改後、修改人、時間）。

> ⚠️ **這一條講的是「改」，不是「寫」。** 手機端本來就會新增資料——開工、報工、機台狀態異動、
> 上架、下架、盤點都是手機發起的寫入（§1.2 功能盤點、§7.1／§7.2）。
> 本條限制的是「送出後回頭改自己剛才那筆」，那必須回桌機做。
> 過去這句話被讀成「行動裝置只能檢視資料」，與同一份文件的開工／報工段落直接衝突。

**8. 加班/換班報工**
每班次獨立一段 `ProductionSession`（開工→下工），不跨班合併。
下一班次開工時建立新的 ProductionSession，各自記錄起訖時間與產出數量。

### 整合

**9. 權限模型**
手機端採**獨立「手機模組權限」**，與桌機畫面權限分開設定。
手機各功能（開工、報工、盤點等）各自有獨立開關，主管可精細控制作業員能使用哪些功能按鈕。

**10. 多廠別**
- 登入後依使用者組織設定顯示**預設廠別**
- 可手動切換廠別（切換後重新載入對應廠別資料）
- 跨廠調撥（如廠間轉倉）：使用者切換至對應廠別操作各自的上/下架
- 可使用的功能由各廠別的手機模組權限控制

**11. 周邊設備整合（印表機/秤）**
現階段不納入。未來可評估 Web Bluetooth（標籤印表機）、Web Serial（條碼秤）的可行性，視現場需求決定是否擴充。

### 推播

**12. 派工推播**
支援兩種模式（系統參數可設定）：
- **人工派工**：班長在手機或桌機選擇派工，指派給特定作業員，觸發 Web Push 通知
- **系統自動派工**：排程引擎跑完後，依作業員綁定機台自動推送「下一張派工」通知

**13. iOS 推播阻擋點**
iOS 16.4+ 已支援 Web Push，不構成阻擋。可在裝置規格要求中明訂 iOS 16.4+ 作為最低版本。

**14. 待簽核推播的落點（2026-09-08 修）**
🚨 `PushApprovalNotifier` 原本推的網址是 **`/Approval/PendingIndex`（桌機路由）**。
Web Push 的訂閱**只在 `wwwroot/mobile/home.page.js` 建立**（全站沒有第二個
`pushManager.subscribe`），所以每一則推播的收件端都是手機 PWA——而 `sw.js` 的
`notificationclick` 會對現有的 `/mobile/` 視窗做 `client.navigate(url)`，
等於把 PWA 從手機殼帶到桌機頁；standalone 模式沒有網址列，使用者只剩 OS 手勢能回來。
已改指 `/mobile/approval/list.html`。**新增任何推播時，url 一律要是手機路由。**

---

## 10.5 待我簽核 — 2026-09-08 交付（唯讀）／2026-09-09 開放簽核

`GET /api/mobile/approval/pending`、`GET /api/mobile/approval/count`
（`Api/Mobile/MobileApprovalController`）＋ `/mobile/approval/list.html`。

**判定語意完全沿用 `IApprovalService.GetMyPendingRequestsAsync`**，與桌機 `PendingIndex`
和 `ProcessApproval` 是同一份規則。手機自己再寫一份「哪些算待我簽核」的條件，
遲早會與「能不能真的簽」不一致。

### 三個刻意的決定

**① ~~唯讀，沒有核准／駁回。~~ 2026-09-09：可以簽了，但只限有摘要的單別。**

原本的理由是：`ApprovalRequest` 上只有單號、型別、申請人、時間、步驟——**沒有金額、
沒有品項、沒有摘要**；桌機簽核人的判斷資訊完全來自點單號跳去各模組的 Details 頁，
而手機端一個 Details 頁都沒有，所以放核准鈕等於做出一個**看不到內容卻按得下去**的按鈕。

**那個理由沒有變，變的是缺的東西補上了**：`IApprovalSummaryProvider`
（`Modules/_Shared/IApprovalSummaryProvider.cs`），一個 `DocumentType` 一份摘要，
註冊方式與 `IApprovalSyncContributor` 相同（各模組自己的 `IAppModule.RegisterServices`）。

於是不變式從「不准有寫入」變成 **「只有看得到內容的單別才簽得了」**，而它是**三層**：

| 層 | 行為 |
|---|---|
| `GET pending` | `canDecideOnMobile = false` → 卡片不給核准鈕，並**明講**為什麼 |
| `GET {id}/summary` | `supported = false` → 面板顯示「請用電腦簽核」，核准區整個隱藏 |
| `POST {id}/decide` | 直接回 `NOT_SUPPORTED_ON_MOBILE`（400） |

三層都要有：前端一有 bug 就會出現「看不到內容卻簽下去」，那正是整個設計要防的事。

**已實作的單別**（2026-09-09 第二批後共 **14** 個，待辦歸零）：

| 模組 | 單別 | 金額來源 |
|---|---|---|
| PUR | `PurchaseOrder`／`GoodsReceipt`／`GoodsReturn` | 表頭 `TotalAmount`（已是本幣） |
| PUR | `PurchaseRequest` | **無**（回 `null`，不是 0） |
| ORD | `SalesOrder`／`GoodsShipment`／`SalesReturn` | 表頭 `TotalAmount`（已是本幣） |
| ORD | `InternalOrder` | **無**（回 `null`，不是 0） |
| MFG | `WorkOrder` | 標準料＋工＋費 |
| PUR | `OutsourceReceipt` | 表頭 `TotalAmount`（已是本幣） |
| PUR | `VendorEvaluation` | **無**（內容是分數，不是金額） |
| QCR | `NonConformanceReport` | **無**（財務後果在下游單據上） |
| FIN | `APInvoice`／`ARInvoice` | 表頭 **`AmountLocal`**（⚠️ 不是 `TotalAmount`） |

⚠️ **工單的金額刻意用「標準料＋工＋費」**，因為 `WorkOrderReleaseService` 拿去和
`mfg_wo_approval_threshold` 比的正是 `mat + lab + ovh`（§18.2 快速通道）。
顯示別的數字（只顯示料成本、或顯示實際成本）會讓簽核人看到的金額
**與「這張單為什麼會出現在他清單裡」對不起來**，而那種不一致沒有任何跡象可循。
用的是實體上已落地的 `Standard*Cost` 快照而非重算——費率會變，重算會讓
簽核人明天看到的金額與昨天送審時不同。

⚠️ **PUR 三張與 ORD 三張各自共用一個基底**（欄位結構相同，差別只在數量欄位的名字：
`ReceiptQty`／`ReturnQty`／`OrderQty`／`ShipQty`）。抄成六份的下場是其中一份日後被改、
其他沒有，而簽核人看到的差異不會有人發現——那幾張單本來就長得不一樣。
守門：`ApprovalSummaryProviderTests.出貨退回進貨的數量取的是各自的欄位` 逐張驗一次。

⚠️ **在地化一律用 `ProjectZSharedResource`**，不是 Core 的 `SharedResource`——
兩個模組的 Controller 都注入前者，而 `CustomerCode` 這個鍵只存在於前者。
用錯來源不會編譯失敗也不會擲例外：`IStringLocalizer` 查不到鍵時**回傳鍵名本身**，
於是簽核人會在手機上看到一排 `CustomerCode` 這種英文代碼。
（同批把 `DepartmentCode`／`RequestedBy`／`DeliveryAddress` 補進 ProjectZ 那份，
值與 Core 相同。ProjectZSharedResource 是本專案自有檔，改它不製造 Core 漂移。）

🚨 **發票的金額語意與上面六張<u>相反</u>，這是最容易犯也最看不出來的錯。**
採購單／訂單／進貨／出貨的表頭 `TotalAmount` 註解是「**本位幣**總金額」；
而發票的 `TotalAmount` 是「含稅總額（**交易幣**）」，`AmountLocal` 才是本幣。
照抄那組基底的話，一張 USD 1,200 的發票會顯示成「TWD 1,200」——數字小一個數量級以上，
畫面上完全看不出來，簽核人只會覺得「這張怎麼這麼便宜」。
所以發票**刻意不共用**那組基底，另立 `InvoiceApprovalSummaryBase`。
明細也是交易幣（`APInvoiceLine` 沒有 `AmountLocal` 對應欄），乘上表頭 `ExchangeRate`
才與表頭同一個口徑；外幣發票另外把交易幣金額列進 `Rows`——簽核人記得的是
「USD 1,200」，只給換算後的本幣會讓他對不上手上的單據。
守門：`外幣發票的金額取本幣欄位_不是交易幣的TotalAmount`，突變驗證過。

⚠️ **分數與數量不得塞進 `AmountLocal`。** `VendorEvaluation` 的 `TotalScore`（78 分）
放進去會被畫面格式化成「TWD 78」——78 分不是 78 元。NCR 同理：它的財務後果發生在
下游的報廢單／重工工單／採購退貨上，那些單各自另有簽核，在這裡湊一個金額
等於把下游的決定提前塞進這一張。

**剩餘 13 個全部是刻意不做，待辦歸零**：主檔異動 3（`Company`／`Product`／`Tax`——
摘要能提供的資訊不比桌機清單多）＋倉庫作業類 10（`ScrapDoc`／`StockAdjustment`／
`CrossPlantTransfer` 等——判斷依據是實物與儲位，摘要給不出關鍵資訊）。
這 13 個由
`ApprovalSummaryCoverageTests.NotYetImplemented` 逐一列出並**附理由**——
新增送簽單別時測試會紅，逼你二選一：實作，或明寫先不做及為什麼。
另有三支反面守門：清單不留已做完的、不含不存在的單別、provider 的 DocumentType 打錯字會被抓到。

⚠️ **只開放核准與駁回，不含退回修改。** `ReturnedForRevision` 的用途是「請申請人改某個
地方再送」，而那個「某個地方」必須寫在意見裡才有意義——手機上打長文的成本高到會讓人
只打「請修正」，那對申請人沒有任何資訊。退回修改留在桌機。

⚠️ **摘要的授權用「待我簽核」那份清單，不可以直接 `FindAsync`。** 桌機的 `Process` GET
正是踩過這個洞（原本只有 `[Authorize]`，任何登入者拿 requestId 就讀得到別人的單）。
手機摘要回的是**金額與品項明細**，破口代價比桌機那次更大。
守門：`MobileApprovalInboxTests.摘要不得讓人讀到不是指給自己簽的單`（含「連單號都不得出現在
回應裡」的斷言——回 404 卻夾帶 documentId 一樣足以枚舉出有哪些單存在），
已用突變驗證（改成直接查 → 紅）。

守門：`MobileApprovalInboxTests.沒有摘要的單別在手機上簽不了`
（取代原本釘住「缺席」的那一支；除狀態碼外**另外斷言真的沒寫入**——
「回 400 但照樣簽掉」是最壞的一種失敗），同樣突變驗證過。

**② 不掛手機模組閘門**（`MobileModules`／`MobileModuleActions`），首頁入口也**不放進
moduleGrid**。桌機 `ApprovalController` 只有 `[Authorize]`，看得到什麼由「這張單有沒有指到我」
決定；改用手機模組授權會做出「桌機簽得了、手機看不到」，而 `RequireMobileModuleAsync`
的 403 症狀是**清單空白**，與「真的沒有待簽核」分不出來（§26 FPI 有 Web／Mobile 漂移前科）。
R24 的豁免理由寫在 `[PermissionGateExempt]` 裡。
⚠️ 另一個實務理由：純辦公室的簽核者常常**一個手機模組都沒有**，
而 `home.page.js` 的「沒有模組就 return」會把後面的程式碼整段跳過——
所以 badge 的載入刻意排在模組載入**之前**。

**③ 不進 Service Worker 預快取，且不得進離線佇列。** 簽核是絕對不能離線做的事：
排隊期間單據可能被撤回、被別人簽掉、步驟已推進，而 `ProcessApproval` 對這些一律
`return false`——使用者會在幾小時後收到一則「簽核失敗」而完全不知道發生什麼事。
不預快取就會落到 `handleNavigate` 的離線替代頁，那正是要的行為。

⚠️ **開放簽核之後這條更重要，而且多了一道**：`offline-queue.js` 的 `enqueue`
明文排除 `/api/mobile/approval/`，而且是**擲出例外**不是靜靜略過——
靜靜略過的話呼叫端會以為排進去了、使用者也會以為簽好了，那是最壞的一種失敗。
比對用**前綴**不是完整路徑（簽核 URL 帶 requestId，完整比對永遠不會成立，
那種「守門看起來有寫、實際上一次都沒生效」最難發現）。
畫面另外在 `navigator.onLine` 為 false 時當場擋下並說明，因為那個例外對使用者是看不見的。
守門：jest `簽核絕對不進佇列` 三支（含「其他路徑照常入列」的反面對照），突變驗證過。

（`CACHE_NAME` 已 bump 到 `mes-mobile-v14`。）

### 已知界線

- 單別顯示的是**原始英文型別名**（`PurchaseRequest`…），與桌機一致——
  全站沒有任何單別譯名字典，只在手機加一份會變成「電腦叫 PurchaseRequest、手機叫請購單」。
- 單據連結指向**桌機**頁，且以 `target="_blank"` 另開分頁（同 ① 的理由）。
- 沒有登錄連結的單別會**明講**「這個單別尚未登錄單據連結」，不是讓點擊靜靜失效——
  桌機原本就是後者（`if (!urlFn) return;`），那個症狀看起來像畫面壞了。

---

## 10.6 廠別情境 — 2026-09-08 交付（第一、二階段）

> ⚠️ 本節編號刻意跳過 §10.5（那是「待我簽核」，在 PR #138）。兩支分支各自從 main 長出來，
> 合併後 §10.5＝簽核、§10.6＝廠別，順序才是對的。

### 起點：不是「不能切」，是「不知道自己在哪」

`ActivePlantId` 是**使用者資料列上**的欄位，桌機與手機共用同一個值。而手機端在此之前
對 plant 的字串比對是 **0 筆**——畫面上一個字都沒有。更麻煩的是 `auth.js` 明明把
`plantId` 存進 `sessionStorage`，卻沒有任何一處讀它，長期是**死資料**：
看起來手機知道廠別，其實它只是跟著使用者上次在**電腦上**切到的廠走，而現場人員可能根本沒有電腦。

### 第一階段（已交付）：顯示

- `GET /api/mobile/plants/current` → `{ plantId, name, nameEn }`，新檔 `Api/Mobile/MobilePlantController.cs`。
- 首頁導覽列下方新增廠別列；中英名稱由伺服端一起給，前端依 `getLang()` 取用（CodeEntry 慣例）。
- 代碼一律顯示（`名稱 (代碼)`）：現場的口語是廠別代碼，名稱反而不一定人人記得。

三個刻意的決定：

1. **不加在 `/api/mobile/auth/me` 上。** 那支在 Core 且不在 `core-drift-baseline.txt`，
   為一個欄位開 Core PR 不划算；新檔沒有漂移，而且切換階段會落在同一個路由前綴下。
2. **不掛手機模組閘門。** 廠別是每一頁的前提，而 `RequireMobileModuleAsync` 的 403
   在畫面上與「沒有廠別」長得一樣——沒有手機模組權限的人（例如只來簽核的主管）
   會連自己在哪個廠都看不到，而那正是最需要知道的一群。
3. **前端不寫回 `sessionStorage.plantId`。** `requireAuth()` 每頁進來都已經從
   `/api/mobile/auth/me` 寫過一次；同一個欄位兩個寫入者就是漂移的種子。

⚠️ **`IUserContext.AllowedPlantIds` 不可用在這裡**：它的空清單有兩個意思——
角色不限廠（＝**全部**）以及使用者根本沒載入。呼叫端分不出來，直覺實作會讓
權限最大的人看到空的廠別清單。切換階段要列可切廠別時，這個坑會再出現一次。

### 第二階段（已交付）：先補掉切換會踩爆的洞

**離線佇列不記廠別**（記錄只有 `url`/`method`/`body`），廠別是 flush 當下由伺服端從
`ActivePlantId` 決定的。四個入列點的行為並不一致：

| 入列點 | 廠別怎麼決定 | 切廠後 flush |
|---|---|---|
| 報工／完工 | `PlantId = dispatch.PlantId`（跟父單），派工查詢帶 `d.PlantId` | 查不到派工 → 失敗 |
| 揀貨／上架 | 來源單是 `IPlantEntity`，靠全域過濾 | 查不到來源單 → 失敗 |
| **機台狀態** | ~~`_userContext.PlantId`~~ | ~~**成功寫進錯的廠**~~ ← 本次修正 |

🚨 **根因**：`WorkCenterMachine` **沒有 `PlantId` 欄位、也不是 `IPlantEntity`**
（主鍵是 `WorkCenterCode`+`MachineCode`），所以 `ApplicationDbContext` 的**全域廠別過濾攔不到它**
——廠別掛在它的 `WorkCenter` 上。查詢少一個條件，別廠機台碼照樣查得到，接著
`machine.MachineStatus = targetStatus` 會改掉**別廠的機台主檔**，並用當下情境的廠別開一張異動單。
兩件都成功、都不報錯。

**同一個缺陷有四個現場**，本次一併修正（都加 `m.WorkCenter!.PlantId == …`）：

| 位置 | 廠別來源 |
|---|---|
| `MobileMfgController.MachineStatusEvent`（寫入） | `_userContext.PlantId` |
| `MobileMfgController.GetMachine`（讀取） | 同上——讀寫條件必須一致 |
| `MachineStatusOrderController.Save`（**桌機**寫入） | `UserContext.PlantId` |
| `ToolingShotService`（保養自動開單） | `dispatch.PlantId`（跟單走，不跟呼叫者走） |

> 桌機的機台**清單**本來就有這個條件，只有**寫入**點漏掉——於是下拉選不到的機台，
> 直接送代碼還是改得動。

**離線佇列**：入列時記下當下廠別，flush 前比對，不符就標成 error 並顯示
「這筆是 {0} 廠排入的，目前在 {1} 廠」，**不送出去賭伺服端會擋**（擋得住的那幾條回 404，
現場看到的是「同步壞了」而不是「你切了廠」）。用 `continue` 不是 `break`：
一筆跨廠不該讓排在後面的同廠項目一起卡死。

⚠️ **兩邊有任一個是 null 就放行**，這是刻意的：null 代表這筆是本次改動之前排入的
（舊版沒有這個欄位），或當下讀不到廠別。一律擋掉會讓升級後手機裡既有的待同步資料
**全部變成失敗**，比原本的問題更糟。

### 第三、四階段（已交付，2026-09-08 同日）

**第四階段先做**，因為它與服務抽取是同一支檔案的同一件事，分開做要動兩次 Core：
Core PR #24 新增 `IPlantSwitchService`（`GetSwitchablePlantsAsync` / `TrySwitchAsync`），
`UserSettingsController` 的 `Index()` 與 `SwitchPlant` 兩處都改接它——規則從**兩份**變成一份。

⚠️ **抽取的主要價值不是去重，是這條規則第一次有測試。** 同一支控制器的「語言」切換
有 `UserSettingsLanguageTests` 五支，「廠別」切換在 Core 45 個測試檔與本專案三個測試專案裡
**一支都沒有**——而它決定的是使用者能看到哪個廠的資料。Core PR 補了 15 支。

第三階段（ProjectZ）：`GET /api/mobile/plants`、`POST /api/mobile/plants/switch`，
首頁廠別列加「切換」鈕，開底部面板選廠。

#### 四個關鍵決定

1. **權限閘門用 `Forbid()`，不是 MenuKey，也不是豁免。**
   `UserSettings` 被 `PermissionController` 明確排除在權限矩陣之外（**管理員改不了**），
   而種子給非管理員的是 `CanRead=true` / `CanUpdate=false`——掛 `CanRead` 是恆真的裝飾品，
   掛 `CanUpdate` 會把現場人員（OP／WH）全部擋掉**而且沒有人打得開**。
   真正的授權是 `RolePlantAssignment`，由服務強制執行，不通過就 `Forbid()`。
   R24 本來就接受 `Forbid(` 當閘門，所以這是**真閘門**而非豁免。
   （已驗證：把 `Forbid()` 換成 `StatusCode(403)`，R24 立刻紅。）

2. **切換後刪掉「全部」快取，不是點名那兩個。**
   SW 對 `/api/mobile/dispatches`（5 分鐘）與 `/api/mobile/machines`（30 分鐘）是 Cache-First，
   快取鍵只有 URL、URL 不含廠別——不清會**繼續顯示舊廠的派工與機台**最久 30 分鐘，
   而且畫面完全正常。點名兩個等於把 `sw.js` 的快取名稱抄第二份，日後有人加第三個
   廠別敏感的快取時不會有人記得補。代價只是重新下載一次殼層，而這一刻必然連線
   （切換本身就是 POST）。

3. **切換前先擋離線佇列，而且擋在開面板之前。** 讓人選完廠再說「不行」是最糟的順序。
   只看 `pending`：`error` 的項目已經停住，切廠不會讓它更糟。

4. **`Forbid()` 在 `/api` 路徑下是真的 403。** cookie 認證預設會把 forbid 變成 302 導向
   AccessDenied 頁，`api.js` 會拿到 200 的 HTML 然後 JSON 解析失敗、顯示成「網路錯誤」——
   但 `Program.cs` 的 `OnRedirectToAccessDenied` 已經對 `/api` 做過這件事，這個坑不存在。
   （查證過才敢用。）

#### 🚨 `Program.cs` 是 `merge=ours`，Core 的 DI 註冊不會自己過來

`.gitattributes` 把 `Program.cs` 設為 `merge=ours`（ProjectZ 有 Cookie 強化／推播註冊／
Mobile 模組種子等客製），所以合併 Core 時 **Core 那一行 `AddScoped<IPlantSwitchService, …>()`
會被整個丟掉，不留衝突、也不警告**。ProjectZ 這側必須自己寫一行。

漏掉的症狀特別惡劣：**建置成功、全部測試綠**，但「使用者設定」頁一開就 500
（`UserSettingsController` 建構不出來）。查證過**沒有任何測試打得到那一頁**，
也沒有「所有控制器都能從 DI 解析」這類守門。這一類（Core 註冊被 merge=ours 吃掉）
是靜默的，日後 Core 每新增一個服務都要記得在這裡補。

#### 🚨 切廠會影響**另一台裝置上開著的畫面**（2026-09-09 補守門）

`ApplyGlobalValues` 是在 `SaveChanges` **當下**才填 `PlantId ??= CurrentPlantId`，
取的是**送出當下**的廠別，不是**渲染當下**的。所以這條路徑成立：

> 電腦上開著 A 廠的新增表單 → 在手機把廠別切到 B → 回電腦按存檔
> → **單據被建到 B 廠，畫面上完全沒有任何提示**

修改／刪除反而是安全的（全域查詢過濾讓它查不到而回 404，難看但不壞資料），
**真正會靜默壞掉的是新增**。手機端可以切廠之後，這條路徑從「要開兩個桌機分頁才碰得到」
變成「同一個人兩隻手就碰得到」。

**守門**：`Filters/StalePlantContextFilter`
- `_CrudPanelScripts.cshtml` 把渲染當下的廠別寫進 DOM（`#__PlantContext`）
- `crud-panel.js` 的 `submitPanel` 以 `X-Plant-Context` 標頭送出
- Filter 在寫入類方法比對，不一致就回 `{ success:false, message }`
  （與 `DbUniqueViolationExceptionFilter` 同一個契約：面板**不關閉**，使用者填的資料還在）

⚠️ **覆蓋範圍是誠實有限的**：標頭只有 `submitPanel` 會送（68 個 Index 畫面的側欄面板，
也就是單據新增／編輯的主要入口）。**沒有標頭就一律放行**——一般 form post 目前沒有接上，
全部擋掉會把大量正常作業變成錯誤。要擴大覆蓋，就是讓更多送出點帶上這個標頭。

⚠️ 同一個瀏覽器的**兩個分頁**也擋得到（標頭來自各自的渲染），
但**離線佇列**是另一套機制（見第二階段），兩者不共用判定。

#### 沒有做的

- 桌機 `SwitchPlant` 在授權不通過時仍然是「安靜地不切換、導回首頁」。那不是好行為，
  但改它會動到桌機既有互動，不在本次範圍。手機端在同一條規則上回 403——
  **規則同一份，差別只在呈現**。
- 手機端沒有做「切換角色」。角色會連帶改變 `ActivePlantId`（`SwitchRole` 裡有那段邏輯），
  是另一個題目。

> 模組清單只綁 `ActiveRoleId` 不綁廠別，所以切廠**不需要**重載首頁模組格——這省掉一整塊工作。

---

## 11. 我的建議

### 11.1 整體判斷
舊手機程式雖簡單，但**功能定義清晰、流程實戰過**。新 PWA 不需大改業務邏輯，重點是**換掉技術底子**並加強離線能力。

### 11.2 為什麼建議在 ORD 後做、與 MFG 並行
- 若先做 PWA，會少了 MFG 的派工/工單資料來源
- 若先做 MFG 桌機版再做 PWA，PWA 可立即連線使用，不必準備假資料
- INV 模組的手機作業可在 MFG/PWA 之後做

### 11.3 短期可立刻做的兩件事
1. **擴展 wwwroot/pwa/ 結構**（見 § 9 Phase E1 最終決策）：在現有 `wwwroot/pwa/` 基礎上新增 MFG 相關頁面（dispatches.html、sessions.html 等），複用 Service Worker 和 Html5Qrcode 掃描邏輯，無需 Vite 或 Vue 工程
2. **規格化 API 契約**：把 mfg-domain.md §8 + 本文 §8 的 API 寫成 OpenAPI / Swagger，讓 PWA 與 MES 後端可並行開發

### 11.4 已確認重點架構決策摘要
- **裝置**：Android 手機主力，iOS 查詢為輔，平板不優先
- **前端框架**：原生 JavaScript（無 Vite/Vue），與現有 ProjectZ 架構一致，見 § 9 Phase E1
- **部署**：前端輸出至 `wwwroot/pwa/` 或 `wwwroot/mobile/`，共用同一 ASP.NET Core 主機，Cookie 認證（同桌機）
- **認證升級**：Phase E3（離線佇列）若需長時間離線（> 1 小時），可評估升級為 JWT + RefreshToken，但 Phase E1-E2 無需先行
- **離線**：本地快取 4 小時，寫入佇列無限期等待同步（Cookie sliding expiration 自動處理短期離線）
- **權限**：手機模組獨立權限，與桌機分離設定
- **推播**：支援人工派工與系統自動派工兩種模式（參數切換）
- **修改**：手機端**不能修改既有紀錄**（新增寫入不受限——開工／報工／上下架／盤點都在手機做），
  錯誤更正回桌機處理並留稽核記錄。⚠️ 本行原寫「手機端唯讀」，**那是錯的**，
  與 §1.2／§7.1／§7.2 的手機寫入作業矛盾；完整說明見 §10 業務行為第 7 條
- **Setup**：新增 Setup 回報畫面，記錄實際備模/調機時間供後續達成率分析

---

## 12. 自動測試規劃

### 12.1 後端 API 測試（xUnit + TestServer）

| 測試對象 | 驗證點 |
|---------|--------|
| `POST /api/mobile/auth/login` | 正確帳密回傳 Cookie；錯誤帳密回 401（Cookie 認證；若升級為 JWT 再補充）|
| `POST /api/mobile/auth/refresh` | *(Phase E3 升級為 JWT 時補充)*：有效 Refresh Token 回傳新 AccessToken；過期回 401 |
| `GET /api/mobile/dispatches` | 無有效 Cookie 回 401；有效 Cookie 依 machineId 篩選正確 |
| `POST /api/mobile/dispatches/{id}/setup/start` | Setup 開始；重複 clientId 去重 |
| `POST /api/mobile/dispatches/{id}/setup/done` | Setup 完成，Dispatch.Status = ReadyToStart |
| `POST /api/mobile/sessions` | 開工成功；Dispatch.Status = InProgress；重複 clientId 去重 |
| `POST /api/mobile/reports` | 報工累計；QtyDone = QtyTotal 時 Dispatch 自動 Closed；超量回 400 |
| `POST /api/mobile/machines/{id}/status-event` DI（故障）| Machine.CurrentStatus = BreakDown；Scheduled 派工退回 Pending；InProgress 派工 OnHold |
| `POST /api/mobile/machines/{id}/status-event` CI（恢復）| Machine.CurrentStatus = Available；無副作用（需人工重新排程）|
| 非法狀態轉換 | DI 從 Maintenance 狀態（不合法）回 400 + 錯誤碼 `INVALID_STATUS_TRANSITION` |
| 手機模組權限 | 無「開工」權限者呼叫 sessions API 回 403；無「機台異動」權限回 403 |
| 多廠別隔離 | A 廠使用者的派工不出現在 B 廠查詢結果 |

工具：`Microsoft.AspNetCore.Mvc.Testing` + `Testcontainers.PostgreSql`

### 12.2 前端元件測試（Vitest + Vue Test Utils）

> ⚠️ **未採用**：對應 Vue SPA 替代方案。原生 JS 方案的前端測試以 §12.3 E2E（Playwright）為主，元件單元測試可延後或改用輕量 DOM 測試。

| 測試對象 | 驗證點 |
|---------|--------|
| `BarcodeScanner.vue` | 掃到條碼時 emit 正確事件；掃描失敗切換到手動輸入 |
| `OfflineQueue` store（Pinia）| 離線時 enqueue；網路恢復後 dequeue 並發送；clientId 唯一 |
| `TokenService` | accessToken 存 sessionStorage；refreshToken 存 IndexedDB；登出時清除 |
| `ApiClient` 攔截器 | accessToken 過期自動 refresh 再重送原請求；refresh 失敗導向登入頁 |
| `DispatchList.vue` | 顯示 Scheduled 派工清單；空清單顯示空狀態提示 |
| `SetupForm.vue` | Setup 起始按鈕 → API 呼叫；完成按鈕更新 Dispatch 狀態顯示 |
| `MachineStatusForm.vue` | 掃碼後顯示當前狀態；依狀態只顯示合法事件按鈕；送出後更新顯示 |
| `MachineStatusWarning.vue` | 機台轉不可用時，顯示「N 張派工受影響」的警示彈窗 |

工具：`Vitest` + `@vue/test-utils` + `msw`（Mock Service Worker，模擬 API 回應）

### 12.3 E2E PWA 測試（Playwright）

| 測試情境 | 步驟摘要 |
|---------|---------|
| 完整投產流程 | 登入 → 掃派工 QR → Setup 開始 → Setup 完成 → 開工 → 報工 → 確認 QtyDone 累計 |
| JWT Refresh | 設極短 accessToken TTL → 觸發 API → 自動 refresh → 請求成功完成 |
| 離線報工同步 | DevTools 切離線 → 報工 → 切回線上 → 確認後端已收到並 DB 正確寫入 |
| 條碼掃描 fallback | 模擬掃描失敗 → 手動輸入框出現 → 輸入後正常提交 |
| 多語切換 | 切換語系 → UI 文字變更為對應語言；離線快取資料的語系也正確 |
| 帳號切換 | 登出 A 帳號 → 登入 B 帳號 → IndexedDB 的 RefreshToken 已清除 |
| 機台故障回報 | 掃機台 QR → 選「故障發生」→ 確認 → 機台狀態顯示 BreakDown；畫面出現受影響派工警示 |
| 機台離線故障排隊 | 離線 → 回報機台恢復 → 切回線上 → 確認後端狀態已更新（同步優先於報工）|

工具：`@playwright/test`（TypeScript）

> ⚠️ PWA E2E 測試需要 HTTPS（Service Worker 限制）。本地可用 `mkcert` 自簽憑證，
> 或 Chrome `--allow-insecure-localhost` flag 搭配 localhost。

### 12.4 測試整合至開發流程

| 環境 | 觸發時機 | 測試範圍 |
|------|---------|---------|
| 本地 | 存檔後（Vitest watch 模式）| 前端元件測試 |
| CI（PR 開啟）| 每次 Push | 後端 API + 前端元件 |
| CI（合併到 main）| Merge 後 | 全部（含 E2E PWA）|

---

## 13. 程式碼層慣例（未採用的 SPA 方案，已封存）

> ⚠️ 原 §13（Vue 3 + Pinia + Workbox 完整程式碼）為**未採用方案**，已抽出至附錄
> archive/mobile-pwa-appendix-unused-spa.md（內部文件，未公開），避免被誤抄進原生 JS 實作。
> 定案前端＝原生 JS + Cookie（見頂部架構決策、§9 Phase E1、§11.4）。
> 框架無關、仍需實作的概念：應用層離線佇列（IndexedDB）+ `online` 事件主動同步 + 後端 `clientId`/`clientTimestamp` 去重；Service Worker 只做讀取快取、不重放寫入。

## 14. IPQC / FPI 手機 UX 流程（製程品質檢驗）

> ✅ **已實作（2026-07-16，原生 JS，非 Vue）**。IPQC 與 FPI 手機流程前後端均已落地，對應 mfg-domain.md §15.3。
> §14.3 的 Vue 元件屬**未採用的 SPA 方案**，實際實作為原生 JS 靜態頁（page.js 因 CSP script-src nonce 而外部化）。
>
> **實作對照（現況）**：
> | 流程 | 前端頁面 | 後端端點 |
> |------|---------|---------|
> | IPQC 派工清單 | `wwwroot/mobile/mfg/ipqc.html` + `ipqc.page.js`（列 InProgress 派工） | `GET /api/mobile/dispatches?status=InProgress` |
> | FPI 派工清單 | `wwwroot/mobile/mfg/fpi.html` + `fpi.page.js`（列 PendingFPI 派工） | `GET /api/mobile/dispatches?status=PendingFPI` |
> | 檢驗量測（共用） | `wwwroot/mobile/mfg/ipqc-process.html` + `ipqc-process.page.js`（`?orderType=FPI\|IPQC`；MIL-STD-1916 抽樣、規格量測、Ac=0 判定） | 規格/抽樣：`GET /api/mobile/inv/ipqc/spec`、`iqc/sample-size` |
> | IPQC 送出 | 同上 | `POST /api/mobile/inv/ipqc`（不良即暫停派工） |
> | FPI 送出（dispatch-aware） | 同上 | `POST /api/mobile/mfg/inspection-orders`（Pass→派工 `ReadyToStart` 放行；Fail→`OnHold`） |
> | FPI 閘口 | — | Setup 完成後 `RequiresFpi`→派工 `PendingFPI`，開工阻擋；`RequiresInspection` 首次開工自動建 IPQC 待檢單 |
> | 入口 | `mfg/list.html` 頁首「首件 FPI／製程 IPQC」按鈕 | — |
> | 幂等 | `crypto.randomUUID()` clientId | `SyncLogs` 快照去重 |
>
> 測試：`SmartFactory.MES.IntegrationTests/Mobile/MobileFpiInspectionTests.cs`（FPI Pass 放行／Fail 暫停／幂等，真 PG）。
> 以下 §14.1–14.6 保留為原始設計說明；Entity 設計與 §14.3 Vue 範例僅供參考（實作以上表為準）。

IPQC（In-Process Quality Control）移至手機端操作，降低檢驗員往返次數、提高資料即時性。

### 14.1 IPQC 觸發場景

| 場景 | 觸發點 | 檢驗單位 | 備註 |
|------|--------|---------|------|
| **進料檢驗（IQC）** | GoodsReceipt 建立後 | 購買部品管 | Phase H 納入，現階段保留介面 |
| **製程檢驗（IPQC）** | Dispatch InProgress 後 | 現場品管 / 作業員 | 抽樣檢驗指定特性 |
| **完工檢驗（FQC）** | Dispatch 報工時 | 檢驗員 | 附帶在報工流程，見 §5.3 報工 |

本節著重 **製程檢驗（IPQC）** 的手機流程設計。

### 14.2 IPQC Entity 設計

#### InspectionOrder（檢驗單）

```csharp
public class InspectionOrder
{
    public string InspectionOrderId { get; set; }          // PK
    public InspectionOrderType OrderType { get; set; }      // FPI | IPQC | FQC
    public string DispatchId { get; set; }                  // FK → Dispatch（哪張派工需檢驗）
    public string SpecId { get; set; }                      // FK → InspectionSpec（檢驗規範）
    public int SampleQty { get; set; }                      // 抽樣數量
    
    // 檢驗結果
    public int? GoodQty { get; set; }                       // 良品
    public int? NgQty { get; set; }                         // 不良品
    public InspectionResult Status { get; set; }            // Pass / Fail / Pending / Conditional
    
    // 查證
    public string InspectorId { get; set; }                 // 檢驗員
    public DateTime? InspectedAt { get; set; }              // 檢驗時間
    public string Remarks { get; set; }                     // 備註

    // 投料批號快照（缺口四）
    // 手機端開工/領料時帶入；Phase F 領料功能後改系統自動填入
    public string? ActiveMaterialLots { get; set; }         // JSON 陣列快照，如 ["RM24-001","RM24-002"]
}

public enum InspectionOrderType
{
    FPI = 1,   // 首件檢查（Setup 完成後，開工前）
    IPQC = 2,  // 製程巡檢（開工後定期抽樣）
    FQC = 3    // 完工檢驗（報工時）
}

public enum InspectionResult
{
    Pending = 0,    // 待檢驗
    Pass = 1,       // 通過
    Fail = 2,       // 不通過
    Conditional = 3 // 有條件通過（需簽核）
}
```

#### InspectionItem（檢驗項目明細）

```csharp
public class InspectionItem
{
    public string ItemId { get; set; }                      // PK
    public string InspectionOrderId { get; set; }           // FK
    public string SpecItemId { get; set; }                  // FK → InspectionSpecItem
    public string ItemName { get; set; }                    // 項目名稱（快照）
    public string UnitOfMeasure { get; set; }               // 量測單位
    
    // 規範值（快照自規範表）
    public decimal? LowerSpec { get; set; }
    public decimal? TargetValue { get; set; }
    public decimal? UpperSpec { get; set; }
    public decimal? TargetStdDev { get; set; }              // 預設製程標準差σ（快照自 InspectionSpecItem）
                                                            // null 時跳過 3-sigma 警告，只比對上下限
    
    // 實測值（手機端輸入）
    public decimal? MeasuredValue { get; set; }
    public bool IsConforming { get; set; }                  // 是否符合規範（LowerSpec ≤ MeasuredValue ≤ UpperSpec）
    public bool HasSigmaWarning { get; set; }               // 是否觸發 3-sigma 警告（|MeasuredValue - TargetValue| > 3σ）
}
```

### 14.3 手機端 IPQC 流程（UX / UI）

#### 流程總覽

```
[FPI 路徑 — 首件檢查]
手機開工頁
  ↓ Setup 完成後，若 RoutingOperation.RequiresFpi = true
  → Dispatch 轉為 PendingFPI（「開工」按鈕置灰並顯示「等待首件檢查」提示）
品管人員手機
  ↓ 進入「首件檢查」功能 → 掃描派工 QR
  ↓ 建立 FPI 型 InspectionOrder → 填入量測值 → 提交
  if Pass → 後端 Dispatch 解鎖（PendingFPI → ReadyToStart），作業員「開工」按鈕亮起
  if Fail → 通知調機人員重調，再次首件直到 Pass

[IPQC 路徑 — 製程巡檢]
手機主頁
  ↓ 進入「檢驗」功能區
派工檢驗清單
  ↓ 選擇待檢驗派工 → 掃描派工 QR
檢驗規範顯示
  ↓ 抽樣並量測
檢驗結果輸入
  ↓ 逐項填入實測值
  ↓ 每次輸入後自動暫存草稿（IndexedDB Draft Autosave，見下方說明）
  ↓ 若 |MeasuredValue - TargetValue| > 3σ → 震動 + 黃色警告（見下方說明）
  ↓（系統自動判定符合 / 不符合 規格上下限）
檢驗完成確認
  ↓ 提交 → 後端建立 InspectionOrder + Items
後端同步結果
  ↓（若 Fail → 觸發不良流程；若 Pass → 可繼續報工）
```

**Draft Autosave（逐字暫存）設計要點**：
- 每次 `change` 事件觸發後，立即將整份表單草稿序列化為 JSON，以 `(userId, dispatchId)` 為 key 寫入 IndexedDB `inspectionDrafts` 表。
- PWA 載入時自動查詢是否有未提交草稿（同一 `dispatchId`），有則提示「恢復上次未完成的檢驗」。
- 草稿在成功提交後清除；不成功時保留供下次恢復。
- 實作直接擴充現有 IndexedDB 服務（`offlineQueue` 同一 DB，新增 `inspectionDrafts` object store），不引入新依賴。

**3-Sigma 輸入警告設計要點**：
- 前端在 `validateItem(item)` 時：若 `item.TargetStdDev` 不為 null 且 `item.TargetValue` 有值，計算 `|MeasuredValue - TargetValue| > 3 * TargetStdDev`，觸發則：
  1. 手機震動（`navigator.vibrate(300)`）
  2. 在該輸入框下方顯示黃色警告文字：「輸入數值偏離正常範圍（>3σ），請確認量測單位是否正確。」
  3. 不阻擋提交——警告僅提示，最終判定仍以 LowerSpec/UpperSpec 為準。
- `HasSigmaWarning` 欄位在後端 `validateItem` 時同步計算並寫入 `InspectionItem`，供稽核時查詢異常輸入。

#### Stage 1：派工檢驗清單

```vue
<!-- InspectionListView.vue -->
<template>
  <div class="inspection-list">
    <h2>{{ $t('InspectionList.Title') }}</h2>
    
    <div class="filters">
      <button @click="showScanModal = true" class="btn btn-primary">
        <i class="bi bi-upc-scan"></i> {{ $t('Scan') }}
      </button>
    </div>

    <div v-if="inspections.length === 0" class="empty-state">
      {{ $t('NoInspectionsRequired') }}
    </div>

    <div v-for="insp in inspections" :key="insp.id" class="inspection-card">
      <div class="card-header">
        <span class="dispatch-no">{{ insp.dispatchNo }}</span>
        <span class="badge" :class="statusBadgeClass(insp.status)">
          {{ insp.status }}
        </span>
      </div>
      <div class="card-body">
        <p>{{ insp.productName }}</p>
        <p class="text-muted">{{ $t('SampleQty') }}: {{ insp.sampleQty }}</p>
        <button @click="startInspection(insp.id)" class="btn btn-sm btn-info">
          {{ insp.status === 'Pending' ? $t('StartInspection') : $t('ViewResult') }}
        </button>
      </div>
    </div>

    <BarcodeScanner v-if="showScanModal" @scanned="onDispatchScanned" @close="showScanModal = false" />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import * as inspectionAPI from '@/api/inspections'

const inspections = ref([])
const showScanModal = ref(false)

onMounted(async () => {
  const data = await inspectionAPI.getPendingInspections()
  inspections.value = data
})

async function onDispatchScanned(dispatchId: string) {
  // 查詢此派工的檢驗規範，跳轉到檢驗表單
  showScanModal.value = false
  const inspection = await inspectionAPI.getInspectionByDispatch(dispatchId)
  if (inspection) {
    startInspection(inspection.id)
  }
}

function startInspection(inspectionId: string) {
  // 導向檢驗表單
}

function statusBadgeClass(status: string) {
  return {
    'Pending': 'bg-warning',
    'Pass': 'bg-success',
    'Fail': 'bg-danger',
    'Conditional': 'bg-info'
  }[status] || 'bg-light'
}
</script>
```

#### Stage 2：檢驗規範與項目輸入

```vue
<!-- InspectionFormView.vue -->
<template>
  <div class="inspection-form">
    <form @submit.prevent="submitInspection">
      <!-- 派工資訊（唯讀） -->
      <div class="form-section">
        <h3>{{ $t('InspectionForm.DispatchInfo') }}</h3>
        <div class="read-only-row">
          <label>{{ $t('DispatchNo') }}</label>
          <span>{{ inspection.dispatchNo }}</span>
        </div>
        <div class="read-only-row">
          <label>{{ $t('Product') }}</label>
          <span>{{ inspection.productName }}</span>
        </div>
        <div class="form-group">
          <label>{{ $t('InspectionForm.SampleQty') }}</label>
          <input type="number" v-model.number="sampleQty" required />
        </div>
      </div>

      <!-- 檢驗項目 -->
      <div class="form-section">
        <h3>{{ $t('InspectionForm.Items') }}</h3>
        <div v-for="(item, idx) in inspectionItems" :key="idx" class="item-input">
          <label>{{ item.itemName }} ({{ item.unitOfMeasure }})</label>
          <div class="spec-range">
            <span v-if="item.lowerSpec">{{ $t('Lower') }}: {{ item.lowerSpec }}</span>
            <span v-if="item.targetValue">{{ $t('Target') }}: {{ item.targetValue }}</span>
            <span v-if="item.upperSpec">{{ $t('Upper') }}: {{ item.upperSpec }}</span>
          </div>
          <input 
            type="number" 
            v-model.number="item.measuredValue"
            @change="validateItem(idx)"
            step="0.01"
            placeholder="輸入實測值"
          />
          <span v-if="item.isConforming !== null" class="conformance-badge"
                :class="item.isConforming ? 'ok' : 'ng'">
            {{ item.isConforming ? $t('OK') : $t('NG') }}
          </span>
        </div>
      </div>

      <!-- 檢驗結論 -->
      <div class="form-section">
        <h3>{{ $t('InspectionForm.Conclusion') }}</h3>
        <div class="form-group">
          <label>{{ $t('GoodQty') }}</label>
          <input type="number" v-model.number="goodQty" readonly />
        </div>
        <div class="form-group">
          <label>{{ $t('NgQty') }}</label>
          <input type="number" v-model.number="ngQty" readonly />
        </div>
        <div class="form-group">
          <label>{{ $t('OverallResult') }}</label>
          <select v-model="overallResult" required>
            <option value="">請選擇</option>
            <option value="Pass">{{ $t('Pass') }}</option>
            <option value="Fail">{{ $t('Fail') }}</option>
            <option value="Conditional">{{ $t('Conditional') }}</option>
          </select>
        </div>
        <div class="form-group">
          <label>{{ $t('Remarks') }}</label>
          <textarea v-model="remarks" rows="3"></textarea>
        </div>
      </div>

      <!-- 提交 -->
      <button type="submit" class="btn btn-primary btn-lg">
        {{ $t('Submit') }}
      </button>
    </form>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import * as inspectionAPI from '@/api/inspections'

const route = useRoute()
const router = useRouter()

const inspectionId = route.params.id as string
const inspection = ref(null)
const sampleQty = ref(0)
const inspectionItems = ref([])
const overallResult = ref('')
const remarks = ref('')

const goodQty = computed(() => 
  inspectionItems.value.filter(i => i.isConforming).length
)
const ngQty = computed(() => 
  inspectionItems.value.filter(i => !i.isConforming).length
)

onMounted(async () => {
  const data = await inspectionAPI.getInspection(inspectionId)
  inspection.value = data.dispatch
  inspectionItems.value = data.items.map(i => ({
    ...i,
    measuredValue: null,
    isConforming: null
  }))
})

function validateItem(idx: number) {
  const item = inspectionItems.value[idx]
  const val = item.measuredValue
  
  if (val === null || val === undefined) {
    item.isConforming = null
  } else {
    const conforming = 
      (item.lowerSpec === null || val >= item.lowerSpec) &&
      (item.upperSpec === null || val <= item.upperSpec)
    item.isConforming = conforming
  }
}

async function submitInspection() {
  const payload = {
    inspectionOrderId: inspectionId,
    sampleQty: sampleQty.value,
    goodQty: goodQty.value,
    ngQty: ngQty.value,
    overallResult: overallResult.value,
    items: inspectionItems.value.map(i => ({
      itemId: i.id,
      measuredValue: i.measuredValue,
      isConforming: i.isConforming
    })),
    remarks: remarks.value,
    clientId: crypto.randomUUID(),
    clientTimestamp: Date.now()
  }
  
  try {
    await inspectionAPI.submitInspection(payload)
    router.push({
      name: 'InspectionList',
      query: { success: 'true' }
    })
  } catch (e) {
    alert(`提交失敗: ${e.message}`)
  }
}
</script>
```

### 14.4 檢驗結果的後續動作

#### Pass（全部通過）

```
流程：
  1. InspectionOrder.Status = Pass
  2. Dispatch 可繼續生產 / 報工（無限制）
  3. 手機 UI 顯示「檢驗通過」提示，返回派工清單
```

#### Fail（有不良項目）

```
流程：
  1. InspectionOrder.Status = Fail
  2. Dispatch 轉入 OnHold（暫停，待處理）
  3. 警示頁面：
       「此派工檢驗不通過，已暫停。請聯繫品管處理。」
       [檢驗報告] [返回]
  4. 後端自動通知品管人員（Web Push）
  5. 可能的處理：
       - 重新量測（二次檢驗）
       - 返工（建立重工派工）
       - 報廢（不良品處理）
       - 特採（有條件通過）
```

#### Conditional（有條件通過，待簽核）

```
流程：
  1. InspectionOrder.Status = Conditional
  2. 觸發簽核流程（品管主管審核）
  3. 手機端顯示「等待簽核」狀態
  4. 主管透過 Web 簽核同意 → 派工解除 OnHold，可繼續
     或簽核拒絕 → 派工轉向 Fail 處理
```

### 14.5 IPQC 與報工流程的整合

#### 觸發規則（由系統參數控制）

| 條件 | 觸發類型 | 說明 |
|------|----------|------|
| RoutingOperation.RequiresFpi = true | **FPI**（首件檢查） | Setup 完成後強制首件；未通過前「開工」置灰 |
| RoutingOperation.RequiresInspection = true | **IPQC**（製程巡檢） | 開工後自動建立巡檢單 |
| Product.IsCritical = true | IPQC | 關鍵零件必檢 |
| 客訴回料 / 重工 | FPI + IPQC | 品質風險高，兩者皆觸發 |
| 新供應商首批 | IPQC | 過程審核 |
| 一般批量生產 | ❌ 可選 | 由品管判斷是否抽樣 |

#### 流程時序

```
[FPI 路徑]
SetupDone（Setup 完成）
  ↓ RequiresFpi = true
Dispatch → PendingFPI（「開工」按鈕置灰）
  ↓ 品管建立 FPI InspectionOrder 並 Pass
Dispatch → ReadyToStart → 作業員可開工

[IPQC 路徑]
Dispatch 轉 InProgress（開工）
  ↓
  if RequiresInspection → 自動建立 IPQC InspectionOrder（Pending）
  ↓
派工進行中...
  ↓
品管完成檢驗 → 提交 InspectionOrder
  ↓
  if Pass → 允許報工累計
  if Fail / Conditional → Dispatch.Status = OnHold，暫停報工
  ↓
（待簽核或處理）
  ↓
最終解除 / 通過 → 派工恢復，可報工累計至完工
```

### 14.6 自動測試補充（IPQC）

| 測試對象 | 驗證點 |
|---------|--------|
| 檢驗規範載入 | InspectionOrder 建立時帶入規範的 LowerSpec / TargetValue / UpperSpec |
| 實測值驗證 | 輸入實測值後自動判定是否符合規範（黑盒測試） |
| 全體符合 → Pass | GoodQty = SampleQty，NgQty = 0 時自動判定 Pass |
| 有不良項目 → Fail | NgQty > 0 時自動判定 Fail；Dispatch 轉 OnHold |
| 簽核流程 | Conditional 結果觸發簽核，批准 / 拒絕後派工狀態變化 |
| 離線提交 | 檢驗表單離線填寫 → 上線自動同步 → 後端正確寫入 |
