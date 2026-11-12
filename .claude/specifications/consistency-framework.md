# 畫面/操作一致性框架規畫（消除 Vibe Coding 遺漏）

## Context（為什麼要做）

ProjectZ 目前**完全依賴** `.claude/commands/new-screen.md` 的 70+ 項人工檢查清單來確保畫面完整，
**沒有任何 base class、共用 partial、或建置期檢查**。其後果就是使用者觀察到的兩個症狀：

- 存檔前的 JS `alert/confirm` 警示（30+ 處）硬編碼中文，不隨語系切換
- 15 個 Print 視圖 ~200 條標題/欄位/簽核字串硬編碼中文，且 `_PrintLayout` 寫死 `lang="zh-TW"`
- 必填表單（`<select required>` / `<input required>`）未加 `novalidate`，按存檔時觸發**瀏覽器原生 HTML5 驗證泡泡**（如「請選取一個清單中的項目。」），其文字由瀏覽器/OS 語言決定、與 App culture 無關 → en-US 模式仍顯示中文

根因不是這兩個畫面，而是**重複樣板靠手工複製**：`IsAjax`（重複 113 次）、`openPanel/submitPanel`
（重複 40+ 次）、權限 ViewBag、Print ViewBag、resx key — 任一處被 Vibe Coding 漏抄就產生缺陷，
且沒有任何機制在交付前抓出來。

本案目標：把「人工檢查清單」轉成**執行期自動完整 + 建置期自動把關**的雙保險框架，
先建機制、再分批回補既有畫面。已與使用者確認範圍＝完整框架／先建機制後分批回補／執行期＋建置期雙保險。

**零衝突鐵則（已依 Core/ProjectZ 歸屬查證修正）**：所有變更只落在 ProjectZ 顧問可控區，
**對 Core（SmartFactory.MES）零修改、零覆蓋風險**。

不得碰的 Core 檔（會被 `git merge upstream/main` 覆蓋）：
`Program.cs`、`Pages/Shared/_Layout.cshtml`、`Pages/Shared/_PrintLayout.cshtml`、
`Services/PrintHelper.cs`、`Data/ApplicationDbContext.cs`、Core `*Controller`、
**`CLAUDE.md`（在 Core `cp` 同步清單內）**。

對策：
- 列印樣板**不改 Core `_PrintLayout.cshtml`**，改新增 ProjectZ 專屬 `Views/Shared/_PrintLayoutZ.cshtml`，模組 Print 視圖改指向此 Layout
- 規格文件**不改 `CLAUDE.md`**，改更新 `.claude/LOCAL_RULES.md` 與 `.claude/commands/new-screen-projectz.md`（皆 ProjectZ 專屬、Core 禁止覆蓋）
- 共用元件放 `Modules/_Shared/`（新顧問目錄）、`Views/Shared/`（ProjectZ 部署專屬，Core 共用在 `Pages/Shared/`，互不覆蓋）、`wwwroot/js/` 新檔
- 全域 JS/i18n 不經 `_Layout`，改由各畫面 `@section Scripts` 引入共用 partial

---

## 架構：四支柱

### 支柱 1 — 執行期共用元件（讓畫面「天生完整」）

**1a. `ModuleControllerBase`（新檔 `Modules/_Shared/ModuleControllerBase.cs`）**
抽象基底控制器，把重複樣板集中為基底成員，子類別覆寫少量抽象屬性即可：
- `protected bool IsAjax`（取代 113 處重複）
- `protected Task PrepareIndexPermissionsAsync(string menuKey)` → 一次塞 `CanCreate/Update/Delete/Print` ViewBag
- `protected IActionResult ValidationPartial(string formView, object model)` → 統一 ModelState 無效時的 partial/JSON 回傳流程
- `protected JsonResult Ok(...) / Fail(string msg)` → 統一 `{success,message}` 契約（取代 50+ 處手寫）
- `protected Task<string> BuildQrAsync(...)` 與 `protected Task PreparePrintViewBagAsync(...)` → 統一 `ViewBag.QrSvg`/`UserNames`/列印時間，呼叫既有 `Services/PrintHelper.GenerateQrSvg`
- 既有控制器（如 `Modules/PUR/Controllers/PurchaseOrderController.cs`）逐批改為繼承，建構式注入維持不變

**1b. 共用前端樣板**
- 將 `openPanel / submitPanel / deletePo / submitApproval / withdrawApproval` 從每個 `Index.cshtml`
  抽到單一檔 `wwwroot/js/crud-panel.js` ＋ partial `Pages/Shared/_CrudPanelScripts.cshtml`
  （内含 `@Html.AntiForgeryToken()` 與 toast/offcanvas 連結）。各 Index 的 `@section Scripts`
  只 `@await Html.PartialAsync("_CrudPanelScripts")` ＋畫面專屬程式 → 消除 40 份漂移風險
- 參考既有重複來源：`Modules/PUR/Views/PurchaseOrder/Index.cshtml:300-360`

**1b-2. 驗證泡泡在地化（收斂瀏覽器原生 HTML5 驗證）— 已實作並 build 驗證**
- 表象：`<form>` 缺 `novalidate` + 欄位帶原生 `required`/`asp-for` 隱含必填 → 按存檔時
  瀏覽器**先**彈出原生驗證泡泡（如「請選取一個清單中的項目。」），文字依
  **瀏覽器/OS 語言**產生（非 App culture，也不在程式碼內，resx/i18n 框架無法觸及）
- **實際根因（DOM id 契約漂移，正是本案要消滅的 Vibe Coding 樣態）**：
  既有 `module-validation.js` 已備妥「補 `novalidate` + 重 parse unobtrusive」邏輯，
  但其 MutationObserver 監看 `#crudPanelBody`；而 `crud-panel.js` 的 `openPanel`
  注入點為 `#sidePanelBody` → observer 從未掛上，注入的表單永遠保留原生 `required`
- 對策（一處修、40 表單一致，已落地）：`crud-panel.js` 自帶 `armValidation(container)`，
  於 `openPanel` 與 `submitPanel` 兩處 innerHTML 注入後呼叫——對面板內每個
  `<form>` 補 `novalidate` 並 `removeData` 後重跑 `jQuery.validator.unobtrusive.parse`
  （沿用 `module-validation.js` 既有可用慣例，僅綁定到正確容器 id，不依賴其 observer）。
  `_CrudPanelScripts` 已引入 `_ValidationScriptsPartial`（jQuery unobtrusive）
- 結果：驗證改由 unobtrusive 接手，訊息來源為 `[Required]` DataAnnotations，
  經伺服端 `DataAnnotationLocalizerProvider` + `SharedResource` resx 隨語系在地化
  （與支柱 2 同源；非可空實值型別的隱含必填已由 `LocalizableDefaultMessageProvider` 補齊）
- 各 `_Form.cshtml` 不再需逐一手寫 `novalidate`（由共用 JS 統一補），Vibe Coding 不會再漏

**1b-3. `submitPanel` 成功契約強化（修 novalidate 暴露的潛在 bug）— 已實作並 runtime 驗證**
- 連鎖症狀：加 `novalidate` 後，未填必填欄位的送出不再被瀏覽器擋下、改打到伺服端，
  伺服端回傳 `_Form` 驗證錯誤 partial。但 `submitPanel` 原以 `html.includes('refreshList')`
  判斷「成功→關面板」；ORD/INV 的 `_Form` 內含自有 AJAX handler 也有 `refreshList`
  字面（如 `Modules/ORD/Views/SalesOrder/_Form.cshtml:383`）→ **驗證失敗誤判成功、
  側滑面板被秒關**（PUR `_Form` 無該行故未發病——典型 Vibe Coding 漂移）
- 根因：成功/失敗以「子字串嗅探」區分，不可靠
- 對策（`crud-panel.js`，一處修）：改結構判斷——成功時控制器固定回傳「純 `<script>`
  文件」（`Content("<script>...refreshList&&refreshList();</script>")`，全模組一致、
  無非 `<script>` 開頭的成功回傳）；驗證失敗回傳以表單標記開頭的 partial。
  `submitPanel` 改為「trim 後以 `<script` 開頭且含 `refreshList`」才視為成功
- Runtime 實測（authed，POST `/SalesOrder/Save` 空必填）：回應以 `<form id="soForm">`
  開頭且含 `field-validation-error` → 新邏輯正確判為失敗、面板保留顯示在地化錯誤；
  成功回傳格式為程式常數 `<script>` 開頭，契約穩定；PUR 行為不變（無回歸）
- 待驗：瀏覽器 e2e（en-US 觸發 SupplierCode 必填，確認顯示 unobtrusive 英文訊息、
  無原生中文泡泡、面板不關）須以 `SmartFactory.MES.UITests`（:5110）收尾

**1c. ProjectZ 專屬列印樣板（不改 Core `_PrintLayout.cshtml`）**
- 新增 `Views/Shared/_PrintLayoutZ.cshtml`（複製 Core 版為基礎再強化）：
  `<html lang="@CultureInfo.CurrentUICulture.TwoLetterISOLanguageName">`，表頭/頁尾/「列印」「關閉」按鈕用注入的 `IViewLocalizer`
- 新增 `Views/Shared/_PrintBase.cshtml`：本地化的區塊標題、簽核區（製作/審核/核准/日期）、合計列；
  各模組 `Print.cshtml` 改 `Layout = "_PrintLayoutZ"` 並只提供「標題 key + 欄位資料」
- 同步更新 `.claude/commands/new-report.md` 規格（非 Core `cp` 清單，ProjectZ 可改）

### 支柱 2 — 多語完整化（直接解決原始兩問題）

- **JS 字串**：新增 `Pages/Shared/_JsLocalization.cshtml` → 輸出 `window.i18n = {...}`（來源 resx 共用區），
  由 `_CrudPanelScripts` 與 `_PrintLayout` 引入；`crud-panel.js` 及各畫面 `alert/confirm` 改用 `i18n.t('key')`
- **Print 視圖**：依支柱 1c 套 `_PrintBase`，**重用既有 `_Form` resx key**（ProductCode/OrderQty/UomCode…）以減少新增 key
- **DataAnnotations**：伺服端已在線本地化（`BASModule` 設定 + `SharedResource` resx），僅需支柱 3 的分析規則禁止硬編碼 `ErrorMessage`
  - 已補強：`BASModule.LocalizableDefaultMessageProvider` 現會對**非可空實值型別的隱含必填**
    （`bool IsCustomer`、`int`、`enum`…）主動補帶預設訊息（= resx key）的 `RequiredAttribute`，
    使 client/server 兩端訊息皆隨語系翻譯（修正前次只處理顯式屬性的遺漏）
- **驗證訊息欄位名在地化（Option B，已實作並 runtime 驗證）**：訊息 `{0}` 原退回
  屬性名（`「SupplierCode」為必填欄位。`）。新增 `BASModule.LocalizableDisplayNameProvider`
  （`IDisplayMetadataProvider`，與 `LocalizableDefaultMessageProvider` 並列同模式）：
  屬性未標 `[Display]`/`[DisplayName]` 時，把 `DisplayMetadata.DisplayName` 設為
  「以屬性名為 key 經 `SharedResource` localizer 延遲解析」的委派，隨 culture 切換；
  查無 key 則回退屬性名（不退化、由 R5 把關）。零 Model 改動。
  - 字典範圍**＝（Model＋ViewModel 屬性名）∩（per-view resx 標籤）**：此交集自然
    排除跨 view 分歧的按鈕/動作 key（如 `AddEntry` 31 種），只留欄位名；少數欄位名
    輕微分歧（27 個，如 `DeliveryDate`）取**多數值**作單一 model 顯示名，語意一致
  - **全量回補完成**：以該交集自動 harvest 既有 per-view zh/en 標籤，去重去衝突後
    批次寫入 `SharedResource.{zh-TW,en-US}.resx`（pilot 3 + 50 [Required] + 149
    含 ViewModel 隱含必填 = **229 key，雙語 key 集合一致**）。harvest 取既有 UI
    標籤 → 與畫面用詞一致、零 Model 改動
  - 未涵蓋者為系統內部/非表單欄位（`Id`/`PageIndex`/`CanCreate`/序號量等，無
    per-view 標籤）→ 維持回退屬性名、由 R5b baseline 追蹤，不影響使用者畫面
  - Runtime 實測（authed，切 culture cookie 取 partial）：
    - PUR `/PurchaseOrder/Create`：zh-TW「供應商」為必填欄位。/ en "The Supplier field is required."（`maxlength` 同步在地化）
    - ORD `/SalesOrder/Create`、INV `/StockTransfer/Create`、ORD `/GoodsShipment/Create`：
      `建立者/訂單日期/調撥日期/出貨日期/狀態/簽核狀態/公司代號` 等 zh-TW↔en-US 皆正確切換，
      無殘留英文屬性名（含 ViewModel `DateOnly`/`enum` 隱含必填）
    - `dotnet build` 0 error
