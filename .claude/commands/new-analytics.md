---
description: 產生 SmartFactory MES 標準資料分析畫面（KPI 卡片、趨勢圖、彙總表）。觸發時機：要求 Dashboard、儀表板、KPI 報表、趨勢分析、彙總統計，非單據列印。
---

# /new-analytics — SmartFactory MES 資料分析報表產生器

> **此 skill 為非單據型「資料分析／儀表板／KPI 報表」量身打造。**
> 觸發時機：使用者描述「儀表板」「Dashboard」「KPI」「趨勢圖」「彙總統計」「分析報表」「圓餅圖／長條圖／折線圖」「日報／月報」（非個別單據列印）。
>
> 若使用者要的是「印一張訂單／工單」之類的單據，請改用 `/new-report`。
> 若使用者只要把表格匯成 Excel，請用 `/xlsx`。

---

## 與 /new-report、/xlsx 的區別

| 維度 | /new-report | /xlsx | /new-analytics（本檔） |
|------|-------------|-------|----------------------|
| 資料粒度 | 單筆單據 + 明細 | 列表查詢結果 | 跨期間／跨廠／跨類別**彙總**後的數字 |
| 主要呈現 | A4 紙本／PDF | Excel 檔 | KPI 卡片 + 圖表 + 樞紐表 |
| 互動性 | 無（靜態） | 無（下載即離線） | 有（篩選器、鑽研、即時刷新） |
| 適用情境 | 出貨單、工單 | 庫存清單匯出 | 產能利用率、即時良率、月銷售趨勢 |

---

## Step 1：詢問需求

```
資料分析畫面需求確認：

1. 功能名稱（英文，如 ProductionDashboard、SalesAnalytics）：
2. 中文名稱（如 生產儀表板、銷售分析）：
3. 英文顯示名稱（如 Production Dashboard）：
4. MENU 群組與圖示（如 Production / bi-graph-up）：

5. KPI 卡片（最多 6 張，每張 1 個數字 + 1 行說明）：
   範例：今日產出 / 本月達成率 / 良率 / 待出貨單數

6. 圖表清單（每個獨立區塊，至少 1 個）：
   每個請說明：
   a. 圖表類型 [line（折線）/ bar（長條）/ pie（圓餅）/ stacked-bar（堆疊長條）]
   b. X 軸（如 日期、廠別、產品類別）
   c. Y 軸（如 數量、金額、百分比）
   d. 資料來源（表名或描述）

7. 篩選器（影響所有圖表，使用者可即時切換）：
   a. 期間 [今日 / 本週 / 本月 / 本季 / 本年 / 自訂區間，預設 本月]
   b. 廠別（預設：使用者目前廠別；可選「全部」需具備跨廠權限）
   c. 其他（如 產品類別、客戶、班別）

8. 資料新鮮度：
   a. 即時查詢（每次進入頁面跑 query，數秒延遲可接受）
   b. 快取 N 分鐘（大量資料、不頻繁變動）
   c. 物化視圖／背景作業預聚合（巨量資料，搭配 /new-job）

9. 是否提供「鑽研」（點 KPI/圖表元素跳到明細列表）？[Y/N，預設 Y]
10. 是否提供匯出 [Excel / PDF / 影像，可多選]？[預設：Excel]
11. 是否需要列印？[Y/N，預設 Y]（如需走 /new-report 共用排版）
```

---

## Step 2：技術選型

| 元件 | 預設 | 說明 |
|------|------|------|
| 圖表庫 | **Chart.js 4.x** | MIT、輕量（60 KB gz）、Bootstrap 5 整合佳；專案未引入則 Step 2-A 安裝 |
| 篩選器 | Bootstrap 5 form-controls + Select2 | 與既有畫面一致 |
| 資料 API | Controller 回 JSON | 不另起 API 專案，與 MVC Controller 同一處（`/Analytics/{Name}/Data`） |
| 快取 | `IMemoryCache`（單機）／`IDistributedCache`（多副本） | 預設 `IMemoryCache`，超過 1 副本時改 `IDistributedCache` |
| 匯出 | `/xlsx` 既有規格 | 不另寫，呼叫 ClosedXML |
| 列印 | `/new-report` + `_PrintLayoutZ` | 圖表轉 PNG（Chart.js `toBase64Image()`）嵌入 |

