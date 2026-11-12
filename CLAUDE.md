# SmartFactory MES — AI 開發準則

## 代碼維護（CodeEntry）下拉選單多語規範

CodeEntry 的 `NameZh` / `NameEn` 依語系選擇，**語言邏輯集中於 `Services/CodeEntryDisplayExtensions.cs`**，Controller 與 View 不得自行判斷語系。

### Controller 寫法

```csharp
// ✅ 正確：使用 ToDisplayOptionsAsync（回傳 List<CodeEntryOption>，不需傳語系參數）
ViewBag.InvoiceTypeOptions = await _context.CodeEntries
    .AsNoTracking()
    .Where(e => e.Category == "invoice_type" && e.IsActive)
    .OrderBy(e => e.SortOrder)
    .ToDisplayOptionsAsync();

// ✅ 正確：Index 列表用 Dictionary 版本
ViewBag.ToolingTypeNames = await _context.CodeEntries
    .AsNoTracking()
    .Where(e => e.Category == "tooling_type" && e.IsActive)
    .ToDisplayDictionaryAsync();

// ❌ 錯誤：直接 Select NameZh，無多語支援
.Select(e => new { e.Code, e.NameZh }).ToListAsync()
// ❌ 錯誤：傳 _userContext.Language（讀 DB 欄位，不反映 UI 語系 cookie）
.ToDisplayOptionsAsync(_userContext.Language)
```

### View 寫法

```cshtml
@foreach (dynamic iv in invoiceTypes)
{
    <option value="@iv.Code">@iv.Code — @iv.DisplayName</option>
}
```

> `DisplayName` 已由 extension 依語系解析，View 不需判斷 `NameZh` / `NameEn`。

---

## 數量欄位 UOM 小數點套用規範

含數量輸入的畫面（主檔或明細行）**必須**完整套用以下三層對應，詳細規格見 `.claude/commands/new-screen-projectz.md` 第 6 節。

### Controller

```csharp
// 注入 IUomService；PrepareFormDropdowns 加入：
ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

// POST 儲存前對每個數量欄位四捨五入：
qty = await _uomService.RoundQtyAsync(uomCode, qty);
```

### View

```cshtml
@using SmartFactory.MES.Modules.BAS.Services
@{ var uomDecimals = ViewBag.UomDecimals as IReadOnlyDictionary<string, int>; }

<%-- 數量 input --%>
<input type="number" class="form-control line-qty"
       step="@UomStepHelper.GetStep(uomDecimals, line.UomCode)"
       min="@UomStepHelper.GetMinPositive(uomDecimals, line.UomCode)" />

<%-- UOM select --%>
<select class="form-select line-uom" onchange="onUomChange(this)">

<%-- Script --%>
window.uomDecimals = Object.assign(window.uomDecimals || {},
    @Html.Raw(JsonSerializer.Serialize(uomDecimals ?? new Dictionary<string,int>())));
function onUomChange(sel) {
    window.applyUomStep(sel.closest('tr').querySelector('.line-qty'), sel.value);
}
```

> `window.applyUomStep` / `applyUomStepAsync` / `uomStepOf` / `fetchUomDecimals` 由 `wwwroot/js/uom-step.js` 全域提供（已在 `_Layout.cshtml` 載入）。
> `UomService.InvalidateDecimalPlacesCache()` 在 UomController 每次新增/修改/刪除後呼叫，確保快取即時失效。

---

## 金額欄位 Currency 小數點套用規範

含金額輸入的畫面（含 UnitPrice、LineAmount、TotalAmount、StandardCost 等）**必須**完整套用以下三層對應，確保 step 屬性與資料庫儲存精度一致。

### Controller

```csharp
// 注入 ICurrencyService；PrepareFormDropdowns 加入：
ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

// 若畫面有多幣別選擇（PUR 模組）：
ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();

// ResolveBaseCurrencyDpAsync 標準寫法（每個 Controller 加一次）：
private async Task<int> ResolveBaseCurrencyDpAsync()
{
    var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
    if (string.IsNullOrEmpty(baseCurrency)) return 2;
    var map = await _currencyService.GetAllDecimalPlacesAsync();
    return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
}

// POST 儲存前對每個金額欄位四捨五入（簽章：RoundAsync(decimal amount, string currencyCode)）：
amount = await _currencyService.RoundAsync(amount, currencyCode);
```

