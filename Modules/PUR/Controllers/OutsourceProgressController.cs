using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers;

[Authorize]
public class OutsourceProgressController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "OutsourceProgress";

    public OutsourceProgressController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IStringLocalizer<ProjectZSharedResource> localizer)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _localizer         = localizer;
    }

    public async Task<IActionResult> Index(
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        bool overdueOnly = false, int minOverdueDays = 0, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched        = searched;
        ViewBag.FromDate        = fromDate;
        ViewBag.ToDate          = toDate;
        ViewBag.SearchSupplier  = searchSupplier;
        ViewBag.SearchProduct   = searchProduct;
        ViewBag.OverdueOnly     = overdueOnly;
        ViewBag.MinOverdueDays  = minOverdueDays;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.SupplierOptions = await _context.Companies
            .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<OutsourceProgressRowVm>());

        var rows = await BuildQueryAsync(fromDate, toDate, searchSupplier, searchProduct, overdueOnly, minOverdueDays);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        bool overdueOnly = false, int minOverdueDays = 0)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(fromDate, toDate, searchSupplier, searchProduct, overdueOnly, minOverdueDays);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "委外進度追蹤" : "Outsource Progress",
            Headers = isZh
                ? ["委外採購單號", "供應商", "發單日", "預交日", "品號", "行號", "委外量", "已驗收", "未驗收", "進度%", "逾期天數"]
                : ["OPO No.", "Supplier", "Order Date", "Expected", "Product", "Line", "Ordered", "Received", "Pending", "Progress%", "Overdue Days"],
            Rows = rows.Select(r => new object?[]
            {
                r.OutsourcePurchaseOrderId, r.SupplierCode,
                r.OrderDate.ToString("yyyy-MM-dd"),
                r.ExpectedDeliveryDate?.ToString("yyyy-MM-dd"),
                r.ProductCode, r.LineNo,
                r.OrderedQty, r.ReceivedQty, r.PendingQty, r.ProgressPct,
                r.IsOverdue ? r.OverdueDays : (object?)""
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"OutsourceProgress_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<OutsourceProgressRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        bool overdueOnly, int minOverdueDays = 0)
    {
        var plantId = _userContext.PlantId ?? "";
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = DateOnly.FromDateTime(DateTime.Today);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var query =
            from opo in _context.OutsourcePurchaseOrders
            join opol in _context.OutsourcePurchaseOrderLines
                on opo.OutsourcePurchaseOrderId equals opol.OutsourcePurchaseOrderId
            where opo.PlantId == plantId
               && opo.Status != OutsourcePoStatus.Draft
               && opo.OrderDate >= start
               && opo.OrderDate <= end
            select new
            {
                opo.OutsourcePurchaseOrderId,
                opo.SupplierCode,
                opo.OrderDate,
                opo.ExpectedDeliveryDate,
                opol.ProductCode,
                opol.LineNo,
                opol.Qty,
                opol.SplitQty,
                opol.UomCode,
            };

        if (!string.IsNullOrWhiteSpace(searchSupplier))
            query = query.Where(r => r.SupplierCode == searchSupplier);
        if (!string.IsNullOrWhiteSpace(searchProduct))
            query = query.Where(r => r.ProductCode == searchProduct);

        var raw = await query.AsNoTracking().ToListAsync();

        var opoIds = raw.Select(r => r.OutsourcePurchaseOrderId).Distinct().ToList();

        // 已驗收量：已確認/已過帳的委外收貨（對齊 OutsourcePerformance；Confirmed 即廠商已交貨，
        // 稽催不應再列催）。彙總粒度為（單號, 品號），行級歸屬於下方依 LineNo 順序分攤。
        var receivedMap = await _context.OutsourceReceiptLines
            .AsNoTracking()
            .Join(_context.OutsourceReceipts,
                orl => orl.OutsourceReceiptId,
                or  => or.OutsourceReceiptId,
                (orl, or) => new { or.OutsourcePurchaseOrderId, orl.ProductCode, orl.ReceiptQty, or.Status })
            .Where(x => opoIds.Contains(x.OutsourcePurchaseOrderId)
                     && (x.Status == DocStatus.Confirmed || x.Status == DocStatus.Posted))
            .GroupBy(x => new { x.OutsourcePurchaseOrderId, x.ProductCode })
            .Select(g => new
            {
                g.Key.OutsourcePurchaseOrderId,
                g.Key.ProductCode,
                ReceivedQty = g.Sum(x => x.ReceiptQty)
            })
            .ToDictionaryAsync(x => (x.OutsourcePurchaseOrderId, x.ProductCode), x => x.ReceivedQty);

        // 拆單行的委外量以 SplitQty 為準（同 GoodsReceiptController 的訂購量口徑）；
        // 同（單號, 品號）多行時，將收貨量依 LineNo 順序逐行分攤，避免每行重複計入全額。
        var result = new List<OutsourceProgressRowVm>();
        foreach (var g in raw.GroupBy(r => (r.OutsourcePurchaseOrderId, r.ProductCode)))
        {
            receivedMap.TryGetValue(g.Key, out var remainingReceived);
            foreach (var r in g.OrderBy(r => r.LineNo))
            {
                var ordered  = r.SplitQty > 0 ? r.SplitQty : r.Qty;
                var received = Math.Min(ordered, remainingReceived);
                remainingReceived -= received;

                var isOverdue = r.ExpectedDeliveryDate.HasValue
                             && r.ExpectedDeliveryDate.Value < today
                             && received < ordered;
                result.Add(new OutsourceProgressRowVm
                {
                    OutsourcePurchaseOrderId = r.OutsourcePurchaseOrderId,
                    SupplierCode             = r.SupplierCode,
                    OrderDate                = r.OrderDate,
                    ExpectedDeliveryDate     = r.ExpectedDeliveryDate,
                    ProductCode              = r.ProductCode,
                    LineNo                   = r.LineNo,
                    OrderedQty               = ordered,
                    UomCode                  = r.UomCode,
                    ReceivedQty              = received,
                    IsOverdue                = isOverdue,
                    OverdueDays              = isOverdue
                        ? today.DayNumber - r.ExpectedDeliveryDate!.Value.DayNumber : 0,
                });
            }
        }

        if (overdueOnly)
            result = result.Where(r => r.IsOverdue).ToList();
        if (minOverdueDays > 0)
            result = result.Where(r => r.OverdueDays >= minOverdueDays).ToList();

        return result
            .OrderByDescending(r => r.OverdueDays)
            .ThenBy(r => r.ExpectedDeliveryDate)
            .ThenBy(r => r.OutsourcePurchaseOrderId)
            .ToList();
    }
}
