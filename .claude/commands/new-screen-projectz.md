---
description: ProjectZ 專屬的畫面產生補充規範。使用 /new-screen 時，本檔的規則覆蓋或補充 new-screen.md 的對應段落。
---

# ProjectZ 畫面產生補充規範

> **本檔僅適用於 ProjectZ（顧問專案）。**
> 使用 `/new-screen` 時，必須同時套用 `new-screen.md`（Core 規格）與本檔（ProjectZ 覆蓋規格）。
> 本檔的規則優先於 `new-screen.md` 中對應的段落。

---

## 1. 檔案路徑規則（覆蓋 new-screen.md Step 2 desktop）

### 模組功能（如 BAS、PRD、WMS）
```
Modules/{MODULE}/Controllers/{KEY}Controller.cs
Modules/{MODULE}/Views/{KEY}/Index.cshtml
Modules/{MODULE}/Views/{KEY}/_Form.cshtml
Resources/Modules/{MODULE}/Views/{KEY}/*.resx（4 個）
```

### Core 功能（使用者管理、角色、廠別等，不屬於任何模組）
```
Controllers/{KEY}Controller.cs
Views/{KEY}/Index.cshtml
Views/{KEY}/_Form.cshtml
Resources/Views/{KEY}/*.resx（4 個）
```

> **模組判斷原則**：若功能屬於某個業務模組（BAS = 基礎資料，PRD = 生產，WMS = 倉儲），
> Controller 和 View 放在 `Modules/{MODULE}/` 下；其餘才放根目錄。

---

## 2. 模組 Views 基礎檔案（模組功能必做，Core 功能略過）

> ASP.NET Core 的 `_ViewImports.cshtml` 依**實體目錄**套用，不依 `RazorViewEngineOptions` 路徑。
> 模組 Views 無法繼承根 `/Views/_ViewImports.cshtml`，必須自備。

**若 `Modules/{MODULE}/Views/_ViewImports.cshtml` 尚未存在，建立它：**

```cshtml
@using SmartFactory.MES.Models
@using SmartFactory.MES.Modules.{MODULE}.Models
@using Microsoft.AspNetCore.Mvc.Localization
@inject SmartFactory.MES.Services.ICurrencyService CurrencySvc   @* 依模組需求調整 *@
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

**若 `Modules/{MODULE}/Views/_ViewStart.cshtml` 尚未存在，建立它：**

```cshtml
@{
    Layout = "/Pages/Shared/_Layout.cshtml";
}
```

> ⚠️ **缺少 `@addTagHelper`** 造成靜默失敗：`asp-for` 不產生 `name`、`asp-action` 不產生 `action`，
> 表單永遠提交空值，ModelState 永遠無效。**不會有任何編譯錯誤，只有執行時異常。**

---

## 3. DbContext 擴充（覆蓋 new-screen.md Step 3-8）

❌ **禁止修改 `Data/ApplicationDbContext.cs`**（Core 主體，隨 Core 更新同步）

在 `Data/ApplicationDbContextExtensions.cs` 的 `partial class ApplicationDbContext` 中加入：

```csharp
public DbSet<{MODEL}> {DbSet} { get; set; }
```

若有複合主鍵或唯一索引，在同一檔案的 `OnModelCreatingPartial` 中加入 Fluent API。

---

## 4. Menu 登錄（覆蓋 new-screen.md Step 3-6）

❌ **禁止修改以下 Core 檔案**（隨 Core 更新同步，顧問不得異動）：
- `Services/CoreMenuContributor.cs`
- `Resources/SharedResource.zh-TW.resx` / `en-US.resx`

**改為在 ProjectZ 專屬檔案操作：**

在 `Services/ProjectZMenuContributor.cs` 對應群組的 SubItems 插入：
```csharp
new MenuItem { Title = _localizer["{KEY}"], Controller = "{KEY}",
               Action = "Index", Icon = "bi-{MENU_ICON}", MenuKey = "{KEY}" }
```

在 `Resources/ProjectZSharedResource.zh-TW.resx` 與 `en-US.resx` 加入：
- `{KEY}` = `{中文名稱}` / `{英文名稱}`

---

## 5. Step 4 Checklist 補充項目

在執行 `new-screen.md` 的 Step 4 Checklist 之後，**額外確認以下 ProjectZ 專屬項目**：

```
模組 Views 基礎（模組功能必核對）：
[ ] Modules/{MODULE}/Views/_ViewImports.cshtml 存在且含 @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
[ ] Modules/{MODULE}/Views/_ViewStart.cshtml 存在且指定 Layout = "/Pages/Shared/_Layout.cshtml"

_Form 欄位（所有畫面必核對）：
[ ] [Required] 欄位的 label 後有 <span class="text-danger">*</span>（checkbox 旗標除外）
[ ] type="number" 欄位：數量/金額一律 step="any"；純整數（VL 等級、批數等）不寫 step 或 step="1"；絕不寫死 step="0.001" 等小數（R9）
[ ] 每個 asp-for 欄位都有對應 <span asp-validation-for> span（R6d，否則欄位級錯誤只閃不顯示）

JS 在地化（含 inline <script> 的畫面必核對）：
[ ] <script>／.js 內 alert()/confirm()/字串一律 window.i18n.t('Key')，不得硬編碼中文（R4b，會擋 CI）；key 加在 ProjectZSharedResource（zh-TW＋en-US 同步）

Menu（覆蓋 Core checklist 項目）：
[ ] ProjectZMenuContributor 已插入選單項目（❌ 不是 CoreMenuContributor）
[ ] ProjectZSharedResource 兩個 resx 已加入 MENU 文字（❌ 不是 SharedResource）

