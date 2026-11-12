using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ACT.ViewModels;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Controllers;

[Authorize]
public class BadDebtProvisionController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IEclService          _eclService;

    private const string MenuKey = "BadDebtProvision";

    public BadDebtProvisionController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IStringLocalizer<ProjectZSharedResource> localizer,
        IEclService eclService)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _localizer         = localizer;
        _eclService        = eclService;
    }

    public async Task<IActionResult> Index(
        string? asOfDate, string? searchCustomer, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched       = searched;
        ViewBag.AsOfDate       = asOfDate;
        ViewBag.SearchCustomer = searchCustomer;
        ViewBag.CanPrint       = await _permissionService.CanPrint(MenuKey);
        ViewBag.CustomerOptions = await _context.Companies
            .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();

        if (!searched)
            return View(new List<BadDebtRowVm>());

        var rows = await BuildQueryAsync(asOfDate, searchCustomer);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? asOfDate, string? searchCustomer)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(asOfDate, searchCustomer);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "壞帳準備 ECL" : "Bad Debt Provision (ECL)",
            Headers = isZh
                ? ["客戶代號", "帳齡區間", "帳齡天數", "應收金額", "損失率%", "計提金額"]
                : ["Customer", "Aging Bucket", "Days", "Amount", "Loss Rate%", "Provision"],
            Rows = rows.Select(r => new object?[]
            {
                r.CompanyCode, r.AgingBucket, r.AgingDays,
                r.TotalAmount, r.LossRate * 100, r.Provision
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"BadDebtProvision_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>
    /// 應收取自**未結 AR 發票**，以本幣未收餘額（與 AR 帳齡表同一口徑）依到期日分區，
    /// 損失率與 ECL 共用同一份設定。
    ///
    /// <para>原本以「已過帳出貨單」代替應收（當時還沒有 AR 模組）：不扣收款、帳齡從出貨日起算、
    /// 損失率另寫死一套——已全額收款的出貨照樣提列，準備嚴重高估，而且與 ECL 算出不同數字。</para>
    /// </summary>
    private async Task<List<BadDebtRowVm>> BuildQueryAsync(
        string? asOfDate, string? searchCustomer)
    {
        var plantId = _userContext.PlantId ?? "";
        DateOnly.TryParse(asOfDate, out var refDate);
        if (refDate == default) refDate = _dateTimeService.GetLocalDate();

        var query = _context.ARInvoices.AsNoTracking()
            .Where(i => i.PlantId == plantId
                     && i.Status != ARInvoiceStatus.Received
                     && i.Status != ARInvoiceStatus.Void
                     && i.InvoiceDate <= refDate);

        if (!string.IsNullOrWhiteSpace(searchCustomer))
            query = query.Where(i => i.CustomerCode == searchCustomer);

        var invoices = await query.ToListAsync();
        var rates    = await _eclService.GetLossRatesAsync(plantId);

        return invoices
            .Select(i =>
            {
                var days   = refDate.DayNumber - i.DueDate.DayNumber;
                var bucket = EclService.BucketOf(rates, days);
                return new { i.CustomerCode, Days = days, Bucket = bucket, Amount = EclService.ExposureLocal(i) };
            })
            .Where(x => x.Amount != 0m)
            .GroupBy(x => new { x.CustomerCode, x.Bucket.AgingBucket, x.Bucket.LossRate, x.Bucket.AgingDaysFrom })
            .Select(g => new BadDebtRowVm
            {
                CompanyCode = g.Key.CustomerCode,
                AgingBucket = g.Key.AgingBucket,
                AgingDays   = g.Max(x => x.Days),
                TotalAmount = g.Sum(x => x.Amount),
                LossRate    = g.Key.LossRate,
                Provision   = Math.Round(g.Sum(x => x.Amount) * g.Key.LossRate, 2, MidpointRounding.AwayFromZero),
            })
            .OrderBy(r => r.CompanyCode)
            .ThenBy(r => r.AgingDays)
            .ToList();
    }
}
