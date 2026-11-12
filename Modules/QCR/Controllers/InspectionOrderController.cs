using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.QCR.Controllers;

/// <summary>
/// 檢驗單作業。IQC / IPQC / FQC / RQC 記錄統一查詢介面，
/// 並提供桌機端的檢驗登錄：IQC 由待檢進貨明細開單登錄（PendingIqc/SubmitIqc），
/// IPQC / FQC / RQC 待檢單則於明細面板直接提交結果（Submit）。
/// 抽樣、判定與品質隔離放行邏輯一律走 <see cref="IIqcInspectionService"/>，與手機端同源。
/// </summary>
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
public class InspectionOrderController : ModuleControllerBase
{
    private const string MenuKey = "InspectionOrder";
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IExportService _exportService;
    private readonly IDateTimeService _dateTimeService;
    private readonly IIqcInspectionService _iqc;
    private readonly Modules.MFG.Services.IFpiGateService _fpiGate;
    private readonly ISamplingPlanService _samplingPlan;

    public InspectionOrderController(
        IStringLocalizer<ProjectZSharedResource> localizer,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IIqcInspectionService iqc,
        Modules.MFG.Services.IFpiGateService fpiGate,
        ISamplingPlanService samplingPlan)
    {
        _localizer       = localizer;
        _exportService   = exportService;
        _dateTimeService = dateTimeService;
        _iqc             = iqc;
        _fpiGate         = fpiGate;
        _samplingPlan    = samplingPlan;
    }

    public async Task<IActionResult> Index(
        string? inspectionType, string? status,
        string? fromDate, string? toDate, string? searchProduct,
        string? searchSourceDoc = null,
        string sortOrder = "date_desc",
        int pageNumber = 1, int pageSize = 20,
        bool searched = false)
    {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

        await PrepareIndexPermissionsAsync(MenuKey);

        ViewBag.Searched       = searched;
        ViewBag.InspectionType = inspectionType;
        ViewBag.Status         = status;
        ViewBag.FromDate       = fromDate;
        ViewBag.ToDate         = toDate;
        ViewBag.SearchProduct  = searchProduct;
        ViewBag.SearchSourceDoc = searchSourceDoc;
        ViewBag.CurrentSort    = sortOrder;
        ViewBag.CurrentPageSize = pageSize;
        ViewBag.DateSort       = sortOrder == "date_asc" ? "date_desc" : "date_asc";
        ViewBag.ProductSort    = sortOrder == "product_asc" ? "product_desc" : "product_asc";
        ViewBag.ProductOptions = await Db.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();
        // 來源單號（進貨單、工單、派工…）：只列本廠檢驗單出現過的，新的在前
        ViewBag.SourceDocOptions = await Db.InspectionOrders
            .AsNoTracking()
            .Where(o => o.PlantId == UserContext.PlantId && o.SourceDocId != "")
            .GroupBy(o => o.SourceDocId)
            .OrderByDescending(g => g.Max(o => o.CreatedAt))
            .Select(g => g.Key)
            .ToListAsync();

        if (!searched)
            return View((PaginatedList<InspectionOrder>?)null);

        var plantId = UserContext.PlantId;
        var q = Db.InspectionOrders.AsNoTracking()
            .Where(o => o.PlantId == plantId);

        if (!string.IsNullOrWhiteSpace(inspectionType))
            q = q.Where(o => o.InspectionType == inspectionType);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(searchProduct))
            q = q.Where(o => o.ProductCode == searchProduct);
        if (!string.IsNullOrWhiteSpace(searchSourceDoc))
            q = q.Where(o => o.SourceDocId == searchSourceDoc);
        if (DateTime.TryParse(fromDate, out var from))
            q = q.Where(o => o.CreatedAt >= from);
        if (DateTime.TryParse(toDate, out var to))
            q = q.Where(o => o.CreatedAt <= to.AddDays(1));

        q = sortOrder switch
        {
            "date_asc"      => q.OrderBy(o => o.CreatedAt),
            "product_asc"   => q.OrderBy(o => o.ProductCode).ThenByDescending(o => o.CreatedAt),
            "product_desc"  => q.OrderByDescending(o => o.ProductCode).ThenByDescending(o => o.CreatedAt),
            _               => q.OrderByDescending(o => o.CreatedAt)
        };