- **驗證泡泡**：由支柱 1b-2 收斂瀏覽器原生驗證 → 統一走 unobtrusive + 上述伺服端在地化訊息

### 支柱 3 — 建置期把關（雙保險）

> ✅ **狀態**：R1–R12（含 R4b/R5b/R6b/R6c/R6d）全數實作於
> `SmartFactory.MES.Tests/Conventions/ConventionTests.cs`，Cx1–Cx9 於 `ConventionTestsCx.cs`，
> 另有 **R0（meta，文件↔測試對帳）**。所有規則 `dotnet test` 全綠。
>
> ⚠️ **單一事實來源（避免本文件再漂移）**：規則的**完整現役清單以程式為準**——即
> `ConventionTests.cs`／`ConventionTestsCx.cs` 內的 `public void (R\d+\w*|Cx\d+)_…` 方法；
> 各規則**當前違規筆數以 `baseline-*.txt` 為準**，本文件不再寫死數字（避免下方數字與 baseline 漂移）。
> **R0_ConventionDocInSync** 會於 build 期自動斷言「每條現役規則都登錄在下方〈規則索引〉表」，
> 任何新規則漏記文件即讓 build 失敗——以框架自身機制守框架自身文件。
>
> baseline 概況（點時值，僅供參考；精確值見檔）：R1–R5 已空（Phase B 收斂）；
> R4b（PUR/ORD/INV 內聯 .cshtml CJK，待回補）、R5b（系統內部/非表單欄位，不影響使用者畫面）、
> R6（BAS 自洽 crudPanel 既定偏離，won't-fix）、R6b=0、R6c=1（INV WarehouseOps 誤報，won't-fix）、
> R6d、R7（PUR/ORD/INV/BAS controller 硬編碼中文訊息，待分批回補；MFG 已清零）、R8=0、
> R9（以檔案路徑為鍵，同 R4b/R7；baseline 僅剩**刻意 won't-fix** 的比率/費率與工時/加班——套 `±1` 反而錯）、
> R10（placeholder 硬編碼 CJK，INV Pwa 待整頁多語化）、R11=0（2026-09-11 全站收斂到共用 partial 後清空）、R12=0。
>
> #### 規則索引（R0 自動對帳；新增規則務必同步補一列）
>
> | 規則 | 把關內容（一句） |
> |---|---|
> | R0 | meta：每條現役規則都登錄於本表（防文件↔測試漂移） |
| R16 | `RemoveRange(x.Lines)` 後必須 `x.Lines.Clear()`（防殘留舊行被重複過帳） |
| R17 | `IDateTimeService.Now`（Kind=Unspecified 當地時間）不得寫入實體日期欄位，timestamptz 只收 UTC |
| R18 | 載入 `_CrudPanelScripts` 的頁面必須含 `id="successToast"`（缺了 toastSuccess 拋例外，連帶 refreshList 不執行＝存檔成功但畫面毫無反應） |
| R19 | controller 會加整表 ModelState 錯誤（`AddModelError("", …)`）時，對應 `_Form` 必須有 `asp-validation-summary="ModelOnly"`（否則守門訊息渲染不到任何地方＝按了存檔沒反應） |
| R20 | Controller 守衛不得讀表單帶回的 `vm.ApprovalStatus`（hidden 欄位，使用者可改＝偽造 POST 就能改掉審核中的單），須以 `_context.LoadApprovalStatusAsync<Entity>(id)` 讀 DB |
| R22 | 業務日不得取自伺服器時鐘（`DateTime.Today`／`DateTime.UtcNow` 的日期部分），須走 `IDateTimeService.GetLocalDate()`（解析 `Plant.DefaultTimeZone`）；此日期會帶進單號採番年月段、`MovementDate` 與分錄日，錯一天就可能落進已關帳期間。`DateTime.UtcNow` 本身（不取日期部分）是正確的稽核時戳，不在範圍內。**(b)**：顯示用時間戳（列印頁尾、匯出表頭、表單預設年度）同樣不得用 `DateTime.Now`／`DateTimeOffset.Now`，須走 `IDateTimeService.Now`（`.cshtml` 為 `DateTimeSvc.Now`）——(a) 只掃業務日，對「拿現在時刻直接秀出來」是盲的；開發與 CI 刻意跑 `TZ=UTC`（見 `DEVELOPMENT.md`），這種寫法會整整差一個時區且畫面上看不出是哪一種。掃 `.cs`（Modules／Controllers／Services）與 `.cshtml`（Modules／Views／Pages），兩邊各自計數自我檢查。**2026-09-14 已零例外**：原有 11 筆（Core 控制器匯出檔名 10 筆＋背景排程 1 筆）分別走 Core-first 上送與「排程時區改由 `Jobs:TimeZone` 組態明講」收斂完畢，`baseline-R22b.txt` 只留收斂理由 |
| R21 | mapped 的 decimal 欄位須明宣告 `[Column(TypeName = "decimal(p,s)")]`，不得落在無界 PG `numeric`（同語意欄位存進不同精度，對帳差異會像四捨五入誤差）；scale 分類見 `DecimalPrecision`，檢驗量測值 9 欄為白名單 |
| R28 | 使用手冊寫「點『X』」時，X 必須仍在畫面詞彙表內（zh-TW resx 值 ∪ 手機 `i18n.js` 中文值）。補既有缺口 **G2**：`HelpDocResolverTests` 只抓得到「缺頁」，抓不到「頁在但內容過期」——接上手冊 Q&A 後過期內容會被很有自信地講出來且無任何訊號。判定刻意只收點擊句型（手冊 707 處引號中大多是敘述用語，全掃會有 188 筆雜訊淹掉真漂移；收窄後 262 命中／8 未命中） |
| R29 | 規格文件 `<!-- open-items -->` 區塊裡沒劃掉的待決項，必須列在 `STATUS.md`〈真正未完成〉；已劃掉的不得繼續列在那裡（雙向對帳）。守的是**手抄聚合必然漂移**這件事：2026-08-13 逐條查證那份清單，7 條裡 3 條已不成立（待決 #9 早已裁決、G2 已由 R28 守門、§9 #4 的檔案早被刪），同時 §14.2 的待決 #10 從來沒被收進去過。兩個方向代價不對稱——**漏收會把缺口藏起來，比列了過期項嚴重**。刻意**不做自動產生**：清單每條都帶著產生器寫不出來的判斷（「卡外部 ERP 介接方式」），機器只負責對帳。`⏸` 標記＝刻意延後（等上線數據），不列入清單但仍留在來源表內 |
| R32 | 側面板的「版面 id ↔ 腳本契約」不得混用：①載入 `_CrudPanelScripts` 的檔不得出現 BAS 舊式的 `#crudPanel`／`#crudPanelTitle`／`#crudPanelBody`；②有 `id="sidePanel"` 就必須有 `sidePanelLabel` 與 `sidePanelBody`（三個是一組，`crud-panel.js` 三個都取，缺一個 `openPanel` 就在那一行拋 null）；③走共用契約的資料夾內，partial 不得留 `data-ajax`／`data-dismiss-panel`。補的是**沒有任何規則在做跨檔配對**這個缺口——R5b／CSP／R14／Cx8 掃的都是單檔，而單看版面合法、單看腳本也合法，**錯的是兩者的組合**。2026-09-10 的 R-F1（報表排程面板整頁按鈕全死）正是這樣進來的。⚠️ ③的範圍**只到走共用契約的資料夾**：那兩個屬性在 BAS 的 23 個畫面是活的（inline script 真的在讀），全庫仍有 49／41 處，寫成全站禁用會一次誤判掉整個 BAS；且用「同資料夾」而非解析 `PartialAsync` 引數——R-F1 那支表單根本不在 Index 裡 render，靜態追 render 樹會漏掉正是要抓的那一種。附 `probed >= 50` 自我檢查 |
| R31 | 每個手機畫面（`wwwroot/mobile/**/*.html`）都必須有一頁使用手冊在講它——判準是手冊頁 doc-header 自己宣告的「路徑：/mobile/…」。補的是**手機側完全沒有守門**這個缺口：桌機有 `HelpDocResolverTests`（已登錄畫面必有手冊頁），但手機畫面不進 MenuContributor、沒有 MenuKey，母體裡根本沒有它們。代價已發生過一次——手機「待我簽核」自 2026-08-27 唯讀到 09-10 可簽核共三個 PR、14 個單別摘要，手冊裡一個字都沒有（全文搜「簽核」0 命中），而沒有任何測試會紅。配對方向刻意由**手冊宣告**而非檔名推導（`dispatch-list.html` 講的是 `/mobile/mfg/list.html`，用檔名配對等於逼手冊改名迎合程式）。例外須寫理由（登入頁／首頁／離線頁／模組入口清單／共用子頁／轉址殼），並反向檢查例外清單不得留下已不存在的頁 |
| R27 | zh-TW resx 值不得出現「搜尋」——查詢列動作的中譯一律「查詢」（2026-08-12 裁決）。統一前同一個 `Search` 鍵有兩種中譯（72 支「查詢」／58 支「搜尋」／12 支檔內並存），英文兩者都是 Search 故只有中文使用者看得到。**實測不影響 Q&A 語意檢索**，所以這是產品一致性規則，也正因為壞掉不會有徵兆才需要守門 |
| R30 | 查詢 `StorageLocations` 必須在同一運算式鏈內表態廠別範圍：`.ForCurrentPlant(db)`／`.IgnoreQueryFilters()`／`.Join(… Warehouses …)`／條件含 `WhCode ==`，四選一。`StorageLocation` 主鍵是 `(WhCode, LocationCode)`、**沒有 `PlantId` 也沒實作 `IPlantEntity`**，全域廠別過濾器對它不生效——`_db.StorageLocations.Where(l => l.IsActive)` 拿到的是全公司所有廠的儲位。人工驗收 W4-F11 由跨廠調撥來源儲位下拉先踩到，回頭掃描共 15 處同型：庫存異動／調整／報廢／移轉／備料／盤點的儲位下拉全跨廠，行動端掃碼驗證與揀貨過帳更會直接對別廠儲位過帳。共通症狀是**完全沒有錯誤訊息**：選了別廠儲位，伺服端拿本廠查可用量必為 0，使用者只看到與原因無關的「可用量不足」。寫入與主鍵查詢不在範圍內 |
| R33 | controller 的每頁筆數預設值必須出現在 `_PageSizeSelect` 的 `pageSizeOptions` 裡。抓的是「下拉顯示的值與畫面實際筆數不一致」：BAS/SpecialDay 的 controller 預設 31 筆，而選項只有 {10,20,50,100}，於是**沒有任何 option 被選中**，瀏覽器顯示第一項「10」而畫面其實是 31 筆——不報錯、不拋例外，那個數字就一直在騙人。選項清單直接從 partial 讀回來比對，規則與實作不會各說各話。附 `probed >= 50` 自我檢查 |
| R34 | 清單操作欄（表格 `<td>` 內、綁在單一列上的 `<a>`／`<button>`）一律「圖示 ＋ 文字」。明細編輯列的 `removeXxx`／`lookupXxx`／`addXxx` 是例外——每列一個，加字會洗版——但必須有 `title` 或 `aria-label`。2026-09-11 收口前：224 個列級動作裡圖示+文字 123、只有圖示 71、只有文字 30，**20 個畫面自己內部就在混用**；只有圖示的那 71 個中有 53 個連 `title` 都沒有，滑鼠停留沒提示、讀螢幕器也讀不出來。附 `probed >= 150` 自我檢查 |
| R35 | `<!-- mirrors ref="…" -->` 圈起來的「手抄聚合節」，狀態必須與來源 `<!-- open-items -->` 的待決項一致（`manual-test-plan.md` §12〈未完成事項總表〉↔ §11）。補的是 **R29 守不到的第三、四份抄本**：同一批待決項在本專案被抄了四份——§11（來源）、`STATUS.md`、§12 總表、§12 各分節小表——而 R29 只對帳前兩份。§12 自己就寫著「**R29 對不到這裡**」（§12.3）與「後三列一旦在 §11 劃掉，**這裡不會自動跟著變**」（§12.8）：兩處都正確診斷了漂移，然後繼續靠人記得；這條規則就是把那句話變成測試。三個方向的代價不對稱（同 R29）：**漏收**（§11 開著、§12 沒有）讓缺口從總表上消失最嚴重；**過期**（§11 結案、§12 還開著）會有人照著總表把做完的事再做一遍；**早退**（§11 開著、§12 已劃掉）比過期更糟，缺口被「看起來已完成」蓋住。契約刻意遷就既有排版而非逼文件重排：一列「講到」某待決項的判準是 ①第一欄就是編號（§12.3 的 `| ~~5~~ |`）或 ②**第二欄**（出處欄）同時寫出章節記號與編號（§12.8 的 `| … | §11 #12 | … |`）——⚠️ `#N` **只認第二欄**，內文到處都是「改寫了 §11 #8 的風險評估」這種交叉引用，全列去抓會把敘述誤判成聚合列（§12.7 的手動發票列實測正是這樣被誤命中）；結案訊號同收 `~~` 與 `✅`（§12.3／§12.8 用刪節線，§12.1／§12.4 用 ✅）；來源標 `⏸`（刻意延後）者跳過不驗——那種項目在 `STATUS.md` 不列（R29 的規定）、在〈未完成事項總表〉卻**該**列（§12.5 整節就是那個桶子），兩邊語意相反，逼它們一致只會製造假違規。附雙重自我檢查——`scannedRows >= 40`（標記還圈得到表格嗎）與 `matched >= 8`（編號還認得出來嗎），兩者測的是不同的失效方式 |
| R36 | `Modules/` 底下的 `ScheduledStart`／`ScheduledEnd`／`PlannedStartDate`／`PlannedEndDate` 不得出現在任何時區換算（`ToLocalTime`／`ToUtcTime`）的同一行。這些欄位的**數字本身就是廠別當地時間**，`Kind=Utc` 只是貼給 Npgsql 的標籤（`mfg-domain.md` §UTC 地雷；源頭是 `MrpEngine` 的 `Today = SpecifyKind(_dt.Now.Date, Utc)`），再換算一次就是多加一個時差。⚠️ **不能與「顯示不得用伺服器時區」那條合併**：那一條抓的是 BCL 的 `DateTime.ToLocalTime()`、修法是改走 `IDateTimeService`；而這些欄位**兩種都不對**，正解是不換算。把它們「修正」成 `DateTimeSvc.ToLocalTime(...)` 會通過那一條，症狀卻完全一樣（差一個時差且畫面上看不出來），所以要有一條認得這些欄位的規則。2026-09-16 建立前先修掉 12 處既有違規，故 baseline 是空的（零容忍）。附 `fieldMentions >= 50` 自我檢查——欄位一改名，規則會靜默地永遠綠 |
| R37 | `Modules/` 底下 `.cshtml` 的**元素內容位置**不得印 `ToolKey`（`>@x.ToolKey<`）。ToolKey 是內部識別字，報表的顯示名與選單共用同一組鍵（ToolKey ＝ MenuKey，由 `ReportToolCatalogTests` 守住 1:1），所以顯示名一律是 `L[ToolKey]`（`ProjectZSharedResource`）。⚠️ **屬性位置不在管轄範圍**（`value="@x.ToolKey"`、`asp-route-toolKey`）——那是傳值不是給人看的文字，連屬性一起擋會逼出一堆假修正。由來是同一個缺陷修過一次又復發：`_ReportScheduleForm` 的下拉早就改成顯示名（該檔註解還寫著理由），但 `ReportSchedule/Index` 的篩選下拉與「報表」欄、`ReportInbox/Index`、`ReportRunHistory/Index` 四處都還印著 ToolKey——欄頭譯文是「報表／Report」，內容卻是 `InventoryValuation` 這種識別字，**四處各自寫、沒有任何機制看得見它們不一致**。2026-09-16 建立前先修掉那 4 處，故 baseline 是空的（零容忍）。附 `toolKeyMentions >= 10` 自我檢查——畫面重構或欄位改名後，規則會靜默地永遠綠 |
| R38 | 樣式不得**靜默不套用**，兩種形狀合為一條。**(a)** `Views`／`Modules`／`Areas` 的 `.cshtml` 不得有 inline `style=` 屬性——`Program.cs` 的 CSP 是 `style-src 'self'`，而 nonce／hash 對 style **屬性**無效（只對 `<style>` 元素有效），所以寫了就是被瀏覽器整條丟掉：不報錯、不告警，只是版面怪怪的。**(b)** `wwwroot` 下每一頁用到的 `u-…` class，必須在**那一頁自己載入的** stylesheet 裡定義得到——症狀一模一樣，差別只在是誰把它丟掉。⚠️ (b) **不可以拿全站 stylesheet 的聯集判定**：`docs/user/mobile/approval.html` 用的 `u-width-30pct` 在 `docs/user/index.page.css` 有定義，但那一頁根本沒載入那支 css，用聯集掃的第一版正是這樣漏掉它的；站內絕對路徑（`/mobile/css/mobile.css`）以 `wwwroot` 為根而非該頁所在目錄，沒分這兩種會把整個 mobile 誤判成未定義。⚠️ 動態值（`style="width:@x%"`）不是靠 class 解決，走 `site.js` 既有的 `data-pct-width`：CSSOM 屬性設值不受 `style-src` 管制；本規則只認得 `style=` 有沒有出現，不判斷替代方案好不好。由來：2026-09-18 以 Playwright 走訪 145 頁只量到 8 則 CSP console error，靜態掃描卻找到 11 處——差額是「那一列剛好沒渲染出來」（資料為空、面板沒展開），**靠跑測試去撞它會隨資料狀態忽紅忽綠**。建立前先修掉 11 處 inline style ＋ 3 個未定義 class（其中 `u-max-height-180px` 走 Core-first 補進 `csp-utilities.css`），故 baseline 是空的（零容忍）。附兩個自我檢查——`scannedViews >= 200` 與 `scannedPages >= 150`，測的是兩個不同的失效方式（目錄搬動讓任一半靜默永遠綠） |
| R39 | **可授權的選單項（有 `Controller` 或 `Page` 的那些），`MenuKey` 不得重複登錄。** `MenuService.GetFullMenu()` 是 `SelectMany`、不去重；權限以 MenuKey 為鍵，而權限矩陣的表單索引是「MenuKey → 扁平清單位置」查出來的。重複登錄的後果是：① 側邊欄出現兩筆一模一樣的連結；② 權限矩陣出現兩列而**兩列共用同一個 `permissions[i]`**——彼此不獨立，改一列另一列跟著變；③ `RolePermissionSeeder` 的 ScreenInfo 清單跟著重複。三種都是「畫面看起來正常」的形狀。⚠️ **群組標題（沒有 Controller 也沒有 Page）刻意不在範圍內**：兩支 contributor 各宣告同一個群組是既定做法（ProjectZ 的 `AdminManagement` 是刻意留空、給 QA 擷取物掛子項的群組，Core 也有一個同鍵群組），空群組會被 `SidebarMenuViewComponent.FilterMenu` 丟掉，不產生上述任何後果——把群組一起管的話，這條規則第一天就得替一個正確的設計開例外。⚠️ 判定**不掃原始碼字串**，而是用真實 contributor 組出選單樹（`ConventionMenu.Build()`，報表工具開關打開）：跨檔重複與「群組 vs 畫面」這兩件事，regex 都看不出來。由來：`OutsourceMaterial` 2026-04-24 初版登錄於庫存群組，#62「委外管理選單重組」（2026-07-11）又加進 `OspManagement` 而舊的沒移掉，重複了兩個月；發現它的是瀏覽器層的 `PermissionMatrixUiTests`，但那一層只在 nightly 跑，故補這條靜態守門。baseline 空（零容忍）。自我檢查 `flattened.Count >= 100`——選單撈空的話分群永遠是空的，規則會永遠假綠 |
| R40 | **手冊搜尋的資產不得靜默失聯**，兩種形狀合為一條。**(a)** `wwwroot/docs/user` 每一頁都必須載入 `doc-search.css` / `doc-search.js`，且那個相對路徑真的指得到檔案——手冊 173 頁分四種目錄深度、注入是批次做的，路徑寫錯的那幾頁不報錯，只是搜尋框不出現；而使用者多半從系統畫面的「?」直接落在內頁，**首頁看得到搜尋框完全不代表內頁看得到**。**(b)** `doc-search.js` 用到的 class 必須在 `doc-search.css` 定義得到。⚠️ 這是 **R38b 的死角**：R38b 掃的是 `.html` 裡的 class，而搜尋 UI 整個由 JS 動態建立，**沒有任何一個 class 出現在任何一份 `.html` 裡**——症狀與 R38b 一模一樣（樣式被丟掉、版面怪怪的、不報錯），只是靜態掃描那一層看不見它。建立時即零違規，baseline 空（零容忍）。附兩個自我檢查——`scannedPages >= 150`（手冊搬家或副檔名改變會讓 (a) 永遠假綠）與 `usedClasses >= 8`（class 命名慣例一改，(b) 就再也抓不到任何東西） |
| R41 | 每個參照主檔的實體欄位（品號、單位、倉庫／儲位、幣別、往來廠商、工作中心、機台…）都必須在 `ReferenceRules` 登錄檢查規則或豁免（附理由）。2026-10-02 盤點：110 個實體、300 個參照欄位，伺服器端幾乎都沒確認下拉送來的值「存在、屬本廠、仍啟用」，DB 也幾乎沒有外鍵——代號打錯、別廠的、已停用的都寫得進去且不報錯；下拉本身有過濾所以平常看不出來，缺口在送出值不受下拉約束（手改表單、頁面停留太久、手機 API、送出前主檔剛被停用）。檢查本身由 `ReferenceValidator` 統一執行（只驗有變更的值、同次存檔新增的主檔視為存在）。掃 EF snapshot（同 R21），欄位名不統一故用名稱樣式比對、實體自身主鍵不算；反向檢查登錄表不得指到不存在的欄位、同欄不得既是規則又是豁免。baseline 為尚未分批處理的欄位，逐批刪減（2026-10-08 清空；同日檢查正式啟用：存檔攔截器＋全站錯誤 Filter，見 reference-validation-inventory.md §5〈啟用〉）。自我檢查 `refs >= 250` |
> | R1 | `.cshtml` `@Localizer["k"]` 對應 resx 雙語皆有 key |
> | R2 | Controller MenuKey 已登錄 Menu 且 resx 雙語有 key |
> | R3 | `Print.cshtml` 須 `@inject IViewLocalizer`、模板區無 CJK |
> | R4 | `wwwroot/js/*.js` 的 alert/confirm/innerHTML 無硬編碼 CJK |
> | R4b | `.cshtml` 內聯 `<script>` 的 alert/confirm/innerHTML 無硬編碼 CJK |
> | R5 | 每畫面 4 個 resx 齊備、zh-TW/en-US key 集合一致 |
> | R5b | Model `[Required]`／隱含必填屬性名在 `SharedResource` 雙語有 key |
> | R6 | `crud-panel.js` `armValidation` + Index 引入 `_CrudPanelScripts`，收斂原生驗證泡泡 |
> | R6b | 面板成功契約結構式判斷（純 `<script>` 開頭），禁子字串嗅探 |
> | R6c | 動態列 append 後重註冊驗證（`armValidation`／`parseModuleValidation`） |
> | R6d | 每個可編輯 `asp-for` 欄位有對應 `asp-validation-for` span（或 summary=All） |
> | R7 | `*Controller.cs` 伺服端訊息不得硬編碼 CJK |
> | R8 | 無參數 `@Localizer["k"]` 之 resx 值不得含未跳脫 `{N}`（防 FormatException） |
> | R9 | `<input type="number">` step 一律 `any`（核心 helper/JS 源同守）；數量欄位另禁寫死 `step="1"`／`min="1"`（(c)，擋小數輸不進去）|
> | R10 | `placeholder="..."` 不得含硬編碼 CJK（非 `@` 開頭值） |
> | R11 | 分頁 UI 一律走共用 partial：`@model PaginatedList<T>` 的畫面須同時引用 `_Pagination` 與 `_PageSizeSelect`，且任何 View 不得自帶 `class="pagination"` 或 `<select name="pageSize"`（附 `paginated >= 80` 自我檢查） |
> | R12 | Index 顯示 `RequestedBy`/`CreatedBy` 等須經 `userNames` 字典轉名（防顯示 GUID） |
> | R13 | `[HttpPost]` action 本體須含伺服端權限檢查（Forbid/CanX/CanExecuteAsync；類別級 `[Authorize(Roles=…)]` 視為已閘），防裸奔寫入；baseline 僅收經審查豁免 |
> | R14 | 以 class 隱藏（`u-display-none`/`d-none`，皆 `!important`）的元素不得用 `style.display` 切換——蓋不掉 class 導致永久隱藏，一律改 `classList`（cshtml 同目錄配對；wwwroot 同名 `X.html`↔`X.page.js` 配對；共用 js 為接受盲區） |
> | R15 | 凡送出簽核的 DocumentType 都須有「已註冊」的 `IApprovalSyncContributor`（或 Core fallback），否則核准後單據靜默停在 PendingApproval；零容忍、不走 baseline |
> | R23 | 選單登錄畫面的 `Index` 必須有伺服端讀取閘門（`!await …CanX(…)` ／ `Forbid(` ／ `CallerIsAdminAsync`）。專案無全域權限 filter，`[Authorize]` 只保證有登入，漏了＝選單隱藏但 URL 直達可看；`ViewBag.CanX = await …CanX(MenuKey)` 與 `PrepareIndexPermissionsAsync` 都**只餵旗標給 View、不擋人**。**判定有無守衛，不判定用哪個動詞**（`CanRead` vs `CanCreate` 是機密分級的業務決策，見 `report-permission-inventory.md`）。baseline 已清零 |
> | R24 | `/api/…` 端點的每個 action 必須有權限閘門（桌面 `!await …CanX(…)`／`Forbid(`，或手機 `RequireMobilePermissionAsync`／`RequireMobileModuleAsync`），否則須在該 action 標 `[PermissionGateExempt("理由")]`。R23 只鎖畫面等於鎖門開窗——JSON 端點才是資料真正離開伺服器的地方（`/api/warehouse/product-detail` 直接吐 StandardCost）。**共用端點勿鎖單一鍵**：多畫面共用的查詢改用 `CanReadAny(…)`／模組層閘門，否則「有 A 沒有 B」的角色會靜默壞掉（前端只是欄位空白、不報錯）。豁免走 Attribute 不走 baseline；baseline 僅收兩支 Core 檔案 |
> | R25 | 非 `/api/` 的「會吐資料」GET action（`Details`／`Create`(GET)／`Edit`(GET)／JSON 查詢）也須有讀取閘門。R23 只管 `Index`、R24 只管 `/api/…`，中間這塊量最大：2026-08-03 稽核 459 支、**112 支無閘門**（`/SalesOrder/Details/{單號}` 直接吐整張訂單含金額）。單據 `Details` 用 `CanReadDocumentAsync`（模組可讀 **或** 本人是該單簽核關係人——「待我簽核」點單號就是打 `Details`，只鎖 MenuKey 會讓自訂簽核角色靜默 403）。母體排除 `/api/…`（R24）與寫入動詞（R13） |
> | R26 | GET action 不得寫入（本體出現 `SaveChanges`／DbSet `Add\|Remove\|Update`／`ExecuteUpdate\|Delete`／`UserManager` 狀態變更即違規）。**這不是 R13 的重複**：R13 問「誰能做」，R26 問「他會不會被騙著做」——受害者自己有權限，攻擊者只要讓他的瀏覽器發出該 GET（`<img src>`、縮網址）即可，AntiForgery 對 GET 不生效。實例：`TwoFactor/RegenerateRecoveryCodes` 是 GET 卻會作廢本人既有 2FA 復原碼，誘導點一下即毫無徵兆地鎖死救援途徑（Core PR #16 已改 POST）。全庫僅此一支，baseline 零容忍；修法一律是改 POST + AntiForgery，不是加閘門 |
> | Cx1 | `IStringLocalizer<ProjectZSharedResource>` 的 `L["k"]` 雙語 resx 皆有 key |
> | Cx2 | View `onclick/onsubmit` 呼叫的 crud-panel.js 函式皆 `window.*` export |
> | Cx3 | 以 `IsNullOrEmpty` 判 isNew 的屬性不得有 `= Guid.NewGuid()` 預設值 |
> | Cx4 | Side Panel partial 不得自帶 inner `offcanvas-header`（防雙標題） |
> | Cx5 | DataAnnotation `ErrorMessage`/`DisplayName` 不得含硬編碼 CJK |
> | Cx6 | 注入 SharedResource 的 View，HTML 元素文字不得含硬編碼 CJK |
> | Cx7 | 注入 SharedResource 的 View，`<script>` DOM 賦值不得含硬編碼 CJK |
> | Cx8 | inline 事件處理器棘輪：`.cshtml` 不得新增 `on*=`（凍結既有 880 存量，回補後與 baseline 一併退場；CSP 移除 `unsafe-inline` 前置）。改用 `data-action` 委派 |
> | Cx9 | `window.i18n.t('k')` 的 k 必須在 `_JsLocalization.cshtml` 白名單且雙語 resx 皆有（`t()` 查無即回傳原始鍵值，靜默顯示在使用者眼前）|
>
> 註：**R4** 僅掃 `js`/`.cshtml` dialog，**R4b** 補 `.cshtml` 內聯 `<script>`。