### View（單一幣別，使用本位幣 baseDp）

```cshtml
@using SmartFactory.MES.Modules.BAS.Services
@{
    var uomDecimals = ViewBag.UomDecimals as IReadOnlyDictionary<string, int>;
    int baseDp = ViewBag.BaseCurrencyDp as int? ?? 2;
    string moneyStep = baseDp <= 0 ? "1" : "0." + new string('0', baseDp - 1) + "1";
}

<%-- 金額 input --%>
<input type="number" step="@moneyStep" class="form-control" />
```

### View（多幣別行，使用 CurrencyStepHelper）

```cshtml
@using SmartFactory.MES.Modules.BAS.Services
@{
    var currencyDecimals = ViewBag.CurrencyDecimals as IReadOnlyDictionary<string, int>;
}

<%-- 在 @for 迴圈內，每行先取 cdp，同時用於 step 與 value --%>
@for (int i = 0; i < Model!.Lines.Count; i++)
{
    var line = Model!.Lines[i];
    var cdp  = CurrencyStepHelper.GetDp(currencyDecimals, line.CurrencyCode);

    <%-- step：控制瀏覽器允許的輸入精度 --%>
    <%-- value：以 "F" + cdp 格式化，避免 NUMERIC(18,4) 欄位顯示 4 位小數 --%>
    <input type="number"
           step="@CurrencyStepHelper.GetStep(currencyDecimals, line.CurrencyCode)"
           value="@(line.UnitPrice.ToString("F" + cdp))"
           class="form-control line-price" />
    <%-- 所有金額欄位（LineAmount、TaxAmount、TotalAmount）同樣套用 "F" + cdp --%>
    <input type="number" readonly
           step="@CurrencyStepHelper.GetStep(currencyDecimals, line.CurrencyCode)"
           value="@(line.LineAmount.ToString("F" + cdp))"
           class="form-control line-lineamt" />
}

<%-- Script --%>
window.currencyDecimals = Object.assign(window.currencyDecimals || {},
    @Html.Raw(JsonSerializer.Serialize(currencyDecimals ?? new Dictionary<string,int>())));
function onCurrencyChange(sel) {
    const row = sel.closest('tr');
    ['line-price','line-amount','line-tax','line-total'].forEach(cls => {
        row.querySelectorAll('.' + cls).forEach(inp => window.applyCurrencyStep(inp, sel.value));
    });
}
```

> `window.currencyDp` / `currencyStepOf` / `applyCurrencyStep` / `roundHalfAway` / `fmtMoney` 由 `wwwroot/js/currency-format.js` 全域提供（已在 `_Layout.cshtml` 載入）。
> `roundHalfAway` 對齊 C# `MidpointRounding.AwayFromZero`。
> `CurrencyService.InvalidateCache()` 在 CurrencyController 每次新增/修改/刪除後呼叫，確保快取即時失效。
> Index 列表顯示金額欄位：`int baseDp = ViewBag.BaseCurrencyDp as int? ?? 2; string moneyFmt = baseDp <= 0 ? "N0" : $"N{baseDp}";`，然後 `TotalAmount.ToString(moneyFmt)`。

---

## 查詢列控件選用規範（強制）

查詢列每個欄位依性質選擇控件，**不得用 `<input type="text">` 代替下拉**：

| 欄位性質 | 控件 | 說明 |
|---------|------|------|
| **品號**（productCode / searchProduct） | `<select class="select2-enable w-100">` | Controller 需載入 `ViewBag.ProductOptions` |
| **往來廠商/客戶**（supplierCode / searchSupplier / searchCustomer / companyCode） | `<select class="select2-enable w-100">` | Controller 需載入 `ViewBag.SupplierOptions` 或 `ViewBag.CustomerOptions` |
| **單據號**（workOrderId / salesOrderId / poNo / batchId 等） | `<select class="select2-enable w-100">` | Controller 需載入對應 Options |
| **固定枚舉**（status / type / category / 語系等） | `<select class="form-select">` | 直接 foreach 列舉值 |
| **廠別設施**（warehouseCode / locationCode / workCenterId / machineCode） | `<select class="form-select">` | Controller 需載入對應 Options |
| **自由文字**（批號 lotNo / NCR單號 / 關鍵字 searchTerm 跨多欄） | `<input type="text">` | 僅限無法預先列舉的欄位 |

