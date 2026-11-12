using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ACT.Reporting;
using SmartFactory.MES.Modules.ACT.ViewModels;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Controllers;

/// <summary>
/// 試算表。查詢邏輯全在 <see cref="TrialBalanceTool"/>——畫面、排程與常用查詢共用同一份。
///
/// <para>放在「會計管理」而不是「管理報表」：它是**關帳前的檢查工具**，
/// 使用者是從月結那條路走過來的，不是從看財報那條路。</para>
/// </summary>
[Authorize]
public class TrialBalanceController : ModuleControllerBase
{
    private const string MenuKey = "TrialBalance";

    private readonly IUserContext       _userContext;
    private readonly IPermissionService _permissionService;
    private readonly IExportService     _exportService;
    private readonly IDateTimeService   _dateTimeService;
    private readonly ICurrencyService   _currencyService;
    private readonly TrialBalanceTool   _tool;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    public TrialBalanceController(
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        ICurrencyService currencyService,
        TrialBalanceTool tool,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _currencyService   = currencyService;
        _tool              = tool;
        _headers           = headers;
    }

    public async Task<IActionResult> Index(
        string? fromPeriod, string? toPeriod, bool includeZeroRows = false, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        // 沒查過時預設「本期～本期」。原本兩格都空、下拉又由最遠的未來期間排起（會計期間預先建到明年），
        // 隨手選第一、二個就落在所有分錄之後：全部變期初、本期借貸恆為 0（2026-10-05 回報）。
        var currentPeriod = _dateTimeService.GetLocalDate().ToString("yyyyMM");
        ViewBag.Searched        = searched;
        ViewBag.FromPeriod      = searched ? fromPeriod : currentPeriod;
        ViewBag.ToPeriod        = searched ? toPeriod   : currentPeriod;
        ViewBag.CurrentPeriod   = currentPeriod;
        ViewBag.IncludeZeroRows = includeZeroRows;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.BaseCurrencyDp  = await ResolveBaseCurrencyDpAsync();
        ViewBag.PeriodOptions   = await Db.AccountingPeriods
            .AsNoTracking()
            .OrderByDescending(p => p.PeriodId)
            .Select(p => p.PeriodId)
            .ToListAsync();

        if (!searched || string.IsNullOrWhiteSpace(toPeriod))
            return View(new TrialBalanceVm());

        return View(await _tool.ExecuteAsync(
            BuildRequest(fromPeriod, toPeriod, includeZeroRows), User));
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromPeriod, string? toPeriod, bool includeZeroRows = false)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        var request = BuildRequest(fromPeriod, toPeriod, includeZeroRows);
        var vm      = await _tool.ExecuteAsync(request, User);
        var isZh    = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
        var now     = _dateTimeService.Now;

        var title = isZh
            ? $"試算表 {vm.FromPeriod}~{vm.ToPeriod}"
            : $"Trial Balance {vm.FromPeriod}~{vm.ToPeriod}";

        var req = _tool.ToExportRequest(request, vm.Rows, title, _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"TrialBalance_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private TrialBalanceRequest BuildRequest(
        string? fromPeriod, string? toPeriod, bool includeZeroRows)
        => new()
        {
            PlantId         = _userContext.PlantId,
            FromPeriod      = fromPeriod,
            ToPeriod        = toPeriod,
            IncludeZeroRows = includeZeroRows,
        };

    private async Task<int> ResolveBaseCurrencyDpAsync()
    {
        var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
        if (string.IsNullOrEmpty(baseCurrency)) return 2;
        var map = await _currencyService.GetAllDecimalPlacesAsync();
        return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
    }
}
