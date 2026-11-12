---
description: SmartFactory MES 匯率換算規範。凡畫面涉及幣別、金額換算、匯率顯示，必須依此規範實作。
---

# SmartFactory MES — 匯率換算規範

> **此規範在任何涉及外幣金額的畫面時自動套用（無需手動呼叫）。**

---

## 一、核心設計原則

| 原則 | 說明 |
|------|------|
| **匯率方向** | 1 外幣 = Rate 本幣（例：本幣 TWD，USD BuyRate=32 → 1 USD = 32 TWD）。`幣別維護` 即以此「本幣/外幣」方向輸入與儲存 `CurrencyRate.BuyRate/SellRate` |
| **換算方向（務必遵守）** | **本幣 = 外幣 × Rate**（乘法）；**外幣 = 本幣 ÷ Rate**。⚠️ 求本幣金額一律「乘」 |
| **匯率取用** | `RateDate ≤ 單據日期` 且最近（MAX RateDate），不存在則擋單 |
| **快照儲存** | 儲存單據時必須快照 `ExchangeRate`（+ 建議 `ExchangeRateDate`），歷史金額不受後續匯率異動影響 |
| **小數位數** | 依 `Currency.DecimalPlaces` 四捨五入（`MidpointRounding.AwayFromZero`） |
| **本幣不換算** | 幣別 = 本幣（或為空）時 Rate = 1，金額不轉換 |

---

## 二、匯率類型選擇

| 情境 | RateType | 原因 |
|------|----------|------|
| 採購付款（付外幣給廠商） | `Buy` | 公司買入外幣 |
| 銷售收款（收外幣來自客戶） | `Sell` | 公司賣出外幣 |
| 成本計算 / 存貨評價 | `Mid` | (Buy+Sell)÷2，中間價 |
| 未指定 | `Buy`（預設） | 保守原則 |

> ⚠️ **`Mid` 需買賣兩邊都有值**：只維護單邊時 `GetRateAsync` 回 `null`（與「查無匯率」同路徑），
> 不會退成「單邊的一半」。原本 `Rate > 0` 的守門發生在算完平均之後，
> `(Buy + 0) / 2` 大於 0 會通過守門，成本與存貨評價直接少一半且不報錯。

---

## 三、換算公式

```
本幣金額 = 外幣金額 × Rate
外幣金額 = 本幣金額 ÷ Rate
```

---

## 四、單據 Model 必備欄位（含外幣的單據）

> **欄位命名以現行已落地的單據為準**：交易幣金額用 `TotalAmount`（及 `UnitPrice`/`LineAmount`/`TaxAmount`），本幣金額用 `AmountLocal`。
> 早期文件曾用 `ForeignAmount`/`BaseAmount`，**現行程式並未採用此命名**，新單據請對齊下列實際樣式。

```csharp
/// <summary>交易幣別（外幣代號，= 本幣或為空時無匯率差異）</summary>
[MaxLength(20)]
public string? CurrencyCode { get; set; }

/// <summary>匯率快照（1 外幣 = Rate 本幣），儲存時鎖定，不隨後續匯率異動</summary>
[Column(TypeName = "decimal(18,6)")]
public decimal ExchangeRate { get; set; } = 1m;

/// <summary>含稅總額（交易幣，使用者輸入/明細加總）</summary>
[Column(TypeName = "decimal(18,4)")]
public decimal TotalAmount { get; set; }

/// <summary>本幣金額（系統換算，= TotalAmount × ExchangeRate；幣別=本幣時 Rate=1 等於 TotalAmount）</summary>
[Column(TypeName = "decimal(18,4)")]
public decimal AmountLocal { get; set; }
```

> **`ExchangeRateDate`（匯率日期快照）目前各單據 Entity 尚未持久化**（僅 `GetSnapshotAsync` 在記憶體回傳）。
> 若需「使用哪天匯率」的稽核軌跡與第 7-3 節提示，需先替該單據補欄位＋migration；在補欄位前，第 7-3 節提示與第九節對應檢查項視為「選配 / 尚未支援」。
>
### 4-1　表頭合計一律以本幣加總

明細層的 `CurrencyCode` **允許逐行不同**（同一張單可混幣）。因此表頭的 `TotalAmount` 定義為
**各明細 `AmountLocal` 的加總（本位幣）**，而非外幣直接相加 —— 混幣時外幣加總無意義。

適用單據（PUR：採購單／進貨單／進貨退回／委外進貨；ORD：銷貨訂單／出貨單／銷貨退回）：

