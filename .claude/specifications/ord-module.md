---
description: ORD 模組（含 PUR 共通慣例）逆向整理。作為 source of truth，供 MFG 等後續模組的開發參照。包含 Entity 結構、狀態機、Controller 慣例、View 慣例、命名規範、並發控制（xmin）。PUR/委外的業務領域設計另見 pur-domain.md。
---

# ORD + PUR 模組實作慣例（ord-module.md）

> 來源：逆向工程 `/home/dev/ProjectZ/Modules/ORD` 與 `/home/dev/ProjectZ/Modules/PUR`。
> 這兩個模組是 ProjectZ 的 **source of truth**。新模組（尤其 MFG）必須遵循此處記載的慣例。
>
> **本文件只記錄 ORD/PUR 獨有的內容。** 通用基礎設施規則請查原始文件：
> - 檔案路徑、`_ViewImports`、Menu、DbContext → **new-screen-projectz.md §1-4**
> - UOM 小數點規範（Controller/View/JS）→ **CLAUDE.md 或 new-screen-projectz.md §6**
> - Currency 小數點規範（Controller/View/JS）→ **CLAUDE.md 或 new-screen-projectz.md §7**
> - Delete 前的簽核狀態防護 → **CLAUDE.md「線上簽核」段落**
> - IPlantEntity 全域 vs 廠別判斷 → **LOCAL_RULES.md「Entity 資料範圍規範」**

---

## 1. 模組定位

| 模組 | 範圍 | 主要 Entity |
|------|------|------------|
| **ORD** | 銷售管理：訂單 → 出貨 → 銷退、內部出料 | SalesOrder / GoodsShipment / SalesReturn / InternalOrder |
| **PUR** | 採購管理：請購 → 採購 → 進貨 → 退出 | PurchaseRequest / PurchaseOrder / GoodsReceipt / GoodsReturn |

---

## 2. 模組檔案結構

```
Modules/{MODULE}/
├── {MODULE}Module.cs                   # IAppModule 實作（服務注冊）
├── Controllers/
│   └── {Entity}Controller.cs
├── Models/
│   ├── {Entity}.cs                     # EF Entity
│   └── {Entity}Line.cs
├── ViewModels/
│   └── {Entity}FormVm.cs              # Form POST 用 ViewModel
├── Views/
│   ├── _ViewImports.cshtml            # 範本見 new-screen-projectz.md §2
│   ├── _ViewStart.cshtml              # 範本見 new-screen-projectz.md §2
│   └── {Entity}/
│       ├── Index.cshtml
│       ├── _Form.cshtml
│       └── Print.cshtml               # 若有列印需求
└── Services/                           # 複雜業務邏輯（可選）
    ├── I{Module}Service.cs
    └── {Module}Service.cs

Resources/Modules/{MODULE}/Views/{Entity}/
    Index.zh-TW.resx
    Index.en-US.resx
    _Form.zh-TW.resx
    _Form.en-US.resx

Modules/{MODULE}.Tests/                  # 或集中於 SmartFactory.MES.Tests
    {Entity}Tests.cs                      # 單元/整合測試（見 §11-12）
```

**ORD 模組 `_ViewImports.cshtml` 實際 using**（補充 new-screen-projectz.md §2 的範本）：

```cshtml
@using SmartFactory.MES.Modules.ORD.Models
@using SmartFactory.MES.Modules.INV.Models
@using SmartFactory.MES.Modules.INV.Services
@using SmartFactory.MES.Modules.BAS.Services
@inject SmartFactory.MES.Services.IDateTimeService DateTimeSvc
```

---

## 3. Entity 結構

### 3.1 共同規則

> IPlantEntity 全域 vs 廠別的判斷標準見 **LOCAL_RULES.md「Entity 資料範圍規範」**。

- **廠別作業單據**一律實作 `IPlantEntity`（含 `public string? PlantId { get; set; }`）
- **主從關係**：Detail 的複合 PK = `({MasterId}, LineNo)`，LineNo 由 Controller 從 1 遞增
- **第三層 LocationDetail**：PK = `Id (int)`，FK 複合指向 Detail，設 `Cascade Delete`
- decimal 精度：數量 `(18,4)`，單價 `(18,6)`（保留精度供計算），匯率 `(18,6)`
- Fluent API Unique Index：廠別文件一律含 `PlantId`（如 `new { d.PlantId, d.OrderDate }`）

### 3.2 ORD 模組 Entities

#### SalesOrder（銷售訂單）
| 欄位 | 型別 | 說明 |
|------|------|------|
| `SalesOrderId` | string(30) PK | DocNumberService 產生 |
| `PlantId` | string? | IPlantEntity |
| `OrderDate` | DateOnly | |
| `OrderType` | string? | CodeEntry: PZ.SalesOrderType |
| `CompanyCode` | string(20) 必填 | FK → Company（IsCustomer） |
| `DepartmentCode` | string?(20) | |
| `CustomerOrderNo` | string?(50) | |
| `TaxCode` | string?(20) | |
| `TaxRate` | decimal(18,4) | |
| `TotalAmount` | decimal(18,4) | = Σ Lines.TotalAmount |
| `ApprovalStatus` | enum | Draft/PendingApproval/Approved/Rejected |
| `ApprovalRequestId` | string? | FK → ApprovalRequest |
| `Remarks` | string?(500) | |
| `CreatedBy` | string(450) | FK → User |
| `CreatedAt`, `UpdatedAt` | DateTime | |
| `Lines` | Navigation | ICollection\<SalesOrderLine\> |

#### SalesOrderLine（訂單明細）
| 欄位 | 型別 | 說明 |
|------|------|------|
| `SalesOrderId`, `LineNo` | 複合 PK | |
| `ProductCode` | string(50) 必填 | |
| `DeliveryDate` | DateOnly? | |
| `OrderQty` | decimal(18,4) | |
| `UomCode` | string(20) | |
| `StockQty` | decimal(18,4) | 換算後庫存單位量 |
| `StockUomCode` | string(20) | |
| `UnitPrice` | decimal(18,6) | |
| `CurrencyCode` | string(20) | |
| `ExchangeRate` | decimal(18,6) default=1 | |
| `TaxCode`, `TaxRate` | | |
| `LineAmount`, `TaxAmount`, `TotalAmount` | decimal(18,4) | |
| `ShippedQty` | decimal(18,4) | 由 GoodsShipment.Post 累加，不可直接修改 |
| `ReturnedQty` | decimal(18,4) | 由 SalesReturn.Post 累加 |
| `IsClosed` | bool | |
| `Remarks` | string?(500) | |

#### GoodsShipment（出貨單）
- 雙狀態：`ApprovalStatus`（簽核）+ `Status`（DocStatus：Draft/Confirmed/Posted/Void）
- `SalesOrderId` nullable（可不引用銷售訂單直接建）
- `TypeCode` 預設 "GS"
- 三層：GoodsShipment → GoodsShipmentLine → GoodsShipmentLocationDetail

#### GoodsShipmentLine 追蹤欄位
- `SalesOrderId`, `SoLineNo`：來源訂單
- `QtySourceCleared`：已沖銷到訂單的量
- `QtyPutAway` = Σ LocationDetails.Qty
- `ReturnedQty`：已退回量

#### GoodsShipmentLocationDetail（第三層，儲位明細）
- PK: `Id` (int)
- FK: (GoodsShipmentId, LineNo) → GoodsShipmentLine（**Cascade Delete**）
- 欄位：`LocationCode`、`WhCode`（由 LocationCode 回寫）、`LotNo`、`Qty`、`CreatedAt`、`Remarks`