---

## Step 2-A：Chart.js 安裝（第一次使用時）

ProjectZ `wwwroot/lib/` 目前**未含** Chart.js，需先引入：

```bash
# 方法 1：libman（與既有 jquery / bootstrap 同途徑）
cd /home/dev/ProjectZ
libman install chart.js@4 --provider unpkg --destination wwwroot/lib/chart.js --files dist/chart.umd.js

# 方法 2：直接下載放置
mkdir -p wwwroot/lib/chart.js
curl -L https://cdn.jsdelivr.net/npm/chart.js@4.4.6/dist/chart.umd.js \
  -o wwwroot/lib/chart.js/chart.umd.js
```

在 `Pages/Shared/_Layout.cshtml` 加（**僅當 Layout 為 ProjectZ 可改的客製版**；
若 _Layout 屬 Core 範圍，改成在分析畫面的 `@section Scripts` 個別載入）：

```html
<script src="~/lib/chart.js/chart.umd.js" asp-append-version="true"></script>
```

> ⚠️ 一致性框架的 R4 規則：JS 字串禁硬編碼 CJK。圖例／軸標籤一律走 `window.i18n`。

---

## Step 3：Controller 標準骨架

### 檔案路徑

顧問模組：
```
Modules/{MODULE}/Controllers/{Name}AnalyticsController.cs
Modules/{MODULE}/Views/{Name}Analytics/Index.cshtml
Modules/{MODULE}/Models/Analytics/{Name}DashboardVm.cs
Modules/{MODULE}/Services/{Name}AnalyticsService.cs      ← 查詢聚合邏輯
Resources/Modules/{MODULE}/Views/{Name}Analytics/Index.{zh-TW,en-US}.resx
```

### Controller 範本（**繼承 `ModuleControllerBase`**）