於既有 `SmartFactory.MES.Tests` 新增**慣例測試（convention tests）**，納入 CI gate（最低成本先行，
熱點規則日後可升級為 Roslyn analyzer）：

- **R1 resx key 一致**：掃描所有 `.cshtml` 的 `@Localizer["k"]`，驗證對應 resx（zh-TW＋en-US）皆有該 key
- **R2 選單登錄**：每個帶 `[Authorize]`＋`MenuKey` 的 `*Controller`，其 MenuKey 必須出現在
  `ProjectZMenuContributor` 且 `ProjectZSharedResource.{zh-TW,en-US}.resx` 皆有對應 key
- **R3 Print 必用 Localizer**：`*/Print.cshtml` 必須 `@inject IViewLocalizer`，且模板區（非 `@` 運算式）無 CJK 字面
- **R4 JS 無硬編碼 CJK**：`wwwroot/js/*.js` 與 `.cshtml` 的 `<script>` 區塊內，`alert/confirm/innerHTML` 不得含 CJK 字面
- **R5 翻譯不缺漏**：每畫面 4 個 resx 齊備，且 zh-TW 與 en-US 的 key 集合完全相同
- **R5b 驗證欄位名齊備**：掃描所有 Model 帶 `[Required]`／非可空實值型別（隱含必填）
  且未標 `[Display]`/`[DisplayName]` 的屬性，其屬性名必須在 `SharedResource.{zh-TW,en-US}.resx`
  雙語皆有對應 key（否則驗證訊息 `{0}` 會退回英文屬性名）；可自動列舉屬性集合，
  隨分批回補逐步轉綠（baseline 允許清單）
  - **不掃無公開 setter 的唯讀計算屬性**：沒有 setter 就不會被表單繫結，
    也就不可能出現在驗證訊息的 `{0}` 裡，要求它有 resx key 是純粹的誤報
    （2026-09-06 由 `AuditFailureDetail.IsBlocking` 觸發）。
    init-only 與 record 位置參數都算有 setter，仍在掃描範圍內。
  - 現況：表單欄位名已全量回補（229 key），baseline 僅剩系統內部/非表單欄位
    （`Id`/`PageIndex`/`CanCreate`/序號量等，無 per-view 標籤、不出現在使用者畫面）；
    此測試把這些釘進 baseline，未來新欄位若漏補 SharedResource 即會被擋下
