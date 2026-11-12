using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using System.Text.Json;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class RoutingController : ModuleControllerBase
    {
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public RoutingController(IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "routing_id", string searchText = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead("Routing")) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync("Routing");
            if (!searched) return View((PaginatedList<Routing>?)null);

            IQueryable<Routing> query = Db.Routings.AsNoTracking().Include(r => r.Operations);

            if (!string.IsNullOrEmpty(searchText))
            {
                query = query.Where(r =>
                    r.RoutingId.Contains(searchText) ||
                    (r.ProductCode != null && r.ProductCode.Contains(searchText)) ||
                    (r.RoutingName != null && r.RoutingName.Contains(searchText)));
            }

            query = sortOrder switch
            {
                "routing_id_desc"    => query.OrderByDescending(r => r.RoutingId),
                "product_code"       => query.OrderBy(r => r.ProductCode),
                "product_code_desc"  => query.OrderByDescending(r => r.ProductCode),
                _                    => query.OrderBy(r => r.RoutingId)
            };

            var list = await PaginatedList<Routing>.CreateAsync(query, pageNumber ?? 1, pageSize);

            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync("Routing", id)) return Forbid();
            var routing = await Db.Routings
                .Include(r => r.Operations!)
                .ThenInclude(op => op.Boms)
                .FirstOrDefaultAsync(r => r.RoutingId == id);

            if (routing == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            ViewBag.IsReadOnly = true;
            await PrepareFormDropdowns(routing.RoutingId);
            return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
        }

        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate("Routing")) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new Routing()) : View("_Form", new Routing());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Routing routing, string operationsJson = "")
        {
            if (!await Permissions.CanCreate("Routing"))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoCreatePermission"].Value)});</script>", "text/html") : Forbid();

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            if (HasSelfReferencingBom(operationsJson, routing.ProductCode))
            {
                ModelState.AddModelError("", _localizer["BomSelfReferenceError"].Value);
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            if (HasDuplicateSeq(operationsJson))
            {
                ModelState.AddModelError("", _localizer["DuplicateOperationSeq"].Value);
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            // 主鍵重複檢查：PK 只有 RoutingId 不含 PlantId，別廠已用的代碼在本廠
            // 查詢看不到（廠別 Query Filter）仍會撞 PK，故須 IgnoreQueryFilters 並分開提示。
            var dupSamePlant = await Db.Routings.AnyAsync(r => r.RoutingId == routing.RoutingId);
            if (dupSamePlant || await Db.Routings.IgnoreQueryFilters()
                    .AnyAsync(r => r.RoutingId == routing.RoutingId))
            {
                ModelState.AddModelError(nameof(routing.RoutingId),
                    _localizer[dupSamePlant ? "DuplicateCode" : "DuplicateCodeOtherPlant"].Value);
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            routing.PlantId ??= UserContext.PlantId;
            routing.CreatedAt = routing.UpdatedAt = DateTime.UtcNow;

            // header + 工序/BOM 以單一交易包覆：全有或全無，避免 header 已寫入但工序重建失敗。
            await using (var tx = await Db.Database.BeginTransactionAsync())
            {
                Db.Routings.Add(routing);
                await Db.SaveChangesAsync();

                if (!string.IsNullOrEmpty(operationsJson))
                    await UpdateOperations(routing.RoutingId, operationsJson);

                await tx.CommitAsync();
            }

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["CreateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await Permissions.CanUpdate("Routing")) return Forbid();
            var routing = await Db.Routings
                .Include(r => r.Operations!)
                .ThenInclude(op => op.Boms)
                .FirstOrDefaultAsync(r => r.RoutingId == id);

            if (routing == null) return NotFound();

            await PrepareFormDropdowns(routing.RoutingId);
            return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, Routing routing, string operationsJson = "")
        {
            if (!await Permissions.CanUpdate("Routing"))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoUpdatePermission"].Value)});</script>", "text/html") : Forbid();

            if (id != routing.RoutingId) return NotFound();

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            if (HasSelfReferencingBom(operationsJson, routing.ProductCode))
            {
                ModelState.AddModelError("", _localizer["BomSelfReferenceError"].Value);
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            if (HasDuplicateSeq(operationsJson))
            {
                ModelState.AddModelError("", _localizer["DuplicateOperationSeq"].Value);
                await PrepareFormDropdowns(routing.RoutingId);
                return IsAjax ? PartialView("_Form", routing) : View("_Form", routing);
            }

            var existing = await Db.Routings.FirstOrDefaultAsync(r => r.RoutingId == id);
            if (existing == null) return NotFound();

            existing.ProductCode   = routing.ProductCode;
            existing.RoutingName   = routing.RoutingName;
            existing.BatchSize     = routing.BatchSize;
            existing.BufferDays    = routing.BufferDays;
            existing.AvgLeadTime   = routing.AvgLeadTime;
            existing.Version       = routing.Version;
            existing.EffectiveFrom = routing.EffectiveFrom;
            existing.EffectiveTo   = routing.EffectiveTo;
            existing.IsActive      = routing.IsActive;
            existing.UpdatedAt     = DateTime.UtcNow;

            // header + 工序/BOM 以單一交易包覆：全有或全無，避免 header 已更新但工序重建失敗（舊工序已刪、新工序未入）。
            await using (var tx = await Db.Database.BeginTransactionAsync())
            {
                Db.Update(existing);
                await Db.SaveChangesAsync();

                if (!string.IsNullOrEmpty(operationsJson))
                    await UpdateOperations(existing.RoutingId, operationsJson);

                await tx.CommitAsync();
            }

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete("Routing"))
                return IsAjax ? Json(new { success = false, message = _localizer["NoDeletePermission"].Value }) : Forbid();

            var routing = await Db.Routings.FindAsync(id);

            if (routing == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            Db.Routings.Remove(routing);
            await Db.SaveChangesAsync();

            var message = _localizer["DeleteSuccess"].Value;
            if (IsAjax) return Json(new { success = true, message });
            TempData["SuccessMessage"] = message;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private async Task PrepareFormDropdowns(string? routingId = null)
        {
            // 等候時間建議值（每晚由實際報工統計，§23.2-2）：只顯示、不改主檔，工藝人員在工序表單按「套用」後存檔才生效
            ViewBag.QueueSuggestions = string.IsNullOrEmpty(routingId)
                ? new Dictionary<int, RoutingQueueTimeSuggestion>()
                : await Db.RoutingQueueTimeSuggestions.AsNoTracking()
                    .Where(x => x.RoutingId == routingId)
                    .ToDictionaryAsync(x => x.OperationSeq);

            ViewBag.WorkCenters = await Db.Set<SmartFactory.MES.Modules.BAS.Models.WorkCenter>()
                .AsNoTracking()
                .Where(wc => wc.IsActive)
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            ViewBag.Uoms = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Uom>()
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName })
                .ToListAsync();

            // 途程主檔/用料品號 select2 來源；ProductStockUom 供「選品號自動帶庫存單位」（mfg-domain.md §3.1）
            var products = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Product>()
                .AsNoTracking()
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom })
                .ToListAsync();
            ViewBag.Products = products;
            ViewBag.ProductStockUom = products.ToDictionary(p => p.ProductCode, p => p.StockUom ?? "");

            // 工序開工/完工容器 select 來源（mfg-domain.md §3.1(5)）
            var containerList = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Container>()
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.ContainerCode)
                .Select(c => new { c.ContainerCode, c.ContainerName, c.MaxCapacity })
                .ToListAsync();
            ViewBag.Containers = containerList;
            // 容器容量 map：定量(CD_BOR=2)模式選完工容器時，帶為移轉批量 QT_BOR 預設值（仍可改）
            ViewBag.ContainerCapacity = containerList
                .Where(c => c.MaxCapacity.HasValue)
                .ToDictionary(c => c.ContainerCode, c => c.MaxCapacity!.Value);

            // 工序治具/模具 select 來源（mfg-domain.md §3.1(8)）
            ViewBag.Toolings = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Tooling>()
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.ToolingCode)
                .Select(t => new { t.ToolingCode, t.ToolingName })
                .ToListAsync();

            await PrepareIndexPermissionsAsync("Routing");
        }

        // 防遞迴（mfg-domain.md §3.1(6)）：用料品號不可等於途程自身品號（僅當品號有值時適用）
        private static bool HasSelfReferencingBom(string operationsJson, string? productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode) || string.IsNullOrEmpty(operationsJson)) return false;
            List<OperationDto>? opDtos;
            try
            {
                opDtos = JsonSerializer.Deserialize<List<OperationDto>>(
                    operationsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return false; }   // 壞 JSON 交由 UpdateOperations 處理
            return opDtos?.Any(op => op.Boms?.Any(b =>
                string.Equals(b.ProductCode, productCode, StringComparison.Ordinal)) == true) == true;
        }

        // 工序序號/用料序號重複會撞 (RoutingId, OperationSeq[, BomSeq]) 複合 PK——
        // EF identity map 在 Add 階段即擲 InvalidOperationException（非 DbUpdateException），
        // 全域 23505 轉譯 Filter 接不到，故必須在此先驗證。
        private static bool HasDuplicateSeq(string operationsJson)
        {
            if (string.IsNullOrEmpty(operationsJson)) return false;
            List<OperationDto>? opDtos;
            try
            {
                opDtos = JsonSerializer.Deserialize<List<OperationDto>>(
                    operationsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return false; }   // 壞 JSON 交由 UpdateOperations 處理
            if (opDtos == null) return false;
            if (opDtos.GroupBy(op => op.OperationSeq).Any(g => g.Count() > 1)) return true;
            return opDtos.Any(op => op.Boms != null &&
                op.Boms.GroupBy(b => b.BomSeq).Any(g => g.Count() > 1));
        }

        private async Task UpdateOperations(string routingId, string operationsJson)
        {
            // Deserialize FIRST — 壞資料不可先把既有工序刪掉（原子性：呼叫端 Create/Edit 以交易包覆刪+插）。
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var opDtos = JsonSerializer.Deserialize<List<OperationDto>>(operationsJson, opts);
            if (opDtos == null) return;   // null 字面 → 不動既有資料

            // Remove existing operations (cascade deletes boms)
            var existingOps = await Db.RoutingOperations
                .Include(op => op.Boms)
                .Where(op => op.RoutingId == routingId)
                .ToListAsync();

            Db.RoutingOperations.RemoveRange(existingOps);
            await Db.SaveChangesAsync();

            if (opDtos.Count == 0) return;   // 明確空清單 → 全部清空、不再插入

            var newOps = opDtos.Select(dto => new RoutingOperation
            {
                RoutingId           = routingId,
                OperationSeq        = dto.OperationSeq,
                OperationName       = dto.OperationName,
                WorkCenterId        = dto.WorkCenterId,
                UomCode             = dto.UomCode,
                CD_BOR              = (OperationMode)dto.CD_BOR,
                QT_BOR              = dto.QT_BOR,
                TM_SETUP            = dto.TM_SETUP,
                TM_UNIT             = dto.TM_UNIT,
                TimeUnit            = (OperationTimeUnit)dto.TimeUnit,
                SetupTimeUnit       = (OperationTimeUnit)dto.SetupTimeUnit,
                MoveTime            = dto.MoveTime,
                QueueTime           = dto.QueueTime,
                SopNumber           = dto.SopNumber,
                ContainerInCode     = string.IsNullOrWhiteSpace(dto.ContainerInCode) ? null : dto.ContainerInCode,
                ContainerOutCode    = string.IsNullOrWhiteSpace(dto.ContainerOutCode) ? null : dto.ContainerOutCode,
                ToolingCode         = string.IsNullOrWhiteSpace(dto.ToolingCode) ? null : dto.ToolingCode,
                IsOutsource         = dto.IsOutsource,
                RequiresFpi         = dto.RequiresFpi,
                RequiresInspection  = dto.RequiresInspection,
                YieldRate           = dto.YieldRate,
                Boms = (dto.Boms ?? new List<BomDto>()).Select(b => new RoutingBom
                {
                    RoutingId    = routingId,
                    OperationSeq = dto.OperationSeq,
                    BomSeq       = b.BomSeq,
                    ProductCode  = b.ProductCode,
                    Quantity     = b.Quantity,
                    UomCode      = b.UomCode,
                    ScrapRate    = b.ScrapRate,
                    OffcutRate   = b.OffcutRate,
                    // 存檔是整份重建：沒帶這兩欄的話，存一次就把發料方式洗回未設定（＝發料）
                    IssueMethod     = b.IssueMethod,
                    BackflushTiming = b.BackflushTiming,
                }).ToList()
            }).ToList();

            Db.RoutingOperations.AddRange(newOps);
            await Db.SaveChangesAsync();
        }

        private class OperationDto
        {
            public int OperationSeq { get; set; }
            public string? OperationName { get; set; }
            public string WorkCenterId { get; set; } = string.Empty;
            public string UomCode { get; set; } = string.Empty;
            public int CD_BOR { get; set; } = 3;
            public decimal QT_BOR { get; set; } = 1;
            public decimal TM_SETUP { get; set; }
            public decimal TM_UNIT { get; set; }
            public int TimeUnit { get; set; } = 2;   // OperationTimeUnit.Minute
            public int SetupTimeUnit { get; set; } = 2;   // OperationTimeUnit.Minute
            public decimal MoveTime { get; set; }
            public decimal QueueTime { get; set; }
            public string? SopNumber { get; set; }
            public string? ContainerInCode { get; set; }
            public string? ContainerOutCode { get; set; }
            public string? ToolingCode { get; set; }
            public bool IsOutsource { get; set; }
            public bool RequiresFpi { get; set; }
            public bool RequiresInspection { get; set; }
            public decimal YieldRate { get; set; } = 100m;
            public List<BomDto>? Boms { get; set; }
        }

        private class BomDto
        {
            public int BomSeq { get; set; }
            public string ProductCode { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public string UomCode { get; set; } = string.Empty;
            public decimal ScrapRate { get; set; }
            public decimal OffcutRate { get; set; }
            public MaterialIssueMethod? IssueMethod { get; set; }
            public BackflushTiming BackflushTiming { get; set; }
        }
    }
}
