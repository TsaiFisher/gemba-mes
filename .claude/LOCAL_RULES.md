# ═══════════════════════════════════════════════════════════
# ProjectZ 專屬規則（Core 同步時不得修改以下內容）
# ═══════════════════════════════════════════════════════════

> 🚨 **AI 助理請於每次工作前讀完本檔**
> 本檔含 ProjectZ 在地化的禁/允區界線、模組 SOP、一致性框架摘要——
> `CLAUDE.md` 含 ProjectZ 補充內容，同步 Core 時必須逐段保留，
> **凡 `CLAUDE.md` 與本檔衝突，以本檔優先**。
>
> 必讀時機（任一情境觸發）：
> - 新增或修改任何 Controller / View / Migration
> - 新增模組、新 Entity、或新 MenuKey
> - 「Core → ProjectZ 同步」相關操作
> - 一致性框架（CRUD 面板、多語、列印）相關開發

## GitHub 更新工作流程（upstream merge）

ProjectZ 為 Core 的 fork，Core 設為 `upstream` remote。

```bash
# 一次性設定（已完成；新公司專案開新環境時也只跑一次）
git remote add upstream <SmartFactory.MES-github-url>

# 每次 Core 有更新時執行——在同步分支上做，經 PR 合併，不直接推 main
git switch -c chore/sync-core-YYYYMMDD origin/main
./update-core.sh                 # fetch → merge → 建置 → 測試 → snapshot → 漂移守門 → 記錄 .core-version
# .gitattributes 會自動保留 ProjectZ 版本的衝突檔案（merge=ours）——它會靜默丟掉 Core 的改動，
# 逐檔確認被保留的檔案是否漏掉 Core 的安全修正
git push -u origin chore/sync-core-YYYYMMDD
gh pr create --repo TsaiFisher/ProjectZ ...   # CI 通過後以 scripts/safe-merge.sh 合併

# 自己的變更
git add ... && git commit -m "..."
git push origin <branch>         # 永遠推 origin（自己的 repo），不推 upstream
```

> ⚠️ 2026-11-06 更正：本段原本示範 `git merge upstream/main` 之後直接 `git push origin main`。
> Free 方案私有庫沒有 branch protection，直推 main 會跳過 PR 的完整 CI 與 safe-merge.sh 的 job 層檢查，
> push-to-main 那一輪變成唯一閘門。同步一律走分支＋PR。

### 選擇性同步（只採用 Core 的某幾支 PR）

不做全量 merge、只逐檔採用 Core 某支 PR 時（例如 2026-11 的資安修正 Core #68／#69）：

1. 以 `git diff --name-only <該 PR 前的 Core main> <合併後的 Core main>` 取得**完整**檔案清單，
   逐檔決定採用或不採用（不採用的寫進 `scripts/core-behind-acknowledged.txt` 並附理由）。**文件也算**——
   2026-11-06 只同步了 #69 的程式檔、漏了它改的 `deploy/` 文件，邊界 CI 因此紅。
2. `.core-version` 的 `LAST_SYNC*` 保持最後一次全量同步，選擇性採用另記 `LAST_SECURITY_SYNC*`。
3. commit 之後、push 之前跑 `bash scripts/check-core-drift.sh`（它比對 HEAD，不比對工作樹）。

### 邊界 hook 必須啟用

`git config core.hooksPath .githooks` 啟用邊界 pre-commit。個人環境若把 `core.hooksPath` 設成別處
（例如 `/dev/null`），提交時至少用 `git -c core.hooksPath=.githooks commit …` 明確啟用；
**hook 沒跑＝邊界未驗證**，不得記成通過，也不得用 `--no-verify` 繞過。

### 一次性祖先建立（若 repo 原以「複製檔案 + 新 git init」建立）

新公司專案若**非**以 `git clone` Core 建立，而是「複製 Core 檔案 + 全新 `git init`」起步，
則本機與 Core 無共同祖先 → `git merge upstream/main` 會 `fatal: refusing to merge unrelated histories`。

修復配方（**每個專案 repo 一次性**，現有 ProjectZ 已執行於 `19ffbee`）：

```bash
git remote add upstream <core-repo-url>
git fetch upstream
git merge -s ours --allow-unrelated-histories upstream/main \
  -m "建立與 Core(upstream) 的共同祖先：本專案為 Core fork+客製"
git push origin main
```

- `-s ours` 是關鍵：**ProjectZ 樹完全不變、零衝突**，僅在歷史圖記錄 Core 為第二親代
- 等同宣告「現狀＝雙方議定基準」，自此 `git merge upstream/main` 走正常 3-way 合併
- 不可改用 `--allow-unrelated-histories` 預設策略：會對數百個無關歷史檔產生大量假衝突

### merge 後必須驗證，不能直接視為完成

使用 `update-core.sh` 完成明確的主專案／測試專案建置、單元與整合測試、雙 DbContext snapshot 檢查與漂移守門。
任何必要檢查失敗都不得更新 `.core-version`。`merge=ours` 丟棄的安全修正須逐項記錄採用結果。
`Program.cs`、`CLAUDE.md`、`Pages/Shared/_Layout.cshtml` 含客製，逐段合併，不整檔 cp。

