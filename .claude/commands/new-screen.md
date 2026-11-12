---
description: 產生 SmartFactory MES 標準畫面（Controller / View / resx / Tests）。觸發時機：要求新增功能畫面、CRUD 頁面、任何新的管理畫面。
---

# /new-screen — SmartFactory MES 標準畫面產生器

> **此 skill 在產生任何新畫面前自動套用（無需手動呼叫）。**
> 當使用者描述一個新功能畫面需求時，先執行 Step 1 確認模式，再依模式進行。

---

## Step 1：確認產生模式

**首先詢問使用者一個問題：**

```
是否直接以預設值自動產生畫面？

  Y（自動）→ 所有設定採預設值，AI 依功能語意決定名稱，立即產生全部程式碼
  N（自訂）→ 逐項詢問需求後再產生

預設值摘要：
  裝置類型：desktop
  查詢列、分頁、排序、多廠別、時區、列印：全部開啟
  線上簽核：關閉
  代碼維護下拉：無

請回答 Y 或 N：
```

- 若回答 **Y**（或使用者未明確回答 N）→ 跳至 **Step 1-A（自動模式）**
- 若回答 **N** → 進入 **Step 1-B（自訂模式）**

---

## Step 1-A：自動模式

AI 依使用者描述的功能語意，自行決定以下項目，**不再詢問**：

| 項目 | 由 AI 決定 | 固定預設值 |
|------|-----------|-----------|
| 功能名稱（英文） | ✓ | |
| 中文名稱 | ✓ | |
| 英文顯示名稱 | ✓ | |
| MENU 群組 | ✓ | |
| MENU 圖示 | ✓ | |
| 裝置類型 | | desktop |
| 查詢列 | | Y |
| 分頁 | | Y |
| 排序 | | Y |
| 多廠別 | | Y |
| 時區 | | Y |
| 列印 | | Y |
| 線上簽核 | | N |
| 代碼維護下拉 | | 無 |

決定後，**直接輸出規格摘要並開始產生程式碼**（不等待確認）：

```
【自動產生規格】
功能名稱：{KEY}（{中文名稱} / {英文名稱}）
MENU：{群組} / {圖示}
裝置：desktop
功能：查詢 ✓ 分頁 ✓ 排序 ✓ 多廠別 ✓ 時區 ✓ 列印 ✓
簽核：✗  代碼維護下拉：無
→ 開始產生...
```

---

## Step 1-B：自訂模式

請詢問使用者以下問題。**有預設值的項目若使用者未回答，直接採用預設值，不需再追問。**
1~3、5、6 若使用者未提供，由 AI 依功能語意自行決定。

```
請描述新畫面需求（未回答的項目將使用預設值）：

【必填，AI 依語意決定若空白】
1. 功能名稱（英文，如 SalesOrder）：
2. 中文名稱（如 銷售訂單）：
3. 英文顯示名稱（如 Sales Order）：
5. MENU 群組（如 Sales、Production、Administration）：
6. MENU 圖示（Bootstrap Icon class，如 bi-cart）：

【選填，括號內為預設值，直接 Enter 採用預設】
4. 裝置類型（預設：desktop）[desktop / mobile / both]：
7. 線上簽核（預設：N）[Y/N]：
8. 代碼維護下拉欄位 Category（預設：無，有則填逗號分隔，如 orderType,shipMethod）：

【以下預設皆為 Y，只有明確回答 N 才關閉】
9.  查詢列？（預設 Y）：
10. 分頁？（預設 Y）：
11. 欄頭排序？（預設 Y）：
12. 多廠別過濾？（預設 Y）：
13. 時區（IDateTimeService）？（預設 Y）：
14. 列印權限？（預設 Y）：
```

---

## Step 2：裝置類型說明

### desktop（預設）
產生標準 Razor MVC 畫面：
- `Controllers/{KEY}Controller.cs`
- `Views/{KEY}/Index.cshtml` + `_Form.cshtml`
- `Resources/Views/{KEY}/*.resx`（4 個）

### mobile
本系統手機版採 **REST API + 外部 App** 架構（非 Razor View）。
產生 API 端點：
- `Api/Mobile/{KEY}Controller.cs`（繼承 `ControllerBase`，`[Route("api/mobile/{key}")]`）
- 在 `MobileModule` 資料表中登記模組定義（提示使用者手動新增）
- **不產生** Views / resx / offcanvas 等 MVC 元件

API 必須包含端點：
```
GET  api/mobile/{key}          → 清單（支援 ?plantId 過濾）
GET  api/mobile/{key}/{id}     → 單筆詳情
POST api/mobile/{key}          → 新增
PUT  api/mobile/{key}/{id}     → 修改
```

### both
同時產生 desktop Razor MVC 畫面 **+** mobile REST API。
兩套共用同一個 Model 與 DbContext，Controller 分開。

---

## Step 3：必須產生的檔案清單

### 3-1 Controller（desktop / both）

檔案：`Controllers/{KEY}Controller.cs`

**必須包含（所有欄位，依 Y/N 加入對應區塊）**：