#### SalesReturn（銷退單）
- 結構同 GoodsShipment（雙狀態，三層）
- `GoodsShipmentId` nullable（可追蹤來源出貨單）
- Line 追蹤：`GoodsShipmentId`, `ShipmentLineNo`

#### InternalOrder（內部訂單）
- 雙狀態：`ApprovalStatus` + `Status`
- `TypeCode`："NO"（出料，庫存-）/ "NR"（退料，庫存+）
- Lines 無金額欄位，有 `WhCode`, `LocationCode`, `LotNo`（直接指定倉位）

### 3.3 PUR 模組 Entities

#### PurchaseRequest（請購單）
- 無 `DocStatus`，只有 `ApprovalStatus`
- Line 追蹤：`OrderedQty`（已轉採購量）、`IsClosed`

#### PurchaseOrder（採購單）
- `SupplierCode`（FK → Company.IsSupplier）
- Line 追蹤：`PurchaseRequestId`, `PrLineNo`（可空，直接建採購）、`ReceivedQty`、`IsClosed`

#### GoodsReceipt（進貨單）
- 雙狀態：`ApprovalStatus` + `Status`（DocStatus）
- `TypeCode`："PI"（進貨）/ "OI"（委外進貨）
- 三層結構（同 GoodsShipment）

#### GoodsReturn（進貨退出）
- 雙狀態，結構同 GoodsReceipt
- `TypeCode`："PF"（進貨退出）/ "OT"（委外進貨退出）

---

## 4. 業務流程狀態機

### 4.1 單狀態文件（只有 ApprovalStatus）
適用：**SalesOrder、PurchaseRequest、PurchaseOrder**

```
Draft
  ↓ Submit（若無簽核設定 → 直接 Approved）
PendingApproval
  ↓ [核准] → Approved
  ↓ [退回] → Rejected
  ↓ Withdraw → Draft
Rejected
  ↓ Save（修改後自動回 Draft）
Approved
  → 可供後續單據引用
```

**關鍵規則**：送審中（PendingApproval）不可修改、不可刪除，必須先撤回。

### 4.2 雙狀態文件（ApprovalStatus + DocStatus）
適用：**GoodsShipment、SalesReturn、InternalOrder、GoodsReceipt、GoodsReturn**

```
Save    → ApprovalStatus: Draft,     DocStatus: Draft
Submit  → ApprovalStatus: PendingApproval
Confirm → DocStatus: Confirmed（前提：DocStatus == Draft）
UnConfirm → DocStatus: Draft（前提：Confirmed 且 QtyPutAway == 0）
Post    → DocStatus: Posted，觸發 InventoryMovement（前提：Confirmed）
Void    → DocStatus: Void，建反向 InventoryMovement（前提：Posted）
```

**UnConfirm 前提**：`QtyPutAway == 0`（尚未有倉庫作業）

### 4.3 各 DocType 的累計欄位

| 觸發動作 | 累計目標 |
|---------|---------|
| GoodsShipment.Post | SalesOrderLine.ShippedQty += ShipQty |
| GoodsShipment.Void | SalesOrderLine.ShippedQty -= ShipQty（反向扣減）|
| SalesReturn.Post | GoodsShipmentLine.ReturnedQty +=；SalesOrderLine.ReturnedQty += |
| GoodsReceipt.Post | PurchaseOrderLine.ReceivedQty += |
| GoodsReturn.Post | PurchaseOrderLine.ReceivedQty -=（進貨退出，反向扣減）|
| PurchaseOrder.Save | PurchaseRequestLine.OrderedQty += |
| InternalOrder.Post | 無來源累計欄位；僅依 TypeCode 觸發 InventoryMovement（NO 出庫 / NR 入庫）|

---

## 5. Controller 標準結構

### 5.1 宣告與注入

```csharp
[Authorize]
public class SalesOrderController : Controller
{
    private const string MenuKey = "SalesOrder";  // 對應 ProjectZMenuContributor 的 Key
    private const string DocType = "SalesOrder";  // 對應 DocNumberService + ApprovalService

    public SalesOrderController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IDateTimeService dateTimeService,
        IApprovalService approvalService,
        IDocNumberService docNumberService,
        IUomService uomService,
        ICurrencyService currencyService,
        IInventoryPostingService inventoryPostingService) { ... }  // 雙狀態文件 Post 用；單狀態文件可省略

    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";
}
```

> `ResolveBaseCurrencyDpAsync()` 標準寫法見 **CLAUDE.md「金額欄位 Currency」段落**。
> 不含金額的 Controller（Routing、WorkCenter 等）不注入 `ICurrencyService`。
> 過帳服務實際名稱為 `IInventoryPostingService`（INV 模組提供，方法 `PostDocAsync`）；
> 只有雙狀態文件（GoodsShipment/SalesReturn/InternalOrder/GoodsReceipt/GoodsReturn/OutsourceReceipt）需注入。

### 5.2 PrepareFormDropdowns

**ORD 模組特有的下拉選項**（UOM/Currency 精度的注入規則見 new-screen-projectz.md §6-7）：

```csharp
private async Task PrepareFormDropdowns()
{
    // 客戶下拉（CompanyCode 過濾 IsCustomer）
    ViewBag.CustomerOptions = await _context.Companies
        .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
        .OrderBy(c => c.CompanyCode)
        .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

    // 供應商下拉（PUR 模組用）
    ViewBag.SupplierOptions = await _context.Companies
        .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
        .OrderBy(c => c.CompanyCode)
        .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

    // 產品下拉（含 UOM 預設值，讓前端品號切換時自動帶入）
    ViewBag.ProductOptions = await _context.Products
        .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
        .Select(p => new { p.ProductCode, p.ProductName, p.SalesUom, p.StockUom })
        .ToListAsync();

    // CodeEntry 下拉（必須用 ToDisplayOptionsAsync，不得直接 Select NameZh）
    ViewBag.OrderTypeOptions = await _context.CodeEntries
        .AsNoTracking().Where(e => e.Category == "PZ.SalesOrderType" && e.IsActive)
        .OrderBy(e => e.SortOrder).ToDisplayOptionsAsync();

    // 精度對應表
    ViewBag.UomDecimals      = await _uomService.GetAllDecimalPlacesAsync();
    ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();

    // 列印權限
    ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
}
```

### 5.3 Index Action

```csharp
public async Task<IActionResult> Index(
    string? searchTerm, string? companyCode,
    DateOnly? dateFrom, DateOnly? dateTo,
    int? pageNumber, int? pageSize, string? sortOrder,
    bool searched = false)
{
    var plantId = _userContext.PlantId;

    // 查詢條件回填 ViewBag（讓 View 還原查詢列）
    ViewBag.SearchTerm      = searchTerm;
    ViewBag.CompanyCode     = companyCode;
    ViewBag.DateFrom        = dateFrom;
    ViewBag.DateTo          = dateTo;
    ViewBag.Searched        = searched;
    ViewBag.CurrentPageSize = pageSize ?? 10;
    ViewBag.CurrentSort     = sortOrder;
    ViewBag.IdSort          = (sortOrder == "id_asc") ? "id_desc" : "id_asc";
    ViewBag.DateSort        = (sortOrder == "date_asc") ? "date_desc" : "date_asc";

    ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
    ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
    ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
    ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);
    ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

    // 查詢列用下拉
    ViewBag.CustomerOptions = await _context.Companies
        .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
        .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

    // 未搜尋時不查 DB，直接回傳 null Model
    if (!searched) return View((PaginatedList<SalesOrder>?)null);

    var query = _context.SalesOrders.AsNoTracking()
        .Include(so => so.Lines)
        .Where(so => so.PlantId == plantId);

    if (!string.IsNullOrWhiteSpace(searchTerm))
        query = query.Where(so =>
            so.SalesOrderId.Contains(searchTerm) ||
            (so.CustomerOrderNo != null && so.CustomerOrderNo.Contains(searchTerm)) ||
            (so.Remarks        != null && so.Remarks.Contains(searchTerm)));

    if (!string.IsNullOrWhiteSpace(companyCode))
        query = query.Where(so => so.CompanyCode == companyCode);

    if (dateFrom.HasValue) query = query.Where(so => so.OrderDate >= dateFrom.Value);
    if (dateTo.HasValue)   query = query.Where(so => so.OrderDate <= dateTo.Value);

    query = sortOrder switch
    {
        "id_asc"    => query.OrderBy(so => so.SalesOrderId),
        "date_asc"  => query.OrderBy(so => so.OrderDate),
        "date_desc" => query.OrderByDescending(so => so.OrderDate),
        _           => query.OrderByDescending(so => so.SalesOrderId),
    };

    return View(await PaginatedList<SalesOrder>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 10));
}
```

