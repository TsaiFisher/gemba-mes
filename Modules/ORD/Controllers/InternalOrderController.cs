using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.ORD.Controllers
{
    [Authorize]
    public class InternalOrderController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly IInventoryPostingService _postingService;
        private readonly IInventoryReservationService _reservations;
        private readonly IUomService _uomService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Modules.MFG.Services.IMrpAutoExpandService _autoExpand;

        private const string MenuKey = "InternalOrder";
        private const string DocType = "InternalOrder";

        public InternalOrderController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService, IDocNumberService docNumberService,
            IInventoryPostingService postingService,
            IInventoryReservationService reservations,
            IUomService uomService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            Modules.MFG.Services.IMrpAutoExpandService autoExpand)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService; _docNumberService = docNumberService;
            _postingService = postingService; _reservations = reservations; _uomService = uomService;
            _autoExpand = autoExpand;

            _localizer = localizer;
        }

        /// <summary>
        /// §25.5：訂單進入「已核准」後，簡化模式自動展開 MRP（與 SalesOrderController 同一份服務）。
        /// 服務以狀態判定（重算未展開剩餘量），非簡化模式或無未展開需求時不做事。
        /// </summary>
        private async Task<IActionResult> SavedWithAutoExpandAsync()
        {
            var r = await _autoExpand.ExpandApprovedOrdersAsync();
            var message = !r.Triggered ? null
                : r.Error == null
                    ? PzText.Get("Ord_SavedMrpExpandedFmt", r.MrpRunId)
                    : PzText.Get("Ord_SavedMrpFailedFmt", r.Error, r.MrpRunId);

            var toast = message == null
                ? "window.toastSuccess&&window.toastSuccess();"
                : $"window.toastSuccess&&window.toastSuccess({System.Text.Json.JsonSerializer.Serialize(message)});";
            return Content($"<script>{toast}refreshList&&refreshList();</script>", "text/html");
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom }).ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName }).ToListAsync();

            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking().Where(w => w.IsActive && w.PlantId == _userContext.PlantId)
                .OrderBy(w => w.WhCode).Select(w => new { w.WhCode, w.WhName }).ToListAsync();

            var allUnits = await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var currentPlantId = _userContext.PlantId;
            ViewBag.DeptOptions = allUnits
                .Where(u => GetEffectivePlantId(u, allUnits) == currentPlantId)
                .OrderBy(u => u.Name)
                .Select(u => new { u.Id, u.Name })
                .ToList();

            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.CanPrint    = await _permissionService.CanPrint(MenuKey);
        }

        public async Task<IActionResult> Index(
            string? searchTerm, string? typeCode, string? departmentCode,
            DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId;
            ViewBag.SearchTerm     = searchTerm; ViewBag.TypeCode = typeCode;
            ViewBag.DepartmentCode = departmentCode;
            ViewBag.DateFrom = dateFrom; ViewBag.DateTo = dateTo;
            ViewBag.Searched = searched; ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.IdSort   = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            await PrepareIndexPermissionsAsync(MenuKey);

            var allUnits = await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            ViewBag.DeptOptions = allUnits
                .Where(u => GetEffectivePlantId(u, allUnits) == plantId)
                .OrderBy(u => u.Name).Select(u => new { u.Id, u.Name }).ToList();
            ViewBag.DeptNames = allUnits.ToDictionary(u => u.Id, u => u.Name);

            if (!searched) return View((PaginatedList<InternalOrder>?)null);

            var query = _context.InternalOrders.AsNoTracking()
                .Include(d => d.Lines).Where(d => d.PlantId == plantId);

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.InternalOrderId.Contains(searchTerm) || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(typeCode))       query = query.Where(d => d.TypeCode == typeCode);
            if (!string.IsNullOrWhiteSpace(departmentCode)) query = query.Where(d => d.DepartmentCode == departmentCode);
            if (dateFrom.HasValue) query = query.Where(d => d.OrderDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.OrderDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(d => d.InternalOrderId),
                "id_desc"   => query.OrderByDescending(d => d.InternalOrderId),
                "date_asc"  => query.OrderBy(d => d.OrderDate),
                "date_desc" => query.OrderByDescending(d => d.OrderDate),
                _           => query.OrderByDescending(d => d.InternalOrderId),
            };
            return View(await PaginatedList<InternalOrder>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new InternalOrderFormVm { CreatedBy = _userContext.UserId ?? "", OrderDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc, () =>
            {
                // 全模式：核准通過 → 轉 Confirmed（待倉庫作業領料），不在此過帳發料（內部訂單缺料走 MRP）。
                if (doc.Status == DocStatus.Draft) doc.Status = DocStatus.Confirmed;
                return Task.CompletedTask;
            });
            await PrepareFormDropdowns();
            if (doc.Status == DocStatus.Posted || doc.Status == DocStatus.Void) ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", MapToVm(doc)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc, () =>
            {
                // 全模式：核准通過 → 轉 Confirmed（待倉庫作業領料），不在此過帳發料（內部訂單缺料走 MRP）。
                if (doc.Status == DocStatus.Draft) doc.Status = DocStatus.Confirmed;
                return Task.CompletedTask;
            });
            await PrepareFormDropdowns(); ViewBag.IsReadOnly = true;
            // 唯讀面板上的「取消確認」依此顯示（列表對非草稿只給檢視，那是唯一入口）
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            return IsAjax ? PartialView("_Form", MapToVm(doc)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(InternalOrderFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.InternalOrderId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId = _userContext.UserId ?? ""; var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<InternalOrder>(vm.InternalOrderId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns(); return PartialView("_Form", vm);
                }

                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);
                (bool HasMrpLines, bool HasInProgressWo) mrpImpact = (false, false);
                decimal oldTotalQty = 0m;

                if (string.IsNullOrEmpty(vm.InternalOrderId))
                {
                    var doc = new InternalOrder
                    {
                        InternalOrderId = await _docNumberService.NextAsync(DocType, plantId, vm.OrderDate),
                        OrderDate = vm.OrderDate, TypeCode = vm.TypeCode,
                        DepartmentCode = vm.DepartmentCode, Remarks = vm.Remarks,
                        CreatedBy = userId, ApprovalStatus = targetStatus,
                    };
                    await ApplyLinesAsync(doc, vm); _context.InternalOrders.Add(doc);
                    // 全模式：核准即轉 Confirmed（待倉庫作業領料），不在訂單過帳發料。
                    if (targetStatus == DocumentApprovalStatus.Approved) doc.Status = DocStatus.Confirmed;
                }
                else
                {
                    var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == vm.InternalOrderId)
                        ?? throw new InvalidOperationException(PzText.Get("Ord_InternalOrderNotFound"));
                    // xmin 樂觀鎖：以前端帶回的版本作為 OriginalValue（見 ord-module.md §13）
                    _context.Entry(doc).Property(e => e.xmin).OriginalValue = vm.RowVersion;
                    if (doc.Status != DocStatus.Draft) throw new InvalidOperationException(PzText.Get("Inv_OnlyDraftEditable"));
                    if (doc.Lines.Any(l => l.QtyPutAway > 0)) throw new InvalidOperationException(PzText.Get("Inv_WarehouseWorkStarted"));

                    // §21.2：已匯入 MRP 且有投產工單時阻擋修改（與 SalesOrderController 一致）
                    mrpImpact = await CheckMrpImpactAsync(vm.InternalOrderId!);
                    if (mrpImpact.HasInProgressWo)
                        throw new InvalidOperationException(PzText.Get("Ord_WorkOrderInProgress"));

                    // §21.7（縮量反向流）：記錄修改前總量，供稍後判斷是否縮量 → 釋放殘留預留
                    oldTotalQty = doc.Lines.Sum(l => l.OrderQty);

                    _context.InternalOrderLines.RemoveRange(doc.Lines); doc.Lines.Clear();
                    doc.OrderDate = vm.OrderDate; doc.TypeCode = vm.TypeCode;
                    doc.DepartmentCode = vm.DepartmentCode; doc.Remarks = vm.Remarks;
                    doc.UpdatedAt = DateTime.UtcNow; doc.ApprovalStatus = targetStatus;
                    await ApplyLinesAsync(doc, vm);
                    // 全模式：核准即轉 Confirmed（待倉庫作業領料）。內部訂單＝純需求，缺料走 MRP；
                    // 實際發料（倉庫/儲位/批號、扣庫存、回寫 QtyPutAway）統一在「倉庫作業」完成。
                    if (targetStatus == DocumentApprovalStatus.Approved) doc.Status = DocStatus.Confirmed;
                }
                await _context.SaveChangesAsync();
                // §21.6：編輯既有內部訂單後，標記其關聯 MrpRun 為 dirty（提醒重跑以同步數量/交期變更）。
                // 內部訂單與銷售訂單共用 MrpRunLine.SalesOrderId 作為「來源訂單號」。
                if (!string.IsNullOrEmpty(vm.InternalOrderId))
                {
                    await MarkRelatedMrpRunsDirtyAsync(vm.InternalOrderId);
                    // §21.2：有 MRP 展開但無投產工單 → 附帶警示，提醒重跑同步
                    if (mrpImpact.HasMrpLines)
                    {
                        // §21.7：縮量 → 立即釋放殘留預留（避免殭屍超量；MRP 重跑會以新量重建）
                        var newTotalQty = vm.Lines?.Sum(l => l.OrderQty) ?? 0m;
                        if (newTotalQty < oldTotalQty)
                            await ReleaseMrpReservationsForIoAsync(vm.InternalOrderId);
                        return Content("<script>window.toastSuccess&&window.toastSuccess(" + System.Text.Json.JsonSerializer.Serialize(PzText.Get("Ord_SavedSuggestMrpRerun")) + ");refreshList&&refreshList();</script>", "text/html");
                    }
                }
                return targetStatus == DocumentApprovalStatus.Approved
                    ? await SavedWithAutoExpandAsync()
                    : Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                var latest = await _context.InternalOrders.AsNoTracking().Include(d => d.Lines)
                    .FirstOrDefaultAsync(d => d.InternalOrderId == vm.InternalOrderId);
                await PrepareFormDropdowns();
                return PartialView("_Form", latest == null ? vm : MapToVm(latest));
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            { ModelState.AddModelError(string.Empty, ex.Message); await PrepareFormDropdowns(); return PartialView("_Form", vm); }
        }

        /// <summary>
        /// §21.2：查詢此內部訂單是否已匯入 MRP，以及是否有投產中的工單。
        /// HasInProgressWo=true → 阻擋修改；HasMrpLines=true, HasInProgressWo=false → 警示。
        /// 沿用 MrpRunLine.SalesOrderId 作為「來源訂單號」（SO/IO 共用）。
        /// </summary>
        private async Task<(bool HasMrpLines, bool HasInProgressWo)> CheckMrpImpactAsync(string internalOrderId)
        {
            var mrpLineExists = await _context.MrpRunLines.AsNoTracking()
                .AnyAsync(l => l.SalesOrderId == internalOrderId);
            if (!mrpLineExists) return (false, false);

            var batchIds = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == internalOrderId && l.ProductionBatchId != null)
                .Select(l => l.ProductionBatchId!)
                .Distinct()
                .ToListAsync();

            var hasInProgress = batchIds.Count > 0 && await _context.WorkOrders.AsNoTracking()
                .AnyAsync(w => batchIds.Contains(w.ProductionBatchId)
                            && (w.Status == WorkOrderStatus.InProgress
                             || w.Status == WorkOrderStatus.Completed
                             || w.Status == WorkOrderStatus.Closed));

            return (true, hasInProgress);
        }

        /// <summary>§21.7：釋放此內部訂單下所有 MrpRunLine 的庫存預留（縮量時呼叫，沿用 SalesOrderId 作來源鍵）。</summary>
        private async Task ReleaseMrpReservationsForIoAsync(string internalOrderId)
        {
            var mrpLines = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == internalOrderId)
                .Select(l => new { l.MrpRunId, l.LineSeq })
                .ToListAsync();

            foreach (var ml in mrpLines)
                await _reservations.ReleaseBySourceLineAsync(
                    ReservationSourceType.MrpRunLine, ml.MrpRunId, ml.LineSeq);
        }

        /// <summary>§21.6：標記此內部訂單關聯的所有 MrpRun 為 dirty（沿用 MrpRunLine.SalesOrderId 作來源訂單鍵）。</summary>
        private async Task MarkRelatedMrpRunsDirtyAsync(string internalOrderId)
        {
            var runIds = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == internalOrderId)
                .Select(l => l.MrpRunId)
                .Distinct()
                .ToListAsync();

            if (runIds.Count == 0) return;
            var runs = await _context.MrpRuns
                .Where(r => runIds.Contains(r.MrpRunId) && !r.IsDirty)
                .ToListAsync();
            foreach (var r in runs) r.IsDirty = true;
            if (runs.Count > 0) await _context.SaveChangesAsync();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
        {
            var result = await SubmitForApprovalAsync<InternalOrder>(id, MenuKey, DocType);
            // 無簽核流程／自動跳簽時，送審即終局核准 → 同樣要自動展開（§25.5）
            if (result is OkObjectResult) await _autoExpand.ExpandApprovedOrdersAsync();
            return result;
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<InternalOrder>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Draft) return BadRequest(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any()) return BadRequest(new { success = false, message = _localizer["AddLineFirst"].Value });
            // 全模式：確認＝轉 Confirmed（待倉庫作業），不在此過帳發/退料（統一於「倉庫作業」）。
            doc.Status = DocStatus.Confirmed;
            await _context.SaveChangesAsync();
            var msgKey = doc.TypeCode == "NR" ? "ConfirmedAwaitingPutaway" : "ConfirmedAwaitingPick";
            return Ok(new { success = true, message = _localizer[msgKey].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Confirmed) return BadRequest(new { success = false, message = _localizer["ConfirmedOnlyCanWithdraw"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0)) return BadRequest(new { success = false, message = _localizer["WarehouseStartedCannotWithdraw"].Value });
            doc.Status = DocStatus.Draft; await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.FindAsync(id); if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            // 作廢沖銷過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住沖銷與單據狀態更新。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = doc.PlantId ?? "", DocNo = doc.InternalOrderId,
                    Reason = reason, CreatedBy = _userContext.UserId,
                    SetDocVoid = () => doc.Status = DocStatus.Void,
                });
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["VoidedAndReversed"].Value });
            }
            catch (InvalidOperationException ex)
            { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException)
            { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.Status != DocStatus.Draft) return Json(new { success = false, message = _localizer["StagingErrDraftToDelete"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return Json(new { success = false, message = _localizer["HasWarehouseOpsCannotDelete"].Value });
            // 已納入 MRP 執行單的內部訂單不可刪除（MrpRunLine.SalesOrderId 沿用為來源訂單號，含 NO- 前綴）
            var mrpRunIds = await _context.MrpRunLines
                .Where(l => l.SalesOrderId == id)
                .Select(l => l.MrpRunId).Distinct().OrderBy(x => x).ToListAsync();
            if (mrpRunIds.Count > 0)
                return Json(new { success = false, message = string.Format(
                    _localizer["OrderReferencedByMrpCannotDelete"].Value, string.Join(", ", mrpRunIds)) });
            _context.InternalOrders.Remove(doc); await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.InternalOrderId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            ViewBag.DepartmentName = string.IsNullOrEmpty(doc.DepartmentCode) ? null : await _context.OrganizationUnits
                .IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == doc.DepartmentCode).Select(u => u.Name).FirstOrDefaultAsync();
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            return View(doc);
        }

        private static InternalOrderFormVm MapToVm(InternalOrder doc) => new()
        {
            InternalOrderId = doc.InternalOrderId, ApprovalStatus = doc.ApprovalStatus, Status = doc.Status, RowVersion = doc.xmin,
            OrderDate = doc.OrderDate, TypeCode = doc.TypeCode, CreatedBy = doc.CreatedBy,
            DepartmentCode = doc.DepartmentCode, Remarks = doc.Remarks,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new InternalOrderLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode, DeliveryDate = l.DeliveryDate,
                OrderQty = l.OrderQty, UomCode = l.UomCode, StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                WhCode = l.WhCode, LocationCode = l.LocationCode, LotNo = l.LotNo,
                QtyPutAway = l.QtyPutAway, IsClosed = l.IsClosed, Remarks = l.Remarks,
            }).ToList()
        };

        private async Task ApplyLinesAsync(InternalOrder doc, InternalOrderFormVm vm)
        {
            int seq = 1;
            foreach (var lv in vm.Lines)
            {
                // 庫存數量由伺服器依品號主檔換算（2026-10-12）。畫面的 syncStockQty 會先算一份顯示用，
                // 但它是隱藏欄位、送什麼收什麼：換算 API 失敗或被改就會入錯帳。
                var (stockUom, stockQty) = await _uomService.ToStockQtyAsync(lv.ProductCode, lv.UomCode, lv.OrderQty);
                doc.Lines.Add(new InternalOrderLine
                {
                    InternalOrderId = doc.InternalOrderId, LineNo = seq++,
                    ProductCode = lv.ProductCode, DeliveryDate = lv.DeliveryDate,
                    OrderQty = lv.OrderQty, UomCode = lv.UomCode,
                    StockQty = stockQty, StockUomCode = stockUom,
                    WhCode = lv.WhCode, LocationCode = lv.LocationCode, LotNo = lv.LotNo,
                    IsClosed = lv.IsClosed, Remarks = lv.Remarks,
                });
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseLine(string id, int lineNo)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            if (doc.Status == DocStatus.Void)
                return BadRequest(new { success = false, message = _localizer["CloseErrVoided"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["CloseErrNeedApproval"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            if (line == null) return NotFound();
            line.IsClosed = true;
            await _context.SaveChangesAsync();
            // §21.1：IO 目前未匯入 MRP，保留鉤子供未來整合
            return Ok(new { success = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ReopenLine(string id, int lineNo)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            if (doc.Status == DocStatus.Void)
                return BadRequest(new { success = false, message = _localizer["CloseErrVoided"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["ReopenErrNeedApproval"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            if (line == null) return NotFound();
            line.IsClosed = false;
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseDoc(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.InternalOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.InternalOrderId == id);
            if (doc == null) return NotFound();
            if (doc.Status == DocStatus.Void)
                return BadRequest(new { success = false, message = _localizer["CloseErrVoided"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["CloseErrNeedApproval"].Value });
            foreach (var l in doc.Lines.Where(l => !l.IsClosed))
                l.IsClosed = true;
            await _context.SaveChangesAsync();
            // §21.1：IO 目前未匯入 MRP，保留鉤子供未來整合
            return Ok(new { success = true });
        }

        private string? GetEffectivePlantId(OrganizationUnit unit, List<OrganizationUnit> allUnits)
        {
            if (!string.IsNullOrEmpty(unit.PlantId)) return unit.PlantId;
            if (string.IsNullOrEmpty(unit.ParentId)) return null;
            var parent = allUnits.FirstOrDefault(u => u.Id == unit.ParentId);
            return parent == null ? null : GetEffectivePlantId(parent, allUnits);
        }
    }
}