---

## 🚨 merge=ours 會靜默丟掉對方的改動（2026-09-08 實際踩到）

`.gitattributes` 裡標了 `merge=ours` 的檔（22 個，含 `Program.cs`、`ci.yml`、
`wwwroot/mobile/home.html`、各 `.md` 規格、EF snapshot…），在**兩邊都改過同一個檔**時
會呼叫 ours driver，結果是**完整保留我們這側、把對方的改動整個丟掉**——
不列入衝突、不印警告，git 甚至照樣印 `Auto-merging <file>`，看起來一切正常。

**實例**：把 main（剛合入 PR #138）併進功能分支時
- `wwwroot/mobile/home.html` — main 改了 16 行，一行都沒收
- `.claude/specifications/mobile-pwa.md` — main 改了 50 行，一行都沒收

症狀是 #138 剛加好的「待我簽核」入口在手機首頁**直接消失**，而**全部測試照樣全綠**
（沒有任何東西斷言那個入口存在）。

### 風險面比想像中窄，但也因此更難發現

**只有「兩邊都改過」的檔會中招。** 只有對方單邊改過的不會——沒有衝突就不會呼叫 driver，
git 會直接採用對方的版本（同一次合併裡 `Views/Approval/_DocDetailsUrls.cshtml` 就正常收到了）。

### 兩道守門

| 守門 | 擋什麼 | 何時跑 |
|---|---|---|
| `scripts/check-ours-drops.sh` | 合併後列出被丟掉的檔 | `update-core.sh` 合併後；CI 的 `validate-boundaries` 掃 PR 內每個合併 commit |
| `ControllerDependencyResolutionTests` | Core 新增的 DI 註冊被 `Program.cs` 的 merge=ours 吃掉 | 整合測試 |

`check-ours-drops.sh` **分兩種模式**，因為 merge=ours 的用途本來就是「Core 更新時保留客製」：
- 對方是 **Core**（`upstream/main` 的祖先）→ 列出來給人看，`exit 0`
- 對方是**本專案自己的 ref** → `exit 1`，那一定是意外（兩邊都是本專案的內容，丟掉哪邊都是資料遺失）

### 手動修法

```bash
git show <base>:<file>   > /tmp/b
git show <ours>:<file>   > /tmp/o
git show <theirs>:<file> > /tmp/t
git merge-file -L ours -L base -L theirs /tmp/o /tmp/b /tmp/t && cp /tmp/o <file>
```

> ⚠️ **`Program.cs` 特別要注意**：Core 新增的 `AddScoped<IFoo, Foo>()` 會在這裡被丟掉，
> 而症狀是「建置成功、測試全綠，但某一頁一開就 500」——MVC 是收到請求才建控制器的，
> 沒有測試打得到的頁面就永遠不會有人發現。`ControllerDependencyResolutionTests` 補的正是這一格。

---

## Core → ProjectZ 同步規則

同步時依下表逐檔判斷，**不可整目錄 cp**。

### ✅ 可直接覆蓋（與 Core 完全一致，無客製化內容）

| 來源（SmartFactory.MES/）| 目的（ProjectZ/）|
|--------------------------|-----------------|
| `Controllers/CodeEntryController.cs` | `Controllers/CodeEntryController.cs` |
| `Controllers/PlantController.cs` | `Controllers/PlantController.cs` |
| `Controllers/OrganizationController.cs` | `Controllers/OrganizationController.cs` |
| `Controllers/RoleController.cs` | `Controllers/RoleController.cs` |
| `Controllers/UserController.cs` | `Controllers/UserController.cs` |
| `Controllers/ApprovalTemplateController.cs` | `Controllers/ApprovalTemplateController.cs` |
| `Controllers/DocumentApprovalConfigController.cs` | `Controllers/DocumentApprovalConfigController.cs` |
| `Views/CodeEntry/_CodeEntryForm.cshtml` | `Views/CodeEntry/_CodeEntryForm.cshtml` |
| `Views/Approval/PendingIndex.cshtml` | `Views/Approval/PendingIndex.cshtml` |
| `Views/Approval/AllRequests.cshtml` | `Views/Approval/AllRequests.cshtml` |
| `Services/ICodeEntryReferenceChecker.cs` | `Services/ICodeEntryReferenceChecker.cs` |

> 同步指令範本：
> ```bash
> CORE=../SmartFactory.MES
> cp $CORE/Controllers/CodeEntryController.cs ./Controllers/CodeEntryController.cs
> cp $CORE/Views/Approval/PendingIndex.cshtml ./Views/Approval/PendingIndex.cshtml
> cp $CORE/Views/Approval/AllRequests.cshtml ./Views/Approval/AllRequests.cshtml
> # ... 其餘同上表
> ```

### ❌ 禁止覆蓋（ProjectZ 客製化，覆蓋即遺失）