```csharp
[Authorize]
public class {KEY}Controller : Controller
{
    // 必注入
    private readonly IPermissionService _permissionService;
    private readonly IUserContext _userContext;
    private readonly ApplicationDbContext _context;
    private readonly IExportService _exportService;   // 匯出（必注入）
    private const string MenuKey = "{KEY}";
    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    // HAS_TIMEZONE=Y：
    private readonly IDateTimeService _dateTime;

    // HAS_APPROVAL=Y：
    private readonly IApprovalService _approvalService;

    // Index Action：
    public async Task<IActionResult> Index(
        string? searchTerm,        // HAS_SEARCH=Y
        int? pageNumber,           // HAS_PAGINATION=Y
        int? pageSize,             // HAS_PAGINATION=Y
        string? sortOrder = null,  // HAS_SORTING=Y
        bool searched = false)     // HAS_SEARCH=Y
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        // 權限 ViewBag（必須）
        ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
        ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
        ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
        // HAS_PRINT=Y：
        ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);

        // HAS_SEARCH=Y（必須加，否則頁面一開啟就查全部）：
        ViewBag.Searched = searched;
        ViewBag.SearchTerm = searchTerm;
        if (!searched) return View((PaginatedList<{MODEL}>?)null);

        // HAS_SORTING=Y（每個可排序欄位都要有對應 ViewBag）：
        ViewBag.CurrentSort = sortOrder;
        ViewBag.{Field1}Sort = string.IsNullOrEmpty(sortOrder) || sortOrder == "{f1}_asc"
            ? "{f1}_desc" : "{f1}_asc";

        var query = _context.{DbSet}.AsQueryable();

        // HAS_MULTI_PLANT=Y（必須）：
        query = query.Where(x => x.PlantId == _userContext.PlantId);

        // HAS_SEARCH=Y：
        if (!string.IsNullOrEmpty(searchTerm))
            query = query.Where(x => x.{SearchField}.Contains(searchTerm));

        // HAS_SORTING=Y：
        query = sortOrder switch { ... _ => query.OrderBy(x => x.{DefaultSortField}) };

        // HAS_PAGINATION=Y：
        ViewBag.CurrentPageSize = pageSize ?? 10;
        return View(await PaginatedList<{MODEL}>.CreateAsync(
            query.AsNoTracking(), pageNumber ?? 1, pageSize ?? 10));
    }

    // Create/Edit POST（必須）：
    // ⚠️ 安全（必須）：每個寫入 POST 第一行都要做伺服器端權限檢查，
    //    不可只靠 Index 的 ViewBag.CanCreate 隱藏按鈕（攻擊者可直接發 POST）：
    //      Create POST：if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
    //      Edit   POST：if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
    //      合一 Save  ：if (string.IsNullOrEmpty(vm.{DocId})
    //                       ? !await _permissionService.CanCreate(MenuKey)
    //                       : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
    //      Delete POST：if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
    //      Submit/Withdraw/Confirm/Void/Close 等狀態流轉 POST：CanUpdate
    //    此規範由 SmartFactory.MES.Tests 的 PostPermissionConventionTests 強制檢測。
    // HAS_MULTI_PLANT=Y：entity.PlantId = _userContext.PlantId;
    //
    // HAS_TIMEZONE=Y — 日期時間欄位兩種情境：
    //   ① 系統自動產生的時間（建立時間、修改時間）：
    //      entity.CreatedAt = _dateTime.UtcNow;
    //
    //   ② 使用者輸入的日期（需求日期、到期日等）：
    //      // 使用者看到的是廠別當地時間，儲存前必須轉為 UTC
    //      entity.RequiredDate = _dateTime.ToUtcTime(model.RequiredDate);
    //      // GET Edit 回填時，從 UTC 轉為廠別時間：
    //      ViewBag.RequiredDateLocal = _dateTime.ToLocalTime(entity.RequiredDate);
    //
    // HAS_APPROVAL=Y — Save action 標準寫法：
    //   1. 儲存 entity（Create / Update）並呼叫 _context.SaveChangesAsync()
    //   2. 讀取按鈕值：bool saveAndSubmit = Request.Form["submit"] == "save_and_submit";
    //   3. if (saveAndSubmit)
    //      {
    //          var config = await _approvalService.GetApprovalConfig("{KEY}", plantId);
    //          if (config?.ApprovalTemplateId == null)
    //              entity.ApprovalStatus = DocumentApprovalStatus.Approved;
    //          else {
    //              var req = await _approvalService.SubmitForApproval(entity.Id, "{KEY}", config.ApprovalTemplateId, userId);
    //              entity.ApprovalRequestId = req.Id;
    //              entity.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
    //          }
    //          await _context.SaveChangesAsync();
    //      }
    // IsAjax → Json；否則 RedirectToAction

    // PrepareFormDropdowns（CODE_ENTRY_CATS 非空時必須）：
    //   每個 Category 查詢 CodeEntry（PlantId + IsActive + OrderBy SortOrder）放入 ViewBag

    // Export Action（必須，共用 CanPrint 權限）：
    [HttpGet]
    public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        // 與 Index 相同的查詢邏輯（含廠別過濾，不分頁）
        var query = _context.{DbSet}.AsNoTracking()
            .Where(x => x.PlantId == _userContext.PlantId);
        if (!string.IsNullOrEmpty(searchTerm))
            query = query.Where(x => x.{SearchField}.Contains(searchTerm));
        var data = await query.ToListAsync();

        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
        var localNow = _dateTime.Now;

        var req = new ExportRequest
        {
            Title = "{KEY}",
            Headers = [ /* 欄位標題（中英依語系）*/ ],
            Rows = data.Select(item => new object?[]
            {
                /* 對應 Headers 的欄位值，時間欄位用 _dateTime.ToLocalTime(item.CreatedAt) */
            })
        };

        // ❌ 絕對禁止將 Excel case 寫成 "excel"，View 傳的是 "xlsx"，不相符會落入 _ 預設回傳 JSON
        // ✅ Excel 用 _ 預設，或明確寫 "xlsx"；_ 可同時相容 "xlsx" 及任何未知值
        return format switch
        {
            "csv"  => File(_exportService.ToCsv(req),
                          "text/csv",
                          Uri.EscapeDataString($"{KEY}_{localNow:yyyyMMdd}.csv")),
            "json" => File(_exportService.ToJson(req),
                          "application/json",
                          Uri.EscapeDataString($"{KEY}_{localNow:yyyyMMdd}.json")),
            _      => File(_exportService.ToExcel(req),
                          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                          Uri.EscapeDataString($"{KEY}_{_userContext.PlantId}_{localNow:yyyyMMdd_HHmm}.xlsx"))
        };
    }
}
```