        var page = await PaginatedList<InspectionOrder>.CreateAsync(q, pageNumber, pageSize);
        ViewBag.UserNames = await BuildUserNamesAsync(page.Select(o => o.InspectorId));
        return View(page);
    }

    public async Task<IActionResult> Details(string id)
    {
        if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
        var order = await Db.InspectionOrders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.InspectionOrderId == id);

        if (order == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

        ViewBag.UserNames = await BuildUserNamesAsync(new[] { order.InspectorId });
        // Pending 的 IPQC/FQC/RQC 待檢單可在面板直接補登結果（現場無手機/PDA 故障時的桌機路徑）
        ViewBag.CanUpdate = await Permissions.CanUpdate(MenuKey);
        return IsAjax ? PartialView("_Details", order) : View("_Details", order);
    }

    // ── IQC 進料檢驗（桌機登錄）────────────────────────────────────────────

    /// <summary>
    /// IQC 待檢清單：Status=Confirmed 且尚未檢驗的進貨明細（已套用抽樣計畫的 VL 切換與跳批），
    /// 與手機 api/mobile/inv/iqc/pending 同源。
    /// </summary>
    public async Task<IActionResult> PendingIqc()
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();
        await PrepareIndexPermissionsAsync(MenuKey);
        var rows = await _iqc.GetPendingLinesAsync(UserContext.PlantId ?? "");
        return View(rows);
    }

    /// <summary>IQC 登錄表單（側面板）。批量與建議抽樣數一律由伺服端重算，不信任前端傳入。</summary>
    public async Task<IActionResult> IqcForm(string goodsReceiptId, int lineNo)
    {
        if (!await Permissions.CanCreate(MenuKey)) return Forbid();

        var rows = await _iqc.GetPendingLinesAsync(UserContext.PlantId ?? "");
        var row  = rows.FirstOrDefault(r => r.GoodsReceiptId == goodsReceiptId && r.LineNo == lineNo);
        if (row == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

        var vm = new IqcSubmitVm
        {
            GoodsReceiptId    = row.GoodsReceiptId,
            LineNo            = row.LineNo,
            ProductCode       = row.ProductCode,
            LotSize           = row.Qty,
            UomCode           = row.UomCode,
            VerificationLevel = row.VerificationLevel,
            SampleQty         = row.RecommendedSampleSize,
            FailQty           = 0
        };
        ViewBag.RecommendedSampleSize = row.RecommendedSampleSize;
        ViewBag.SamplingState         = row.SamplingState;
        return PartialView("_IqcForm", vm);
    }

    /// <summary>
    /// IQC 結果登錄。判定（MIL-STD-1916 Ac=0）、VL 切換回寫與待驗倉放行皆由
    /// <see cref="IIqcInspectionService"/> 處理，與手機端同一條路。
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitIqc(IqcSubmitVm model)
    {
        if (!await Permissions.CanCreate(MenuKey)) return Forbid();

        // Ac=0 之外的基本合理性：不良數不得超過實際抽樣數
        if (model.FailQty > model.SampleQty)
            ModelState.AddModelError(nameof(model.FailQty), _localizer["IqcFailQtyExceedsSample"].Value);

        if (!ModelState.IsValid)
        {
            ViewBag.RecommendedSampleSize = model.SampleQty;
            return PartialView("_IqcForm", model);
        }

        var outcome = await _iqc.SubmitAsync(new IqcSubmitInput(
            model.GoodsReceiptId, model.LineNo, model.ProductCode, model.LotSize,
            model.SampleQty, model.FailQty, model.VerificationLevel, model.Remarks),
            UserContext.PlantId ?? "");

        if (!outcome.Success)
        {
            // ErrorCode → 在地化訊息（R1：Localizer 索引一律靜態字面）
            var message = outcome.ErrorCode switch
            {
                "IQC_ALREADY_DONE" => _localizer["IqcAlreadyDone"].Value,
                "INVALID_QTY"      => _localizer["IqcInvalidSampleQty"].Value,
                _                  => _localizer["IqcMissingReceipt"].Value
            };
            return JsonFail(message);
        }

        return JsonOk(new { outcome.InspectionOrderId, result = outcome.Result }, outcome.Message);
    }

    // ── FPI 首件檢查（桌機登錄；mfg-domain.md §26.2-②）───────────────────────

    /// <summary>
    /// FPI 待檢清單：工序要求首件檢查、且尚無判定 Pass 之 FPI 檢驗單的派工。
    ///
    /// 桌機端原本完全沒有 FPI 路徑（檢驗單無建立入口、Submit 無 Pass→解鎖分支），
    /// 導致純桌機部署的派工一旦進 PendingFPI 就出不來（§26.1 缺口 B）。本清單與
    /// <see cref="FpiForm"/>／<see cref="SubmitFpi"/> 補齊該路徑，與手機 fpi.page.js 同源。
    /// </summary>
    public async Task<IActionResult> PendingFpi()
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();
        await PrepareIndexPermissionsAsync(MenuKey);

        // 未結案且尚未生產完成的派工才需要首件（Closed/OnHold 不列）
        var active = new[]
        {
            DispatchStatus.Pending, DispatchStatus.Scheduled, DispatchStatus.SetupInProgress,
            DispatchStatus.ReadyToStart, DispatchStatus.PendingFPI
        };

        var rows = await (
            from d in Db.Dispatches.AsNoTracking()
            join o in Db.WorkOrderOperations.AsNoTracking()
                on new { d.WorkOrderId, d.OperationSeq } equals new { o.WorkOrderId, o.OperationSeq }
            join w in Db.WorkOrders.AsNoTracking() on d.WorkOrderId equals w.WorkOrderId
            where o.RequiresFpi && active.Contains(d.Status)
               && !Db.InspectionOrders.Any(i => i.SourceDocId == d.DispatchId
                                             && i.Status == "Pass"
                                             && (i.OrderType == InspectionOrderType.FPI || i.InspectionType == "FPI"))
            orderby d.PlannedStartDate, d.DispatchId
            select new FpiPendingRow(
                d.DispatchId, d.WorkOrderId, d.OperationSeq, o.OperationName,
                w.ProductCode, d.Quantity, d.MachineCode, d.Status, d.PlannedStartDate)
        ).ToListAsync();

        return View(rows);
    }

    /// <summary>FPI 登錄表單（側面板）。批量與建議抽樣數一律由伺服端重算，不信任前端傳入。</summary>
    public async Task<IActionResult> FpiForm(string dispatchId)
    {
        if (!await Permissions.CanCreate(MenuKey)) return Forbid();

        var dispatch = await Db.Dispatches.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DispatchId == dispatchId);
        if (dispatch == null)
            return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

        var productCode = await Db.WorkOrders.AsNoTracking()
            .Where(w => w.WorkOrderId == dispatch.WorkOrderId)
            .Select(w => w.ProductCode).FirstOrDefaultAsync() ?? string.Empty;

        var decision = await _samplingPlan.GetDecisionAsync(
            productCode: productCode, supplierCode: null, inspectionType: "FPI",
            lotSize: dispatch.Quantity, plantId: dispatch.PlantId,
            machineCode: dispatch.MachineCode, toolingCode: dispatch.ToolingCode);

        var vm = new FpiSubmitVm
        {
            DispatchId        = dispatch.DispatchId,
            WorkOrderId       = dispatch.WorkOrderId,
            OperationSeq      = dispatch.OperationSeq,
            ProductCode       = productCode,
            LotSize           = dispatch.Quantity,
            VerificationLevel = decision?.VerificationLevel ?? 4,
            SampleQty         = decision?.SampleSize ?? 1,
            FailQty           = 0
        };
        ViewBag.RecommendedSampleSize = vm.SampleQty;
        ViewBag.SamplingState         = decision?.SamplingState ?? "";
        return PartialView("_FpiForm", vm);
    }

    /// <summary>
    /// FPI 結果登錄：建立（或補完）FPI 檢驗單並判定，Pass → 派工解鎖、Fail → OnHold。
    /// 判定與派工狀態異動走 <see cref="Modules.MFG.Services.IFpiGateService"/>，與手機端同一份實作。
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitFpi(FpiSubmitVm model)
    {
        if (!await Permissions.CanCreate(MenuKey)) return Forbid();

        if (model.FailQty > model.SampleQty)
            ModelState.AddModelError(nameof(model.FailQty), _localizer["IqcFailQtyExceedsSample"].Value);

        if (!ModelState.IsValid)
        {
            ViewBag.RecommendedSampleSize = model.SampleQty;
            return PartialView("_FpiForm", model);
        }

        var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == model.DispatchId);
        if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

        // 已有 Pass 的首件單就不重複登錄（避免重複解鎖與重複計入抽樣狀態）
        bool alreadyPassed = await Db.InspectionOrders.AsNoTracking()
            .AnyAsync(i => i.SourceDocId == model.DispatchId && i.Status == "Pass"
                        && (i.OrderType == InspectionOrderType.FPI || i.InspectionType == "FPI"));
        if (alreadyPassed) return JsonFail(_localizer["FpiAlreadyPassed"].Value);

        // 必須是 UTC：CreatedAt / InspectedAt / UpdatedAt 都是 timestamptz 欄位，
        // 而 IDateTimeService.Now 回的是 Kind=Unspecified 的當地時間，Npgsql 直接拒收。
        // 語意上也只能是 UTC——IqcInspectionService 寫的是 DateTime.UtcNow，
        // SpcAnalysisService 又以 UTC 區間篩 InspectedAt，混入當地時間會讓製程能力分析取錯批。
        var now = DateTime.UtcNow;
        bool passed = model.FailQty == 0;

        // 沿用手機端語意：先找 Pending 的首件單（可能由他處預先建立），沒有才手動建立
        var order = await Db.InspectionOrders
            .FirstOrDefaultAsync(o => o.SourceDocId == model.DispatchId && o.Status == "Pending"
                                   && (o.OrderType == InspectionOrderType.FPI || o.InspectionType == "FPI"));
        if (order == null)
        {
            order = new InspectionOrder
            {
                InspectionType = "FPI",
                OrderType      = InspectionOrderType.FPI,
                PlantId        = dispatch.PlantId,
                SourceDocId    = dispatch.DispatchId,
                ProductCode    = model.ProductCode,
                LotSize        = dispatch.Quantity,
                CreatedAt      = now
            };
            Db.InspectionOrders.Add(order);
        }

        order.SampleSize        = model.SampleQty;
        order.ActualSampleQty   = model.SampleQty;
        order.FailQty           = model.FailQty;
        order.VerificationLevel = model.VerificationLevel;
        order.Remarks           = model.Remarks;
        order.Status            = passed ? "Pass" : "Fail";
        order.InspectorId       = UserContext.UserId;
        order.InspectedAt       = now;
        order.UpdatedAt         = now;

        _fpiGate.ApplyResult(dispatch, passed, now);

        await _samplingPlan.RecordResultAsync(
            productCode: model.ProductCode, supplierCode: null, plantId: dispatch.PlantId,
            passed: passed, machineCode: dispatch.MachineCode, toolingCode: dispatch.ToolingCode);

        await Db.SaveChangesAsync();

        // R1：Localizer 索引一律靜態字面
        var msg = passed ? _localizer["FpiPassed"].Value : _localizer["FpiFailed"].Value;
        return JsonOk(new { order.InspectionOrderId, status = order.Status }, msg);
    }

    /// <summary>UserId → UserName 對照表（畫面顯示 InspectorId 用，避免直接印出 GUID）。</summary>
    private async Task<Dictionary<string, string>> BuildUserNamesAsync(IEnumerable<string?> userIds)
    {
        var ids = userIds.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<string, string>()
            : await Db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
    }

    /// <summary>
    /// IPQC 結果提交（mfg-domain.md §15.3 / Phase H）。
    /// 品管人員掃碼確認檢驗結果，若有不良則自動暫停派工（OnHold）。
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(string id, [FromForm] InspectionResultSubmitVm model)
    {
        if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
        var order = await Db.InspectionOrders.FirstOrDefaultAsync(o => o.InspectionOrderId == id);
        if (order == null)
            return IsAjax ? JsonFail(PzText.Get("Qcr_InspectionNotFound")) : NotFound();

        // 只允許 Pending 狀態提交
        if (order.Status != "Pending")
            return JsonFail(PzText.Get("Qcr_InspectionNotPending"));

        // 更新檢驗結果
        order.ActualSampleQty = model.ActualSampleQty ?? 0;
        order.FailQty         = model.FailQty ?? 0;
        order.InspectorId     = UserContext.UserId;
        order.InspectedAt     = DateTime.UtcNow;   // timestamptz 欄位只收 UTC（見上方 SaveFpiResult 註解）

        // 判定檢驗結果：Ac=0 允收計劃（任何不良即拒批）
        order.Status = order.FailQty == 0 ? "Pass" : "Fail";

        bool isFpi = order.OrderType == InspectionOrderType.FPI || order.InspectionType == "FPI";

        if (isFpi && !string.IsNullOrEmpty(order.SourceDocId))
        {
            // FPI（mfg-domain.md §26.2-②）：Pass → 派工解鎖 ReadyToStart、Fail → OnHold。
            // 與 MobileMfgController 共用 IFpiGateService，避免兩端語意漂移。
            // 缺了這段，桌機端的派工進了 PendingFPI 就永遠出不來（§26.1 缺口 B）。
            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(
                d => d.DispatchId == order.SourceDocId && d.PlantId == order.PlantId);

            if (dispatch != null)
                _fpiGate.ApplyResult(dispatch, order.Status == "Pass", DateTime.UtcNow);
        }
        // Step 2：IPQC 檢驗失敗（FailQty ≥ 1）自動暫停生產中的派工（mfg-domain.md §15.3.1）
        else if (order.Status == "Fail" && !string.IsNullOrEmpty(order.SourceDocId))
        {
            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(
                d => d.DispatchId == order.SourceDocId && d.PlantId == order.PlantId);

            if (dispatch != null && dispatch.Status == DispatchStatus.InProgress)
                Modules.MFG.Services.DispatchHold.Hold(dispatch, DispatchHoldReason.IpqcFailed, DateTime.UtcNow);
        }

        await Db.SaveChangesAsync();

        return IsAjax
            ? JsonOk(new { status = order.Status, message = _localizer["InspectionSubmitted", order.Status].Value })
            : Redirect(nameof(Details) + "?id=" + id);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? inspectionType, string? status,
        string? fromDate, string? toDate, string? searchProduct, string? searchSourceDoc = null)
    {
        if (!await Permissions.CanPrint(MenuKey)) return Forbid();

        var plantId = UserContext.PlantId;
        var q = Db.InspectionOrders.AsNoTracking()
            .Where(o => o.PlantId == plantId);

        if (!string.IsNullOrWhiteSpace(inspectionType))
            q = q.Where(o => o.InspectionType == inspectionType);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(searchProduct))
            q = q.Where(o => o.ProductCode == searchProduct);
        if (!string.IsNullOrWhiteSpace(searchSourceDoc))
            q = q.Where(o => o.SourceDocId == searchSourceDoc);
        if (DateTime.TryParse(fromDate, out var from))
            q = q.Where(o => o.CreatedAt >= from);
        if (DateTime.TryParse(toDate, out var to))
            q = q.Where(o => o.CreatedAt <= to.AddDays(1));

        var rows = await q.OrderByDescending(o => o.CreatedAt).ToListAsync();
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "檢驗單紀錄" : "Inspection Orders",
            Headers = isZh
                ? ["檢驗單號", "類型", "來源單號", "品號", "批量", "應抽數", "不良數", "結果", "檢驗員", "檢驗時間"]
                : ["Order ID", "Type", "Source Doc", "Product", "Lot Size", "Sample Size", "Fail Qty", "Status", "Inspector", "Inspected At"],
            Rows = rows.Select(r => new object?[]
            {
                r.InspectionOrderId, r.InspectionType, r.SourceDocId, r.ProductCode,
                r.LotSize, r.SampleSize, r.FailQty, r.Status,
                r.InspectorId, r.InspectedAt?.ToString("yyyy-MM-dd HH:mm") ?? ""
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"InspectionOrders_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    // GET: /InspectionOrder/Print/{id}（2026-10-27：檢驗單紙本，現場量測記錄用）
    [HttpGet]
    public async Task<IActionResult> Print(string id)
    {
        if (!await Permissions.CanPrint(MenuKey)) return Forbid();
        var doc = await Db.InspectionOrders.AsNoTracking().Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.InspectionOrderId == id);
        if (doc == null) return NotFound();
        await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!, new[] { doc.InspectorId });
        await PreparePrintSignAsync(id, null);
        var n = await PrintNames.ForAsync(doc);
        var f = await PrintFormatsAsync();
        var L = PrintL;
        var t = ScreenText("QCR", "InspectionOrder");
        static string? Num(decimal? v) => v?.ToString("0.####");
        var p = new PrintDocument
        {
            Title = L["Print_Title_InspectionOrder"], DocNo = doc.InspectionOrderId,
            DateLabel = L["Print_CreatedAt"], DateValue = PrintTime(doc.CreatedAt),
        }
            .Field(L["Print_InspectionType"], doc.InspectionType)
            .Field(L["Print_SourceDoc"], doc.SourceLineNo.HasValue ? $"{doc.SourceDocId}-{doc.SourceLineNo}" : doc.SourceDocId)
            .Field(L["Print_Product"], $"{doc.ProductCode} {n.Product(doc.ProductCode)}".Trim())
            .Field(L["Print_LotSize"], f.Qty(doc.LotSize, null))
            .Field(L["Print_SampleSize"], doc.SampleSize.ToString())
            .Field(L["Print_ActualSampleQty"], doc.ActualSampleQty > 0 ? doc.ActualSampleQty.ToString() : null)
            .Field(L["Print_FailQty"], doc.FailQty > 0 ? doc.FailQty.ToString() : null)
            .Field(L["Print_Status"], t[doc.Status])
            .Field(L["Print_Inspector"], PrintUser(doc.InspectorId))
            .Field(L["Print_InspectedAt"], PrintTime(doc.InspectedAt))
            .Field(L["Print_Remarks"], doc.Remarks);
        var tbl = new PrintTable()
            .Col(L["Print_Seq"]).Col(L["Print_InspectionItem"]).Col(L["Print_Uom"])
            .Col(L["Print_LowerSpec"], true).Col(L["Print_TargetValue"], true).Col(L["Print_UpperSpec"], true)
            .Col(L["Print_MeasuredValue"], true).Col(L["Print_Judgement"]);
        foreach (var i in doc.Items.OrderBy(x => x.SeqNo))
            tbl.Row(i.SeqNo.ToString(), i.ItemName, n.UomWith(i.UomCode),
                Num(i.LowerSpec), Num(i.TargetValue), Num(i.UpperSpec), Num(i.MeasuredValue),
                i.IsConforming switch { true => L["Print_Conforming"], false => L["Print_NonConforming"], _ => null });
        p.Tables.Add(tbl);
        return PrintDocumentView(p);
    }
}

