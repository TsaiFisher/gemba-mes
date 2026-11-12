using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機料件作業：工單發退料（倉庫揀料過帳）、備料單（鎖庫存／移入備料倉）、現場領料／退料申請、委外收料。
    ///
    /// <para><b>帳務一律走共用服務</b>（<see cref="IMaterialSlipService"/>、<see cref="IOutsourceReceiptService"/>、
    /// <see cref="IMaterialStagingService"/>），這裡只負責權限、去重與回應格式——不另寫一份過帳邏輯。</para>
    ///
    /// <para><b>可離線排隊的</b>：揀料過帳、移入備料倉、領料／退料申請（皆以 clientId 去重，伺服器送達當下才驗）。
    /// <b>只能連線做的</b>：備料確認（鎖庫存）、委外收料（同時產生應付發票）——結果要當場知道，
    /// 排隊幾小時後才失敗的帳不能讓人以為已經做完。</para>
    /// </summary>
    [Route("api/mobile/material")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobileMaterialController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _user;
        private readonly IMobilePermissionService _mobilePermission;
        private readonly IMaterialSlipService _slips;
        private readonly IMaterialStagingService _staging;
        private readonly IOutsourceReceiptService _receipts;
        private readonly IDateTimeService _dt;

        public MobileMaterialController(ApplicationDbContext db, IUserContext user,
            IMobilePermissionService mobilePermission, IMaterialSlipService slips,
            IMaterialStagingService staging, IOutsourceReceiptService receipts, IDateTimeService dt)
        {
            _db = db; _user = user; _mobilePermission = mobilePermission;
            _slips = slips; _staging = staging; _receipts = receipts; _dt = dt;
        }

        // 作業鍵（Program.cs 種子：倉儲作業 MaterialIssue／Staging／OutsourceReceive；投產報工 Requisition）
        private const string Inv = "Inv", Mfg = "Mfg";

        private async Task<IActionResult?> RequireMobilePermissionAsync(string moduleKey, string actionKey)
        {
            var roleId = _user.ActiveRoleId;
            if (!string.IsNullOrEmpty(roleId) && await _mobilePermission.CanExecuteAsync(roleId, moduleKey, actionKey))
                return null;
            return StatusCode(StatusCodes.Status403Forbidden,
                new MobileErrorResult(Error: new("FORBIDDEN", PzText.Get("Mob_ForbiddenAction"))));
        }

        private static IActionResult Bad(string message) =>
            new BadRequestObjectResult(new MobileErrorResult(Error: new("BUSINESS_RULE", message)));

        // ═════════════════════ 工單發退料（倉庫揀料過帳）════════════════════════

        /// <summary>待處理的發退料單。kind=issue：發料類（發料／領料／委外發料／重工發料）；kind=return：退料類。</summary>
        [HttpGet("slips")]
        public async Task<IActionResult> GetSlips([FromQuery] string? kind = null)
        {
            if (await RequireMobilePermissionAsync(Inv, "MaterialIssue") is { } forbidden) return forbidden;
            bool wantReturn = string.Equals(kind, "return", StringComparison.OrdinalIgnoreCase);

            var list = (await PendingSlipsAsync(wantReturn))
                .Select(s => new
                {
                    s.SlipId, Category = s.Category.ToString(), Status = s.Status.ToString(),
                    s.SlipDate, s.SourceDocId, s.TargetSupplierCode, LineCount = s.Lines.Count,
                });
            return Ok(new MobileOkResult(Data: list));
        }

        /// <summary>
        /// 待處理的發退料單——清單（GetSlips）與首頁待辦數（GetCounts）共用這一份定義：
        /// 兩邊各寫一份條件，數字就會跟點進去看到的對不上。
        /// </summary>
        private async Task<List<MaterialSlip>> PendingSlipsAsync(bool wantReturn)
        {
            var rows = await _db.MaterialSlips.AsNoTracking().Include(s => s.Lines)
                .Where(s => s.PlantId == _user.PlantId
                         && (s.Status == MaterialSlipStatus.Draft || s.Status == MaterialSlipStatus.Approved
                          || s.Status == MaterialSlipStatus.Picking || s.Status == MaterialSlipStatus.Confirmed))
                .OrderBy(s => s.SlipDate).ThenBy(s => s.SlipId)
                .ToListAsync();
            return rows.Where(s => MaterialSlipFlow.IsReturn(s.Category) == wantReturn && MaterialSlipService.CanPickOnMobile(s))
                .ToList();
        }

        /// <summary>單張明細＋每個品號目前有庫存的儲位（發料用）；工單已備料的話把備料儲位排第一。</summary>
        [HttpGet("slips/{id}")]
        public async Task<IActionResult> GetSlip(string id)
        {
            if (await RequireMobilePermissionAsync(Inv, "MaterialIssue") is { } forbidden) return forbidden;
            var s = await _db.MaterialSlips.AsNoTracking().Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.SlipId == id && x.PlantId == _user.PlantId);
            if (s == null) return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_RecordNotFound"))));

            bool isReturn = MaterialSlipFlow.IsReturn(s.Category);
            var products = s.Lines.Select(l => l.ProductCode).Distinct().ToList();
            var stock = isReturn ? new Dictionary<string, List<StockRow>>() : await StockByProductAsync(products);
            var stagedAt = s.SourceDocType == "WorkOrder" ? await StagedLocationsAsync(s.SourceDocId, products) : new();

            return Ok(new MobileOkResult(Data: new
            {
                s.SlipId, Category = s.Category.ToString(), Status = s.Status.ToString(), s.SlipDate,
                s.SourceDocId, s.TargetSupplierCode, s.Remarks, IsReturn = isReturn,
                Lines = s.Lines.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, l.ProductCode, l.UomCode,
                    Qty = l.ApprovedQty ?? l.RequestedQty,
                    WhCode = isReturn ? (l.ToWarehouseCode ?? l.FromWarehouseCode) : l.FromWarehouseCode,
                    LocationCode = isReturn ? (l.ToLocationCode ?? l.FromLocationCode) : l.FromLocationCode,
                    l.LotNo,
                    StagedLocation = stagedAt.GetValueOrDefault(l.ProductCode),
                    Stock = OrderStaged(stock.GetValueOrDefault(l.ProductCode) ?? new(), stagedAt.GetValueOrDefault(l.ProductCode)),
                }),
            }));
        }

        public record SlipPostRequest(List<SlipPickLine> Lines, string? ClientId = null, DateTime? ClientTimestamp = null);

        /// <summary>揀料確認＋過帳（一次完成）。可離線排隊：clientId 重送只回第一次的結果。</summary>
        [HttpPost("slips/{id}/post")]
        public async Task<IActionResult> PostSlip(string id, [FromBody] SlipPostRequest req)
        {
            if (await RequireMobilePermissionAsync(Inv, "MaterialIssue") is { } forbidden) return forbidden;
            if (await ReplayAsync(req.ClientId) is { } replay) return Ok(replay);

            var ok = new MobileOkResult(Data: new { SlipId = id }, Message: PzText.Get("Mob_SlipPosted"));
            return await RunWithSyncLogAsync(req.ClientId, req.ClientTimestamp, "material/slips/post", ok,
                () => _slips.PickAndPostAsync(id, req.Lines ?? new()));
        }

        // ═════════════════════ 現場領料／退料申請（上工頁）═══════════════════════

        /// <summary>
        /// 這張派工所屬工單的廠內用料：需求、已發、已退、淨領用、發料方式，以及尚可申請領料／退料的量
        /// （與建單守門同一份計算）。委外派工帶 <c>IsOutsource</c>，畫面據此停用。
        /// </summary>
        [HttpGet("dispatches/{dispatchId}/materials")]
        public async Task<IActionResult> GetDispatchMaterials(string dispatchId)
        {
            if (await RequireMobilePermissionAsync(Mfg, "Requisition") is { } forbidden) return forbidden;
            var d = await _db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(x => x.DispatchId == dispatchId && x.PlantId == _user.PlantId);
            if (d == null) return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            // 同品號跨工序合併顯示：申請與退料都以品號為單位，對應哪一筆用料由伺服器導出
            var rows = d.IsOutsource ? Array.Empty<WorkOrderSlipQuota>() : await _slips.GetWorkOrderSlipQuotaAsync(d.WorkOrderId);
            return Ok(new MobileOkResult(Data: new { d.WorkOrderId, d.IsOutsource, Materials = rows }));
        }

        public record SlipRequestBody(List<SlipRequestLine> Lines, string? Remarks = null,
            string? ClientId = null, DateTime? ClientTimestamp = null);

        /// <summary>領料申請（MR，送出成待核准）。可離線排隊。</summary>
        [HttpPost("dispatches/{dispatchId}/requisition")]
        public async Task<IActionResult> Requisition(string dispatchId, [FromBody] SlipRequestBody req)
        {
            if (await RequireMobilePermissionAsync(Mfg, "Requisition") is { } forbidden) return forbidden;
            return await CreateWorkOrderSlipAsync(dispatchId, MaterialSlipCategory.InternalRequisition, req, "Mob_RequisitionSubmitted");
        }

        /// <summary>退料（MX，草稿等倉庫收料）。可離線排隊。</summary>
        [HttpPost("dispatches/{dispatchId}/return")]
        public async Task<IActionResult> MaterialReturn(string dispatchId, [FromBody] SlipRequestBody req)
        {
            if (await RequireMobilePermissionAsync(Mfg, "Requisition") is { } forbidden) return forbidden;
            return await CreateWorkOrderSlipAsync(dispatchId, MaterialSlipCategory.InternalReturn, req, "Mob_ReturnSlipCreated");
        }

        private async Task<IActionResult> CreateWorkOrderSlipAsync(string dispatchId, MaterialSlipCategory cat,
            SlipRequestBody req, string okKey)
        {
            // 權限已由兩個公開動作各自檢查（慣例守門 R13／R24 只認動作本體）
            if (await ReplayAsync(req.ClientId) is { } replay) return Ok(replay);

            var d = await _db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(x => x.DispatchId == dispatchId && x.PlantId == _user.PlantId);
            if (d == null) return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));
            // 委外工序的料在廠商那裡：要走委外發料／委外退料。2026-10-10 實測曾用廠內退料把它退進倉庫＝憑空多出庫存
            if (d.IsOutsource)
                return BadRequest(new MobileErrorResult(Error: new("OUTSOURCE_DISPATCH", PzText.Get("Mob_OutsourceDispatchNoSlip"))));

            string? slipId = null;
            var result = await RunWithSyncLogAsync(req.ClientId, req.ClientTimestamp,
                cat == MaterialSlipCategory.InternalReturn ? "material/return" : "material/requisition",
                () => new MobileOkResult(Data: new { SlipId = slipId }, Message: PzText.Get(okKey, slipId ?? "")),
                async () => slipId = await _slips.CreateForWorkOrderAsync(cat, d.WorkOrderId, req.Lines ?? new(), req.Remarks));
            return result;
        }

        // ═════════════════════ 備料單（鎖庫存／移入備料倉）══════════════════════

        /// <summary>待確認（已核准的草稿）與待移入（已確認）的備料單。</summary>
        [HttpGet("staging")]
        public async Task<IActionResult> GetStagingOrders()
        {
            if (await RequireMobilePermissionAsync(Inv, "Staging") is { } forbidden) return forbidden;
            var list = await PendingStagingQuery()
                .OrderBy(o => o.CreatedAt)
                .Select(o => new { o.StagingId, o.Status, o.SourceType, o.SourceId, o.StagingWhCode, o.StagingLocationCode,
                                   LineCount = o.Lines.Count })
                .ToListAsync();
            return Ok(new MobileOkResult(Data: list));
        }

        /// <summary>待確認（已核准的草稿）與待移入（已確認）的備料單；清單與待辦數共用。</summary>
        private IQueryable<MaterialStagingOrder> PendingStagingQuery() =>
            _db.MaterialStagingOrders.AsNoTracking()
                .Where(o => o.PlantId == _user.PlantId
                         && ((o.Status == "Draft" && o.ApprovalStatus == DocumentApprovalStatus.Approved)
                          || o.Status == "Confirmed"));

        [HttpGet("staging/{id}")]
        public async Task<IActionResult> GetStagingOrder(string id)
        {
            if (await RequireMobilePermissionAsync(Inv, "Staging") is { } forbidden) return forbidden;
            var o = await _db.MaterialStagingOrders.AsNoTracking().Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.StagingId == id && x.PlantId == _user.PlantId);
            if (o == null) return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_RecordNotFound"))));

            var stock = await StockByProductAsync(o.Lines.Select(l => l.ProductCode).Distinct().ToList());
            return Ok(new MobileOkResult(Data: new
            {
                o.StagingId, o.Status, o.SourceType, o.SourceId, o.StagingWhCode, o.StagingLocationCode,
                Lines = o.Lines.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, l.ProductCode, l.UomCode, l.RequiredQty, l.PickedQty,
                    WhCode = l.FromWarehouseCode, LocationCode = l.FromLocationCode,
                    // 備料倉本身的庫存不列——從備料倉「移入」備料倉沒有意義
                    Stock = (stock.GetValueOrDefault(l.ProductCode) ?? new())
                        .Where(r => r.LocationCode != o.StagingLocationCode).ToList(),
                }),
            }));
        }

        /// <summary>確認（鎖庫存）。只能連線做：可用量不足要當場知道。</summary>
        [HttpPost("staging/{id}/confirm")]
        public async Task<IActionResult> ConfirmStaging(string id)
        {
            if (await RequireMobilePermissionAsync(Inv, "Staging") is { } forbidden) return forbidden;
            var o = await _db.MaterialStagingOrders.Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.StagingId == id && x.PlantId == _user.PlantId);
            if (o == null) return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_RecordNotFound"))));
            if (o.Status != "Draft") return Bad(PzText.Get("Mob_StagingNotDraft"));
            if (o.ApprovalStatus != DocumentApprovalStatus.Approved) return Bad(PzText.Get("Mob_StagingNeedApproval"));
            try
            {
                await _staging.ConfirmAsync(o, _user.UserId ?? "");
            }
            catch (InvalidOperationException ex) { return Bad(ex.Message); }
            return Ok(new MobileOkResult(Message: PzText.Get("Mob_StagingConfirmed")));
        }

        public record StageLine(int LineNo, string? WhCode, string? LocationCode);
        public record StageRequest(List<StageLine> Lines, string? ClientId = null, DateTime? ClientTimestamp = null);

        /// <summary>揀料移入備料倉（逐行回報來源倉／儲位）。可離線排隊。</summary>
        [HttpPost("staging/{id}/stage")]
        public async Task<IActionResult> Stage(string id, [FromBody] StageRequest req)
        {
            if (await RequireMobilePermissionAsync(Inv, "Staging") is { } forbidden) return forbidden;
            if (await ReplayAsync(req.ClientId) is { } replay) return Ok(replay);

            var o = await _db.MaterialStagingOrders.Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.StagingId == id && x.PlantId == _user.PlantId);
            if (o == null) return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_RecordNotFound"))));

            foreach (var l in req.Lines ?? new())
            {
                var line = o.Lines.FirstOrDefault(x => x.LineNo == l.LineNo);
                if (line == null) continue;
                line.FromWarehouseCode = string.IsNullOrWhiteSpace(l.WhCode) ? null : l.WhCode;
                line.FromLocationCode  = string.IsNullOrWhiteSpace(l.LocationCode) ? null : l.LocationCode;
            }
            var ok = new MobileOkResult(Message: PzText.Get("Mob_StagingStaged"));
            return await RunWithSyncLogAsync(req.ClientId, req.ClientTimestamp, "material/staging/stage", ok,
                () => _staging.StageAsync(o, _user.UserId ?? ""));
        }

        // ═════════════════════ 委外收料 ════════════════════════════════════════

        /// <summary>可收料的委外採購單（已確認、還有未收量）。q：單號或廠商代碼關鍵字。</summary>
        [HttpGet("outsource-orders")]
        public async Task<IActionResult> GetOutsourceOrders([FromQuery] string? q = null)
        {
            if (await RequireMobilePermissionAsync(Inv, "OutsourceReceive") is { } forbidden) return forbidden;
            var opos = await ReceivableOposAsync(q);
            var names = await _db.Companies.AsNoTracking()
                .Where(c => opos.Select(o => o.Opo.SupplierCode).Contains(c.CompanyCode))
                .ToDictionaryAsync(c => c.CompanyCode, c => c.CompanyName);

            var list = opos.Select(x => (object)new
            {
                x.Opo.OutsourcePurchaseOrderId, x.Opo.SupplierCode, SupplierName = names.GetValueOrDefault(x.Opo.SupplierCode),
                x.Opo.ExpectedDeliveryDate, RemainingQty = x.Remaining, LineCount = x.OpenLines,
            }).ToList();
            return Ok(new MobileOkResult(Data: list));
        }

        /// <summary>還有未收量的已確認委外採購單（最多 100 張）；清單與待辦數共用。</summary>
        private async Task<List<(OutsourcePurchaseOrder Opo, decimal Remaining, int OpenLines)>> ReceivableOposAsync(string? q)
        {
            var query = _db.OutsourcePurchaseOrders.AsNoTracking()
                .Where(o => o.PlantId == _user.PlantId && o.Status == OutsourcePoStatus.Confirmed);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var kw = q.Trim();
                query = query.Where(o => o.OutsourcePurchaseOrderId.Contains(kw) || o.SupplierCode.Contains(kw));
            }
            var opos = await query.OrderBy(o => o.ExpectedDeliveryDate).ThenBy(o => o.OutsourcePurchaseOrderId)
                .Take(100).ToListAsync();

            var result = new List<(OutsourcePurchaseOrder, decimal, int)>();
            foreach (var o in opos)
            {
                var lines = await _receipts.GetReceivableLinesAsync(o.OutsourcePurchaseOrderId);
                var remaining = lines.Sum(l => l.RemainingQty);
                if (remaining <= 0) continue;
                result.Add((o, remaining, lines.Count(l => l.RemainingQty > 0)));
            }
            return result;
        }

        [HttpGet("outsource-orders/{id}")]
        public async Task<IActionResult> GetOutsourceOrder(string id)
        {
            if (await RequireMobilePermissionAsync(Inv, "OutsourceReceive") is { } forbidden) return forbidden;
            var o = await _db.OutsourcePurchaseOrders.AsNoTracking()
                .FirstOrDefaultAsync(x => x.OutsourcePurchaseOrderId == id && x.PlantId == _user.PlantId);
            if (o == null) return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_RecordNotFound"))));
            var supplierName = await _db.Companies.AsNoTracking()
                .Where(c => c.CompanyCode == o.SupplierCode).Select(c => c.CompanyName).FirstOrDefaultAsync();
            var isOperation = await _receipts.IsOperationOutsourceAsync(id);
            return Ok(new MobileOkResult(Data: new
            {
                o.OutsourcePurchaseOrderId, o.SupplierCode, SupplierName = supplierName, o.ExpectedDeliveryDate,
                Status = o.Status.ToString(),
                // 簡化模式下收料即入庫：每行都要指定倉庫／儲位；一般模式由後續「入庫上架」決定。
                // 工序委外收回不入庫（§19.7 C），不需要儲位
                IsOperation = isOperation,
                RequiresLocation = !isOperation && await _receipts.RequiresLocationAtConfirmAsync(),
                // 委外收料當場驗收（廠別參數）：每行填不良數，合格直接入一般倉、不合格入待驗倉
                InspectAtReceipt = await _receipts.InspectAtReceiptEnabledAsync(),
                Lines = await _receipts.GetReceivableLinesAsync(id),
            }));
        }

        public record OutsourceReceiveRequest(string OpoId, List<MobileReceiveLine> Lines, bool IsFinalReceipt = false,
            string? Remarks = null, string? ClientId = null, DateTime? ClientTimestamp = null);

        /// <summary>
        /// 委外收料：建收貨單→送審→已核准就確認。只能連線做（同時產生應付發票）；
        /// clientId 只防連按兩次，不代表可以離線排隊。
        /// </summary>
        [HttpPost("outsource-receipts")]
        public async Task<IActionResult> ReceiveOutsource([FromBody] OutsourceReceiveRequest req)
        {
            if (await RequireMobilePermissionAsync(Inv, "OutsourceReceive") is { } forbidden) return forbidden;
            if (await ReplayAsync(req.ClientId) is { } replay) return Ok(replay);

            string? receiptId = null; ReceiptOutcome outcome = default;
            return await RunWithSyncLogAsync(req.ClientId, req.ClientTimestamp, "material/outsource-receipts",
                () => new MobileOkResult(
                    Data: new { ReceiptId = receiptId, Outcome = outcome.ToString() },
                    Message: PzText.Get(outcome switch
                    {
                        ReceiptOutcome.Posted                   => "Mob_OutsourceReceivedPosted",
                        ReceiptOutcome.ConfirmedAwaitingPutAway => "Mob_OutsourceReceivedAwaitPutAway",
                        _                                       => "Mob_OutsourceReceivedPendingApproval",
                    }, receiptId ?? "")),
                async () => (receiptId, outcome) = await _receipts.ReceiveFromMobileAsync(
                    req.OpoId, _dt.GetLocalDate(), req.IsFinalReceipt, req.Lines ?? new(), req.Remarks));
        }

        /// <summary>簽核後才核准的收貨單：等著確認（確認後才產生應付發票、能上架）。</summary>
        [HttpGet("outsource-receipts/approved")]
        public async Task<IActionResult> GetApprovedReceipts()
        {
            if (await RequireMobilePermissionAsync(Inv, "OutsourceReceive") is { } forbidden) return forbidden;
            var list = await ApprovedReceiptsQuery()
                .OrderBy(r => r.ReceiptDate)
                .Select(r => new { r.OutsourceReceiptId, r.OutsourcePurchaseOrderId, r.SupplierCode, r.ReceiptDate,
                                   LineCount = r.Lines.Count })
                .ToListAsync();
            return Ok(new MobileOkResult(Data: list));
        }

        private IQueryable<SmartFactory.MES.Modules.PUR.Models.OutsourceReceipt> ApprovedReceiptsQuery() =>
            _db.OutsourceReceipts.AsNoTracking()
                .Where(r => r.PlantId == _user.PlantId && r.Status == DocStatus.Draft
                         && r.ApprovalStatus == DocumentApprovalStatus.Approved);

        // ═════════════════════ 倉儲選單的待辦數 ════════════════════════════════

        /// <summary>
        /// 倉儲作業選單三張料件卡的待辦數（2026-10-12）。每一項用的是該頁清單的同一份查詢，數字＝點進去看到的筆數。
        /// 沒有該作業權限的項目回 null（卡片不顯示數字）——不回 403：一張卡沒權限不該讓整個選單跳「無權限」。
        /// 委外收料＝可收料的委外採購單＋待確認的收貨單（兩者都在委外收料頁處理）。
        /// </summary>
        [HttpGet("counts")]
        public async Task<IActionResult> GetCounts()
        {
            // 模組層閘門：連倉儲模組都沒有就不該拿到任何數字
            var roleId = _user.ActiveRoleId;
            if (string.IsNullOrEmpty(roleId)) return (await RequireMobilePermissionAsync(Inv, "MaterialIssue"))!;   // 沒有角色＝一定是 403
            async Task<bool> Can(string action) => await _mobilePermission.CanExecuteAsync(roleId, Inv, action);

            int? issue = null, ret = null, staging = null, outsource = null;
            if (await Can("MaterialIssue"))
            {
                issue = (await PendingSlipsAsync(false)).Count;
                ret   = (await PendingSlipsAsync(true)).Count;
            }
            if (await Can("Staging"))
                staging = await PendingStagingQuery().CountAsync();
            if (await Can("OutsourceReceive"))
                outsource = (await ReceivableOposAsync(null)).Count + await ApprovedReceiptsQuery().CountAsync();

            return Ok(new MobileOkResult(Data: new { SlipIssue = issue, SlipReturn = ret, Staging = staging, Outsource = outsource }));
        }

        [HttpPost("outsource-receipts/{id}/confirm")]
        public async Task<IActionResult> ConfirmReceipt(string id)
        {
            if (await RequireMobilePermissionAsync(Inv, "OutsourceReceive") is { } forbidden) return forbidden;
            if (!await _db.OutsourceReceipts.AnyAsync(r => r.OutsourceReceiptId == id && r.PlantId == _user.PlantId))
                return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_RecordNotFound"))));
            try
            {
                var outcome = await _receipts.ConfirmAsync(id);
                return Ok(new MobileOkResult(Data: new { ReceiptId = id, Outcome = outcome.ToString() },
                    Message: PzText.Get(outcome == ReceiptOutcome.Posted
                        ? "Mob_OutsourceReceivedPosted" : "Mob_OutsourceReceivedAwaitPutAway", id)));
            }
            catch (InvalidOperationException ex) { return Bad(ex.Message); }
            catch (DbUpdateConcurrencyException) { return Conflict(new MobileErrorResult(Error: new("CONCURRENCY_CONFLICT", PzText.Get("Mob_ConcurrencyReload")))); }
        }

        // ═════════════════════ helpers ═════════════════════════════════════════

        public record StockRow(string WhCode, string LocationCode, string? LotNo, decimal QtyOnHand, decimal QtyAvailable);

        private async Task<Dictionary<string, List<StockRow>>> StockByProductAsync(List<string> products)
        {
            var plantId = _user.PlantId ?? "";
            var invs = await _db.Inventories.AsNoTracking()
                .Where(i => i.PlantId == plantId && products.Contains(i.ProductCode) && i.QtyOnHand > 0)
                .Select(i => new { i.ProductCode, i.LocationCode, i.LotNo, i.QtyOnHand, i.QtyReserved })
                .ToListAsync();
            var locCodes = invs.Select(i => i.LocationCode).Distinct().ToList();
            var whOf = await _db.StorageLocations.ForCurrentPlant(_db).AsNoTracking()
                .Where(l => locCodes.Contains(l.LocationCode))
                .ToDictionaryAsync(l => l.LocationCode, l => l.WhCode);
            return invs.GroupBy(i => i.ProductCode).ToDictionary(g => g.Key, g => g
                .Select(i => new StockRow(whOf.GetValueOrDefault(i.LocationCode) ?? "", i.LocationCode, i.LotNo,
                                          i.QtyOnHand, i.QtyOnHand - i.QtyReserved))
                .OrderByDescending(r => r.QtyAvailable).ToList());
        }

        /// <summary>這張工單已移入備料倉的品號 → 備料儲位。發料時把它排第一：料已經搬過去了。</summary>
        private async Task<Dictionary<string, string>> StagedLocationsAsync(string? workOrderId, List<string> products)
        {
            if (string.IsNullOrEmpty(workOrderId)) return new();
            var rows = await _db.MaterialStagingLines.AsNoTracking()
                .Where(l => l.StagingOrder!.Status == "Completed" && products.Contains(l.ProductCode)
                         && (l.SourceWorkOrderId == workOrderId
                             || (l.SourceWorkOrderId == null && l.StagingOrder.SourceType == "WorkOrder" && l.StagingOrder.SourceId == workOrderId)))
                .Select(l => new { l.ProductCode, l.StagingOrder!.StagingLocationCode })
                .ToListAsync();
            return rows.Where(r => r.StagingLocationCode != null)
                .GroupBy(r => r.ProductCode).ToDictionary(g => g.Key, g => g.First().StagingLocationCode!);
        }

        private static List<StockRow> OrderStaged(List<StockRow> rows, string? stagedLoc) =>
            stagedLoc == null ? rows : rows.OrderByDescending(r => r.LocationCode == stagedLoc).ToList();

        private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

        private async Task<MobileOkResult?> ReplayAsync(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return null;   // 線上直接送出不帶 clientId 時不去重
            var log = await _db.SyncLogs.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId);
            if (log == null) return null;
            return log.ResponseSnapshot != null
                ? JsonSerializer.Deserialize<MobileOkResult>(log.ResponseSnapshot, SnapshotJson)
                : new MobileOkResult(Message: PzText.Get("Mob_AlreadyProcessed"));
        }

        private Task<IActionResult> RunWithSyncLogAsync(string? clientId, DateTime? clientTs, string endpoint,
            MobileOkResult ok, Func<Task> action) => RunWithSyncLogAsync(clientId, clientTs, endpoint, () => ok, action);

        /// <summary>
        /// 執行業務動作，並讓 SyncLog 與業務變更落在**同一次** SaveChanges／交易：
        /// SyncLog 先加進追蹤，服務內部的 SaveChanges 會一併寫入；業務失敗就一起不存在，
        /// 重送時不會被誤判成「已處理」。結果快照在動作完成後才補上（含新單號）。
        /// </summary>
        private async Task<IActionResult> RunWithSyncLogAsync(string? clientId, DateTime? clientTs, string endpoint,
            Func<MobileOkResult> okFactory, Func<Task> action)
        {
            SmartFactory.MES.Modules.MFG.Models.SyncLog? log = null;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                log = new SmartFactory.MES.Modules.MFG.Models.SyncLog
                {
                    ClientId = clientId!, Endpoint = endpoint,
                    ClientTimestamp = clientTs?.ToUniversalTime() ?? DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                };
                _db.SyncLogs.Add(log);
            }
            try
            {
                await action();
            }
            catch (InvalidOperationException ex)
            {
                if (log != null) _db.Entry(log).State = EntityState.Detached;
                return Bad(ex.Message);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (log != null) _db.Entry(log).State = EntityState.Detached;
                return Conflict(new MobileErrorResult(Error: new("CONCURRENCY_CONFLICT", PzText.Get("Mob_ConcurrencyReload"))));
            }

            var ok = okFactory();
            if (log != null)
            {
                var snap = JsonSerializer.Serialize(ok, SnapshotJson);
                if (snap.Length <= 2000) { log.ResponseSnapshot = snap; await _db.SaveChangesAsync(); }
            }
            return Ok(ok);
        }
    }
}