| 檔案 | 原因 |
|------|------|
| `.claude/LOCAL_RULES.md` | 本檔，ProjectZ 專屬規則 |
| `Views/Approval/_DocDetailsUrls.cshtml` | 模組 URL 映射（PUR+BAS+CodeEntry），Core 版本為空 |
| `Program.cs` | ProjectZ 已整合 BASModule 等顧問模組 |
| `Data/ApplicationDbContextExtensions.cs` | BAS Entity 的 DbSet 與 Fluent API |
| `Modules/BAS/` | 整個 BAS 模組目錄 |
| `Modules/PUR/` | 整個 PUR 模組目錄 |
| `Resources/ProjectZSharedResource.*` | ProjectZ 專案 MENU 多語＋一致性框架共用字串 |
| `Resources/ProjectZValidationResource.*` | 顧問模組 Model/VM 屬性名 → 中英欄位名（驗證訊息 `{0}` 來源，R5b 把關） |
| `Services/ProjectZMenuContributor.cs` | ProjectZ MENU 定義 |
| `Modules/_Shared/` | 一致性框架共用基底（`ModuleControllerBase`） |
| `Views/Shared/_CrudPanelScripts.cshtml`、`_JsLocalization.cshtml`、`_PrintLayoutZ.cshtml`、`_PrintBase.cshtml` | 一致性框架共用 partial／列印樣板 |
| `wwwroot/js/crud-panel.js` | 一致性框架共用 CRUD 腳本 |
| `SmartFactory.MES.Tests/Conventions/` | 一致性框架建置期把關（R1–R5）＋ baseline |

### ⚠️ 需手動比對合併（Core 有更新但 ProjectZ 亦有差異）

| 檔案 | 需合併的差異 |
|------|-------------|
| `appsettings.json` | Core 新增設定需手動移植，但 DB/JWT 等連線設定是 ProjectZ 自己的 |
| `Views/Shared/_Layout.cshtml` | Core 有版面更新時，需保留 ProjectZ 的模組入口連結 |

---

## 畫面產生規範（ProjectZ 覆蓋）

> ⛔ **AI 禁止事項（違反即視為流程錯誤）**：
> - 禁止在執行 new-screen skill 之前輸出任何分析文字、規劃摘要或問題
> - 禁止先詢問「要從哪個部分開始？」或「確認範圍後再進行」
> - 禁止在開始實作前未先以 `find` / `grep` 確認畫面是否已存在
>
> **正確流程**：
> 1. 先 `find Modules -name "*{KEY}*"` 確認畫面是否已存在
> 2. 不存在 → 立即進入 new-screen Step 1（詢問 Y/N 自動模式）
> 3. 已存在 → 評估現有畫面與規範的差距，直接列出需修改的項目

**當使用者要求產生任何新功能畫面時，無需手動呼叫 `/new-screen`，
AI 必須同時套用以下兩份規格（缺一不可）：**
1. `.claude/commands/new-screen.md` — Core 基礎規格（與 Core 同步，不得修改）
2. `.claude/commands/new-screen-projectz.md` — ProjectZ 補充規格（覆蓋 Core 的對應段落）

> 兩份規格衝突時，`new-screen-projectz.md` 的規則優先。

規格涵蓋（缺一不可）：
- 查詢列（含 `searched=true` hidden input）
- 分頁（`PaginatedList<T>`，首/上/頁碼/下/末導航）
- 欄頭排序（`sortOrder` ViewBag + sort icon）
- 多廠別過濾（`PlantId` 過濾與 POST 賦值）
- 多語（4 個 resx，key 必須與 View 完全一致）
- Side Panel offcanvas + Success Toast + Anti-Forgery token
- `submitPanel(form, submitter)` 含 `fd.set(submitter.name, submitter.value)`
- 線上簽核整合（依需求）
- 代碼維護下拉（依需求）
- `ProjectZMenuContributor` + `ProjectZSharedResource` 更新（❌ 禁止改 Core 的 MenuService / SharedResource）

**技術棧補充（ProjectZ 追加）：**
- `IMenuContributor` — Core MENU 由 `CoreMenuContributor`，專案 MENU 由 `ProjectZMenuContributor`
- `IStringLocalizer<ProjectZSharedResource>`（專案 MENU 多語，`ProjectZMenuContributor` 專用）

---

## 核心禁止事項（ProjectZ 追加）

以下規則為 Core 禁止事項之外的 ProjectZ 追加限制：

- ❌ 不得修改 `Services/MenuService.cs`（Core 收集器，不含 menu 定義）
- ❌ 不得修改 `Services/CoreMenuContributor.cs`（Core MENU，隨 Core 更新同步）
- ❌ 不得修改 `Resources/SharedResource.zh-TW.resx` / `en-US.resx`（Core 多語，隨 Core 更新同步；
  顧問模組欄位名改放 `Resources/ProjectZValidationResource.*.resx`）

---

## PR 合併程序（2026-09-08 新增）

**一律用 `scripts/safe-merge.sh <PR#>` 合併，不要直接 `gh pr merge`。**

理由：本 repo 的草稿 PR 會被 CI 跳過 `Build & Test`（2026-09-03 起新開 PR 還會自動轉草稿以省 Actions 分鐘數，
**2026-11-06 已撤除**，新開 PR 直接跑完整 CI；手動開的草稿仍然 skip），草稿 PR 的 `Build & Test`
會被 **skip**，而那一輪 workflow run **仍然回報 `conclusion=success`**。
於是「CI 綠了」在 run 層有三種分不出來的含意：

