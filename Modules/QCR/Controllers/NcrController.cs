using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.QCR.Controllers;

/// <summary>
/// 不合格品單（NCR）— 對應舊系統 QCR Phase I。
/// 處置：Scrap 自動建報廢單 / Rework 自動建重工工單 / UseAsIs 需簽核 / Return 告知。
/// </summary>
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
public class NcrController : ModuleControllerBase
{
    private const string MenuKey = "Ncr";
    private const string DocType = "NonConformanceReport";

    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IDocNumberService      _docNumber;
    private readonly IApprovalService       _approvalService;
    private readonly IInventoryPostingService _invPosting;
    private readonly IDateTimeService       _dateTimeSvc;
    private readonly Services.IQcrReturnService _returnService;
    private readonly IReworkWorkOrderService _rework;

    public NcrController(
        IStringLocalizer<ProjectZSharedResource> localizer,
        IDocNumberService docNumber,
        IApprovalService approvalService,
        IInventoryPostingService invPosting,
        IDateTimeService dateTimeSvc,
        Services.IQcrReturnService returnService,
        IReworkWorkOrderService rework)
    {
        _rework          = rework;
        _localizer       = localizer;
        _docNumber       = docNumber;
        _approvalService = approvalService;
        _invPosting      = invPosting;
        _dateTimeSvc     = dateTimeSvc;
        _returnService   = returnService;
    }

    // ── Index ─────────────────────────────────────────────────────────────

    public async Task<IActionResult> Index(
        string? searchProduct, string? searchNcrId, string? ncrStatus, string? disposition,
        string sortOrder = "date_desc",
        int pageNumber = 1, int pageSize = 20,
        bool searched = false)
    {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

        await PrepareIndexPermissionsAsync(MenuKey);

        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.SearchNcrId   = searchNcrId;
        ViewBag.NcrStatus     = ncrStatus;
        ViewBag.Disposition   = disposition;
        ViewBag.CurrentSort   = sortOrder;
        ViewBag.CurrentPageSize = pageSize;
        ViewBag.DateSort      = sortOrder == "date_asc" ? "date_desc" : "date_asc";
        ViewBag.ProductOptions = await Db.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View((PaginatedList<NonConformanceReport>?)null);

        var plantId = UserContext.PlantId;
        var q = Db.NonConformanceReports.AsNoTracking()
            .Where(n => n.PlantId == plantId);

        if (!string.IsNullOrWhiteSpace(searchProduct))
            q = q.Where(n => n.ProductCode == searchProduct);
        if (!string.IsNullOrWhiteSpace(searchNcrId))
            q = q.Where(n => n.NcrId.Contains(searchNcrId));
        if (!string.IsNullOrWhiteSpace(ncrStatus))
            q = q.Where(n => n.NcrStatus == ncrStatus);
        if (!string.IsNullOrWhiteSpace(disposition))
            q = q.Where(n => n.Disposition == disposition);

        q = sortOrder switch
        {
            "date_asc"   => q.OrderBy(n => n.CreatedAt),
            "product_asc" => q.OrderBy(n => n.ProductCode).ThenByDescending(n => n.CreatedAt),
            _            => q.OrderByDescending(n => n.CreatedAt)
        };

        return View(await PaginatedList<NonConformanceReport>.CreateAsync(q, pageNumber, pageSize));
    }

    // ── Create ────────────────────────────────────────────────────────────