- **R6 表單不外露瀏覽器原生驗證**：（a）`crud-panel.js` 必須含 `armValidation`
  並在 `openPanel`/`submitPanel` 注入後呼叫；（b）所有面板 Index 皆引入
  `_CrudPanelScripts`（內含 `_ValidationScriptsPartial`）；（c）守住 `crud-panel.js`
  與其注入容器的 DOM id 契約一致（防 `sidePanelBody`/`crudPanelBody` 類漂移復發）
  → 確保驗證一律走伺服端在地化的 unobtrusive 訊息，不外露瀏覽器語言
- **R6b 面板成功契約一致**：（a）`submitPanel` 必以「回應 trim 後以 `<script` 開頭」
  判斷成功（禁用 `includes('refreshList')` 類子字串嗅探）；（b）所有控制器 Save
  成功回傳必為「純 `<script>` 文件」（掃 `Modules/*/Controllers/*.cs`，不得有
  非 `<script>` 開頭的成功 `Content(...)`）；（c）驗證失敗一律回 `_Form` partial
  → 防「`_Form` 內含 `refreshList` 字面 → 驗證失敗誤判成功關面板」復發
- **R6c 動態列驗證重註冊（Phase F，規劃中）**：凡 `.cshtml` `<script>` 內以
  `appendChild`／`insertAdjacentHTML` 動態新增含 `data-val` 的明細列者，須於插入後呼叫
  `window.armValidation(form)` → 防動態列漏即時前端驗證（伺服端仍兜底，屬 **UX 一致性**把關，
  非正確性破口）。詳見 Phase F1
- **R6d 欄位級驗證訊息可顯示（已實作）**：每個 `_Form.cshtml` 內**可編輯的 `asp-for` 欄位**
  （`<input>`/`<select>`/`<textarea>`，排除 `type="hidden"` 與唯讀）必須有**對應同屬性的
  `<span asp-validation-for="同屬性">`**；或該表單改用 `asp-validation-summary="All"`。
  - 破口：表單只放 `asp-validation-summary="ModelOnly"`（僅模型層）又無欄位級 span →
    **欄位級 ModelState 錯誤（`[Required]`／自訂如「迄>起」「工作中心或機台擇一」）回前端無處渲染**
    → 面板「只閃一下、沒訊息」。`AddModelError("")` 的模型層錯誤才進 ModelOnly summary。
  - 與 R6/R6b 互補：R6/R6b 保證 unobtrusive 接手＋伺服端在地化訊息**有來源**，
    R6d 保證這些訊息在每個欄位**有去處**（顯示元素）。
  - 實作：掃 `Modules/**/_Form*.cshtml`／`Views/**/_Form*.cshtml`，對每個 `asp-for="X"`
    可編輯欄位檢查同檔存在 `asp-validation-for="X"`（或表單含 `asp-validation-summary="All"`）；
    既有缺漏入 baseline（以檔案路徑為鍵，同 R4b/R7/R9）分批回補。
  - 已知踩雷並修：MFG `Routing/_Form`、`PlannedDowntime/_Form`（新建時漏寫，已補齊）。
- **R7 Controller 訊息不得硬編碼 CJK**：掃 `Modules/*/Controllers/*Controller.cs`
  去註解後的 `message=`／`AddModelError(`／`TempData[`／`alert(` 行，不得含 CJK
  字面 → 改走 `IStringLocalizer<ProjectZSharedResource>`。R4/R4b 僅掃 js/cshtml，
  本規則補齊 controller 伺服端訊息（en-US 下原樣顯示中文的破口）。既有 PUR/ORD/INV/BAS
  共 32 controller 入 baseline 分批回補；MFG 已在地化、不在 baseline
