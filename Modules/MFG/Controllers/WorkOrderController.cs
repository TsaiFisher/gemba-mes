using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 工單查詢 / 明細 / 釋出（mfg-domain.md §4.2 / §16 / §18）。工單由 MRP 展開產生，
    /// 此處不提供手動新增；釋出（Planned→Released）時計算並快照標準成本（Standard*），
    /// 並依「WorkOrder」簽核設定送審或直接核准（§18.2）；PendingApproval 可撤回（Withdraw）退回 Planned。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class WorkOrderController : ModuleControllerBase
    {
        private const string MenuKey = "WorkOrder";
        private const string DocType = "WorkOrder";   // 簽核 DocumentType（mfg-domain.md §18.2）
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IApprovalService _approvalService;
        private readonly IInventoryReservationService _reservations;
        private readonly IWorkOrderMaterialReadinessService _readiness;
        private readonly IWorkOrderReleaseService _release;
        private readonly IUomService _uomService;
        private readonly IReworkWorkOrderService _rework;
        private readonly ICurrencyService _currencyService;

        public WorkOrderController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IApprovalService approvalService,
            IInventoryReservationService reservations,
            IWorkOrderMaterialReadinessService readiness,
            IWorkOrderReleaseService release,
            IUomService uomService,
            ICurrencyService currencyService,
            IReworkWorkOrderService rework)
        {
            _rework          = rework;
            _localizer       = localizer;
            _approvalService = approvalService;
            _reservations    = reservations;
            _readiness       = readiness;
            _release         = release;
            _uomService      = uomService;
            _currencyService = currencyService;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "work_order_id_desc",
            string searchText = "", string statusFilter = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = statusFilter;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<WorkOrder>?)null);

            IQueryable<WorkOrder> query = Db.WorkOrders.AsNoTracking();

            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(w =>
                    w.WorkOrderId.Contains(searchText) ||
                    w.ProductCode.Contains(searchText) ||
                    w.ProductionBatchId.Contains(searchText));

            if (!string.IsNullOrEmpty(statusFilter) && Enum.TryParse<WorkOrderStatus>(statusFilter, out var st))
                query = query.Where(w => w.Status == st);

            query = sortOrder switch
            {
                "work_order_id"      => query.OrderBy(w => w.WorkOrderId),
                "product_code"       => query.OrderBy(w => w.ProductCode),
                "product_code_desc"  => query.OrderByDescending(w => w.ProductCode),
                "planned_end"        => query.OrderBy(w => w.PlannedEndDate),
                "planned_end_desc"   => query.OrderByDescending(w => w.PlannedEndDate),
                _                    => query.OrderByDescending(w => w.WorkOrderId)
            };

            var list = await PaginatedList<WorkOrder>.CreateAsync(query, pageNumber ?? 1, pageSize);
            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var wo = await Db.WorkOrders
                .Include(w => w.Operations.OrderBy(o => o.OperationSeq))
                    .ThenInclude(o => o.Materials)
                .FirstOrDefaultAsync(w => w.WorkOrderId == id);

            if (wo == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            await SyncApprovalStatusAsync(wo);   // 開啟明細時拉回簽核終局狀態（與 sync contributor 互補）

            ViewBag.ChildWorkOrders = await Db.WorkOrders.AsNoTracking()
                .Where(w => w.ParentWorkOrderId == id)
                .Select(w => new
                {
                    w.WorkOrderId, w.ProductCode, w.Quantity, w.Status,
                    UomCode = Db.Products.Where(p => p.ProductCode == w.ProductCode).Select(p => p.StockUom).FirstOrDefault()
                })
                .ToListAsync();

            ViewBag.ProductUom = await Db.Products.AsNoTracking()
                .Where(p => p.ProductCode == wo.ProductCode)
                .Select(p => p.StockUom).FirstOrDefaultAsync();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            ViewBag.IsReadOnly = true;
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Details", wo) : View("_Details", wo);
        }

        // GET: /WorkOrder/Print/{id} — 工單列印（工單抬頭 + 工序途程 + 各工序需領料）。
        // 2026-10-28 拿掉標準／實際成本：這張紙是發到現場的，現場要的是「要領什麼、還差多少」，
        // 成本在紙上既用不到又是敏感資訊（成本仍可在系統畫面查）。
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();

            var wo = await Db.WorkOrders
                .AsNoTracking()
                .Include(w => w.Operations.OrderBy(o => o.OperationSeq))
                    .ThenInclude(o => o.Materials)
                .FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null) return NotFound();

            ViewBag.ProductUom = await Db.Products.AsNoTracking()
                .Where(p => p.ProductCode == wo.ProductCode)
                .Select(p => p.StockUom).FirstOrDefaultAsync();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            // WorkOrder 無 CreatedBy（僅 CreatedByRoleId）→ 不傳使用者名解析，QR 指向明細頁。
            await PreparePrintViewBagAsync(
                Url.Action("Details", null, new { id }, Request.Scheme)!);
            await PreparePrintSignAsync(id, null);
            return View(wo);
        }

        /// <summary>
        /// 投產（Release）：Planned→Released（mfg-domain.md §24）。委派 IWorkOrderReleaseService 執行
        /// 「重算量（net-at-release）→ 硬料閘（含在途鎖）→ 鎖庫出 → 標準成本快照 → 送審/核准」。
        /// 重算量 ≠ 暫定量且未確認時回傳 needsConfirm，前端顯示「暫定 → 重算」由人工確認後再帶 confirmed=true。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Release(string id, bool confirmed = false)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var result = await _release.ApplyAsync(id, confirmed);

            switch (result.Outcome)
            {
                case WorkOrderReleaseOutcome.NotFound:
                    return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                case WorkOrderReleaseOutcome.NotPlanned:
                    return Json(new { success = false, message = _localizer["WorkOrderNotPlanned"].Value });
                case WorkOrderReleaseOutcome.NotNeeded:
                    return Json(new { success = false, notNeeded = true,
                        message = string.Format(_localizer["ReleaseNotNeeded"].Value, result.PlannedQty) });
                case WorkOrderReleaseOutcome.NeedsConfirm:
                    return Json(new
                    {
                        success = false,
                        needsConfirm = true,
                        planned = result.PlannedQty,
                        recalculated = result.RecalculatedQty,
                        message = _localizer["ReleaseRecalcConfirm"].Value
                    });
                case WorkOrderReleaseOutcome.MaterialBlocked:
                    return Json(new
                    {
                        success = false,
                        materialBlocked = true,
                        shortages = result.ShortageProducts,
                        message = _localizer["ReleaseMaterialBlocked"].Value
                    });
                case WorkOrderReleaseOutcome.Submitted:
                    return Json(new { success = true, message = _localizer["ReleaseSubmitted"].Value, materialRisk = result.MaterialRisk });
                case WorkOrderReleaseOutcome.ReleasedNoApprovalFlow:
                    // 已投產，但本該送審卻沒有流程可送（A3）：成功訊息之外附警示
                    return Json(new { success = true, warning = true, materialRisk = result.MaterialRisk,
                        message = PzText.Get("Mfg_ReleaseNoApprovalFlowFmt", result.WorkOrderId) });
                default: // Released
                    return Json(new { success = true, message = _localizer["ReleaseSuccess"].Value, materialRisk = result.MaterialRisk });
            }
        }

        /// <summary>
        /// §21.5：物料備齊確認閘口。逐料確認可用量／下階半成品完工，寫回 MaterialReadinessStatus，
        /// 回傳 per-material 明細供 WO 明細頁顯示。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckReadiness(string id)
        {
            var result = await _readiness.CheckAsync(id);
            return Json(new
            {
                success = true,
                overall = result.Overall.ToString(),
                lines = result.Lines.Select(l => new
                {
                    productCode = l.ProductCode,
                    requiredQty = l.RequiredQty,
                    availableQty = l.AvailableQty,
                    state = l.State.ToString()
                })
            });
        }

        /// <summary>重工工單建立表單（GET）— 預填原工單資訊（mfg-domain.md §14.2）。</summary>
        public async Task<IActionResult> ReworkForm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var wo = await Db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null) return NotFound();

            ViewBag.OriginalWo = wo;
            // 勾選要重做的工序（§14.2 只需建立需要重做的製程），預設全勾
            ViewBag.Operations = await Db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == id)
                .OrderBy(o => o.OperationSeq)
                .Select(o => new ReworkOperationOption(o.OperationSeq, o.OperationName, o.WorkCenterId, o.Materials.Count))
                .ToListAsync();
            return PartialView("_ReworkForm", wo);
        }

        /// <summary>
        /// 手工建立重工工單（POST）— mfg-domain.md §14.2。
        /// IsRework=true、ReworkFromWorkOrderId=原工單；沿用原批號、品號、途程。
        /// 使用者指定重工數量，其餘由 Release 後排程處理。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateRework(string id, decimal quantity, string? remarks, int[]? operationSeqs,
            bool includeDefective = false)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

            var orig = await Db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (orig == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            if (quantity <= 0)
                return Json(new { success = false, message = _localizer["ReworkQtyRequired"].Value });

            // 原工單有工序時至少要勾一道，且只能勾原工單上的工序——沒有工序的重工工單投產後排不出派工。
            var origSeqs = await Db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == id).Select(o => o.OperationSeq).ToListAsync();
            var selected = (operationSeqs ?? Array.Empty<int>()).Distinct().ToList();
            if (origSeqs.Count > 0 && selected.Count == 0)
                return Json(new { success = false, message = _localizer["ReworkOperationsRequired"].Value });
            if (selected.Except(origSeqs).Any())
                return Json(new { success = false, message = _localizer["ReworkOperationsInvalid"].Value });

            var now  = DateTime.UtcNow;
            // 重工工單 ID：RW + 原工單後 10 碼 + 毫秒戳，確保唯一
            var rwId = $"RW{id[Math.Max(0, id.Length - 8)..].ToUpper()}{now:ffffss}";

            // 複製工序與用料（用料依重工數量等比換算）由服務負責，NCR 處置「重工」走同一份。
            await _rework.AddAsync(orig, rwId, quantity, orig.ProductCode, now.AddDays(7),   // 暫估 7 天，釋出後排程決定
                origSeqs.Count > 0 ? selected : null, includeDefective);
            await Db.SaveChangesAsync();

            return Json(new { success = true, message = _localizer["ReworkCreated"].Value, workOrderId = rwId });
        }

        /// <summary>撤回釋出簽核：PendingApproval → 退回 Planned（可修正後重新釋出）。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var wo = await Db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (WorkOrderStateMachine.CanWithdraw(wo.ApprovalStatus) != WorkOrderGuard.Allowed)
                return Json(new { success = false, message = _localizer["WorkOrderNotPending"].Value });

            if (!string.IsNullOrEmpty(wo.ApprovalRequestId))
                await _approvalService.WithdrawRequest(wo.ApprovalRequestId, UserContext.UserId ?? string.Empty);

            wo.ApprovalStatus = DocumentApprovalStatus.Draft;
            wo.ApprovalRequestId = null;
            wo.Status = WorkOrderStatus.Planned;   // 退回已規劃，可修正後重新釋出
            wo.UpdatedAt = DateTime.UtcNow;
            await WorkOrderApprovalGate.UnscheduleAsync(Db, id, wo.UpdatedAt);
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["WorkOrderWithdrawn"].Value });
        }

        /// <summary>
        /// 工單作廢：任何尚未開始生產的工單（Planned/Released/PendingApproval）可作廢。
        /// 已有派工回報（QtyDone &gt; 0）者不允許作廢（mfg-domain.md §4.2）。
        /// 作廢後釋放所有 InventoryReservation（同 CompleteAsync 邏輯）。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var wo = await Db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == id);
            if (wo == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            var hasProduction = await Db.Dispatches.AsNoTracking()
                .AnyAsync(d => d.WorkOrderId == id && d.QtyDone > 0);

            // 已領未退的料同樣擋住作廢。§4.2 原本只寫「檢查派工有無回報」，漏掉了這一段：
            // 領料單過帳的當下料就已經實際離開倉庫、庫存扣掉、IssuedQty 回寫，
            // 但那全都發生在任何人回報產出之前——所以「還沒開始生產」與「料還在倉裡」
            // 不是同一件事，只看 QtyDone 會讓一張料已經領光的工單照樣作廢掉。
            //
            // 作廢之後沒有任何東西會來承接那批料：工單不會結案、成本結算不會跑，
            // 料就停在「已領出、無工單消耗」的狀態。生產領料不入總帳（形態轉換），
            // 所以試算表照樣平衡——帳面完全安靜，只有倉庫少了東西而查無去向。
            // 正確的順序是先開退料單把料退回，IssuedQty − ReturnedQty 歸零後才能作廢。
            var hasIssuedMaterial = await Db.WorkOrderMaterials.AsNoTracking()
                .AnyAsync(m => m.WorkOrderId == id && m.IssuedQty - m.ReturnedQty > 0);

            var voidGuard = WorkOrderStateMachine.CanVoid(wo.Status, hasProduction, hasIssuedMaterial);
            if (voidGuard != WorkOrderGuard.Allowed)
                return Json(new { success = false, message = voidGuard switch
                {
                    WorkOrderGuard.CannotVoid    => _localizer["WorkOrderCannotVoid"].Value,
                    WorkOrderGuard.HasProduction => _localizer["WorkOrderHasProduction"].Value,
                    _                            => _localizer["WorkOrderHasIssuedMaterial"].Value,
                } });

            // 還沒過帳的領料單同樣是「料會離開倉庫」的承諾：上面只看已過帳未退，2026-10-20 實測
            // 先作廢、再過帳殘留的 Confirmed 領料單，料就發到作廢工單上。可取消的（草稿～揀料中，
            // 尚無庫存異動）隨作廢一併取消；倉庫已確認揀料的不能取消，只能擋。
            var openSlips = await Db.MaterialSlips
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == id
                         && s.Status != MaterialSlipStatus.Posted && s.Status != MaterialSlipStatus.Closed
                         && s.Status != MaterialSlipStatus.Cancelled)
                .ToListAsync();
            var confirmedSlips = openSlips.Where(s => s.Status == MaterialSlipStatus.Confirmed)
                .Select(s => s.SlipId).ToList();
            if (confirmedSlips.Count > 0)
                return Json(new { success = false, message = string.Format(
                    _localizer["WorkOrderHasConfirmedSlip"].Value, string.Join(", ", confirmedSlips)) });

            // 委外採購單：作廢不會去動它，收貨確認也照樣能過帳到作廢工單。草稿可先刪除（會回沖委外建議），
            // 已確認者已對廠商承諾、沒有作廢出口，工單也就不能作廢。
            var openOpos = await Db.OutsourcePurchaseOrders.AsNoTracking()
                .Where(o => o.Status != OutsourcePoStatus.Void && o.Lines.Any(l => l.SourceWorkOrderId == id))
                .Select(o => o.OutsourcePurchaseOrderId).ToListAsync();
            if (openOpos.Count > 0)
                return Json(new { success = false, message = string.Format(
                    _localizer["WorkOrderHasOutsourcePo"].Value, string.Join(", ", openOpos)) });

            // 撤簽 + 作廢 + 釋放預留須以單一交易包住，避免中途失敗造成
            // 「預留已釋放（自身 SaveChanges）卻工單未轉 Void」的不一致；
            // 並發作廢由 WorkOrder.xmin 守門：後到者收尾 SaveChanges 比對失敗 → 整段回滾。
            await using var tx = await Db.Database.BeginTransactionAsync();
            try
            {
                if (!string.IsNullOrEmpty(wo.ApprovalRequestId))
                    await _approvalService.WithdrawRequest(wo.ApprovalRequestId, UserContext.UserId ?? string.Empty);

                wo.Status         = WorkOrderStatus.Void;
                wo.ApprovalStatus = DocumentApprovalStatus.Draft;
                wo.UpdatedAt      = DateTime.UtcNow;

                foreach (var slip in openSlips)
                {
                    slip.Status    = MaterialSlipStatus.Cancelled;
                    slip.UpdatedAt = DateTime.UtcNow;
                }

                await _reservations.ReleaseBySourceAsync(
                    ReservationSourceType.WorkOrderMaterial, id);

                // 工單作廢後，該工單備料單接手的預留同樣沒有存在意義，一併釋放；
                // 備料單本身狀態由使用者決定（可能要退料回原倉），此處只解除庫存鎖定。
                await _reservations.ReleaseWorkOrderStagingAsync(id);

                // 未結束的派工一併取消：否則它們仍留在派工清單上當現場待辦，
                // 準備中／準備完成者還會被排程當成凍結佔用、一直佔著機台。
                await HttpContext.RequestServices
                    .GetRequiredService<IDispatchLifecycleService>()
                    .CancelOpenDispatchesAsync(id, DateTime.UtcNow);

                await Db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // 並發作廢：另一請求已先作廢本工單，本次交易已回滾。
                return Json(new { success = false, message = _localizer["ConcurrencyConflict"].Value });
            }

            return Json(new { success = true, message = _localizer["WorkOrderVoided"].Value });
        }

    }

    /// <summary>「建立重工」面板上可勾選的原工單工序。</summary>
    internal sealed record ReworkOperationOption(int OperationSeq, string? OperationName, string WorkCenterId, int MaterialCount);
}