| 層級 | 欄位 | 幣別 |
|------|------|------|
| 明細 | `UnitPrice` / `LineAmount` / `TaxAmount` / `TotalAmount` | 交易幣 |
| 明細 | `AmountLocal` = `TotalAmount × ExchangeRate` | 本位幣 |
| 表頭 | `TotalAmount` = Σ 明細 `AmountLocal` | 本位幣 |

> ⚠️ `AmountLocal` 一律由後端以權威匯率重算，**不採信前端送來的值**；前端表單只負責顯示。
> 畫面上交易幣三欄依幣別分群顯示合計，本幣欄則單一加總。

---

## 五、ICurrencyService 完整 API

```csharp
// 注入
private readonly ICurrencyService _currencySvc;

// ── 基本查詢 ──────────────────────────────────────────────────────────────

// 本幣代號（來自 SystemParam，快取 10 分鐘）
string? baseCurrency = await _currencySvc.GetBaseCurrencyAsync();

// 幣別設定（DecimalPlaces、Symbol）
Currency? currency = await _currencySvc.GetCurrencyAsync("USD");

// 所有啟用中幣別（供下拉選單，快取 10 分鐘）
List<Currency> currencies = await _currencySvc.GetActiveCurrenciesAsync();

// ── 匯率取用 ──────────────────────────────────────────────────────────────

// 取匯率（預設 Buy）
decimal? rate = await _currencySvc.GetRateAsync("USD", documentDate);
decimal? rate = await _currencySvc.GetRateAsync("USD", documentDate, RateType.Sell);
decimal? rate = await _currencySvc.GetRateAsync("USD", documentDate, RateType.Mid);

// 取實際使用的匯率日期（顯示提示用）
DateOnly? rateDate = await _currencySvc.GetRateDateAsync("USD", documentDate);

// 一次取得快照（儲存單據時使用）
CurrencyRateSnapshot? snap = await _currencySvc.GetSnapshotAsync("USD", documentDate, RateType.Buy);
if (snap != null)
{
    entity.ExchangeRate     = snap.Rate;
    entity.ExchangeRateDate = snap.RateDate;
}

// ── 金額換算 ──────────────────────────────────────────────────────────────

// 本幣 → 外幣（結果已依外幣 DecimalPlaces 四捨五入）
decimal? foreign = await _currencySvc.ToForeignAsync(100m, "USD", documentDate);

// 外幣 → 本幣（結果已依本幣 DecimalPlaces 四捨五入）— 本幣換算的唯一正確入口
decimal? baseAmt = await _currencySvc.ToBaseAsync(3.2m, "USD", documentDate);

// ── 顯示格式 ──────────────────────────────────────────────────────────────

// 四捨五入（依幣別 DecimalPlaces）
decimal rounded = await _currencySvc.RoundAsync(3.1415m, "USD");  // → 3.14

// 格式化顯示（符號 + 千分位）
string display = await _currencySvc.FormatAsync(1234.5m, "USD");  // → "$ 1,234.50"
string display = await _currencySvc.FormatAsync(1234m,   "JPY");  // → "¥ 1,234"
```

---

## 六、Controller 標準整合模式

### 6-1 Index action（下拉選單）

```csharp
// 在 Index 和 Create/Edit GET 均需載入
ViewBag.CurrencyOptions = await _currencySvc.GetActiveCurrenciesAsync();
ViewBag.BaseCurrency    = await _currencySvc.GetBaseCurrencyAsync();
```