```csharp
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.{MODULE}.Controllers;

[Authorize]
public sealed class {Name}AnalyticsController : ModuleControllerBase
{
    private readonly {Name}AnalyticsService _service;
    private readonly IPermissionService _permissionService;
    private readonly IUserContext _userContext;
    private readonly IDateTimeService _dateTimeSvc;
    private const string MenuKey = "{Name}Analytics";

    public {Name}AnalyticsController(
        {Name}AnalyticsService service,
        IPermissionService permissionService,
        IUserContext userContext,
        IDateTimeService dateTimeSvc)
    {
        _service = service;
        _permissionService = permissionService;
        _userContext = userContext;
        _dateTimeSvc = dateTimeSvc;
    }

    /// <summary>儀表板主頁（殼）</summary>
    public async Task<IActionResult> Index()
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();
        await PrepareIndexPermissionsAsync(MenuKey);   // 由 ModuleControllerBase 提供
        ViewBag.DefaultPeriod = "month";
        ViewBag.DefaultPlantId = _userContext.PlantId;
        return View();
    }

    /// <summary>圖表資料 API（AJAX 取 JSON）</summary>
    [HttpGet]
    public async Task<IActionResult> Data(string period = "month",
                                          string? plantId = null,
                                          string? category = null,
                                          DateTime? from = null,
                                          DateTime? to = null)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        // 期間統一在後端解析，避免前端時區誤算
        var (utcFrom, utcTo) = ResolvePeriod(period, from, to);
        plantId ??= _userContext.PlantId;

        var vm = await _service.BuildDashboardAsync(utcFrom, utcTo, plantId, category);
        return JsonOk(vm);   // ModuleControllerBase 提供 {success:true, data:...}
    }

    /// <summary>鑽研：跳到明細列表</summary>
    [HttpGet]
    public IActionResult Drilldown(string metric, string period, string? plantId)
    {
        // 依 metric 導向對應 Index 並帶查詢條件
        return metric switch
        {
            "pending-shipments" => RedirectToAction("Index", "SalesOrder",
                new { status = "Pending", searched = true }),
            _ => NotFound()
        };
    }

    private (DateTime utcFrom, DateTime utcTo) ResolvePeriod(string period,
                                                              DateTime? from,
                                                              DateTime? to)
    {
        var today = _dateTimeSvc.Now.Date;
        return period switch
        {
            "today"   => (_dateTimeSvc.ToUtcTime(today),              _dateTimeSvc.ToUtcTime(today.AddDays(1))),
            "week"    => (_dateTimeSvc.ToUtcTime(today.AddDays(-(int)today.DayOfWeek)),
                          _dateTimeSvc.ToUtcTime(today.AddDays(1))),
            "month"   => (_dateTimeSvc.ToUtcTime(new DateTime(today.Year, today.Month, 1)),
                          _dateTimeSvc.ToUtcTime(today.AddDays(1))),
            "quarter" => (_dateTimeSvc.ToUtcTime(new DateTime(today.Year, ((today.Month-1)/3)*3+1, 1)),
                          _dateTimeSvc.ToUtcTime(today.AddDays(1))),
            "year"    => (_dateTimeSvc.ToUtcTime(new DateTime(today.Year, 1, 1)),
                          _dateTimeSvc.ToUtcTime(today.AddDays(1))),
            "custom"  => (_dateTimeSvc.ToUtcTime(from ?? today.AddDays(-30)),
                          _dateTimeSvc.ToUtcTime((to ?? today).AddDays(1))),
            _ => (_dateTimeSvc.ToUtcTime(today.AddDays(-30)), _dateTimeSvc.ToUtcTime(today.AddDays(1)))
        };
    }
}
```

---

## Step 4：Service（聚合查詢，與 Controller 解耦便於單元測試）

```csharp
public sealed class {Name}AnalyticsService
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public {Name}AnalyticsService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<{Name}DashboardVm> BuildDashboardAsync(
        DateTime utcFrom, DateTime utcTo, string plantId, string? category)
    {
        var key = $"{nameof({Name}AnalyticsService)}:{utcFrom:O}:{utcTo:O}:{plantId}:{category}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

            // ⚠️ 避免在記憶體 .GroupBy() 大量資料 — 用 EF Core 翻譯成 SQL GROUP BY
            var trendRaw = await _db.Set<YourTxnEntity>()
                .AsNoTracking()
                .Where(x => x.PlantId == plantId
                         && x.OccurredAt >= utcFrom
                         && x.OccurredAt < utcTo
                         && (category == null || x.Category == category))
                .GroupBy(x => x.OccurredAt.Date)
                .Select(g => new { Date = g.Key, Qty = g.Sum(x => x.Qty), Amount = g.Sum(x => x.Amount) })
                .OrderBy(x => x.Date)
                .ToListAsync();

            return new {Name}DashboardVm
            {
                Kpis = new()
                {
                    TodayQty = trendRaw.LastOrDefault()?.Qty ?? 0,
                    PeriodTotalAmount = trendRaw.Sum(x => x.Amount),
                    // ...
                },
                TrendLabels = trendRaw.Select(x => x.Date.ToString("MM-dd")).ToArray(),
                TrendQty    = trendRaw.Select(x => x.Qty).ToArray(),
                TrendAmount = trendRaw.Select(x => x.Amount).ToArray(),
                // ... 其他圖表資料
            };
        }) ?? new {Name}DashboardVm();
    }
}
```

> 註冊：`Modules/{MODULE}/{MODULE}Module.cs` 的 `RegisterServices`：
> ```csharp
> services.AddScoped<{Name}AnalyticsService>();
> ```

---

## Step 5：View 標準骨架（Index.cshtml）

