using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Npgsql;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using System.Text.Json;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// MRP 執行單（mfg-domain.md §4.1 / §5.1）：建立 Draft、編輯需求列、按「執行 MRP」
    /// 呼叫 <see cref="IMrpEngine"/> 展開為生產批/工單/派工，並回呈展開結果（採購/委外/缺料訊號）。
    /// 已 Executed 的執行單不可再編輯；可「重跑」（§10-5）：清除前次未投產展開後重新展開，
    /// 有工單已投產則擋下（WO_IN_PROGRESS）。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class MrpRunController : ModuleControllerBase
    {
        private const string MenuKey = "MrpRun";

        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IMrpEngine _engine;
        private readonly IDocNumberService _docNumber;
        private readonly IMrpRunExecutionService _execution;

        public MrpRunController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IMrpEngine engine,
            IDocNumberService docNumber,
            IMrpRunExecutionService execution)
        {
            _localizer = localizer;
            _engine = engine;
            _docNumber = docNumber;
            _execution = execution;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "mrp_run_id_desc",
            string searchText = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<MrpRun>?)null);

            IQueryable<MrpRun> query = Db.MrpRuns.AsNoTracking().Include(r => r.Lines);

            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(r =>
                    r.MrpRunId.Contains(searchText) ||
                    (r.Remarks != null && r.Remarks.Contains(searchText)));

            query = sortOrder switch
            {
                "mrp_run_id"      => query.OrderBy(r => r.MrpRunId),
                "executed_at"     => query.OrderBy(r => r.ExecutedAt),
                "executed_at_desc"=> query.OrderByDescending(r => r.ExecutedAt),
                _                 => query.OrderByDescending(r => r.MrpRunId)
            };

            var list = await PaginatedList<MrpRun>.CreateAsync(query, pageNumber ?? 1, pageSize);
            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var run = await Db.MrpRuns.AsNoTracking()
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.MrpRunId == id);

            if (run == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            ViewBag.IsReadOnly = true;
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", run) : View("_Form", run);
        }

        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new MrpRun()) : View("_Form", new MrpRun());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(MrpRun run, string linesJson = "")
        {
            if (!await Permissions.CanCreate(MenuKey))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoCreatePermission"].Value)});</script>", "text/html") : Forbid();

            var plantId = UserContext.PlantId;
            var runId = await _docNumber.NextAsync("MrpRun", plantId, DateOnly.FromDateTime(DateTime.UtcNow));

            run.MrpRunId = runId;
            run.Status = MrpRunStatus.Draft;
            run.PlantId ??= plantId;
            run.CreatedAt = run.UpdatedAt = DateTime.UtcNow;

            await using (var tx = await Db.Database.BeginTransactionAsync())
            {
                Db.MrpRuns.Add(run);
                await Db.SaveChangesAsync();
                await ReplaceLines(runId, linesJson);
                await tx.CommitAsync();
            }

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["CreateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var run = await Db.MrpRuns
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.MrpRunId == id);
            if (run == null) return NotFound();

            // 已展開的執行單以唯讀檢視呈現（含展開結果欄）
            ViewBag.IsReadOnly = run.Status != MrpRunStatus.Draft;
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", run) : View("_Form", run);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, MrpRun run, string linesJson = "", string doExecute = "")
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoUpdatePermission"].Value)});</script>", "text/html") : Forbid();

            if (id != run.MrpRunId) return NotFound();

            var existing = await Db.MrpRuns.FirstOrDefaultAsync(r => r.MrpRunId == id);
            if (existing == null) return NotFound();

            if (existing.Status != MrpRunStatus.Draft)
                return IsAjax
                    ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["MrpAlreadyExecuted"].Value)});</script>", "text/html")
                    : BadRequest();

            existing.Remarks = run.Remarks;
            existing.UpdatedAt = DateTime.UtcNow;

            await using (var tx = await Db.Database.BeginTransactionAsync())
            {
                Db.Update(existing);
                await Db.SaveChangesAsync();
                await ReplaceLines(id, linesJson);
                await tx.CommitAsync();
            }

            // 「執行 MRP」：交易包覆與請購單落地由 IMrpRunExecutionService 負責（§25.5 與訂單自動展開共用）
            if (doExecute == "1")
            {
                MrpRunResult result;
                try
                {
                    result = await _execution.ExecuteAsync(id);
                }
                catch (MrpExpansionValidationException vex)
                {
                    // 全有全無：預檢未過 → 整張回滾不產生；逐列 ValidationError 已由服務回填
                    return Content($"<script>alert({JsonSerializer.Serialize(vex.Message)});</script>", "text/html");
                }
                catch (Exception ex)
                {
                    return Content($"<script>alert({JsonSerializer.Serialize(FriendlyErrorMessage(ex))});</script>", "text/html");
                }
                return PartialView("_ExecuteResult", result);
            }

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>
        /// 重跑前置可行性評估（§10-5 分級閘門）：唯讀回傳可執行等級與逐項理由，供畫面在
        /// 使用者按下重跑之前預告後果——L3 直接擋下並說明原因，L2 則提示哪些下游單據
        /// 不會被自動收斂、需人工處理。不異動任何資料。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Evaluate(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var run = await Db.MrpRuns.AsNoTracking().FirstOrDefaultAsync(r => r.MrpRunId == id);
            if (run == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (run.Status != MrpRunStatus.Executed)
                return Json(new { success = false, message = _localizer["MrpNotExecuted"].Value });

            var eval = await _engine.EvaluateReRunAsync(id);
            return Json(new
            {
                success = true,
                level = eval.Level.ToString(),
                canReRun = eval.CanReRun,
                blocking = eval.Blocking.Select(i => new { kind = i.Kind.ToString(), documentId = i.DocumentId, description = i.Description }),
                residuals = eval.Residuals.Select(i => new { kind = i.Kind.ToString(), documentId = i.DocumentId, description = i.Description })
            });
        }

        /// <summary>
        /// 封存已展開的 MRP 執行單（§10-5 L3）：下游已有實體足跡無法重跑時，宣告此次展開不再維護。
        /// 不連鎖作廢下游單據；僅作廢完全未投產的生產批／工單，讓其需求回到可匯入清單由新單接手。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Archive(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            try
            {
                var voided = await _engine.ArchiveAsync(id);
                return Json(new
                {
                    success = true,
                    message = _localizer["MrpArchived", id, voided.Count].Value,
                    voidedBatchIds = voided
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = FriendlyErrorMessage(ex) });
            }
        }

        /// <summary>重跑已展開的 MRP 執行單（§10-5）：清除前次展開後重新展開；有工單已投產則擋下。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReRun(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoUpdatePermission"].Value)});</script>", "text/html");

            MrpRunResult result;
            try
            {
                result = await _execution.ReRunAsync(id);
            }
            catch (MrpExpansionValidationException vex)
            {
                // 全有全無：回滾後前次展開仍完整保留；失敗列的 ValidationError 已由服務回填
                return Content($"<script>alert({JsonSerializer.Serialize(vex.Message)});</script>", "text/html");
            }
            catch (Exception ex)
            {
                return Content($"<script>alert({JsonSerializer.Serialize(FriendlyErrorMessage(ex))});</script>", "text/html");
            }
            return PartialView("_ExecuteResult", result);
        }

        /// <summary>
        /// DbUpdateException 本身的訊息（"An error occurred while saving the entity changes…"）無資訊量：
        /// 唯一鍵/外鍵衝突轉在地化友善訊息，其餘取最內層例外訊息呈現。
        /// </summary>
        private string FriendlyErrorMessage(Exception ex)
        {
            for (var e = (Exception?)ex; e != null; e = e.InnerException)
            {
                if (e is PostgresException pg)
                {
                    if (pg.SqlState == PostgresErrorCodes.UniqueViolation)
                        return _localizer["DbUniqueViolation"].Value;
                    if (pg.SqlState == PostgresErrorCodes.ForeignKeyViolation)
                        return _localizer["DbForeignKeyViolation"].Value;
                }
            }
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.Message;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return IsAjax ? Json(new { success = false, message = _localizer["NoDeletePermission"].Value }) : Forbid();

            var run = await Db.MrpRuns.FindAsync(id);
            if (run == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            if (run.Status != MrpRunStatus.Draft)
                return Json(new { success = false, message = _localizer["MrpAlreadyExecuted"].Value });

            Db.MrpRuns.Remove(run);   // 級聯刪除 MrpRunLine
            await Db.SaveChangesAsync();

            var message = _localizer["DeleteSuccess"].Value;
            if (IsAjax) return Json(new { success = true, message });
            TempData["SuccessMessage"] = message;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ImportableOrderLines = await LoadImportableOrderLinesAsync();
            await PrepareIndexPermissionsAsync(MenuKey);
        }

        /// <summary>
        /// 可匯入的未結案訂單明細（已核准的「銷售訂單」與「內部訂單」），附**淨需求剩餘量**
        /// （mfg-domain.md §11 Phase B 落地，「已排程」＝已展開成生產批/工單、由 MFG 動態計算）：
        /// 剩餘 = 訂單量 − Σ(該訂單明細在「已執行」MRP 單的需求量，排除已作廢生產批)。僅回剩餘 &gt; 0 者。
        /// 訂單號 OrderId（SO/NO 前綴本身可區分來源）寫入 `MrpRunLine.SalesOrderId`（沿用為「來源訂單號」）。
        /// 回匿名型別清單（View 以 dynamic 取用，避免新增 DTO 屬性觸發驗證命名規約）。
        /// </summary>
        private async Task<object> LoadImportableOrderLinesAsync()
        {
            var executedRunIds = Db.MrpRuns
                .Where(r => r.Status == MrpRunStatus.Executed || r.Status == MrpRunStatus.Archived)
                .Select(r => r.MrpRunId);

            // 已展開量：依「來源訂單號 + 明細序」彙總（SO/NO 單號全域唯一，不需區分型別）
            var committed = await Db.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId != null && executedRunIds.Contains(l.MrpRunId))
                .Where(l => l.ProductionBatchId == null ||
                            !Db.ProductionBatches.Any(b => b.BatchId == l.ProductionBatchId && b.Status == ProductionBatchStatus.Void))
                .GroupBy(l => new { l.SalesOrderId, l.SalesOrderLineSeq })
                .Select(g => new { g.Key.SalesOrderId, g.Key.SalesOrderLineSeq, Qty = g.Sum(x => x.RequiredQty) })
                .ToListAsync();
            var committedMap = committed.ToDictionary(x => (x.SalesOrderId!, x.SalesOrderLineSeq ?? 0), x => x.Qty);

            var soKind = _localizer["OrderKindSales"].Value;
            var ioKind = _localizer["OrderKindInternal"].Value;

            var soLines = (await Db.SalesOrderLines.AsNoTracking()
                .Where(sl => !sl.IsClosed && sl.OrderQty > 0)
                .Join(Db.SalesOrders.Where(so => so.ApprovalStatus == DocumentApprovalStatus.Approved),
                      sl => sl.SalesOrderId, so => so.SalesOrderId,
                      (sl, so) => new { OrderId = sl.SalesOrderId, sl.LineNo, sl.ProductCode, sl.OrderQty, sl.DeliveryDate })
                .ToListAsync())
                .Select(x => new { x.OrderId, x.LineNo, x.ProductCode, x.OrderQty, x.DeliveryDate, Kind = soKind });

            var ioLines = (await Db.InternalOrderLines.AsNoTracking()
                .Where(il => !il.IsClosed && il.OrderQty > 0)
                .Join(Db.InternalOrders.Where(io => io.ApprovalStatus == DocumentApprovalStatus.Approved),
                      il => il.InternalOrderId, io => io.InternalOrderId,
                      (il, io) => new { OrderId = il.InternalOrderId, il.LineNo, il.ProductCode, il.OrderQty, il.DeliveryDate })
                .ToListAsync())
                .Select(x => new { x.OrderId, x.LineNo, x.ProductCode, x.OrderQty, x.DeliveryDate, Kind = ioKind });

            return soLines.Concat(ioLines)
                .Select(x =>
                {
                    committedMap.TryGetValue((x.OrderId, x.LineNo), out var c);
                    var remaining = x.OrderQty - c;
                    return new
                    {
                        OrderId = x.OrderId,
                        x.LineNo,
                        x.ProductCode,
                        x.OrderQty,
                        Committed = c,
                        Remaining = remaining,
                        DeliveryDate = x.DeliveryDate?.ToString("yyyy-MM-dd") ?? "",
                        x.Kind
                    };
                })
                .Where(x => x.Remaining > 0)
                .OrderBy(x => x.DeliveryDate)
                .ToList();
        }

        /// <summary>先刪後加重建需求列（草稿階段全量覆寫；先反序列化再刪，壞 JSON 不動既有列）。</summary>
        private async Task ReplaceLines(string runId, string linesJson)
        {
            if (string.IsNullOrEmpty(linesJson)) return;
            List<LineDto>? dtos;
            try
            {
                dtos = JsonSerializer.Deserialize<List<LineDto>>(
                    linesJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return; }
            if (dtos == null) return;

            var existing = await Db.MrpRunLines.Where(l => l.MrpRunId == runId).ToListAsync();
            Db.MrpRunLines.RemoveRange(existing);
            await Db.SaveChangesAsync();

            if (dtos.Count == 0) return;

            var seq = 0;
            var newLines = dtos
                .Where(d => !string.IsNullOrWhiteSpace(d.ProductCode))
                .Select(d => new MrpRunLine
                {
                    MrpRunId = runId,
                    LineSeq = ++seq,
                    ProductCode = d.ProductCode,
                    RequiredQty = d.RequiredQty,
                    // 交期為日期，匯入時以 "yyyy-MM-dd" 反序列化 → Kind=Unspecified；
                    // 標為 UTC 以相容 PostgreSQL timestamptz（house style：DateTime 一律存 UTC）。
                    RequiredDate = DateTime.SpecifyKind(d.RequiredDate, DateTimeKind.Utc),
                    SalesOrderId = string.IsNullOrWhiteSpace(d.SalesOrderId) ? null : d.SalesOrderId,
                    SalesOrderLineSeq = d.SalesOrderLineSeq,
                    Priority = d.Priority < 1 ? 100 : d.Priority
                })
                .ToList();

            Db.MrpRunLines.AddRange(newLines);
            await Db.SaveChangesAsync();
        }

        private class LineDto
        {
            public string ProductCode { get; set; } = string.Empty;
            public decimal RequiredQty { get; set; }
            public DateTime RequiredDate { get; set; }
            public string? SalesOrderId { get; set; }
            public int? SalesOrderLineSeq { get; set; }
            public int Priority { get; set; } = 100;
        }
    }
}