---

### 3-2 API Controller（mobile / both）

檔案：`Api/Mobile/{KEY}Controller.cs`

```csharp
[Route("api/mobile/{key}")]
[ApiController]
[Authorize(Policy = "AuthenticatedUser")]
public class {KEY}Controller : ControllerBase
{
    GET  → 清單（含 PlantId 過濾）
    GET {id} → 單筆
    POST → 新增
    PUT {id} → 修改
}
```

完成後提示：
```
⚠ 請至後台「手機功能設定」新增 MobileModule，ModuleKey = "{KEY}"
```

---

### 3-3 View — Index.cshtml（desktop / both）

檔案：`Views/{KEY}/Index.cshtml`

**必須依序包含以下所有區塊**：

```
① @inject / @model / @{ ViewData, canCreate, canUpdate, canDelete } 宣告

② 標題列 + 新增按鈕（canCreate 控管）

③ 搜尋卡片（HAS_SEARCH=Y）
   - <form asp-action="Index" method="get">
   - <input type="hidden" name="searched" value="true" />   ← 絕對不可漏
   - searchTerm 輸入框 + 清除連結 + 搜尋按鈕
   - HAS_PRINT=Y：列印按鈕（window.print()）
   - 匯出下拉按鈕（共用 CanPrint，所有畫面必須）：
     Excel 為主按鈕，下拉展開 CSV / JSON 選項
     format 參數：xlsx / csv / json
   - HAS_PAGINATION=Y：pageSize <select>（10/20/50/100）

   ── 搜尋欄位 UI 選型規則（所有畫面，含報表）──
   │ 欄位類型                                    │ 使用控件                  │
   │────────────────────────────────────────────│──────────────────────────│
   │ 品號（productCode/searchProduct）            │ select.select2-enable    │
   │ 單據編號（可從 DB 取清單的 ID）               │ select.select2-enable    │
   │ 來源單一的代碼（供應商/客戶/倉庫代碼等）      │ select.select2-enable    │
   │ 狀態/類型等固定 Enum 選項                    │ <select>（普通下拉）      │
   │ 日期                                        │ <input type="date">      │
   │ 可跨多來源的關鍵字（如 searchText）           │ <input type="text">      │
   │ 可同時搜尋 code + name + 備註 等多欄位        │ <input type="text">      │

   select2-enable 標準寫法（Controller 需準備 ViewBag.XxxOptions）：
   Controller：
     ViewBag.ProductOptions = await _context.Products
         .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
         .Select(p => new { p.ProductCode, p.ProductName }).ToListAsync();
   View top：
     var productOptions = ViewBag.ProductOptions as IEnumerable<dynamic> ?? Enumerable.Empty<dynamic>();
     string searchProduct = ViewBag.SearchProduct as string ?? "";
   View HTML：
     <select name="searchProduct" class="form-select form-select-sm select2-enable w-100"
             data-placeholder="@Localizer["Product"]" data-allow-clear="true">
         <option value=""></option>
         @foreach (var p in productOptions)
         {
             <option value="@p.ProductCode" selected="@(searchProduct == (string)p.ProductCode)">@p.ProductCode — @p.ProductName</option>
         }
     </select>

④ 未搜尋提示（HAS_SEARCH=Y）
   @if (!(ViewBag.Searched as bool? ?? false)) { ... bi-search 圖示 + SearchFirst 文字 }

⑤ 資料表格（else 區塊）
   欄頭（HAS_SORTING=Y）：每欄用 <a asp-route-sortOrder> 包住 + bi-sort-up/down icon
   資料列：
     HAS_APPROVAL=Y → 待審核記錄隱藏編輯按鈕，顯示「查看審核」
     操作按鈕：canUpdate → 編輯、canDelete → 刪除（均受權限控管）

⑥ 分頁導航（HAS_PAGINATION=Y）— 必須完整包含三件套，缺一 R11 CI 失敗：
   (a) 搜尋卡片 <form> 內加 pageSize <select>（name="pageSize"，onchange="this.form.submit()"）
   (b) 排序連結（欄頭 <a>）必須帶 pageSize = ViewBag.CurrentPageSize 保留每頁筆數
   (c) 資料表格下方 inline 分頁（不得用 _PaginationPartial；可用 _Pagination 或以下 inline 範本）

   ── inline 分頁範本（放在 else { 資料表格 } 區塊最末）──
   <div class="d-flex justify-content-between align-items-center mt-3">
       <div class="text-muted small">@Localizer["PageInfo", Model.PageIndex, Model.TotalPages, Model.TotalItems]</div>
       <nav aria-label="Page navigation">
           <ul class="pagination pagination-sm mb-0">
               <li class="page-item @(Model.HasPreviousPage ? "" : "disabled")">
                   <a class="page-link shadow-none" href="@Url.Action("Index", new { pageNumber = 1, searched = true, {FILTER_PARAMS}, sortOrder, pageSize })">«</a>
               </li>
               <li class="page-item @(Model.HasPreviousPage ? "" : "disabled")">
                   <a class="page-link shadow-none" href="@Url.Action("Index", new { pageNumber = Model.PageIndex - 1, searched = true, {FILTER_PARAMS}, sortOrder, pageSize })">‹</a>
               </li>
               @for (int p = Math.Max(1, Model.PageIndex - 2); p <= Math.Min(Model.TotalPages, Model.PageIndex + 2); p++)
               {
                   <li class="page-item @(p == Model.PageIndex ? "active" : "")">
                       <a class="page-link shadow-none" href="@Url.Action("Index", new { pageNumber = p, searched = true, {FILTER_PARAMS}, sortOrder, pageSize })">@p</a>
                   </li>
               }
               <li class="page-item @(Model.HasNextPage ? "" : "disabled")">
                   <a class="page-link shadow-none" href="@Url.Action("Index", new { pageNumber = Model.PageIndex + 1, searched = true, {FILTER_PARAMS}, sortOrder, pageSize })">›</a>
               </li>
               <li class="page-item @(Model.HasNextPage ? "" : "disabled")">
                   <a class="page-link shadow-none" href="@Url.Action("Index", new { pageNumber = Model.TotalPages, searched = true, {FILTER_PARAMS}, sortOrder, pageSize })">»</a>
               </li>
           </ul>
       </nav>
   </div>

   resx 必須加 PageInfo key：
     zh-TW：第 {0}/{1} 頁，共 {2} 筆
     en-US：Page {0}/{1}, Total {2} records

⑦ Side Panel offcanvas（必須）
   <div class="offcanvas offcanvas-end shadow-lg" id="crudPanel" style="width:min(560px,95vw)">

⑧ Success Toast（必須）
   <div id="successToast" class="toast ... bg-success">

⑨ Anti-Forgery token
   <form id="__AjaxAntiForgery" style="display:none;">@Html.AntiForgeryToken()</form>

⑩ @section Scripts（必須）
   - openPanel(url, title)
   - submitPanel(form, submitter)         ← 必須傳入 submitter
       const fd = new FormData(form);
       if (submitter?.name) fd.set(submitter.name, submitter.value);  ← 絕對不可漏
   - deleteItem(url, name)
```