| 現象 | run 結論 | 測試真的跑了嗎 |
|---|---|---|
| 按 Ready 後那一輪 | success | ✅ 有 |
| 草稿 PR（手動開草稿；11/6 前還包括自動轉草稿那輪）| success | ❌ 被 skip |
| 只推分支 | success | ❌ 只跑 Validate Module Boundaries |

`safe-merge.sh` 一律看 **job 層**的 conclusion，並且擋下 `running`（CI 還沒跑完）。

⚠️ **不能用 `gh pr merge --auto`**：Free 方案私有庫的 `branches/main/protection` 回 403
（沒有 branch protection ＝ 沒有 required checks），且 `allow_auto_merge=false`。
沒有必要檢查可等，auto-merge 等於立刻合併。**升級 Pro 或轉公開之後**拿到
required status checks，這支腳本就可以退役。

實際代價（2026-09-08）：PR #131 在 `gh pr ready` 之後 21 秒合併，CI 還在跑、
結果是 failure，`main` 因此變紅，額外花掉三輪 CI 與一整段追查。

## 代碼維護（CodeEntry）命名規範

**CodeEntry 為全公司全域共用**（不區分廠別），所有廠別看到同一份代碼清單。

專案專屬的 CodeEntry Category 必須加 `PZ.` 前綴，避免與 Core 衝突：
- ✅ `PZ.ProductType`、`PZ.WarehouseZone`
- ❌ `ProductType`（與 Core 可能重名）

---

## 零衝突模組架構（ProjectZ 補充 SOP）

Core 擴充點規則見 CLAUDE.md 的「零衝突模組架構」。ProjectZ 新增模組的完整 SOP：

1. 建立 `Modules/{Name}/{Name}Module.cs`（實作 `IAppModule`）
2. 在 `Data/ApplicationDbContextExtensions.cs` 加入 DbSet 與 `OnModelCreatingPartial`
3. 建立 `Modules/{Name}/Controllers/`、`Modules/{Name}/Views/`
   - **必須建立 `Modules/{Name}/Views/_ViewImports.cshtml`**（含 `@addTagHelper`，否則所有 Tag Helper 失效）
   - **必須建立 `Modules/{Name}/Views/_ViewStart.cshtml`**（指定 Layout）
   - `_ViewImports.cshtml` 內容範本：
     ```cshtml
     @using SmartFactory.MES.Models
     @using SmartFactory.MES.Modules.{Name}.Models
     @using Microsoft.AspNetCore.Mvc.Localization
     @inject SmartFactory.MES.Services.ICurrencyService CurrencySvc   <%-- 依模組需求調整 --%>
     @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
     ```
   - `_ViewStart.cshtml` 內容範本：
     ```cshtml
     @{
         Layout = "/Pages/Shared/_Layout.cshtml";
     }
     ```
4. 執行 `dotnet ef migrations add {Name}_Init`

### 新增「可選模組」（有 `Features:*:Enabled` 開關的模組）額外檢查清單

一般模組做完上面四步就好。**只有帶功能開關、可以整個關掉的模組**（例如 `Modules/RPS`、
`Modules/QAA`）要再過這一張表——這些項目的共通點是「漏了不會有錯誤訊息，只會安靜地不對」：

- [ ] **MenuKey 登錄 `IsModuleLicensed`（缺口 G3）**。
      [`Services/PermissionService.cs`](../Services/PermissionService.cs) 的
      `IsModuleLicensed(menuKey)` 目前是 `=> true` 的樁，所以今天漏登錄沒有任何影響。
      但它一旦真的實作模組授權，**沒登錄的 MenuKey 會連 Admin 都被擋**——
      而症狀是「畫面點不進去」，不會有人聯想到授權表。新模組建立當下就補，不要等到那天。
- [ ] **`RegisterServices` 開頭就 return**：開關關閉時，服務、`AddHostedService`、
      View 搜尋路徑**全部不註冊**，而不是註冊完再用旗標擋。
      ⚠️ 這一條的前提是**開關與模組同時誕生**。若開關是在模組已有一部分上線之後才補上
      （`Modules/RPS` 就是這個情況：常用查詢先上、排程段後補），開頭 return 會把已上線的部分
      一起關掉——而開關預設 false，等於既有部署升級後憑空少功能。此時正確做法是
      **開關只罩新加的那一段**（RPS 罩派工器、保留期清理與三支畫面，不罩 0-A 的常用查詢），
      並在模組檔與規格上寫明這個範圍，否則下一個人會照字面「修正」成開頭 return。
- [ ] **關閉時要出聲**：開關關掉的後果若是「某類工作從此不執行」，啟動 log 必須寫出
      受影響的筆數（RPS 用 `ReportSchedulingDisabledNotice`）。靜默停止與「本來就沒事做」
      在外觀上完全一樣，沒有人會發現。
