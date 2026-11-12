using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>備料單 (IM) — inventory.md §11</summary>
    [Authorize]
    public class MaterialStagingController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _user;
        private readonly IPermissionService _perm;
        private readonly IDateTimeService _dt;
        private readonly IApprovalService _approval;
        private readonly IDocNumberService _docNumber;
        private readonly IMaterialStagingService _staging;
        private readonly MFG.Services.IStagingSourceService _stagingSource;
        private readonly IUomService _uom;
        private readonly IStringLocalizer<ProjectZSharedResource> _loc;

        private const string MenuKey = "MaterialStaging";
        private const string DocType = "MaterialStagingOrder";

        public MaterialStagingController(
            ApplicationDbContext db,
            IUserContext user,
            IPermissionService perm,
            IDateTimeService dt,
            IApprovalService approval,
            IDocNumberService docNumber,
            IMaterialStagingService staging,
            MFG.Services.IStagingSourceService stagingSource,
            IUomService uom,
            IStringLocalizer<ProjectZSharedResource> loc)
        {
            _db = db; _user = user; _perm = perm; _dt = dt;
            _approval = approval; _docNumber = docNumber;
            _staging = staging; _stagingSource = stagingSource; _uom = uom; _loc = loc;
        }

        // ── Dropdowns ────────────────────────────────────────────────────────

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ProductOptions = await _db.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom }).ToListAsync();

            ViewBag.UomOptions = await _db.Uoms
                .AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName }).ToListAsync();

            ViewBag.WarehouseOptions = await _db.Warehouses
                .AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName }).ToListAsync();

            ViewBag.LocationOptions = await _db.StorageLocations
                .ForCurrentPlant(_db)
                .AsNoTracking().Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();

            // 來源單據下拉（生產批／工單／派工）：只列本廠尚未結案／作廢者，
            // 已結案的單再備料沒有意義，也避免選單被歷史單據灌爆。
            var plantId = _user.PlantId;

            ViewBag.BatchOptions = await _db.ProductionBatches
                .AsNoTracking()
                .Where(b => b.PlantId == plantId
                         && b.Status != MFG.Models.ProductionBatchStatus.Completed
                         && b.Status != MFG.Models.ProductionBatchStatus.Void)
                .OrderByDescending(b => b.BatchId)
                .Select(b => new { b.BatchId, b.ProductCode })
                .Take(500)
                .ToListAsync();

            ViewBag.WorkOrderOptions = await _db.WorkOrders
                .AsNoTracking()
                .Where(w => w.PlantId == plantId
                         && w.Status != MFG.Models.WorkOrderStatus.Closed
                         && w.Status != MFG.Models.WorkOrderStatus.Void)
                .OrderByDescending(w => w.WorkOrderId)
                .Select(w => new { w.WorkOrderId, w.ProductCode })
                .Take(500)
                .ToListAsync();

            ViewBag.DispatchOptions = await _db.Dispatches
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.Status != MFG.Models.DispatchStatus.Closed
                         && d.Status != MFG.Models.DispatchStatus.Cancelled)
                .OrderByDescending(d => d.DispatchId)
                .Select(d => new { d.DispatchId, d.WorkOrderId, d.OperationSeq })
                .Take(500)
                .ToListAsync();

            ViewBag.UomDecimals = await _uom.GetAllDecimalPlacesAsync();
            ViewBag.CanPrint    = await _perm.CanPrint(MenuKey);
        }

        // ── Index ─────────────────────────────────────────────────────────────

        public async Task<IActionResult> Index(
            string? searchTerm, string? status, DateOnly? dateFrom, DateOnly? dateTo,
            string sortOrder = "id_desc", int pageNumber = 1, int pageSize = 20,
            bool searched = false)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            // Index.cshtml 以 ViewBag.Searched 決定顯示「請先查詢」提示或資料表；
            // 漏設會讓它恆為 false，列表即使查詢過也永遠看不到任何備料單。
            ViewBag.Searched   = searched;
            ViewBag.SearchTerm = searchTerm;
            ViewBag.Status     = status;
            ViewBag.DateFrom   = dateFrom;
            ViewBag.DateTo     = dateTo;
            ViewBag.CurrentSort    = sortOrder;
            ViewBag.CurrentPageSize = pageSize;
            ViewBag.IdSort   = sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";

            ViewBag.CanCreate = await _perm.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _perm.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _perm.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _perm.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<MaterialStagingOrder>?)null);

            var q = _db.MaterialStagingOrders.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(o => o.StagingId.Contains(searchTerm)
                              || (o.SourceId != null && o.SourceId.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(status))
                q = q.Where(o => o.Status == status);
            if (dateFrom.HasValue) q = q.Where(o => o.CreatedAt >= dateFrom.Value.ToDateTime(TimeOnly.MinValue));
            if (dateTo.HasValue)   q = q.Where(o => o.CreatedAt <= dateTo.Value.ToDateTime(TimeOnly.MaxValue));

            q = sortOrder switch
            {
                "id_asc"   => q.OrderBy(o => o.StagingId),
                "id_desc"  => q.OrderByDescending(o => o.StagingId),
                "date_asc" => q.OrderBy(o => o.CreatedAt),
                _          => q.OrderByDescending(o => o.CreatedAt),
            };

            var paged = await PaginatedList<MaterialStagingOrder>.CreateAsync(q, pageNumber, pageSize);
            return View(paged);
        }

        // ── Create / Edit / Details ───────────────────────────────────────────

        public async Task<IActionResult> Create()
        {
            if (!await _perm.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new MaterialStagingFormVm
            {
                CreatedBy = _user.UserId ?? "",
            }) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _perm.CanUpdate(MenuKey)) return Forbid();
            var order = await LoadWithLinesAsync(id);
            if (order == null) return NotFound();

            await SyncApprovalStatusAsync(order);
            await PrepareFormDropdowns();
            if (order.Status is "Confirmed" or "Picking" or "Completed" or "Void")
            {
                // 已確認之後欄位不可再改，但「揀料完成」「作廢」這類狀態動作仍必須做得到——
                // 只鎖欄位、不鎖動作；純檢視（Details）不設此旗標。
                ViewBag.IsReadOnly   = true;
                ViewBag.AllowActions = true;
            }
            return IsAjax ? PartialView("_Form", ToVm(order)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var order = await LoadWithLinesAsync(id);
            if (order == null) return NotFound();
            await SyncApprovalStatusAsync(order);
            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(order)) : RedirectToAction(nameof(Index));
        }

        // ── Save ──────────────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(MaterialStagingFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.StagingId)
                ? !await Permissions.CanCreate(MenuKey)
                : !await Permissions.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }

            try
            {
                var userId  = _user.UserId  ?? "";
                var plantId = _user.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _db.LoadApprovalStatusAsync<MaterialStagingOrder>(vm.StagingId)
                    == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _loc["StagingErrPendingApproval"]);
                    await PrepareFormDropdowns();
                    return PartialView("_Form", vm);
                }

                var config       = await _approval.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                var isNew = string.IsNullOrEmpty(vm.StagingId);
                MaterialStagingOrder order;

                if (isNew)
                {
                    order = new MaterialStagingOrder
                    {
                        StagingId = await _docNumber.NextAsync(DocType, plantId),
                        CreatedBy = userId,
                        PlantId   = plantId,
                    };
                    _db.MaterialStagingOrders.Add(order);
                }
                else
                {
                    order = await LoadWithLinesAsync(vm.StagingId!)
                            ?? throw new InvalidOperationException(PzText.Get("Inv_StagingNotFound"));
                    if (order.Status != "Draft")
                        throw new InvalidOperationException(PzText.Get("Inv_StagingOnlyDraftEditable"));
                    // RemoveRange 僅標記舊行為 Deleted，不會從 order.Lines 記憶體集合移除；
                    // 若不 Clear，下方重新 Add 新行後 order.Lines 會同時含新舊物件。
                    _db.MaterialStagingLines.RemoveRange(order.Lines);
                    order.Lines.Clear();
                }

                order.SourceType           = vm.SourceType;
                order.SourceId             = vm.SourceId;
                order.StagingWhCode        = vm.StagingWhCode;
                order.StagingLocationCode  = vm.StagingLocationCode;
                order.Remarks              = vm.Remarks;
                order.UpdatedAt            = DateTime.UtcNow;
                order.ApprovalStatus       = targetStatus;

                var lineNo = 1;
                foreach (var lv in vm.Lines)
                {
                    var rqty = await _uom.RoundQtyAsync(lv.UomCode, lv.RequiredQty);
                    order.Lines.Add(new MaterialStagingLine
                    {
                        StagingId    = order.StagingId,
                        LineNo       = lineNo++,
                        ProductCode  = lv.ProductCode,
                        UomCode      = lv.UomCode,
                        StockUomCode = lv.StockUomCode ?? lv.UomCode,
                        RequiredQty  = rqty,
                        // 逐行認工單／派工：生產批來源一張單橫跨多張工單，預留轉移與領料消耗都靠這兩欄
                        SourceWorkOrderId = lv.SourceWorkOrderId
                            ?? (order.SourceType == "WorkOrder" ? order.SourceId : null),
                        SourceDispatchId  = lv.SourceDispatchId,
                        FromWarehouseCode = lv.FromWarehouseCode,
                        FromLocationCode  = lv.FromLocationCode,
                        Remarks      = lv.Remarks,
                    });
                }

                await _db.SaveChangesAsync();
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
        }

        // ── Approval workflow ─────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<MaterialStagingOrder>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<MaterialStagingOrder>(id, MenuKey);

        // ── Confirm（鎖庫存）────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var order = await LoadWithLinesAsync(id);
            if (order == null) return NotFound();
            if (order.Status != "Draft")
                return BadRequest(new { success = false, message = _loc["StagingErrDraftToConfirm"].Value });
            if (order.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _loc["StagingErrNeedApproval"].Value });

            try
            {
                await _staging.ConfirmAsync(order, _user.UserId ?? "");
                return Ok(new { success = true, message = _loc["StagingConfirmed"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ── Stage（揀料完成，實際移入備料倉）────────────────────────────────────

        /// <summary>
        /// 揀料完成：把明細由來源儲位實際移入備料倉（同廠調撥），Confirmed → Completed。
        /// 預留不在此釋放，留到領料過帳才等額退場（見 IMaterialStagingService.StageAsync）。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Stage(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var order = await LoadWithLinesAsync(id);
            if (order == null) return NotFound();

            try
            {
                await _staging.StageAsync(order, _user.UserId ?? "");
                return Ok(new { success = true, message = _loc["StagingStaged"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ── 來源帶料（派工／工單／生產批 → 備料明細）──────────────────────────

        /// <summary>
        /// 依來源帶出待備料明細與預設備料倉，供表單「帶入用料」按鈕使用。
        /// 需求量已扣掉既有發料與既有備料單鎖量，重複帶入不會把同一批料鎖兩次。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SourceLines(string sourceType, string sourceId)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceId))
                return Json(new { success = false, lines = Array.Empty<object>() });

            var plantId = _user.PlantId ?? "";
            var lines   = await _stagingSource.GetLinesAsync(sourceType, sourceId, plantId);
            var (whCode, locationCode) = await _stagingSource.ResolveStagingTargetAsync(sourceType, sourceId, plantId);

            return Json(new
            {
                success = true,
                stagingWhCode = whCode,
                stagingLocationCode = locationCode,
                lines = lines.Select(l => new
                {
                    productCode       = l.ProductCode,
                    productName       = l.ProductName,
                    uomCode           = l.UomCode,
                    stockUomCode      = l.StockUomCode,
                    requiredQty       = l.RequiredQty,
                    sourceWorkOrderId = l.SourceWorkOrderId,
                    sourceDispatchId  = l.SourceDispatchId,
                }),
            });
        }

        // ── Void（作廢，釋放預留）──────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var order = await LoadWithLinesAsync(id);
            if (order == null) return NotFound();

            try
            {
                await _staging.VoidAsync(order);
                return Ok(new { success = true, message = _loc["StagingVoided"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // ── Delete ───────────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _perm.CanDelete(MenuKey)) return Forbid();

            var order = await LoadWithLinesAsync(id);
            if (order == null) return Json(new { success = false, message = _loc["StagingNotFound"].Value });
            if (order.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _loc["StagingErrDeletePending"].Value });
            if (order.Status != "Draft")
                return Json(new { success = false, message = _loc["StagingErrDraftToDelete"].Value });

            _db.MaterialStagingOrders.Remove(order);
            await _db.SaveChangesAsync();
            return Json(new { success = true, message = _loc["StagingDeleted"].Value });
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private Task<MaterialStagingOrder?> LoadWithLinesAsync(string id) =>
            _db.MaterialStagingOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.StagingId == id);

        private MaterialStagingFormVm ToVm(MaterialStagingOrder o) => new()
        {
            StagingId          = o.StagingId,
            SourceType         = o.SourceType,
            SourceId           = o.SourceId,
            StagingWhCode      = o.StagingWhCode,
            StagingLocationCode = o.StagingLocationCode,
            Status             = o.Status,
            ApprovalStatus     = o.ApprovalStatus,
            ApprovalRequestId  = o.ApprovalRequestId,
            Remarks            = o.Remarks,
            CreatedBy          = o.CreatedBy,
            Lines = o.Lines.OrderBy(l => l.LineNo).Select(l => new MaterialStagingLineVm
            {
                LineNo       = l.LineNo,
                ProductCode  = l.ProductCode,
                UomCode      = l.UomCode,
                StockUomCode = l.StockUomCode,
                RequiredQty  = l.RequiredQty,
                PickedQty    = l.PickedQty,
                ReservationId = l.ReservationId,
                SourceWorkOrderId = l.SourceWorkOrderId,
                SourceDispatchId  = l.SourceDispatchId,
                FromWarehouseCode = l.FromWarehouseCode,
                FromLocationCode  = l.FromLocationCode,
                Remarks      = l.Remarks,
            }).ToList(),
        };

        // GET: /MaterialStaging/Print/{id}（2026-10-27：備料單紙本，倉庫揀料用）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.MaterialStagingOrders.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.StagingId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy, doc.ConfirmedBy, doc.StagedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var t = ScreenText("INV", "MaterialStaging");
            var p = new PrintDocument
            {
                Title = L["Print_Title_MaterialStaging"], DocNo = doc.StagingId,
                DateLabel = L["Print_CreatedAt"], DateValue = PrintTime(doc.CreatedAt),
            }
                .Field(L["Print_SourceDoc"], $"{t[doc.SourceType]} {doc.SourceId}".Trim())
                .Field(L["Print_StagingLocation"], n.PlaceWith(doc.StagingWhCode, doc.StagingLocationCode))
                .Field(L["Print_Status"], t[doc.Status])
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
                .Field(L["Print_ConfirmedBy"], PrintUser(doc.ConfirmedBy))
                .Field(L["Print_StagedBy"], PrintUser(doc.StagedBy))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_RequiredQty"], true).Col(L["Print_PickedQty"], true).Col(L["Print_Uom"])
                .Col(L["Print_From"]).Col(L["Print_WorkOrder"]).Col(L["Print_Remarks"]);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.RequiredQty, l.UomCode), f.Qty(l.PickedQty, l.UomCode), n.UomWith(l.UomCode),
                    n.PlaceWith(l.FromWarehouseCode, l.FromLocationCode), l.SourceWorkOrderId, l.Remarks);
            p.Tables.Add(tbl);
            return PrintDocumentView(p);
        }
    }
}