---

### 3-4 View — _Form.cshtml（desktop / both）

檔案：`Views/{KEY}/_Form.cshtml`

```html
<!-- onsubmit 必須這樣寫 -->
<form id="panelForm" action="@action" method="post"
      onsubmit="submitPanel(this, event.submitter); return false;">
    @Html.AntiForgeryToken()
    <div asp-validation-summary="ModelOnly" ...></div>

    <!-- CODE_ENTRY_CATS 非空 → 對應欄位用 <select>，選項來自 ViewBag.{Cat}Options -->
    <!-- 欄位標籤一律用 @Localizer["FieldName"] -->
    <!-- [Required] 欄位（含 string 型非 nullable 屬性）的 label 後必須加 <span class="text-danger">*</span> -->
    <!-- 範例：
         <label class="form-label">@Localizer["FieldName"] <span class="text-danger">*</span></label>
         <input asp-for="FieldName" class="form-control" />
         <span asp-validation-for="FieldName" class="text-danger small"></span>
         checkbox（IsActive 等 bool 旗標）不加 *
    -->

    <div class="p-3 border-top bg-light d-flex justify-content-end gap-2">
        @if (isReadOnly)
        {
            <button type="button" class="btn btn-secondary" data-bs-dismiss="offcanvas">@Localizer["Close"]</button>
        }
        else
        {
            <button type="button" class="btn btn-secondary" data-bs-dismiss="offcanvas">@Localizer["Cancel"]</button>

            {{!-- HAS_SIMPLIFIED_MODE=Y（與 InvSimplifiedMode 連動的庫存單據）：
                   PrepareFormDropdowns 內必須讀取 InvSimplifiedMode 並設 ViewBag.IsSimplified
                   頂部宣告：bool isSimplified = ViewBag.IsSimplified == true;
                   Confirm 按鈕的 docAction 第三個參數（dialog 訊息）必須依 isSimplified 分成兩個 resx key：
                     ConfirmConfirmSimplified = "...庫存將立即異動"
                     ConfirmConfirmWarehouse  = "...確認後請至【倉庫作業】完成上/下架以更新庫存"
                   按鈕標籤同樣分：ConfirmSimplified / ConfirmWarehouse
                   ⚠️ 禁止在 dialog 訊息中寫死「產生庫存記錄」—非簡化模式下此訊息完全錯誤 --}}

            {{!-- HAS_APPROVAL=Y：送審 / 撤回 按鈕（已存在的單據才顯示） --}}
            @if (!isNew && Model?.ApprovalStatus == DocumentApprovalStatus.Draft)
            {
                <button type="button" class="btn btn-outline-primary"
                        onclick="submitApproval('@Model!.{PK}', '{KEY}')">
                    <i class="bi bi-send me-1"></i>送審
                </button>
            }
            @if (!isNew && Model?.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                <button type="button" class="btn btn-outline-warning"
                        onclick="withdrawApproval('@Model!.{PK}', '{KEY}')">
                    <i class="bi bi-arrow-counterclockwise me-1"></i>撤回
                </button>
            }

            {{!-- 儲存 / 儲存後送審：審核中或已核准不顯示 --}}
            @if (Model?.ApprovalStatus != DocumentApprovalStatus.PendingApproval
              && Model?.ApprovalStatus != DocumentApprovalStatus.Approved)
            {
                {{!-- HAS_APPROVAL=Y 才加「儲存後送審」 --}}
                <button type="submit" class="btn btn-outline-primary" name="submit" value="save_and_submit">
                    <i class="bi bi-send me-1"></i>儲存後送審
                </button>
                <button type="submit" class="btn btn-primary" name="submit" value="save">
                    <i class="bi bi-floppy me-1"></i>@Localizer["Save"]
                </button>
            }
        }
    </div>
</form>
```