### select2-enable 標準寫法

```cshtml
<select name="productCode" class="form-select form-select-sm select2-enable w-100"
        data-placeholder="@Localizer["All"]" data-allow-clear="true">
    <option value=""></option>
    @foreach (var p in productOptions)
    {
        <option value="@p.ProductCode" selected="@(curProduct == (string)p.ProductCode)">@p.ProductCode — @p.ProductName</option>
    }
</select>
```

### Controller Options 標準寫法

```csharp
// 品號
ViewBag.ProductOptions = await _context.Products
    .AsNoTracking().Where(p => p.IsActive)
    .OrderBy(p => p.ProductCode)
    .Select(p => new { p.ProductCode, p.ProductName })
    .ToListAsync();

// 廠商（SupplierType = "S"）
ViewBag.SupplierOptions = await _context.Companies
    .AsNoTracking().Where(c => c.IsActive && c.SupplierType == "S")
    .OrderBy(c => c.CompanyCode)
    .Select(c => new { c.CompanyCode, c.CompanyName })
    .ToListAsync();

// 客戶（SupplierType = "C"）
ViewBag.CustomerOptions = await _context.Companies
    .AsNoTracking().Where(c => c.IsActive && c.SupplierType == "C")
    .OrderBy(c => c.CompanyCode)
    .Select(c => new { c.CompanyCode, c.CompanyName })
    .ToListAsync();
```

> 使用 select2 後，Controller 篩選改精確比對（`==`），不再用 `Contains()`。

---

## 畫面產生規範（自動套用）

**當使用者要求產生任何新功能畫面時，無需手動呼叫 `/new-screen`，
AI 必須自動依照 `.claude/commands/new-screen.md` 的完整規格執行。**

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
- `CoreMenuContributor` + `SharedResource` 更新

---

## 專案技術棧

- ASP.NET Core 10 MVC + Razor Views
- Entity Framework Core（PostgreSQL）
- Bootstrap 5 + Bootstrap Icons
- `PaginatedList<T>`（`Models/PaginatedList.cs`）
- `IUserContext`（廠別、使用者、語系）
- `IPermissionService`（CanRead/Create/Update/Delete/Print）
- `IApprovalService`（線上簽核）
- `IDateTimeService`（時區感知時間）
- `IMenuContributor`（`CoreMenuContributor` 負責 Core MENU 定義）
- `IStringLocalizer<SharedResource>`（MENU 多語）
- `IViewLocalizer`（View 多語）

---

## 核心禁止事項

- ❌ 不得修改驗證流程或繞過指紋比對 —— 指紋不符時只能請使用者換發授權，不得改程式跳過
- ❌ 不得將 `appsettings.Development.json`、`license.json`、`.env`、機器錨定檔提交版控
- ❌ 不得替換 `Resources/license-public.pem`
- ⚠️ 指紋演算法（`Services/MachineAnchor.cs`）是**單一來源**，Core 端以 `<Compile Include Link>`
  共用於主專案、`SmartFactory.MES.Security`、`SmartFactory.MES.MachineId`、`SmartFactory.MES.LicenseAuthority`。
  改動它等同讓**所有既有授權與已加密連線字串同時失效**，且必須 Core-first 後同步至本 fork，
  並重新核發全部授權、重新加密全部 `DefaultEncrypted`。
  （完整規劃見 Core 端 SECURITY-OPERATIONS.md §14，該檔不在版控中）

---

## 零衝突模組架構

Core 已實作兩個擴充點，讓顧問不需修改任何 Core 檔案：

### `IAppModule`（`Services/IAppModule.cs`）
- 顧問在 `Modules/{Name}/{Name}Module.cs` 實作 `IAppModule`
- Core 的 `Program.cs` 自動掃描並呼叫 `RegisterServices` / `Configure`
- 新增服務、Filter、RazorViewEngineOptions 一律寫在 `{Name}Module.cs`，**不得修改 Program.cs**

### `partial class ApplicationDbContext`
- Core 的 `ApplicationDbContext` 是 `partial class`，結尾呼叫 `OnModelCreatingPartial(builder)`
- 顧問在 `Data/ApplicationDbContextExtensions.cs` 以另一個 `partial class` 片段加入 DbSet + Fluent API
- **不得修改 `Data/ApplicationDbContext.cs`（Core 主體）**

