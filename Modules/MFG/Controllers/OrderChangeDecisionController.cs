using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 訂單變更決策清單（§21.2 / 總體評估 E）。唯讀工作清單：列出「已投產（工單 InProgress/Completed）」
    /// 因而被 §21.2 鎖定無法直接修改的來源訂單，供生產/業務人員人工決策（先暫停工單 → 改單，或接受現況）。
    /// 投產前的縮量已由 §21.7 自動釋放預留 + MRP 重跑處理，不在此清單。
    /// </summary>
    [Authorize]
    public class OrderChangeDecisionController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IUomService _uomService;

        private const string MenuKey = "OrderChangeDecision";

        public OrderChangeDecisionController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IUomService uomService)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _uomService = uomService;
        }

        public async Task<IActionResult> Index(
            string? searchProduct, bool onlyDirty = false, string? sortOrder = null,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.OrderSort       = sortOrder == "order_asc" ? "order_desc" : "order_asc";
            ViewBag.SearchProduct   = searchProduct;
            ViewBag.OnlyDirty       = onlyDirty;
            ViewBag.ProductOptions  = await _context.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName }).ToListAsync();

            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            if (!searched) return View((PaginatedList<OrderChangeDecisionRow>?)null);

            var plantId = _userContext.PlantId;

            var query = from ml in _context.MrpRunLines.AsNoTracking()
                        join wo in _context.WorkOrders.AsNoTracking()
                            on ml.ProductionBatchId equals wo.ProductionBatchId
                        join run in _context.MrpRuns.AsNoTracking()
                            on ml.MrpRunId equals run.MrpRunId
                        join p in _context.Products.AsNoTracking()
                            on wo.ProductCode equals p.ProductCode into pJoin
                        from p in pJoin.DefaultIfEmpty()
                        where wo.PlantId == plantId
                           && ml.SalesOrderId != null
                           && (wo.Status == WorkOrderStatus.InProgress || wo.Status == WorkOrderStatus.Completed)
                        select new OrderChangeDecisionRow
                        {
                            SourceOrderId   = ml.SalesOrderId!,
                            ProductCode     = wo.ProductCode,
                            UomCode         = p != null ? p.StockUom : null,
                            WorkOrderId     = wo.WorkOrderId,
                            WorkOrderStatus = wo.Status,
                            PlannedQty      = wo.Quantity,
                            MrpRunId        = ml.MrpRunId,
                            IsDirty         = run.IsDirty,
                        };

            if (!string.IsNullOrWhiteSpace(searchProduct))
                query = query.Where(r => r.ProductCode == searchProduct || r.SourceOrderId.Contains(searchProduct));
            if (onlyDirty)
                query = query.Where(r => r.IsDirty);

            query = sortOrder switch
            {
                "order_asc"  => query.OrderBy(r => r.SourceOrderId).ThenBy(r => r.WorkOrderId),
                "order_desc" => query.OrderByDescending(r => r.SourceOrderId).ThenBy(r => r.WorkOrderId),
                _            => query.OrderByDescending(r => r.IsDirty).ThenBy(r => r.SourceOrderId),
            };

            var paged = await PaginatedList<OrderChangeDecisionRow>.CreateAsync(
                query, pageNumber ?? 1, pageSize ?? 20);

            // 來源訂單型別（SO vs IO）：以單號集合一次查詢解析，避免逐列查 DB。
            var ids = paged.Select(r => r.SourceOrderId).Distinct().ToList();
            var soSet = (await _context.SalesOrders.AsNoTracking()
                .Where(s => ids.Contains(s.SalesOrderId))
                .Select(s => s.SalesOrderId).ToListAsync()).ToHashSet();
            foreach (var r in paged)
                r.SourceType = soSet.Contains(r.SourceOrderId) ? "SO" : "IO";

            return View(paged);
        }
    }

    /// <summary>唯讀顯示投影列。欄位標題由 View 的 IViewLocalizer（Index.*.resx）在地化，
    /// 此處非表單綁定欄位，故值型別欄位以 [Display] 標註，排除於驗證欄位 key 慣例（R5b）外。</summary>
    public class OrderChangeDecisionRow
    {
        public string SourceOrderId    { get; set; } = "";
        public string SourceType       { get; set; } = "";
        public string ProductCode      { get; set; } = "";
        public string? UomCode         { get; set; }
        public string WorkOrderId      { get; set; } = "";
        [Display(Name = "WO Status")]
        public WorkOrderStatus WorkOrderStatus { get; set; }
        [Display(Name = "Planned Qty")]
        public decimal PlannedQty      { get; set; }
        public string MrpRunId         { get; set; } = "";
        public bool IsDirty            { get; set; }
    }
}
