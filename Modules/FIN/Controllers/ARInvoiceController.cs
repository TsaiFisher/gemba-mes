using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.FIN;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.FIN.Controllers
{
    [Authorize]
    public class ARInvoiceController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IARInvoiceService _service;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IApprovalService _approvalService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "ARInvoice";

        public ARInvoiceController(
            ApplicationDbContext context,
            IARInvoiceService service,
            IUserContext userContext,
            IPermissionService permissionService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IApprovalService approvalService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _service = service;
            _userContext = userContext;
            _permissionService = permissionService;
            _dateTimeService = dateTimeService;
            _currencyService = currencyService;
            _approvalService = approvalService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(
            string? searchCustomer, string? searchStatus, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched       = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder      = sortOrder;
            ViewBag.CustomerSort   = sortOrder == "cust_asc"   ? "cust_desc"   : "cust_asc";
            ViewBag.DateSort       = sortOrder == "date_asc"   ? "date_desc"   : "date_asc";
            ViewBag.AmountSort     = sortOrder == "amount_asc" ? "amount_desc" : "amount_asc";
            ViewBag.SearchCustomer = searchCustomer;
            ViewBag.SearchStatus   = searchStatus;
            ViewBag.CanCreate      = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate      = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete      = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint       = await _permissionService.CanPrint(MenuKey);
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            if (!searched) return View((PaginatedList<ARInvoice>?)null);

            var query = _context.ARInvoices
                .Where(i => i.PlantId == _userContext.PlantId)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchCustomer))
                query = query.Where(i => i.CustomerCode == searchCustomer);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<ARInvoiceStatus>(searchStatus, out var st))
                query = query.Where(i => i.Status == st);

            query = sortOrder switch
            {
                "cust_asc"    => query.OrderBy(i => i.CustomerCode),
                "cust_desc"   => query.OrderByDescending(i => i.CustomerCode),
                "date_desc"   => query.OrderByDescending(i => i.InvoiceDate),
                "amount_asc"  => query.OrderBy(i => i.TotalAmount),
                "amount_desc" => query.OrderByDescending(i => i.TotalAmount),
                _             => query.OrderByDescending(i => i.InvoiceDate)
            };

            var list = await PaginatedList<ARInvoice>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            var invoice = await _context.ARInvoices
                .Include(i => i.Lines)
                .Include(i => i.Allocations)
                .FirstOrDefaultAsync(i => i.ARInvoiceId == id);
            if (invoice == null) return NotFound();
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_Form", invoice);
            return View("_Form", invoice);
        }

        // ── 手動建單（manual-test-plan.md §11 #9），與 AP 側對稱 ─────────────
        //
        // 用途是**沒有出貨的收入**（服務費、雜項收入）。明細只收科目、沒有品號：
        // 正常出貨開票走 StandardAccounts.SalesRevenueCode，手動開票若也掛品號，
        // 同一個成品會有兩條開票路徑落到兩個營收科目，5-6 報表對帳就對不起來。

        // GET: /ARInvoice/CreateManual
        [HttpGet]
        public async Task<IActionResult> CreateManual()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();

            var today = _dateTimeService.GetLocalDate();
            var vm = new ManualARInvoiceViewModel
            {
                InvoiceDate = today,
                DueDate = today.AddDays(30),
                CurrencyCode = await _currencyService.GetBaseCurrencyAsync(),
                Lines = { new ManualARInvoiceLineViewModel() },
            };
            await PrepareManualDropdownsAsync();
            if (IsAjax) return PartialView("_ManualForm", vm);
            return View("_ManualForm", vm);
        }

        // POST: /ARInvoice/CreateManual
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateManual(ManualARInvoiceViewModel vm)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();

            vm.Lines = vm.Lines.Where(l => !string.IsNullOrWhiteSpace(l.AccountCode)).ToList();
            if (vm.Lines.Count == 0)
                ModelState.AddModelError(nameof(vm.Lines), _localizer["ManualInvoiceNeedsLine"].Value);

            if (!ModelState.IsValid)
            {
                await PrepareManualDropdownsAsync();
                if (IsAjax) return PartialView("_ManualForm", vm);
                return View("_ManualForm", vm);
            }

            var currency = string.IsNullOrWhiteSpace(vm.CurrencyCode)
                ? await _currencyService.GetBaseCurrencyAsync() ?? ""
                : vm.CurrencyCode!;

            // AR 用**賣出**匯率（公司收款自外幣客戶），與 O2C 整條銷售鏈一致。
            // 取成買入會讓同一筆銷售有兩個本幣金額——§6.5 記過這個形狀。
            var rate = vm.ExchangeRate
                ?? await _currencyService.GetRateAsync(currency, vm.InvoiceDate, RateType.Sell)
                ?? 1m;

            var taxRates = await _context.Set<Modules.BAS.Models.Tax>().AsNoTracking()
                .ToDictionaryAsync(t => t.TaxCode, t => t.TaxRate);

            var invoice = new ARInvoice
            {
                CustomerCode = vm.CustomerCode,
                InvoiceDate  = vm.InvoiceDate,
                DueDate      = vm.DueDate,
                CurrencyCode = currency,
                ExchangeRate = rate,
                Remarks      = vm.Remarks,
                PlantId      = _userContext.PlantId,
            };

            var lineNo = 0;
            foreach (var l in vm.Lines)
            {
                var taxRate = !string.IsNullOrEmpty(l.TaxCode) && taxRates.TryGetValue(l.TaxCode, out var tr) ? tr : 0m;
                var lineAmount = await _currencyService.RoundAsync(l.Qty * l.UnitPrice, currency);
                // Tax.TaxRate 以**小數**存放（0.05 = 5%，見 BAS/Models/Tax.cs），
                // 不可再除以 100。ARInvoiceService.GenerateFromShipmentAsync 早就修掉這個
                // 「誤加 / 100 使稅額縮成百分之一」的算式，但兩支手動開票的控制器一直沒跟上——
                // 於是同一個稅別，自動開票 5%、手動開票 0.05%，而兩張傳票都借貸平衡。
                var taxAmount  = await _currencyService.RoundAsync(lineAmount * taxRate, currency);

                invoice.Lines.Add(new ARInvoiceLine
                {
                    LineNo      = ++lineNo,
                    AccountCode = l.AccountCode,
                    Remarks     = l.Description,
                    Qty         = l.Qty,
                    UnitPrice   = l.UnitPrice,
                    LineAmount  = lineAmount,
                    TaxCode     = l.TaxCode,
                    TaxRate     = taxRate,
                    TaxAmount   = taxAmount,
                });
            }

            invoice.TaxAmount   = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount = invoice.Lines.Sum(l => l.LineAmount + l.TaxAmount);
            invoice.AmountLocal = await _currencyService.RoundAsync(invoice.TotalAmount * rate, currency);

            var approvalStatus = await _approvalService.ResolveSaveStatusAsync("ARInvoice", _userContext.PlantId);

            try
            {
                var created = await _service.CreateManualAsync(invoice, approvalStatus);
                if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value, id = created.ARInvoiceId });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            catch (InvalidOperationException ex)
            {
                if (IsAjax) return Json(new { success = false, message = ex.Message });
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareManualDropdownsAsync();
                return View("_ManualForm", vm);
            }
        }

        private async Task PrepareManualDropdownsAsync()
        {
            ViewBag.CustomerOptions = await _context.Companies.AsNoTracking()
                .Where(c => c.IsActive && c.IsCustomer)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            // 只列收入類科目（4/7）且**可手動選用**——填成費用科目的傳票依然平衡、稽核也不會叫。
            var arTypes = new[]
            {
                Modules.BAS.Models.AccountType.Revenue,
                Modules.BAS.Models.AccountType.OtherIncome,
            };
            ViewBag.AccountOptions = await _context.Set<Modules.BAS.Models.ChartOfAccount>().AsNoTracking()
                .Where(a => a.IsActive && a.AllowManualEntry && arTypes.Contains(a.AccountType))
                .OrderBy(a => a.AccountCode)
                .Select(a => new { a.AccountCode, a.AccountName })
                .ToListAsync();

            ViewBag.TaxOptions = await _context.Set<Modules.BAS.Models.Tax>().AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.TaxCode)
                .Select(t => new { t.TaxCode, t.TaxName, t.TaxRate })
                .ToListAsync();

            ViewBag.Currencies = await _currencyService.GetActiveCurrenciesAsync();
            ViewBag.BaseCurrency = await _currencyService.GetBaseCurrencyAsync() ?? "";
        }


        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoVoidPermission"].Value });
            try
            {
                await _service.VoidAsync(id);
                return Json(new { success = true, message = _localizer["Voided"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>已作廢的自動發票依來源單重新開立（2026-10-22）。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reissue(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoPermission"].Value });
            try
            {
                var created = await _service.ReissueFromSourceAsync(id);
                return Json(new { success = true, message = string.Format(_localizer["InvoiceReissuedFmt"].Value, created.ARInvoiceId) });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // GET: /ARInvoice/Print/{id}（2026-10-27：應收發票）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.ARInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.ARInvoiceId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!);
            await PreparePrintSignAsync(id, null);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var cur = doc.CurrencyCode;
            var p = new PrintDocument
            {
                Title = L["Print_Title_ARInvoice"], DocNo = doc.ARInvoiceId,
                DateLabel = L["Print_InvoiceDate"], DateValue = doc.InvoiceDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Customer"], n.CompanyWith(doc.CustomerCode))
                .Field(L["Print_DueDate"], doc.DueDate.ToString("yyyy-MM-dd"))
                .Field(L["Print_Currency"], n.CurrencyWith(cur))
                .Field(L["Print_ExchangeRate"], doc.ExchangeRate.ToString("0.######"))
                .Field(L["Print_SourceDoc"], $"{doc.SourceDocType} {doc.SourceDocId}".Trim())
                .Field(L["Print_Status"], FinEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_Qty"], true).Col(L["Print_Uom"]).Col(L["Print_UnitPrice"], true)
                .Col(L["Print_Amount"], true).Col(L["Print_Tax"]).Col(L["Print_TaxAmount"], true);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.Qty, l.UomCode), n.UomWith(l.UomCode), f.Money(l.UnitPrice, cur),
                    f.Money(l.LineAmount, cur), n.TaxWith(l.TaxCode), f.Money(l.TaxAmount, cur));
            p.Tables.Add(tbl);
            p.Total($"{L["Print_Subtotal"]} {n.CurrencyWith(cur)}：{f.Money(doc.TotalAmount - doc.TaxAmount, cur)}")
             .Total($"{L["Print_TaxAmount"]} {n.CurrencyWith(cur)}：{f.Money(doc.TaxAmount, cur)}")
             .Total($"{L["Print_TotalAmount"]} {n.CurrencyWith(cur)}：{f.Money(doc.TotalAmount, cur)}")
             .Total($"{L["Print_LocalTotal"]}：{f.Local(doc.AmountLocal)}");
            return PrintDocumentView(p);
        }
    }
}