DbContext（覆蓋 Core checklist 項目）：
[ ] DbSet 已加入 Data/ApplicationDbContextExtensions.cs（❌ 不是 ApplicationDbContext.cs）
```

---

## 6. 數量欄位 UOM 小數點套用規範

**所有含數量輸入的畫面（包含主檔數量欄位與明細行數量欄位）都必須套用此規範。**

> ⚠️ **R9 — step 一律 `"any"`（spinner ±1）**：所有「跟隨 UOM/幣別精度」的
> `type="number"` 欄位（數量/金額/重量/容量…），`step` **一律 `"any"`**——上下三角形
> 每按一次 ±1、仍可鍵入小數。**絕不寫死** `step="0.0001"`/`"0.01"` 之類精確小數
> （否則 spinner 每按一次只 ±0.0001）。精度由 `min`（`GetMinPositive`）+ 伺服端
> `RoundQtyAsync`/`RoundAsync` 把關。`UomStepHelper.GetStep`/`CurrencyStepHelper.GetStep`
> 與 JS `applyUomStep`/`applyCurrencyStep`/`currencyStepOf` 皆已回 `"any"`,照本規格寫即合規。
> **例外（保留精確 step、勿套 any）**:稅率/匯率/UOM 換算比例（值域 0–1,±1 會爆量）、
> 加班/工時等刻意小步進、序號 `step="10"`。建置期由 `SmartFactory.MES.Tests` 慣例測試
> **R9** 把關（細節見 `.claude/specifications/consistency-framework.md` Phase E）。

`wwwroot/js/uom-step.js` 已在 `_Layout.cshtml` 全域載入，提供以下全域函式：
- `window.uomDecimals`：每個表單以 `Object.assign` 注入
- `window.applyUomStep(el, uomCode, opts)`：同步設定 `step` / `min`
- `window.applyUomStepAsync(el, uomCode, opts)`：非同步（先從快取，再 API fetch）
- `window.uomStepOf(uomCode)`：回傳「該 UOM 最小正值」（如 `"0.01"`,供 `min` 用；**非** spinner step——step 由 `applyUomStep` 設為 `"any"`）
- `window.fetchUomDecimals(uomCode)`：快取 + fetch `/api/uom/{code}/decimals`

### Controller（含數量欄位的畫面 Controller）

```csharp
// 1. 注入 IUomService（Modules/BAS/Services/IUomService.cs）
private readonly IUomService _uomService;

// 2. PrepareFormDropdowns 加入：
ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

// 3. POST 儲存前對所有數量欄位進行四捨五入：
var qty      = await _uomService.RoundQtyAsync(vm.UomCode, vm.Qty);
var stockQty = await _uomService.RoundQtyAsync(vm.StockUomCode, vm.StockQty);
// decimal? 欄位需先判斷 HasValue：
if (model.BoxQty.HasValue)
    model.BoxQty = await _uomService.RoundQtyAsync(model.StockUom, model.BoxQty.Value);
```

### View（含數量輸入的 .cshtml）

```cshtml
@* 1. 在檔案頂部加入 using *@
@using SmartFactory.MES.Modules.BAS.Services
@* 2. 宣告 ViewBag 取得值（若有固定單位，可只取對應的 step） *@
var uomDecimals = ViewBag.UomDecimals as IReadOnlyDictionary<string, int>;
@* 固定單位範例（主檔單一數量欄位）：*@
var stockStep = UomStepHelper.GetStep(uomDecimals, Model?.StockUom);

@* 3. 數量 input：加 step / min / class *@
<%-- 明細行（動態 UOM）--%>
<input ... type="number" class="form-control line-qty"
       step="@UomStepHelper.GetStep(uomDecimals, line.UomCode)"
       min="@UomStepHelper.GetMinPositive(uomDecimals, line.UomCode)" />

<%-- 主檔固定 UOM 欄位（不隨行異動）--%>
<input asp-for="SafetyStock" type="number" step="@stockStep" min="0" class="form-control stock-qty-input" />

@* 4. UOM 下拉：加 class + onchange *@
<select ... class="form-select line-uom" onchange="onUomChange(this)">

@* 5. 固定 UOM 下拉（主檔，切換時更新所有 .stock-qty-input）：*@
<select asp-for="StockUom" class="form-select" onchange="onStockUomChange(this.value)">
```

```html
<script>
    // 注入所有 UOM 小數位數（必填）
    window.uomDecimals = Object.assign(window.uomDecimals || {},
        @Html.Raw(System.Text.Json.JsonSerializer.Serialize(uomDecimals ?? new Dictionary<string,int>())));

    // 明細行 UOM 切換
    function onUomChange(sel) {
        var qty = sel.closest('tr').querySelector('.line-qty');
        window.applyUomStep(qty, sel.value);
    }

    // 主檔固定 UOM 切換（一次更新全部 .stock-qty-input）
    function onStockUomChange(uomCode) {
        document.querySelectorAll('.stock-qty-input')
            .forEach(el => window.applyUomStep(el, uomCode, { minPositive: false }));
    }

    // addLine() 新增動態行後，補上 applyUomStep：
    function addLine() {
        // ... 建立 tr ...
        document.getElementById('linesTbody').appendChild(tr);
        // 若品號選單有預設 purchaseUom，呼叫 applyUomStepAsync：
        const uomSel = tr.querySelector('.line-uom');
        const qtyInp = tr.querySelector('.line-qty');
        if (uomSel && qtyInp) window.applyUomStep(qtyInp, uomSel.value);
    }

    // onProductChange：品號切換時同步設定 UOM step
    function onProductChange(sel, idx) {
        const purchaseUom = sel.selectedOptions[0]?.dataset.purchaseUom || '';
        const row = sel.closest('tr');
        const uomSel = row.querySelector(`select[name="Lines[${idx}].UomCode"]`);
        if (uomSel && purchaseUom) uomSel.value = purchaseUom;
        const qty = row.querySelector('.line-qty');
        if (qty && purchaseUom) window.applyUomStepAsync(qty, purchaseUom);
    }