- [ ] **開關預設 `false`**：預設全關，行為與沒有這個模組時完全相同。
- [ ] **`RolePermissionSeeder` 的補鍵是一次性的**：鍵一旦散出去，事後改角色矩陣
      **不會回收既有授權**。所以群組與矩陣要在第一次種權限之前就定好。
- [ ] **落地檔案路徑進 `.gitignore`**：模型二進位、產生的報表檔一旦推上 git，歷史清不掉。

> 前三項屬於「開燈那一刻就生效」，第四、五項屬於「事後補救成本極高」。
> 兩類都不適合等到上線前才想。

> ⚠️ `_ViewImports.cshtml` 依 **實體目錄**（Physical Path）套用，不依 `RazorViewEngineOptions` 的搜尋路徑。
> 即使在 `BASModule.cs` 註冊了 `/Modules/BAS/Views/` 路徑，Views 下仍需自己的 `_ViewImports.cshtml`，
> 否則 `asp-for`、`asp-action`、`asp-route-*` 等 Tag Helper 全部失效，form action 不產生、input name 屬性消失。

---

## Entity 資料範圍規範（全域 vs 廠別）

### ProjectZ 各 Entity 分類（已依規範實作）

#### 全域主檔（不實作 `IPlantEntity`，無 PlantId，全公司共用）

| Entity | 說明 |
|--------|------|
| `CodeEntry` | 通用代碼檔，全公司共用下拉選單 |
| `Company` | 客戶／供應商，公司層級概念 |
| `ProductCategory` | 產品類別，全公司統一標準 |
| `Product` | 產品主檔，同一產品可在多廠生產 |
| `Tax` | 稅別，財務稅率全公司統一 |
| `Currency` | 幣別，全公司統一 |
| `Uom` | 計量單位，全公司統一 |
| `Container` | 包裝容器規格，全公司統一 |
| `InventoryTransactionType` | 庫存異動類別，系統標準配置 |

#### 廠別主檔（實作 `IPlantEntity`，各廠獨立維護）

| Entity | 說明 |
|--------|------|
| `Tooling` | 模治具，廠別設備資產 |
| `WorkCenter` | 工作中心，廠別生產設備 |
| `Warehouse` | 倉庫，廠別資產 |
| `ShiftGroup` / `Shift` | 班別，各廠所在地法規／時區不同 |
| `SpecialDay` | 特殊日，各廠所在國家假日不同 |
| `DocNumberConfig` | 單號設定，各廠單號格式可能不同 |

#### 作業單據（實作 `IPlantEntity`，嚴格廠別隔離）

| Entity | 說明 |
|--------|------|
| `PurchaseRequest` / `PurchaseOrder` | 採購請單／採購單 |
| `GoodsReceipt` / `GoodsReturn` | 進貨單／退貨單 |
| `InventoryMovement` | 庫存異動紀錄 |

### 全域 Entity 的實作規範

1. Model：不繼承 `IPlantEntity`，不加 `PlantId` 屬性
2. `ApplicationDbContextExtensions`：Unique Index 使用代碼本身（不含 PlantId）
   ```csharp
   // ✅ 全域
   .HasIndex(t => t.TaxCode).IsUnique();
   // ❌ 廠別（全域 Entity 不可用）
   .HasIndex(t => new { t.PlantId, t.TaxCode }).IsUnique();
   ```
3. Controller：不手動指定 `PlantId`，不以 `_userContext.PlantId` 過濾查詢
4. ApprovalConfig：`GetApprovalConfig("EntityType", null)`（PlantId 傳 `null`）

---

## 待我簽核 — ProjectZ 的 docDetailsUrls 擴充方式

**ProjectZ 使用獨立的 Partial 檔管理映射，Core 主檔可安全覆蓋同步：**

- `Views/Approval/_DocDetailsUrls.cshtml` — **ProjectZ 專屬，禁止 cp 覆蓋**
- `Views/Approval/PendingIndex.cshtml` — 與 Core 完全一致，可直接 cp
- `Views/Approval/AllRequests.cshtml` — 與 Core 完全一致，可直接 cp

> ⚠️ `_DocDetailsUrls.cshtml` **不可包含 `<script>` 標籤**。
> 此 Partial 由主 View 的 `<script>` 區塊內以 `@await Html.PartialAsync` 呼叫，
> 若自帶 `<script>` 標籤會造成巢狀，瀏覽器遇到內層 `</script>` 即關閉外層 script，
> 剩餘 JS 程式碼全部以純文字顯示在頁面上。
> 正確寫法：只輸出純 JS，不包 `<script>` 標籤。

### 🚨 2026-09-08 起：映射的唯一來源是 C#，不是這支 Partial

`_DocDetailsUrls.cshtml` **不再手寫任何一行對應**，它由
`Modules/_Shared/ApprovalDocumentLinks.Templates` 產生 JS。

**新增單別請改 C#，不要改 Partial：**

```csharp
// Modules/_Shared/ApprovalDocumentLinks.cs
["DocumentType"] = "/Controller/Details/{id}",
```