---

## Entity 資料範圍規範（IPlantEntity vs 全域）

### 判斷原則

| 類型 | 實作介面 | 說明 |
|------|---------|------|
| **全域主檔** | 不實作 `IPlantEntity` | 全公司共用，無廠別隔離 |
| **廠別主檔** | 實作 `IPlantEntity` | 各廠獨立維護 |
| **作業單據** | 實作 `IPlantEntity` | 嚴格廠別隔離，絕不跨廠 |

### 全域主檔規則

- 不實作 `IPlantEntity`，不加 `PlantId` 屬性
- Unique Index 使用**代碼本身**（不含 PlantId）
- Controller 不得手動指定 `PlantId`，也不得以 `_userContext.PlantId` 過濾查詢
- `GetApprovalConfig` 的 PlantId 參數**傳 `_userContext.PlantId`，不可傳 `null`**
  （⚠️ 這條原本寫「傳 `null`（全域審核設定）」，照著寫必得到一個永遠不生效的設定：
  `DocumentApprovalConfig` 實作 `IPlantEntity`，存檔時 `ApplyGlobalValues` 會把
  `PlantId ??= CurrentPlantId` 填上——**沒有任何入口建得出「不指定廠別」的設定**；
  而 `GetApprovalConfig` 沒有 `IgnoreQueryFilters`，全域查詢過濾 `PlantId == CurrentPlantId`
  也還壓著。條件要 `null`、過濾要廠別，**雙重不可能**。
  失效形狀是**放行**不是卡住：查不到就走「無簽核設定」分支直接核准，沒有任何訊號。
  2026-09-01 修正，同批修掉照著這條寫的 `CodeEntryController` 兩處；
  `VendorEvaluationController` 已於同源修正中先行改好）
  - ⚠️ **與上一條不衝突，別再搞混**：上一條講的是「這個全域主檔**自己的**查詢不得用廠別過濾」
    （代碼全公司共用，這是對的）；本條講的是「**簽核設定**這張表要用哪個廠別去查」。
    簽核設定是廠別主檔（`IPlantEntity`），不是全域主檔——**主檔全域不代表它的簽核設定也全域**。
    把兩者混為一談，正是這條規則當初寫錯的原因。
- 全公司所有廠別共用同一份資料

### 廠別主檔 / 作業單據規則

- 實作 `IPlantEntity`，包含 `public string? PlantId { get; set; }`
- DbContext 的 `ApplyGlobalValues()` 自動填入 `PlantId`（`??=`），Controller 不需手動指定
- Global Query Filter 自動套用 `PlantId == currentPlantId`
- Unique Index 必須包含 `PlantId`（如 `(PlantId, WarehouseCode)`）

### 新增 Entity 的判斷清單

```
✅ 適合全域的特徵：
  - 財務標準（幣別、稅別）
  - 計量標準（計量單位、換算比例）
  - 往來廠商（客戶、供應商）
  - 產品主檔（各廠共用同一產品）
  - 系統標準分類代碼

✅ 適合廠別的特徵：
  - 廠別設施（倉庫、工作中心、班別）
  - 廠別時程（特殊日、班別群組）
  - 作業單據（請購、採購、進退貨）
  - 廠別設定（單號格式、系統參數）
```

---

## 待我簽核 — 點擊單號顯示單據（docDetailsUrls 擴充模式）

`Views/Approval/PendingIndex.cshtml` 的單號欄位已改為可點擊按鈕，點擊後在 Side Panel 以唯讀模式顯示單據。

**Core 的角色**：`PendingIndex.cshtml` 和 `AllRequests.cshtml` 宣告空映射後立即呼叫 partial：
```js
const docDetailsUrls = {};
@await Html.PartialAsync("_DocDetailsUrls")   // 由各部署專案提供此檔
```
Core 自身的 `Views/Approval/_DocDetailsUrls.cshtml` 為空（僅含注解）。