    public async Task<IActionResult> Create()
    {
        if (!await Permissions.CanCreate(MenuKey)) return Forbid();
        await PrepareFormDropdowns();
        var model = new NonConformanceReport { Disposition = "Scrap" };
        return IsAjax ? PartialView("_Form", model) : View("_Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] NonConformanceReport model)
    {
        if (!await Permissions.CanCreate(MenuKey))
            return JsonFail(_localizer["NoCreatePermission"].Value);

        // CreatedBy/NcrStatus 由 Controller 指派，不在表單內 → 移除以免影響 IsValid
        ModelState.Remove(nameof(NonConformanceReport.CreatedBy));
        ModelState.Remove(nameof(NonConformanceReport.NcrStatus));
        RequireScrapLocation(model);
        if (!ModelState.IsValid)
            return ValidationPartial("_Form", model);

        var plantId = UserContext.PlantId ?? "";
        var userId  = UserContext.UserId  ?? "";

        model.NcrId     = await _docNumber.NextAsync(DocType, plantId, _dateTimeSvc.GetLocalDate());
        model.PlantId   = plantId;
        model.CreatedBy = userId;
        model.NcrStatus = NcrStatus.Open;
        model.ApprovalStatus = DocumentApprovalStatus.Draft;
        model.CreatedAt = model.UpdatedAt = DateTime.UtcNow;

        Db.NonConformanceReports.Add(model);
        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["CreateSuccess"].Value, data: new { ncrId = model.NcrId });
    }

    // ── Edit ──────────────────────────────────────────────────────────────

    public async Task<IActionResult> Edit(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey)) return Forbid();

        var ncr = await Db.NonConformanceReports.AsNoTracking()
            .FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

        if (ncr.NcrStatus != NcrStatus.Open)
            return IsAjax ? JsonFail(_localizer["NcrNotEditable"].Value) : BadRequest();

