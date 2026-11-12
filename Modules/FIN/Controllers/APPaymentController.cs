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
    public class APPaymentController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IFinPaymentService _paymentService;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IApprovalService _approvalService;
        private readonly IDateTimeService _dt;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "APPayment";
        private const string DocType = "APPayment";

        public APPaymentController(
            ApplicationDbContext context, IFinPaymentService paymentService,
            IUserContext userContext, IPermissionService permissionService,
            IApprovalService approvalService, IDateTimeService dt,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _paymentService = paymentService;
            _userContext = userContext; _permissionService = permissionService;
            _approvalService = approvalService; _dt = dt;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(
            string? searchSupplier, string? searchStatus, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            ViewBag.Searched       = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder      = sortOrder;
            ViewBag.SupplierSort   = sortOrder == "sup_asc" ? "sup_desc" : "sup_asc";
            ViewBag.DateSort       = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.SearchSupplier = searchSupplier;
            ViewBag.SearchStatus   = searchStatus;
            ViewBag.CanCreate      = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate      = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanPrint       = await _permissionService.CanPrint(MenuKey);
            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            if (!searched)
            {
                ViewBag.BankAccounts = await _context.BankAccounts
                    .Where(b => b.IsActive && b.PlantId == _userContext.PlantId)
                    .AsNoTracking().ToListAsync();
                return View((PaginatedList<APPayment>?)null);
            }

            var query = _context.APPayments
                .Where(p => p.PlantId == _userContext.PlantId)
                .AsNoTracking();
            if (!string.IsNullOrWhiteSpace(searchSupplier))
                query = query.Where(p => p.SupplierCode == searchSupplier);
            if (!string.IsNullOrWhiteSpace(searchStatus) && Enum.TryParse<APPaymentStatus>(searchStatus, out var st))
                query = query.Where(p => p.Status == st);

            query = sortOrder switch
            {
                "sup_asc"   => query.OrderBy(p => p.SupplierCode),
                "sup_desc"  => query.OrderByDescending(p => p.SupplierCode),
                "date_desc" => query.OrderByDescending(p => p.PaymentDate),
                _           => query.OrderByDescending(p => p.PaymentDate)
            };

            var list = await PaginatedList<APPayment>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);

            // 待審核 + 可用銀行帳戶
            ViewBag.BankAccounts = await _context.BankAccounts
                .Where(b => b.IsActive && b.PlantId == _userContext.PlantId)
                .AsNoTracking().ToListAsync();

            return View(list);
        }

        // GET: /APPayment/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareDropdowns();
            var vm = new APPaymentFormVm { PaymentDate = _dt.GetLocalDate() };
            if (IsAjax) return PartialView("_CreateForm", vm);
            return View("_CreateForm", vm);
        }

        // POST: /APPayment/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(APPaymentFormVm vm)
        {
            if (!ModelState.IsValid)
            {
                await PrepareDropdowns();
                return PartialView("_Form", vm);
            }

            if (!await _permissionService.CanCreate(MenuKey))
                return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

            try
            {
                var req = new APPaymentRequest(
                    SupplierCode: vm.SupplierCode,
                    PaymentDate:  vm.PaymentDate,
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

                await _paymentService.CreateAPPaymentAsync(req, _userContext.UserId ?? "", _userContext.PlantId ?? "");
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareDropdowns();
                return PartialView("_Form", vm);
            }
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            var pmt = await _context.APPayments
                .Include(p => p.Allocations).ThenInclude(a => a.APInvoice)
                .FirstOrDefaultAsync(p => p.APPaymentId == id);
            if (pmt == null) return NotFound();
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_Form", pmt);
            return View("_Form", pmt);
        }

        // POST: Confirm（過帳扣帳）— 走簽核流後由主管操作
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoConfirmPermission"].Value });
            try
            {
                await _paymentService.ConfirmAPPaymentAsync(id);
                return Json(new { success = true, message = _localizer["APPaymentConfirmed"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            catch (DbUpdateConcurrencyException)
            {
                // 另一張付款單同時核銷了同一張發票（APInvoice.xmin 擋下）。
                // 沒有這個 catch 使用者會看到 500；而在掛上 xmin 之前，
                // 這裡根本不會發生——兩張付款單會各自扣款成功而發票只認一次。
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
                await _paymentService.VoidAPPaymentAsync(id);
                return Json(new { success = true, message = _localizer["APPaymentVoided"].Value });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            catch (DbUpdateConcurrencyException)
            {
                // 作廢也會反沖 APInvoice.PaidAmount 與 BankAccount.Balance（兩者皆有 xmin），
                // 與 Confirm 走同一組權杖——另一張付款單同時核銷同一張發票時這裡一樣會撞。
                // Confirm 早就接住了，Void 沒跟上，撞到就是 500。
                return Json(new { success = false, message = _localizer["ConcurrencyConflict"].Value });
            }
        }

        // GET: 取得廠商未結 AP Invoice 供前端核銷 Modal 使用
        [HttpGet]
        public async Task<IActionResult> GetOpenInvoices(string supplierCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            // 未核准的發票不得出現在核銷清單：手動發票加了簽核之後，Status 仍是 Unpaid，
            // 只濾 Status 的話一張還沒人簽的發票照樣付得出去，簽核就形同虛設。
            // 認列型發票的 ApprovalStatus 一律是 Approved，不受影響。
            var invoices = await _context.APInvoices
                .Where(i => i.SupplierCode == supplierCode
                         && i.PlantId == _userContext.PlantId
                         && i.ApprovalStatus == DocumentApprovalStatus.Approved
                         && i.Status != APInvoiceStatus.Void
                         // 未付的照列；付清後才折讓而多付的（負餘額）也列，供供應商退款或抵用。
                         // 折讓沖平（餘額 0）的發票狀態不會變（折讓不是付款），以餘額排除。
                         && ((i.Status != APInvoiceStatus.Paid && i.TotalAmount - i.PaidAmount - i.DebitedAmount > 0)
                             || i.TotalAmount - i.PaidAmount - i.DebitedAmount < 0))
                .Select(i => new {
                    i.APInvoiceId, i.InvoiceDate, i.DueDate, i.TotalAmount,
                    i.PaidAmount, i.DebitedAmount, Outstanding = i.TotalAmount - i.PaidAmount - i.DebitedAmount,
                    i.CurrencyCode, i.SourceDocId
                })
                .AsNoTracking()
                .ToListAsync();
            return Json(invoices);
        }

        private async Task PrepareDropdowns()
        {
            ViewBag.Suppliers = await _context.Companies
                .Where(c => c.IsSupplier && c.IsActive)
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

        // GET: /APPayment/Print/{id}（2026-10-27：付款單）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.APPayments.AsNoTracking()
                .Include(r => r.Allocations).ThenInclude(a => a.APInvoice)
                .FirstOrDefaultAsync(r => r.APPaymentId == id);
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
                Title = L["Print_Title_APPayment"], DocNo = doc.APPaymentId,
                DateLabel = L["Print_PaymentDate"], DateValue = doc.PaymentDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Supplier"], n.CompanyWith(doc.SupplierCode))
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
            foreach (var a in doc.Allocations.OrderBy(x => x.APInvoiceId))
                tbl.Row(a.APInvoiceId, a.APInvoice?.InvoiceDate.ToString("yyyy-MM-dd"), a.APInvoice?.DueDate.ToString("yyyy-MM-dd"),
                    f.Money(a.AllocatedAmount, doc.CurrencyCode), f.Local(a.AllocatedAmountLocal));
            p.Tables.Add(tbl);
            return PrintDocumentView(p);
        }
    }
}