```cshtml
@inject IViewLocalizer Localizer
@inject SmartFactory.MES.Services.IPermissionService PermissionService
@{
    ViewData["Title"] = Localizer["{Name}Analytics"].Value;
    var defaultPeriod = ViewBag.DefaultPeriod as string ?? "month";
    var defaultPlantId = ViewBag.DefaultPlantId as string;
}

<div class="container-fluid p-3">
    <h1 class="h3 mb-3"><i class="bi bi-graph-up"></i> @Localizer["{Name}Analytics"]</h1>

    <%-- ── 篩選列 ────────────────────────────────────────────── --%>
    <div class="card mb-3">
        <div class="card-body">
            <form id="analyticsFilter" class="row g-2">
                <div class="col-md-2">
                    <label class="form-label">@Localizer["Period"]</label>
                    <select name="period" class="form-select">
                        <option value="today">@Localizer["Today"]</option>
                        <option value="week">@Localizer["ThisWeek"]</option>
                        <option value="month" selected>@Localizer["ThisMonth"]</option>
                        <option value="quarter">@Localizer["ThisQuarter"]</option>
                        <option value="year">@Localizer["ThisYear"]</option>
                        <option value="custom">@Localizer["Custom"]</option>
                    </select>
                </div>
                <%-- 其他篩選欄位（廠別、類別 ...） --%>
                <div class="col-md-auto d-flex align-items-end">
                    <button type="submit" class="btn btn-primary">@Localizer["Apply"]</button>
                    @if (await PermissionService.CanPrint("{Name}Analytics"))
                    {
                        <button type="button" class="btn btn-outline-info ms-2" onclick="window.print()">
                            <i class="bi bi-printer"></i> @Localizer["Print"]
                        </button>
                    }
                </div>
            </form>
        </div>
    </div>

    <%-- ── KPI 卡片區 ──────────────────────────────────────────── --%>
    <div class="row g-3 mb-3" id="kpiCards">
        <div class="col-md-3"><div class="card kpi-card"><div class="card-body">
            <div class="kpi-label">@Localizer["TodayQty"]</div>
            <div class="kpi-value" data-kpi="todayQty">—</div>
        </div></div></div>
        <%-- ... 其他 KPI 卡片 ... --%>
    </div>

    <%-- ── 圖表區 ─────────────────────────────────────────────── --%>
    <div class="row g-3">
        <div class="col-md-8"><div class="card"><div class="card-body">
            <h6>@Localizer["DailyTrend"]</h6>
            <canvas id="trendChart" height="120"></canvas>
        </div></div></div>
        <div class="col-md-4"><div class="card"><div class="card-body">
            <h6>@Localizer["CategoryBreakdown"]</h6>
            <canvas id="categoryChart" height="200"></canvas>
        </div></div></div>
    </div>
</div>

@section Scripts {
    @await Html.PartialAsync("_JsLocalization")    <%-- window.i18n（一致性框架） --%>
    <script src="~/lib/chart.js/chart.umd.js" asp-append-version="true"></script>
    <script>
        (function () {
            const url = '@Url.Action("Data")';
            const filterForm = document.getElementById('analyticsFilter');
            let trendChart, categoryChart;

            async function load() {
                const qs = new URLSearchParams(new FormData(filterForm)).toString();
                const r = await fetch(`${url}?${qs}`, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
                const j = await r.json();
                if (!j.success) { alert(window.i18n.t('LoadFailed')); return; }
                const d = j.data;

                // KPI
                document.querySelector('[data-kpi="todayQty"]').textContent = d.kpis.todayQty;

                // 折線圖
                if (trendChart) trendChart.destroy();
                trendChart = new Chart(document.getElementById('trendChart'), {
                    type: 'line',
                    data: {
                        labels: d.trendLabels,
                        datasets: [{
                            label: window.i18n.t('Qty'),
                            data: d.trendQty,
                            borderColor: '#0d6efd',
                            tension: 0.3
                        }]
                    },
                    options: { responsive: true, maintainAspectRatio: true }
                });

                // 圓餅圖（範例）
                if (categoryChart) categoryChart.destroy();
                categoryChart = new Chart(document.getElementById('categoryChart'), {
                    type: 'pie',
                    data: { labels: d.categoryLabels, datasets: [{ data: d.categoryValues }] }
                });
            }

            filterForm.addEventListener('submit', e => { e.preventDefault(); load(); });
            load();   // 初次載入
        })();
    </script>
}
```

