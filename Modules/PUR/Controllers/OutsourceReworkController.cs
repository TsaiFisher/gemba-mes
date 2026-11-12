using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using System.Globalization;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.PUR;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    /// <summary>
    /// 委外重工單（Phase F，pur-domain.md §4.2）。
    /// 單頭無明細；狀態流轉 Draft→Sent→Received，Draft/Sent 可作廢。
    /// 成本/索賠依責任歸屬雙路徑（OutsourceReworkCostCalculator）於儲存時計算。
    /// </summary>
    [Authorize]
    public class OutsourceReworkController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IDocNumberService _docNumberService;
        private readonly ICurrencyService _currencyService;
        private readonly IExportService _exportService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "OutsourceRework";
        private const string DocType = "OutsourceRework";

        public OutsourceReworkController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IDocNumberService docNumberService, ICurrencyService currencyService,
            IExportService exportService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _docNumberService = docNumberService; _currencyService = currencyService;
            _exportService = exportService;
            _localizer = localizer;
        }

        // GET: /OutsourceRework
        public async Task<IActionResult> Index(
            string? searchDispatch, string? searchNcr, string? searchStatus, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.DateSort        = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.QtySort         = sortOrder == "qty_asc"  ? "qty_desc"  : "qty_asc";
            ViewBag.SearchDispatch  = searchDispatch;
            ViewBag.SearchNcr       = searchNcr;
            ViewBag.SearchStatus    = searchStatus;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CanCreate       = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate       = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete       = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);

            if (!searched)
            {
                await PrepareSearchDropdowns();
                return View((PaginatedList<OutsourceRework>?)null);
            }

            var list = await PaginatedList<OutsourceRework>.CreateAsync(
                BuildQuery(searchDispatch, searchNcr, searchStatus, sortOrder),
                pageNumber ?? 1, pageSize ?? 20);
            await PrepareSearchDropdowns();
            return View(list);
        }

        // GET: /OutsourceRework/Export
        [HttpGet]
        public async Task<IActionResult> Export(
            string? searchDispatch, string? searchNcr, string? searchStatus, string? sortOrder,
            string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var data = await BuildQuery(searchDispatch, searchNcr, searchStatus, sortOrder).ToListAsync();
            var isZh = CultureInfo.CurrentUICulture.Name != "en-US";
            var localNow = _dateTimeService.Now;

            var req = new ExportRequest
            {
                Title = "OutsourceRework",
                Headers = isZh
                    ? ["重工單號", "原始派工", "重工委外單", "來源NCR", "重工數量", "責任歸屬", "廠商占比%", "向廠商扣款", "重工加工費", "狀態"]
                    : ["Rework No", "Original Dispatch", "Rework OPO", "Source NCR", "Rework Qty", "Liability", "Vendor %", "Chargeback", "Processing Fee", "Status"],
                Rows = data.Select(r => new object?[]
                {
                    r.OutsourceReworkId, r.OriginalDispatchId, r.OutsourcePurchaseOrderId, r.SourceNcrId,
                    r.ReworkQty, r.LiabilityParty.ToString(), r.VendorLiablePct,
                    r.VendorChargebackAmt, r.ReworkProcessingFee, r.Status.ToString()
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"OutsourceRework_{localNow:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"OutsourceRework_{localNow:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"OutsourceRework_{_userContext.PlantId}_{localNow:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // GET: /OutsourceRework/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new OutsourceRework
            {
                PlantId   = _userContext.PlantId,
                CreatedBy = _userContext.UserId ?? "",
            };
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        // GET: /OutsourceRework/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var vm = await _context.OutsourceReworks.FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (vm == null) return NotFound();
            if (vm.Status != OutsourceReworkStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["DraftOnlyCanEdit"].Value });
            await PrepareFormDropdowns();
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        // GET: /OutsourceRework/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var vm = await _context.OutsourceReworks.FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (vm == null) return NotFound();
            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        // POST: /OutsourceRework/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(OutsourceRework vm)
        {
            // CreatedBy/Status/計算結果欄由 Controller 指派，不在表單內
            // 單號由伺服端採番：新增時表單帶空字串，非可空字串屬性會被隱含 [Required] 擋下
            // （ModelState 恆 invalid、面板靜默重渲染建不出單），故先移除其驗證。
            ModelState.Remove(nameof(OutsourceRework.OutsourceReworkId));
            ModelState.Remove(nameof(OutsourceRework.CreatedBy));
            ModelState.Remove(nameof(OutsourceRework.Status));
            ModelState.Remove(nameof(OutsourceRework.VendorChargebackAmt));
            ModelState.Remove(nameof(OutsourceRework.ReworkProcessingFee));

            if (vm.ReworkQty <= 0)
                ModelState.AddModelError(nameof(OutsourceRework.ReworkQty), _localizer["ReworkQtyMustBePositive"].Value);
            if (vm.LiabilityParty == ReworkLiabilityParty.Shared && vm.VendorLiablePct is < 0m or > 100m)
                ModelState.AddModelError(nameof(OutsourceRework.VendorLiablePct), _localizer["VendorLiablePctRange"].Value);
            if (vm.ReworkCost < 0)
                ModelState.AddModelError(nameof(OutsourceRework.ReworkCost), _localizer["AmountMustBeNonNegative"].Value);
            if (vm.ProcessingFee < 0)
                ModelState.AddModelError(nameof(OutsourceRework.ProcessingFee), _localizer["AmountMustBeNonNegative"].Value);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }

            // 跨模組引用（僅字串 FK）應用層驗證：填了就必須存在且屬本廠
            if (!string.IsNullOrWhiteSpace(vm.OriginalDispatchId) &&
                !await _context.Dispatches.AnyAsync(d => d.DispatchId == vm.OriginalDispatchId && d.PlantId == _userContext.PlantId))
                return Json(new { success = false, message = string.Format(_localizer["DispatchNotFound"].Value, vm.OriginalDispatchId) });

            if (!string.IsNullOrWhiteSpace(vm.OutsourcePurchaseOrderId) &&
                !await _context.OutsourcePurchaseOrders.AnyAsync(o => o.OutsourcePurchaseOrderId == vm.OutsourcePurchaseOrderId && o.PlantId == _userContext.PlantId))
                return Json(new { success = false, message = string.Format(_localizer["OutsourcePONotFound"].Value, vm.OutsourcePurchaseOrderId) });

            if (!string.IsNullOrWhiteSpace(vm.SourceNcrId) &&
                !await _context.NonConformanceReports.AnyAsync(n => n.NcrId == vm.SourceNcrId && n.PlantId == _userContext.PlantId))
                return Json(new { success = false, message = string.Format(_localizer["NcrNotFound"].Value, vm.SourceNcrId) });

            // 成本模型：依責任歸屬計算雙路徑金額（本位幣小數位）
            var dp = await ResolveBaseCurrencyDpAsync();
            var cost = OutsourceReworkCostCalculator.Compute(
                vm.LiabilityParty, vm.VendorLiablePct, vm.ReworkCost, vm.ProcessingFee, dp);

            var isNew = string.IsNullOrEmpty(vm.OutsourceReworkId);
            if (isNew)
            {
                if (!await _permissionService.CanCreate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

                vm.OutsourceReworkId = await _docNumberService.NextAsync(DocType, _userContext.PlantId ?? "");
                vm.PlantId   = _userContext.PlantId;
                vm.CreatedBy = _userContext.UserId ?? "";
                vm.Status    = OutsourceReworkStatus.Draft;
                vm.CreatedAt = DateTime.UtcNow;
                vm.UpdatedAt = DateTime.UtcNow;
                (vm.VendorLiablePct, vm.VendorChargebackAmt, vm.ReworkProcessingFee) = cost;

                _context.OutsourceReworks.Add(vm);
            }
            else
            {
                if (!await _permissionService.CanUpdate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

                var existing = await _context.OutsourceReworks
                    .FirstOrDefaultAsync(r => r.OutsourceReworkId == vm.OutsourceReworkId);
                if (existing == null) return NotFound();
                if (existing.Status != OutsourceReworkStatus.Draft)
                    return Json(new { success = false, message = _localizer["DraftOnlyCanEdit"].Value });

                existing.OriginalDispatchId       = vm.OriginalDispatchId;
                existing.OutsourcePurchaseOrderId = vm.OutsourcePurchaseOrderId;
                existing.SourceNcrId              = vm.SourceNcrId;
                existing.ReworkQty                = vm.ReworkQty;
                existing.ReworkReason             = vm.ReworkReason;
                existing.LiabilityParty           = vm.LiabilityParty;
                existing.ReworkCost               = vm.ReworkCost;
                existing.ProcessingFee            = vm.ProcessingFee;
                (existing.VendorLiablePct, existing.VendorChargebackAmt, existing.ReworkProcessingFee) = cost;
                existing.Remarks                  = vm.Remarks;
                existing.UpdatedAt                = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
        }

        // POST: /OutsourceRework/Send/{id} — Draft→Sent（送回委外廠重工）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            var rec = await _context.OutsourceReworks.FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (rec == null) return NotFound();
            if (rec.Status != OutsourceReworkStatus.Draft)
                return Json(new { success = false, message = _localizer["ReworkOnlyDraftCanSend"].Value });

            rec.Status    = OutsourceReworkStatus.Sent;
            rec.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["ReworkSent"].Value });
        }

        // POST: /OutsourceRework/Receive/{id} — Sent→Received（重工品收回）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Receive(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            var rec = await _context.OutsourceReworks.FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (rec == null) return NotFound();
            if (rec.Status != OutsourceReworkStatus.Sent)
                return Json(new { success = false, message = _localizer["ReworkOnlySentCanReceive"].Value });

            rec.Status    = OutsourceReworkStatus.Received;
            rec.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["ReworkReceived"].Value });
        }

        // POST: /OutsourceRework/Void/{id} — Draft/Sent→Void
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoVoidPermission"].Value });
            var rec = await _context.OutsourceReworks.FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (rec == null) return NotFound();
            if (rec.Status is not (OutsourceReworkStatus.Draft or OutsourceReworkStatus.Sent))
                return Json(new { success = false, message = _localizer["ReworkReceivedCannotVoid"].Value });

            rec.Status    = OutsourceReworkStatus.Void;
            rec.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["Voided"].Value });
        }

        // POST: /OutsourceRework/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoDeletePermission"].Value });
            var rec = await _context.OutsourceReworks.FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (rec == null) return NotFound();
            if (rec.Status != OutsourceReworkStatus.Draft)
                return Json(new { success = false, message = _localizer["StagingErrDraftToDelete"].Value });
            _context.OutsourceReworks.Remove(rec);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        private IQueryable<OutsourceRework> BuildQuery(
            string? searchDispatch, string? searchNcr, string? searchStatus, string? sortOrder)
        {
            var query = _context.OutsourceReworks
                .Where(r => r.PlantId == _userContext.PlantId)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchDispatch))
                query = query.Where(r => r.OriginalDispatchId == searchDispatch);
            if (!string.IsNullOrWhiteSpace(searchNcr))
                query = query.Where(r => r.SourceNcrId == searchNcr);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<OutsourceReworkStatus>(searchStatus, out var st))
                query = query.Where(r => r.Status == st);

            return sortOrder switch
            {
                "date_asc"  => query.OrderBy(r => r.CreatedAt),
                "date_desc" => query.OrderByDescending(r => r.CreatedAt),
                "qty_asc"   => query.OrderBy(r => r.ReworkQty),
                "qty_desc"  => query.OrderByDescending(r => r.ReworkQty),
                _           => query.OrderByDescending(r => r.CreatedAt)
            };
        }

        private async Task PrepareSearchDropdowns()
        {
            ViewBag.DispatchOptions = await _context.Dispatches
                .Where(d => d.PlantId == _userContext.PlantId && d.IsOutsource)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => d.DispatchId)
                .AsNoTracking().ToListAsync();

            ViewBag.NcrOptions = await _context.NonConformanceReports
                .Where(n => n.PlantId == _userContext.PlantId)
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => n.NcrId)
                .AsNoTracking().ToListAsync();
        }

        private async Task PrepareFormDropdowns()
        {
            await PrepareSearchDropdowns();

            ViewBag.OpoOptions = await _context.OutsourcePurchaseOrders
                .Where(o => o.PlantId == _userContext.PlantId)
                .OrderByDescending(o => o.OrderDate)
                .Select(o => new { o.OutsourcePurchaseOrderId, o.SupplierCode })
                .AsNoTracking().ToListAsync();

            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        // GET: /OutsourceRework/Print/{id}
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.OutsourceReworks.AsNoTracking().FirstOrDefaultAsync(r => r.OutsourceReworkId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!, new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            // 重工單本身沒有品號：品號、廠商取自原派工的工單與委外採購單
            var productCode = doc.OriginalDispatchId == null ? null : await Db.Dispatches.AsNoTracking()
                .Where(d => d.DispatchId == doc.OriginalDispatchId)
                .Select(d => d.Operation!.WorkOrder!.ProductCode).FirstOrDefaultAsync();
            var supplierCode = doc.OutsourcePurchaseOrderId == null ? null : await Db.OutsourcePurchaseOrders.AsNoTracking()
                .Where(o => o.OutsourcePurchaseOrderId == doc.OutsourcePurchaseOrderId)
                .Select(o => o.SupplierCode).FirstOrDefaultAsync();
            var n = await PrintNames.ForAsync(doc, new { ProductCode = productCode, SupplierCode = supplierCode });
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var liability = doc.LiabilityParty switch
            {
                ReworkLiabilityParty.Vendor => L["LiabilityVendor"].Value,
                ReworkLiabilityParty.Us     => L["LiabilityUs"].Value,
                _                           => L["LiabilityShared"].Value,
            };
            var p = new PrintDocument
            {
                Title = L["Print_Title_OutsourceRework"], DocNo = doc.OutsourceReworkId,
                DateLabel = L["Print_CreatedAt"], DateValue = PrintTime(doc.CreatedAt),
            }
                .Field(L["Print_Supplier"], n.CompanyWith(supplierCode))
                .Field(L["Print_ProductCode"], productCode)
                .Field(L["Print_ProductName"], n.Product(productCode))
                .Field(L["OriginalDispatch"], doc.OriginalDispatchId)
                .Field(L["OutsourcePurchaseOrderNo"], doc.OutsourcePurchaseOrderId)
                .Field(L["SourceNcr"], doc.SourceNcrId)
                .Field(L["ReworkQty"], doc.ReworkQty.ToString("0.####"))
                .Field(L["ReworkReason"], doc.ReworkReason)
                .Field(L["LiabilityParty"], liability)
                .Field(L["VendorLiablePct"], doc.LiabilityParty == ReworkLiabilityParty.Shared ? doc.VendorLiablePct.ToString("0.##") + "%" : null)
                .Field(L["ReworkCost"], f.Local(doc.ReworkCost))
                .Field(L["ProcessingFee"], f.Local(doc.ProcessingFee))
                .Field(L["VendorChargebackAmt"], f.Local(doc.VendorChargebackAmt))
                .Field(L["ReworkProcessingFee"], f.Local(doc.ReworkProcessingFee))
                .Field(L["Print_Status"], PurEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
                .Field(L["Print_Remarks"], doc.Remarks);
            return PrintDocumentView(p);
        }
    }
}