/// <summary>檢驗結果提交 ViewModel（手機端 / 桌機端輸入）。</summary>
public class InspectionResultSubmitVm
{
    /// <summary>實際抽樣數（品管確認）。</summary>
    public int? ActualSampleQty { get; set; }

    /// <summary>不良品數量（Fail Count）。</summary>
    public int? FailQty { get; set; }

    /// <summary>量測項目結果（未來可擴充量測值陣列）。</summary>
    public string? MeasurementNotes { get; set; }
}

/// <summary>桌機 IQC 登錄 ViewModel（欄位對齊手機 IqcSubmitRequest）。</summary>
public class IqcSubmitVm
{
    [Required]
    public string GoodsReceiptId { get; set; } = string.Empty;

    public int LineNo { get; set; }

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>批量（進貨明細庫存單位數量，由伺服端帶出，唯讀顯示）。</summary>
    public decimal LotSize { get; set; }

    public string? UomCode { get; set; }

    /// <summary>MIL-STD-1916 驗證等級（1-7）。</summary>
    [Range(1, 7)]
    public int VerificationLevel { get; set; } = 4;

    /// <summary>實際抽樣數。</summary>
    [Range(0.000001, double.MaxValue)]
    public decimal SampleQty { get; set; }

    /// <summary>不良數（Ac=0：≥1 即拒批）。</summary>
    [Range(0, double.MaxValue)]
    public decimal FailQty { get; set; }