> ⚠️ **本節取代 `CLAUDE.md`〈docDetailsUrls 擴充模式〉的步驟 1。**
> CLAUDE.md 含 ProjectZ 補充內容，須保留並逐段合併 Core 更新；
> 依本檔開頭的「LOCAL_RULES 優先」原則，以這一節為準。步驟 2／3（補 `Details` action、
> `_Form` 的唯讀模式）**完全不變**。

**為什麼改**：手機的待簽核清單（`/mobile/approval/list.html`）需要同一份對應，
而把它抄成第二份 JS 的下場是慢慢分岔——分岔的症狀特別難看：
某個單別在其中一邊點下去沒反應、另一邊正常，而「點了沒反應」**本來就沒有錯誤訊息**
（`PendingIndex.cshtml` 是 `if (!urlFn) return;`）。

**改的時候順手補掉了 5 個從來沒登錄過的單別**：`APInvoice`／`ARInvoice`／
`LandedCostAllocation`／`MaterialStagingOrder`／`NonConformanceReport`。
其中兩個漏掉的原因是**名字對不上**——`NonConformanceReport` 的控制器叫 `Ncr`、
`MaterialStagingOrder` 的叫 `MaterialStaging`。**不要憑 DocumentType 推控制器名。**

守門：
- `ApprovalDocumentLinkCoverageTests` — 每個有 `IApprovalSyncContributor` 的單別都必須登錄
  （已變異驗證：拿掉 `NonConformanceReport` 立刻紅並指名）
- `MobileApprovalInboxTests.桌機清單的連結對照表與手機用的是同一份` — 釘住 Razor 真的有產生出 JS。
  ⚠️ 這支測試**刻意不做 `HtmlDecode`**：Razor 不知道自己在 `<script>` 裡，
  漏了 `Html.Raw` 就會把引號編成實體，而先解碼再斷言正好把要抓的缺陷抵銷掉
  （第一版就是這樣寫的，拿掉 `Html.Raw` 照樣綠）。

---

## 系統參數 — ProjectZ 的擴充方式（2026-07-27 起改為單行委派）

與 docDetailsUrls 同一模式：**Core 擴充點只保留一行委派，內容全在 ProjectZ 自有檔。**

| 檔案 | 歸屬 | 動作 |
|------|------|------|
| `Views/SystemParam/_SystemParamModuleExtensions.cshtml` | Core 既有檔（在 `scripts/core-drift-baseline.txt`） | **只有一行** `@await Html.PartialAsync("_ProjectZSystemParams")`，**不要往裡面加東西** |
| `Views/SystemParam/_ProjectZSystemParams.cshtml` | ProjectZ 自有 | 所有模組設定區塊加在這裡 |
| `Resources/Views/SystemParam/_ProjectZSystemParams.{zh-TW,en-US}.resx` | ProjectZ 自有 | 對應多語 key（`IViewLocalizer` 依 View 路徑解析，檔名必須與 View 同名） |
| `Filters/SystemParamEnhancementFilter.cs` | ProjectZ 自有 | Index 投影到 ViewData、Save 讀 Form 持久化 |
| `Models/ProjectZSystemParam.cs` | ProjectZ 自有 | 參數 key 常數 |

> 改為單行委派的理由：此 Core 檔每加一個模組設定，漂移就擴大一次。
> 收斂為一行後，之後所有新參數都不再碰 Core 檔，Core 日後改動此檔的 merge 成本趨近於零。

**新增一個系統參數的完整步驟**（無 migration、無 seeder、無選單、不改 `Program.cs`）：
1. `Models/ProjectZSystemParam.cs` 加常數。
2. `_ProjectZSystemParams.cshtml` 加欄位。checkbox 必須配 `<input type="hidden" name="X" value="false" />`，
   否則未勾選時該欄不會出現在 Form。
3. 兩個 resx 加 key（label + hint）。
4. `SystemParamEnhancementFilter`：Index 投影、Save 正規化寫入。
5. 讀取端一律 `ISystemParamService.GetAsync`；寫入端一律 `SetAsync`/`SetBatchAsync`——
   **不可直接對 `DbContext` 增刪 `SystemParams` 列**（服務以 `IMemoryCache` singleton 快取整份參數
   10 分鐘，只有這兩個方法會 `_cache.Remove`，直接動 DbContext 會留下髒快取）。
6. **未設定（null）必須等同「舊行為」**，不寫預設列，讓「從未設定」與「明確設為 false」可區分。
7. 補 `SystemParamEnhancementFilterTests`（投影／正規化／未勾選存 false／從未設定）與使用者手冊頁。

---

## 一致性框架（畫面/操作一致且完整 — 必用）

為消除 Vibe Coding 遺漏（多語、權限、AJAX 樣板漏抄），ProjectZ 導入「執行期共用元件 + 建置期把關」雙保險。**所有新畫面必用，既有畫面分批回補。對 Core 零修改。**

> 📐 **完整架構規格＝ `.claude/specifications/consistency-framework.md`**（四支柱設計、Core/ProjectZ 歸屬、Phase A–C 進度、驗證方式）。新增/重構畫面前必讀；本節為其執行摘要。

### 執行期共用元件