**新增模組支援的步驟**（在 ProjectZ 等部署專案中操作）：
1. 在 `Views/Approval/_DocDetailsUrls.cshtml` 的 `Object.assign(docDetailsUrls, {...})` 加入一行：
   ```js
   'DocumentType': id => `/Controller/Details/${id}`,
   ```
   > ✅ PendingIndex / AllRequests 主體與 Core 完全一致，同步時直接 cp 不會遺失任何設定。
   > ⚠️ `_DocDetailsUrls.cshtml` 不可包含 `<script>` 標籤——它被插入在主 View 既有的 `<script>` 區塊內，
   > 巢狀 `<script>` 會導致瀏覽器提前結束外層 script，剩餘程式碼以純文字顯示在頁面上。
2. 在對應 Controller 新增 `Details(string id)` GET action：
   - 載入 entity + lines
   - 呼叫 `PrepareFormDropdowns()`
   - 設定 `ViewBag.IsReadOnly = true`
   - 回傳 `PartialView("_Form", vm)`（AJAX 用）
3. 在 `_Form.cshtml` 加入唯讀模式支援：
   - `bool isReadOnly = ViewBag.IsReadOnly == true;`
   - `<fieldset @(isReadOnly ? "disabled=\"disabled\"" : "")>` 包住表單內容
   - Footer 加入 `@if (isReadOnly) { 關閉按鈕 } else { 原有按鈕 }`
4. 單據類型的**顯示名稱**：簽核畫面（待我簽核、所有簽核記錄、簽核／歷程／轉簽面板）經
   `IApprovalDocumentTypeLabels` 依語系顯示，不直接印代碼。部署專案在自己的 `IAppModule.RegisterServices`
   註冊 `IApprovalDocumentTypeLabelContributor`，從自家資源檔回傳名稱；不認識的類型回 `null`。
   Core 自己的類型（`CodeEntry`）在 `SharedResource` 的 `ApprovalDocType_{代碼}`。
   沒有任何來源認得時顯示代碼原樣（不會空白，但也不會報錯——新增送審單據時記得一併補名稱）。

---

## 線上簽核 — 送審後資料刪除的防護規則

### 防護策略

| 層級 | 機制 | 說明 |
|------|------|------|
| **後端阻擋（主防線）** | DeleteConfirmed 檢查 `ApprovalStatus == PendingApproval` | 有待審記錄時拒絕刪除，回傳錯誤訊息提示先撤回 |
| **前端降級（備援）** | `openPanel` 攔截 HTTP 404 | 萬一資料已不存在（如 DB 直接操作），Side Panel 顯示具體說明而非通用錯誤 |

### DeleteConfirmed 標準寫法（有簽核欄位的 Entity）

```csharp
public async Task<IActionResult> DeleteConfirmed(string code)
{
    var rec = await _context.XXX.FindAsync(code);
    if (rec == null)
    {
        if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_XxxNotFound") });
        return NotFound();
    }

    if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
    {
        if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_XxxPendingApprovalWithdrawFirst") });
        TempData["ErrorMessage"] = CoreText.Get("Msg_XxxPendingApproval");
        return RedirectToAction(nameof(Index), new { searched = true });
    }

    _context.XXX.Remove(rec);
    // ...
}
```

> 使用者看得到的訊息一律取自資源檔：Core 用 `CoreText.Get(key)`，鍵加到 `Resources/SharedResource.{zh-TW,en-US}.resx`；
> 部署專案用各自的資源類別（ProjectZ：`PzText.Get`）。寫死中文會在英文介面原樣出現，
> `HardcodedCjkLiteralGuardTests` 會讓 build 紅（日誌、屬性說明、`isZh ? … : …` 雙語分支不算）。

### openPanel 404 處理（Views/Approval/PendingIndex、AllRequests）

```js
fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
    .then(r => { if (!r.ok) throw r.status; return r.text(); })  // throw 數字狀態碼，非 Error 物件
    .then(html => { body.innerHTML = DOMPurify.sanitize(html); bindPanelForms(); })
    .catch(status => {
        const msg = status === 404
            ? '此單據已不存在（可能已被刪除）。'
            : '載入失敗，請重試。';
        body.innerHTML = `<div class="alert alert-warning m-3">...${msg}</div>`;
    });
```

---

## Core → 部署專案 同步規則

當 Core 有更新需要同步至 ProjectZ 等部署專案時，依下表逐檔判斷。

### ✅ 可直接覆蓋（部署專案與 Core 完全一致）

