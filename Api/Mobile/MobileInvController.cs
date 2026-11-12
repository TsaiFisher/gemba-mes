using SmartFactory.MES.Modules._Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機倉儲 IQC 進料檢驗 API（MIL-STD-1916 / mobile-pwa.md §7.2 aiq / Phase G-H）。
    /// 認證：Cookie（與桌機共用）。
    /// </summary>
    [Route("api/mobile/inv")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobileInvController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;
        private readonly IIqcInspectionService _iqc;
        private readonly IMobilePermissionService _mobilePermission;
        private readonly IDocNumberService _docNumber;

        public MobileInvController(
            ApplicationDbContext db,
            IUserContext userContext,
            IIqcInspectionService iqc,
            IMobilePermissionService mobilePermission,
            IDocNumberService docNumber)
        {
            _db       = db;
            _userContext = userContext;
            _iqc      = iqc;
            _mobilePermission = mobilePermission;
            _docNumber = docNumber;
        }

        /// <summary>
        /// 行動權限二次驗證（mobile-pwa.md §6）：角色未授權該作業時回 403。
        /// 回傳 null 表示通過。
        /// </summary>
        private async Task<IActionResult?> RequireMobilePermissionAsync(string moduleKey, string actionKey)
        {
            var roleId = _userContext.ActiveRoleId;
            if (!string.IsNullOrEmpty(roleId)
                && await _mobilePermission.CanExecuteAsync(roleId, moduleKey, actionKey))
                return null;

            return StatusCode(StatusCodes.Status403Forbidden,
                new MobileErrorResult(Error: new("FORBIDDEN", PzText.Get("Mob_ForbiddenAction"))));
        }

        // ── MIL-STD-1916 查表 ────────────────────────────────────────────────

        /// <summary>回傳 VL 選項清單（供前端下拉顯示）。</summary>
        [HttpGet("iqc/vl-options")]
        [PermissionGateExempt("MIL-STD-1916 標準查表，回傳固定選項清單，不觸及任何資料庫或業務資料")]
        public IActionResult GetVlOptions()
            => Ok(new MobileOkResult(Data: MilStd1916Service.GetVlOptions()
                .Select(v => new { v.Value, v.Label, v.Description })));

        /// <summary>
        /// 依批量 + 驗證等級查出應抽樣數（MIL-STD-1916 Table II）。
        /// </summary>
        [HttpGet("iqc/sample-size")]
        [PermissionGateExempt("MIL-STD-1916 Table II 純計算，輸入即輸出，不觸及任何資料庫或業務資料")]
        public IActionResult GetSampleSize([FromQuery] decimal lotSize, [FromQuery] int? vl = null)
        {
            // vl 選填：寫成可空，理由見 GetIpqcSpec。
            var level0 = vl ?? 4;
            if (lotSize <= 0) return BadRequest(new MobileErrorResult(Error: new("INVALID_LOT", PzText.Get("Mob_LotSizePositive"))));
            if (level0 < 1 || level0 > 7) return BadRequest(new MobileErrorResult(Error: new("INVALID_VL", PzText.Get("Mob_VlRange"))));

            var level  = (MilStd1916Service.VerificationLevel)level0;
            var n      = MilStd1916Service.GetSampleSize(lotSize, level);
            return Ok(new MobileOkResult(Data: new { sampleSize = n, acceptNumber = 0, rejectNumber = 1 }));
        }

        // ── IQC 進料檢驗 ─────────────────────────────────────────────────────

        /// <summary>
        /// 取得待檢列表（IQC + FQC + RQC）。
        /// IQC：Status=Confirmed 且尚未完成 IQC 的 GoodsReceipt 明細，套用抽樣計畫決策（VL 切換 + 跳批）。
        /// FQC/RQC：InspectionOrders 中 Status=Pending 的 FQC/RQC 待檢單。
        /// </summary>
        [HttpGet("iqc/pending")]
        public async Task<IActionResult> GetIqcPending()
        {
            // 與 POST iqc 同鍵：看得到待檢清單＝能進 IQC 作業。
            if (await RequireMobilePermissionAsync("Inv", "Iqc") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId ?? "";

            // ── IQC 進料待檢（與桌機 QCR/InspectionOrder 共用 IIqcInspectionService）──
            var iqcRows = (await _iqc.GetPendingLinesAsync(plantId))
                .Select(r => (object)new
                {
                    inspectionType    = "IQC",
                    goodsReceiptId    = r.GoodsReceiptId,
                    typeCode          = r.TypeCode,
                    receiptDate       = r.ReceiptDate.ToString("yyyy-MM-dd"),
                    lineNo            = r.LineNo,
                    productCode       = r.ProductCode,
                    qty               = r.Qty,
                    uomCode           = r.UomCode,
                    verificationLevel = r.VerificationLevel,
                    vlLabel           = $"VL-{r.VerificationLevel}",
                    recommendedSampleSize = r.RecommendedSampleSize,
                    samplingState     = r.SamplingState,
                    acceptNumber      = 0,
                    iqcStatus         = "Pending",
                    // GoodsReceipt／OutsourceReceipt（委外收回品，2026-10-12 起也要品檢才能進一般倉）
                    sourceKind        = r.SourceKind
                })
                .ToList();

            // ── FQC / RQC 待檢單（InspectionOrders.Status = Pending）────────────
            var pendingOrders = await _db.InspectionOrders.AsNoTracking()
                .Where(o => o.PlantId == plantId
                         && (o.InspectionType == "FQC" || o.InspectionType == "RQC")
                         && o.Status == "Pending")
                .OrderBy(o => o.CreatedAt)
                .Select(o => (object)new
                {
                    inspectionType    = o.InspectionType,
                    inspectionOrderId = o.InspectionOrderId,
                    sourceDocId       = o.SourceDocId,
                    sourceLineNo      = o.SourceLineNo,
                    productCode       = o.ProductCode,
                    qty               = o.LotSize,
                    verificationLevel = o.VerificationLevel,
                    vlLabel           = $"VL-{o.VerificationLevel}",
                    recommendedSampleSize = o.SampleSize,
                    samplingState     = "Normal",
                    acceptNumber      = 0,
                    iqcStatus         = "Pending"
                })
                .ToListAsync();

            return Ok(new MobileOkResult(Data: iqcRows.Concat(pendingOrders)));
        }

        /// <summary>
        /// 提交 IQC 檢驗結果（MIL-STD-1916：Ac=0，任何不良即拒批）。
        /// </summary>
        [HttpPost("iqc")]
        public async Task<IActionResult> SubmitIqc([FromBody] IqcSubmitRequest req)
        {
            if (await RequireMobilePermissionAsync("Inv", "Iqc") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId ?? "";

            var outcome = await _iqc.SubmitAsync(new IqcSubmitInput(
                req.GoodsReceiptId, req.LineNo, req.ProductCode, req.LotSize,
                req.SampleQty, req.FailQty, req.VerificationLevel, req.Remarks), plantId);

            if (!outcome.Success)
            {
                var error = new MobileErrorResult(Error: new(outcome.ErrorCode!, outcome.Message));
                return outcome.ErrorCode == "IQC_ALREADY_DONE" ? Conflict(error) : BadRequest(error);
            }

            return Ok(new MobileOkResult(
                Data: new
                {
                    Id = outcome.RecordId,
                    outcome.InspectionOrderId,
                    result = outcome.Result,
                    sampleSize = outcome.SampleSize,
                    failQty = outcome.FailQty
                },
                Message: outcome.Message));
        }

        // ── IPQC 製程檢驗 ────────────────────────────────────────────────────

        /// <summary>
        /// 查詢適用的 IPQC 檢驗規格（含規格項目）。前端 ipqc-process.html 用此端點預載規格項目列表。
        ///
        /// <para>帶 <paramref name="dispatchId"/> 時，品號與批量一律由伺服器從派工／工單取得，
        /// <paramref name="productCode"/>／<paramref name="lotSize"/> 被忽略。原本量測頁拿不到品號，
        /// 就拿工單號頂替（ipqc-process.page.js 的 fallback），品號專屬規格因此從未在手機上出現過，
        /// 永遠退回通用規格——而送出時伺服器會依真正的品號判定，兩邊必須看同一份規格。</para>
        /// </summary>
        [HttpGet("ipqc/spec")]
        public async Task<IActionResult> GetIpqcSpec(
            [FromQuery] string? productCode, [FromQuery] decimal? lotSize = null, [FromQuery] string? dispatchId = null)
        {
            // ⚠️ 選填的實值型別查詢參數一律寫成可空（decimal?）。Core 的 LocalizableDefaultMessageProvider
            //    替「所有」非可空實值型別補 [Required]——連 action 參數也算——於是 `decimal lotSize = 1`
            //    沒帶時不會取預設值，而是 400「lotSize 為必填」。手機首件／巡檢頁帶 dispatchId 不帶 lotSize，
            //    規格因此從未載入過，畫面退回預設抽樣數、並跳出「操作失敗(400)」（2026-10-05 手機實測）。
            var lot = lotSize ?? 1;
            // 與 POST ipqc 同鍵（呼叫端是 mfg/ipqc-process 頁）。
            if (await RequireMobilePermissionAsync("Mfg", "Inspect") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId ?? "";

            if (!string.IsNullOrEmpty(dispatchId))
            {
                var ctx = await FindIpqcDispatchAsync(dispatchId, plantId);
                if (ctx == null)
                    return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));
                (productCode, lot) = (ctx.Value.ProductCode, ctx.Value.LotSize);
            }
            else if (string.IsNullOrEmpty(productCode))
                return BadRequest(new MobileErrorResult(Error: new("MISSING_PRODUCT", PzText.Get("Mob_ProductCodeRequired"))));

            var spec = await MobileInspectionJudge.FindSpecAsync(_db, plantId, productCode);

            if (spec == null)
            {
                // 沒有規格時回傳預設（VL-IV，空項目列表）
                var defaultVl   = MilStd1916Service.VerificationLevel.IV;
                var defaultN    = MilStd1916Service.GetSampleSize(lot, defaultVl);
                return Ok(new MobileOkResult(Data: new
                {
                    specId            = (string?)null,
                    verificationLevel = (int)defaultVl,
                    vlLabel           = $"VL-{(int)defaultVl}",
                    sampleSize        = defaultN,
                    triggerMode       = "Always",
                    items             = Array.Empty<object>()
                }));
            }

            var vl = (MilStd1916Service.VerificationLevel)spec.VerificationLevel;
            var n  = MilStd1916Service.GetSampleSize(lot, vl);

            return Ok(new MobileOkResult(Data: new
            {
                spec.SpecId,
                spec.VerificationLevel,
                vlLabel    = $"VL-{spec.VerificationLevel}",
                sampleSize = n,
                spec.TriggerMode,
                items = spec.Items.Select(i => new
                {
                    i.SpecItemId,
                    i.SeqNo,
                    i.ItemName,
                    i.UomCode,
                    i.LowerSpec,
                    i.TargetValue,
                    i.UpperSpec
                })
            }));
        }

        /// <summary>
        /// 提交 IPQC 製程檢驗結果（MIL-STD-1916：Ac=0，Fail 時觸發 Dispatch 暫停）。
        ///
        /// <para>手機只提供量測值、抽樣數、不良數與驗證等級；品號、批量、應抽樣數、規格上下限、
        /// 逐項是否符合與合格判定都由伺服器決定（見 <see cref="MobileInspectionJudge"/>）。
        /// 請求中的 ProductCode／LotSize／SampleSize／Status 與規格項目的上下限保留供舊版 App 相容，不採用。</para>
        /// </summary>
        [HttpPost("ipqc")]
        public async Task<IActionResult> SubmitIpqc([FromBody] IpqcSubmitRequest req)
        {
            // IPQC 由投產報工模組頁面（mfg/ipqc-process.html）發起，沿用 Mfg/Inspect 作業權限
            if (await RequireMobilePermissionAsync("Mfg", "Inspect") is { } forbidden) return forbidden;

            if (!MobileInspectionJudge.IsValidQty(req.ActualSampleQty, req.FailQty))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_QTY", PzText.Get("Mob_SampleQtyInvalid"))));
            if (!Enum.IsDefined(typeof(MilStd1916Service.VerificationLevel), req.VerificationLevel))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_VL", PzText.Get("Mob_VlRange"))));

            var plantId = _userContext.PlantId ?? "";

            // 派工必須是本廠的：原本查不到也照建檢驗單，任何字串都能掛一張「合格」的 IPQC。
            var ctx = await FindIpqcDispatchAsync(req.SourceDocId, plantId);
            if (ctx == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));
            var (dispatch, productCode, lotSize) = ctx.Value;

            var refs  = MobileInspectionJudge.FromSpec(await MobileInspectionJudge.FindSpecAsync(_db, plantId, productCode));
            var items = (req.Items ?? []).Select(it =>
            {
                var m = new MobileInspectionJudge.Measured(it.SpecItemId, it.SeqNo, it.ItemName, it.UomCode,
                    it.LowerSpec, it.TargetValue, it.UpperSpec, it.MeasuredValue);
                return MobileInspectionJudge.BuildItem(m, MobileInspectionJudge.Match(m, refs));
            }).ToList();

            // Ac=0：不良數 > 0 或任一量測不符合即拒收。
            var (passed, failQty) = MobileInspectionJudge.Judge(req.FailQty, items);
            var vl  = (MilStd1916Service.VerificationLevel)req.VerificationLevel;
            var now = DateTime.UtcNow;

            var order = new InspectionOrder
            {
                InspectionType    = "IPQC",
                PlantId           = plantId,
                SourceDocId       = dispatch.DispatchId,
                ProductCode       = productCode,
                LotSize           = lotSize,
                SampleSize        = MilStd1916Service.GetSampleSize(lotSize, vl),
                VerificationLevel = req.VerificationLevel,
                ActualSampleQty   = (int)req.ActualSampleQty,
                FailQty           = failQty,
                Status            = passed ? "Pass" : "Fail",
                Remarks           = req.Remarks,
                InspectorId       = _userContext.UserId,
                InspectedAt       = now,
                CreatedAt         = now,
                UpdatedAt         = now,
                Items             = items
            };
            _db.InspectionOrders.Add(order);

            // Fail → Dispatch 轉 OnHold（與規格 §14.4 一致）
            if (!passed && dispatch.Status == Modules.MFG.Models.DispatchStatus.InProgress)
                Modules.MFG.Services.DispatchHold.Hold(
                    dispatch, Modules.MFG.Models.DispatchHoldReason.IpqcFailed, now);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Fail 路徑會改 Dispatch.Status（xmin）；並發競態回 409 結構化封包而非裸 500。
                return Conflict(new MobileErrorResult(Error: new("CONCURRENCY_CONFLICT", PzText.Get("Mob_ConcurrencyReload"))));
            }

            var msg = passed
                ? PzText.Get("Mob_IpqcPass")
                : items.Any(i => i.IsConforming == false)
                    ? PzText.Get("Mob_IpqcFailOutOfSpec")
                    : PzText.Get("Mob_IpqcFailDefectsFmt", failQty);

            return Ok(new MobileOkResult(
                Data: new { order.InspectionOrderId, order.Status },
                Message: msg));
        }

        /// <summary>本廠的派工，以及伺服器認定的品號（工單）與批量（派工數量）。查不到回 null。</summary>
        private async Task<(Modules.MFG.Models.Dispatch Dispatch, string ProductCode, decimal LotSize)?> FindIpqcDispatchAsync(
            string? dispatchId, string plantId)
        {
            if (string.IsNullOrEmpty(dispatchId)) return null;
            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == dispatchId && d.PlantId == plantId);
            if (dispatch == null) return null;
            var productCode = await _db.WorkOrders.AsNoTracking()
                .Where(w => w.WorkOrderId == dispatch.WorkOrderId)
                .Select(w => w.ProductCode)
                .FirstOrDefaultAsync() ?? "";
            return (dispatch, productCode, dispatch.Quantity);
        }

        // ── 庫存盤點（inventory.md §12 / mobile-pwa.md）────────────────────────
        // 依清冊盤點：帶 ItemId，直接對應 PC 已產生的 InventoryCountItem。
        // 自由盤點：不帶 ItemId，依「使用者＋廠別」自動找/建進行中的 WithoutSheet 清冊
        // （取代舊版 /api/warehouse/cycle-count 立即建調整單的獨立路徑，統一走盤點清冊
        //  初盤→複盤→PC 結帳的單一資料流程）。

        /// <summary>列出目前可依清冊盤點的清冊（WithSheet，狀態=Counting）。</summary>
        [HttpGet("count/sheets")]
        public async Task<IActionResult> GetCountSheets()
        {
            if (await RequireMobilePermissionAsync("Inv", "Count") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId ?? "";

            var sheets = await _db.InventoryCountSheets.AsNoTracking()
                .Where(s => s.PlantId == plantId && s.SheetStatus == "Counting" && s.CountMode == "WithSheet")
                // 已全部盤完（沒有待盤明細）的清冊不列：手機上選了只會看到空清單（2026-10-05 手機實測）。
                // 清冊仍是 Counting——等電腦端複盤／結帳；複盤若把明細退回 Pending，這裡會再出現。
                .Where(s => s.Items.Any(i => i.ItemStatus == "Pending"))
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.SheetId,
                    s.SnapshotAt,
                    s.WhCodeFrom,
                    s.WhCodeTo,
                    pendingCount = s.Items.Count(i => i.ItemStatus == "Pending"),
                    totalCount   = s.Items.Count,
                })
                .ToListAsync();

            return Ok(new MobileOkResult(Data: sheets));
        }

        /// <summary>取得清冊明細（預設只列待盤 Pending）。</summary>
        [HttpGet("count/{sheetId}/items")]
        public async Task<IActionResult> GetCountItems(string sheetId, [FromQuery] bool? onlyPending = null)
        {
            if (await RequireMobilePermissionAsync("Inv", "Count") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId ?? "";
            var sheet = await _db.InventoryCountSheets.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SheetId == sheetId && s.PlantId == plantId);
            if (sheet == null)
                return NotFound(new MobileErrorResult(Error: new("NOT_FOUND", PzText.Get("Mob_CountSheetNotFound"))));

            var q = _db.InventoryCountItems.AsNoTracking().Where(i => i.SheetId == sheetId);
            if (onlyPending ?? true) q = q.Where(i => i.ItemStatus == "Pending");   // 可空理由見 GetIpqcSpec

            var items = await q.OrderBy(i => i.SeqNo)
                .Select(i => new
                {
                    i.ItemId, i.SeqNo, i.WhCode, i.LocationCode, i.LotNo, i.ProductCode,
                    i.BookQty, i.ItemStatus,
                })
                .ToListAsync();

            return Ok(new MobileOkResult(Data: items));
        }

        /// <summary>
        /// 提交初盤（支援批次多筆，可跨儲位/品號）。
        /// 帶 ItemId → 依清冊盤點，直接對應該筆明細；不帶 → 自由盤點，自動找/建當前
        /// 使用者在本廠進行中的 WithoutSheet 清冊，找不到對應明細則自動新建（BookQty=目前庫存快照）。
        /// </summary>
        [HttpPost("count/first-count")]
        public async Task<IActionResult> SubmitFirstCount([FromBody] CountSubmitRequest req)
        {
            if (await RequireMobilePermissionAsync("Inv", "Count") is { } forbidden) return forbidden;
            if (req.Lines == null || req.Lines.Count == 0)
                return BadRequest(new MobileErrorResult(Error: new("NO_LINES", PzText.Get("Mob_NoCountData"))));
            if (await GetCountReplayAsync(req.ClientId) is { } replay) return Ok(replay);

            var plantId = _userContext.PlantId ?? "";
            var userId  = _userContext.UserId  ?? "";
            var now     = DateTime.UtcNow;

            InventoryCountSheet? freeSheet = null;
            var results = new List<CountLineResult>();

            foreach (var line in req.Lines)
            {
                InventoryCountItem? item;

                if (line.ItemId.HasValue)
                {
                    item = await _db.InventoryCountItems.Include(i => i.Sheet)
                        .FirstOrDefaultAsync(i => i.ItemId == line.ItemId.Value);
                    if (item == null || item.Sheet?.PlantId != plantId)
                    {
                        results.Add(new(line.ItemId, null, false, PzText.Get("Mob_LineNotFound")));
                        continue;
                    }
                    if (item.Sheet!.SheetStatus != "Counting")
                    {
                        results.Add(new(line.ItemId, item.SheetId, false, PzText.Get("Mob_SheetNotCounting")));
                        continue;
                    }
                    // 覆盤之後不得再改初盤（同桌機端 InventoryCountController.SaveFirstCount）。
                    // 沒有這道，重送或晚到的初盤會把狀態寫回 Counted，覆盤結果被架空。
                    if (item.ItemStatus is not ("Pending" or "Counted"))
                    {
                        results.Add(new(line.ItemId, item.SheetId, false, PzText.Get("Mob_LineRecountedLocked")));
                        continue;
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(line.ProductCode) || string.IsNullOrWhiteSpace(line.LocationCode))
                    {
                        results.Add(new(null, null, false, PzText.Get("Mob_ProductOrLocationMissing")));
                        continue;
                    }

                    if (freeSheet == null)
                    {
                        freeSheet = await _db.InventoryCountSheets.Include(s => s.Items)
                            .Where(s => s.PlantId == plantId && s.CreatedBy == userId
                                     && s.CountMode == "WithoutSheet" && s.SheetStatus == "Counting")
                            .OrderByDescending(s => s.CreatedAt)
                            .FirstOrDefaultAsync();

                        if (freeSheet == null)
                        {
                            var sheetId = await _docNumber.NextAsync(
                                "InventoryCountSheet", plantId, DateOnly.FromDateTime(DateTime.Today));
                            freeSheet = new InventoryCountSheet
                            {
                                SheetId     = sheetId,
                                PlantId     = plantId,
                                CountMode   = "WithoutSheet",
                                SheetStatus = "Counting",
                                SnapshotAt  = now,
                                CreatedBy   = userId,
                                CreatedAt   = now,
                                UpdatedAt   = now,
                            };
                            _db.InventoryCountSheets.Add(freeSheet);
                        }
                    }

                    var lotNo = line.LotNo ?? "";
                    item = freeSheet.Items.FirstOrDefault(i =>
                        i.ProductCode == line.ProductCode && i.LocationCode == line.LocationCode
                        && i.LotNo == lotNo && i.FirstCountQty == null);

                    if (item == null)
                    {
                        var sysQty = await _db.Inventories.AsNoTracking()
                            .Where(i => i.PlantId == plantId && i.ProductCode == line.ProductCode
                                     && i.LocationCode == line.LocationCode && i.LotNo == lotNo)
                            .Select(i => (decimal?)i.QtyOnHand)
                            .FirstOrDefaultAsync() ?? 0m;

                        item = new InventoryCountItem
                        {
                            SheetId      = freeSheet.SheetId,
                            SeqNo        = freeSheet.Items.Count + 1,
                            WhCode       = line.WhCode ?? "",
                            LocationCode = line.LocationCode,
                            LotNo        = lotNo,
                            ProductCode  = line.ProductCode,
                            BookQty      = sysQty,
                            ItemStatus   = "Pending",
                        };
                        freeSheet.Items.Add(item);
                    }
                }

                item.FirstCountQty  = line.Qty;
                item.FirstCountedBy = userId;
                item.FirstCountedAt = now;
                item.ItemStatus     = "Counted";

                results.Add(new(item.ItemId == 0 ? null : item.ItemId, item.SheetId, true, null));
            }

            var okCount = results.Count(r => r.Success);
            var result  = new MobileOkResult(Data: results, Message: PzText.Get("Mob_CountSubmittedFmt", okCount, results.Count));
            TrackCountSyncLog(req, "/api/mobile/inv/count/first-count", results, result.Message);
            await _db.SaveChangesAsync();
            return Ok(result);
        }

        /// <summary>列出可複盤項目（狀態=Counted 且非本人初盤，橫跨本廠所有進行中清冊）。</summary>
        [HttpGet("count/recount-pending")]
        public async Task<IActionResult> GetRecountPending()
        {
            if (await RequireMobilePermissionAsync("Inv", "Count") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId ?? "";
            var userId  = _userContext.UserId  ?? "";

            var items = await _db.InventoryCountItems.AsNoTracking()
                .Where(i => i.Sheet!.PlantId == plantId && i.Sheet.SheetStatus == "Counting"
                         && i.ItemStatus == "Counted" && i.FirstCountedBy != userId)
                .OrderBy(i => i.SheetId).ThenBy(i => i.SeqNo)
                .Select(i => new
                {
                    i.ItemId, i.SheetId, i.SeqNo, i.WhCode, i.LocationCode, i.LotNo, i.ProductCode,
                    i.BookQty, i.FirstCountQty,
                })
                .ToListAsync();

            return Ok(new MobileOkResult(Data: items));
        }

        /// <summary>提交複盤（支援批次多筆）。複盤人不得與初盤人相同（比照 PC 規則職責分離）。</summary>
        [HttpPost("count/recount")]
        public async Task<IActionResult> SubmitRecount([FromBody] CountSubmitRequest req)
        {
            if (await RequireMobilePermissionAsync("Inv", "Count") is { } forbidden) return forbidden;
            if (req.Lines == null || req.Lines.Count == 0)
                return BadRequest(new MobileErrorResult(Error: new("NO_LINES", PzText.Get("Mob_NoRecountData"))));
            if (await GetCountReplayAsync(req.ClientId) is { } replay) return Ok(replay);

            var plantId = _userContext.PlantId ?? "";
            var userId  = _userContext.UserId  ?? "";
            var now     = DateTime.UtcNow;
            var results = new List<CountLineResult>();

            foreach (var line in req.Lines)
            {
                if (!line.ItemId.HasValue)
                {
                    results.Add(new(null, null, false, PzText.Get("Mob_LineIdMissing")));
                    continue;
                }

                var item = await _db.InventoryCountItems.Include(i => i.Sheet)
                    .FirstOrDefaultAsync(i => i.ItemId == line.ItemId.Value);
                if (item == null || item.Sheet?.PlantId != plantId)
                {
                    results.Add(new(line.ItemId, null, false, PzText.Get("Mob_LineNotFound")));
                    continue;
                }
                if (item.Sheet!.SheetStatus != "Counting" || item.ItemStatus != "Counted")
                {
                    results.Add(new(line.ItemId, item.SheetId, false, PzText.Get("Mob_LineCannotRecount")));
                    continue;
                }
                if (item.FirstCountedBy == userId)
                {
                    results.Add(new(line.ItemId, item.SheetId, false, PzText.Get("Mob_RecounterSameAsCounter")));
                    continue;
                }

                item.RecountQty  = line.Qty;
                item.RecountedBy = userId;
                item.RecountedAt = now;
                item.ItemStatus  = "Recounted";
                results.Add(new(line.ItemId, item.SheetId, true, null));
            }

            var okCount = results.Count(r => r.Success);
            var result  = new MobileOkResult(Data: results, Message: PzText.Get("Mob_RecountSubmittedFmt", okCount, results.Count));
            TrackCountSyncLog(req, "/api/mobile/inv/count/recount", results, result.Message);
            await _db.SaveChangesAsync();
            return Ok(result);
        }

        // ── 盤點的離線重送去重 ───────────────────────────────────────────────
        //
        // 盤點原本不能離線排隊，因為重送不冪等：自由盤點每送一次就多建一筆明細
        // （同品號同儲位、FirstCountQty 已有值的不會被重用）。有了 clientId 之後，
        // 同一批第二次到達只回第一次的結果，不再動資料。
        //
        // 帳面量 BookQty 刻意**維持取送達當下**的庫存（2026-09-25 使用者裁決），
        // 不是盤點當下——離線期間若有異動，差異會包含那段期間的進出。

        private static readonly System.Text.Json.JsonSerializerOptions SnapshotJson =
            new(System.Text.Json.JsonSerializerDefaults.Web);   // camelCase：前端讀 data[].success

        /// <summary>SyncLog.ResponseSnapshot 上限 2000 字元（見 SyncLog 模型）。</summary>
        private const int SnapshotMaxLength = 2000;

        private async Task<MobileOkResult?> GetCountReplayAsync(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return null;   // 線上直接送出，不去重
            var log = await _db.SyncLogs.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId);
            if (log == null) return null;
            return log.ResponseSnapshot != null
                ? System.Text.Json.JsonSerializer.Deserialize<MobileOkResult>(log.ResponseSnapshot, SnapshotJson)
                : new MobileOkResult(Message: PzText.Get("Mob_AlreadyProcessed"));
        }

        /// <summary>
        /// 與業務變更同一次 SaveChanges 寫入 SyncLog（單一交易，業務失敗就一起不存在）。
        ///
        /// 快照**只存失敗的明細**：成功的那些重放時不需要（前端只用它判斷部分失敗、
        /// 顯示黃字），而整批全存很容易超過 2000 字元——一張 30 行的清冊就爆，
        /// 那會讓整筆盤點在 SaveChanges 撞欄寬、連業務資料一起存不進去。
        /// 失敗明細本身太多時從尾端截掉，訊息仍說得出總數。
        /// </summary>
        private void TrackCountSyncLog(CountSubmitRequest req, string endpoint,
            List<CountLineResult> results, string? message)
        {
            if (string.IsNullOrWhiteSpace(req.ClientId)) return;

            var failed = results.Where(r => !r.Success).ToList();
            string snapshot;
            do
            {
                snapshot = System.Text.Json.JsonSerializer.Serialize(
                    new MobileOkResult(Data: failed, Message: message), SnapshotJson);
                if (snapshot.Length <= SnapshotMaxLength || failed.Count == 0) break;
                failed.RemoveAt(failed.Count - 1);
            } while (true);

            _db.SyncLogs.Add(new Modules.MFG.Models.SyncLog
            {
                ClientId        = req.ClientId!,
                Endpoint        = endpoint,
                ClientTimestamp = req.ClientTimestamp?.ToUniversalTime() ?? DateTime.UtcNow,
                CreatedAt       = DateTime.UtcNow,
                ResponseSnapshot = snapshot.Length <= SnapshotMaxLength ? snapshot : null,
            });
        }
    }

    /// <param name="ClientId">離線佇列去重鍵；線上直接送出不帶。</param>
    public record CountSubmitRequest(List<CountLineInput> Lines, string? ClientId = null, DateTime? ClientTimestamp = null);

    public record CountLineInput(
        int?     ItemId,
        string?  ProductCode,
        string?  LocationCode,
        string?  WhCode,
        string?  LotNo,
        decimal  Qty);

    public record CountLineResult(
        int?     ItemId,
        string?  SheetId,
        bool     Success,
        string?  Message);

    public record IqcSubmitRequest(
        string  GoodsReceiptId,
        int     LineNo,
        string  ProductCode,
        decimal LotSize,
        decimal SampleQty,
        decimal FailQty,
        int     VerificationLevel,
        string? Remarks);

    public record IpqcSubmitRequest(
        string   InspectionType,
        string   SourceDocId,
        string?  ProductCode,
        decimal  LotSize,
        int      SampleSize,
        int      VerificationLevel,
        decimal  ActualSampleQty,
        decimal  FailQty,
        string?  Status,
        string?  Remarks,
        List<IpqcItemRequest>? Items);

    public record IpqcItemRequest(
        int      SeqNo,
        string   ItemName,
        string?  UomCode,
        decimal? LowerSpec,
        decimal? TargetValue,
        decimal? UpperSpec,
        decimal? MeasuredValue,
        string?  SpecItemId = null);
}