| 元件 | 規定 |
|------|------|
| `Modules/_Shared/ModuleControllerBase.cs` | 顧問模組控制器**一律繼承**；提供 `IsAjax`、`PrepareIndexPermissionsAsync`、`JsonOk/JsonFail`、`ValidationPartial`、`PreparePrintViewBagAsync`。建構式注入不變。 |
| `Views/Shared/_CrudPanelScripts.cshtml` | Index `@section Scripts` 第一行 `@await Html.PartialAsync("_CrudPanelScripts")`；不得自抄 `openPanel/submitPanel/...` |
| `wwwroot/js/crud-panel.js` | 共用 CRUD 腳本；**禁止硬編碼 CJK**，字串走 `window.i18n.t('Key')` |
| `Views/Shared/_JsLocalization.cshtml` | 輸出 `window.i18n`；新 JS 字串 key 加在 `ProjectZSharedResource`（zh-TW＋en-US 同步） |
| `Views/Shared/_PrintLayoutZ.cshtml`／`_PrintBase.cshtml` | 列印一律用此 Layout（語系感知）＋簽核區 partial；**不改 Core `_PrintLayout.cshtml`** |

### 建置期把關（`SmartFactory.MES.Tests/Conventions/`）

`dotnet test` 會跑 R1–R9；**不在 baseline 的新違規即建置失敗**：

- R1 View `@Localizer["k"]` 必須存在於配對 resx（zh-TW＋en-US）
- R2 Controller `MenuKey` 必須登錄於 `ProjectZMenuContributor`／`ProjectZSharedResource`
- R3 `Print.cshtml` 必須透過 `Localizer` 取字串
- R4 `wwwroot/js/*.js` **與 `Modules/**/*.cshtml` 內聯 `<script>`** 的 `alert/confirm` 不得含硬編碼 CJK
- R5 每個 `*.zh-TW.resx` 必有 `*.en-US.resx` 且 key 集合完全相同
- R5b Model 帶 `[Required]`／非可空實值型別（隱含必填）且未標 `[Display]`/`[DisplayName]` 的屬性名，必須在 `SharedResource` ∪ `ProjectZValidationResource` 的 `{zh-TW,en-US}` 雙語皆有 key（否則驗證訊息 `{0}` 退回英文屬性名）；**顧問模組欄位一律加在 `ProjectZValidationResource`**
- R6 `crud-panel.js` 必含 `armValidation` 並於 `openPanel`/`submitPanel` 注入後呼叫；用 `openPanel` 的 Index 必引入 `_CrudPanelScripts`（防原生驗證泡泡外露）
- R6b `submitPanel` 必以「回應 trim 後以 `<script` 開頭」判斷成功（禁 `includes('refreshList')` 子字串嗅探）；控制器成功回傳必為純 `<script>` 文件
- R7 `Modules/*/Controllers/*Controller.cs` 的 `message=`/`AddModelError`/`TempData`/`alert` 不得含硬編碼 CJK（走 `IStringLocalizer<ProjectZSharedResource>`）
- R8 View 直接輸出且無參數的 `@Localizer["k"]`,其 resx 值不得含未跳脫 `{N}`（否則 `WriteTo` 時 `string.Format` → `FormatException`）；用 `{{N}}` 跳脫或帶參數
- R9 數量/金額/重量等「跟隨 UOM/幣別精度」的 `type="number"` 欄位,`step` 一律 `"any"`（spinner ±1）；**禁寫死** `step="0.0001"`/`"0.01"`,精度由 `min`+伺服端 `RoundQtyAsync`/`RoundAsync` 把關。例外：稅率/匯率/換算比例（0–1）、加班/工時、序號 `step="10"` 保留精確 step。多幣別行幣別欄排在單價前；`.line-price`+同列 `.line-currency` 由 `currency-format.js` 自動依幣別 dp 即時收斂

> Phase B 回補：逐行刪除 `baseline-R*.txt` 的既有違規即收斂；baseline 檔不得用於放行新畫面。
> 驗證訊息在地化機制（R5b 對應）已上移 **Core**（`AddLocalizedValidation()`），顧問模組不得重複註冊 provider。
> Core 的查找為**鏈式**：`SharedResource`（Core 主導，本專案不得修改）→ `ProjectZValidationResource`
>（ProjectZ 自有，由 `BASModule` 以 `AddValidationResourceFallback<ProjectZValidationResource>()` 登記）。
> **新欄位雙語 key 一律加在 `ProjectZValidationResource`**，不要再往 Core 的 `SharedResource` 加。
> ⚠️ 不要改成 fallback 到 `ProjectZSharedResource`——後者是選單／畫面用語，存在同名但語意不同的鍵
>（如 `DefectRate`＝「不良率報表」選單 vs「不良率」欄位），混用會讓驗證訊息顯示成選單標題。

> ✅ **畫面完成前的自我檢核**：新增或修改任何 `_Form` / Controller 後，執行
> ```bash
> dotnet test --filter "FullyQualifiedName~ConventionTests" --project SmartFactory.MES.Tests
> ```
> 確認無新增違規（全綠 / 或僅 baseline 內的既有違規）才算完成。未執行即送出 PR 視為流程錯誤。