### 5.4 Create / Edit / Details

```csharp
public async Task<IActionResult> Create()
{
    if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
    await PrepareFormDropdowns();
    var vm = new SalesOrderFormVm
    {
        CreatedBy = _userContext.UserId ?? "",
        OrderDate = _dateTimeService.GetLocalDate()
    };
    return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
}

public async Task<IActionResult> Edit(string id)
{
    if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
    var so = await _context.SalesOrders.Include(s => s.Lines)
        .FirstOrDefaultAsync(s => s.SalesOrderId == id);
    if (so == null) return NotFound();
    await SyncApprovalStatusAsync(so);   // 從 ApprovalService 同步最新狀態
    await PrepareFormDropdowns();
    return IsAjax ? PartialView("_Form", MapToVm(so)) : RedirectToAction(nameof(Index));
}

// 唯讀，用於「待我簽核」Side Panel
public async Task<IActionResult> Details(string id)
{
    var so = await _context.SalesOrders.Include(s => s.Lines)
        .FirstOrDefaultAsync(s => s.SalesOrderId == id);
    if (so == null) return NotFound();
    await PrepareFormDropdowns();
    ViewBag.IsReadOnly = true;
    return IsAjax ? PartialView("_Form", MapToVm(so)) : RedirectToAction(nameof(Index));
}
```

### 5.5 Save (POST)

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Save(SalesOrderFormVm vm)
{
    if (!ModelState.IsValid)
    {
        await PrepareFormDropdowns();
        return PartialView("_Form", vm);
    }
    try
    {
        var userId  = _userContext.UserId  ?? "";
        var plantId = _userContext.PlantId ?? "";

        if (string.IsNullOrEmpty(vm.SalesOrderId))
        {
            // 新增：DocNumberService 產生單號
            var so = new SalesOrder
            {
                SalesOrderId    = await _docNumberService.NextAsync(DocType, plantId, vm.OrderDate),
                OrderDate       = vm.OrderDate,
                CompanyCode     = vm.CompanyCode,
                CustomerOrderNo = vm.CustomerOrderNo,
                TaxCode         = vm.TaxCode,
                Remarks         = vm.Remarks,
                CreatedBy       = userId,
            };
            ApplyLines(so, vm);
            _context.SalesOrders.Add(so);
        }
        else
        {
            var so = await _context.SalesOrders.Include(s => s.Lines)
                .FirstOrDefaultAsync(s => s.SalesOrderId == vm.SalesOrderId)
                ?? throw new InvalidOperationException("找不到此銷售訂單。");

            if (so.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                throw new InvalidOperationException("銷售訂單審核中，請先撤回後再修改。");

            // xmin 樂觀鎖：以前端帶回的版本作為 OriginalValue，EF 產生 WHERE xmin = @original（見 §13）
            _context.Entry(so).Property(e => e.xmin).OriginalValue = vm.RowVersion;

            so.OrderDate       = vm.OrderDate;
            so.CompanyCode     = vm.CompanyCode;
            so.CustomerOrderNo = vm.CustomerOrderNo;
            so.TaxCode         = vm.TaxCode;
            so.Remarks         = vm.Remarks;
            so.UpdatedAt       = DateTime.UtcNow;

            // 明細：差異更新（不整批刪除重建，見 §5.6 注意事項）
            UpdateLines(so, vm);
        }

        await _context.SaveChangesAsync();
        return Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
    }
    catch (InvalidOperationException ex)
    {
        ModelState.AddModelError(string.Empty, ex.Message);
        await PrepareFormDropdowns();
        return PartialView("_Form", vm);
    }
}
```

### 5.6 Helper Methods

```csharp
// Entity → ViewModel（唯讀追蹤欄位也要帶入，供 View 顯示）
private static SalesOrderFormVm MapToVm(SalesOrder so) => new()
{
    SalesOrderId    = so.SalesOrderId,
    ApprovalStatus  = so.ApprovalStatus,
    RowVersion      = so.xmin,          // xmin 樂觀鎖，回送前端 hidden（見 §13）
    OrderDate       = so.OrderDate,
    CreatedBy       = so.CreatedBy,
    CompanyCode     = so.CompanyCode,
    CustomerOrderNo = so.CustomerOrderNo,
    TaxCode         = so.TaxCode,
    Remarks         = so.Remarks,
    Lines = so.Lines.OrderBy(l => l.LineNo).Select(l => new SalesOrderLineVm
    {
        LineNo       = l.LineNo,
        ProductCode  = l.ProductCode,
        DeliveryDate = l.DeliveryDate,
        OrderQty     = l.OrderQty,
        UomCode      = l.UomCode,
        UnitPrice    = l.UnitPrice,
        CurrencyCode = l.CurrencyCode,
        TaxCode      = l.TaxCode,
        LineAmount   = l.LineAmount,
        TaxAmount    = l.TaxAmount,
        TotalAmount  = l.TotalAmount,
        ShippedQty   = l.ShippedQty,   // 唯讀，顯示用
        IsClosed     = l.IsClosed,
        Remarks      = l.Remarks,
    }).ToList()
};

// 新增時用 ApplyLines（全量建立）
private static void ApplyLines(SalesOrder so, SalesOrderFormVm vm)
{
    int seq = 1;
    foreach (var lv in vm.Lines)
        so.Lines.Add(new SalesOrderLine
        {
            SalesOrderId = so.SalesOrderId,
            LineNo       = seq++,
            ProductCode  = lv.ProductCode,
            DeliveryDate = lv.DeliveryDate,
            OrderQty     = lv.OrderQty,
            UomCode      = lv.UomCode,
            UnitPrice    = lv.UnitPrice,
            CurrencyCode = lv.CurrencyCode,
            TaxCode      = lv.TaxCode,
            LineAmount   = lv.LineAmount,
            TaxAmount    = lv.TaxAmount,
            TotalAmount  = lv.TotalAmount,
            IsClosed     = lv.IsClosed,
            Remarks      = lv.Remarks,
        });
    so.TotalAmount = so.Lines.Sum(l => l.TotalAmount);
}

// 修改時用 UpdateLines（差異更新，保留累計欄位）
// ⚠️ **Critical**：有 ShippedQty / ReceivedQty 的 Line 不可整批刪除，必須差異更新
private static void UpdateLines(SalesOrder so, SalesOrderFormVm vm)
{
    var vmLineKeys = vm.Lines.Select((l, idx) => (Index: idx + 1, l.ProductCode)).ToList();
    var dbLines    = so.Lines.OrderBy(l => l.LineNo).ToList();
    
    // Step 1：刪除前端不再有的行（但須檢查 ShippedQty）
    var toDelete = dbLines
        .Where(db => !vmLineKeys.Any(vm => vm.Index == db.LineNo))
        .ToList();
    foreach (var line in toDelete)
    {
        if (line.ShippedQty > 0)
            throw new InvalidOperationException(
                $"明細行 {line.LineNo} 已出貨 {line.ShippedQty}，無法刪除。");
        _context.SalesOrderLines.Remove(line);
    }
    
    // Step 2：新增或更新前端傳來的行
    int seq = 1;
    foreach (var lv in vm.Lines)
    {
        var existing = dbLines.FirstOrDefault(db => db.LineNo == seq);
        if (existing == null)
        {
            // 新增行
            so.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = so.SalesOrderId,
                LineNo       = seq,
                ProductCode  = lv.ProductCode,
                DeliveryDate = lv.DeliveryDate,
                OrderQty     = lv.OrderQty,
                UomCode      = lv.UomCode,
                UnitPrice    = lv.UnitPrice,
                CurrencyCode = lv.CurrencyCode,
                TaxCode      = lv.TaxCode,
                LineAmount   = lv.LineAmount,
                TaxAmount    = lv.TaxAmount,
                TotalAmount  = lv.TotalAmount,
                IsClosed     = lv.IsClosed,
                Remarks      = lv.Remarks,
                // ShippedQty / ReturnedQty：新行初值為 0（由 EF 自動）
            });
        }
        else
        {
            // 更新行（保留累計欄位）
            existing.ProductCode  = lv.ProductCode;
            existing.DeliveryDate = lv.DeliveryDate;
            existing.OrderQty     = lv.OrderQty;
            existing.UomCode      = lv.UomCode;
            existing.UnitPrice    = lv.UnitPrice;
            existing.CurrencyCode = lv.CurrencyCode;
            existing.TaxCode      = lv.TaxCode;
            existing.LineAmount   = lv.LineAmount;
            existing.TaxAmount    = lv.TaxAmount;
            existing.TotalAmount  = lv.TotalAmount;
            existing.IsClosed     = lv.IsClosed;
            existing.Remarks      = lv.Remarks;
            // ⚠️ ShippedQty / ReturnedQty 不更新（這些只能由出貨單累加）
        }
        seq++;
    }
    
    so.TotalAmount = so.Lines.Sum(l => l.TotalAmount);
}
```

> **明細重建注意**：有累計欄位（ShippedQty / ReceivedQty）的 Line 不可整批刪除重建，
> 需保留原 Line 並只更新可修改欄位，否則累計值會遺失。

### 5.7 簽核 Actions

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Submit(string id)
{
    var so = await _context.SalesOrders.FindAsync(id);
    if (so == null) return NotFound();
    if (so.ApprovalStatus != DocumentApprovalStatus.Draft &&
        so.ApprovalStatus != DocumentApprovalStatus.Rejected)
        return BadRequest(new { success = false, message = "僅草稿或退回狀態可送審" });

    var config = await _approvalService.GetApprovalConfig(DocType, _userContext.PlantId ?? "");
    if (config?.ApprovalTemplateId == null)
    {
        so.ApprovalStatus = DocumentApprovalStatus.Approved;
        await _context.SaveChangesAsync();
        return Ok(new { success = true, message = "已核准（無需簽核）" });
    }

    var req = await _approvalService.SubmitForApproval(
        id, DocType, config.ApprovalTemplateId, _userContext.UserId ?? "");
    so.ApprovalRequestId = req.Id;
    so.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
    await _context.SaveChangesAsync();
    return Ok(new { success = true, message = "已送出審核" });
}

[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Withdraw(string id)
{
    var so = await _context.SalesOrders.FindAsync(id);
    if (so == null) return NotFound();
    if (so.ApprovalStatus != DocumentApprovalStatus.PendingApproval)
        return BadRequest(new { success = false, message = "僅待審核狀態可撤回" });

    if (!await _approvalService.WithdrawRequest(so.ApprovalRequestId!, _userContext.UserId ?? ""))
        return BadRequest(new { success = false, message = "撤回失敗，請確認您是否為申請人" });

    so.ApprovalStatus    = DocumentApprovalStatus.Draft;
    so.ApprovalRequestId = null;
    await _context.SaveChangesAsync();
    return Ok(new { success = true, message = "已撤回" });
}
```

