using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Controllers
{
    [Authorize]
    public class APAgingController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;

        private const string MenuKey = "APAging";

        public APAgingController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IExportService exportService,
            IDateTimeService dateTimeService)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _exportService = exportService;
            _dateTimeService = dateTimeService;
        }

        public async Task<IActionResult> Index(
            DateOnly? asOf, string? searchSupplier, string? sortOrder,
            bool searched = false)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            // 同 ARAging：帳齡基準日以廠別當地日為準，DateTime.Today 是伺服器時區（容器多為 UTC）。
            var asOfDate = asOf ?? _dateTimeService.GetLocalDate();
            ViewBag.AsOf            = asOfDate;
            ViewBag.SearchSupplier  = searchSupplier;
            ViewBag.Searched        = searched;
            // 表頭排序連結的下一個值（點同一欄＝切方向）。預設檢視是總額降冪。
            ViewBag.PartySort       = sortOrder == "party_asc" ? "party_desc" : "party_asc";
            ViewBag.TotalSort       = sortOrder == "total_asc" ? "total_desc" : "total_asc";
            ViewBag.SortOrder       = sortOrder;
            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            if (!searched) return View("~/Modules/FIN/Views/AgingReport/APAging.cshtml", new List<AgingRowVm>());

            var rows = await BuildAgingAsync(asOfDate, searchSupplier, isAP: true, sortOrder);
            return View("~/Modules/FIN/Views/AgingReport/APAging.cshtml", rows);
        }

        [HttpGet]
        public async Task<IActionResult> Export(DateOnly? asOf, string? searchSupplier, string? sortOrder = null)
        {
            // 匯出＝資料出境，與全庫其餘 Export 一致改用 CanPrint（FIN 模組 Full=HQ/FIN，可見範圍不變）。
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var asOfDate = asOf ?? _dateTimeService.GetLocalDate();
            var rows = await BuildAgingAsync(asOfDate, searchSupplier, isAP: true, sortOrder);

            var req = new ExportRequest
            {
                Title = $"{PzText.Get("Fin_APAgingTitle")}_{asOfDate:yyyyMMdd}",
                Headers   = PzText.Get("Fin_ExportHeaders_APAging").Split('|'),
                Rows      = rows.Select(r => new object?[] {
                    r.PartyCode, r.Current, r.Days30, r.Days60, r.Days90, r.Days90Plus, r.Total
                }).ToList()
            };
            var bytes = _exportService.ToExcel(req);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{PzText.Get("Fin_APAgingTitle")}_{asOfDate:yyyyMMdd}.xlsx");
        }

        private async Task<List<AgingRowVm>> BuildAgingAsync(
            DateOnly asOf, string? filterParty, bool isAP, string? sortOrder)
        {
            var query = _context.APInvoices
                .Where(i => i.PlantId == _userContext.PlantId
                         && i.Status != APInvoiceStatus.Void
                         && (i.Status != APInvoiceStatus.Paid
                              // 已付清後才開折讓＝供應商欠我們（負餘額），也要看得到
                              || i.TotalAmount - i.PaidAmount - i.DebitedAmount < 0))
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(filterParty))
                query = query.Where(i => i.SupplierCode == filterParty);

            var invoices = await query
                .Select(i => new {
                    Party     = i.SupplierCode,
                    i.DueDate,
                    Outstanding = i.AmountLocal - (i.PaidAmount + i.DebitedAmount) * i.ExchangeRate   // 折讓也要扣
                })
                .ToListAsync();

            var grouped = invoices
                .GroupBy(i => i.Party)
                .Select(g =>
                {
                    var vm = new AgingRowVm { PartyCode = g.Key };
                    foreach (var inv in g)
                    {
                        var days = (asOf.ToDateTime(TimeOnly.MinValue) - inv.DueDate.ToDateTime(TimeOnly.MinValue)).Days;
                        var amt  = inv.Outstanding;
                        if (days <= 0)       vm.Current   += amt;
                        else if (days <= 30) vm.Days30    += amt;
                        else if (days <= 60) vm.Days60    += amt;
                        else if (days <= 90) vm.Days90    += amt;
                        else                 vm.Days90Plus += amt;
                    }
                    vm.Total = vm.Current + vm.Days30 + vm.Days60 + vm.Days90 + vm.Days90Plus;
                    return vm;
                })
                .ToList();

            // 排序：預設總額降冪（欠最多的在最上面）。四個值都認，讓表頭可以來回切。
            return sortOrder switch
            {
                "party_asc"  => grouped.OrderBy(r => r.PartyCode).ToList(),
                "party_desc" => grouped.OrderByDescending(r => r.PartyCode).ToList(),
                "total_asc"  => grouped.OrderBy(r => r.Total).ToList(),
                _            => grouped.OrderByDescending(r => r.Total).ToList(),
            };
        }
    }
}
