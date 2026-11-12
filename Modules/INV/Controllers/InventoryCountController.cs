using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.INV.Controllers;

/// <summary>
/// 盤點清冊 — 建立、初盤輸入、覆盤（主管抽驗）、結帳（差異過帳 StockAdjustment）。
/// 對應舊系統 IC01（清冊）+ ICC01（實盤）。
/// </summary>
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
public class InventoryCountController : ModuleControllerBase
{
    private const string MenuKey = "InventoryCount";
    private const string DocType = "InventoryCountSheet";

    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IDocNumberService       _docNumber;
    private readonly IInventoryPostingService _invPosting;
    private readonly IDateTimeService        _dateSvc;
    private readonly IExportService          _exportSvc;
    private readonly Modules.INV.Services.IInventoryCostLayerService _costLayers;

    public InventoryCountController(
        IStringLocalizer<ProjectZSharedResource> localizer,
        IDocNumberService docNumber,
        IInventoryPostingService invPosting,
        IDateTimeService dateSvc,
        IExportService exportSvc,
        Modules.INV.Services.IInventoryCostLayerService costLayers)
    {
        _localizer  = localizer;
        _docNumber  = docNumber;
        _invPosting = invPosting;
        _dateSvc    = dateSvc;
        _exportSvc  = exportSvc;
        _costLayers = costLayers;
    }

    // ── Index ─────────────────────────────────────────────────────────────

    public async Task<IActionResult> Index(
        string? sheetStatus, string? countMode,
        string? fromDate, string? toDate,
        string sortOrder = "date_desc",
        int pageNumber = 1, int pageSize = 20,
        bool searched = false)
    {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

        await PrepareIndexPermissionsAsync(MenuKey);

        // Index.cshtml 以 ViewBag.Searched 決定要顯示「請先查詢」提示還是資料表；
        // 漏設會讓它恆為 false，列表即使查詢過也永遠只顯示提示、看不到任何盤點單。
        ViewBag.Searched     = searched;
        ViewBag.SheetStatus  = sheetStatus;
        ViewBag.CountMode    = countMode;
        ViewBag.FromDate     = fromDate;
        ViewBag.ToDate       = toDate;
        ViewBag.CurrentSort  = sortOrder;
        ViewBag.CurrentPageSize = pageSize;
        ViewBag.DateSort     = sortOrder == "date_asc" ? "date_desc" : "date_asc";

        if (!searched)
            return View((PaginatedList<InventoryCountSheet>?)null);

        var plantId = UserContext.PlantId;
        var q = Db.InventoryCountSheets.AsNoTracking()
            .Where(s => s.PlantId == plantId);

        if (!string.IsNullOrWhiteSpace(sheetStatus))
            q = q.Where(s => s.SheetStatus == sheetStatus);
        if (!string.IsNullOrWhiteSpace(countMode))
            q = q.Where(s => s.CountMode == countMode);
        if (DateTime.TryParse(fromDate, out var from))
            q = q.Where(s => s.CreatedAt >= from);
        if (DateTime.TryParse(toDate, out var to))
            q = q.Where(s => s.CreatedAt <= to.AddDays(1));

        q = sortOrder switch
        {
            "date_asc" => q.OrderBy(s => s.CreatedAt),
            _          => q.OrderByDescending(s => s.CreatedAt)
        };

        var list = await PaginatedList<InventoryCountSheet>.CreateAsync(q, pageNumber, pageSize);

        var userIds = list.Select(s => s.CreatedBy).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        ViewBag.UserNames = await Db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

        return View(list);
    }

    // ── Create ────────────────────────────────────────────────────────────