    public string? Remarks { get; set; }
}

/// <summary>
/// FPI 待檢清單列（mfg-domain.md §26.2-②）。
/// 「待檢」＝工序 <c>RequiresFpi</c> 且尚無判定 Pass 的 FPI 檢驗單——以事實判定，
/// 不以派工 <c>PendingFPI</c> 狀態判定，否則未經備料的派工不會出現在清單上。
/// </summary>
public record FpiPendingRow(
    string DispatchId,
    string WorkOrderId,
    int    OperationSeq,
    string OperationName,
    string ProductCode,
    decimal Quantity,
    string? MachineCode,
    DispatchStatus Status,
    DateTime? PlannedStartDate);

/// <summary>桌機 FPI 首件登錄 ViewModel（欄位對齊手機 InspectionSubmitRequest 的 FPI 用法）。</summary>
public class FpiSubmitVm
{
    [Required]
    public string DispatchId { get; set; } = string.Empty;

    public string WorkOrderId { get; set; } = string.Empty;

    public int OperationSeq { get; set; }

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>批量（派工數量，由伺服端帶出，唯讀顯示）。</summary>
    public decimal LotSize { get; set; }

    [Range(1, 7)]
    public int VerificationLevel { get; set; } = 4;

    /// <summary>實際抽樣數。</summary>
    [Range(1, int.MaxValue)]
    public int SampleQty { get; set; } = 1;

    /// <summary>不良數（MIL-STD-1916 Ac=0：≥1 即判 Fail）。</summary>
    [Range(0, int.MaxValue)]
    public int FailQty { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }
}
