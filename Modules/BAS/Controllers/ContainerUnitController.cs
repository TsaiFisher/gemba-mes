using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    /// <summary>
    /// 箱子（容器個體）維護：批次建立箱號、列印固定 QR 標籤、查目前內容與歷程、清空、停用。
    /// 規則集中在 <see cref="IContainerUnitService"/>（container-tracking.md §9）。
    /// </summary>
    [Authorize]
    public class ContainerUnitController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IContainerUnitService _units;
        private readonly IContainerPackService _pack;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "ContainerUnit";

        public ContainerUnitController(ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IContainerUnitService units, IContainerPackService pack,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _units = units; _pack = pack; _localizer = localizer;
        }

        /// <summary>
        /// 清單列：在手量由綁定批號的庫存推導（<see cref="IContainerUnitService.BoundQtyAsync"/> 同口徑）；
        /// 在製狀態與數量同 <see cref="IContainerUnitService.IsInWipAsync"/>／<see cref="IContainerUnitService.WipQtyAsync"/>。
        /// </summary>
        public readonly record struct UnitRow(
            string ContainerUnitId, string ContainerCode, string? ProductCode, string? LotNo,
            decimal Qty, bool IsActive, DateTime? BoundAt,
            string? WorkOrderId, int? OperationSeq, bool InWip, decimal WipQty,
            string? OutTypeCode, string? OutDocNo)
        {
            /// <summary>在廠外：綁定批號已出完，且最後一筆出庫是出廠類異動（同 <see cref="IContainerUnitService.OffsiteAsync"/>）。</summary>
            public bool Offsite => LotNo != null && Qty <= 0 && OutTypeCode != null
                                   && ContainerUnitService.LeavePlantTypeCodes.Contains(OutTypeCode);
        }

        public async Task<IActionResult> Index(
            string? searchTerm, string? containerCode, string? status,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            await PrepareIndexPermissionsAsync(MenuKey);

            ViewBag.SearchTerm = searchTerm; ViewBag.ContainerCode = containerCode; ViewBag.Status = status;
            ViewBag.Searched = searched; ViewBag.CurrentPageSize = pageSize ?? 20; ViewBag.CurrentSort = sortOrder;
            ViewBag.ContainerOptions = await ContainerOptionsAsync(activeOnly: false);

            if (!searched) return View((PaginatedList<UnitRow>?)null);

            // 先在實體上篩選、最後才投影：「使用中／空箱」用相關子查詢判斷（負庫存不允許，有正數列＝使用中）。
            // 在製要看工單狀態，工單有資料權限過濾——整條查詢關掉全域過濾、廠別自己帶，
            // 否則看不到那張工單的角色會把在製箱看成空箱
            var plant = _userContext.PlantId;
            var inv = _context.Inventories;
            var wos = _context.WorkOrders;
            var prc = _context.ProductionReportContainers;
            var reps = _context.ProductionReports;
            var dps = _context.Dispatches;
            var moves = _context.StockMovements;
            var leave = ContainerUnitService.LeavePlantTypeCodes;
            var q = _context.ContainerUnits.IgnoreQueryFilters().AsNoTracking().Where(u => u.PlantId == plant);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                // 掃箱號或批號都找得到：掃描槍掃到的是箱上的固定 QR
                var term = searchTerm.Trim();
                q = q.Where(u => u.ContainerUnitId.Contains(term)
                              || (u.LotNo != null && u.LotNo.Contains(term))
                              || (u.ProductCode != null && u.ProductCode.Contains(term)));
            }
            if (!string.IsNullOrEmpty(containerCode)) q = q.Where(u => u.ContainerCode == containerCode);
            q = status switch
            {
                "empty"    => q.Where(u => u.IsActive && !inv.Any(i => i.PlantId == u.PlantId && i.ProductCode == u.ProductCode
                                                                   && i.LotNo == u.LotNo && i.QtyOnHand > 0)
                                       && !(u.LotNo != null && leave.Contains(moves.Where(m => m.PlantId == u.PlantId && m.ProductCode == u.ProductCode
                                                                       && m.LotNo == u.LotNo && m.Direction == -1)
                                                              .OrderByDescending(m => m.CreatedAt)
                                                              .Select(m => m.TypeCode).FirstOrDefault()))
                                       && !(u.WorkOrderId != null && wos.Any(w => w.WorkOrderId == u.WorkOrderId && w.PlantId == u.PlantId
                                                && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Void))),
                "wip"      => q.Where(u => u.WorkOrderId != null && wos.Any(w => w.WorkOrderId == u.WorkOrderId && w.PlantId == u.PlantId
                                                && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Void)),
                "inuse"    => q.Where(u => inv.Any(i => i.PlantId == u.PlantId && i.ProductCode == u.ProductCode
                                                     && i.LotNo == u.LotNo && i.QtyOnHand > 0)),
                "offsite"  => q.Where(u => u.LotNo != null
                                       && !inv.Any(i => i.PlantId == u.PlantId && i.ProductCode == u.ProductCode
                                                     && i.LotNo == u.LotNo && i.QtyOnHand > 0)
                                       && leave.Contains(moves.Where(m => m.PlantId == u.PlantId && m.ProductCode == u.ProductCode
                                                                       && m.LotNo == u.LotNo && m.Direction == -1)
                                                              .OrderByDescending(m => m.CreatedAt)
                                                              .Select(m => m.TypeCode).FirstOrDefault())),
                "inactive" => q.Where(u => !u.IsActive),
                _          => q,
            };
            q = sortOrder == "id_desc"
                ? q.OrderByDescending(u => u.ContainerUnitId)
                : q.OrderBy(u => u.ContainerUnitId);

            var rows = q.Select(u => new UnitRow(
                u.ContainerUnitId, u.ContainerCode, u.ProductCode, u.LotNo,
                inv.Where(i => i.PlantId == u.PlantId && i.ProductCode == u.ProductCode && i.LotNo == u.LotNo)
                   .Sum(i => (decimal?)i.QtyOnHand) ?? 0m,
                u.IsActive, u.BoundAt, u.WorkOrderId, u.OperationSeq,
                u.WorkOrderId != null && wos.Any(w => w.WorkOrderId == u.WorkOrderId && w.PlantId == u.PlantId
                    && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Void),
                (from c in prc
                 join r in reps on c.ReportId equals r.ReportId
                 join d in dps on r.DispatchId equals d.DispatchId
                 where c.ContainerUnitId == u.ContainerUnitId && r.WorkOrderId == u.WorkOrderId
                    && d.OperationSeq == u.OperationSeq && r.CreatedAt >= u.BoundAt
                 select (decimal?)c.ActualQty).Sum() ?? 0m,
                u.LotNo == null ? null : moves.Where(m => m.PlantId == u.PlantId && m.ProductCode == u.ProductCode
                                                    && m.LotNo == u.LotNo && m.Direction == -1)
                    .OrderByDescending(m => m.CreatedAt).Select(m => m.TypeCode).FirstOrDefault(),
                u.LotNo == null ? null : moves.Where(m => m.PlantId == u.PlantId && m.ProductCode == u.ProductCode
                                                    && m.LotNo == u.LotNo && m.Direction == -1)
                    .OrderByDescending(m => m.CreatedAt).Select(m => m.DocNo).FirstOrDefault()));

            return View(await PaginatedList<UnitRow>.CreateAsync(rows, pageNumber ?? 1, pageSize ?? 20));
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.ContainerOptions = await ContainerOptionsAsync(activeOnly: true);
            return PartialView("_CreateForm");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string containerCode, string prefix, int count)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return JsonFail(_localizer["NoCreatePermission"].Value);
            try
            {
                var ids = await _units.CreateBatchAsync(containerCode, prefix, count, _userContext.UserId);
                return JsonOk(message: _localizer["ContainerUnitCreatedFmt", ids.Count, ids[0], ids[^1]].Value);
            }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
        }

        [HttpGet]
        public async Task<IActionResult> Details(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var unit = await _context.ContainerUnits.AsNoTracking().FirstOrDefaultAsync(u => u.ContainerUnitId == id);
            if (unit == null) return NotFound();
            await PrepareIndexPermissionsAsync(MenuKey);

            ViewBag.ContainerName = await _context.Containers.AsNoTracking()
                .Where(c => c.ContainerCode == unit.ContainerCode).Select(c => c.ContainerName).FirstOrDefaultAsync();
            ViewBag.ProductName = unit.ProductCode == null ? null : await _context.Products.AsNoTracking()
                .Where(p => p.ProductCode == unit.ProductCode).Select(p => p.ProductName).FirstOrDefaultAsync();
            ViewBag.Content = await _units.ContentAsync(unit);
            ViewBag.InWip   = await _units.IsInWipAsync(unit);
            ViewBag.WipQty  = await _units.WipQtyAsync(unit);
            ViewBag.Offsite = await _units.OffsiteAsync(unit);
            ViewBag.History = await _context.ContainerUnitHistories.AsNoTracking()
                .Where(h => h.ContainerUnitId == id)
                .OrderByDescending(h => h.Id).Take(50).ToListAsync();
            return PartialView("_Details", unit);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Clear(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            try { await _units.ClearAsync(id, _userContext.UserId); }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
            catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        /// <summary>
        /// 既有庫存裝箱／換箱（§9.4 第 4 期）：選儲位上的一份庫存（或掃來源箱號＝換箱）裝進空箱。
        /// 換批號走同廠調撥，見 <see cref="IContainerPackService"/>。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Pack(string? id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            ViewBag.UnitId = id;
            return PartialView("_PackForm");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Pack(string containerUnitId, string productCode, string locationCode,
            string? lotNo, decimal qty)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            if (string.IsNullOrWhiteSpace(containerUnitId) || string.IsNullOrWhiteSpace(productCode) || string.IsNullOrWhiteSpace(locationCode))
                return JsonFail(_localizer["ContainerUnitPackIncomplete"].Value);
            try
            {
                var (transferId, lot) = await _pack.PackAsync(containerUnitId, productCode.Trim(), locationCode.Trim(),
                    lotNo, qty, _userContext.UserId);
                return JsonOk(message: _localizer["ContainerUnitPackedFmt", containerUnitId.Trim().ToUpperInvariant(), lot, transferId].Value);
            }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
            catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }
        }

        /// <summary>容器回收（§9.4）：掃回來的箱子一行一個；也接受單一箱子面板上的「回收」。</summary>
        [HttpGet]
        public async Task<IActionResult> Recover()
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            return PartialView("_RecoverForm");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Recover(string? unitIds)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            var ids = (unitIds ?? "").Split(new[] { '\n', '\r', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) return JsonFail(_localizer["ContainerUnitRecoverEmpty"].Value);

            var results = await _units.RecoverAsync(ids, _userContext.UserId);
            var ok = results.Count(r => r.Error == null);
            var failed = results.Where(r => r.Error != null).Select(r => r.Error!).ToList();
            var msg = _localizer["ContainerUnitRecoveredFmt", ok].Value;
            // 有箱子沒回收成功就走失敗路徑：訊息留在畫面上讓人看清楚是哪幾個（成功的已存檔）
            return failed.Count == 0 ? JsonOk(message: msg) : JsonFail(msg + "\n" + string.Join("\n", failed));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActive(string id, bool active)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            try { await _units.SetActiveAsync(id, active, _userContext.UserId); }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
            catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        /// <summary>固定標籤：貼在箱子上長期使用，QR＝箱號（不含內容，內容隨每次裝貨改變）。ids 以逗號分隔。</summary>
        [HttpGet]
        public async Task<IActionResult> Labels(string ids)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var units = await _context.ContainerUnits.AsNoTracking()
                .Where(u => wanted.Contains(u.ContainerUnitId))
                .ToListAsync();
            if (units.Count == 0) return NotFound();

            var names = await _context.Containers.AsNoTracking()
                .ToDictionaryAsync(c => c.ContainerCode, c => c.ContainerName);
            var labels = units
                .OrderBy(u => u.ContainerUnitId, StringComparer.Ordinal)
                .Select(u => new UnitLabel(u.ContainerUnitId,
                    names.TryGetValue(u.ContainerCode, out var n) ? n : u.ContainerCode,
                    PrintHelper.GenerateQrSvg(u.ContainerUnitId, 4)))
                .ToList();
            return View(labels);
        }

        public readonly record struct UnitLabel(string ContainerUnitId, string ContainerName, string QrSvg);

        private async Task<List<Container>> ContainerOptionsAsync(bool activeOnly)
            => await _context.Containers.AsNoTracking()
                .Where(c => !activeOnly || c.IsActive)
                .OrderBy(c => c.ContainerCode)
                .ToListAsync();
    }
}