    public async Task<IActionResult> Create()
    {
        if (!await Permissions.CanCreate(MenuKey)) return Forbid();
        await PrepareFormDropdowns();
        return IsAjax
            ? PartialView("_Form", new InventoryCountSheet())
            : View("_Form", new InventoryCountSheet());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] InventoryCountSheet model)
    {
        if (!await Permissions.CanCreate(MenuKey))
            return JsonFail(_localizer["NoCreatePermission"].Value);
        // CreatedBy/SheetStatus 由 Controller 指派，不在表單內 → 移除以免影響 IsValid
        ModelState.Remove(nameof(InventoryCountSheet.CreatedBy));
        ModelState.Remove(nameof(InventoryCountSheet.SheetStatus));
        if (!ModelState.IsValid)
            return ValidationPartial("_Form", model);

        var plantId = UserContext.PlantId ?? "";
        var userId  = UserContext.UserId  ?? "";

        // 採番的業務日決定單號的年月段，須用廠別當地日（DateTime.Today 是伺服器時區）。
        model.SheetId    = await _docNumber.NextAsync(DocType, plantId, _dateSvc.GetLocalDate());
        model.PlantId    = plantId;
        model.CreatedBy  = userId;
        model.SheetStatus = "Counting";
        model.SnapshotAt = DateTime.UtcNow;
        model.CreatedAt  = model.UpdatedAt = DateTime.UtcNow;

        // WithSheet：依範圍自動產生明細
        if (model.CountMode == "WithSheet")
            await GenerateItemsInternal(model, plantId);

        Db.InventoryCountSheets.Add(model);
        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["CreateSuccess"].Value,
            data: new { sheetId = model.SheetId });
    }

    // ── Edit ──────────────────────────────────────────────────────────────

    public async Task<IActionResult> Edit(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
        var sheet = await Db.InventoryCountSheets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();
        if (sheet.SheetStatus != "Counting")
            return IsAjax ? JsonFail(_localizer["SheetNotEditable"].Value) : BadRequest();

        await PrepareFormDropdowns();
        // _Form 的「結帳／刪除」鈕綁在 ViewBag.CanUpdate／CanDelete 上，而這兩個旗標
        // 只有 PrepareIndexPermissionsAsync 會設。Edit 與 Details 都沒呼叫，
        // 於是兩個旗標恆為 null、canClose 恆為 false——**結帳鈕從來沒有出現過**，
        // 盤點在畫面上走不完（伺服端的 Close 完全正常，只是按不到）。
        // 2026-09-05 人工驗收 4-7 實測到。
        await PrepareIndexPermissionsAsync(MenuKey);
        return IsAjax ? PartialView("_Form", sheet) : View("_Form", sheet);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, [FromForm] InventoryCountSheet model)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);
        ModelState.Remove(nameof(InventoryCountSheet.CreatedBy));
        ModelState.Remove(nameof(InventoryCountSheet.SheetStatus));
        if (!ModelState.IsValid)
            return ValidationPartial("_Form", model);

        var sheet = await Db.InventoryCountSheets.FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (sheet.SheetStatus != "Counting") return JsonFail(_localizer["SheetNotEditable"].Value);

        sheet.CountMode    = model.CountMode;
        sheet.WhCodeFrom   = model.WhCodeFrom;
        sheet.WhCodeTo     = model.WhCodeTo;
        sheet.LocCodeFrom  = model.LocCodeFrom;
        sheet.LocCodeTo    = model.LocCodeTo;
        sheet.ProdCodeFrom = model.ProdCodeFrom;
        sheet.ProdCodeTo   = model.ProdCodeTo;
        sheet.Remarks      = model.Remarks;
        sheet.UpdatedAt    = DateTime.UtcNow;

        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["UpdateSuccess"].Value);
    }

    // ── Details（唯讀，開 side panel 時用）───────────────────────────────

    public async Task<IActionResult> Details(string id)
    {
        if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
        var sheet = await Db.InventoryCountSheets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

        await PrepareFormDropdowns();
        await PrepareIndexPermissionsAsync(MenuKey);   // 同 Edit：唯讀面板的「編輯」鈕也綁在 CanUpdate 上
        ViewBag.IsReadOnly = true;
        return IsAjax ? PartialView("_Form", sheet) : View("_Form", sheet);
    }

    // ── Delete（只允許 Counting 且無明細）────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(string id)
    {
        if (!await Permissions.CanDelete(MenuKey))
            return JsonFail(_localizer["NoDeletePermission"].Value);

        var sheet = await Db.InventoryCountSheets
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (sheet.SheetStatus != "Counting")
            return JsonFail(_localizer["SheetNotDeletable"].Value);
        if (sheet.Items.Any(i => i.ItemStatus != "Pending"))
            return JsonFail(_localizer["SheetHasCountedItems"].Value);

        Db.InventoryCountSheets.Remove(sheet);
        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["DeleteSuccess"].Value);
    }

    // ── GenerateItems（重新快照，僅允許 Counting + 無任何已初盤明細）────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateItems(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var sheet = await Db.InventoryCountSheets
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (sheet.SheetStatus != "Counting") return JsonFail(_localizer["SheetNotEditable"].Value);
        if (sheet.Items.Any(i => i.ItemStatus != "Pending"))
            return JsonFail(_localizer["SheetHasCountedItems"].Value);

        // 清除現有 Pending 明細，重新快照
        Db.InventoryCountItems.RemoveRange(sheet.Items);
        sheet.Items.Clear();
        sheet.SnapshotAt = DateTime.UtcNow;

        var plantId = UserContext.PlantId ?? "";
        await GenerateItemsInternal(sheet, plantId);

        sheet.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        return JsonOk(message: string.Format(_localizer["GenerateItemsSuccess"].Value, sheet.Items.Count),
            data: new { count = sheet.Items.Count });
    }

    // ── 盤點明細清單（Index 下鑽） ────────────────────────────────────────

    public async Task<IActionResult> CountItems(
        string id, string? filterStatus,
        string? filterProduct, bool forRecount = false,
        int pageNumber = 1, int pageSize = 20)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();
        await PrepareIndexPermissionsAsync(MenuKey);

        var sheet = await Db.InventoryCountSheets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null) return NotFound();

        ViewBag.Sheet        = sheet;
        ViewBag.ForRecount   = forRecount;
        ViewBag.FilterStatus = filterStatus;
        ViewBag.FilterProduct = filterProduct;
        ViewBag.ProductOptions = await Db.InventoryCountItems
            .AsNoTracking().Where(i => i.SheetId == id)
            .Select(i => i.ProductCode)
            .Distinct().OrderBy(c => c)
            .ToListAsync();

        var q = Db.InventoryCountItems.AsNoTracking()
            .Where(i => i.SheetId == id);

        if (!string.IsNullOrWhiteSpace(filterStatus))
            q = q.Where(i => i.ItemStatus == filterStatus);
        if (!string.IsNullOrWhiteSpace(filterProduct))
            q = q.Where(i => i.ProductCode == filterProduct);
        if (forRecount)
            q = q.Where(i => i.ItemStatus == "Counted"); // 只顯示待覆盤

        q = q.OrderBy(i => i.SeqNo);
        return View(await PaginatedList<InventoryCountItem>.CreateAsync(q, pageNumber, pageSize));
    }

    // ── 電腦端輸入初盤量 ──────────────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFirstCount(
        int itemId, decimal qty, string? note)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var item = await Db.InventoryCountItems
            .Include(i => i.Sheet)
            .FirstOrDefaultAsync(i => i.ItemId == itemId);
        if (item == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (item.Sheet?.SheetStatus != "Counting")
            return JsonFail(_localizer["SheetNotEditable"].Value);
        // 覆盤之後不得再改初盤：否則狀態被寫回 Counted，覆盤結果（RecountQty／RecountedBy）
        // 還留在列上卻重新出現在待覆盤清單，職責分離的那一道等於白做。
        // 手機端 count/first-count 同一條規則（離線佇列晚送時最容易撞上）。
        if (item.ItemStatus is not ("Pending" or "Counted"))
            return JsonFail(_localizer["FirstCountLockedAfterRecount"].Value);

        item.FirstCountQty  = qty;
        item.FirstCountedBy = UserContext.UserId;
        item.FirstCountedAt = DateTime.UtcNow;
        item.ItemStatus     = "Counted";
        if (!string.IsNullOrWhiteSpace(note)) item.AuditNote = note;

        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["CountSaved"].Value);
    }

    // ── 覆盤（主管抽驗，人員不同）────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRecount(
        int itemId, decimal qty, string? note)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var item = await Db.InventoryCountItems
            .Include(i => i.Sheet)
            .FirstOrDefaultAsync(i => i.ItemId == itemId);
        if (item == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (item.Sheet?.SheetStatus != "Counting")
            return JsonFail(_localizer["SheetNotEditable"].Value);
        if (item.ItemStatus != "Counted")
            return JsonFail(_localizer["RecountRequiresFirstCount"].Value);

        var userId = UserContext.UserId ?? "";
        if (item.FirstCountedBy == userId)
            return JsonFail(_localizer["RecountDifferentUser"].Value);

        item.RecountQty  = qty;
        item.RecountedBy = userId;
        item.RecountedAt = DateTime.UtcNow;
        item.ItemStatus  = "Recounted";
        if (!string.IsNullOrWhiteSpace(note)) item.AuditNote = note;

        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["RecountSaved"].Value);
    }

    // ── 結帳（差異過帳 → StockAdjustment） ───────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var sheet = await Db.InventoryCountSheets
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (sheet == null) return JsonFail(_localizer["RecordNotFound"].Value);
        // 已產出調整單者一律擋，且比狀態守門更早——狀態是可被改回去的（人為改庫、
        // 未來的「重開清冊」），而「這張清冊已經扣過帳」是既成事實。這道守門若放在
        // 採番之後，第二次結帳就會拿到一個新單號，過帳層以「單號＋行號＋類別」為鍵的
        // 冪等檢查便完全失效，同批差異會被扣第二次。
        if (!string.IsNullOrEmpty(sheet.AdjustmentDocId))
            return JsonFail(string.Format(_localizer["SheetAlreadyClosed"].Value, sheet.AdjustmentDocId));
        if (sheet.SheetStatus != "Counting")
            return JsonFail(_localizer["SheetNotEditable"].Value);

        var pendingCount = sheet.Items.Count(i => i.ItemStatus == "Pending");
        if (pendingCount > 0)
            return JsonFail(string.Format(_localizer["CloseHasPending"].Value, pendingCount));

        var plantId = UserContext.PlantId ?? "";
        var userId  = UserContext.UserId  ?? "";
        // 盤盈虧調整單的 AdjustDate 會一路帶進庫存異動與日記帳，須用廠別當地日。
        var today   = _dateSvc.GetLocalDate();

        // 調整單**等到確定有差異才建**。原本是無條件先建再說，於是零差異的清冊
        // （盤點最常見、也最該安靜的結果）每次都留下一張 0 明細的草稿調整單：
        // 白佔一個單號，而且會出現在庫存調整列表裡等著被人誤點確認。
        // 2026-09-05 人工驗收 4-7 實測到。
        var adj = new Modules.INV.Models.StockAdjustment
        {
            AdjustDate     = today,
            TypeCode       = "IC",
            CreatedBy      = userId,
            PlantId        = plantId,
            Remarks        = $"盤點結帳 {sheet.SheetId}",
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };

        int lineNo = 0;
        foreach (var item in sheet.Items.Where(i => i.FinalQty.HasValue))
        {
            var variance = item.FinalQty!.Value - item.BookQty;
            if (variance == 0m) { item.ItemStatus = "Finalized"; continue; }

            lineNo++;
            // 盤盈是入庫，而入庫要有單價。這裡沒有進貨單、沒有供應商，也就沒有原始單價，
            // 但 FIFO 品號的入庫**一定**要帶單價（InventoryPostingService 的守門會擋，
            // 見 W2-F8），於是原本整張清冊只要盤出一筆 FIFO 盤盈就結不了帳，
            // 而且錯誤訊息講的是「入庫未帶單價，無法建立 FIFO 成本層」，
            // 離「盤點」這件事非常遠。2026-09-05 人工驗收 4-7 實測到。
            //
            // 成本基礎取**現存成本層的加權平均**：盤出來的多餘品項就是同一批貨。
            // 完全沒有成本層時回 null（全新品號盤盈，沒有任何基礎可推），
            // 讓過帳層那道守門照常擋下——寧可擋住，也不要建 0 元成本層。
            // MA 品號不需要在這裡帶：過帳層本來就會用該廠現行移動平均，語意正確。
            decimal? inboundUnitCost = null;
            if (variance > 0)
                inboundUnitCost = await _costLayers.GetCurrentUnitCostAsync(
                    plantId, item.ProductCode, item.LocationCode ?? "", item.LotNo ?? "");

            adj.Lines.Add(new Modules.INV.Models.StockAdjustmentLine
            {
                LineNo            = lineNo,
                ProductCode       = item.ProductCode,
                LocationCode      = item.LocationCode,
                WhCode            = item.WhCode,
                Qty               = Math.Abs(variance),
                StockQty          = Math.Abs(variance),
                Direction         = variance > 0 ? 1 : -1,
                UnitPrice         = inboundUnitCost,
            });
            item.ItemStatus = "Finalized";
        }

        // 有差異才配單號、才建單；零差異的清冊 AdjustmentDocId 保持 null。
        string? adjId = null;
        if (adj.Lines.Any())
        {
            adjId = await _docNumber.NextAsync("StockAdjustment", plantId, today);
            adj.StockAdjustmentId = adjId;
            foreach (var l in adj.Lines) l.StockAdjustmentId = adjId;
            Db.StockAdjustments.Add(adj);
        }

        sheet.SheetStatus     = "Closed";
        sheet.ClosedBy        = userId;
        sheet.ClosedAt        = DateTime.UtcNow;
        sheet.UpdatedAt       = DateTime.UtcNow;
        // 與調整單在同一個交易內落地：回沖時兩者一起消失，不會留下「指向不存在單號」的清冊。
        sheet.AdjustmentDocId = adjId;

        // 結帳＋建調整單＋過帳庫存差異為單一原子單元：過帳路徑內含日記帳自身 SaveChanges，
        // 須以顯式交易包住兩次 SaveChanges，避免中途失敗造成「已結帳卻庫存未更新」的不一致。
        await using var tx = await Db.Database.BeginTransactionAsync();
        try
        {
            await Db.SaveChangesAsync();

            // 過帳庫存差異
            if (adj.Lines.Any())
            {
                await _invPosting.PostDocSimplifiedAsync(new Modules.INV.Services.PostDocSimplifiedRequest
                {
                    PlantId      = plantId,
                    DocNo        = adjId!,
                    MovementDate = today,
                    TypeCode     = "IC",
                    // 調整單本身的 TypeCategory 預設就是 Adjustment，但這裡原本沒往下傳，
                    // 於是庫存異動與分錄看到的是 null——盤盈因此被當成進貨、貸進暫估應付。
                    TypeCategory = adj.TypeCategory,
                    CreatedBy    = userId,
                    // 已過帳＝Posted（原本設 Confirmed：庫存已異動，調整單卻停在待倉庫作業、作廢不了；同 NCR 報廢單，2026-10-15）
                    SetDocPosted = () => adj.Status = Modules.INV.Models.DocStatus.Posted,
                    Lines        = adj.Lines.Select(l => new Modules.INV.Services.SimplifiedPostLine
                    {
                        LineNo           = l.LineNo,
                        ProductCode      = l.ProductCode,
                        LocationCode     = l.LocationCode ?? "",
                        WhCode           = l.WhCode ?? "",
                        Qty              = l.StockQty,
                        UnitPrice        = l.UnitPrice,
                        DirectionOverride = l.Direction,
                        SetQtyPutAway    = q => l.QtyPutAway = q,
                    }).ToList()
                });
                await Db.SaveChangesAsync();
            }

            await tx.CommitAsync();
        }
        catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
        catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }

        return JsonOk(message: adjId == null
            ? _localizer["SheetCloseNoVariance"].Value
            : string.Format(_localizer["SheetCloseSuccess"].Value, adjId));
    }

    // ── Excel 匯出明細 ────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Export(string id)
    {
        if (!await Permissions.CanPrint(MenuKey)) return Forbid();
        var items = await Db.InventoryCountItems.AsNoTracking()
            .Where(i => i.SheetId == id)
            .OrderBy(i => i.SeqNo)
            .ToListAsync();

        var now  = _dateSvc.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
        var req  = new ExportRequest
        {
            Title   = isZh ? $"盤點清冊 {id}" : $"Inventory Count {id}",
            Headers = isZh
                ? ["序號", "倉庫", "儲位", "批號", "品號", "帳面量", "初盤量", "覆盤量", "差異", "狀態", "備注"]
                : ["Seq", "WH", "Location", "Lot", "Product", "Book Qty", "1st Count", "Recount", "Variance", "Status", "Note"],
            Rows = items.Select(i => new object?[]
            {
                i.SeqNo, i.WhCode, i.LocationCode, i.LotNo, i.ProductCode,
                i.BookQty, i.FirstCountQty, i.RecountQty, i.Variance, i.ItemStatus, i.AuditNote
            }).ToList()
        };

        return File(_exportSvc.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"InventoryCount_{id}_{now:yyyyMMdd}.xlsx"));
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task GenerateItemsInternal(InventoryCountSheet sheet, string plantId)
    {
        bool hasLocFilter =
            !string.IsNullOrWhiteSpace(sheet.WhCodeFrom)  || !string.IsNullOrWhiteSpace(sheet.WhCodeTo) ||
            !string.IsNullOrWhiteSpace(sheet.LocCodeFrom) || !string.IsNullOrWhiteSpace(sheet.LocCodeTo);

        // 取得儲位→倉庫對照（依範圍篩選）
        var locQuery = Db.StorageLocations.ForCurrentPlant(Db).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(sheet.WhCodeFrom))
            locQuery = locQuery.Where(l => string.Compare(l.WhCode, sheet.WhCodeFrom) >= 0);
        if (!string.IsNullOrWhiteSpace(sheet.WhCodeTo))
            locQuery = locQuery.Where(l => string.Compare(l.WhCode, sheet.WhCodeTo) <= 0);
        if (!string.IsNullOrWhiteSpace(sheet.LocCodeFrom))
            locQuery = locQuery.Where(l => string.Compare(l.LocationCode, sheet.LocCodeFrom) >= 0);
        if (!string.IsNullOrWhiteSpace(sheet.LocCodeTo))
            locQuery = locQuery.Where(l => string.Compare(l.LocationCode, sheet.LocCodeTo) <= 0);

        Dictionary<string, string> locMap = hasLocFilter
            ? await locQuery.ToDictionaryAsync(l => l.LocationCode, l => l.WhCode)
            : new Dictionary<string, string>();

        if (hasLocFilter && locMap.Count == 0) return; // 範圍過濾後無任何儲位

        var invQuery = Db.Inventories.AsNoTracking()
            .Where(i => i.PlantId == plantId && i.QtyOnHand > 0);

        if (hasLocFilter)
        {
            var locCodes = locMap.Keys.ToList();
            invQuery = invQuery.Where(i => locCodes.Contains(i.LocationCode));
        }
        if (!string.IsNullOrWhiteSpace(sheet.ProdCodeFrom))
            invQuery = invQuery.Where(i => string.Compare(i.ProductCode, sheet.ProdCodeFrom) >= 0);
        if (!string.IsNullOrWhiteSpace(sheet.ProdCodeTo))
            invQuery = invQuery.Where(i => string.Compare(i.ProductCode, sheet.ProdCodeTo) <= 0);

        var inventories = await invQuery
            .OrderBy(i => i.LocationCode).ThenBy(i => i.ProductCode)
            .ToListAsync();

        // 無範圍篩選時，補充載入 LocationCode→WhCode
        if (!hasLocFilter)
        {
            var locs = inventories.Select(i => i.LocationCode).Distinct().ToList();
            locMap = await Db.StorageLocations.ForCurrentPlant(Db).AsNoTracking()
                .Where(l => locs.Contains(l.LocationCode))
                .ToDictionaryAsync(l => l.LocationCode, l => l.WhCode);
        }

        int seq = 0;
        foreach (var inv in inventories)
        {
            seq++;
            sheet.Items.Add(new InventoryCountItem
            {
                SheetId      = sheet.SheetId,
                SeqNo        = seq,
                WhCode       = locMap.TryGetValue(inv.LocationCode, out var wh) ? wh : "",
                LocationCode = inv.LocationCode,
                LotNo        = inv.LotNo,
                ProductCode  = inv.ProductCode,
                BookQty      = inv.QtyOnHand,
                ItemStatus   = "Pending",
            });
        }
    }

    private async Task PrepareFormDropdowns()
    {
        ViewBag.WarehouseOptions = await Db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.WhCode)
            .Select(w => new { w.WhCode, w.WhName })
            .ToListAsync();
        ViewBag.LocationOptions = await Db.StorageLocations.ForCurrentPlant(Db).AsNoTracking()
            .Where(l => l.IsActive)
            .OrderBy(l => l.LocationCode)
            .Select(l => new { l.LocationCode, l.LocationName })
            .ToListAsync();
        ViewBag.ProductOptions = await Db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();
        await PrepareIndexPermissionsAsync(MenuKey);
    }

    // GET: /InventoryCount/Print/{id}（2026-10-27：盤點表。不印帳面數，避免盤點人照抄）
    [HttpGet]
    public async Task<IActionResult> Print(string id)
    {
        if (!await Permissions.CanPrint(MenuKey)) return Forbid();
        var doc = await Db.InventoryCountSheets.AsNoTracking().Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.SheetId == id);
        if (doc == null) return NotFound();
        await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!,
            new[] { doc.CreatedBy, doc.ClosedBy });
        await PreparePrintSignAsync(id, doc.CreatedBy);
        var n = await PrintNames.ForAsync(doc, new { FromWhCode = doc.WhCodeFrom, ToWhCode = doc.WhCodeTo });
        var f = await PrintFormatsAsync();
        var L = PrintL;
        var t = ScreenText("INV", "InventoryCount");
        static string? Range(string? a, string? b) =>
            string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b) ? null : $"{a} ～ {b}";
        var p = new PrintDocument
        {
            Title = L["Print_Title_InventoryCount"], DocNo = doc.SheetId,
            DateLabel = L["Print_SnapshotAt"], DateValue = PrintTime(doc.SnapshotAt),
        }
            .Field(L["Print_CountMode"], t[doc.CountMode])
            .Field(L["Print_Status"], t[doc.SheetStatus])
            .Field(L["Print_Warehouse"], Range(n.WarehouseWith(doc.WhCodeFrom), n.WarehouseWith(doc.WhCodeTo)))
            .Field(L["Print_Location"], Range(doc.LocCodeFrom, doc.LocCodeTo))
            .Field(L["Print_ProductCode"], Range(doc.ProdCodeFrom, doc.ProdCodeTo))
            .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
            .Field(L["Print_ClosedBy"], PrintUser(doc.ClosedBy))
            .Field(L["Print_Remarks"], doc.Remarks);
        var tbl = new PrintTable()
            .Col(L["Print_Seq"]).Col(L["Print_Warehouse"]).Col(L["Print_Location"]).Col(L["Print_LotNo"])
            .Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
            .Col(L["Print_FirstCountQty"], true).Col(L["Print_RecountQty"], true).Col(L["Print_Remarks"]);
        foreach (var i in doc.Items.OrderBy(x => x.SeqNo))
            tbl.Row(i.SeqNo.ToString(), n.WarehouseWith(i.WhCode), n.LocationWith(i.LocationCode), i.LotNo,
                i.ProductCode, n.Product(i.ProductCode),
                f.Qty(i.FirstCountQty, null), f.Qty(i.RecountQty, null), i.AuditNote);
        p.Tables.Add(tbl);
        return PrintDocumentView(p);
    }
}