</script>
```

### UomStepHelper（靜態輔助，可在 View 任何地方呼叫）

```csharp
// 位於 Modules/BAS/Services/UomStepHelper.cs
UomStepHelper.GetStep(uomDecimals, uomCode)        // 一律 "any"（spinner ±1）
UomStepHelper.GetMinPositive(uomDecimals, uomCode) // "1" | "0.001" | …（入料最小正值,供 min）
UomStepHelper.GetStep(decimalPlaces)               // 純 int 版本,亦回 "any"
```

### Step 4 Checklist 補充項目（數量欄位）

```
UOM 小數點套用（含數量欄位的畫面必核對）：
[ ] Controller PrepareFormDropdowns 已加 ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync()
[ ] View @using SmartFactory.MES.Modules.BAS.Services 已加入
[ ] 所有數量 input 已加 step / min（UomStepHelper）+ 對應 class（line-qty 或 stock-qty-input）
[ ] 所有 UOM select 已加 class="line-uom" + onchange="onUomChange(this)"（或主檔版本）
[ ] Script 已注入 window.uomDecimals Object.assign
[ ] POST 儲存前對每個數量欄位呼叫 await _uomService.RoundQtyAsync(uomCode, qty)
```

---

## 7. 金額欄位 Currency 小數點套用規範

**所有含金額輸入的畫面（UnitPrice、LineAmount、TaxAmount、TotalAmount、StandardCost 等）都必須套用此規範。**

`wwwroot/js/currency-format.js` 已在 `_Layout.cshtml` 全域載入，提供以下全域函式：
- `window.currencyDecimals`：每個表單以 `Object.assign` 注入
- `window.currencyDp(currencyCode)`：回傳幣別小數位數（int）
- `window.currencyStepOf(currencyCode)`：回傳 step 字串（一律 `"any"`,spinner ±1）
- `window.applyCurrencyStep(el, currencyCode)`：設定 input 的 `step` 屬性
- `window.roundHalfAway(value, dp)`：對齊 C# `MidpointRounding.AwayFromZero`
- `window.fmtMoney(value, currencyCode)`：格式化顯示金額

### Controller

```csharp
// 1. 注入 ICurrencyService
private readonly ICurrencyService _currencyService;

// 2. PrepareFormDropdowns 加入：
//    單一本位幣（INV 等不需幣別選擇的畫面）：
ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

//    多幣別行（PUR 等有幣別 select 的畫面）：
ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();

// 3. ResolveBaseCurrencyDpAsync 標準寫法（每個 Controller 加一次）：
private async Task<int> ResolveBaseCurrencyDpAsync()
{
    var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
    if (string.IsNullOrEmpty(baseCurrency)) return 2;
    var map = await _currencyService.GetAllDecimalPlacesAsync();
    return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
}

// 4. POST 儲存前對每個金額欄位四捨五入：
amount = await _currencyService.RoundAsync(currencyCode, amount);
```

### View（單一本位幣，使用 baseDp）

```cshtml
@using SmartFactory.MES.Modules.BAS.Services
@{
    int baseDp = ViewBag.BaseCurrencyDp as int? ?? 2;
}

@* step 一律 "any"（R9,spinner ±1）；value 用 "F"+baseDp 控顯示位數,精度由伺服端 RoundAsync 把關 *@
<input type="number"
       step="any"
       value="@(Model!.UnitCost.ToString("F" + baseDp))"
       class="form-control" />
```

### View（多幣別行，使用 CurrencyStepHelper）

```cshtml
@using SmartFactory.MES.Modules.BAS.Services
@{
    var currencyDecimals = ViewBag.CurrencyDecimals as IReadOnlyDictionary<string, int>;
}