> **HAS_APPROVAL=N 時**：移除所有 `@if (isNew / ApprovalStatus)` 條件，只保留「取消」+「儲存」兩個按鈕，不需 `name="submit"` 區分值。

---

### 3-5 Resource 檔案（必須建立 4 個）

路徑：`Resources/Views/{KEY}/`

**Index.zh-TW.resx / Index.en-US.resx** 必須包含：

| 鍵 | zh-TW | en-US |
|----|-------|-------|
| `{KEY}` | {中文名稱} | {英文名稱} |
| `Add{KEY}` | 新增{中文名稱} | Add {英文名稱} |
| `Search` | 搜尋 | Search |
| `SearchPlaceholder` | 搜尋... | Search... |
| `SearchFirst` | 請輸入查詢條件後按搜尋以載入資料 | Enter criteria then search |
| `PageSize` | 每頁顯示 | Items per page |
| `NoData` | 目前尚無符合條件的資料 | No data found |
| `Edit` | 編輯 | Edit |
| `Delete` | 刪除 | Delete |
| `ConfirmDelete` | 確定要刪除？ | Are you sure? |
| `PageInfo` | 第 {0} 頁，共 {1} 頁 | Page {0} of {1} |
| `First` | 首頁 | First |
| `Last` | 末頁 | Last |
| `Previous` | 上一頁 | Previous |
| `Next` | 下一頁 | Next |
| `Actions` | 操作 | Actions |
| `Print` | 列印 | Print |（HAS_PRINT=Y）|
| 各業務欄位 | | |

**_Form.zh-TW.resx / _Form.en-US.resx** 必須包含：
`Save`、`Cancel` + 各業務欄位標籤

> **⚠ View 中使用的 key 必須與 resx 中的 key 完全一致（大小寫、有無 's'）**

---

### 3-6 Menu 登錄（CoreMenuContributor + SharedResource）

在 `Services/CoreMenuContributor.cs` 對應群組的 SubItems 插入：
```csharp
new MenuItem { Title = _localizer["{KEY}"], Controller = "{KEY}",
               Action = "Index", Icon = "bi-{MENU_ICON}", MenuKey = "{KEY}" }
```

在 `Resources/SharedResource.zh-TW.resx` 與 `en-US.resx` 加入：
- `{KEY}` = `{中文名稱}` / `{英文名稱}`

> ⚠ **不得直接修改 `MenuService.cs`**（該檔為收集器，不含 menu 定義）

---

### 3-7 Model

必須包含：
- `[Key] string Id = Guid.NewGuid().ToString()`（單據型主檔改用業務單號為 PK，如 `SalesOrderId`，由 `IDocNumberService` 產生）
- HAS_MULTI_PLANT=Y：`public string? PlantId { get; set; }`
- HAS_APPROVAL=Y：
  ```csharp
  public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;
  public string? ApprovalRequestId { get; set; }
  ```
- 所有欄位加上 `[Required]`、`[MaxLength]` 等驗證屬性

