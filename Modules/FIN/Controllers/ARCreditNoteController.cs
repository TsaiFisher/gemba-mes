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
    /// <summary>
    /// 銷貨折讓單（W3-F3 Phase 2）。
    ///
    /// **刻意沒有建立與編輯**：折讓單的每一個數字都來自「哪一張退貨單、退了哪一張發票的哪一行、
    /// 退了多少」，全部由 <c>ARCreditNoteService.CreateDraftFromSalesReturnAsync</c> 在退貨過帳當下算出來。
    /// 開放手鍵等於讓人可以折一個與退貨量對不上的金額，而那正是
    /// <c>RETURN_SUBLEDGER_UNREVERSED</c> 在比的東西——手鍵的折讓沖不掉稽核，只會讓帳更難對。
    /// 需要折讓一筆與退貨無關的金額時，正確入口是手動 AR 發票（負向情境另議），不是這裡。
    ///
    /// 因此本畫面只有三件事：看、確認、作廢。
    /// </summary>
    [Authorize]
    public class ARCreditNoteController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IARCreditNoteService _service;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "ARCreditNote";

        public ARCreditNoteController(
            ApplicationDbContext context,
            IARCreditNoteService service,
            IUserContext userContext,
            IPermissionService permissionService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _service = service; _userContext = userContext;
            _permissionService = permissionService; _currencyService = currencyService;
            _localizer = localizer;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        public async Task<IActionResult> Index(
            string? searchCustomer, string? searchStatus, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表守 CanRead：選單本身是用 CanRead 過濾的，守 CanCreate 會讓
            // 「有讀取權、沒有建立權」的角色看得到選單、點下去 403（同 APInvoice 的前科）。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            await PrepareIndexPermissionsAsync(MenuKey);
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.CustomerSort    = sortOrder == "customer_asc" ? "customer_desc" : "customer_asc";
            ViewBag.DateSort        = sortOrder == "date_asc"     ? "date_desc"     : "date_asc";
            ViewBag.AmountSort      = sortOrder == "amount_asc"   ? "amount_desc"   : "amount_asc";
            ViewBag.SearchCustomer  = searchCustomer;
            ViewBag.SearchStatus    = searchStatus;
            ViewBag.BaseCurrencyDp  = await ResolveBaseCurrencyDpAsync();
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();
            ViewBag.CustomerNames = await _context.Companies.AsNoTracking()
                .ToDictionaryAsync(c => c.CompanyCode, c => c.CompanyName);

            if (!searched) return View((PaginatedList<ARCreditNote>?)null);

            var query = _context.ARCreditNotes.AsNoTracking().Include(n => n.Lines).AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchCustomer))
                query = query.Where(n => n.CustomerCode == searchCustomer);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<CreditNoteStatus>(searchStatus, out var st))
                query = query.Where(n => n.Status == st);

            query = sortOrder switch
            {
                "customer_asc"  => query.OrderBy(n => n.CustomerCode),
                "customer_desc" => query.OrderByDescending(n => n.CustomerCode),
                "date_asc"      => query.OrderBy(n => n.NoteDate),
                "amount_asc"    => query.OrderBy(n => n.TotalAmount),
                "amount_desc"   => query.OrderByDescending(n => n.TotalAmount),
                _               => query.OrderByDescending(n => n.NoteDate),
            };

            return View(await PaginatedList<ARCreditNote>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var note = await _context.ARCreditNotes.AsNoTracking()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.ARCreditNoteId == id);
            if (note == null) return NotFound();

            ViewBag.IsReadOnly     = true;
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.CanUpdate      = await _permissionService.CanUpdate(MenuKey);
            return IsAjax ? PartialView("_Form", note) : View("_Form", note);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return JsonFail(_localizer["NoPermission"].Value);
            try
            {
                await _service.ConfirmAsync(id);
                return JsonOk(message: _localizer["Posted"].Value);
            }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
            catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return JsonFail(_localizer["NoVoidPermission"].Value);
            try
            {
                await _service.VoidAsync(id, reason);
                return JsonOk(message: _localizer["Voided"].Value);
            }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
            catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }
        }

        // GET: /ARCreditNote/Print/{id}（2026-10-27：銷貨折讓單）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.ARCreditNotes.AsNoTracking().Include(c => c.Lines)
                .FirstOrDefaultAsync(c => c.ARCreditNoteId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!, new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var cur = doc.CurrencyCode;
            var p = new PrintDocument
            {
                Title = L["Print_Title_ARCreditNote"], DocNo = doc.ARCreditNoteId,
                DateLabel = L["Print_DocDate"], DateValue = doc.NoteDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Customer"], n.CompanyWith(doc.CustomerCode))
                .Field(L["Print_Currency"], n.CurrencyWith(cur))
                .Field(L["Print_ExchangeRate"], doc.ExchangeRate.ToString("0.######"))
                .Field(L["Print_SourceDoc"], $"{doc.SourceDocType} {doc.SourceDocId}".Trim())
                .Field(L["Print_Status"], FinEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_InvoiceNo"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_Qty"], true).Col(L["Print_Uom"]).Col(L["Print_UnitPrice"], true)
                .Col(L["Print_Amount"], true).Col(L["Print_TaxAmount"], true);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), $"{l.ARInvoiceId}-{l.InvoiceLineNo}", l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.Qty, l.UomCode), n.UomWith(l.UomCode), f.Money(l.UnitPrice, cur),
                    f.Money(l.LineAmount, cur), f.Money(l.TaxAmount, cur));
            p.Tables.Add(tbl);
            p.Total($"{L["Print_TaxAmount"]} {n.CurrencyWith(cur)}：{f.Money(doc.TaxAmount, cur)}")
             .Total($"{L["Print_TotalAmount"]} {n.CurrencyWith(cur)}：{f.Money(doc.TotalAmount, cur)}")
             .Total($"{L["Print_LocalTotal"]}：{f.Local(doc.AmountLocal)}");
            return PrintDocumentView(p);
        }
    }
}