@for (int i = 0; i < Model!.Lines.Count; i++)
{
    var line = Model!.Lines[i];
    var cdp  = CurrencyStepHelper.GetDp(currencyDecimals, line.CurrencyCode);  // ← 每行取一次

    <%-- step 控制輸入精度；value 用 "F"+cdp 避免 NUMERIC(18,4) 欄位顯示 4 位小數 --%>
    <input name="Lines[@i].UnitPrice" type="number"
           step="@CurrencyStepHelper.GetStep(currencyDecimals, line.CurrencyCode)"
           value="@(line.UnitPrice.ToString("F" + cdp))"
           class="form-control line-price" />
    <input name="Lines[@i].LineAmount" type="number" readonly
           step="@CurrencyStepHelper.GetStep(currencyDecimals, line.CurrencyCode)"
           value="@(line.LineAmount.ToString("F" + cdp))"
           class="form-control line-lineamt" />
    <input name="Lines[@i].TaxAmount" type="number" readonly
           step="@CurrencyStepHelper.GetStep(currencyDecimals, line.CurrencyCode)"
           value="@(line.TaxAmount.ToString("F" + cdp))"
           class="form-control line-taxamt" />
    <input name="Lines[@i].TotalAmount" type="number" readonly
           step="@CurrencyStepHelper.GetStep(currencyDecimals, line.CurrencyCode)"
           value="@(line.TotalAmount.ToString("F" + cdp))"
           class="form-control line-totalamt" />
}
```

> **為何需要 `"F" + cdp`？** PostgreSQL `NUMERIC(18,4)` 欄位讀回 C# 的 `decimal` 時，
> 內部 scale 保留 4，`@line.UnitPrice` 直接渲染會輸出 `value="1500.0000"`，
> 瀏覽器顯示 4 位小數。`ToString("F2")` 強制輸出 `"1500.00"`，
> 顯示位數與幣別設定一致。

```html
<script>
    // 注入所有幣別小數位數（多幣別畫面必填）
    window.currencyDecimals = Object.assign(window.currencyDecimals || {},
        @Html.Raw(System.Text.Json.JsonSerializer.Serialize(currencyDecimals ?? new Dictionary<string,int>())));

    // 幣別切換時更新所有金額欄位的 step
    function onCurrencyChange(sel) {
        const row = sel.closest('tr');
        ['.line-price', '.line-lineamt', '.line-taxamt', '.line-totalamt'].forEach(s => {
            const el = row.querySelector(s);
            if (el) window.applyCurrencyStep(el, sel.value);
        });
        calcLine(row);
    }

    // calcLine：用 roundHalfAway 計算並寫回 value
    function calcLine(row) {
        const qty     = parseFloat(row.querySelector('.line-qty')?.value)   || 0;
        const price   = parseFloat(row.querySelector('.line-price')?.value) || 0;
        const taxRate = parseFloat(row.querySelector('.line-taxrate')?.value) || 0;
        const exRate  = parseFloat(row.querySelector('.line-exrate')?.value)  || 1;
        const ccy     = row.querySelector('.line-currency')?.value || '';
        const dp      = window.currencyDp(ccy);
        const lineAmt  = window.roundHalfAway(qty * price, dp);
        const taxAmt   = window.roundHalfAway(lineAmt * taxRate, dp);
        const totalAmt = window.roundHalfAway((lineAmt + taxAmt) * exRate, dp);
        const laEl = row.querySelector('.line-lineamt');
        const taEl = row.querySelector('.line-taxamt');
        const ttEl = row.querySelector('.line-totalamt');
        if (laEl) laEl.value = lineAmt;
        if (taEl) taEl.value = taxAmt;
        if (ttEl) ttEl.value = totalAmt;
    }