> **⚠ R5b — value-type 業務欄位需有全域顯示名 key**：凡 `decimal`/`int`/`DateTime` 等
> 非 nullable 值型別欄位（即使沒有 `[Required]`）會被慣例測試 **R5b** 要求在
> `Resources/SharedResource.{zh-TW,en-US}.resx` 皆有同名 key（Phase C：驗證在地化走 Core SharedResource）。
> 純內部/計算欄位若不需顯示，改加 `[NotMapped]` 或 `[Display(Name=...)]` 排除，否則建置期 R5b 會擋 CI。

> **🔒 並發控制（樂觀鎖）— 單據型必加**：多人協作的單據型主檔（有明細／簽核／狀態流轉）
> 必須加 PostgreSQL `xmin` 樂觀並發 token，避免覆蓋他人修改。完整規範見
> `.claude/specifications/ord-module.md` §13。要點：
> - Entity：`public uint xmin { get; set; }`（PG 系統欄位，**不需 migration**）
> - Fluent（`ApplicationDbContextExtensions.cs`）：`builder.Entity<{MODEL}>().UseXminAsConcurrencyToken();`
> - VM：`public uint RowVersion { get; set; }`（`_Form` 加 `<input type="hidden" asp-for="RowVersion" />`）
> - Controller Save 編輯路徑：`_context.Entry(e).Property(x => x.xmin).OriginalValue = vm.RowVersion;`
>   並 `catch (DbUpdateConcurrencyException)` 回 `_Form` 顯示「已被他人修改」訊息。
> - ⚠ 勿用 SQL-Server 式 `[Timestamp] byte[] RowVersion`——在 PostgreSQL 不會自動遞增、形同失效。

### 3-8 DbContext 與 Migration

在 `Data/ApplicationDbContext.cs` 的 DbSet 區段加入：

```csharp
public DbSet<{MODEL}> {DbSet} { get; set; }
```

若有複合主鍵或唯一索引，在 `OnModelCreating` 中加入 Fluent API 設定。

完成後執行 Migration：
```bash
dotnet ef migrations add {KEY}_Init
```

---

## Step 4：完成後自我確認（必須逐項核對）

```
Controller：
[ ] Index 有 if (!searched) return View(null)（HAS_SEARCH=Y）
[ ] Index 有 PlantId 過濾（HAS_MULTI_PLANT=Y）
[ ] Index 有排序 switch（HAS_SORTING=Y）
[ ] Index 有 PaginatedList（HAS_PAGINATION=Y）
[ ] Create/Edit POST 有 PlantId 賦值（HAS_MULTI_PLANT=Y）
[ ] Save action 讀 Request.Form["submit"] 判斷 save_and_submit 分支（HAS_APPROVAL=Y）
[ ] _Form 有「儲存後送審」+ 「送審」+ 「撤回」按鈕，條件判斷正確（HAS_APPROVAL=Y）
[ ] HAS_SIMPLIFIED_MODE=Y：PrepareFormDropdowns 設 ViewBag.IsSimplified；Confirm dialog 訊息使用 ConfirmConfirmSimplified / ConfirmConfirmWarehouse 兩個 key，禁止寫死「產生庫存記錄」
[ ] PrepareFormDropdowns 有 CodeEntry 查詢（CODE_ENTRY_CATS 非空）

View Index：
[ ] <input type="hidden" name="searched" value="true" /> 存在於搜尋表單中
[ ] 有未搜尋提示區塊（HAS_SEARCH=Y）
[ ] pageSize <select>（HAS_PAGINATION=Y）
[ ] 欄頭排序連結 + sort icon（HAS_SORTING=Y）
[ ] 分頁導航（首/上/頁碼/下/末）（HAS_PAGINATION=Y）
[ ] Side Panel offcanvas HTML 存在
[ ] Success Toast HTML 存在
[ ] submitPanel 接受 submitter 參數且有 fd.set(submitter.name, submitter.value)

View _Form：
[ ] onsubmit="submitPanel(this, event.submitter); return false;"
[ ] [Required] 欄位的 label 後有 <span class="text-danger">*</span>（checkbox 旗標除外）
[ ] type="number" 的數量/金額欄位 step="any"（絕不寫死 step="0.001" 等小數；純整數欄位可省略 step）

Resources：
[ ] 4 個 resx 已建立
[ ] Index resx 含全部必要鍵
[ ] View 使用的 key 與 resx key 完全一致

Menu & i18n：
[ ] CoreMenuContributor 已插入選單項目（對應群組 SubItems）
[ ] SharedResource 兩個 resx 已加入 MENU 文字（KEY 與 CoreMenuContributor 一致）

DbContext & Migration：
[ ] DbSet 已加入 Data/ApplicationDbContext.cs
[ ] dotnet ef migrations add {KEY}_Init 已執行

並發控制（單據型主檔）：
[ ] Entity 有 public uint xmin；ApplicationDbContextExtensions 有 UseXminAsConcurrencyToken()（見 ord-module.md §13）
[ ] VM 有 uint RowVersion；_Form 有 <input type="hidden" asp-for="RowVersion" />
[ ] Controller Save 編輯路徑設 OriginalValue=vm.RowVersion 並 catch DbUpdateConcurrencyException
[ ] value-type 業務欄位在 SharedResource 雙語皆有 key（R5b），否則 [NotMapped]/[Display] 排除

匯出：
[ ] Controller 有 Export Action（注入 IExportService）
[ ] Export switch Excel case 為 `_`（default）或 `"xlsx"`，❌ 禁止寫 `"excel"`
[ ] View 搜尋列有匯出下拉按鈕（xlsx / csv / json）
[ ] Index resx 已加入 ExportExcel 翻譯

完成後提示：
[ ] HAS_APPROVAL=Y → 提示至「作業類型簽核設定」新增 DocumentType = {KEY}
[ ] DEVICE=mobile 或 both → 提示至「手機功能設定」新增 MobileModule，ModuleKey = {KEY}
```

