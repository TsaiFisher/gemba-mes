using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    [Authorize]
    public class PurchaseRequestController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPurchaseService _purchaseService;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IUomService _uomService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "PurchaseRequest";
        private const string DocType = "PurchaseRequest";

        public PurchaseRequestController(
            ApplicationDbContext context,
            IPurchaseService purchaseService,
            IUserContext userContext,
            IPermissionService permissionService,
            IDateTimeService dateTimeService,
            IApprovalService approvalService,
            IUomService uomService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _purchaseService = purchaseService;
            _userContext = userContext;
            _permissionService = permissionService;
            _dateTimeService = dateTimeService;
            _approvalService = approvalService;
            _uomService = uomService;
        
            _localizer = localizer;
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.PurchaseUom, p.StockUom })
                .ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName })
                .ToListAsync();

            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        // GET: /PurchaseRequest
        public async Task<IActionResult> Index(
            string? searchTerm, string? requestedBy,
            DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId;

            ViewBag.SearchTerm    = searchTerm;
            ViewBag.RequestedBy   = requestedBy;
            ViewBag.DateFrom      = dateFrom;
            ViewBag.DateTo        = dateTo;
            ViewBag.Searched      = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort   = sortOrder;
            ViewBag.IdSort        = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort      = sortOrder == "date_asc" ? "date_desc" : "date_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<PurchaseRequest>?)null);

            var query = _context.PurchaseRequests
                .AsNoTracking()
                .Include(pr => pr.Lines)
                .Where(pr => pr.PlantId == plantId);

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(pr => pr.PurchaseRequestId.Contains(searchTerm) || pr.Remarks!.Contains(searchTerm));

            if (!string.IsNullOrWhiteSpace(requestedBy))
                query = query.Where(pr => pr.RequestedBy == requestedBy);

            if (dateFrom.HasValue)
                query = query.Where(pr => pr.RequestDate >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(pr => pr.RequestDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"   => query.OrderBy(pr => pr.PurchaseRequestId),
                "id_desc"  => query.OrderByDescending(pr => pr.PurchaseRequestId),
                "date_asc" => query.OrderBy(pr => pr.RequestDate),
                "date_desc" => query.OrderByDescending(pr => pr.RequestDate),
                _           => query.OrderByDescending(pr => pr.PurchaseRequestId),
            };

            var paged = await PaginatedList<PurchaseRequest>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);

            // 將 UserId 對應到 UserName 供 View 顯示
            var userIds = paged.Select(pr => pr.RequestedBy).Distinct().ToList();
            var userNames = await _context.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
            ViewBag.UserNames = userNames;

            return View(paged);
        }

        // GET: /PurchaseRequest/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new PurchaseRequestFormVm { RequestedBy = _userContext.UserId ?? "", RequestDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /PurchaseRequest/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

            var pr = await _context.PurchaseRequests
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseRequestId == id);
            if (pr == null) return NotFound();

            await SyncApprovalStatusAsync(pr);

            await PrepareFormDropdowns();

            var vm = new PurchaseRequestFormVm
            {
                PurchaseRequestId = pr.PurchaseRequestId,
                ApprovalStatus    = pr.ApprovalStatus,
                RowVersion        = pr.xmin,
                RequestDate       = pr.RequestDate,
                RequestedBy       = pr.RequestedBy,
                Remarks           = pr.Remarks,
                Lines = pr.Lines.OrderBy(l => l.LineNo).Select(l => new PurchaseRequestLineVm
                {
                    LineNo        = l.LineNo,
                    ProductCode   = l.ProductCode,
                    DeliveryDate  = l.DeliveryDate,
                    RequestQty    = l.RequestQty,
                    UomCode       = l.UomCode,
                    StockQty      = l.StockQty,
                    StockUomCode  = l.StockUomCode,
                    Remarks       = l.Remarks,
                    IsClosed      = l.IsClosed,
                }).ToList()
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /PurchaseRequest/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var pr = await _context.PurchaseRequests
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseRequestId == id);
            if (pr == null) return NotFound();

            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;

            var vm = new PurchaseRequestFormVm
            {
                PurchaseRequestId = pr.PurchaseRequestId,
                ApprovalStatus    = pr.ApprovalStatus,
                RowVersion        = pr.xmin,
                RequestDate       = pr.RequestDate,
                RequestedBy       = pr.RequestedBy,
                Remarks           = pr.Remarks,
                Lines = pr.Lines.OrderBy(l => l.LineNo).Select(l => new PurchaseRequestLineVm
                {
                    LineNo        = l.LineNo,
                    ProductCode   = l.ProductCode,
                    DeliveryDate  = l.DeliveryDate,
                    RequestQty    = l.RequestQty,
                    UomCode       = l.UomCode,
                    StockQty      = l.StockQty,
                    StockUomCode  = l.StockUomCode,
                    Remarks       = l.Remarks,
                    IsClosed      = l.IsClosed,
                }).ToList()
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // POST: /PurchaseRequest/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(PurchaseRequestFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.PurchaseRequestId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }

            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 若為已審核中或已核准，不允許再次儲存
                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<PurchaseRequest>(vm.PurchaseRequestId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns();
                    return PartialView("_Form", vm);
                }

                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                PurchaseRequest pr;
                if (string.IsNullOrEmpty(vm.PurchaseRequestId))
                {
                    pr = await _purchaseService.CreatePurchaseRequestAsync(vm, userId, plantId);
                    pr.ApprovalStatus = targetStatus;
                }
                else
                {
                    pr = await _purchaseService.UpdatePurchaseRequestAsync(vm, userId);
                    pr.ApprovalStatus = targetStatus;
                    // xmin 樂觀鎖的蓋章在 UpdatePurchaseRequestAsync 內部、寫入之前完成
                }
                await _context.SaveChangesAsync();

                if (Request.HasFormContentType && Request.Form["submit"] == "save_and_submit")
                {
                    var config = await _approvalService.GetApprovalConfig(DocType, plantId);
                    if (config?.ApprovalTemplateId == null)
                    {
                        pr.ApprovalStatus = DocumentApprovalStatus.Approved;
                    }
                    else
                    {
                        var approvalReq = await _approvalService.SubmitForApproval(pr.PurchaseRequestId, DocType, config.ApprovalTemplateId, userId);
                        pr.ApprovalRequestId = approvalReq.Id;
                        pr.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
                    }
                    await _context.SaveChangesAsync();
                }

                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>",
                    "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
        }

        // POST: /PurchaseRequest/Submit/{id} — 送審
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<PurchaseRequest>(id, MenuKey, DocType);

        // POST: /PurchaseRequest/Withdraw/{id} — 撤回
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<PurchaseRequest>(id, MenuKey);

        // POST: /PurchaseRequest/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

            try
            {
                await _purchaseService.DeletePurchaseRequestAsync(id);
                return Ok(new { success = true, message = _localizer["DeleteSuccess"].Value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // GET: /PurchaseRequest/Print/{id}
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.PurchaseRequests
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.PurchaseRequestId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.RequestedBy });
            await PreparePrintSignAsync(id, doc.RequestedBy);
            return View(doc);
        }

        // GET: /PurchaseRequest/Lines/{id} — 取得請購單明細（供 PO 畫面選入使用）
        [HttpGet]
        public async Task<IActionResult> Lines(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var plantId = _userContext.PlantId;
            var lines = await _context.PurchaseRequestLines
                .AsNoTracking()
                .Include(l => l.PurchaseRequest)
                .Where(l => l.PurchaseRequestId == id
                         && l.PurchaseRequest!.PlantId == plantId
                         && l.PurchaseRequest.ApprovalStatus == DocumentApprovalStatus.Approved
                         && !l.IsClosed
                         && l.RequestQty > l.OrderedQty)
                .OrderBy(l => l.LineNo)
                .Select(l => new
                {
                    l.LineNo,
                    l.ProductCode,
                    l.DeliveryDate,
                    l.RequestQty,
                    l.UomCode,
                    l.StockQty,
                    l.StockUomCode,
                    l.OrderedQty,
                    RemainingQty = l.RequestQty - l.OrderedQty,
                })
                .ToListAsync();

            return Json(lines);
        }

        // GET: /PurchaseRequest/AvailableLines — 取得所有可請購明細（供 PO Modal 多選使用）
        [HttpGet]
        public async Task<IActionResult> AvailableLines(string? searchTerm)
        {
            if (!await _permissionService.CanReadAny(MenuKey, "PurchaseOrder")) return Forbid();
            var plantId = _userContext.PlantId;

            // 動態計算：從 PO 明細加總已下單量（排除已拒絕的 PO）
            var orderedRaw = await _context.PurchaseOrderLines.AsNoTracking()
                .Where(ol => ol.PurchaseOrder!.PlantId == plantId
                    && ol.PurchaseOrder.ApprovalStatus != DocumentApprovalStatus.Rejected
                    && ol.PurchaseRequestId != null && ol.PrLineNo.HasValue)
                .GroupBy(ol => new { ol.PurchaseRequestId, ol.PrLineNo })
                .Select(g => new { g.Key.PurchaseRequestId, PrLineNo = g.Key.PrLineNo!.Value, Ordered = g.Sum(ol => ol.OrderQty) })
                .ToListAsync();
            var orderedMap = orderedRaw.ToDictionary(x => (x.PurchaseRequestId!, x.PrLineNo), x => x.Ordered);

            var query = _context.PurchaseRequestLines
                .AsNoTracking()
                .Include(l => l.PurchaseRequest)
                .Where(l => l.PurchaseRequest!.PlantId == plantId
                         && l.PurchaseRequest.ApprovalStatus == DocumentApprovalStatus.Approved
                         && !l.IsClosed);

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(l =>
                    l.PurchaseRequestId.ToLower().Contains(lower) ||
                    l.ProductCode.ToLower().Contains(lower));
            }

            var prLines = await query
                .OrderBy(l => l.PurchaseRequestId)
                .ThenBy(l => l.LineNo)
                .Select(l => new
                {
                    l.PurchaseRequestId, l.LineNo, l.ProductCode, l.DeliveryDate,
                    l.RequestQty, l.UomCode, l.StockQty, l.StockUomCode,
                })
                .Take(200)
                .ToListAsync();

            var lines = prLines
                .Select(l =>
                {
                    var ordered   = orderedMap.TryGetValue((l.PurchaseRequestId, l.LineNo), out var o) ? o : 0m;
                    var remaining = l.RequestQty - ordered;
                    return new
                    {
                        l.PurchaseRequestId, l.LineNo, l.ProductCode, l.DeliveryDate,
                        l.RequestQty, l.UomCode, l.StockQty, l.StockUomCode,
                        OrderedQty = ordered, RemainingQty = remaining,
                    };
                })
                .Where(l => l.RemainingQty > 0)
                .ToList();

            return Json(lines);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseDoc(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.PurchaseRequests.Include(d => d.Lines).FirstOrDefaultAsync(d => d.PurchaseRequestId == id);
            if (doc == null) return NotFound();
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["CloseErrNeedApproval"].Value });
            foreach (var l in doc.Lines.Where(l => !l.IsClosed))
                l.IsClosed = true;
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }
    }
}