</script>
```

### 多幣別行 UI 慣例（欄位順序 + 金額即時收斂）

1. **幣別欄放在「單價」欄之前**:多幣別明細表頭與每列,`CurrencyCode` 欄一律排在
   `UnitPrice` 之前（thead `<th>`、server `@for` 列、JS addLine 模板三處都要一致）。
   使用者先選幣別再輸單價,且讓下述「即時收斂」一開始就取到正確小數位數。
2. **編輯金額即時依幣別 dp 收斂(自動,免額外程式)**:`currency-format.js` 已內建
   capture 階段委派——當 `.line-price` 變更且**同列有 `.line-currency`** 時,自動以該列
   幣別 dp 收斂顯示值(先於該欄 `onchange="calcLine"`)。新畫面只要 (a) 幣別 select 用
   `class="line-currency"`、(b) 單價 input 用 `class="line-price"` 且兩者同 `<tr>` 即生效。
   單一本位幣畫面(無 per-row 幣別)不掛此收斂,精度仍由 `calcLine` + 伺服端 `RoundAsync` 保證。
   需要手動收斂某欄時可呼叫 `window.roundInputToCurrency(el, code)`。

### CurrencyStepHelper（靜態輔助，可在 View 任何地方呼叫）

```csharp
// 位於 Modules/BAS/Services/CurrencyStepHelper.cs
CurrencyStepHelper.GetStep(currencyDecimals, currencyCode)  // 一律 "any"（spinner ±1）
CurrencyStepHelper.GetDp(currencyDecimals, currencyCode)    // int，預設 2（供 "F"+dp 顯示）
```

### Step 4 Checklist 補充項目（金額欄位）

```
Currency 小數點套用（含金額欄位的畫面必核對）：
[ ] Controller 已注入 ICurrencyService
[ ] PrepareFormDropdowns 已加 ViewBag.BaseCurrencyDp 或 ViewBag.CurrencyDecimals（依畫面類型）
[ ] 所有金額 input 的 step 一律 "any"（R9；CurrencyStepHelper.GetStep 已回 "any"，或直接寫 step="any"）
[ ] 多幣別行：幣別欄排在單價欄之前；.line-price + 同列 .line-currency 即由 currency-format.js 自動依幣別 dp 收斂
[ ] 所有金額 input 的 value 已用 ToString("F" + cdp) 或 ToString("F" + baseDp) 格式化（⚠️ 避免 NUMERIC(18,4) 顯示 4 位）
[ ] 多幣別行：@for 迴圈內已宣告 var cdp = CurrencyStepHelper.GetDp(...)
[ ] Script 已注入 window.currencyDecimals Object.assign（多幣別畫面）
[ ] POST 儲存前對每個金額欄位呼叫 await _currencyService.RoundAsync(currencyCode, amount)
```

---

## 8. 常見錯誤（ProjectZ 專屬，補充 new-screen.md 錯誤表）

| 錯誤 | 正確做法 |
|------|---------|
| 模組 Views 下沒有 `_ViewImports.cshtml` | 模組 Views（`/Modules/{N}/Views/`）不繼承根 `/Views/_ViewImports.cshtml`；必須自備含 `@addTagHelper` 的版本，否則所有 Tag Helper 靜默失效 |
| 模組 Views 下沒有 `_ViewStart.cshtml` | 缺少 Layout 指定，頁面無側邊選單與導覽列 |
| Export switch case 寫 `"excel"` | View 的 `asp-route-format` 傳 `"xlsx"`；`"excel"` 永遠不匹配，落入 `_` 回傳 JSON |
| Menu 登錄寫入 `CoreMenuContributor` | 顧問 Menu 一律寫 `ProjectZMenuContributor`，Core 檔案不得修改 |
| i18n 加入 `SharedResource.zh-TW.resx` | 顧問 i18n 一律寫 `ProjectZSharedResource`，Core 檔案不得修改 |
| DbSet 寫入 `ApplicationDbContext.cs` | 顧問 Entity 一律加在 `ApplicationDbContextExtensions.cs` |
| 清單中直接顯示 `@item.CurrencyCode`、`@item.StockMode` 等代碼欄位 | 必須在 Controller 載入對應 `ViewBag.{Field}Names`（`Dictionary<string,string>`），View 用 `TryGetValue` 顯示名稱 |
| 用 inline 事件屬性 `onclick="fn()"` / `onchange="fn(this)"` / `onsubmit="...return false;"` | 一律改用 `data-action` 委派（見第 10 節）；inline on*= 會在 CSP 收緊（script-src 加 nonce）時全部失效 |

---

## 9. 一致性框架（必用，取代手抄樣板）

> 📐 完整設計與規則見 `.claude/specifications/consistency-framework.md`（四支柱、Core/ProjectZ 歸屬、Phase A–C、驗證在地化 Core 修根）。產生畫面前必讀。

新畫面**必須**使用共用框架，不得手抄重複樣板（手抄＝Vibe Coding 遺漏來源；建置期 R1–R9（含 R4b/R5b/R6b）＋ Phase F 的 R6c 測試會把關；R10 評估後撤案）：

| 項目 | 規定 | 檔案 |
|------|------|------|
| 控制器 | 繼承 `ModuleControllerBase`（取代手寫 `IsAjax`、權限 ViewBag、JSON 契約、列印 ViewBag）。建構式注入不變。 | `Modules/_Shared/ModuleControllerBase.cs` |
| Index 腳本 | `@section Scripts` 第一行 `@await Html.PartialAsync("_CrudPanelScripts")`；不得自抄 `openPanel/submitPanel/...` | `Views/Shared/_CrudPanelScripts.cshtml`、`wwwroot/js/crud-panel.js` |
| JS 字串 | 一律 `window.i18n.t('Key')`，**禁止**在 `<script>` 或 `.js` 硬編碼 CJK；key 加在 `ProjectZSharedResource`（zh-TW＋en-US 同步） | `Views/Shared/_JsLocalization.cshtml` |
| 權限 | Index 用 `await PrepareIndexPermissionsAsync(MenuKey)` 一行帶入四旗標 | 基底方法 |
| JSON 回傳 | 成功 `return JsonOk(...)`、失敗 `return JsonFail(msg)`（不取名 Ok/Fail 以免遮蔽 `ControllerBase.Ok`）；驗證失敗 `return ValidationPartial("_Form", model)` | 基底方法 |
| 動態子表 | 凡用 JS `append` 新增 input 列（`addLine` 等），插入 DOM 後**必須**呼叫 `window.armValidation(form)` 重註冊 unobtrusive 驗證（否則動態列僅靠伺服端兜底、缺即時前端紅字）；建置期 R6c 把關（Phase F） | `wwwroot/js/crud-panel.js` |
| resx 欄位名 key | 通用欄位名優先**重用**既有顯示名（驗證訊息由 Core 的鏈式 provider 解析 `SharedResource` → `ProjectZValidationResource`，**R5b** 把關其存在）；Core 的 `SharedResource` **唯讀**，顧問模組新欄位一律加在 `ProjectZValidationResource`（❌ 不是 `ProjectZSharedResource`，那是選單／畫面用語，會撞名）。勿新造近義重複（如 `CreatedDate` vs `CreateDate`）。view 標籤仍各自放 per-view resx（與全域同名屬正常、非重複） | `Resources/ProjectZValidationResource.*.resx` |

> 既有畫面分批回補；新畫面一律直接套框架。

---

## 10. 事件繫結：data-action 委派（取代 inline on*=，CSP 強化）

**新畫面一律不得使用 inline 事件屬性**（`onclick` / `onchange` / `onsubmit` / `oninput` / `onkeyup` …）。
改用全域 `data-action` 事件委派——由 `wwwroot/js/data-action.js`（已於 `_Layout` 全域載入）
在 `document` 層級統一監聽。

> **為什麼**：CSP3 一旦於 `script-src` 加入 `nonce-source`，瀏覽器即**忽略** `'unsafe-inline'`，
> 屆時所有 inline `on*=` 屬性都會失效（且 inline 屬性無法帶 nonce）。改 data-action 後，
> 行為集中於一支可帶 nonce 的外部 JS，與未來移除 `'unsafe-inline'` 並存無虞。
> 詳見 `.claude/reviews/overall-assessment.md` P2「移除 script-src 'unsafe-inline'」。

### 對照表

| inline 寫法（禁止） | data-action 寫法（正確） |
|---------------------|--------------------------|
| `onclick="openPanel('/X/Create')"` | `data-action="openPanel" data-url="/X/Create"` |
| `onchange="onUomChange(this)"` | `data-action-change="onUomChange"` |
| `onsubmit="submitPanel(this, event.submitter); return false;"` | `data-action-submit="submitPanel"` |
| `onchange="this.form.submit()"`（pageSize） | `data-action-change="submitOwnForm"`（共用小工具，見下） |

- 預設 `data-action` 綁 **click**；其他事件用 `data-action-{event}`（change / input / submit / keyup / blur）。
- 同一元素可並存多種（如 `<select data-action-change="..." data-action="...">`）。

### 全域函式簽章

```js
// @section Scripts 內定義；this === el === 觸發元素（與舊 inline 的 this 等義）
window.openPanel = function (e, el) {
    var url = el.dataset.url;          // 參數一律從 el.dataset 讀（取代 fn('arg')）
    // ...
    return false;                      // 回傳 false → dispatcher 代呼 preventDefault（對齊 "return false;"）
};
```

- 需要參數時放 `data-*`，函式內以 `el.dataset.xxx` 取用。
- 回傳 `false` 等同舊 inline 的 `; return false;`（阻止表單送出 / 連結跳轉）。
- 動態插入的節點**無需重綁**（委派在 document 層級）；`window.bindDataActions()` 為相容 no-op。

### Step 4 Checklist 補充項目（事件繫結）

```
[ ] View 內無任何 inline on*= 屬性（grep 'on(click|change|submit|input|keyup)=' 應為 0）
[ ] 互動元素改用 data-action / data-action-{event}
[ ] 對應 window.<fn> 簽章為 (event, el)，參數從 el.dataset 讀取
[ ] 需阻擋預設行為者回傳 false
```

> ⚠️ **既有 880 個 inline handler（141 檔）為存量債**，依 `overall-assessment.md` 分模組逐批回補、
> 以 E2E 驗證後，最終才移除 CSP 的 `'unsafe-inline'`。本節僅約束**新畫面**止住 inflow。

---

## 11. 使用手冊頁（新畫面必產出）

新畫面除了程式，**必須同時產出 `wwwroot/docs/user/{module}/{controller-kebab}.html` 手冊頁**，
並把 controller 名稱加入 `Services/HelpDocResolver.cs` 對應模組清單——導覽列的「?」說明連結由此解析。
`HelpDocResolverTests.Resolve_EveryRegisteredController_MapsToExistingManualPage` 守門，漏補會紅燈。

> ⚠️ 該測試只驗**檔案存在**，不驗內容。截圖與欄位說明表沒補不會紅燈，
> 必須靠本節 checklist 自覺執行（2026-07-28 曾一次回補 4 張缺圖與 105 頁缺欄位說明表）。

> 🪤 **稽核陷阱**：不能用「頁面有沒有 `<table>` / `field-table`」判斷有沒有欄位說明——
> 作業型單據頁另有一張共用的**「單據狀態」表**（草稿／待審核／已確認／已過帳／已作廢），
> 同樣掛 `class="field-table"`。2026-07-28 首輪盤點就是這樣把 25 張單據頁（銷售訂單、
> 出貨單、銷退單、內部訂單、採購/請購/進退貨、備料單、報廢單、調整單、調撥單…）
> 誤判為「已有欄位說明」而整批漏掉。**正確判準是頁內有沒有 `<h2>…欄位說明</h2>` 段**：
>
> ```bash
> # 找出缺欄位說明的畫面頁
> for f in wwwroot/docs/user/*/*.html; do
>   [ "$(basename $f)" = index.html ] && continue
>   grep -q '欄位說明' "$f" || echo "$f"
> done
> ```

### 頁面骨架（照抄同模組既有頁最省事）

```
navbar → sidebar（列出同模組全部畫面，當前頁加 class="active"）→ main：
  .doc-header（h1 圖示＋畫面名／小字：路由 or 選單位置 | 一句話用途）
  <p> 一段用途說明
  <img class="screen-shot" src="img/{controller-kebab}.png" loading="lazy">
  <div class="screen-shot-caption">…（示意，實際欄位/資料依權限與查詢條件而異）</div>
  <h2><i class="bi bi-search …"></i>查詢</h2>          ← .step-row + .step-badge 逐步驟
  <h2><i class="bi bi-list-check …"></i>作業流程</h2>
  <h2><i class="bi bi-table …"></i>欄位說明</h2>        ← 見下
  <h2><i class="bi bi-link-45deg …"></i>相關畫面</h2>
```

### 截圖

- 放 `wwwroot/docs/user/{module}/img/{controller-kebab}.png`，viewport **1440×900**（與既有頁一致）。
- **要截有資料的畫面**：查詢型畫面先帶查詢字串（如 `?searchProduct=PA1104&searched=true`）再截，
  空狀態（「請選擇…後查詢」）對讀者沒有資訊量。
- 產生方式：`dotnet run` 起 `:5110` → Playwright 登入 `/Identity/Account/Login`
  （欄位 id 是 `#Input_Email` / `#Input_Password`，**不是** `UserName`）→ 逐頁 goto + screenshot。
  `appsettings.Development.json` 的 `TwoFactorEnforcementEnabled: false` 讓本機免 TOTP。

### 欄位說明表

每頁「欄位說明」段至少一張 `table.field-table`，欄位名稱**取自 View 實際渲染的中文標籤**
（`Resources/Modules/{MODULE}/Views/{Controller}/Index.zh-TW.resx` 的值，不是 C# 屬性名）：

```html
<h3>查詢條件</h3>
<table class="table table-sm field-table border">
  <thead><tr><th class="u-width-20pct">條件</th><th>說明</th></tr></thead>
  <tbody><tr><td>期間（YYYYMM）</td><td>以會計期間篩選，如 202607</td></tr></tbody>
</table>
<h3>清單欄位</h3>
<table class="table table-sm field-table border">
  <thead><tr><th class="u-width-28pct">欄位</th><th>說明</th></tr></thead>
  <tbody><tr><td>品號</td><td><span class="badge bg-danger">必填</span> 唯一料號，建立後不可修改</td></tr></tbody>
</table>
```

- **維護型畫面**分「表頭 / 明細」兩表，必填欄位加 `<span class="badge bg-danger">必填</span>`。
- **報表型畫面**分「查詢條件 / 報表欄位」兩表；計算欄位要寫出**算式與口徑**
  （例：`OEE ＝ 可用率 × 表現性 × 良率`、`庫存天數(DSI) ＝ 365 ÷ 周轉率`），
  只寫「顯示 OEE」等於沒寫。
- 寬度只能用既有工具類別 `u-width-12pct / 20pct / 28pct`（CSP 禁內聯 `style=`，
  同理不得用 `<style>` 區塊——BAS 是逐頁 `{page}.page.css`，其餘模組共用 `screen.page.css`）。

### Step 4 Checklist 補充項目（使用手冊）

```
[ ] wwwroot/docs/user/{module}/{controller-kebab}.html 已建立，sidebar 同模組各頁互連
[ ] controller 已加入 HelpDocResolver 的所屬模組清單
[ ] img/{controller-kebab}.png 已截圖（1440×900、畫面帶資料非空狀態）
[ ] 「欄位說明」段有 field-table：維護型＝表頭/明細，報表型＝查詢條件/報表欄位
[ ] 計算欄位已寫出算式與口徑；必填欄位有「必填」badge
[ ] 無內聯 style=／<style>，寬度用 u-width-* 工具類別
[ ] 模組 index.html 的畫面清單已加入本頁
```

---

## 12. 對話型畫面契約（S-7 類；CRUD 契約不適用時走這一份）

**適用對象**：沒有清單／分頁／排序／側欄表單的畫面，目前只有 Q&A 對話畫面（S-7）。
`/new-screen` 的 CRUD 契約套不上去——**前例**是 BAS 模組的畫面契約與 `_CrudPanelScripts`
不相容，當時的結論是不強推。

> **這一節必須先於畫面存在。** 規格 `.claude/specifications/llm-qa-and-report-tools.md` §16.3
> 明文要求「S-7 動工前應先補一份對話型畫面契約，而不是在 PR 裡臨時發明」。
> 理由很實際：契約若在 PR 裡才長出來，它會被畫面倒推出來，而不是反過來。

### 12.1 CRUD 契約中**不適用**的項目（唯一的豁免清單）

查詢列、`searched=true`、`PaginatedList<T>` 分頁、欄頭排序、Side Panel offcanvas、
`submitPanel`、Anti-Forgery 隱藏欄以外的表單慣例。**豁免僅限這些**，其餘一律照舊。

### 12.2 照樣適用、沒有例外的七項

| # | 項目 | 出處 |
|---|---|---|
| 1 | 選單登錄鏈：`CoreMenuContributor` ＋ `SharedResource` 中英雙鍵，MenuKey 對齊 Controller | 本檔 §4 |
| 2 | 使用手冊頁 ＋ `HelpDocResolver` 登錄 | 本檔 §11 |
| 3 | 無 inline `on*=`，一律 `data-action` 委派 | 本檔 §10 |
| 4 | 無內聯 `style=`／`<style>`，寬度用 `u-width-*` | 本檔 §11 |
| 5 | i18n：不得硬編碼中文。⚠️ 舊 View 一旦 `@inject` localizer，整頁硬編碼 CJK 會**一次爆成新違規**（Cx6／Cx7） | 一致性框架 |
| 6 | 權限：`IPermissionService` 閘門，MenuKey `QA_ASSISTANT` | 規格 §17.4 |
| 7 | **新群組必須登錄 `GroupToModule` ＋ `Matrix`** | 規格 §17.4 |

> ⚠️ 第 7 項是本類畫面最容易踩的：`RolePermissionSeeder` 步驟 3b 會在**每次啟動**
> 把「從未出現過的選單鍵」自動補給角色。沒登錄 `GroupToModule` → 落入 `_UNKNOWN`
> → default-deny → **畫面做完了沒有人看得到**；照抄 RPT 那一列 `(F,R,R,R,R,R,R)`
> → **開燈那一刻 OP 作業員自動拿到 LLM 助理權限，沒有人按過任何按鈕**。
> 第一版矩陣用 `(F, N, N, N, N, R, N)`。

### 12.3 輸出渲染：最高風險項

**LLM 輸出絕不可當 HTML 渲染。** 預設路徑是 `textContent`，不是 `innerHTML`。

```js
// ✅ 預設：純文字，連 sanitize 都不必經過
bubble.textContent = answer.body;

// ⚠️ 只有在確定要支援 Markdown 時才走這條，且白名單必須收窄
bubble.innerHTML = DOMPurify.sanitize(rendered, {
    ALLOWED_TAGS: ['p', 'br', 'ul', 'ol', 'li', 'strong', 'em', 'code', 'pre'],
    ALLOWED_ATTR: [],          // 不給任何屬性 → 連 href 都不開
});
```

- DOMPurify 已由 [`Pages/Shared/_Layout.cshtml`](../../Pages/Shared/_Layout.cshtml) 全域載入，**不要另外引入**。
- 「先 sanitize 再 innerHTML」不是預設選項而是**例外**：預設不渲染標記，
  就沒有「白名單開太寬」這個失敗面。

### 12.4 引文行必須可查證，而且只有真的存在的頁才畫成連結

`QaPromptBuilder.Parse` 回傳 `(Body, Citations, IsNoAnswer)`。

**分類的依據是 `QaPromptBuilder.SelectWithinBudget(hits)`——真的進了 prompt 的那幾塊，
不是檢索回傳的全部**（被字數預算丟掉的塊模型根本沒看到）。三種形狀，缺一不可：

| 引文 | 呈現 | 為什麼 |
|---|---|---|
| 手冊（`/docs/user/<module>/…`） | **真連結** | 那些檔案真的在 `wwwroot` 底下 |
| 擷取物（`rules/<slug>.html`） | **就地展開**（可摺疊，標題 `HeadingPath`、內容 `Text`），不畫 `<a>` | 它**只是頁面代號，不是網址**；磁碟上沒有這個檔，而且刻意不產（`wwwroot` 匿名可讀） |
| `given` 以外的代號（幻覺） | **純文字並標示「無從查證」**，不展開也不連結 | 見下 |

> ⚠️ **本節第三列 2026-08-14 依定案 #47 改過，原文是「過濾掉的引文不顯示」。**
> 改的理由：不顯示的話，「模型編了一個頁碼」與「模型只引了一頁」在畫面上完全一樣，
> 而前者正是使用者最該提高警覺的訊號。同理**不可展開成空白區塊**——那會把
> 「編出來的頁碼」偽裝成「這頁剛好沒內容」。詳見
> `llm-qa-and-report-tools.md` §10.11.12 與定案 #47。

- 幻覺引文是實測到的行為（六顆候選中出現 0～15 次），不是理論風險——
  **畫面端必須自己再分類一次**，不能假設模型守規矩。
- 比對一律**序數、區分大小寫**：`ManualGenerationBenchmarkTests` 判幻覺用的是
  `given.Contains(c)`。畫面放寬成不分大小寫的話，**量到的品質與使用者看到的東西
  從此不是同一件事**。
- `IsNoAnswer` 要渲染成**明確的狀態**（「使用手冊中查無相關說明」），不是空白氣泡。

### 12.5 延遲：這是本類畫面的 UX 主軸，不是邊角

定案模型 `gemma4:latest` 實測 **52.8 秒/題**，六顆候選的全距是 **41.5–267.6 秒/題**
（開發機無獨顯、一律 CPU 推論；定案見 llm-qa-and-report-tools.md 定案 #44）。
`ILlmClient` 目前**只有非串流**的 `ChatAsync`。因此：

- 送出後**立即**鎖住輸入框並顯示可取消的等待狀態。「按鈕轉個圈」在兩分鐘的等待上不夠。
- 取消必須真的送出 `CancellationToken`，不是只把畫面上的東西藏起來。
- 要做串流就**擴充 `ILlmClient`**，不得在 Controller 直接打 Ollama 繞過契約
  （`think:false`、逾時、截斷判定都在那一層，繞過去等於全部重寫一次）。

### 12.6 絕不可做的兩件事

1. **使用者的提問與模型的答案，一律不得寫進向量庫**（規格 §8.7：業務資料絕不可進入向量庫）。
2. **不得為了「讓它答得出來」而放寬檢索的權限過濾。** 答不出來是正確行為。

### 12.7 管理動作（手動重建索引）

S-7 一併提供。它需要一個畫面 → 就要走完整條登錄鏈 ＋ MenuKey ＋ 權限，
所以刻意押到這裡一起做；在它存在之前，重建索引的唯一觸發方式是重啟應用程式。

### Step 4 Checklist 補充項目（對話型畫面）

```
[ ] 答案預設以 textContent 渲染；若走 innerHTML 則 DOMPurify 白名單已收窄且 ALLOWED_ATTR 為空
[ ] 引文依 given（SelectWithinBudget 的結果）分成三類：手冊連結／擷取物就地展開／幻覺標為無從查證
[ ] 分類比對為序數且區分大小寫（與 ManualGenerationBenchmarkTests 的幻覺判定等價）
[ ] 若引文類別以列舉傳到前端，該列舉帶 [JsonConverter(typeof(JsonStringEnumConverter))]
    （本專案未全域註冊，預設序列化成數字，比對必然落空且不會報錯）
[ ] IsNoAnswer 渲染為明確狀態，不是空白
[ ] 送出後鎖輸入 + 可取消，且取消真的送出 CancellationToken
[ ] 未在 Controller 直接呼叫 Ollama（一律經 ILlmClient）
[ ] 提問與答案未寫入任何向量庫欄位
[ ] MenuKey QA_ASSISTANT 已登錄 CoreMenuContributor + 兩份 resx
[ ] 新選單群組已登錄 GroupToModule + Matrix，且第一版為 (F, N, N, N, N, R, N)
[ ] 手冊頁已產出並登錄 HelpDocResolver
[ ] View 內無 inline on*= 與無內聯 style=
```

---


## 廠區寫入上下文（2026-11-06，security-review-20261105 §5 #3）

- 桌機 `_Layout` 載入 `_PlantContextScripts`（`wwwroot/js/plant-context.js`）：把畫面渲染當下的廠別寫進
  `<meta name="mes-plant-context">`，同源的 fetch、jQuery AJAX 與原生 POST 表單自動帶 `X-Plant-Context`
  （原生表單用隱藏欄位 `__PlantContext`）。**新畫面不需自己帶**；自行用原生 `XMLHttpRequest` 或
  `navigator.sendBeacon` 送寫入的話不會被覆蓋，必須手動加標頭。
- 手機 `apiCall` 帶目前頁面的廠別；離線佇列帶**排入當下**的 plantId，不得改成重送當下的廠別。
- `StalePlantContextFilter.RequiresContext` 是強制範圍的單一來源：MFG／INV／PUR／ORD／QCR／FIN／ACT 全部、
  BAS 與手機 API 依名單。**在 BAS 或 `Api/Mobile` 新增控制器時，必須加進 `BasPlantControllers`／
  `BasGlobalControllers`（或手機的兩組名單）**——`StalePlantContextFilterTests` 會列舉實際控制器，沒歸類就紅。
  判斷依據：寫入的是 `IPlantEntity` 就列廠別，否則列全域。
- 缺少或不一致一律 409、不執行動作，畫面顯示伺服器訊息並保留使用者輸入；不得自動切換廠別或改成新廠資料重送。
  離線佇列收到這種 409 會把該筆標成錯誤保留（不遺失），由使用者確認後處理。
- 整合測試的 `TestWebApplicationFactory` 預設帶 P1 快照；測其他廠別時要換掉 `X-Plant-Context` 標頭
  （範例：`CrossPlantTransferPostingResidueTests.UseRenderedPlant`）。