---

## 附：常見錯誤（務必避免）

| 錯誤 | 正確做法 |
|------|---------|
| `submitPanel(form)` 沒傳 submitter | 必須 `submitPanel(this, event.submitter)` |
| `new FormData(form)` 未加 `fd.set(submitter.name, submitter.value)` | 否則核准/退回按鈕的 action 值會遺失，導致全部變成「拒絕」 |
| 搜尋表單沒有 `searched=true` hidden input | 否則搜尋後重整頁面會回到未搜尋狀態 |
| Index 沒有 `if (!searched) return View(null)` | 否則頁面一開啟就查全部資料 |
| View 用 `Plants`，resx key 是 `Plant` | key 必須完全一致 |
| Create POST 沒設 `PlantId` | 多廠別資料將混淆 |
| `DateTime.UtcNow` | HAS_TIMEZONE=Y 時改用 `_dateTime.UtcNow` |
| 使用者輸入日期直接存入 DB | 必須先 `_dateTime.ToUtcTime(inputDate)` 轉為 UTC 再存 |
| View 顯示 `item.Date.ToLocalTime()` | 改用 `DateTimeSvc.ToLocalTime(item.Date)`（依廠別時區）|
| Export switch case 寫 `"excel"` | View 的 `asp-route-format` 傳 `"xlsx"`，case 必須是 `"xlsx"` 或 `_`（default）；寫 `"excel"` 永遠不匹配，落入 `_` 回傳 JSON |
| 顧問在模組 Views 下缺少 `_ViewImports.cshtml` | 模組 Views（`/Modules/{N}/Views/`）不繼承根 `/Views/_ViewImports.cshtml`；必須自備含 `@addTagHelper` 的 `_ViewImports.cshtml`，否則所有 Tag Helper 靜默失效 |

---

## Step 5：自動產生測試並執行確認

> **畫面程式碼完成後，必須依序產生三層測試，並執行確認全部通過。**

---

### 5-1 單元測試

檔案：`SmartFactory.MES.Tests/{KEY}ControllerTests.cs`

繼承 `TestBase`（提供 InMemory DbContext、MockPermissionService、MockUserContext）。

> **⚠ 建構子簽章是測試的硬相依**：下方 `new {KEY}Controller(...)` 的參數順序／數量
> 必須與實際 Controller 建構子**完全一致**。單據型 Controller 常額外注入
> `IApprovalService`／`ISystemParamService`／`IDocNumberService`／`IInventoryReservationService`
> 等服務（非泛型範例的 4 參數）——新增/移除注入時，**務必同步更新所有引用該建構子的測試**，
> 否則測試組件編譯失敗會連帶讓「整個 Tests 專案（含慣例測試 R1–R11）無法執行」。
> 服務相依用 `new Mock<IXxx>().Object` 帶入；有讀取分支者需 `Setup(...).ReturnsAsync(...)`。

```csharp
namespace SmartFactory.MES.Tests;

public class {KEY}ControllerTests : TestBase
{
    private {KEY}Controller CreateController()
    {
        var permSvc = MockPermissionService(canRead: true, canCreate: true,
                                            canUpdate: true, canDelete: true);
        var userCtx = MockUserContext(plantId: "P001");
        var exportSvc = new Mock<IExportService>().Object;
        return new {KEY}Controller(permSvc, userCtx, CreateDbContext(), exportSvc);
    }

    // ── Index 未搜尋 → 回傳 null Model ──
    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: false) as ViewResult;
        Assert.Null(result?.Model);
    }

    // ── Index 搜尋有結果 → 回傳 PaginatedList ──
    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        var db = CreateDbContext();
        db.{DbSet}.Add(new {MODEL} { Id = Guid.NewGuid().ToString(), PlantId = "P001" });
        await db.SaveChangesAsync();

        var permSvc = MockPermissionService(canRead: true);
        var userCtx = MockUserContext(plantId: "P001");
        var exportSvc = new Mock<IExportService>().Object;
        var ctrl = new {KEY}Controller(permSvc, userCtx, db, exportSvc);

        var result = await ctrl.Index(null, 1, 10, null, searched: true) as ViewResult;
        var list = result?.Model as PaginatedList<{MODEL}>;
        Assert.NotNull(list);
        Assert.True(list.TotalCount >= 1);
    }

    // ── 無讀取權限 → Forbid ──
    [Fact]
    public async Task Index_NoPermission_ReturnsForbid()
    {
        var permSvc = MockPermissionService(canRead: false);
        var userCtx = MockUserContext(plantId: "P001");
        var exportSvc = new Mock<IExportService>().Object;
        var ctrl = new {KEY}Controller(permSvc, userCtx, CreateDbContext(), exportSvc);
        var result = await ctrl.Index(null, null, null, null, searched: true);
        Assert.IsType<ForbidResult>(result);
    }

    // ── Create POST 成功 → 資料庫有記錄 ──
    [Fact]
    public async Task Create_ValidModel_SavesToDatabase()
    {
        var db = CreateDbContext();
        var ctrl = new {KEY}Controller(
            MockPermissionService(canCreate: true),
            MockUserContext(plantId: "P001"),
            db,
            new Mock<IExportService>().Object);

        var model = new {MODEL} { /* 填入必要欄位 */ };
        await ctrl.Create(model);

        Assert.Equal(1, db.{DbSet}.Count());
        Assert.Equal("P001", db.{DbSet}.First().PlantId);
    }
}
```