> ⚠️ R4 / 一致性框架：所有 `alert/confirm` 與圖表 label 經 `window.i18n.t(key)`，禁止硬編碼中文。

---

## Step 6：多語 resx（4 個）

```
Resources/Modules/{MODULE}/Views/{Name}Analytics/Index.zh-TW.resx
Resources/Modules/{MODULE}/Views/{Name}Analytics/Index.en-US.resx
Resources/Modules/{MODULE}/Controllers/{Name}AnalyticsController.zh-TW.resx
Resources/Modules/{MODULE}/Controllers/{Name}AnalyticsController.en-US.resx
```

至少包含的 key（zh-TW / en-US 必須同名）：

```
{Name}Analytics / 生產儀表板 / Production Dashboard
Period          / 期間       / Period
Today/ThisWeek/ThisMonth/ThisQuarter/ThisYear/Custom
Apply           / 套用       / Apply
Print           / 列印       / Print
TodayQty        / 今日產出   / Today Qty
DailyTrend      / 每日趨勢   / Daily Trend
CategoryBreakdown / 類別分佈 / Category Breakdown
Qty             / 數量       / Qty
LoadFailed      / 載入失敗   / Load failed
```

> 對應 R5：zh-TW 與 en-US 的 key 集合**必須完全一致**。
> 對應 R1：View 中 `@Localizer["k"]` 全部存在於 resx。

---

## Step 7：Menu 登錄（ProjectZ 規則）

在 `Services/ProjectZMenuContributor.cs` 的對應群組 SubItems 插入：

```csharp
new MenuItem {
    Title = _localizer["{Name}Analytics"],
    Controller = "{Name}Analytics",
    Action = "Index",
    Icon = "bi-graph-up",
    MenuKey = "{Name}Analytics"
}
```

在 `Resources/ProjectZSharedResource.{zh-TW,en-US}.resx` 加：

```
{Name}Analytics / 生產儀表板 / Production Dashboard
```

❌ 禁止改 Core 的 `CoreMenuContributor.cs` / `SharedResource.*.resx`。

---

## Step 8：權限與資料隔離

| 維度 | 規則 |
|------|------|
| 讀取權限 | `_permissionService.CanRead("{Name}Analytics")` |
| 列印權限 | `_permissionService.CanPrint("{Name}Analytics")` |
| 跨廠資料 | 預設只看 `_userContext.PlantId`；要查全廠須另一個 MenuKey（如 `{Name}AnalyticsCrossPlant`） |
| 鑽研到明細 | 跳到對應 Index 仍跑既有權限檢查，不需重複 |
| 匿名/未登入 | `[Authorize]` 阻擋，AJAX 端點若 401 由前端統一處理 |

---

## Step 9：列印支援（可選，呼叫 /new-report 共用排版）

如需「儀表板列印成 PDF / 印給主管」：

1. Print View 用 `Layout = "/Views/Shared/_PrintLayoutZ.cshtml"`（一致性框架共用樣板）
2. 圖表轉 PNG 嵌入：`canvas.toDataURL('image/png')` → `<img>` 標籤
3. 在 Print 載入時等所有 chart `animation: false`，並用 `chart.render()` 同步完成才印
4. CSS `@media print` 隱藏篩選列與按鈕，與既有 `site.css` 一致

詳見 `/new-report` 規格。

---

## Step 10：效能與資料新鮮度

### 10.1 即時查詢 + 5 分鐘 IMemoryCache（預設）

如 Step 4，適用每日交易量 < 10 萬筆。