- **R8 `@Localizer` 無參數佔位符地雷**：掃 `Modules/**/*.cshtml`，「直接輸出且無參數」
  的 `@Localizer["k"]`（後面非 `.Value`/`.Name` 等取原始字串）若其 resx 值含未跳脫
  `{N}`，`IViewLocalizer` 的 `LocalizedHtmlString` 在 `WriteTo` 會立即 `string.Format`
  → `FormatException`、整頁渲染中斷（Phase D 的「途程新增載入失敗」即此因）。
  修法：resx 值用 `{{N}}` 跳脫（給 JS `.replace` 用），或呼叫端帶入參數，或改 `.Value`
- **R9 number input spinner step 一律 `any`**：HTML `<input type="number">` 的 `step`
  **一身兼二職**——同時決定「上下三角形(spinner)每按一次的增減量」與「允許輸入的小數精度」。
  寫死 `step="0.000001"` → spinner 一按即 ±0.000001（使用者觀察到的「不能都增減 1 嗎」症狀）。
  單一 `step` 數值無法同時滿足「spinner ±1」與「可輸入到小數第 N 位」，唯一解是 `step="any"`
  （各瀏覽器於 `any` 下 spinner 預設 ±1，且接受任意小數）；精度改由 **`min`（最小正值）+
  伺服端 `RoundQtyAsync`/`RoundAsync`** 雙重把關。
  - （a）守住**核心產生源**：`UomStepHelper`/`CurrencyStepHelper.GetStep` 必回 `"any"`；
    `uom-step.js applyUomStep`、`currency-format.js applyCurrencyStep`/`currencyStepOf` 必設
    `step="any"`。防「修了 JS 沒同步修 C# helper」的漂移復發（本案 MFG 數量欄位即此破口：
    JS 路徑早已 `any`，但 C# helper 與寫死字面仍 `0.000001`）。
  - （b）掃 `Modules/**/*.cshtml`，不得新增寫死 `step="0.xxx"`；既有入 baseline 分批回補。
  - （c）**數量欄位不得寫死 `step="1"` 或 `min="1"`**（2026-09-11 新增，baseline 另檔 `baseline-R9c.txt`）。
    （b）只掃 `step="0.\d+"`，**對 `step="1"` 是盲的**——而那才是嚴重的那一個：`0.x` 只是 spinner
    不好按，`"1"`／`min="1"` 卻讓瀏覽器原生驗證**直接拒收小數**，小數單位的品號在該畫面上
    根本輸不進去，而畫面上沒有任何訊息指向真正的原因。實地形狀：`SalesReturn`／`GoodsShipment`
    兩支 `_Form` 各 4 處寫死，而兩支 Controller 都呼叫 `RoundQtyAsync`——**伺服端按單位精度收，
    畫面只收整數，兩邊契約相反**。判準只認數量欄位（class 帶 `line-qty`，或 `name` 以 `Qty` 結尾），
    故天數／次數／序號那類整數欄位不受影響；`readonly` 顯示欄位排除。掃描器帶 `probed >= 40`
    自我檢查（比照 R21／R22／R32）：選擇器失效時規則會靜默永遠綠，那比沒有規則更糟。
  - **範圍界線（重要）**：R9 只鎖「該跟隨 UOM/幣別精度」的數量/金額/重量欄位。下列為**刻意**
    UX，留 baseline 視為 won't-fix，**不可**一律 `any`：序號 `step="10"`（不符 `0.x` 故本就
    不觸發）、抽樣數／不良數（計件且與 UOM 無關，見 `baseline-R9c.txt`）、加班半小時 `step="0.5"`、工時 `step="0.01"`（0–24 小時）、稅率/匯率/UOM 換算
    比例（值域 0–1 的比率，±1 會爆量）。新欄位請依語意判斷，勿盲套。
- **R10 placeholder 無硬編碼 CJK**：掃 `Modules/**/*.cshtml`，`placeholder="..."` 之值
  若不以 `@` 開頭（即非 `@Localizer[...]`）且含 CJK → 標記違規。補 R1/R4 盲點：
  `placeholder` 既非 `@Localizer` 顯示字串、也非 dialog JS，en-US 下硬中文 placeholder
  永不切換。baseline：`INV Pwa/Index.cshtml`（整頁無 `IViewLocalizer`，待整頁多語化後一併移除）。
  - ⚠️ **編號沿革**：原 R10 提案為「per-view 欄位名 key 去重」，已於 Phase F2 評估後**撤案不建置**
    （重疊 212 key 係正常設計，見下 F2）。**R10 編號其後改派給本 placeholder 規則**；引用舊文時請注意語意已變。
- **R11 分頁 UI 走共用 partial**（2026-09-11 改寫）：`@model PaginatedList<T>` 的畫面必須同時引用
  `_Pagination`（摘要＋頁碼）與 `_PageSizeSelect`（每頁筆數，住在查詢表單內）；**任何** View 都不得
  自帶 `class="pagination"` 或 `<select name="pageSize"`。
  - ⚠️ **改寫前這條要求的正好相反**：它檢查「畫面內有沒有 `name="pageSize"` 與 `Model.PageIndex`」，
    也就是**每支畫面各自複製一份分頁 markup 才算合格**。86 份複製品的結果是：摘要措辭長出
    zh 4 種／en 9 種，`PageInfo` 這個 key 同時存在 2 參數與 3 參數兩種格式（複製時少印一個數字，
    不會有任何錯誤訊息）。規則把「每個人自己抄一份」定義成了合規。
  - ⚠️ 更值得記的是 baseline 裡那一行 `AccountingPeriod: 缺分頁導航`。那不是格式瑕疵——
    controller 每頁 20 筆、View 也宣告了 `PaginatedList`，但畫面上一個分頁控制項都沒有，
    **使用者翻不到第 2 頁**（一年 12 期，第二年起的會計期間就進不去了）。規則抓到了，
    baseline 把它變成永久容忍。收口時連同 baseline 一起清空。
- **R12 Index UserId 欄位走顯示名查詢**：`Index.cshtml` 出現 `RequestedBy`／`CreatedBy` 等 UserId
  欄位時，必須經 `userNames` 字典（`ViewBag.UserNames`）轉為可讀使用者名稱；直接渲染 UserId 會對
  使用者顯示 GUID（`mfg-domain` §13 MachineStatusOrder 曾發生）。baseline 為空（既有 Index 皆已補）。

---

### Cx 系列 — 補充規則（ConventionTestsCx.cs，因應實際 bug 新增）

> 對應 `SmartFactory.MES.Tests/Conventions/ConventionTestsCx.cs`。
> 每個規則均使用相同的 baseline 機制：初次執行自動建立 baseline（既有違規），
> 之後任何**新**違規立即讓建置失敗。

- **Cx1 ProjectZSharedResource key 完整性**：
  掃描注入 `IStringLocalizer<ProjectZSharedResource>`（alias 通常為 `L`）的 View，
  找出 `L["key"]` 靜態字串 key，驗證兩語系 resx 皆有此 key。
  補 R1 盲點：R1 只掃 per-view resx，此規則掃共用 ProjectZSharedResource。
  baseline-Cx1：34 筆 FIN 模組既有缺漏（待分批回補）。

- **Cx2 crud-panel.js JS 函式 export 一致性**：
  掃 View 中 `onclick/onsubmit` 呼叫的 crud-panel.js 函式集
  （openPanel、submitPanel、closeSidePanel 等），驗證皆在 `crud-panel.js` 的 `window.*` export。
  baseline-Cx2：空（零違規）。
  *防重現*：`closeSidePanel` 未 export 導致「關閉按鈕無效」的 bug 類型。

- **Cx3 Model Guid 預設值陷阱**：
  掃 Model .cs 檔尋找 `string.IsNullOrEmpty(Model.X)` 判 isNew 的屬性名，
  若同一屬性名在某 Model 有 `= Guid.NewGuid()` 預設值 → 標記違規。
  baseline-Cx3：空（零違規）。
  *防重現*：`SpecId = Guid.NewGuid()` 導致 `isNew` 永遠 false → Create 打到 Edit → RecordNotFound。

- **Cx4 Side Panel Partial 不可含 inner offcanvas-header**：
  掃 `_` 開頭的 Partial View，若同時含 `offcanvas-header` 與 `offcanvas-body` class → 標記違規。
  baseline-Cx4：空（零違規）。
  *防重現*：Partial 有自己的 offcanvas-header，與 Index.cshtml 外層 header 疊成雙標題。

- **Cx5 DataAnnotation 屬性不得含硬編碼 CJK**：
  掃 `Models/` 與 `Modules/` 下所有 `.cs`（非 `.cshtml`）檔案，
  找出 `[Required(ErrorMessage = "...中文...")]`、`[StringLength(..., ErrorMessage = "...")]`、
  `[DisplayName("...中文...")]` 等 DataAnnotation 屬性參數含 CJK 字符 → 標記違規。
  baseline-Cx5：1 筆（`UserSettingsViewModel.cs:22`）。
  *防重現*：`Plant.cs` 的 `ErrorMessage = "廠別代碼為必填"` 硬編碼中文，
  Core ↔ ProjectZ `cp` 同步時悄悄覆蓋客製化訊息；
  正確做法：不帶 `ErrorMessage`，讓驗證訊息走 SharedResource resx（R5b 把關多語）。
  > ⚠️ .NET 的 `Directory.EnumerateFiles("*.cs")` 在某些平台也會比對 `.cshtml`；
  > 本規則已加 `Path.GetExtension` 明確過濾，僅掃真正的 `.cs` 檔。

- **Cx6 View HTML 元素不得含硬編碼 CJK**：
  掃描注入 `ProjectZSharedResource` 的 View，找出 `<h1-h6>`、`<label>`、`<th>`、`<td>`、`<span>`、`<p>` 等 HTML 元素文字（不含 `@` 運算式）有 CJK 字符 → 標記違規。
  baseline-Cx6：33 筆 FIN 模組既有硬中文標籤（ARReceipt 核銷區塊、OutsourceReceipt 明細欄 header 等）。
  *防重現*：`APPayment/_CreateForm.cshtml` 的 `核銷應付發票`、`選擇供應商後自動載入未結發票` 等 HTML 標題文字在英文模式永不切換，本規則會抓到此類遺漏。