### 5.8 Delete

> Delete 前的簽核狀態防護（PendingApproval 時拒絕刪除）見 **CLAUDE.md「線上簽核」段落**。

ORD/PUR 特有的業務關聯檢查（在簽核防護之後加）：

```csharp
// SalesOrder：有出貨記錄不可刪
if (so.Lines.Any(l => l.ShippedQty > 0))
    return Json(new { success = false, message = "已有出貨記錄，無法刪除。" });

// PurchaseOrder：有進貨記錄不可刪
if (po.Lines.Any(l => l.ReceivedQty > 0))
    return Json(new { success = false, message = "已有進貨記錄，無法刪除。" });

// GoodsShipment：已過帳不可刪
if (doc.Status == DocStatus.Posted)
    return Json(new { success = false, message = "已過帳單據無法刪除，請改用作廢功能。" });
```

### 5.9 雙狀態 Actions（GoodsShipment 類型）

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Confirm(string id)
{
    var doc = await _context.GoodsShipments.Include(d => d.Lines)
        .FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
    if (doc == null) return NotFound();
    if (doc.Status != DocStatus.Draft)
        return BadRequest(new { success = false, message = "僅草稿狀態可確認。" });
    doc.Status = DocStatus.Confirmed;
    await _context.SaveChangesAsync();
    return Ok(new { success = true });
}