### 6-2 Create / Edit POST（換算 + 快照）

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Create(OrderViewModel vm)
{
    if (!ModelState.IsValid) { /* ... */ }

    // ① 取匯率快照（依單據類型選 Buy / Sell）
    var snap = await _currencySvc.GetSnapshotAsync(
        vm.CurrencyCode,
        vm.DocumentDate,
        RateType.Buy);   // 採購用 Buy；銷售用 Sell

    if (snap == null)
    {
        ModelState.AddModelError("CurrencyCode",
            $"找不到 {vm.CurrencyCode} 在 {vm.DocumentDate:yyyy-MM-dd} 或之前的匯率，請至幣別維護新增。");
        await PrepareFormDropdowns();
        return IsAjax ? PartialView("_Form", vm) : View("_Form", vm);
    }

    // ② 快照寫入單據（歷史金額不可回算）
    entity.CurrencyCode      = vm.CurrencyCode;
    entity.ExchangeRate      = snap.Rate;
    entity.ExchangeRateDate  = snap.RateDate;   // Entity 有此欄位才寫
    entity.TotalAmount       = vm.TotalAmount;  // 交易幣

    // ③ 換算本幣金額（本幣 = 外幣 × Rate，由 ToBaseAsync 處理）
    entity.AmountLocal = await _currencySvc.ToBaseAsync(
        vm.TotalAmount, vm.CurrencyCode, vm.DocumentDate, RateType.Buy) ?? 0m;
}
```

### 6-3 PrepareFormDropdowns（必須包含）

```csharp
private async Task PrepareFormDropdowns()
{
    ViewBag.CurrencyOptions = await _currencySvc.GetActiveCurrenciesAsync();
    ViewBag.BaseCurrency    = await _currencySvc.GetBaseCurrencyAsync();
    // ... 其他下拉
}
```

---

## 七、View 標準整合模式

### 7-1 _ViewImports.cshtml（已全域注入，無需重複宣告）

```razor
@inject ICurrencyService CurrencySvc
```

### 7-2 幣別下拉選單

```razor
<select asp-for="CurrencyCode" class="form-select" id="currencySelect"
        onchange="fetchRate(this.value)">
    <option value="">— @Localizer["NotSet"] —</option>
    @foreach (var c in ViewBag.CurrencyOptions as List<Currency> ?? new())
    {
        <option value="@c.CurrencyCode"
                data-dp="@c.DecimalPlaces"
                data-symbol="@c.Symbol"
                selected="@(Model.CurrencyCode == c.CurrencyCode)">
            @c.CurrencyCode — @c.CurrencyName
        </option>
    }
</select>
```

### 7-3 匯率顯示（顯示取用日期提示）

```razor
@if (Model.ExchangeRateDate.HasValue && Model.CurrencyCode != baseCurrency)
{
    <div class="form-text text-muted">
        <i class="bi bi-info-circle"></i>
        使用 @Model.ExchangeRateDate.Value.ToString("yyyy-MM-dd") 匯率：
        1 @Model.CurrencyCode = @Model.ExchangeRate @baseCurrency
    </div>
}
```

### 7-4 金額格式化顯示（清單）

```razor
@* 同步方式（在 Controller 預先換算後放入 ViewModel）— 建議 *@
<td class="text-end">@item.FormattedTotalAmount</td>

@* 或在 View 直接呼叫（async，僅用於非大量資料場景）*@
<td class="text-end">@(await CurrencySvc.FormatAsync(item.TotalAmount, item.CurrencyCode ?? ""))</td>
```

### 7-5 即時匯率查詢（JS，表單輸入時）

```javascript
async function fetchRate(currencyCode) {
    if (!currencyCode) { clearRateDisplay(); return; }
    const docDate = document.getElementById('documentDate')?.value;
    if (!docDate) return;
    const res = await fetch(`/api/currency/rate?currencyCode=${currencyCode}&documentDate=${docDate}`);
    if (!res.ok) { showRateNotFound(currencyCode); return; }
    const data = await res.json();
    document.getElementById('rateDisplay').textContent =
        `1 ${baseCurrency} = ${data.rate} ${currencyCode}（${data.rateDate}）`;
}
```

---

## 八、API Endpoint（查詢匯率，供前端 JS 使用）

> **已由 `Modules/BAS/Controllers/CurrencyApiController` 全域提供，無需在各 Controller 重複實作。**

實際端點與參數命名（前端請以此為準）：

```
GET /api/currency/rate?currencyCode=USD&documentDate=2025-03-01&rateType=Buy
GET /api/currency/decimals          （回傳各幣別 DecimalPlaces 對照）
```

`/rate` 回傳（找不到匯率時回 200 + `found:false`，非 404）：

```jsonc
{
  "currencyCode": "USD",
  "documentDate": "2025-03-01",
  "rateType":     "Buy",
  "rate":         0.032,          // found:false 時為 null
  "rateDate":     "2025-03-01",   // found:false 時為 null
  "found":        true
}
```

> 前端判斷請用 `data.found`，不要倚賴 HTTP 狀態碼。

---

## 九、Step 4 自我確認清單（涉及外幣的畫面）

```
Model：
[ ] 含 CurrencyCode、ExchangeRate、TotalAmount（交易幣）、AmountLocal（本幣）欄位（ExchangeRateDate 選配）