- **Cx7 Script 區塊 DOM 賦值不得含硬編碼 CJK**：
  掃描注入 `ProjectZSharedResource` 的 View 的 `<script>` 區塊，
  偵測 `innerHTML`/`innerText`/`textContent`/`insertAdjacentHTML` 的賦值字串，
  以及 template literal（ `` ` `` 反引號字串）中含 CJK 字符且未使用 `${_msgs.xxx}` 或 `${window.i18n...}` 變數注入 → 標記違規。
  baseline-Cx7：1 筆（ARReceipt `_CreateForm.cshtml` 的 JS template literal）。
  *防重現*：`APPayment/_CreateForm.cshtml` 的 `container.innerHTML = '載入失敗'` 與動態 table header `` `<th>發票號</th>...` `` 在英文模式永不切換，本規則補 R4b 的 `alert/confirm` 盲點。
  > **修法模式**：在 `<script>` 前用 Razor 預注入 `var _msgs = { key: '@Html.Raw(L["key"].Value)' };`，JS 改引用 `_msgs.key`（已套用於 APPayment）。

### 支柱 4 — 更新產生器規格（讓新畫面天生合規）

更新 `.claude/commands/new-screen-projectz.md`、`new-report.md`、`.claude/LOCAL_RULES.md`
（**皆 ProjectZ 專屬、不在 Core `cp` 清單；不改 `CLAUDE.md`**）：
新畫面**繼承 `ModuleControllerBase`、引入 `_CrudPanelScripts`、Print 套 `_PrintLayoutZ`/`_PrintBase`、JS 用 `window.i18n`**。
70 項手工清單縮為「用基底＋共用 partial」少數幾項 → Vibe Coding 輸出從源頭減少遺漏。

---

## 關鍵檔案（標註 Core/ProjectZ 歸屬）

| 動作 | 檔案 | 歸屬 |
|---|---|---|
| 新增 | `Modules/_Shared/ModuleControllerBase.cs` | ProjectZ |
| 新增 | `wwwroot/js/crud-panel.js`、`Views/Shared/_CrudPanelScripts.cshtml` | ProjectZ |
| 修改（已做） | `wwwroot/js/crud-panel.js` 加 `armValidation`（novalidate + unobtrusive 重 parse） | ProjectZ |
| 修改（已做） | `wwwroot/js/crud-panel.js` `submitPanel` 成功判斷改結構式（修 ORD/INV 面板秒關） | ProjectZ |
| 修改（已做） | `Modules/BAS/BASModule.cs` `LocalizableDefaultMessageProvider` 補隱含必填 | ProjectZ |
| 修改（已做） | `Modules/BAS/BASModule.cs` 新增 `LocalizableDisplayNameProvider`（欄位名在地化） | ProjectZ |
| 修改（已做，全量） | `Resources/SharedResource.{zh-TW,en-US}.resx` 全量回補欄位名（共 229 key，雙語一致，runtime 驗證 PUR/ORD/INV） | ProjectZ |
| 新增 | `Views/Shared/_PrintLayoutZ.cshtml`、`Views/Shared/_PrintBase.cshtml`、`Views/Shared/_JsLocalization.cshtml` | ProjectZ |
| 修改 | 15 個 `Modules/*/Views/*/Print.cshtml`（分批，套 `_PrintLayoutZ`/`_PrintBase`） | ProjectZ |
| 修改 | 40+ `Modules/*/Views/*/Index.cshtml`（分批，引入 `_CrudPanelScripts`） | ProjectZ |
| 修改 | 各 `Modules/*/Controllers/*Controller.cs`（分批，繼承基底） | ProjectZ |
| 修改 | `ProjectZSharedResource.*.resx` / 模組 resx（補 key） | ProjectZ |
| 新增 | `SmartFactory.MES.Tests` 內 R1–R5 慣例測試（獨立資料夾/命名空間，不改 Core 既有測試） | ProjectZ |
| 修改（已做） | `UomStepHelper`/`CurrencyStepHelper.GetStep` 回 `"any"`（min 拆 `PrecisionStep`） | ProjectZ |
| 修改（已做） | `wwwroot/js/uom-step.js`/`currency-format.js` step 一律 `"any"`（R9 核心源） | ProjectZ |
| 修改（已做） | `Modules/MFG/Views/Routing/_Form.cshtml` 7 個數量/工時/率 step 改 `"any"` | ProjectZ |
| 新增（已做） | `SmartFactory.MES.Tests/Conventions` R9 測試 + `baseline-R9.txt`（25 筆分批回補） | ProjectZ |
| 修改（已做） | `wwwroot/js/currency-format.js` 加 `roundInputToCurrency` + 委派收斂（多幣別金額即時依 dp） | ProjectZ |
| 新增（已做） | `SmartFactory.MES.UITests/RoutingFormStepTests.cs`（途程 step="any" 瀏覽器回歸） | ProjectZ |
| 修改 | `.claude/commands/new-screen-projectz.md`、`new-report.md`、`.claude/LOCAL_RULES.md` | ProjectZ |
| 重用（不改） | `Services/PrintHelper.cs`、`Services/CodeEntryDisplayExtensions.cs`、`BASModule` DataAnnotations 設定 | Core/共用 |
| **不碰** | `Program.cs`、`Pages/Shared/_Layout.cshtml`、`Pages/Shared/_PrintLayout.cshtml`、`Data/ApplicationDbContext.cs`、Core `*Controller`、`CLAUDE.md` | Core |

---

## 執行順序（先建機制 → 分批回補）

**Phase A — 建框架（不動既有行為）**
1. `ModuleControllerBase` ＋ `_CrudPanelScripts`/`crud-panel.js` ＋ `_PrintBase`/`_JsLocalization` ＋ `_PrintLayout` 本地化
2. R1–R5 慣例測試（先掛 baseline 允許清單，全綠才併入 CI gate）
3. 更新產生器規格與 CLAUDE.md

**Phase B — 分批回補**（每批：控制器繼承基底 → Index 引入共用 partial → Print 套 `_PrintBase` → JS 改 `i18n` → 補 resx；該批 baseline 移除、測試轉綠）
- 批次順序：PUR → ORD → INV → BAS / MFG

---

## Phase C — MFG 完整在地化 ＋ 管理系統 Core 修根（2026-05-19 完成）

使用者觀察：MFG「製造執行」顯示格式未依本計畫（view 仍硬編碼中文、無 per-view resx），
且與 Core 共用的「管理系統」必填欄位中英顯示亦未處理。經評估後採「Core 修根 + 同步回 ProjectZ」。

### C1. 驗證在地化機制上移至 Core（根因修正）

- 原 `Modules/BAS/BASModule.cs` 內的 `LocalizableDefaultMessageProvider` /
  `LocalizableDisplayNameProvider` / `DataAnnotationLocalizerProvider` /
  `ModelBindingMessageProvider` / `ModelValidatorProviders` 去重——這些是 **MVC 全域**
  設定，原僅存在於顧問模組，導致 **Core 自身（純 Core 部署）的「管理系統」驗證訊息
  在 zh-TW 仍顯示英文屬性名**（Core 無任何 provider、`SharedResource` 僅 21 key）。
- 對策：新增 **Core** `Services/LocalizedValidationExtensions.cs`
  （`AddLocalizedValidation()` 擴充），於 **Core** `Program.cs`
  `AddRazorPages().AddDataAnnotationsLocalization()` 後呼叫。邏輯為 Phase B 已 runtime
  驗證過的同一份程式逐字移植。
- `Resources/SharedResource.{zh-TW,en-US}.resx` 提升為 **Core 主導的超集（249 key）**
  ＝ Core 原 21 + ProjectZ Phase B 229（補回 ProjectZ 漏掉的 Core menu key
  `DeviceStatus`/`ReportingInterface`/`ReportingManagement`）+ 管理系統 Core-model
  欄位名（`Name`/`Category`/`Code`/`NameZh`/`NameEn`/`DocumentType`/`DocumentTypeName`/
  `Email`/`UserName`）+ MFG 欄位名（`RoutingName`/`BatchSize`/`BufferDays`/
  `AvgLeadTime`/`WorkCenterId`/`Note`/`TM_WKS`/`TM_WKO`）。符合 `.gitattributes`
  「`Resources/SharedResource.*.resx` → Core 主導，ProjectZ 不修改」之既定規則。
- ProjectZ 端對齊：`Program.cs` 同步加 `AddLocalizedValidation()`、複製 Core
  `LocalizedValidationExtensions.cs` 與 249-key `SharedResource`、**移除 BASModule
  內重複的 provider 註冊與兩個巢狀 provider 類別**（避免 `ModelMetadataDetailsProviders`
  雙掛）。BASModule 由 213 行縮為 54 行。

> 註：管理系統為 Core `cp` 同步清單檔，**零 Core view/controller 修改**；修正點全在
> Core `Program.cs` + 新增 Core 服務檔 + Core `SharedResource` —— 屬 Core 主體自身缺陷修補，
> 經 Core 同步機制自然流向 ProjectZ。Core 無原生 `required` 問題（mgmt 表單用 `asp-for`，
> 僅 User 密碼 2 處 literal `required`，屬 Core 既有、本案不動）。

### C2. MFG 完整視圖在地化（對齊 PUR 標準）

- 4 個 view（`Routing/Index`、`Routing/_Form`、`WorkCalendar/Index`、`WorkCalendar/_Form`）
  全部硬編碼中文標籤/標題/JS 訊息 → 改 `@inject IViewLocalizer Localizer` + `@Localizer["k"]`，
  新增 8 個 per-view resx（`Resources/Modules/MFG/Views/{V}/{Name}.{zh-TW,en-US}.resx`，
  共 zh/en 各 200 key，鍵集合一致），刪除無對應 view 路徑的 stale stub
  （`_RoutingForm.*`/`_WorkCalendarForm.*`，僅 1 個 "Form" key）。
- **MFG view-specific JS 在地化採 per-view resx + Razor 內聯 `@Localizer`**（建一個 JS
  `L`/`wcL` 物件，server-render 時即依 culture 解析），而非 `window.i18n`/
  `ProjectZSharedResource`——避免把 MFG 專屬 key 汙染全域 JS 字典；R4 僅掃
  `wwwroot/js/*.js`，本作法不違規。格式字串以 `'{0}'` + JS `.replace` 帶入。

### C3. 驗證結果

- `dotnet build`（Core 與 ProjectZ，含 Razor 編譯）0 error
- `dotnet test` R1–R5 全綠（R1 證 MFG `@Localizer` key 雙語齊備；R5 證含 249-key
  `SharedResource` 在內所有 resx pair 鍵集合一致）
- ProjectZ app boot smoke：HTTP 200、Application started，新 Core provider 無 DI 例外

---

## Phase D — MFG 途程/工作日曆畫面修正 ＋ 分頁標準 ＋ R7/R8（2026-05-21 完成）

使用者觀察到 MFG「途程主檔」三項缺陷,逐一修復並回頭補強框架:

### D1. 途程「新增」面板「載入失敗，請重試。」（FormatException）

- 根因:`Routing/_Form.cshtml` 的 JS 模板字串以 `@Localizer["BomTitleFmt"]` 等取值,
  但 `IViewLocalizer` 回傳的 `LocalizedHtmlString` 在 `WriteTo` 時對值立即跑
  `string.Format`;這 4 個 key（`BomTitleFmt`/`JsOpSeqDupFmt`/`JsOpDelConfirmFmt`/
  `JsBomSeqDupFmt`）的值含 `{0}`（原意給 JS `.replace`）、無參數 → `FormatException`
  → Razor 在 `<script>` 中途中斷 → 回應截斷 → 瀏覽器 `fetch` 失敗 → openPanel 顯示
  「載入失敗」。
- 修法:resx 值 `{0}` → `{{0}}` 跳脫,`IViewLocalizer` 輸出回 literal `{0}`,JS
  `.replace('{0}', …)` 照常。**並新增 R8 永久把關此地雷**(見支柱 3)。

### D2. 列表分頁/每頁筆數標準（drift 修正 ＋ 納入框架）

- 現象:Routing/WorkCalendar Index 缺「每頁筆數」選擇器,分頁是舊式（"第 1 / 0 頁",
  無總筆數、無 « ‹ › » 導航）— 與 PUR 標準不一致。
- **分頁/每頁筆數標準**(本就在 `.claude/commands/new-screen.md` 行 312/323,但無 gate
  → 早期手刻畫面漂走):
  - 控制器 Index 收 `int pageSize`、設 `ViewBag.CurrentPageSize`、傳入 `PaginatedList`
  - 查詢列右上 `<select name="pageSize">`（10/20/50/100,`onchange` 即送出）
  - 分頁列「第 X / Y 頁,共 Z 筆」（用 `Model.TotalItems`,在地化 `PageInfo` 帶 3 參數）
    + « ‹ 頁碼 › » 導航,空清單時隱藏
- Routing/WorkCalendar 已回補對齊。**註**:分頁 markup 因每頁路由參數不同（searchText
  vs fromDate/toDate/workCenterId）難做共用 partial,故維持「規格 + 逐畫面套用」,不抽
  共用元件;新畫面由產生器規格保證,既有畫面比照本次手法回補。

### D3. 途程主檔三層改頁簽（UI）

- 原三層（途程主檔／工序明細／工序用料）垂直堆疊於同一面板 → 改 Bootstrap 頁簽:
  Tab1「途程主檔」、Tab2「工序與用料」(工序明細 + 用料)。`_Form` JS（getElementById）
  不受頁簽切換影響;Tab 上以 `opsTabBadge` 顯示工序數。

### D4. MFG controller 訊息在地化（R7 試點）

- MFG `RoutingController`/`WorkCalendarController` 的伺服端訊息（權限拒絕／找不到／
  成功／重複／批次產生）原硬編碼中文,en-US 下原樣顯示 → 改注入
  `IStringLocalizer<ProjectZSharedResource>`,新增 10 個泛用 key
  （`NoCreate/Update/DeletePermission`、`Create/Update/DeleteSuccess`、`DuplicateRecord`、
  `BatchSelectShiftGroup`、`BatchShiftNotFound`、`BatchGenerateDone`）至
  `ProjectZSharedResource.{zh-TW,en-US}.resx`,reuse 既有 `RecordNotFound`。
- **此為 R7 試點**:全 app controller 訊息硬編碼是通病（PUR/ORD/INV/BAS ~497 處）,
  MFG 先清零,其餘 32 controller 入 R7 baseline 分批回補（同 R4b/R5b 模式）。

### D5. 驗證結果

- `dotnet build` 0 error;`dotnet test` 慣例 11 案全綠（R1–R8）
- runtime 實測（authed,zh-TW + en-US）:途程新增面板完整載入（含頁簽）、Create/Delete
  成功並回在地化訊息、找不到回 `RecordNotFound`、列表顯示「第 X/Y 頁,共 Z 筆」+ 導航 +
  每頁筆數選擇器;en-US 同步正確、無原生中文殘留

---

## Phase E — number input spinner step 統一（2026-05-22 完成）

使用者觀察：生產管理（MFG 途程主檔）的數量欄位,上下三角形（spinner）每按一次是
±0.000001 而非 ±1（「之前的模組修過、又復發」）。經查證為典型「同一 UI 契約多個平行
產生源、只修一處、其餘漂移、無建置期把關」樣態。

### E1. 根因（step 一身兼二職 + 三源漂移）

HTML `<input type="number">` 的 `step` 同時決定「spinner 增減量」與「允許輸入精度」。
`step` 由**三個平行來源**產生,當時只修了 JS 一源:

| 產生源 | 修正前 | spinner |
|---|---|---|
| `uom-step.js applyUomStep` | ✅ 已 `step="any"` | ±1 |
| `UomStepHelper.GetStep` / `CurrencyStepHelper.GetStep`（C# 初次 render） | ❌ 回精確小數 | ±0.000001 |
| `currency-format.js applyCurrencyStep`/`currencyStepOf` | ❌ 回精確小數 | ±0.00000001 |
| 視圖內**寫死** `step="0.000001"`（繞過 helper） | ❌ 32 處 | ±0.000001 |

MFG 途程數量欄位屬最後一種（寫死字面、從不經已修好的 JS）→ 復發。

### E2. 對策（核心三源回 `any`,精度靠 min + 伺服端 round）

- `UomStepHelper.GetStep`/`CurrencyStepHelper.GetStep` 改回 `"any"`;`UomStepHelper`
  另拆私有 `PrecisionStep` 供 `GetMinPositive`（min 仍須精確值,`min="any"` 非法）。
- `currency-format.js`:`currencyStepOf`/`applyCurrencyStep` 改回 `"any"`(一併修好 6 個
  PUR/ORD 表單以 `${window.currencyStepOf(...)}` 動態建列的金額欄位,使同表單初次/動態列
  行為一致)。`uom-step.js applyUomStep` 維持 `"any"`,並補註 `uomStepOf` 是 min/精度值非
  spinner step。
- MFG `Routing/_Form.cshtml` 7 個寫死小數 step 的數量/工時/比率欄位（BatchSize、
  AvgLeadTime、opQtBor、opSetup、opUnit、bomQty、bomScrap）改 `step="any"`;`ScrapRate`
  經查為**百分比**（`損耗率 %`,±1 = ±1%,合理）。序號 `step="10"`、`BufferDays`(整數,無
  step) 不動。

> 因 C# `GetStep` 為中央產生源,改一處即連帶修好 ORD `SalesOrder`/`InternalOrder`、
> INV `CrossPlantTransfer`、BAS `Product`(經 `UomStepHelper.GetStep`)及 6 個 currency 表單
> server-render 欄位——零散表單免逐一改。

### E3. R9 建置期把關（防再漂）

新增 **R9**(見支柱 3):(a) 守核心產生源 `GetStep`/JS 必為 `any`;(b) 掃 `.cshtml` 禁新增
寫死 `step="0.xxx"`,既有違規入 `baseline-R9.txt`,**以檔案路徑為鍵（同 R4b/R7,不帶行號——
行號會隨無關編輯漂移而使 baseline 失準）**。其中**待回補的數量/金額/重量/容量**已於 E6 全數改
`step="any"`,baseline 收斂至 **7 檔刻意 won't-fix**(稅率/匯率/UOM 換算比例、加班/工時——
套 ±1 反而錯,保留精確 step;baseline 檔內已分類註解)。
> **更新（2026-07-16 複查）**：`baseline-R9.txt` 現為 **9 檔**（FIN 模組後續建置新增 `APPayment/_CreateForm.cshtml`、`ARReceipt/_CreateForm.cshtml` 兩筆比率/費率類 won't-fix），機制運作正常，僅此處數字未同步，不代表回歸。

### E4. 驗證結果

- `dotnet build` 0 error;`dotnet test` 慣例 12 案全綠（R1–R9,R9 baseline 自動建立 25 筆）。
- MFG `Routing/_Form.cshtml` 已無任何 `step="0.x"`;helper/JS 核心三源契約由 R9(a) 釘住。
- 新增瀏覽器端回歸 `SmartFactory.MES.UITests/RoutingFormStepTests.cs`（編譯通過）:
  斷言途程新增面板 7 個數量/工時/率欄位 `step="any"`、序號欄維持 `step="10"`。
  執行需 live app（`run_ui_tests.sh` 自起 :5000,或起 app 後 `dotnet test UITests`;
  注意 `appsettings.test.json` BaseUrl 與 memory 記「:5110」之既有 port 漂移）。

### E5. 金額欄位「依幣別小數位數」評估與即時收斂（多幣別表單）

評估結論:金額的「依幣別 dp 作業」**本就三處在線**——server render `"F"+cdp`、client
`calcLine` 用 `currencyDp`+`roundHalfAway`、server save `RoundAsync(currencyCode, amount)`;
R9 的 `step="any"` 只改 spinner、**未動精度**（且面板已 `novalidate`,精確 step 本就不再
即時攔阻超精度輸入,一律 server 兜底）。唯一殘留 cosmetic 縫:使用者手鍵超精度單價時,
**輸入框顯示值**未即時收斂(計算金額已收斂、儲存正確)。

對策(中央、零 per-form markup):`currency-format.js` 新增 `roundInputToCurrency(el,code)`
＋一個 capture 階段委派 `change` 監聽——當 `.line-price` 變更且**該列存在 `.line-currency`**
（= per-row 多幣別）時,依該列幣別 dp 收斂顯示值,並於該欄 inline `onchange="calcLine"` 前
執行(使顯示單價與計算金額同以收斂後單價為準)。`if (!ccySel) return` 自動涵蓋
PUR×3 + ORD `SalesOrder`,略過無 per-row 幣別的 ORD `SalesReturn`/`GoodsShipment`
(其精度仍由 calcLine + 伺服端 `RoundAsync` 保證)。spinner 維持 ±1。

### E6. 全模組回補 + 幣別欄序 + 零 Core 驗證 + 產生器規格（2026-05-22）

依使用者三項要求收尾:

- **零 Core 影響(已驗證)**:本案所有變更檔(`UomStepHelper`/`CurrencyStepHelper`、
  `uom-step.js`/`currency-format.js`、MFG/ORD/BAS `*.cshtml`、`SmartFactory.MES.Tests`、
  UITests)經比對 **Core repo(`../SmartFactory.MES`)皆不存在**——全屬 ProjectZ 顧問層,
  `git merge upstream/main` 無可覆蓋對象、零衝突。未動任何 Core `cp`/`merge=ours` 檔。
- **R9 全量回補(baseline 25→10)**:把寫死 `step="0.x"` 的**真量值**欄位改 `step="any"`——
  `BAS Product`(NetWeight/GrossWeight 重量)、`BAS Container`(MaxCapacity 容量)、
  `ORD SalesOrder/SalesReturn/GoodsShipment`(JS 動態列 UnitPrice/LineAmount/TaxAmount/
  TotalAmount,共 12 欄;PUR 早經 helper/`currencyStepOf` 收斂)。baseline 餘 7 檔刻意
  won't-fix(見 `baseline-R9.txt` 分類註解;R9(b) 以檔案路徑為鍵,同 R4b/R7)。
- **幣別欄移到單價前**:多幣別表單(`PUR PurchaseOrder/GoodsReceipt/GoodsReturn` +
  `ORD SalesOrder`)的 `CurrencyCode` 欄(thead `<th>`、server `@for` 列、JS addLine 模板
  三處同步)移到 `UnitPrice` 之前——先選幣別再輸單價,亦讓 E5 即時收斂一開始就取到正確 dp。
- **產生器規格(支柱 4)**:`new-screen-projectz.md` 第 6/7 節加 R9 規則框、修正既有
  `step="0.x"`/`moneyStep` 範例為 `step="any"`、補多幣別欄序與即時收斂慣例;`LOCAL_RULES.md`
  建置期把關清單補 R7/R8/R9。`CLAUDE.md` 屬 Core `cp` 同步**不改**,規則改落 ProjectZ 專屬檔。
- 驗證:`dotnet build`(含 Razor 編譯,確認 4 表欄序調整無破損)0 error;`dotnet test`
  慣例 12 案全綠;全庫 `step="0.x"` 殘留 = 10(= baseline won't-fix)。

---

## Phase F — 動態列驗證 ＋ key 去重 ＋ 基底彈性（規劃中，md 先行）

> 來源：對 consistency-framework 的第二層檢視（2026-05-25）。本階段**先寫規格、後改程式**：
> 程式／測試／既有檔回補於本節定稿後分批進行。F1/F2 各自新增一條慣例測試，既有違規比照 R9
> 以**檔案路徑為鍵**入 baseline 分批收斂（勿帶行號——行號會隨無關編輯漂移而使 baseline 失準）。

### F1. 動態子表驗證重註冊（新增 R6c）

- **現況缺口（已驗證）**：`crud-panel.js` 的 `armValidation` 只在 `openPanel`／`submitPanel`
  注入 innerHTML 後各跑一次，且**未對外曝露**（不在 `window.*` export 清單）。面板開啟後用 JS
  `append` 的明細列（如 ORD `SalesOrder` 動態加列），其 `data-val-*` 不會被 unobtrusive 重新 parse。
- **嚴重性界定（重要）**：這是 **UX 一致性**缺口、**非正確性破口**。支柱 1b-3 刻意全表單
  `novalidate` ＋ 伺服端兜底（驗證失敗回 `_Form` partial 顯示在地化錯誤）；動態列送出後伺服端
  照樣擋下。缺的只是「動態列的即時前端內聯紅字」（靜態欄位有、動態列要等一次 round-trip）。
- **對策**：
  1. `crud-panel.js` 曝露 `window.armValidation(formOrContainer)`（沿用既有扁平 export 風格，
     **不另造** `window.MES` 命名空間）。
  2. 產生器規格（`new-screen-projectz.md` §9）明訂：凡 JS `append` 新增 input 列者，插入 DOM 後
     **必須**呼叫 `window.armValidation(form)`。
  3. **R6c 慣例測試**：掃 `Modules/**/*.cshtml`，`<script>` 內出現 `appendChild`／
     `insertAdjacentHTML`／`insertRow`／`cloneNode` 等動態建列樣態且含表單輸入者，必須出現
     `armValidation(` 呼叫；既有違規入 baseline 分批回補。
- **回補結果（2026-05-25，`dotnet test` 13 案全綠）**：baseline 18 → **1**。
  - R6c 接受**兩種**合規重註冊 API：crud-panel.js 的 `armValidation(`、BAS legacy 面板的 `parseModuleValidation(`。
  - 已回補 17 檔：
    - PUR×4、ORD×4、INV×8（addLine/動態列 append 後加 `window.armValidation(form)`）。
    - `BAS WorkCenter/Index`（動態機台列 append 後加 `window.parseModuleValidation()`）。其面板走 BAS legacy
      `#crudPanelBody` + `module-validation.js`（未載 crud-panel.js），而 `module-validation.js` 早已對外曝露
      `window.parseModuleValidation`（重 parse 整個 `#crudPanelBody` 的 form）→ 用既有 API 一行修、零波及；
      **未**改共用 observer 為 `subtree:true`（會波及 16 支載入該檔的畫面、引發重 parse 風暴）。
  - 餘 1 筆 won't-fix（見 `baseline-R6c.txt`）：`INV WarehouseOps/Index`——誤報，唯一動態 append 是 toast
    通知（`document.body.appendChild(t)`），非驗證表單列。

### F2. per-view 欄位名 key 去重（已評估 → 撤案，2026-05-25）

> ⚠️ **編號注意**：本節「R10」指**此撤案的原始提案**。撤案後 **R10 編號已改派**給現役的
> 「placeholder 無硬編碼 CJK」規則（見支柱 3 R10）。本節保留作歷史紀錄，與現役 R10 無關。

- **原始提案（原 R10）**：加慣例測試，禁 per-view resx 欄位名 key 與全域 `SharedResource` 重複，引導重用。
- **實測否決（先量後決，依計畫）**：
  - per-view resx 共 **88 檔、union 607 key**；全域 `SharedResource` **256 key**；**exact 重疊 = 212 key**。
  - 此重疊是**正常設計、不可消除**：view 的 `@Localizer["OrderDate"]` 由 per-view `IViewLocalizer` 解析，
    與 DataAnnotation 驗證訊息用的全域顯示名是**不同 localizer**，兩邊各自都需要該 key；無法讓 view 標籤
    改去全域解析。R10 在此定義下會把 212 個正常 key 全判為違規。
  - 全域字典正規化（小寫＋去非英數）後**零大小寫/分隔變體**（已乾淨；harvest 階段已取多數值收斂）。
    真正想防的「語意近義」（`CreateDate` vs `CreatedDate`）需編輯距離/詞幹比對，誤判率高、且現況無此例。
- **結論：不建置此規則**。欄位名「在全域存在」已由既有 **R5b** 把關；「優先重用全域顯示名」改列為產生器
  規格指引（`new-screen-projectz.md` §9）。未來若真出現近義碎裂，再評估「全域內編輯距離 ≤1」的輕量守則
  （baseline 起點為 0）。

### F3. `ModuleControllerBase` helper 改 `virtual`

- 現行 5 個 helper（`PrepareIndexPermissionsAsync`／`JsonOk`／`JsonFail`／`ValidationPartial`／
  `PreparePrintViewBagAsync`）與 `IsAjax` 皆 `protected` **非 virtual**。改為 `virtual` 提供非標準
  畫面（Dashboard／精靈／二段存檔）漸進覆寫的彈性，導入成本極低、不動既有行為。
- **不誇大為「防 AI 放棄繼承」**：此基底是薄的正交 helper（非 CRUD 生命週期框架），Dashboard 等
  照樣可繼承後單純不用這些 helper；且在地化底線本就由 **R7 掃所有 `*Controller.cs`（不論是否繼承
  基底，見 `ConventionTests.cs` R7）** 守住。virtual 只是便宜保險。

### F4. Roslyn 升級備忘（不採 git-hook 過渡）

- 支柱 3 已記「熱點規則日後可升級為 Roslyn analyzer」。補充：改 Roslyn 不只「提早到編譯期」，更能用
  **語意模型精準鎖定字串字面 vs 註解**，順帶消除現行 R7/R8「正則去註解後掃 CJK」的脆弱
  （`ConventionTests.cs` 手動 `Regex.Replace` 剝註解）。
- **git-hook 過渡不採**：對「AI 寫碼」工作流槓桿低（AI 本就頻繁 `dotnet build`，真正槓桿在編譯期、
  非 commit 期）。

---

---

## 測試架構：三層金字塔

```
            ┌───────────────────────────────┐
            │  Layer 3 — E2E（執行期）      │  SmartFactory.MES.UITests/
            │  Playwright 瀏覽器全端測試    │  需 live app（:5110）
            └───────────────────────────────┘
          ┌─────────────────────────────────────┐
          │  Layer 2 — Convention（建置期）     │  SmartFactory.MES.Tests/
          │  靜態分析：R0–R12、Cx1–Cx9         │  dotnet test（不需 app）
          └─────────────────────────────────────┘
        ┌─────────────────────────────────────────┐
        │  Layer 1 — Unit / Integration（建置期） │  SmartFactory.MES.Tests/
        │  Controller / Service 單元測試           │  dotnet test（不需 app）
        └─────────────────────────────────────────┘
```

### Layer 2 — Convention 規則總覽

| 規則集 | 檔案 | 說明 |
|-------|------|------|
| R0 | `ConventionTests.cs` | meta：文件↔測試對帳（每條現役規則須登錄〈規則索引〉表） |
| R1–R12（含 R4b/R5b/R6b/R6c/R6d） | `ConventionTests.cs` | 在地化、JS/placeholder CJK、表單驗證 span、UOM step、分頁 UI、UserId 顯示名 |
| R13–R36 | `ConventionTests.cs` | 授權閘門（R13／R23–R26）、金額與時間語意（R17／R21／R22）、面板與表單陷阱（R16／R18–R20／R32 面板契約跨檔配對）、清單畫面一致性（R11 分頁 partial／R33 每頁筆數預設值／R34 操作欄圖示+文字）、廠別範圍（R30 儲位查詢）、文件↔實作對帳（R27 用字、R28 手冊點擊目標、R29 待決項聚合、R31 手機畫面手冊覆蓋、R35 §12 總表↔§11 手抄聚合對帳）、排程時刻的時區語意（R36）。**本列 2026-08-13 補**——這張概覽表自 R13 起就一直沒跟上，而〈規則索引〉是全的，正好是 R29 在講的同一種病 |
| Cx1–Cx9 | `ConventionTestsCx.cs` | 實際 bug 回補規則（i18n key 缺漏、JS export、Guid 預設值、雙 title、DataAnnotation CJK、HTML 元素硬中文、JS innerHTML 硬中文、inline handler 棘輪、JS i18n 白名單） |

> **完整現役清單與逐條說明見支柱 3 的〈規則索引〉表**（由 R0 自動對帳）。本表僅為分檔概覽。
> 所有 Convention 規則都使用 baseline 機制：首次建立 baseline（既有違規），之後任何**新**違規立即讓 build 失敗。
> 分批回補：從 baseline 刪除已修正的行，直到歸零。

### Layer 3 — E2E 測試架構

#### 專案結構

```
SmartFactory.MES.UITests/
├── appsettings.test.json     ← BaseUrl（:5110）、Headless 設定
├── UiTestFixture.cs          ← 共用 Playwright Browser + Context（[Collection("UI Tests")]）
├── CrudSmokeTestBase.cs      ← ★ Smoke 測試模板（本節說明）
├── Pages/
│   ├── BasePage.cs           ← 基底導航
│   ├── LoginPage.cs          ← 登入 helper
│   └── CrudPanelPage.cs      ← ★ 共用側欄操作（openPanel / submitPanel / Toast 斷言）
├── QCR/
│   ├── DefectCodeSmokeTests.cs
│   ├── InspectionSpecSmokeTests.cs
│   ├── NcrSmokeTests.cs
│   └── InstrumentSmokeTests.cs
├── INV/
│   └── InventoryCountSmokeTests.cs
├── LoginTests.cs             ← 登入功能測試
└── RoutingFormStepTests.cs   ← MFG 途程 step="any" 瀏覽器回歸（R9 執行期守門）
```

#### 兩種測試風格

**風格 A：CrudSmokeTestBase（廣度 Smoke，每畫面 15 行）**

繼承 `CrudSmokeTestBase` 只需覆寫 4 個抽象成員，自動獲得 **6 個標準測試**：

```csharp
public class DefectCodeSmokeTests : CrudSmokeTestBase
{
    public DefectCodeSmokeTests(UiTestFixture fixture) : base(fixture) { }
    protected override string IndexPath          => "/DefectCode";
    protected override string NewButtonSelector  => "button[data-url$='/DefectCode/Create']";
    protected override string PanelReadySelector => "input[name='Code']";
    protected override async Task FillRequiredFieldsAsync(IPage page, string s)
    {
        await page.FillAsync("#sidePanelBody input[name='Code']", $"E2E-{s}");
        await page.FillAsync("#sidePanelBody input[name='Name']", $"E2E 測試 {s}");
    }
}
```

自動產生的 6 個測試：

| 測試 | 對應功能清單 |
|-----|------------|
| `Index_PageLoads_NoJsError` | Index 無 JS error |
| `NewPanel_Opens_HasSingleTitle_NoDoubleHeader` | 無雙標題（Cx4 執行期驗證） |
| `NewPanel_EmptySubmit_ShowsValidationError_StaysOnPanel` | 空白驗證留在面板 |
| `NewPanel_CloseButton_ClosesPanelSuccessfully` | 關閉按鈕有效（Cx2 執行期驗證） |
| `NewPanel_FillAndSave_ShowsSuccessToast` | 儲存 → Toast |
| `NewPanel_SaveSuccess_RefreshesList_PageStillLoaded` | 儲存後刷新列表 |

**風格 B：/webapp-testing（深度，依需求手工撰寫）**

見 `.claude/commands/webapp-testing.md`。適合高頻/高風險畫面補充搜尋、編輯、刪除、
簽核流程、匯出等完整情境測試。

#### 執行方式

```bash
# 終端機 1：啟動應用程式
dotnet run --project /home/dev/ProjectZ/SmartFactory.MES.csproj --urls http://localhost:5110

# 終端機 2：跑所有 E2E 測試
dotnet test /home/dev/ProjectZ/SmartFactory.MES.UITests/ -c Debug

# 只跑特定模組
dotnet test --filter "QCR"

# 只跑特定畫面（Smoke 模板的 6 個測試）
dotnet test --filter "DefectCode"
```

> ⚠️ UITests 需要 live app 在 `:5110`，`dotnet test` 單獨執行不含 app 啟動。
> CI 中跑 E2E 需在 pipeline 先啟動 app。

#### 現有 Smoke 覆蓋清單

| 畫面 | 模組 | 測試檔案 |
|-----|------|---------|
| DefectCode（不良項目代碼） | QCR | `QCR/DefectCodeSmokeTests.cs` |
| InspectionSpec（檢驗規範） | QCR | `QCR/InspectionSpecSmokeTests.cs` |
| Ncr（不合格品單） | QCR | `QCR/NcrSmokeTests.cs` |
| Instrument（量測儀器） | QCR | `QCR/InstrumentSmokeTests.cs` |
| InventoryCount（盤點清冊） | INV | `INV/InventoryCountSmokeTests.cs` |

---

## 驗證方式

- `dotnet build` 通過；`dotnet test`：R1–R5 隨每批回補由紅轉綠（baseline 逐批縮小）
- `dotnet run`，以 `/UserSettings/SetLanguage?culture=en-US` 切換語系：
  - 開啟已回補的 Print 視圖，zh-TW / en-US 標題、區塊、簽核、按鈕皆切換
  - 觸發必填驗證錯誤與一個 JS `confirm`（送審/刪除），兩語系訊息皆正確
  - **驗證泡泡專項**：en-US 模式於 PUR 新增單開 Side Panel，直接按 Save 不選供應商，
    應顯示 unobtrusive 英文訊息（如 "The SupplierCode field is required."），
    **不得**出現瀏覽器原生中文泡泡「請選取一個清單中的項目。」；
    zh-TW 模式同步顯示「『供應商』為必填欄位。」（含 bool/隱含必填欄位）
- 既有畫面回歸：跑 `SmartFactory.MES.UITests`（`run_ui_tests.sh`）確認 AJAX 面板/分頁/匯出未受影響
- 零衝突檢查：`git status` 確認未改任何 Core 檔（`Program.cs`、`Pages/Shared/_Layout.cshtml`、
  `Pages/Shared/_PrintLayout.cshtml`、`Services/PrintHelper.cs`、`Data/ApplicationDbContext.cs`、
  Core `*Controller`、`CLAUDE.md`）→ 模擬 `git merge upstream/main` 應無衝突
  - **Phase C 例外（刻意且正確）**：`Program.cs` / `Services/LocalizedValidationExtensions.cs` /
    `Resources/SharedResource.*.resx` 的修改是在 **Core repo 內**進行的根因修補
    （非 ProjectZ 側改 Core 檔），經既定 Core→部署同步規則（`.gitattributes`：Program.cs
    接受 Core 更新、SharedResource Core 主導）流向 ProjectZ；ProjectZ 側僅做對齊複製，
    不產生 merge 衝突