---

### 5-2 整合測試

檔案：`SmartFactory.MES.IntegrationTests/{KEY}/{KEY}IntegrationTests.cs`

使用 `TestWebApplicationFactory`（`Microsoft.AspNetCore.Mvc.Testing`）測試真實 HTTP 端點。

```csharp
namespace SmartFactory.MES.IntegrationTests.{KEY};

public class {KEY}IntegrationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // ── 未登入 → 重導向至登入頁 ──
    [Fact]
    public async Task Index_Unauthenticated_RedirectsToLogin()
    {
        var response = await _client.GetAsync("/{KEY}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    // ── 登入後可讀取 Index（200 OK）──
    [Fact]
    public async Task Index_AuthenticatedAdmin_ReturnsOk()
    {
        await factory.LoginAsAdminAsync(_client);
        var response = await _client.GetAsync("/{KEY}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── GET /Create（空表單）→ 200 OK ──
    [Fact]
    public async Task Create_Get_ReturnsForm()
    {
        await factory.LoginAsAdminAsync(_client);
        var response = await _client.GetAsync("/{KEY}/Create");
        response.EnsureSuccessStatusCode();
    }

    // ── POST /Create（有效資料）→ 不回傳 4xx ──
    [Fact]
    public async Task Create_Post_ValidData_Succeeds()
    {
        await factory.LoginAsAdminAsync(_client);
        var token = await factory.GetAntiForgeryTokenAsync(_client, "/{KEY}/Create");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            /* 加入各必填欄位 */
        });
        var response = await _client.PostAsync("/{KEY}/Create", content);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect);
    }

    // ── Export xlsx → 回傳 .xlsx 內容型別 ──
    [Fact]
    public async Task Export_Xlsx_ReturnsSpreadsheet()
    {
        await factory.LoginAsAdminAsync(_client);
        var response = await _client.GetAsync("/{KEY}/Export?format=xlsx&searched=true");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);
    }
}
```

---

### 5-3 UI 測試（Playwright）

參考 `/webapp-testing` skill 產生：

- **Page Object**：`SmartFactory.MES.UITests/Pages/{KEY}Page.cs`
- **測試類別**：`SmartFactory.MES.UITests/{KEY}/{KEY}Tests.cs`

最少須包含：查詢、新增、刪除、匯出（xlsx）四個測試案例。

> ⚠ UI 測試需先啟動應用程式（`dotnet run --project SmartFactory.MES`）才能執行。

---

### 5-4 執行指令（必須全部通過後才算完成）

```bash
# 1. 編譯確認
dotnet build SmartFactory.MES.sln

# 2. 單元測試
dotnet test SmartFactory.MES.Tests/ -v normal

# 3. 整合測試
dotnet test SmartFactory.MES.IntegrationTests/ -v normal

# 4. UI 測試（需先啟動應用程式）
dotnet test SmartFactory.MES.UITests/ --filter "FullyQualifiedName~{KEY}" -v normal
```

**所有測試通過後，在 Step 4 Checklist 最後補充：**

```
測試：
[ ] 單元測試產生並通過（SmartFactory.MES.Tests/{KEY}ControllerTests.cs）
[ ] 整合測試產生並通過（SmartFactory.MES.IntegrationTests/{KEY}/{KEY}IntegrationTests.cs）
[ ] UI 測試產生（SmartFactory.MES.UITests/{KEY}/{KEY}Tests.cs）
[ ] dotnet build 無 Error
[ ] dotnet test SmartFactory.MES.Tests 全部通過
[ ] dotnet test SmartFactory.MES.IntegrationTests 全部通過
```


## 安全修正補充（2026-11-05）

- 廠區資料寫入須驗證目前角色與廠區的授權關係；不能只相信 ActivePlantId 或隱藏欄位。
- 畫面／裝置切廠後，舊表單必須拒絕寫入；預期廠區只作防誤寫，不能替代伺服器授權。
- 全域主檔與廠區單據分開處理，不將全域主檔強制套用廠區條件。
- 角色、廠區切換使用 Core 共用服務，不在控制器複製授權規則。新訊息須同時補中英文資源。
