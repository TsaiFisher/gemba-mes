using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

/// <summary>
/// 工單用料狀況（唯讀查詢）：以 WorkOrderMaterial 為列，一次看齊
/// 需求量 / 已發 / 已退 / 未發 與廠級可用量，並標示備齊狀態。
/// 備齊狀態口徑與 <see cref="IWorkOrderMaterialReadinessService"/>（mfg-domain.md §21.5）一致，
/// 但本畫面純查詢：不寫回 WorkOrder.MaterialReadinessStatus。
/// </summary>
[Authorize]
public class WorkOrderMaterialStatusController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IUomService          _uomService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "WorkOrderMaterialStatus";

    public WorkOrderMaterialStatusController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IUomService uomService,
        IStringLocalizer<ProjectZSharedResource> localizer)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _uomService        = uomService;
        _localizer         = localizer;
    }

    public async Task<IActionResult> Index(
        string? searchBatch, string? searchWorkOrder, string? searchMaterial,
        bool onlyShort = false, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched        = searched;
        ViewBag.SearchBatch     = searchBatch;
        ViewBag.SearchWorkOrder = searchWorkOrder;
        ViewBag.SearchMaterial  = searchMaterial;
        ViewBag.OnlyShort       = onlyShort;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.UomDecimals     = await _uomService.GetAllDecimalPlacesAsync();

        var plantId = _userContext.PlantId ?? "";
        ViewBag.BatchOptions = await _context.ProductionBatches
            .AsNoTracking()
            .Where(b => b.PlantId == plantId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => b.BatchId)
            .Take(500)
            .ToListAsync();
        ViewBag.WorkOrderOptions = await _context.WorkOrders
            .AsNoTracking()
            .Where(wo => wo.PlantId == plantId && wo.Status != WorkOrderStatus.Void)
            .OrderByDescending(wo => wo.CreatedAt)
            .Select(wo => wo.WorkOrderId)
            .Take(500)
            .ToListAsync();
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<WorkOrderMaterialStatusRowVm>());

        var rows = await BuildQueryAsync(searchBatch, searchWorkOrder, searchMaterial, onlyShort);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? searchBatch, string? searchWorkOrder, string? searchMaterial, bool onlyShort = false)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        var rows = await BuildQueryAsync(searchBatch, searchWorkOrder, searchMaterial, onlyShort);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "工單用料狀況" : "Work Order Material Status",
            Headers = isZh
                ? ["生產批", "工單號", "工單狀態", "產出品號", "工序", "序", "料件品號", "品名",
                   "單位", "發料方式", "需求量", "已發", "已退", "淨發", "未發", "可用量", "備齊狀態"]
                : ["Batch", "Work Order", "WO Status", "Output", "Op", "Seq", "Material", "Name",
                   "UOM", "Issue Method", "Required", "Issued", "Returned", "Net", "Open", "Available", "Readiness"],
            Rows = rows.Select(r => new object?[]
            {
                r.ProductionBatchId, r.WorkOrderId, r.Status.ToString(), r.ParentProductCode,
                r.OperationSeq, r.BomSeq, r.ProductCode, r.ProductName, r.UomCode,
                r.IssueMethod.ToString(),
                r.RequiredQty, r.IssuedQty, r.ReturnedQty, r.NetIssuedQty, r.OpenQty,
                r.AvailableQty, r.ReadinessState.ToString()
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"WorkOrderMaterialStatus_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<WorkOrderMaterialStatusRowVm>> BuildQueryAsync(
        string? searchBatch, string? searchWorkOrder, string? searchMaterial, bool onlyShort)
    {
        var plantId = _userContext.PlantId ?? "";

        var woQuery = _context.WorkOrders
            .Where(wo => wo.PlantId == plantId && wo.Status != WorkOrderStatus.Void);

        if (!string.IsNullOrWhiteSpace(searchBatch))
            woQuery = woQuery.Where(wo => wo.ProductionBatchId == searchBatch);
        if (!string.IsNullOrWhiteSpace(searchWorkOrder))
            woQuery = woQuery.Where(wo => wo.WorkOrderId == searchWorkOrder);

        var workOrders = await woQuery.AsNoTracking()
            .Select(wo => new
            {
                wo.WorkOrderId, wo.ProductionBatchId, wo.ProductCode, wo.Status
            })
            .ToListAsync();

        if (workOrders.Count == 0) return [];

        var woIds = workOrders.Select(w => w.WorkOrderId).ToList();

        var matQuery = _context.Set<WorkOrderMaterial>()
            .Where(m => woIds.Contains(m.WorkOrderId));

        if (!string.IsNullOrWhiteSpace(searchMaterial))
            matQuery = matQuery.Where(m => m.ProductCode == searchMaterial);

        var materials = await matQuery.AsNoTracking().ToListAsync();
        if (materials.Count == 0) return [];

        var materialCodes = materials.Select(m => m.ProductCode).Distinct().ToList();

        var productNames = await _context.Products.AsNoTracking()
            .Where(p => materialCodes.Contains(p.ProductCode))
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName);

        var availableMap = await BuildPlantAvailableMapAsync(plantId, materialCodes);
        var ownReservedMap = await BuildOwnReservationMapAsync(plantId, woIds, materialCodes);

        // 下階自製料：子工單是否已完工（Completed／Closed）
        var childIds = materials
            .Where(m => !string.IsNullOrEmpty(m.ChildWorkOrderId))
            .Select(m => m.ChildWorkOrderId!)
            .Distinct()
            .ToList();
        var completedChildren = childIds.Count == 0
            ? []
            : (await _context.WorkOrders.AsNoTracking()
                .Where(w => childIds.Contains(w.WorkOrderId)
                         && (w.Status == WorkOrderStatus.Completed || w.Status == WorkOrderStatus.Closed))
                .Select(w => w.WorkOrderId)
                .ToListAsync())
              .ToHashSet();

        var woMap = workOrders.ToDictionary(w => w.WorkOrderId);

        var rows = materials
            .Select(m =>
            {
                var wo = woMap[m.WorkOrderId];

                // §21.5 逐料備齊判定（與 WorkOrderMaterialReadinessService 同口徑）
                decimal available = 0m;
                MaterialLineReadiness state;
                if (m.IssueMethod == MaterialIssueMethod.Backflush)
                {
                    state = MaterialLineReadiness.Ready;          // 完工才沖，開工前不硬性要求
                }
                else if (!string.IsNullOrEmpty(m.ChildWorkOrderId))
                {
                    state = completedChildren.Contains(m.ChildWorkOrderId!)
                        ? MaterialLineReadiness.Ready
                        : MaterialLineReadiness.PendingSubassembly;
                }
                else
                {
                    // 本工單可用量＝廠級自由量 ＋ 本工單自身預留（MRP 已為本工單鎖住的在手量）
                    available = availableMap.GetValueOrDefault(m.ProductCode, 0m)
                              + ownReservedMap.GetValueOrDefault((m.WorkOrderId, m.ProductCode), 0m);
                    state = available >= m.RequiredQty
                        ? MaterialLineReadiness.Ready
                        : MaterialLineReadiness.Shortage;
                }

                return new WorkOrderMaterialStatusRowVm
                {
                    ProductionBatchId = wo.ProductionBatchId,
                    WorkOrderId       = m.WorkOrderId,
                    Status            = wo.Status,
                    ParentProductCode = wo.ProductCode,
                    OperationSeq      = m.OperationSeq,
                    BomSeq            = m.BomSeq,
                    ProductCode       = m.ProductCode,
                    ProductName       = productNames.GetValueOrDefault(m.ProductCode),
                    UomCode           = m.UomCode,
                    IssueMethod       = m.IssueMethod,
                    ChildWorkOrderId  = m.ChildWorkOrderId,
                    RequiredQty       = m.RequiredQty,
                    IssuedQty         = m.IssuedQty,
                    ReturnedQty       = m.ReturnedQty,
                    AvailableQty      = available,
                    ReadinessState    = state,
                };
            });

        if (onlyShort)
            rows = rows.Where(r => r.ReadinessState != MaterialLineReadiness.Ready);

        return rows
            .OrderBy(r => r.ReadinessState == MaterialLineReadiness.Ready ? 1 : 0)
            .ThenBy(r => r.WorkOrderId)
            .ThenBy(r => r.OperationSeq)
            .ThenBy(r => r.BomSeq)
            .ToList();
    }

    /// <summary>
    /// 批次版「本工單自身預留量」＝Σ Active 在手預留，以 (工單號, 品號) 為鍵。
    /// 含 MRP 為工單鎖的 WorkOrderMaterial 預留，與該工單備料單接手的預留
    /// （備料確認時工單預留已等額轉移過去）。口徑同
    /// IInventoryReservationService.GetReservedForWorkOrderAsync，但一次算完整份工單清單（逐料呼叫會 N+1）。
    /// </summary>
    private async Task<Dictionary<(string WorkOrderId, string ProductCode), decimal>> BuildOwnReservationMapAsync(
        string plantId, List<string> workOrderIds, List<string> productCodes)
    {
        // 備料單號 → 來源工單號
        var stagingToWo = await _context.Set<MaterialStagingOrder>()
            .Where(o => o.SourceType == "WorkOrder" && o.SourceId != null && workOrderIds.Contains(o.SourceId))
            .Select(o => new { o.StagingId, o.SourceId })
            .ToDictionaryAsync(x => x.StagingId, x => x.SourceId!);

        var stagingIds = stagingToWo.Keys.ToList();

        var rows = await _context.Set<InventoryReservation>()
            .Where(r => r.PlantId == plantId
                     && productCodes.Contains(r.ProductCode)
                     && r.Category == ReservationCategory.OnHand
                     && r.Status == ReservationStatus.Active
                     && ((r.SourceType == ReservationSourceType.WorkOrderMaterial
                          && workOrderIds.Contains(r.SourceDocId))
                      || (r.SourceType == ReservationSourceType.MaterialStagingOrder
                          && stagingIds.Contains(r.SourceDocId))))
            .Select(r => new { r.SourceType, r.SourceDocId, r.ProductCode, r.ReservedQty })
            .ToListAsync();

        var map = new Dictionary<(string, string), decimal>();
        foreach (var r in rows)
        {
            var woId = r.SourceType == ReservationSourceType.MaterialStagingOrder
                ? stagingToWo[r.SourceDocId]
                : r.SourceDocId;
            var key = (woId, r.ProductCode);
            map[key] = map.GetValueOrDefault(key, 0m) + r.ReservedQty;
        }
        return map;
    }

    /// <summary>
    /// 批次版廠級自由量＝Σ QtyOnHand（排除待驗倉）− Σ Active 在手預留，
    /// 口徑同 IInventoryReservationService.GetPlantAvailableQtyAsync，
    /// 但一次算完整份品號清單（逐料呼叫會 N+1）。
    /// </summary>
    private async Task<Dictionary<string, decimal>> BuildPlantAvailableMapAsync(
        string plantId, List<string> productCodes)
    {
        var quarantineLocationCodes = await _context.StorageLocations
            .Join(_context.Warehouses.Where(w => w.WarehouseType == WarehouseType.Quarantine),
                l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
            .ToListAsync();

        var onHand = await _context.Inventories
            .Where(i => i.PlantId == plantId
                     && productCodes.Contains(i.ProductCode)
                     && !quarantineLocationCodes.Contains(i.LocationCode))
            .GroupBy(i => i.ProductCode)
            .Select(g => new { ProductCode = g.Key, Qty = g.Sum(x => x.QtyOnHand) })
            .ToDictionaryAsync(x => x.ProductCode, x => x.Qty);

        var reserved = await _context.Set<InventoryReservation>()
            .Where(r => r.PlantId == plantId
                     && productCodes.Contains(r.ProductCode)
                     && r.Category == ReservationCategory.OnHand
                     && r.Status == ReservationStatus.Active)
            .GroupBy(r => r.ProductCode)
            .Select(g => new { ProductCode = g.Key, Qty = g.Sum(x => x.ReservedQty) })
            .ToDictionaryAsync(x => x.ProductCode, x => x.Qty);

        // 下限 0 與 GetPlantAvailableQtyAsync 一致（超額預留不讓自由量變負數）
        return productCodes.ToDictionary(
            code => code,
            code => Math.Max(onHand.GetValueOrDefault(code, 0m) - reserved.GetValueOrDefault(code, 0m), 0m));
    }
}