```bash
CORE=../SmartFactory.MES          # 依實際路徑調整
PROJ=.                             # 在部署專案根目錄執行

cp $CORE/Models/Plant.cs                                    $PROJ/Models/Plant.cs
cp $CORE/Services/ExportService.cs                          $PROJ/Services/ExportService.cs
cp $CORE/Services/ICodeEntryReferenceChecker.cs             $PROJ/Services/ICodeEntryReferenceChecker.cs
cp $CORE/Controllers/CodeEntryController.cs                 $PROJ/Controllers/CodeEntryController.cs
cp $CORE/Controllers/PlantController.cs                     $PROJ/Controllers/PlantController.cs
cp $CORE/Controllers/OrganizationController.cs              $PROJ/Controllers/OrganizationController.cs
cp $CORE/Controllers/RoleController.cs                      $PROJ/Controllers/RoleController.cs
cp $CORE/Controllers/UserController.cs                      $PROJ/Controllers/UserController.cs
cp $CORE/Controllers/ApprovalTemplateController.cs          $PROJ/Controllers/ApprovalTemplateController.cs
cp $CORE/Controllers/DocumentApprovalConfigController.cs    $PROJ/Controllers/DocumentApprovalConfigController.cs
cp $CORE/Views/CodeEntry/_CodeEntryForm.cshtml              $PROJ/Views/CodeEntry/_CodeEntryForm.cshtml
cp $CORE/Views/Approval/PendingIndex.cshtml                 $PROJ/Views/Approval/PendingIndex.cshtml
cp $CORE/Views/Approval/AllRequests.cshtml                  $PROJ/Views/Approval/AllRequests.cshtml
```

### ❌ 禁止覆蓋（部署專案有客製化內容）

| 檔案 | 原因 |
|------|------|
| `Views/Approval/_DocDetailsUrls.cshtml` | 部署專案的模組 URL 映射，Core 版本為空注解 |
| `Views/SystemParam/_SystemParamModuleExtensions.cshtml` | 部署專案的系統參數擴充區塊，Core 版本為空注解 |
| `Program.cs` | 部署專案已整合顧問模組（BAS、PUR 等） |
| `Data/ApplicationDbContextExtensions.cs` | 顧問模組的 DbSet 與 Fluent API |
| `appsettings.json` | 部署環境的連線字串等設定 |
| `Modules/` | 整個顧問模組目錄 |
| `.claude/LOCAL_RULES.md` | 部署專案的專屬規則 |

### 邊界 hook 與 Core 修正的提交順序

部署專案啟用邊界保護（`git config core.hooksPath .githooks`）後，pre-commit 會擋下
「Core 既有檔案」的修改；cp 同步豁免比對的是 **`upstream/main` 遠端 ref 的內容**。
因此涉及 Core 檔案的修正，提交順序必須是：

1. 先在 Core repo commit 並 push（合入 main）
2. 部署專案 `git fetch upstream main`
3. 部署專案內容此時與 upstream/main 一致 → 提交自然放行

> ⚠️ Core 尚未推上 main 前，部署專案的對應變更會被 hook 視為違規——這是預期行為，
> 請依上述順序處理，不要用 `--no-verify` 繞過。

### merge 近全自動 — 僅 snapshot 需一個檢查步驟

建議以 `./update-core.sh` 執行整個同步流程（fetch → 備份分支 → merge → build → test → snapshot 校驗 → 漂移守門 → 更新 `.core-version` 紀錄）；手動 `git merge upstream/main` 時注意：