[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> UnConfirm(string id)
{
    var doc = await _context.GoodsShipments.Include(d => d.Lines)
        .FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
    if (doc == null) return NotFound();
    if (doc.Status != DocStatus.Confirmed)
        return BadRequest(new { success = false, message = "僅已確認狀態可取消確認。" });
    if (doc.Lines.Any(l => l.QtyPutAway > 0))
        return BadRequest(new { success = false, message = "已有下架作業，無法取消確認。" });
    doc.Status = DocStatus.Draft;
    await _context.SaveChangesAsync();
    return Ok(new { success = true });
}

[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Post(string id)
{
    var doc = await _context.GoodsShipments.Include(d => d.Lines)
        .FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
    if (doc == null) return NotFound();
    if (doc.Status != DocStatus.Confirmed)
        return BadRequest(new { success = false, message = "僅已確認狀態可過帳。" });
    try
    {
        await _inventoryPostingService.PostDocAsync(new PostDocRequest { DocNo = doc.GoodsShipmentId, ... });
        doc.Status = DocStatus.Posted;
        // 累計回寫到來源單據的 Line（見 §4.3）
        foreach (var line in doc.Lines.Where(l => l.SalesOrderId != null))
        {
            var soLine = await _context.SalesOrderLines.FindAsync(line.SalesOrderId, line.SoLineNo);
            if (soLine != null) soLine.ShippedQty += line.ShipQty;
        }
        await _context.SaveChangesAsync();
        return Ok(new { success = true });
    }
    catch (InvalidOperationException ex)
    {
        return BadRequest(new { success = false, message = ex.Message });
    }
}
```

### 5.10 跨文件查詢 Action

```csharp
// 查詢 SalesOrder 中尚未完全出貨的明細（GoodsShipment 建立時選取來源用）
[HttpGet]
public async Task<IActionResult> OpenLines(string id)
{
    var lines = await _context.SalesOrderLines
        .AsNoTracking().Include(l => l.SalesOrder)
        .Where(l => l.SalesOrderId == id
            && l.SalesOrder!.PlantId == _userContext.PlantId
            && l.SalesOrder.ApprovalStatus == DocumentApprovalStatus.Approved
            && !l.IsClosed
            && l.OrderQty > l.ShippedQty)
        .OrderBy(l => l.LineNo)
        .Select(l => new {
            l.LineNo, l.ProductCode, l.OrderQty, l.UomCode,
            l.UnitPrice, l.CurrencyCode, l.TaxCode,
            l.ShippedQty,
            RemainingQty = l.OrderQty - l.ShippedQty,
        })
        .ToListAsync();
    return Json(lines);
}
```

---

## 6. View 慣例

> UOM/Currency 的 step/cdp/Object.assign 寫法見 **new-screen-projectz.md §6-7**（權威來源）。
> 本節只記錄 ORD 特有的結構。

### 6.1 Index.cshtml 三態顯示

```cshtml
@{
    bool hasSearched = ViewBag.Searched == true;
    int  baseDp      = ViewBag.BaseCurrencyDp as int? ?? 2;
    string moneyFmt  = baseDp <= 0 ? "N0" : $"N{baseDp}";

    Func<string, string> sortIcon = col =>
        ViewBag.CurrentSort?.StartsWith(col) == true
            ? (ViewBag.CurrentSort.EndsWith("asc") ? "bi-sort-up" : "bi-sort-down")
            : "bi-arrow-down-up text-muted";
}

@if (!hasSearched)
{
    <div class="alert alert-light border text-muted text-center py-4">
        <i class="bi bi-search fs-4 d-block mb-2"></i>@Localizer["SearchPrompt"]
    </div>
}
else if (Model == null || !Model.Any())
{
    <div class="alert alert-warning text-center">@Localizer["NoResults"]</div>
}
else { /* 表格 + 分頁 */ }
```

### 6.2 狀態 Badge

```cshtml
@{
    var apprBadge = item.ApprovalStatus switch
    {
        DocumentApprovalStatus.Draft           => "secondary",
        DocumentApprovalStatus.PendingApproval => "warning",
        DocumentApprovalStatus.Approved        => "success",
        DocumentApprovalStatus.Rejected        => "danger",
        _ => "light"
    };
    // 雙狀態文件追加：
    var docBadge = item.Status switch
    {
        DocStatus.Draft     => "secondary",
        DocStatus.Confirmed => "info",
        DocStatus.Posted    => "success",
        DocStatus.Void      => "dark",
        _ => "light"
    };
}
<span class="badge bg-@apprBadge">@item.ApprovalStatus</span>
<span class="badge bg-@docBadge">@item.Status</span>
```

### 6.3 _Form.cshtml 骨架

```cshtml
@{
    bool isNew      = string.IsNullOrEmpty(Model?.SalesOrderId);
    bool isReadOnly = ViewBag.IsReadOnly == true;
    bool canEdit    = !isReadOnly && (isNew ||
                        Model?.ApprovalStatus is
                            DocumentApprovalStatus.Draft or
                            DocumentApprovalStatus.Rejected);
    bool canDelete  = ViewBag.CanDelete == true;
}

<form id="entityForm" asp-action="Save" asp-controller="SalesOrder" method="post"
      onsubmit="submitPanel(this, null); return false;">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="SalesOrderId" />
    <input type="hidden" asp-for="CreatedBy" />
    <input type="hidden" asp-for="ApprovalStatus" />

    <fieldset @(!canEdit ? "disabled=\"disabled\"" : "")>
        <div class="p-3">
            <div asp-validation-summary="ModelOnly" class="alert alert-danger py-2 mb-3"
                 style="display:@(ViewContext.ModelState.ErrorCount > 0 ? "block" : "none")"></div>
            <!-- 主檔欄位 / 明細 ... -->
        </div>
    </fieldset>

    <div class="p-3 border-top d-flex gap-2 justify-content-end bg-light">
        @if (isReadOnly)
        {
            <button type="button" class="btn btn-secondary" onclick="closePanel()">
                <i class="bi bi-x-lg"></i> @Localizer["Close"]
            </button>
        }
        else
        {
            @if (canEdit)
            {
                <button type="submit" class="btn btn-primary">
                    <i class="bi bi-check-lg"></i> @Localizer["Save"]
                </button>
            }
            @if (!isNew)
            {
                @if (Model?.ApprovalStatus is
                     DocumentApprovalStatus.Draft or DocumentApprovalStatus.Rejected)
                {
                    <button type="button" class="btn btn-outline-success"
                            onclick="submitForApproval('@Model.SalesOrderId')">
                        <i class="bi bi-send"></i> @Localizer["Submit"]
                    </button>
                }
                @if (Model?.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                {
                    <button type="button" class="btn btn-outline-warning"
                            onclick="withdrawApproval('@Model.SalesOrderId')">
                        <i class="bi bi-arrow-counterclockwise"></i> @Localizer["Withdraw"]
                    </button>
                }
                @if (canDelete && Model?.ApprovalStatus != DocumentApprovalStatus.PendingApproval)
                {
                    <button type="button" class="btn btn-outline-danger"
                            onclick="deleteRecord('@Model.SalesOrderId')">
                        <i class="bi bi-trash"></i> @Localizer["Delete"]
                    </button>
                }
            }
            <button type="button" class="btn btn-secondary" onclick="closePanel()">
                <i class="bi bi-x-lg"></i> @Localizer["Close"]
            </button>
        }
    </div>
</form>
```

### 6.4 明細行 name 屬性慣例

```cshtml
<!-- 明細行的 field name 格式：Lines[{i}].{PropertyName} -->
<input type="hidden"  name="Lines[@i].LineNo"      value="@line.LineNo" />
<input type="hidden"  name="Lines[@i].ProductCode"  value="@line.ProductCode" />
<input type="number"  name="Lines[@i].OrderQty"     class="form-control form-control-sm text-end line-qty" />
<select               name="Lines[@i].UomCode"      class="form-select form-select-sm line-uom" onchange="onUomChange(this)">
<input type="number"  name="Lines[@i].UnitPrice"    class="form-control form-control-sm text-end line-price" />
<select               name="Lines[@i].CurrencyCode" class="form-select form-select-sm line-currency" onchange="onCurrencyChange(this)">
<input type="number"  name="Lines[@i].LineAmount"   class="form-control form-control-sm text-end bg-light line-lineamt" readonly />

<!-- step / min / value 格式化（"F"+cdp）見 new-screen-projectz.md §6-7 -->
```

### 6.5 _Form.cshtml 簽核相關 JS

```html
<script>
    function submitForApproval(id) {
        fetch(`/SalesOrder/Submit`, {
            method: 'POST',
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                'RequestVerificationToken':
                    document.querySelector('[name="__RequestVerificationToken"]').value
            }
        }).then(r => r.json())
          .then(d => { if (d.success) { toastSuccess?.(); refreshList?.(); closePanel?.(); } });
    }

    function withdrawApproval(id) {
        fetch(`/SalesOrder/Withdraw`, { method: 'POST', headers: { /* 同上 */ } })
            .then(r => r.json())
            .then(d => { if (d.success) { toastSuccess?.(); refreshList?.(); } });
    }

    function deleteRecord(id) {
        if (!confirm('確定刪除此筆資料？')) return;
        fetch(`/SalesOrder/Delete`, { method: 'POST', headers: { /* 同上 */ } })
            .then(r => r.json())
            .then(d => {
                if (d.success) { closePanel?.(); refreshList?.(); }
                else alert(d.message);
            });
    }

    function addLine() {
        const tbody = document.getElementById('linesTbody');
        const idx   = tbody.rows.length;
        // 動態插入 tr，field name 用 Lines[idx].XXX
    }

    function deleteLine(btn) {
        btn.closest('tr').remove();
        reindexLines(); // 重新編 Lines[0], Lines[1], ...
    }
</script>
```

---

## 7. 命名慣例

### 7.1 Action 命名對照

| 動作 | Action 名稱 | HTTP | 回傳型別 |
|------|------------|------|---------|
| 列表 | `Index` | GET | `View(PaginatedList<T>?)` |
| 新增表單 | `Create` | GET | `PartialView("_Form")` |
| 編輯表單 | `Edit(string id)` | GET | `PartialView("_Form")` |
| 唯讀詳情 | `Details(string id)` | GET | `PartialView("_Form")` + `IsReadOnly=true` |
| 儲存 | `Save` | POST | `Content("<script>...</script>")` |
| 列印 | `Print(string id)` | GET | `View(entity)` |
| 送審 | `Submit(string id)` | POST | `Ok/BadRequest({ success, message })` |
| 撤回 | `Withdraw(string id)` | POST | `Ok/BadRequest(...)` |
| 刪除 | `Delete(string id)` | POST | `Json({ success, message })` |
| 確認 | `Confirm(string id)` | POST | `Ok/BadRequest(...)` |
| 取消確認 | `UnConfirm(string id)` | POST | `Ok/BadRequest(...)` |
| 過帳 | `Post(string id)` | POST | `Ok/BadRequest(...)` |
| 作廢 | `Void(string id)` | POST | `Ok/BadRequest(...)` |
| 來源明細查詢 | `OpenLines(string id)` | GET | `Json(anonymous[])` |

### 7.2 ViewBag Key 命名

| 用途 | Key 格式 | 型別 |
|------|---------|------|
| 下拉選項 | `{Entity}Options` | `IEnumerable<dynamic>` |
| CodeEntry 選項 | `{類型}Options`（去掉 PZ.）| `IEnumerable<CodeEntryOption>` |
| UOM 精度表 | `UomDecimals` | `IReadOnlyDictionary<string, int>` |
| 幣別精度表 | `CurrencyDecimals` | `IReadOnlyDictionary<string, int>` |
| 本位幣精度 | `BaseCurrencyDp` | `int` |
| 權限旗標 | `Can{Action}` | `bool`（CanCreate/CanUpdate/CanDelete/CanPrint） |
| 查詢條件回填 | 同參數名 | `SearchTerm`, `CompanyCode`, `DateFrom`, `DateTo` |
| 排序 | `{Column}Sort`, `CurrentSort` | `string`（`IdSort`, `DateSort`） |
| 分頁大小 | `CurrentPageSize` | `int` |
| 搜尋旗標 | `Searched` | `bool` |
| 唯讀模式 | `IsReadOnly` | `bool` |
| 使用者名稱對應 | `UserNames` | `Dictionary<string, string>` |

### 7.3 單號格式（DocNumberService）

```
{Prefix}{YY}{MM}-{Seq5}   →   SO2501-00001
```

| Entity | Prefix |
|--------|--------|
| SalesOrder | SO |
| GoodsShipment | GS |
| SalesReturn | GR |
| InternalOrder | NO（出料/退料共用同一單號前綴，以 `TypeCode` NO/NR 區分流向，非各自前綴）|
| PurchaseRequest | PR |
| PurchaseOrder | PO |
| GoodsReceipt | PE |
| GoodsReturn | PF |
| OutsourceReceipt | **OR**（單號 `OR{YYMM}-{Seq5}`，DocNumberConfig DocType=`OutsourceReceipt`；INV 過帳 TypeCode=`OI` 委外入庫）|
| OutsourceRework | RW（Phase F 新建，詳見 pur-domain.md §4.2）|

> ⚠️ **命名空間區分**：OutsourceReceipt 的**單號前綴** `OR` 與 OutsourceMaterialDoc 的 **TypeCode** `OR`（委外退料）字面相同但屬不同維度（單號 vs 庫存異動類別），不會在 DB 衝突；但文件敘述時務必標明，避免讀者誤判。委外四單的權威定義在 **pur-domain.md**。

### 7.4 CodeEntry Category 命名

ProjectZ 自訂 Category 必須加 `PZ.` 前綴：
```
PZ.SalesOrderType       # 銷售訂單類型
PZ.ScrapReason          # 報廢原因
PZ.{模組}{類型}          # 新模組比照（如 PZ.MfgRouteType）
```

---

## 8. 多語資源檔必有 Key

### 8.1 Index.cshtml

```
PageTitle               # 頁面 <title> 與 <h1>
AddEntry                # 新增按鈕文字
Search / SearchPlaceholder
All                     # 下拉「全部」選項
DateFrom / DateTo
First / Previous / Next / Last
TotalRecords / NoResults / SearchPrompt
[所有欄頭名稱]           # 與 th 一一對應
EditEntry               # 編輯按鈕 title（若有）
```

### 8.2 _Form.cshtml

```
[所有欄位標籤]           # 與 Label 一一對應
Select{Entity}          # 下拉「請選擇」提示
LinesTitle / AddLine    # 明細區段
[所有明細欄頭]
Save / Close
Submit / Withdraw       # 簽核按鈕（有簽核時加）
Delete                  # 刪除按鈕（有刪除時加）
```

---

## 9. 整合登錄 SOP（快速版）

> 此為 4 點速查；**含測試層的完整 10 步登錄流程見 §12**（兩者勿重複維護，以 §12 為準）。
> 所有步驟的詳細程式碼見 **new-screen-projectz.md §3-4**。此處只列出登錄點：

1. `Services/ProjectZMenuContributor.cs` — 加入 `MenuItem`（MenuKey 與 Controller `MenuKey` 常數對齊）
2. `Resources/ProjectZSharedResource.zh-TW.resx` + `en-US.resx` — 加入選單文字
3. `Data/ApplicationDbContextExtensions.cs` — 加入 `DbSet` + Fluent API
4. `Views/Approval/_DocDetailsUrls.cshtml` — 加入 `'DocType': id => '/Controller/Details/${id}'`

---

## 10. MFG 模組開發時的對應關係

| ORD/PUR 概念 | MFG 對應 |
|-------------|---------|
| SalesOrder | WorkOrder（工單） |
| SalesOrderLine | WorkOrderOperation（工序）/ WorkOrderMaterial（用料）|
| GoodsShipment（雙狀態）| Dispatch（派工，有 InProgress/Closed 等業務狀態）|
| `Submit` / `Withdraw` | 同名 Action，相同邏輯 |
| `Confirm` / `Post` | `Release`（釋出工單）/ `Start`（開工）|
| Line.`IsClosed` | Dispatch.`Status == Closed` |
| 累計欄位（ShippedQty）| QtyDone（報工累計量）|
| 整批刪除重建 Lines | 同，但 InProgress 後不可刪重建 |
| PrepareFormDropdowns | 加入 WorkCenter、Machine、Routing 等 MFG 主檔下拉 |
| `MenuKey` / `DocType` 常數 | 同模式（如 `"WorkOrder"` → WO2501-00001）|

### 不含金額的 MFG 主檔畫面

Routing、WorkCenter、Machine、WorkCalendar 等：
- 不注入 `ICurrencyService`、不加 `ViewBag.CurrencyDecimals`
- 通常不需 `IApprovalService`（主檔一般無簽核）
- 有數量欄位時注入 `IUomService`，加 `ViewBag.UomDecimals`

### MRP 執行畫面的差異

MrpRun 是「觸發批次作業」而非 CRUD：
- 無 `Create` / `Edit` Action；有 `Run` POST Action
- 狀態：Draft → Executed（無 ApprovalStatus）
- 結果查詢以 `Index` 呈現展開後的 MrpRunLine 清單

### 手機 API 的差異

MFG 手機 API Controller 不繼承一般 MVC 模式：
- 回傳 `ApiResponse<T>` 統一格式（見 mobile-pwa.md §8.2）
- 接受 `clientId` + `clientTimestamp`（離線去重，見 mobile-pwa.md §8.3）
- 路由前綴：`/api/mobile/`，純 JSON，無 Side Panel / PartialView

---

## 11. 自動測試規劃

ORD/PUR 模組的自動化測試涵蓋三層：單元測試（邏輯）、整合測試（DB + API）、E2E UI 測試。
遵循統一規格，詳細寫法見各測試區段。

### 11.1 單元測試（xUnit，純邏輯無 DB）

| 測試對象 | 典型測試案例 |
|---------|------------|
| UOM 轉換 | 同一產品 SalesUom（件）→ StockUom（個），轉換正確 |
| Currency 匯率計算 | Line.TotalAmount = OrderQty × UnitPrice × ExchangeRate，四捨五入對齐 MidpointRounding.AwayFromZero |
| Tax 計算 | LineAmount → TaxAmount → TotalAmount，稅率套用正確 |
| 累計欄位邏輯 | ShippedQty 累加上限 ≤ OrderQty；ReturnedQty 反向扣減 |
| Unique Key 驗證 | 同廠同日期不可重複建單（若設有唯一索引） |

工具：`xUnit` + `FluentAssertions`

### 11.2 整合測試（TestServer + 測試 DB）

#### 資料模型層

| 測試對象 | 驗證點 |
|---------|--------|
| SalesOrder 狀態機 | Draft → PendingApproval → Approved 轉換正確；Rejected 可回 Draft 再修改 |
| GoodsShipment 雙狀態 | ApprovalStatus 與 DocStatus 各自獨立，Post 前必須 Confirmed |
| GoodsReceipt 累計 | Post 後 PurchaseOrderLine.ReceivedQty 正確累加，不重複 |
| 第三層 Cascade Delete | GoodsShipmentLine 刪除 → LocationDetail 自動刪除 |
| PlantId 隔離（多廠） | A 廠使用者查詢不見 B 廠單據 |
| 簽核狀態防護 | PendingApproval 中的單據禁止編輯和刪除（DELETE 回 400） |

#### API 層

| 測試對象 | 驗證點 |
|---------|--------|
| `POST /SalesOrder/Save` | 新增 SalesOrder 成功，ID 自動生成；重複提交回 400 |
| `POST /SalesOrder/Submit` | Draft 狀態送審 → PendingApproval；已簽核單據禁止再送 |
| `POST /GoodsShipment/Confirm` | Draft → Confirmed，QtyPutAway 仍為 0；尋源訂單行自動帶入 |
| `POST /GoodsShipment/Post` | Confirmed → Posted，累計 SalesOrderLine.ShippedQty；回 400 若已 Posted |
| `POST /GoodsShipment/Void` | Posted 可作廢 → Void，反向 InventoryMovement 建立 |
| `POST /GoodsShipment/UnConfirm` | Confirmed 可取消確認（前提 QtyPutAway==0）；有下架作業時回 400 |
| `POST /SalesOrder/Delete` | PendingApproval 狀態禁止刪除（回 `{ success: false, message: "..." }`）|
| `GET /SalesOrder/OpenLines/{id}` | 查詢尚未完全出貨的訂單行 |

工具：`Microsoft.AspNetCore.Mvc.Testing` + `Testcontainers.PostgreSql`

#### 業務流程層

| 流程 | 驗證步驟 |
|------|---------|
| 完整銷售流程 | 建 SO → Approved → 建 GS → Confirmed → Posted → 檢查 SO.ShippedQty 累加 |
| 銷退流程 | 建 GR（來源 GS）→ Posted → GoodsShipmentLine.ReturnedQty 扣減；SalesOrderLine.ReturnedQty 累加 |
| 採購全流程 | PR Draft → Approved → 建 PO → Approved → 建 PE（進貨）→ Posted → PO.ReceivedQty 累加 |
| 內部訂單 | 建 NO（出料 / 退料）→ Posted → InventoryMovement 流向正確（+ / -） |
| 多幣別計價 | Line 使用不同 Currency，ExchangeRate 套用，TotalAmount（本位幣）累計正確 |

#### 權限與簽核層

| 項目 | 驗證點 |
|------|--------|
| 權限檢查 | 無新增權限者 POST /Save 回 403 |
| 簽核參數 | 超過金額門檻自動 PendingApproval；低於門檻自動 Approved |
| 簽核核准 / 退回 | Core ApprovalService 介接正常；核准後 WorkOrder 可投產；退回後改為 Rejected |
| 撤回簽核 | PendingApproval 可撤回回 Draft |

### 11.3 E2E UI 測試（Playwright）

遵循 [webapp-testing.md](./webapp-testing.md) 規格，以下為 ORD/PUR 特有場景。

#### 銷售訂單

| 場景 | 步驟摘要 |
|------|---------|
| 新建訂單 | Create 表單 → 輸入客戶、日期、行明細 → Save → 檢查列表顯示 Draft |
| 送審與核准 | 開啟 Draft 訂單 → Submit 按鈕 → 跳轉簽核頁 → 核准 → 回列表顯示 Approved |
| 撤回簽核 | Approved 訂單若支援撤回 → Withdraw 按鈕 → 回 Draft |
| 刪除防護 | PendingApproval 訂單 → Delete 按鈕禁用（或點擊回 error toast） |
| 編輯明細 | 開啟 Draft 訂單 → 增加 / 修改 / 刪除行 → Save |
| UOM/Currency 轉換 | 品號選單帶入 SalesUom → 修改為 StockUom → 數量自動換算；幣別改變 ExchangeRate 自動帶入 |

#### 出貨單

| 場景 | 步驟摘要 |
|------|---------|
| 新建出貨 | 選擇來源 SO → 自動帶入待出貨行 → 輸入實際出貨量 → Save（Draft） |
| 確認出貨 | 從 Draft Confirm → 狀態變 Confirmed；取消確認（UnConfirm）無下架時可反向 |
| 過帳出貨 | Confirmed GS Confirm → Post 按鈕 → 變 Posted；檢查 SO.ShippedQty 累加 |
| 作廢出貨 | Posted GS Void 按鈕 → 確認對話框 → 變 Void；檢查 SO.ShippedQty 反向扣減 |
| 分次出貨 | 同一 SO 行分兩次出貨 GS → 兩次都 Post 後 ShippedQty 累計等於 OrderQty |

#### 採購單

| 場景 | 步驟摘要 |
|------|---------|
| 新建採購 | 選擇供應商 → 輸入物料行 → Save（Draft）|
| 自 PR 產生 | PurchaseRequest Approved → 建 PO 時可「尋源 PR 行」自動帶入、金額回寫 |
| 進貨簽核 | 採購單 Approved → 建 GoodsReceipt → 進貨 Confirmed → Post → 檢查 PO.ReceivedQty 累加 |
| 進貨退出 | GoodsReceipt 已 Posted → 建 GoodsReturn → Post → 檢查 PO.ReceivedQty 反向扣減 |

#### 內部訂單

| 場景 | 步驟摘要 |
|------|---------|
| 出料訂單 | TypeCode = "NO"（出料）→ 指定倉、位置 → Post → 庫存出庫 |
| 退料訂單 | TypeCode = "NR"（退料）→ 指定倉、位置 → Post → 庫存進庫 |

#### 多語與設定

| 場景 | 驗證點 |
|------|--------|
| 多語切換 | 登入後切換語系 Cookie → 刷新頁面 UI 文字變更（表單欄位標籤、按鈕、表頭） |
| 代碼維護下拉 | 如 SalesOrderType、ScrapReason 下拉顯示本語言名稱，與 CodeEntry 同步 |
| 廠別過濾 | 切換廠別後重新載入 → 列表僅顯示該廠單據 |

### 11.4 測試資料策略與 Fixture

```csharp
public class OrdPurTestFixture : IAsyncLifetime
{
    private TestcontainersDatabase _postgres;
    public ApplicationDbContext Context { get; private set; }
    
    public async Task InitializeAsync()
    {
        _postgres = await TestcontainersDatabase.Create();
        // 建立測試 DB，執行 migration
        
        Context = new ApplicationDbContext(...);
        
        // 建立最小必要主檔：
        // 1 Company（Customer+Supplier），1 Product，1 Uom，1 Currency，1 Tax
        await SeedMinimalData();
    }
    
    private async Task SeedMinimalData()
    {
        var company = new Company { CompanyCode = "CUS01", IsCustomer = true, ... };
        var product = new Product { ProductCode = "P001", ... };
        var uom = new Uom { UomCode = "PC", ... };
        Context.Add(company);
        Context.Add(product);
        Context.Add(uom);
        await Context.SaveChangesAsync();
    }
    
    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}

// 使用
[Collection("OrdPur Integration Tests")]
public class SalesOrderIntegrationTests : IAsyncLifetime
{
    private OrdPurTestFixture _fixture;
    
    public async Task InitializeAsync() => _fixture = new OrdPurTestFixture();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();
    
    [Fact]
    public async Task SaveSalesOrder_NewOrder_Success()
    {
        var controller = new SalesOrderController(_fixture.Context, ...);
        var vm = new SalesOrderFormVm { ... };
        var result = await controller.Save(vm);
        // assert
    }
}
```

### 11.5 測試運行配置

#### 本地開發

```bash
# 執行所有測試
dotnet test Modules/ORD.Tests/Modules.ORD.Tests.csproj

# 執行單一分類
dotnet test --filter "Category=Integration"

# Watch 模式
dotnet watch test
```

#### CI 管線（GitHub Actions）

```yaml
name: ORD/PUR Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    
    services:
      postgres:
        image: postgres:15
        env:
          POSTGRES_PASSWORD: testpass
        options: >-
          --health-cmd pg_isready
          --health-interval 10s
    
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      - run: dotnet test --logger trx --collect:"XPlat Code Coverage"
      - uses: actions/upload-artifact@v3
        with:
          name: coverage
          path: '**/coverage.cobertura.xml'
```

#### 測試覆蓋率目標

| 層級 | 目標 |
|------|------|
| 單元測試 | ≥ 80%（邏輯密集型 Service） |
| 整合測試 | ≥ 70%（API Endpoint） |
| E2E UI 測試 | 覆蓋「完整流程」每個 Entity（GS → SO → 出貨全流程等） |

---

## 12. 整合登錄完整 SOP（含測試層）

1. **Entity 與 DbContext**：`Data/ApplicationDbContextExtensions.cs` 加入 `DbSet` + Fluent API
2. **Entity 新增後首先新增測試類**：`Modules/ORD.Tests/` 建 `{Entity}Tests.cs`
3. **Controller**：`Modules/ORD/Controllers/{Entity}Controller.cs`，注入 Services
4. **Service**（若複雜業務）：`Modules/ORD/Services/` 抽出純邏輯
5. **ViewModel**：`Modules/ORD/ViewModels/{Entity}FormVm.cs`
6. **View**：`Modules/ORD/Views/{Entity}/Index.cshtml` + `_Form.cshtml`
7. **多語資源**：`Resources/Modules/ORD/Views/{Entity}/` 四個 .resx
8. **菜單**：`Services/ProjectZMenuContributor.cs` + `ProjectZSharedResource.zh-TW/en-US.resx`
9. **簽核 / 待審詳情**：`Views/Approval/_DocDetailsUrls.cshtml` 加映射
10. **最後：測試驗收**
    - 單元測試通過（覆蓋率 ≥ 80%）
    - 整合測試通過（含權限、簽核、多廠隔離）
    - E2E 冒煙測試通過（主要流程可用）

---

## 13. 並發控制規範（樂觀鎖，PostgreSQL xmin）

ORD/PUR 為多人作業的交易單據，需防止「Lost Update」（後儲存覆蓋前儲存）。

> ⚠️ **PostgreSQL 不可用 SQL-Server 式 `[Timestamp] byte[] RowVersion`**——該欄位在 PG 上不會自動遞增，樂觀鎖形同失效（專案早期 INV 曾如此，後以 `20260423074321_INV_InventoryUseXminConcurrency` 改正）。
> 一律使用 **PostgreSQL 系統欄位 `xmin`**（每張表自動存在，每次 UPDATE 自動變動），透過 Npgsql `UseXminAsConcurrencyToken()` 映射。**不需 migration、不新增實體欄位。**
> 已落地參考：INV `Inventory`（shadow 版）、FIN `BankAccount`（顯式屬性版）。

### 13.1 Entity 層：顯式 xmin 屬性

```csharp
public class SalesOrder : IPlantEntity
{
    public string SalesOrderId { get; set; }
    public string? PlantId { get; set; }
    // ... 其他欄位 ...

    // PostgreSQL 系統欄位 xmin；屬性名必須是 "xmin" 才能被 UseXminAsConcurrencyToken 綁定
    public uint xmin { get; set; }
}
// PurchaseOrder、GoodsShipment、GoodsReceipt 等關鍵單據同樣加入（比照 BAS/FIN BankAccount）
```

### 13.2 Fluent API（`Data/ApplicationDbContextExtensions.cs`）

```csharp
builder.Entity<SalesOrder>().UseXminAsConcurrencyToken();   // 無 DDL，不產生 AddColumn migration
```

### 13.3 Controller 層：傳回原版本 + 捕捉並發異常

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> Save(SalesOrderFormVm vm)
{
    try
    {
        // ... 編輯路徑載入 so 後、SaveChanges 前 ...
        // 以前端帶回的版本作為 OriginalValue，EF 產生 WHERE xmin = @original
        _context.Entry(so).Property(e => e.xmin).OriginalValue = vm.RowVersion;

        await _context.SaveChangesAsync();
        return Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
    }
    catch (DbUpdateConcurrencyException)
    {
        ModelState.AddModelError(string.Empty, "此單據已被他人修改，請重新載入後再試一次。");
        var latest = await _context.SalesOrders.AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.SalesOrderId == vm.SalesOrderId);
        await PrepareFormDropdowns();
        return PartialView("_Form", latest == null ? vm : MapToVm(latest));
    }
    catch (InvalidOperationException ex)
    {
        ModelState.AddModelError(string.Empty, ex.Message);
        await PrepareFormDropdowns();
        return PartialView("_Form", vm);
    }
}
```

> 雙狀態文件的 `Confirm/Post/Void` 等獨立 Action 直接載入→改→存於同一 context，xmin 原值即當下 DB 值，無需手動帶回。

### 13.4 ViewModel 與 View

```csharp
public class SalesOrderFormVm
{
    public string SalesOrderId { get; set; }
    public uint RowVersion { get; set; }   // 對應 Entity.xmin（uint，前端以數字字串往返）
    // ... 其他欄位 ...
}
```

```cshtml
<!-- _Form.cshtml：與 SalesOrderId / CreatedBy / ApprovalStatus 並列 -->
<input type="hidden" asp-for="RowVersion" />
```

### 13.5 注意事項

- `xmin` 由 PostgreSQL 管理，開發者**不應手動寫入**（僅讀取、僅設 OriginalValue 供比對）。
- 並發衝突時提示「資料已被他人修改」，重載最新版本供使用者參考；**不自動強制覆蓋**。
- 新增（Create）路徑無需設 OriginalValue（尚無既存列）。
- **多廠隔離（Global Query Filter）** + **xmin 樂觀鎖** 並用，兼顧隔離與並發安全。
