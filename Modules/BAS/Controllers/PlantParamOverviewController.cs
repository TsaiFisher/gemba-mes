using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers;

/// <summary>
/// 各廠參數覆寫一覽（`plant-scoped-system-params.md` §7 待決 #7，裁決＝唯讀報表）。
///
/// <para><b>唯讀</b>：本畫面刻意不提供編輯。廠別參數的唯一寫入點是廠別維護面板
/// （`Views/Plant/_ProjectZPlantParams.cshtml` + `Filters/PlantFormEnhancementFilter`），
/// 多開一個寫入點就會有兩份三態語意要維護。</para>
///
/// <para><b>廠別範圍</b>：以 <see cref="IUserContext.AllowedPlantIds"/> 收斂；
/// 該清單為空代表角色沒有廠別指派限制（例如掛根 OU 的管理角色），此時顯示全部廠別。
/// <see cref="PlantParam"/> 刻意不是 <c>IPlantEntity</c>，沒有全域查詢過濾，
/// 所以跨廠讀取要在這裡自己收斂，不能靠框架。</para>
/// </summary>
[Authorize]
public class PlantParamOverviewController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly ISystemParamService  _systemParams;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "PlantParamOverview";

    public PlantParamOverviewController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        ISystemParamService systemParams,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IStringLocalizer<ProjectZSharedResource> localizer)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _systemParams      = systemParams;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _localizer         = localizer;
    }

    public async Task<IActionResult> Index()
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        return View(await BuildAsync());
    }

    [HttpGet]
    public async Task<IActionResult> Export(string format = "xlsx")
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        var vm   = await BuildAsync();
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
        var now  = _dateTimeService.Now;

        // 表頭：參數 / 模組 / 全域值 / 各廠一欄
        var headers = new List<string>
        {
            isZh ? "參數" : "Parameter",
            isZh ? "模組" : "Module",
            isZh ? "全域值" : "Global Value",
        };
        headers.AddRange(vm.Plants.Select(p => $"{p.Id} {p.Name}"));

        var inherited = isZh ? "繼承" : "Inherited";
        var req = new ExportRequest
        {
            Title   = isZh ? "各廠參數覆寫一覽" : "Plant Parameter Override Overview",
            Headers = headers.ToArray(),
            Rows    = vm.Rows.Select(r =>
            {
                var cells = new List<object?> { r.Def.Key, r.Def.Module, Display(r.GlobalValue, isZh) };
                cells.AddRange(r.Cells.Select(c => c.State == PlantParamOverviewCell.Overridden
                    ? Display(c.DisplayValue, isZh)
                    : inherited));
                return cells.ToArray();
            }).ToList(),
        };

        return format switch
        {
            "csv"  => File(_exportService.ToCsv(req), "text/csv",
                           Uri.EscapeDataString($"PlantParamOverview_{now:yyyyMMdd}.csv")),
            "json" => File(_exportService.ToJson(req), "application/json",
                           Uri.EscapeDataString($"PlantParamOverview_{now:yyyyMMdd}.json")),
            _      => File(_exportService.ToExcel(req),
                           "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                           Uri.EscapeDataString($"PlantParamOverview_{now:yyyyMMdd_HHmm}.xlsx")),
        };
    }

    /// <summary>空字串與 null 在報表上要看得出差別——「覆寫成空值」是刻意設定，不是沒設。</summary>
    private static string Display(string? value, bool isZh) =>
        string.IsNullOrEmpty(value) ? (isZh ? "（空值）" : "(empty)") : value;

    private async Task<PlantParamOverviewVm> BuildAsync()
    {
        var allowed = _userContext.AllowedPlantIds;

        var plantQuery = _context.Plants.AsNoTracking();
        // 空清單＝角色沒有廠別指派限制，顯示全部；非空才收斂。
        if (allowed.Count > 0)
            plantQuery = plantQuery.Where(p => allowed.Contains(p.Id));

        var plantRows = await plantQuery
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.NameEn })
            .ToListAsync();
        var plants = plantRows
            .Select(p => new PlantParamOverviewPlant(p.Id, SmartFactory.MES.Modules._Shared.PlantDisplay.Name(p.Name, p.NameEn, p.Id)))
            .ToList();

        var plantIds = plants.Select(p => p.Id).ToList();

        // 一次撈完再在記憶體轉置：參數 12 筆 × 廠別數，量級固定且小。
        var overrides = await _context.PlantParams
            .Where(p => plantIds.Contains(p.PlantId))
            .AsNoTracking()
            .ToListAsync();
        var overrideMap = overrides.ToDictionary(o => (o.Key, o.PlantId), o => o.Value);

        var rows = new List<PlantParamOverviewRow>(PlantParamCatalog.All.Count);
        foreach (var def in PlantParamCatalog.All)
        {
            var globalValue = await _systemParams.GetAsync(def.Key);
            var cells = plants.Select(p => overrideMap.TryGetValue((def.Key, p.Id), out var v)
                ? new PlantParamOverviewCell(p.Id, PlantParamOverviewCell.Overridden, v)
                : new PlantParamOverviewCell(p.Id, PlantParamOverviewCell.Inherited, globalValue)
            ).ToList();

            rows.Add(new PlantParamOverviewRow(def, globalValue, cells));
        }

        return new PlantParamOverviewVm(plants, rows);
    }
}
