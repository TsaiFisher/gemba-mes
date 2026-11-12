using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using System.Globalization;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    [Authorize]
    public class OutsourceReceiptController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly IInventoryPostingService _postingService;
        private readonly IPlantParamService _plantParams;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IAPInvoiceService _apInvoiceService;
        private readonly IExportService _exportService;
        private readonly IInventoryReservationService _reservations;
        private readonly IOutsourceReceiptSettlementService _settlementService;
        private readonly Modules.ACT.Services.IJournalEntryService _journalService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "OutsourceReceipt";
        private const string DocType = "OutsourceReceipt";

        public OutsourceReceiptController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService, IDocNumberService docNumberService,
            IInventoryPostingService postingService, IPlantParamService plantParams,
            IUomService uomService, ICurrencyService currencyService,
            IAPInvoiceService apInvoiceService, IExportService exportService,
            IInventoryReservationService reservations,
            IOutsourceReceiptSettlementService settlementService,
            Modules.ACT.Services.IJournalEntryService journalService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService; _docNumberService = docNumberService;
            _postingService = postingService; _plantParams = plantParams;
            _uomService = uomService; _currencyService = currencyService;
            _apInvoiceService = apInvoiceService;
            _exportService = exportService;
            _reservations = reservations;
            _settlementService = settlementService;
            _journalService = journalService;

            _localizer = localizer;
        }

        // 送審／確認／金額正規化的單一實作（手機收料也走它）。由本控制器既有的相依組出來而不是另外注入：
        // 既有單元測試與三支委外流程測試的建構式不必改。
        // ⚠️ 選配相依一定要從 RequestServices 帶進去：自動扣料（工序委外 C、B1 收貨自動扣送外料）缺
        // IMaterialSlipService／IPlantDefaultLocationResolver 會直接擲錯——原本只帶必要相依，桌機確認工序委外
        // 收貨一律失敗（2026-10-15 OutsourceSimplifiedFlowTests 揪出），手機走 DI 那一份所以沒事。
        // 沒有 HttpContext（單元測試）時全部是 null，服務自己退回內建預設，與原本相同。
        private OutsourceReceiptService? _receipts;
        private OutsourceReceiptService Receipts => _receipts ??= new OutsourceReceiptService(
            _context, _userContext, _approvalService, _docNumberService, _postingService, _plantParams,
            _uomService, _currencyService, _apInvoiceService, _reservations, _settlementService,
            _journalService, _localizer,
            overQty:   HttpContext?.RequestServices?.GetService<SmartFactory.MES.Modules.MFG.Services.IOverQuantityPolicy>(),
            progress:  HttpContext?.RequestServices?.GetService<SmartFactory.MES.Modules.MFG.Services.IOutsourceProgressService>(),
            slips:     HttpContext?.RequestServices?.GetService<SmartFactory.MES.Modules.MFG.Services.IMaterialSlipService>(),
            locations: HttpContext?.RequestServices?.GetService<SmartFactory.MES.Modules.BAS.Services.IPlantDefaultLocationResolver>(),
            iqc:       HttpContext?.RequestServices?.GetService<SmartFactory.MES.Modules.QCR.Services.IIqcInspectionService>());

        // GET: /OutsourceReceipt
        public async Task<IActionResult> Index(
            string? searchSupplier, string? searchOpo, string? searchStatus, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched       = searched;
            ViewBag.SortOrder      = sortOrder;
            ViewBag.SupplierSort   = sortOrder == "sup_asc" ? "sup_desc" : "sup_asc";
            ViewBag.DateSort       = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.SearchSupplier = searchSupplier;
            ViewBag.SearchOpo      = searchOpo;
            ViewBag.SearchStatus   = searchStatus;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CanCreate      = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate      = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete      = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint       = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();
            ViewBag.OpoOptions = await _context.OutsourceReceipts
                .AsNoTracking().Where(r => r.PlantId == _userContext.PlantId)
                .Select(r => r.OutsourcePurchaseOrderId)
                .Distinct().OrderByDescending(id => id)
                .ToListAsync();

            if (!searched) return View((PaginatedList<OutsourceReceipt>?)null);

            var query = _context.OutsourceReceipts
                .Where(r => r.PlantId == _userContext.PlantId)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchSupplier))
                query = query.Where(r => r.SupplierCode == searchSupplier);
            if (!string.IsNullOrWhiteSpace(searchOpo))
                query = query.Where(r => r.OutsourcePurchaseOrderId == searchOpo);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<DocStatus>(searchStatus, out var st))
                query = query.Where(r => r.Status == st);

            query = sortOrder switch
            {
                "sup_asc"   => query.OrderBy(r => r.SupplierCode),
                "sup_desc"  => query.OrderByDescending(r => r.SupplierCode),
                "date_asc"  => query.OrderBy(r => r.ReceiptDate),
                "date_desc" => query.OrderByDescending(r => r.ReceiptDate),
                _           => query.OrderByDescending(r => r.CreatedAt)
            };

            var list = await PaginatedList<OutsourceReceipt>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(list);
        }

        // GET: /OutsourceReceipt/Export
        [HttpGet]
        public async Task<IActionResult> Export(
            string? searchSupplier, string? searchOpo, string? searchStatus, string? sortOrder,
            string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var query = _context.OutsourceReceipts
                .Where(r => r.PlantId == _userContext.PlantId)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchSupplier))
                query = query.Where(r => r.SupplierCode == searchSupplier);
            if (!string.IsNullOrWhiteSpace(searchOpo))
                query = query.Where(r => r.OutsourcePurchaseOrderId == searchOpo);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<DocStatus>(searchStatus, out var st))
                query = query.Where(r => r.Status == st);

            query = sortOrder switch
            {
                "sup_asc"   => query.OrderBy(r => r.SupplierCode),
                "sup_desc"  => query.OrderByDescending(r => r.SupplierCode),
                "date_asc"  => query.OrderBy(r => r.ReceiptDate),
                "date_desc" => query.OrderByDescending(r => r.ReceiptDate),
                _           => query.OrderByDescending(r => r.CreatedAt)
            };

            var data = await query.ToListAsync();
            var isZh = CultureInfo.CurrentUICulture.Name != "en-US";
            var localNow = _dateTimeService.Now;

            var req = new ExportRequest
            {
                Title = "OutsourceReceipt",
                Headers = isZh
                    ? ["收貨單號", "供應商", "委外採購單", "收貨日期", "總金額", "狀態"]
                    : ["Receipt No", "Supplier", "Outsource PO", "Receipt Date", "Total Amount", "Status"],
                Rows = data.Select(r => new object?[]
                {
                    r.OutsourceReceiptId, r.SupplierCode, r.OutsourcePurchaseOrderId,
                    r.ReceiptDate.ToString("yyyy-MM-dd"), r.TotalAmount, r.Status.ToString()
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"OutsourceReceipt_{localNow:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"OutsourceReceipt_{localNow:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"OutsourceReceipt_{_userContext.PlantId}_{localNow:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // GET: /OutsourceReceipt/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareDropdowns();
            var vm = new OutsourceReceipt
            {
                PlantId     = _userContext.PlantId,
                ReceiptDate = DateOnly.FromDateTime(_dateTimeService.Now),
                CreatedBy   = _userContext.UserId ?? "",
            };
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        // GET: /OutsourceReceipt/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var vm = await _context.OutsourceReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (vm == null) return NotFound();
            if (vm.Status != DocStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["DraftOnlyCanEdit"].Value });
            await PrepareDropdowns();
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        // GET: /OutsourceReceipt/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            var vm = await _context.OutsourceReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (vm == null) return NotFound();
            await PrepareDropdowns();
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        // POST: /OutsourceReceipt/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(OutsourceReceipt vm)
        {
            // 單號與建立者由伺服端指派：新增時表單帶空字串，非可空字串屬性會被隱含 [Required]
            // 擋下（ModelState 恆 invalid、面板靜默重渲染建不出單），故先移除其驗證。
            ModelState.Remove(nameof(OutsourceReceipt.OutsourceReceiptId));
            ModelState.Remove(nameof(OutsourceReceipt.CreatedBy));

            // 明細的 parent FK（OutsourceReceiptId）同屬「非可空字串 + 伺服端才賦值」的隱含必填縫：
            // 新建時前端 addLineRow 不渲染 Lines[i].OutsourceReceiptId，binder 仍會對每個明細列的
            // 空值加隱含必填錯誤，同樣讓 ModelState 恆 invalid、靜默重繪建不出單（實機 smoke 揪出，
            // 表頭移除後仍卡在這層）。逐列移除其驗證，parent FK 於下方存檔時才由伺服端賦值。
            for (int i = 0; i < (vm.Lines?.Count ?? 0); i++)
                ModelState.Remove($"Lines[{i}].{nameof(OutsourceReceiptLine.OutsourceReceiptId)}");

            if (!ModelState.IsValid)
            {
                await PrepareDropdowns();
                return PartialView("_Form", vm);
            }

            var isNew = string.IsNullOrEmpty(vm.OutsourceReceiptId);

            if (isNew)
            {
                if (!await _permissionService.CanCreate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

                // 驗證委外採購單存在
                var opoExists = await _context.OutsourcePurchaseOrders
                    .AnyAsync(o => o.OutsourcePurchaseOrderId == vm.OutsourcePurchaseOrderId && o.PlantId == _userContext.PlantId);
                if (!opoExists)
                    return Json(new { success = false, message = string.Format(_localizer["OutsourcePONotFound"].Value, vm.OutsourcePurchaseOrderId) });

                vm.OutsourceReceiptId = await _docNumberService.NextAsync("OutsourceReceipt", _userContext.PlantId ?? "");
                vm.PlantId     = _userContext.PlantId;
                vm.CreatedBy   = _userContext.UserId ?? "";
                vm.Status      = DocStatus.Draft;
                vm.CreatedAt   = DateTime.UtcNow;
                vm.UpdatedAt   = DateTime.UtcNow;
                vm.ApprovalStatus = DocumentApprovalStatus.Draft;

                // 單位換算缺主檔是「主檔沒建好」而不是「系統壞了」，要讓維護人員在面板上看見
                // 訊息（含品號與兩個單位），不能讓它變成一頁 500。
                try { await Receipts.NormalizeLinesAsync(vm.OutsourcePurchaseOrderId, vm.Lines); }
                catch (UomConversionNotFoundException ex)
                {
                    await PrepareDropdowns();
                    ModelState.AddModelError(string.Empty, ex.Message);
                    return PartialView("_Form", vm);
                }
                vm.TotalAmount = await Receipts.ApplyLocalAmountsAsync(vm.Lines, vm.ExchangeRate, vm.CurrencyCode);

                _context.OutsourceReceipts.Add(vm);
            }
            else
            {
                if (!await _permissionService.CanUpdate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

                var existing = await _context.OutsourceReceipts.Include(r => r.Lines)
                    .FirstOrDefaultAsync(r => r.OutsourceReceiptId == vm.OutsourceReceiptId);
                if (existing == null) return NotFound();
                if (existing.Status != DocStatus.Draft)
                    return Json(new { success = false, message = _localizer["DraftOnlyCanEdit"].Value });

                // xmin 樂觀鎖：以前端帶回的版本作為 OriginalValue（見 ord-module.md §13）
                _context.Entry(existing).Property(e => e.xmin).OriginalValue = vm.xmin;

                existing.ReceiptDate             = vm.ReceiptDate;
                existing.SupplierCode            = vm.SupplierCode;
                existing.OutsourcePurchaseOrderId = vm.OutsourcePurchaseOrderId;
                existing.CurrencyCode            = vm.CurrencyCode;
                existing.ExchangeRate            = vm.ExchangeRate;
                existing.Remarks                 = vm.Remarks;
                existing.IsFinalReceipt          = vm.IsFinalReceipt;
                existing.UpdatedAt               = DateTime.UtcNow;

                // 更新明細：清除後重建
                // RemoveRange 僅標記舊行為 Deleted，不會從 existing.Lines 記憶體集合移除；
                // 若不 Clear，下方 TotalAmount 加總會把殘留的舊行金額一併算入。
                _context.OutsourceReceiptLines.RemoveRange(existing.Lines);
                existing.Lines.Clear();
                foreach (var l in vm.Lines)
                {
                    l.OutsourceReceiptId = existing.OutsourceReceiptId;
                    existing.Lines.Add(l);
                }

                try { await Receipts.NormalizeLinesAsync(existing.OutsourcePurchaseOrderId, existing.Lines); }
                catch (UomConversionNotFoundException ex)
                {
                    await PrepareDropdowns();
                    ModelState.AddModelError(string.Empty, ex.Message);
                    return PartialView("_Form", vm);
                }
                existing.TotalAmount = await Receipts.ApplyLocalAmountsAsync(existing.Lines, existing.ExchangeRate, existing.CurrencyCode);
            }

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await PrepareDropdowns();
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                return PartialView("_Form", vm);
            }
            return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
        }

        // POST: /OutsourceReceipt/Submit/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.OutsourceReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (rec == null) return NotFound();
            if (rec.Status != DocStatus.Draft)
                return Json(new { success = false, message = _localizer["DraftOnlyCanSubmit"].Value });
            if (!rec.Lines.Any())
                return Json(new { success = false, message = _localizer["AddLineFirst"].Value });

            var approved = await Receipts.SubmitAsync(id);
            return Json(new { success = true, message = _localizer[approved ? "StagingApprovedNoWorkflow" : "StagingSubmitted"].Value });
        }

        // POST: /OutsourceReceipt/Withdraw/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.OutsourceReceipts.FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (rec == null) return NotFound();
            await _approvalService.WithdrawRequest(rec.ApprovalRequestId ?? "", _userContext.UserId ?? "");
            rec.ApprovalStatus = DocumentApprovalStatus.Draft;
            rec.UpdatedAt      = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        // POST: /OutsourceReceipt/Confirm/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoConfirmPermission"].Value });

            var rec = await _context.OutsourceReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (rec == null) return NotFound();
            if (rec.Status != DocStatus.Draft)
                return Json(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (rec.ApprovalStatus != DocumentApprovalStatus.Approved)
                return Json(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!rec.Lines.Any())
                return Json(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

            try
            {
                var outcome = await Receipts.ConfirmAsync(id);
                var message = _localizer[outcome == ReceiptOutcome.Posted ? "PostedSimple" : "ConfirmedAwaitingWarehouse"].Value;
                return Json(new { success = true, message });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Json(new { success = false, message = _localizer["ConcurrencyConflict"].Value });
            }
        }

        // POST: /OutsourceReceipt/Void/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoVoidPermission"].Value });
            var rec = await _context.OutsourceReceipts.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (rec == null) return NotFound();
            if (rec.Status == DocStatus.Posted)
                return Json(new { success = false, message = _localizer["AlreadyPostedNeedReverse"].Value });

            // Confirm 當下就已自動產生應付發票，作廢若只改收貨單狀態，那張發票會變成
            // 掛在作廢單上的孤兒——供應商對帳多出一筆、AP 未清餘額永遠掛帳。
            // 已付款的發票不能作廢，此時整個作廢動作必須擋下，不能只作廢收貨單。
            var apInvoice = await _context.APInvoices
                .FirstOrDefaultAsync(i => i.SourceDocType == "OutsourceReceipt"
                                       && i.SourceDocId == id
                                       && i.Status != APInvoiceStatus.Void);
            if (apInvoice != null && apInvoice.Status != APInvoiceStatus.Open)
                return Json(new { success = false, message = _localizer["OutsourceVoidBlockedByPaidInvoice"].Value });

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                if (apInvoice != null)
                    await _apInvoiceService.VoidAsync(apInvoice.APInvoiceId);   // 連同認列分錄回沖

                await Receipts.ApplyOutsourceSupplyPegAsync(rec, convert: false);   // §24.9(3) 回退：在手→在途
                rec.Status    = DocStatus.Void;
                rec.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            return Json(new { success = true, message = _localizer["Voided"].Value });
        }

        // POST: /OutsourceReceipt/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoDeletePermission"].Value });
            var rec = await _context.OutsourceReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.OutsourceReceiptId == id);
            if (rec == null) return NotFound();
            if (rec.Status != DocStatus.Draft)
                return Json(new { success = false, message = _localizer["StagingErrDraftToDelete"].Value });
            _context.OutsourceReceipts.Remove(rec);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // GET: 取得指定委外採購單的未結明細，供建立收貨時選入
        [HttpGet]
        public async Task<IActionResult> GetOpoLines(string opoId)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var lines = await _context.OutsourcePurchaseOrderLines
                .Where(l => l.OutsourcePurchaseOrderId == opoId)
                // 單價必須帶出來：這是委外報價 → OPO 一路談好的加工費，
                // 回 null 的話畫面預設 0，作業員不自己翻單重打就是應付 0、成品吸收 0 加工費，
                // 而且沒有任何提示（見 W2-F9）。
                .Select(l => new {
                    l.LineNo, l.ProductCode, l.Qty, l.UomCode, unitCost = (decimal?)l.UnitPrice
                })
                .AsNoTracking()
                .ToListAsync();
            return Json(lines);
        }

        private async Task PrepareDropdowns()
        {
            ViewBag.Suppliers = await _context.Companies
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .AsNoTracking().ToListAsync();

            ViewBag.OpenOpos = await _context.OutsourcePurchaseOrders
                .Where(o => o.PlantId == _userContext.PlantId
                         && o.Status == SmartFactory.MES.Modules.MFG.Models.OutsourcePoStatus.Confirmed)
                .OrderByDescending(o => o.OrderDate)
                .AsNoTracking().ToListAsync();

            ViewBag.CurrencyOptions = await _context.Currencies
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { Code = c.CurrencyCode, Name = $"{c.CurrencyCode} — {c.CurrencyName}" })
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

        // GET: /OutsourceReceipt/Print/{id}
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.OutsourceReceipts.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.OutsourceReceiptId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!, new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var p = new PrintDocument
            {
                Title = L["Print_Title_OutsourceReceipt"], DocNo = doc.OutsourceReceiptId,
                DateLabel = L["ReceiptDate"], DateValue = doc.ReceiptDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Supplier"], n.CompanyWith(doc.SupplierCode))
                .Field(L["OutsourcePurchaseOrderNo"], doc.OutsourcePurchaseOrderId)
                .Field(L["Print_TxnType"], n.TxTypeWith(doc.TypeCode))
                .Field(L["Print_Currency"], n.CurrencyWith(doc.CurrencyCode))
                .Field(L["ExchangeRate"], doc.ExchangeRate.ToString("0.######"))
                .Field(L["IsFinalReceipt"], doc.IsFinalReceipt ? L["Print_Yes"] : L["Print_No"])
                .Field(L["Print_Status"], DocEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
                .Field(L["Print_CreatedAt"], PrintTime(doc.CreatedAt))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["ReceiptQty"], true).Col(L["Print_Uom"]).Col(L["UnitCost"], true)
                .Col(L["Print_Amount"], true).Col(L["Print_Tax"]).Col(L["Print_TaxAmount"], true).Col(L["Print_TotalAmount"], true)
                .Col(L["Print_Warehouse"]).Col(L["Print_Location"]).Col(L["Print_LotNo"]);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.ReceiptQty, l.UomCode), n.UomWith(l.UomCode), f.Money(l.UnitCost, l.CurrencyCode ?? doc.CurrencyCode),
                    f.Money(l.LineAmount, l.CurrencyCode ?? doc.CurrencyCode), n.TaxWith(l.TaxCode),
                    f.Money(l.TaxAmount, l.CurrencyCode ?? doc.CurrencyCode), f.Money(l.TotalAmount, l.CurrencyCode ?? doc.CurrencyCode),
                    n.WarehouseWith(l.WhCode), n.LocationWith(l.LocationCode), l.LotNo);
            p.Tables.Add(tbl);
            p.Total($"{L["Print_Total"]} {n.CurrencyWith(doc.CurrencyCode)}：{f.Money(doc.Lines.Sum(x => x.TotalAmount), doc.CurrencyCode)}");
            return PrintDocumentView(p);
        }
    }
}