### 10.2 大資料量 → 物化視圖（搭配 /new-job 排程刷新）

```sql
-- Migrations/{Timestamp}_Add{Name}DailyAgg.cs 內用 migrationBuilder.Sql(...)
CREATE MATERIALIZED VIEW {modulename}.daily_{name}_agg AS
SELECT plant_id, occurred_at::date AS day, category, SUM(qty) AS qty, SUM(amount) AS amount
FROM {modulename}.your_txn
GROUP BY plant_id, occurred_at::date, category;

CREATE UNIQUE INDEX ON {modulename}.daily_{name}_agg (plant_id, day, category);
```

排程刷新 → 用 `/new-job` 產生 `{Name}AggRefreshService` 每小時 `REFRESH MATERIALIZED VIEW CONCURRENTLY`。

### 10.3 跨副本部署

`IMemoryCache` 是每副本獨立，A/B 副本看到的快取可能不同步。多副本場景請改：
```csharp
services.AddStackExchangeRedisCache(opt => opt.Configuration = ...);
// Service 改注入 IDistributedCache
```

---

## Step 11：Checklist（不得省略）

```
[ ] Controller 繼承 ModuleControllerBase（一致性框架）
[ ] Service 聚合查詢用 EF Core .GroupBy() → SQL；禁止在記憶體 .ToList() 後再聚合
[ ] 時間轉換：使用者輸入 → IDateTimeService.ToUtcTime；資料庫 UTC → ToLocalTime
[ ] 篩選器在後端解析期間，不信任前端傳的 from/to
[ ] 跨廠別資料：明確檢查使用者是否有「跨廠權限」（另一個 MenuKey）
[ ] 圖表 label / KPI 標題 / alert 訊息 → window.i18n.t() 或 @Localizer，禁硬編碼 CJK
[ ] resx 4 個檔案 key 完全一致（R1、R5）
[ ] ProjectZMenuContributor 插入選單 + ProjectZSharedResource 加 MENU 文字（R2）
[ ] 第一次引入時把 Chart.js 加進 wwwroot/lib/ 或經 libman 安裝
[ ] 大量資料 → 設 IMemoryCache 5 分鐘 或 物化視圖 + /new-job 排程刷新
[ ] 鑽研按鈕指向既有 Index 並帶查詢條件（避免重寫明細頁）
[ ] 單元測試 Service：給定時間區間與 mock 資料，斷言聚合結果
```

---

## 與其他規格的關係

- **一致性框架（LOCAL_RULES.md）**：Controller 繼承 `ModuleControllerBase`、資料 API 用 `JsonOk/JsonFail` 統一契約、JS 經 `window.i18n`
- **`/xlsx`**：匯出按鈕直接呼叫 Controller 的 `Export` action，重用既有規格
- **`/new-job`**：物化視圖刷新、預聚合排程
- **`/new-report`**：要把儀表板列印成 PDF 時的排版規格
- **`/new-screen`**：本檔規格涵蓋了「儀表板」這一類非 CRUD 畫面；CRUD 仍走 `/new-screen`

---

> 完整範例：未來實作後可指向 `Modules/{MODULE}/Controllers/{Name}AnalyticsController.cs` 與相關檔。


## 日期與效能驗收補充（2026-11-05）

- `IDateTimeService` 沒有 UtcNow 屬性；以 Now／GetLocalDate 取得廠區日期，不直接讀伺服器時鐘。
- DateOnly 業務日期以當地日期做 `[from, to)`；UTC timestamp 邊界各自呼叫 ToUtcTime，不混用兩者。
- 篩選優先使用日期區間，避免在 WHERE 對欄位取 Year／Month；圖表按月 GroupBy 仍可取 Month。
- 注入固定時鐘測試月初、年初與 UTC 跨日；PostgreSQL 查詢計畫及代表性資料量支持後才新增索引。
- 快取鍵必須包含實際資料授權範圍；角色或資料範圍相異時，不共用機密彙總快取。