Controller：
[ ] Index + Create/Edit GET 均呼叫 PrepareFormDropdowns（含 CurrencyOptions + BaseCurrency）
[ ] Create/Edit POST 先取 GetSnapshotAsync，找不到時 ModelState.AddModelError
[ ] 找到後：快照寫入 ExchangeRate（若 Entity 有 ExchangeRateDate 欄位則一併寫入）
[ ] 找到後：ToBaseAsync 換算 AmountLocal（本幣 = 外幣 × Rate，切勿用除）
[ ] 使用正確的 RateType（採購=Buy，銷售=Sell，成本=Mid）

View：
[ ] 幣別下拉來自 ViewBag.CurrencyOptions
[ ] （選配，需 Entity 有 ExchangeRateDate 欄位）匯率日期提示（顯示取用了哪天的匯率）
[ ] 清單金額顯示已格式化（FormatAsync 或 ViewModel 預先格式化）

常見錯誤：
[ ] ❌ 未快照就存單 → 歷史金額隨匯率更新而改變
[ ] ❌ 找不到匯率未驗證，直接除以 0
[ ] ❌ 幣別 = 本幣時仍查匯率（應 Rate=1 直通）
[ ] ❌ View 大量呼叫 FormatAsync（每列一次 DB 查詢）→ 改在 Controller 預算
```

---

## 十、常見錯誤對照

| 錯誤 | 正確做法 |
|------|---------|
| 存單時未快照匯率直接計算 | 用 `GetSnapshotAsync` 取快照並寫入 `ExchangeRate`、`ExchangeRateDate` |
| `GetRateAsync` 回傳 null 未處理就除法 | 先 `if (snap == null) ModelState.AddModelError(...)` |
| 幣別 = 本幣仍查匯率 | Service 內部已處理，Rate = 1；但 View 不應顯示「匯率日期提示」 |
| 在 View foreach 逐筆呼叫 `FormatAsync` | 大量資料改在 Controller 預先格式化放 ViewModel，或用靜態 helper |
| 使用 `Math.Round(x, 2)` 硬編碼 2 位 | 改用 `await _currencySvc.RoundAsync(x, currencyCode)`（簽章為 `RoundAsync(decimal amount, string currencyCode)`） |
| 採購單使用 SellRate | 採購 → Buy，銷售 → Sell，成本 → Mid |
| **本幣金額用 `外幣 ÷ Rate`** | **本幣 = `外幣 × Rate`**；`Rate` 的方向是「1 外幣 = Rate 本幣」（如 USD=32），除法會把金額算反 |

---

## 十一、方向一致性歷程（已收斂）

> 全系統統一採「**本幣 = 外幣 × Rate**，Rate = 本幣/外幣（如 USD=32），由幣別維護輸入」。
> FIN（AP/AR 發票、付款、核銷、匯兌損益、帳齡）、ACT（分錄、FxReval）本來即以此方向實作，為基準。

| 項目 | 狀態 |
|------|------|
| `CurrencyService.ToBaseAsync`（外幣→本幣） | ✅ 已修正為 `外幣 × Rate`（原誤用 `÷`） |
| `CurrencyService.ToForeignAsync`（本幣→外幣） | ✅ 已修正為 `本幣 ÷ Rate`（原誤用 `×`） |
| `CurrencyServiceTests` 種子與斷言 | ✅ 已改為本幣/外幣方向（USD=31.25、JPY=0.25） |
| FIN/ACT 金額換算 | ✅ 本即乘法方向，無需更動 |
| PUR 各單據金額欄位註解原誤標「本幣」 | ✅ 已更正為「採購幣」（PUR 僅存交易幣、不換算本幣） |
| PUR/ORD 明細 `TotalAmount` 實際存本幣（前端 `(未稅+稅額) × 匯率`）卻註解為交易幣 | ✅ 已收斂：`TotalAmount` 正名為交易幣，另補 `AmountLocal`；migration `AddAmountLocalToDocumentLines` 回算既有資料 |
| `PurchaseAmountController` 對 `TotalAmount` 再乘一次匯率（重複換算） | ✅ 已改為直接加總 `AmountLocal` |
| FIN `APInvoiceService` / `ARInvoiceService` 稅額誤用 `TaxRate / 100` | ✅ 已移除 `/ 100`（`Tax.TaxRate` 本就以 0.05 = 5% 存放） |

> 既有資料：`AddAmountLocalToDocumentLines` 已附一次性回算 SQL
> （舊 `TotalAmount` → `AmountLocal`，再 `÷ ExchangeRate` 還原交易幣）。匯率為 1 的本幣單據不受影響。
