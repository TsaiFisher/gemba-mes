using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 完工入庫作業（mfg-domain.md §5.4 / §17.2 Phase F）。
    /// 列出 Completed 工單，確認目標倉庫/儲位後觸發 IT 工單繳庫 + Backflush 後沖。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class ProductionCompletionController : ModuleControllerBase
    {
        private const string MenuKey = "ProductionCompletion";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IProductionCompletionService _completion;
        private readonly IPlantParamService _plantParams;
        private readonly ISamplingPlanService _sampling;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IPlantDefaultLocationResolver _plantDefaults;

        public ProductionCompletionController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IProductionCompletionService completion,
            IPlantParamService plantParams,
            ISamplingPlanService sampling,
            IUomService uomService,
            ICurrencyService currencyService,
            IPlantDefaultLocationResolver plantDefaults)
        {
            _localizer  = localizer;
            _completion = completion;
            _plantParams   = plantParams;
            _sampling   = sampling;
            _uomService = uomService;
            _currencyService = currencyService;
            _plantDefaults   = plantDefaults;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "work_order_id_desc",
            string searchText = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort   = sortOrder;
            ViewBag.SearchText    = searchText;
            ViewBag.Searched      = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<WorkOrder>?)null);

            IQueryable<WorkOrder> query = Db.WorkOrders.AsNoTracking()
                .Where(w => w.Status == WorkOrderStatus.Completed);

            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(w =>
                    w.WorkOrderId.Contains(searchText) ||
                    w.ProductCode.Contains(searchText));

            query = sortOrder switch
            {
                "work_order_id"  => query.OrderBy(w => w.WorkOrderId),
                "planned_end"    => query.OrderBy(w => w.PlannedEndDate),
                "planned_end_desc" => query.OrderByDescending(w => w.PlannedEndDate),
                _                => query.OrderByDescending(w => w.WorkOrderId)
            };

            var list = await PaginatedList<WorkOrder>.CreateAsync(query, pageNumber ?? 1, pageSize);
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.ProductUomMap = await Db.Products.AsNoTracking()
                .Select(p => new { p.ProductCode, p.StockUom }).ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            return View(list);
        }

        /// <summary>載入入庫確認 Side Panel（GET）。</summary>
        public async Task<IActionResult> ConfirmForm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var wo = await Db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null) return NotFound();

            // 計算完工量（最後一道工序；與完工入庫服務同一個定義）
            var qtyDone = await WorkOrderOutput.CompletedQtyAsync(Db, id);

            // 取預設成品倉。系統參數是全域單值，而倉庫代碼只屬於某一個廠（`Warehouse` 主鍵為
            // `WhCode` 單欄且是 IPlantEntity）——不驗廠別的話，在沒有該代碼的廠會預選一個
            // 下拉選單裡根本不存在的值（表單靜默失去預設），在別廠則是指向他廠倉庫。
            var (paramWh, paramLoc) = await _plantDefaults.ResolveAsync(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgFgWarehouse, UserContext.PlantId),
                await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgFgLocation, UserContext.PlantId),
                UserContext.PlantId);

            // 產品類別的預設倉儲優先於系統參數：系統參數是全廠單一的「成品倉」，
            // 而工單的產出可能是半成品（WIP）——半成品照預設收進成品倉時**不會有任何錯誤**，
            // 錯誤要等到下一張母件工單後沖時才爆，訊息是「可用量不足」、指向完全不同的地方，
            // 而後沖與入庫在同一個交易裡，整張母件工單從此再也入不了庫（W2-F6）。
            // 後沖取的正是同一個「產品類別預設倉儲」，兩邊同源才不會各指一處。
            var (resolvedWh, resolvedLoc) = await _plantDefaults.ResolveForProductAsync(
                wo.ProductCode, UserContext.PlantId, paramWh ?? "", paramLoc ?? "");
            var defaultWh  = resolvedWh;
            var defaultLoc = resolvedLoc;

            // 所有可用倉庫（成品倉 WarehouseType = Finished 或 Raw，讓使用者選）
            var warehouses = await Db.Set<Warehouse>().AsNoTracking()
                .Where(w => w.PlantId == UserContext.PlantId && w.IsActive)
                .OrderBy(w => w.WhCode)
                .ToListAsync();

            ViewBag.QtyDone    = qtyDone;
            ViewBag.DefaultWh  = defaultWh;
            ViewBag.DefaultLoc = defaultLoc;
            ViewBag.Warehouses = warehouses;
            ViewBag.ProductUom = await Db.Products.AsNoTracking()
                .Where(p => p.ProductCode == wo.ProductCode)
                .Select(p => p.StockUom).FirstOrDefaultAsync();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            // 分箱入庫（container-tracking.md §8 L1+）：預設容器＝最後一道工序的完工容器，每箱數量＝其最大容量
            var containers = await Db.Containers.AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.ContainerCode)
                .ToListAsync();
            var defaultContainer = await ReportContainers.LastOperationOutContainerAsync(Db, id);
            ViewBag.Containers       = containers;
            ViewBag.DefaultContainer = defaultContainer;
            ViewBag.DefaultBoxQty    = containers.FirstOrDefault(c => c.ContainerCode == defaultContainer)?.MaxCapacity;

            return PartialView("_ConfirmForm", wo);
        }

        /// <summary>執行完工入庫（POST）。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Complete(string id, string whCode, string locationCode, string? lotNo,
            bool packByBox = false, string? containerCode = null, decimal? boxQty = null, string? boxUnitIds = null)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            if (string.IsNullOrWhiteSpace(whCode) || string.IsNullOrWhiteSpace(locationCode))
                return JsonFail(_localizer["WhAndLocationRequired"].Value);

            var wo = await Db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null)
                return JsonFail(_localizer["RecordNotFound"].Value);

            if (wo.Status != WorkOrderStatus.Completed)
                return JsonFail(_localizer["WorkOrderNotCompleted"].Value);

            // ── FQC 閘控（Phase G-H）：InspectionSpec.RequiresFQC = true 時
            //    必須先通過 FQC 才允許入庫（mobile-pwa.md §14.1）
            var plantId = UserContext.PlantId ?? wo.PlantId ?? "";
            var fqcSpec = await Db.InspectionSpecs.AsNoTracking()
                .Where(s => s.PlantId == plantId && s.IsActive && s.RequiresFQC)
                .FirstOrDefaultAsync(s => s.ProductCode == wo.ProductCode)
                ?? await Db.InspectionSpecs.AsNoTracking()
                    .Where(s => s.PlantId == plantId && s.IsActive && s.RequiresFQC)
                    .FirstOrDefaultAsync(s => s.ProductCode == null);

            if (fqcSpec != null)
            {
                var fqcPassed = await Db.InspectionOrders.AsNoTracking().AnyAsync(o =>
                    o.InspectionType == "FQC" &&
                    o.SourceDocId    == id     &&
                    o.Status         == "Pass");

                if (!fqcPassed)
                    return JsonFail(_localizer["FQCRequired"].Value);
            }

            // 分箱：每箱數量照產品庫存單位捨入（與完工量同一個單位）；有效性（>0、箱數上限、批號重複）由服務把關
            CompletionBoxing? boxing = null;
            if (packByBox)
            {
                var uom = await Db.Products.AsNoTracking()
                    .Where(p => p.ProductCode == wo.ProductCode)
                    .Select(p => p.StockUom).FirstOrDefaultAsync();
                // 箱號：一行一個（掃描槍逐一掃入），也接受逗號／空白分隔
                var unitIds = (boxUnitIds ?? "")
                    .Split(new[] { '\r', '\n', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                boxing = new CompletionBoxing(
                    await _uomService.RoundQtyAsync(uom, boxQty ?? 0m),
                    string.IsNullOrWhiteSpace(containerCode) ? null : containerCode.Trim(),
                    unitIds.Length == 0 ? null : unitIds,
                    UserContext.UserId);
            }

            try
            {
                if (boxing == null)
                {
                    await _completion.CompleteAsync(id, plantId, whCode, locationCode, lotNo);
                    return JsonOk(message: _localizer["CompletionSuccess"].Value);
                }

                await _completion.CompleteAsync(id, plantId, whCode, locationCode, lotNo, boxing);
                // 有綁箱號＝箱子身上已有固定 QR（箱號，掃了即時查目前內容），不再給批號箱標籤：
                // 同一箱貼兩種 QR 現場不知道掃哪張，且批號標籤在換箱／換批號後就過時（2026-11-05 使用者裁決）。
                // 沒綁箱號時批號標籤是分辨各箱的唯一方式，照舊提供。
                var labelUrl = boxing.UnitIds is { Count: > 0 }
                    ? null
                    : Url.Action(nameof(BoxLabels), new { id, containerCode = boxing.ContainerCode });
                return JsonOk(data: new { labelUrl }, message: _localizer["CompletionSuccess"].Value);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 並發完工入庫：另一請求已先繳庫，本次整段交易已回滾，未重複過帳。
                Response.StatusCode = StatusCodes.Status409Conflict;
                return JsonFail(_localizer["ConcurrencyConflict"].Value);
            }
            catch (InvalidOperationException ex)
            {
                return JsonFail(ex.Message);
            }
        }

        /// <summary>分箱入庫「帶入空箱」：本廠該種類可用的空箱號（最多 take 個）；裝著本工單在製品的箱子排最前。</summary>
        [HttpGet]
        public async Task<IActionResult> EmptyBoxUnits(string containerCode, int take, string? workOrderId = null)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            if (string.IsNullOrWhiteSpace(containerCode) || take <= 0) return JsonOk(data: Array.Empty<string>());
            var ids = await new ContainerUnitService(Db).EmptyUnitIdsAsync(containerCode, Math.Min(take, CompletionBoxing.MaxBoxes), workOrderId);
            return JsonOk(data: ids);
        }

        /// <summary>
        /// 批號箱標籤（container-tracking.md §8 L1+）：**只給沒綁箱號的分箱入庫用**——有綁箱號時箱子已貼固定 QR（箱號），
        /// 完工入庫不提供此連結（§8.5）。每個批號一張，QR 內容＝批號，手機掃標籤即等於掃批號。以庫存異動為準（依批號淨額，作廢回沖後的批號不再印），
        /// 所以入庫後任何時候都能重印，不必另存一份箱清單。
        /// </summary>
        public async Task<IActionResult> BoxLabels(string id, string? containerCode = null)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();

            var wo = await Db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null) return NotFound();

            var boxes = (await Db.StockMovements.AsNoTracking()
                    .Where(m => m.DocNo == id && m.TypeCode == "IT" && m.ProductCode == wo.ProductCode)
                    .Select(m => new { m.LotNo, m.StockQty, m.Direction, m.StockUomCode, m.MovementDate })
                    .ToListAsync())
                .GroupBy(m => m.LotNo)
                .Select(g => new BoxLabel(
                    g.Key, g.Sum(m => m.StockQty * m.Direction), g.First().StockUomCode, g.Min(m => m.MovementDate),
                    PrintHelper.GenerateQrSvg(g.Key == "" ? id : g.Key, 4)))
                .Where(b => b.Qty > 0)
                .OrderBy(b => b.LotNo, StringComparer.Ordinal)
                .ToList();
            if (boxes.Count == 0) return NotFound();

            ViewBag.WorkOrderId   = id;
            ViewBag.ProductCode   = wo.ProductCode;
            ViewBag.ProductName   = await Db.Products.AsNoTracking()
                .Where(p => p.ProductCode == wo.ProductCode).Select(p => p.ProductName).FirstOrDefaultAsync();
            ViewBag.ContainerName = string.IsNullOrEmpty(containerCode) ? null
                : await Db.Containers.AsNoTracking()
                    .Where(c => c.ContainerCode == containerCode).Select(c => c.ContainerName).FirstOrDefaultAsync();
            ViewBag.Formats       = await PrintFormatsAsync();
            ViewBag.PrintTime     = await PlantNowAsync();
            return View(boxes);
        }

        public readonly record struct BoxLabel(string LotNo, decimal Qty, string UomCode, DateOnly ReceivedOn, string QrSvg);
    }
}