- **新 migration 檔案**：Core 新增的 `.cs` 直接合入，不與 ProjectZ migration 衝突（時間戳唯一）
- **Snapshot 衝突**：`.gitattributes` 設定 `merge=ours` 自動保留 ProjectZ 版本；**runtime 安全**（EF 依 `__EFMigrationsHistory` 套用，不依 snapshot），但 **design-time 有地雷**——若 Core 該次帶入新 entity/欄位，保留下來的舊 snapshot 不含該變更，下一次 `dotnet ef migrations add` 會把 Core 已套用過的 DDL 重新生成（重複/衝突 DDL 或虛假欄位增刪）
- **因此 merge 後必跑**：`scripts/check-snapshot-drift.sh`（CI 亦會擋）。失敗時依腳本檔頭程序處理：`dotnet ef migrations add CoreSnapshotSync` → 清空 Up()/Down()（DDL 已由 Core migration 套用過）→ 保留 snapshot 更新一併 commit
- **Migration 執行**：`Program.cs` 已設定 `db.Database.Migrate()`，啟動自動套用
- **Layout／Program.cs／CLAUDE.md**：依部署專案實際歸屬逐段合併；ProjectZ 已有客製版本，不可整檔覆蓋。
- **Core 漂移控管**：`scripts/check-core-drift.sh` 以**三方比對**（merge-base／HEAD／`upstream/main`）分三類回報：
  - **就地漂移**（改了 Core 既有檔）與 **分頭新增**（fork 之後兩邊各自長出同名檔）→ 由 `scripts/core-drift-baseline.txt` 凍結（現 69 筆，目標逐步歸零），新增即 `exit 1`；新需求一律走擴充點或上送 Core
  - **落後 Core**（Core 有而本專案沒跟）→ **不擋**，但會逐條列出；決定刻意不跟的請寫進 `scripts/core-behind-acknowledged.txt` **並附理由**
  > ⚠️ 它比對的是 **HEAD 不是工作樹**：改好但還沒 commit 的修正，這裡仍會顯示為漂移。
  > ⚠️ 它**答不了**「我們落後 Core 幾個 commit」——squash 上送之下祖先數沒有意義，詳見腳本檔頭。

---

## 參考文件

### 項目級準則
- `DEVELOPMENT.md` — 本地開發環境設定、DB migration、速率限制
- `CONSULTANT-ARCHITECTURE.md` — 模組架構、顧問開發範圍
- `.claude/LOCAL_RULES.md` — ProjectZ 專屬規則
- `.claude/README.md` — `.claude/` 目錄結構說明

### 業務領域規格 🔍
**⚠️ 重要：編碼時需主動查閱相應規格檔案（不會自動納入上下文）**

| 規格檔案 | 用途 | 涉及模組 |
|---------|------|---------|
| `.claude/specifications/mfg-domain.md` | 製造執行（MFG）業務設計、Phase 規劃、API 定義 | MFG |
| `.claude/specifications/mobile-pwa.md` | 手機 PWA 應用、前後端接口、離線設計 | Mobile |
| `.claude/specifications/ord-module.md` | 訂單模組（ORD）資料模型、並發控制 | ORD |
| `.claude/specifications/inventory.md` | 庫存（INV）基礎規格 | INV |
| `.claude/specifications/inventory-advanced.md` | 庫存進階特性 | INV |
| `.claude/specifications/currency.md` | **匯率換算**（方向、快照、RateType、ICurrencyService API）。與本檔「金額欄位 Currency」章節主題不同：本檔講**小數點精度/step**，currency.md 講**外幣↔本幣換算** | 全模組 |

**套用方式**：
- 編碼涉及 MFG 模組 → 查閱 `mfg-domain.md` 確認業務規格、Phase 規劃
- 編碼涉及金額欄位 → 查閱 `currency.md` 或本檔「金額欄位」章節
- 編碼涉及手機 API → 查閱 `mobile-pwa.md` 了解前後端設計

### AI 工具命令規格
- `.claude/commands/new-screen.md` — 畫面產生完整規格（/new-screen）
- `.claude/commands/new-screen-projectz.md` — ProjectZ 補充規格（覆蓋 Core 對應段落）
- `.claude/commands/new-report.md` — 列印單據產生規格（/new-report）
- `.claude/commands/webapp-testing.md` — E2E UI 測試產生規格（/webapp-testing）
- `.claude/commands/xlsx.md` — Excel 匯出產生規格（/xlsx）

### 模組開發
- `Modules/_TEMPLATE/CLAUDE.md` — 模組開發規範（命名、注入、測試）


## 共通資安修正的歸屬與驗證（2026-11-05）

顧問新增業務功能仍走 IAppModule／partial；維護 Core 自身的共通安全邊界則先在 Core 修正，
再逐檔同步。ProjectZ 的 Program.cs 保留客製，透過 AddMesLoginRateLimiting／AddMesProxyHeaders 共用政策。
同步必要檢查失敗應非零退出，全部通過後才更新 .core-version；不得以 --skip-build 記錄同步成功。
本機 hooks 停用或 fetch 不成功代表「未驗證」，不能當作邊界檢查通過。