---

## 待決項的收錄規則（R29 守門）

`.claude/specifications/STATUS.md`〈真正未完成〉是「下次規劃直接看這裡，不用重讀全文」的入口。
它是**手抄的聚合**，所以會漂移——2026-08-13 逐條查證，7 條裡有 3 條已經不成立，
同時 `llm-qa-and-report-tools.md` §14.2 的待決 #10 一直開著卻從沒被收進去。
**R29** 因此把兩邊接起來對帳。

### 三種標記，決定一個待決項的去向

| 來源端寫法 | 意思 | 要不要進〈真正未完成〉 |
|---|---|---|
| 編號**沒有**任何裝飾（`\| 10 \|`） | 還開著 | **要**——沒列會紅 |
| 編號被 `~~` 圍起來（`\| ~~**9**~~ \|`） | 已結案 | **不要**——還列著會紅 |
| 編號前加 `⏸`（`\| ⏸ 8 \|`） | 刻意延後，**等上線後的實際數據才決定** | 不要，但**留在來源表內**不刪 |

`⏸` 是為了把兩種「還沒決定」分開：
**現在就決定得了的**（例如 #10，是純設計裁決，只是決定完有遷移成本）要進清單；
**現在決定不了的**（例如 #8 最小群組數門檻要看實際客戶分佈）進了只會稀釋清單。
以前這條界線是隱含的，#10 就是這樣跟著 #6／#8／#7 一起掉出去的。

### 結案時怎麼寫

**原地劃掉、保留編號與理由，不要刪除也不要重新編號**——`STATUS.md` 是以
「§9 #1」「待決 #3」這種寫法引用的，編號即識別碼。
理由留著才有決策脈絡（既有的表都是 `~~原標題~~ | ✅ 2026-0X-XX 裁決：… 以下為決策過程留存：…`）。

### 新增一個受守門的區塊

在來源檔用 HTML 註解圈起來即可，不必改動既有排版：

```markdown
<!-- open-items ref="檔名或章節（要與 STATUS.md 的寫法對得上）" -->
| # | 事項 | … |
|---|---|---|
| 10 | … |
<!-- /open-items -->
```

編號取自**表格第一欄**或**有序清單的序號**；`STATUS.md` 那一條只要在
同一個項目符號裡同時出現 `ref` 與編號就算對上，所以 `待決 #3／#11` 這種合寫法不必拆開。

### 已納入守門的區塊

- `llm-qa-and-report-tools.md` §14.2 待決表（B 組、C 組）
- `llm-qa-and-report-tools.md` §17.9 已知缺口表（G1／G2／G3）
- `user-manual-tutor.md` §9 待裁決

⚠️ **`STATUS.md` 上表〈完成度索引〉不在守門範圍**，仍是純人工——
完成度是整份文件的主觀評語，沒有可對帳的單位。同理，
`business_data_flow_reconciliation.md` 的 P-1～P-4 路線圖也沒有帶編號的待決區塊可掛，
`P-4` 那一條是人工維護的。

---

## 參考文件（ProjectZ 追加）

- `.claude/specifications/consistency-framework.md` — 畫面/操作一致性框架完整規格（四支柱、Phase A–C、Core 修根）
- `.claude/commands/new-screen-projectz.md` — ProjectZ 畫面產生補充規格（覆蓋 Core 對應段落）
- `.claude/commands/new-job.md` — 背景作業／排程產生規格（IHostedService / Hangfire；ProjectZ 專屬）
- `.claude/commands/new-analytics.md` — 資料分析儀表板規格（KPI / 趨勢圖 / 彙總；ProjectZ 專屬，Chart.js 需第一次引入）

## 自動載入機制（依環境決定，未必啟用）

`.claude/settings.json` 內含一條 `UserPromptSubmit` hook 範本，**預計**每次使用者送出 prompt 時把「先讀本檔」「衝突時 LOCAL_RULES 優先」等提醒注入到該回合的 model context。

⚠️ **啟用前提**：機器需安裝 Claude Code **CLI**（非僅 VS Code extension），並透過 terminal `/hooks` 信任流程啟用。
若機器受管控**無法**安裝 Node.js / Claude Code CLI，hook 不會被觸發——此時**規則執行完全依靠本檔頂部的紅字提醒、AI memory、與顧問人工 review**，所以 AI 助理請務必在每次 ProjectZ 相關工作開始前主動 Read 本檔。

詳細運作、啟用條件、修改方式見 `.claude/README.md` §4。


## 資安修正驗收（2026-11-05）

角色與廠區切換規則由 Core 共用服務負責，部署專案不另抄判定。
廠區寫入端點依 `StalePlantContextFilter.RequiresContext` 要求快照（細節見 new-screen-projectz.md〈廠區寫入上下文〉）；全域主檔豁免。
推播在訂閱入口與實際送出兩階段都經 `PushEndpointPolicy` 檢查，不能只在訂閱時驗網址；推播不走系統代理（`deploy/mobile-device-security.md` §六）。
測試結果分開記錄單元、HTTP、真 PostgreSQL、瀏覽器及正式部署；未執行者不得記為完成。
