using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.FIN.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.FIN;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.FIN.Controllers
{
    [Authorize]
    public class ARReceiptController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IFinPaymentService _paymentService;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dt;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "ARReceipt";

        public ARReceiptController(
            ApplicationDbContext context, IFinPaymentService paymentService,
            IUserContext userContext, IPermissionService permissionService, IDateTimeService dt,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _paymentService = paymentService;
            _userContext = userContext; _permissionService = permissionService; _dt = dt;
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
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.DateSort        = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.SearchCustomer  = searchCustomer;
            ViewBag.SearchStatus    = searchStatus;
            ViewBag.CanCreate       = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate       = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            if (!searched) return View((PaginatedList<ARReceipt>?)null);

            var query = _context.ARReceipts
                .Where(r => r.PlantId == _userContext.PlantId)
                .AsNoTracking();
            if (!string.IsNullOrWhiteSpace(searchCustomer))
                query = query.Where(r => r.CustomerCode == searchCustomer);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<ARReceiptStatus>(searchStatus, out var st))
                query = query.Where(r => r.Status == st);
            query = sortOrder == "date_asc" ? query.OrderBy(r => r.ReceiptDate) : query.OrderByDescending(r => r.ReceiptDate);
            var list = await PaginatedList<ARReceipt>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(list);
        }

        // GET: /ARReceipt/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareDropdowns();
            var vm = new ARReceiptFormVm { ReceiptDate = _dt.GetLocalDate() };
            if (IsAjax) return PartialView("_CreateForm", vm);
            return View("_CreateForm", vm);
        }

        // POST: /ARReceipt/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(ARReceiptFormVm vm)
        {
            if (!ModelState.IsValid)
            {
                await PrepareDropdowns();
                return PartialView("_CreateForm", vm);
            }

            if (!await _permissionService.CanCreate(MenuKey))
                return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

            try
            {
                var req = new ARReceiptRequest(
                    CustomerCode: vm.CustomerCode,
                    ReceiptDate:  vm.ReceiptDate,
                    Method:       vm.Method,
                    CurrencyCode: vm.CurrencyCode,
                    ExchangeRate: vm.ExchangeRate,
                    Amount:       vm.Amount,
                    BankAccountId: vm.BankAccountId,
                    Reference:    vm.Reference,
                    Remarks:      vm.Remarks,
                    Allocations:  vm.Allocations
                        .Where(a => a.Amount != 0)   // 負數＝對多收付的發票退款／抵用
                        .Select(a => new PaymentAllocationLine(a.InvoiceId, a.Amount))
                        .ToList());

                await _paymentService.CreateARReceiptAsync(req, _userContext.UserId ?? "", _userContext.PlantId ?? "");
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareDropdowns();
                return PartialView("_CreateForm", vm);
            }
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            var rec = await _context.ARReceipts
                .Include(r => r.Allocations).ThenInclude(a => a.ARInvoice)
                .FirstOrDefaultAsync(r => r.ARReceiptId == id);
            if (rec == null) return NotFound();
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_Form", rec);
            return View("_Form", rec);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoConfirmPermission"].Value });
            try
            {
                await _paymentService.ConfirmARReceiptAsync(id);
                return Json(new { success = true, message = _localizer["ARReceiptConfirmed"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            catch (DbUpdateConcurrencyException)
            {
                // 另一張收款單同時核銷了同一張應收發票（ARInvoice.xmin 擋下）。
                return Json(new { success = false, message = _localizer["ConcurrencyConflict"].Value });
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoVoidPermission"].Value });
            try
            {
                await _paymentService.VoidARReceiptAsync(id);
                return Json(new { success = true, message = _localizer["ARReceiptVoided"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            catch (DbUpdateConcurrencyException)
            {
                // 同 APPaymentController.Void：作廢反沖 ARInvoice.ReceivedAmount 與
                // BankAccount.Balance，走的是與 Confirm 相同的 xmin 權杖。
                return Json(new { success = false, message = _localizer["ConcurrencyConflict"].Value });
            }
        }

        private async Task PrepareDropdowns()
        {
            ViewBag.Customers = await _context.Companies
                .Where(c => c.IsCustomer && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .AsNoTracking().ToListAsync();

            ViewBag.BankAccounts = await _context.BankAccounts
                .Where(b => b.IsActive && b.PlantId == _userContext.PlantId)
                .OrderBy(b => b.AccountId)
                .AsNoTracking().ToListAsync();

            ViewBag.CurrencyOptions = await _context.Currencies
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { c.CurrencyCode, c.CurrencyName })
                .AsNoTracking().ToListAsync();
        }

        [HttpGet]
        public async Task<IActionResult> GetOpenInvoices(string customerCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var invoices = await _context.ARInvoices
                .Where(i => i.CustomerCode == customerCode
                         && i.PlantId == _userContext.PlantId
                         && i.Status != ARInvoiceStatus.Void
                         // 未收的照列；收訖後才折讓而多收的（負餘額）也列，供退款或抵用。
                         // 折讓沖平（餘額 0）的發票狀態不會變（折讓不是收款），以餘額排除。
                         && ((i.Status != ARInvoiceStatus.Received && i.TotalAmount - i.ReceivedAmount - i.CreditedAmount > 0)
                             || i.TotalAmount - i.ReceivedAmount - i.CreditedAmount < 0))
                .Select(i => new {
                    i.ARInvoiceId, i.InvoiceDate, i.DueDate, i.TotalAmount,
                    i.ReceivedAmount, i.CreditedAmount, Outstanding = i.TotalAmount - i.ReceivedAmount - i.CreditedAmount,
                    i.CurrencyCode, i.SourceDocId
                })
                .AsNoTracking()
                .ToListAsync();
            return Json(invoices);
        }

        // GET: /ARReceipt/Print/{id}（2026-10-27：收款單）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.ARReceipts.AsNoTracking()
                .Include(r => r.Allocations).ThenInclude(a => a.ARInvoice)
                .FirstOrDefaultAsync(r => r.ARReceiptId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!);
            await PreparePrintSignAsync(id, null);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var bank = string.IsNullOrEmpty(doc.BankAccountId) ? null
                : await _context.BankAccounts.AsNoTracking().Where(b => b.AccountId == doc.BankAccountId)
                    .Select(b => b.BankName + " " + b.AccountNo).FirstOrDefaultAsync();
            var p = new PrintDocument
            {
                Title = L["Print_Title_ARReceipt"], DocNo = doc.ARReceiptId,
                DateLabel = L["Print_ReceiptDate"], DateValue = doc.ReceiptDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Customer"], n.CompanyWith(doc.CustomerCode))
                .Field(L["Print_PaymentMethod"], FinEnumDisplay.Text(L, doc.Method))
                .Field(L["Print_BankAccount"], bank ?? doc.BankAccountId)
                .Field(L["Print_Reference"], doc.Reference)
                .Field(L["Print_Currency"], n.CurrencyWith(doc.CurrencyCode))
                .Field(L["Print_ExchangeRate"], doc.ExchangeRate.ToString("0.######"))
                .Field(L["Print_Amount"], f.Money(doc.Amount, doc.CurrencyCode))
                .Field(L["Print_AmountLocal"], f.Local(doc.AmountLocal))
                .Field(L["Print_ExchangeGainLoss"], doc.ExchangeGainLoss != 0 ? f.Local(doc.ExchangeGainLoss) : null)
                .Field(L["Print_Status"], FinEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable { Title = L["Print_Allocations"] }
                .Col(L["Print_InvoiceNo"]).Col(L["Print_InvoiceDate"]).Col(L["Print_DueDate"])
                .Col(L["Print_AllocatedAmount"], true).Col(L["Print_AmountLocal"], true);
            foreach (var a in doc.Allocations.OrderBy(x => x.ARInvoiceId))
                tbl.Row(a.ARInvoiceId, a.ARInvoice?.InvoiceDate.ToString("yyyy-MM-dd"), a.ARInvoice?.DueDate.ToString("yyyy-MM-dd"),
                    f.Money(a.AllocatedAmount, doc.CurrencyCode), f.Local(a.AllocatedAmountLocal));
            p.Tables.Add(tbl);
            return PrintDocumentView(p);
        }
    }
}