        await PrepareFormDropdowns();
        return IsAjax ? PartialView("_Form", ncr) : View("_Form", ncr);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, [FromForm] NonConformanceReport model)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        ModelState.Remove(nameof(NonConformanceReport.CreatedBy));
        ModelState.Remove(nameof(NonConformanceReport.NcrStatus));
        RequireScrapLocation(model);
        if (!ModelState.IsValid)
            return ValidationPartial("_Form", model);

        var ncr = await Db.NonConformanceReports.FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (ncr.NcrStatus != NcrStatus.Open) return JsonFail(_localizer["NcrNotEditable"].Value);

        ncr.SourceType      = model.SourceType;
        ncr.SourceId        = model.SourceId;
        ncr.ProductCode     = model.ProductCode;
        ncr.Qty             = model.Qty;
        ncr.UomCode         = model.UomCode;
        ncr.DefectCode      = model.DefectCode;
        ncr.Disposition     = model.Disposition;
        ncr.WhCode          = model.WhCode;
        ncr.LocationCode    = model.LocationCode;
        ncr.LotNo           = model.LotNo;
        ncr.RootCause       = model.RootCause;
        ncr.CorrectiveAction = model.CorrectiveAction;
        ncr.Remarks         = model.Remarks;
        ncr.UpdatedAt       = DateTime.UtcNow;

        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["UpdateSuccess"].Value);
    }

    // ── Details ───────────────────────────────────────────────────────────

    public async Task<IActionResult> Details(string id)
    {
        if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
        var ncr = await Db.NonConformanceReports.AsNoTracking()
            .FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

        await PrepareFormDropdowns();
        ViewBag.IsReadOnly = true;
        return IsAjax ? PartialView("_Form", ncr) : View("_Form", ncr);
    }

    // ── Delete / Void ─────────────────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(string id)
    {
        if (!await Permissions.CanDelete(MenuKey))
            return JsonFail(_localizer["NoDeletePermission"].Value);

        var ncr = await Db.NonConformanceReports.FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null) return JsonFail(_localizer["RecordNotFound"].Value);

        if (ncr.NcrStatus != NcrStatus.Open)
            return JsonFail(_localizer["NcrNotDeletable"].Value);

        Db.NonConformanceReports.Remove(ncr);
        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["DeleteSuccess"].Value);
    }

    // ── 送審（UseAsIs 需要上級核准）───────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var ncr = await Db.NonConformanceReports.FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (ncr.Disposition != "UseAsIs")
            return JsonFail(_localizer["SubmitOnlyForUseAsIs"].Value);
        if (ncr.NcrStatus != NcrStatus.Open)
            return JsonFail(_localizer["NcrNotEditable"].Value);

        var plantId = UserContext.PlantId ?? "";
        var userId  = UserContext.UserId  ?? "";
        var config  = await _approvalService.GetApprovalConfig(DocType, plantId);

        if (config?.ApprovalTemplateId == null)
        {
            // 無簽核範本時直接標記為 Approved（寬鬆模式）
            ncr.NcrStatus      = NcrStatus.Approved;
            ncr.ApprovalStatus = DocumentApprovalStatus.Approved;
            ncr.ApprovedBy     = userId;
            ncr.ApprovedAt     = DateTime.UtcNow;
        }
        else
        {
            var req = await _approvalService.SubmitForApproval(
                ncr.NcrId, DocType, config.ApprovalTemplateId, userId);
            ncr.ApprovalRequestId = req.Id;
            ncr.NcrStatus         = NcrStatus.PendingApproval;
            ncr.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
        }

        ncr.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["SubmitSuccess"].Value);
    }

    /// <summary>
    /// 報廢處置執行時要從指定儲位扣庫存，沒填就執行不了（ExecuteScrapAsync 會擋）。
    /// 存檔當下就擋——原本存得進去、到「執行處置」才失敗，而失敗訊息又被畫面吞掉，看起來像按了沒反應（2026-10-27 回報）。
    /// </summary>
    private void RequireScrapLocation(NonConformanceReport model)
    {
        if (model.Disposition != "Scrap") return;
        if (string.IsNullOrWhiteSpace(model.WhCode))
            ModelState.AddModelError(nameof(NonConformanceReport.WhCode), _localizer["ScrapNeedsLocation"].Value);
        if (string.IsNullOrWhiteSpace(model.LocationCode))
            ModelState.AddModelError(nameof(NonConformanceReport.LocationCode), _localizer["ScrapNeedsLocation"].Value);
    }

    // ── 執行處置 ──────────────────────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var ncr = await Db.NonConformanceReports.FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null) return JsonFail(_localizer["RecordNotFound"].Value);

        var executableStatuses = new[] { NcrStatus.Open, NcrStatus.Approved };
        if (!executableStatuses.Contains(ncr.NcrStatus))
            return JsonFail(_localizer["NcrNotExecutable"].Value);

        if (ncr.Disposition == "UseAsIs" && ncr.NcrStatus != NcrStatus.Approved)
            return JsonFail(_localizer["UseAsIsRequiresApproval"].Value);

        var plantId = UserContext.PlantId ?? "";
        var userId  = UserContext.UserId  ?? "";
        string? resultMsg = null;

        try
        {
            // 處置執行（Scrap/Rework 內含庫存過帳，過帳路徑又含日記帳自身 SaveChanges）與
            // NCR 狀態更新須為單一原子單元；交易放動作層，helper 內的 SaveChanges 會加入此環境交易。
            await using var tx = await Db.Database.BeginTransactionAsync();
            switch (ncr.Disposition)
            {
                case "Scrap":
                    resultMsg = await ExecuteScrapAsync(ncr, plantId, userId);
                    break;
                case "Rework":
                    resultMsg = await ExecuteReworkAsync(ncr, plantId, userId);
                    break;
                case "Return":
                    // Return：自 IQC 進料來源自動建立採購退貨草稿單（金額/庫存沖銷由採購確認過帳）
                    var returnId = await _returnService.CreateDraftReturnFromNcrAsync(ncr, plantId, userId);
                    resultMsg = string.Format(_localizer["ReturnDraftCreated"].Value, returnId);
                    break;
                case "ReturnRework":
                    // ReturnRework：委外 IQC 不合格退件重工，自動建立委外重工單草稿（pur-domain.md §4.2 觸發時機）
                    resultMsg = await ExecuteReturnReworkAsync(ncr, plantId, userId);
                    break;
                case "UseAsIs":
                    // UseAsIs：標記執行完成（讓步接受，另經簽核）
                    resultMsg = _localizer["UseAsIsExecuted"].Value;
                    break;
            }

            ncr.NcrStatus    = NcrStatus.Executed;
            ncr.ExecutedBy   = userId;
            ncr.ExecutedAt   = DateTime.UtcNow;
            ncr.UpdatedAt    = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            return JsonFail($"{_localizer["ExecuteFailed"].Value}：{ex.Message}");
        }

        return JsonOk(message: resultMsg ?? _localizer["ExecuteSuccess"].Value);
    }

    // ── 結案 ──────────────────────────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(string id)
    {
        if (!await Permissions.CanUpdate(MenuKey))
            return JsonFail(_localizer["NoUpdatePermission"].Value);

        var ncr = await Db.NonConformanceReports.FirstOrDefaultAsync(n => n.NcrId == id);
        if (ncr == null) return JsonFail(_localizer["RecordNotFound"].Value);
        if (ncr.NcrStatus != NcrStatus.Executed)
            return JsonFail(_localizer["CloseRequiresExecuted"].Value);

        ncr.NcrStatus  = NcrStatus.Closed;
        ncr.ClosedAt   = DateTime.UtcNow;
        ncr.UpdatedAt  = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        return JsonOk(message: _localizer["CloseSuccess"].Value);
    }

    // ── 私有：Scrap 執行 ─────────────────────────────────────────────────

    private async Task<string> ExecuteScrapAsync(
        NonConformanceReport ncr, string plantId, string userId)
    {
        if (string.IsNullOrWhiteSpace(ncr.LocationCode) || string.IsNullOrWhiteSpace(ncr.WhCode))
            throw new InvalidOperationException(_localizer["ScrapNeedsLocation"].Value);

        var scrapId = await _docNumber.NextAsync("ScrapDoc", plantId,
            _dateTimeSvc.GetLocalDate());

        var scrap = new ScrapDoc
        {
            ScrapDocId     = scrapId,
            ScrapDate      = _dateTimeSvc.GetLocalDate(),
            TypeCode       = "IJ",
            TypeCategory   = "Scrap",
            CreatedBy      = userId,
            PlantId        = plantId,
            // 報廢原因是使用者維護的代碼（PZ.ScrapReason，沒有系統種子）；原本寫死 "NCR"，下拉參照檢查啟用後
            // 整張報廢單被擋、NCR 報廢處置直接失敗（2026-10-16）。來源改記在備註。
            Remarks        = $"NCR {ncr.NcrId}",
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };

        scrap.Lines.Add(new ScrapDocLine
        {
            ScrapDocId  = scrapId,
            LineNo      = 1,
            ProductCode = ncr.ProductCode,
            Qty         = ncr.Qty,
            StockQty    = ncr.Qty,
            UomCode     = ncr.UomCode,
            StockUomCode = ncr.UomCode,
            WhCode      = ncr.WhCode,
            LocationCode = ncr.LocationCode,
            SourceDocId  = ncr.NcrId,
        });

        Db.ScrapDocs.Add(scrap);
        await Db.SaveChangesAsync();

        // 過帳至庫存（IJ 方向 -1，扣減在手量）
        await _invPosting.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
        {
            PlantId      = plantId,
            DocNo        = scrapId,
            MovementDate = _dateTimeSvc.GetLocalDate(),
            TypeCode     = "IJ",
            TypeCategory = "Scrap",
            CreatedBy    = userId,
            // 已過帳＝Posted。原本設成 Confirmed（待倉庫作業）：庫存與分錄都已入帳，報廢單卻停在「已確認」，
            // 作廢要求 Posted 而永遠作廢不了、取消確認又因已上架量擋下——NCR 開出的報廢單沒有任何回沖途徑（2026-10-15）。
            SetDocPosted = () => scrap.Status = DocStatus.Posted,
            Lines = scrap.Lines.Select(l => new SimplifiedPostLine
            {
                LineNo      = l.LineNo,
                ProductCode = l.ProductCode,
                LocationCode = l.LocationCode!,
                WhCode      = l.WhCode!,
                LotNo       = ncr.LotNo ?? "",
                Qty         = l.StockQty,
                UomCode     = l.UomCode ?? "",
                StockUomCode = l.StockUomCode ?? "",
                SourceDocId  = l.SourceDocId,
                SetQtyPutAway = q => l.QtyPutAway = q,
            }).ToList()
        });

        ncr.ScrapDocId = scrapId;
        await Db.SaveChangesAsync();

        return string.Format(_localizer["ScrapExecuted"].Value, scrapId);
    }

    // ── 私有：Rework 執行 ────────────────────────────────────────────────

    private async Task<string> ExecuteReworkAsync(
        NonConformanceReport ncr, string plantId, string userId)
    {
        // 找原始工單（從 InspectionOrder 或直接 SourceId=WorkOrderId）
        string? origWoId = null;

        if (ncr.SourceType == "InspectionOrder" && !string.IsNullOrWhiteSpace(ncr.SourceId))
        {
            var order = await Db.InspectionOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.InspectionOrderId == ncr.SourceId);
            if (order?.SourceDocId != null)
            {
                // IPQC: SourceDocId = DispatchId → 找 WorkOrderId
                var dispatch = await Db.Dispatches.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DispatchId == order.SourceDocId);
                origWoId = dispatch?.WorkOrderId;
            }
        }
        else if (ncr.SourceType == "ProductionReport" && !string.IsNullOrWhiteSpace(ncr.SourceId))
        {
            var report = await Db.ProductionReports.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ReportId == ncr.SourceId);
            origWoId = report?.WorkOrderId;
        }

        if (string.IsNullOrWhiteSpace(origWoId))
        {
            // 無原始工單：僅標記，重工工單由使用者手工建立
            return _localizer["ReworkAdvisory"].Value;
        }

        var orig = await Db.WorkOrders.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkOrderId == origWoId);
        if (orig == null) return _localizer["ReworkAdvisory"].Value;

        var rwId = await _docNumber.NextAsync("WorkOrder", plantId,
            _dateTimeSvc.GetLocalDate());

        // 與工單列表「建立重工」同一份：複製原工單工序與用料（依不良數量等比換算）。
        // 原本只建表頭，重工工單沒有工序排不出派工、沒有用料發不了料。
        // NCR 記了倉庫／儲位＝不良品在庫：把它本身列為重工用料，投產時一起發出（§16.4b，否則完工多出一份良品）。
        await _rework.AddAsync(orig, rwId, ncr.Qty, ncr.ProductCode, DateTime.UtcNow.AddDays(3),
            includeDefective: !string.IsNullOrWhiteSpace(ncr.WhCode) && !string.IsNullOrWhiteSpace(ncr.LocationCode));
        ncr.ReworkWorkOrderId = rwId;
        await Db.SaveChangesAsync();

        return string.Format(_localizer["ReworkExecuted"].Value, rwId);
    }

    // ── 私有：ReturnRework 執行（退件重工 → 委外重工單草稿）──────────────

    private async Task<string> ExecuteReturnReworkAsync(
        NonConformanceReport ncr, string plantId, string userId)
    {
        if (ncr.Qty <= 0)
            throw new InvalidOperationException(_localizer["ReturnReworkNeedsQty"].Value);

        // 原始委外派工：IQC/IPQC 來源為 InspectionOrder 時，其 SourceDocId 若為委外派工則帶入
        string? origDispatchId = null;
        if (ncr.SourceType == "InspectionOrder" && !string.IsNullOrWhiteSpace(ncr.SourceId))
        {
            var order = await Db.InspectionOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.InspectionOrderId == ncr.SourceId);
            if (!string.IsNullOrWhiteSpace(order?.SourceDocId))
            {
                var isOutsourceDispatch = await Db.Dispatches.AsNoTracking()
                    .AnyAsync(d => d.DispatchId == order.SourceDocId && d.IsOutsource);
                if (isOutsourceDispatch) origDispatchId = order.SourceDocId;
            }
        }

        var rwId = await _docNumber.NextAsync("OutsourceRework", plantId,
            _dateTimeSvc.GetLocalDate());

        var reason = ncr.RootCause ?? ncr.DefectCode;
        if (reason?.Length > 500) reason = reason[..500];

        // 定責預設廠商責任（委外加工不良），成本基額待採購補登後由儲存重算
        Db.OutsourceReworks.Add(new Modules.PUR.Models.OutsourceRework
        {
            OutsourceReworkId  = rwId,
            SourceNcrId        = ncr.NcrId,
            OriginalDispatchId = origDispatchId,
            ReworkQty          = ncr.Qty,
            ReworkReason       = reason,
            LiabilityParty     = Modules.PUR.Models.ReworkLiabilityParty.Vendor,
            VendorLiablePct    = 100m,
            Status             = Modules.PUR.Models.OutsourceReworkStatus.Draft,
            PlantId            = plantId,
            CreatedBy          = userId,
        });
        ncr.OutsourceReworkId = rwId;
        await Db.SaveChangesAsync();

        return string.Format(_localizer["ReturnReworkExecuted"].Value, rwId);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private async Task PrepareFormDropdowns()
    {
        var plantId = UserContext.PlantId;
        ViewBag.DefectCodeOptions = await Db.DefectCodes.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.Code)
            .Select(d => new { d.Code, d.Name })
            .ToListAsync();

        ViewBag.WarehouseOptions = await Db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.WhCode)
            .Select(w => new { w.WhCode, w.WhName })
            .ToListAsync();

        await PrepareIndexPermissionsAsync(MenuKey);
    }

    // GET: /Ncr/Print/{id}（2026-10-27：不合格品報告紙本）
    [HttpGet]
    public async Task<IActionResult> Print(string id)
    {
        if (!await Permissions.CanPrint(MenuKey)) return Forbid();
        var doc = await Db.NonConformanceReports.AsNoTracking().FirstOrDefaultAsync(x => x.NcrId == id);
        if (doc == null) return NotFound();
        await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!,
            new[] { doc.CreatedBy, doc.ApprovedBy, doc.ExecutedBy });
        await PreparePrintSignAsync(id, doc.CreatedBy);
        var n = await PrintNames.ForAsync(doc);
        var f = await PrintFormatsAsync();
        var L = PrintL;
        var t = ScreenText("QCR", "Ncr");
        var defectName = string.IsNullOrEmpty(doc.DefectCode) ? null
            : await Db.DefectCodes.AsNoTracking().Where(d => d.Code == doc.DefectCode).Select(d => d.Name).FirstOrDefaultAsync();
        var linked = string.Join(", ", new[] { doc.ScrapDocId, doc.ReworkWorkOrderId, doc.GoodsReturnId, doc.OutsourceReworkId }
            .Where(x => !string.IsNullOrEmpty(x)));
        var p = new PrintDocument
        {
            Title = L["Print_Title_Ncr"], DocNo = doc.NcrId,
            DateLabel = L["Print_CreatedAt"], DateValue = PrintTime(doc.CreatedAt),
        }
            .Field(L["Print_SourceDoc"], $"{t[doc.SourceType]} {doc.SourceId}".Trim())
            .Field(L["Print_Product"], $"{doc.ProductCode} {n.Product(doc.ProductCode)}".Trim())
            .Field(L["Print_Qty"], $"{f.Qty(doc.Qty, doc.UomCode)} {n.UomWith(doc.UomCode)}".Trim())
            .Field(L["Print_Location"], n.PlaceWith(doc.WhCode, doc.LocationCode))
            .Field(L["Print_LotNo"], doc.LotNo)
            .Field(L["Print_DefectCode"], string.IsNullOrEmpty(defectName) ? doc.DefectCode : $"{doc.DefectCode} {defectName}")
            .Field(L["Print_RootCause"], doc.RootCause)
            .Field(L["Print_CorrectiveAction"], doc.CorrectiveAction)
            .Field(L["Print_Disposition"], t[doc.Disposition])
            .Field(L["Print_LinkedDocs"], linked)
            .Field(L["Print_Status"], t[doc.NcrStatus])
            .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
            .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
            .Field(L["Print_ApprovedBy"], PrintUser(doc.ApprovedBy))
            .Field(L["Print_ExecutedBy"], PrintUser(doc.ExecutedBy))
            .Field(L["Print_Remarks"], doc.Remarks);
        return PrintDocumentView(p);
    }
}
