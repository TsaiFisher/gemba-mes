using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IMrpRunExecutionService"/>
    public class MrpRunExecutionService : IMrpRunExecutionService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDateTimeService _dt;
        private readonly IMrpEngine _engine;
        private readonly IPurchaseService _purchaseService;
        private readonly IPlantParamService _plantParams;
        private readonly IUserContext _userContext;

        public MrpRunExecutionService(
            ApplicationDbContext db,
            IMrpEngine engine,
            IPurchaseService purchaseService,
            IPlantParamService plantParams,
            IUserContext userContext,
            IDateTimeService dt)
        {
            _db = db;
            _dt = dt;
            _engine = engine;
            _purchaseService = purchaseService;
            _plantParams = plantParams;
            _userContext = userContext;
        }

        public Task<MrpRunResult> ExecuteAsync(string mrpRunId, CancellationToken ct = default) =>
            RunInTransactionAsync(mrpRunId, () => _engine.RunAsync(mrpRunId, ct), ct);

        public Task<MrpRunResult> ReRunAsync(string mrpRunId, CancellationToken ct = default) =>
            RunInTransactionAsync(mrpRunId, () => _engine.ReRunAsync(mrpRunId, ct), ct);

        /// <summary>
        /// 展開＋請購單落地的共用交易外殼。
        ///
        /// 交易是必要的，不是保險：引擎的 CreateBatch 含中間 SaveChanges，失敗時必須整批回滾，
        /// 否則會留下「有生產批沒工單」的半成品資料。
        ///
        /// 例外一律**回滾後原樣往外拋**，讓呼叫端各自決定呈現方式（畫面 alert／訂單核准端記警示）；
        /// 但預檢失敗（MrpExpansionValidationException）的逐列錯誤在此就地回填——
        /// 它必須在交易之外另存，且無論哪個呼叫端都需要，放呼叫端會各寫一份。
        /// </summary>
        private async Task<MrpRunResult> RunInTransactionAsync(
            string mrpRunId, Func<Task<MrpRunResult>> expand, CancellationToken ct)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await expand();
                await CreatePurchaseRequestFromDemandsAsync(result);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (MrpExpansionValidationException vex)
            {
                // 全有全無：回滾後（首次展開）不產生任何東西／（重跑）前次展開完整保留
                await tx.RollbackAsync(ct);
                await PersistValidationErrorsAsync(mrpRunId, vex.FailedLines);
                throw;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        /// <summary>
        /// 展開失敗（交易已回滾）後，另行把各需求列的預檢錯誤寫回 ValidationError 供畫面標示。
        /// 先清空 ChangeTracker——引擎在回滾前追蹤的新增/刪除實體若殘留，這裡的 SaveChanges 會誤存。
        /// </summary>
        private async Task PersistValidationErrorsAsync(string runId, IReadOnlyList<MrpSkippedLine> failedLines)
        {
            _db.ChangeTracker.Clear();
            var errorMap = failedLines.ToDictionary(f => f.LineSeq, f => f.Error);
            var lines = await _db.MrpRunLines.Where(l => l.MrpRunId == runId).ToListAsync();
            foreach (var line in lines)
                line.ValidationError = errorMap.TryGetValue(line.LineSeq, out var err)
                    ? (err.Length > 500 ? err[..500] : err)   // 欄位 MaxLength(500)
                    : null;
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// 將本次 MRP 的採購需求（CD_MRP=P，含 BOM 各階）彙總為一張請購單草稿（mfg-domain.md §11 Phase B 落地③）；
        /// 沿用既有「請購轉採購」批次作業消化（PR→PO 已支援 OrderedQty/IsClosed 部分轉/併單/拆單）。
        /// 同品號合併（數量加總、取最早需求日）。委外需求另走獨立委外通道，不在此建。
        ///
        /// 重跑去重（稽核修正）：以 <see cref="PurchaseRequest.SourceMrpRunId"/> 比對本執行單是否已建過請購單——
        /// 尚為草稿且無明細轉採購者，直接覆寫重建（比照 ReRunAsync 對生產批/工單的重建方式）；
        /// 已送審／已核准／已有明細轉採購者不可覆寫，跳過建立新單並於警示中提示人工檢視，避免同一需求重複請購。
        /// </summary>
        private async Task CreatePurchaseRequestFromDemandsAsync(MrpRunResult result)
        {
            var plantId = _userContext.PlantId ?? string.Empty;
            var userId = _userContext.UserId ?? string.Empty;

            // 本次展開的採購需求，依品號彙總（數量加總、取最早需求日）
            var desired = result.PurchaseDemands
                .GroupBy(d => d.ProductCode)
                .ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.Qty), Date: g.Min(x => x.RequiredDate)));

            var existing = await _db.PurchaseRequests.Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.SourceMrpRunId == result.MrpRunId);

            // 情形 A：本執行單尚無請購單
            if (existing == null)
            {
                if (desired.Count == 0) return;
                var vm = new PurchaseRequestFormVm
                {
                    RequestDate = _dt.GetLocalDate(),
                    RequestedBy = userId,
                    Remarks = $"MRP {result.MrpRunId}",
                    SourceMrpRunId = result.MrpRunId,
                    Lines = await BuildLineVmsAsync(desired)
                };
                var pr = await _purchaseService.CreatePurchaseRequestAsync(vm, userId, plantId);
                await StampLineSourceAsync(pr.PurchaseRequestId, result.MrpRunId);
                result.CreatedPurchaseRequestId = pr.PurchaseRequestId;
                return;
            }

            result.CreatedPurchaseRequestId = existing.PurchaseRequestId;

            // 情形 B：既有請購單仍為草稿且無明細轉採購 → MRP 仍完全擁有，直接覆寫重建。
            // desired 為空時等同清空明細——這是正確的：需求已不存在，草稿不該留著舊明細。
            if (existing.ApprovalStatus == DocumentApprovalStatus.Draft
                && existing.Lines.All(l => l.OrderedQty <= 0))
            {
                var updateVm = new PurchaseRequestFormVm
                {
                    PurchaseRequestId = existing.PurchaseRequestId,
                    // xmin 樂觀鎖：帶上剛剛讀到的版本。MRP 認定自己完全擁有這張草稿，
                    // 但那是「讀到當下」的判斷——中間若有人動過，覆寫重建就會把對方的
                    // 修改無聲吃掉。帶版本號等於讓 UpdatePurchaseRequestAsync 幫忙確認
                    // 這個前提還成立；不成立就讓它丟出來，由 MRP 的錯誤處理接住。
                    RowVersion = existing.xmin,
                    RequestDate = existing.RequestDate,
                    RequestedBy = existing.RequestedBy,
                    Remarks = $"MRP {result.MrpRunId}",
                    Lines = await BuildLineVmsAsync(desired)
                };
                var updated = await _purchaseService.UpdatePurchaseRequestAsync(updateVm, userId);
                await StampLineSourceAsync(updated.PurchaseRequestId, result.MrpRunId);
                return;
            }

            // 情形 C：已送審／已核准／已有明細轉採購 → 不可整張覆寫，改逐品號對帳
            await ReconcileProcessedPurchaseRequestAsync(result, existing, desired);
        }

        /// <summary>採購需求彙總轉請購明細 VM（補查品號主檔取採購／庫存單位）。</summary>
        private async Task<List<PurchaseRequestLineVm>> BuildLineVmsAsync(
            Dictionary<string, (decimal Qty, DateTime Date)> desired)
        {
            // 需求信號只帶品號/數量/日期，未含單位；補查品號主檔取採購/庫存單位
            // （手動建單走 _Form JS 依 ProductOptions 自動帶入，MRP 這條路徑繞過表單須自行補）。
            var productCodes = desired.Keys.ToList();
            var uomByProduct = await _db.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => (p.PurchaseUom, p.StockUom));

            return desired
                .OrderBy(kv => kv.Key)
                .Select((kv, i) =>
                {
                    uomByProduct.TryGetValue(kv.Key, out var uom);
                    var purchaseUom = uom.PurchaseUom ?? uom.StockUom ?? "";
                    return new PurchaseRequestLineVm
                    {
                        LineNo = i + 1,
                        ProductCode = kv.Key,
                        RequestQty = kv.Value.Qty,
                        DeliveryDate = DateOnly.FromDateTime(kv.Value.Date),
                        UomCode = purchaseUom,
                        StockUomCode = uom.StockUom ?? purchaseUom
                    };
                })
                .ToList();
        }

        /// <summary>
        /// 把 MRP 產生的明細標記來源執行單號。PurchaseService 為手動建單與 MRP 共用，
        /// 不在其中夾帶 MRP 專屬欄位；由呼叫端在建立／覆寫後補蓋，讓後續重跑能分辨
        /// 「MRP 產生的明細」與「使用者事後手動加入的明細」。
        /// </summary>
        private async Task StampLineSourceAsync(string purchaseRequestId, string mrpRunId)
        {
            var lines = await _db.PurchaseRequestLines
                .Where(l => l.PurchaseRequestId == purchaseRequestId && l.SourceMrpRunId == null)
                .ToListAsync();
            foreach (var l in lines) l.SourceMrpRunId = mrpRunId;
            if (lines.Count > 0) await _db.SaveChangesAsync();
        }

        /// <summary>
        /// 逐品號對帳（§10-5 P1）：既有請購單已進入後續流程、不可整張覆寫時，比對
        /// 「本次展開的採購需求」與「本執行單先前產生的請購明細」，產出五類差異並依
        /// 明細的下游消化狀態決定處置。
        ///
        /// 「本次已無需求」再細分兩類：同一次展開若有該品號的工單，代表產銷模式由採購
        /// 改為自製／委外（<see cref="MrpPurchaseVarianceKind.ModeChanged"/>），需求只是
        /// 移轉通道而非消失——訊息一併點出接手的工單，採購人員才知道為何要結案。
        /// </summary>
        private async Task ReconcileProcessedPurchaseRequestAsync(
            MrpRunResult result, PurchaseRequest existing,
            Dictionary<string, (decimal Qty, DateTime Date)> desired)
        {
            var autoClose = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgMrpAutoCloseObsoletePr, existing.PlantId), "true",
                StringComparison.OrdinalIgnoreCase);

            // 本次展開建立工單的品號 → 判斷「模式轉換」並在訊息中指出接手的工單
            var wos = await _db.WorkOrders.AsNoTracking()
                .Where(w => w.SourceMrpRunId == result.MrpRunId)
                .Select(w => new { w.ProductCode, w.WorkOrderId })
                .ToListAsync();
            var woByProduct = wos.GroupBy(w => w.ProductCode)
                .ToDictionary(g => g.Key, g => g.Select(x => x.WorkOrderId).ToList());

            var mrpLines = existing.Lines
                .Where(l => l.SourceMrpRunId == result.MrpRunId)
                .OrderBy(l => l.LineNo)
                .ToList();
            var closedAny = false;

            foreach (var line in mrpLines)
            {
                if (desired.TryGetValue(line.ProductCode, out var want))
                {
                    if (want.Qty == line.RequestQty) continue;

                    // 已核准單據不自動改量（改量等同變更已核准內容），僅提示差異
                    result.PurchaseVariances.Add(new MrpPurchaseVariance
                    {
                        Kind = want.Qty > line.RequestQty
                            ? MrpPurchaseVarianceKind.Increased
                            : MrpPurchaseVarianceKind.Decreased,
                        Disposition = MrpPurchaseVarianceDisposition.Informational,
                        ProductCode = line.ProductCode,
                        PurchaseRequestId = existing.PurchaseRequestId,
                        LineNo = line.LineNo,
                        PreviousQty = line.RequestQty,
                        CurrentQty = want.Qty,
                        Description = $"品號 {line.ProductCode} 需求量由 {line.RequestQty} 變為 {want.Qty}，"
                            + $"請購單 {existing.PurchaseRequestId} 第 {line.LineNo} 行未自動調整，請人工確認。"
                    });
                    continue;
                }

                // 本次已無此採購需求
                if (line.IsClosed) continue;   // 已結案退場，不再提示

                var modeChanged = woByProduct.TryGetValue(line.ProductCode, out var woIds);
                var kind = modeChanged
                    ? MrpPurchaseVarianceKind.ModeChanged
                    : MrpPurchaseVarianceKind.Removed;
                var reason = modeChanged
                    ? $"品號 {line.ProductCode} 已改為自製／委外，改由工單 {string.Join("、", woIds!)} 供給"
                    : $"品號 {line.ProductCode} 本次展開已無採購需求";

                MrpPurchaseVarianceDisposition disposition;
                string action;
                if (line.OrderedQty > 0)
                {
                    disposition = MrpPurchaseVarianceDisposition.ManualRequired;
                    action = $"該行已轉採購 {line.OrderedQty}，不可自動結案，請人工判斷是否取消後續採購。";
                }
                else if (existing.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                {
                    disposition = MrpPurchaseVarianceDisposition.ManualRequired;
                    action = "請購單簽核中，請先撤回簽核再處理該行。";
                }
                else if (autoClose)
                {
                    line.IsClosed = true;
                    closedAny = true;
                    disposition = MrpPurchaseVarianceDisposition.AutoClosed;
                    action = "該行未轉採購，已自動結案退場。";
                }
                else
                {
                    disposition = MrpPurchaseVarianceDisposition.PendingClose;
                    action = "該行未轉採購，可安全結案退場；請至請購單將此行結案"
                        + $"（或開啟系統參數 {ProjectZSystemParamKeys.MfgMrpAutoCloseObsoletePr} 由系統自動處理）。";
                }

                result.PurchaseVariances.Add(new MrpPurchaseVariance
                {
                    Kind = kind,
                    Disposition = disposition,
                    ProductCode = line.ProductCode,
                    PurchaseRequestId = existing.PurchaseRequestId,
                    LineNo = line.LineNo,
                    PreviousQty = line.RequestQty,
                    CurrentQty = 0m,
                    Description = $"{reason}；請購單 {existing.PurchaseRequestId} 第 {line.LineNo} 行（{line.RequestQty}）{action}"
                });
            }

            // 本次新增、既有請購單沒有的品號
            var known = mrpLines.Select(l => l.ProductCode).ToHashSet();
            foreach (var kv in desired.Where(kv => !known.Contains(kv.Key)).OrderBy(kv => kv.Key))
            {
                result.PurchaseVariances.Add(new MrpPurchaseVariance
                {
                    Kind = MrpPurchaseVarianceKind.Added,
                    Disposition = MrpPurchaseVarianceDisposition.ManualRequired,
                    ProductCode = kv.Key,
                    PurchaseRequestId = existing.PurchaseRequestId,
                    LineNo = 0,
                    PreviousQty = 0m,
                    CurrentQty = kv.Value.Qty,
                    Description = $"品號 {kv.Key} 為本次新增的採購需求（{kv.Value.Qty}），"
                        + $"既有請購單 {existing.PurchaseRequestId} 已進入後續流程無法自動加入，請另行請購。"
                });
            }

            if (closedAny) await _db.SaveChangesAsync();

            await PersistVariancesAsync(result);
        }

        /// <summary>
        /// 差異項落地（§10-5 P2）：先刪除本執行單既有的未結差異，再寫入本次對帳結果。
        /// 差異是由最新一次展開推導出來的，不累積歷史雜訊；已結清（Resolved）者保留供追溯。
        /// 自動結案的項目已無待辦，直接標為 Resolved。
        /// </summary>
        private async Task PersistVariancesAsync(MrpRunResult result)
        {
            var stale = await _db.MrpPurchaseVarianceRecords
                .Where(v => v.MrpRunId == result.MrpRunId && v.Status == MrpVarianceStatus.Open)
                .ToListAsync();
            _db.MrpPurchaseVarianceRecords.RemoveRange(stale);

            var userId = _userContext.UserId;
            var now = DateTime.UtcNow;
            foreach (var v in result.PurchaseVariances)
            {
                var autoClosed = v.Disposition == MrpPurchaseVarianceDisposition.AutoClosed;
                _db.MrpPurchaseVarianceRecords.Add(new MrpPurchaseVarianceRecord
                {
                    MrpRunId          = result.MrpRunId,
                    ProductCode       = v.ProductCode,
                    PurchaseRequestId = v.PurchaseRequestId,
                    LineNo            = v.LineNo,
                    Kind              = v.Kind,
                    Disposition       = v.Disposition,
                    Status            = autoClosed ? MrpVarianceStatus.Resolved : MrpVarianceStatus.Open,
                    PreviousQty       = v.PreviousQty,
                    CurrentQty        = v.CurrentQty,
                    Description       = v.Description.Length > 1000 ? v.Description[..1000] : v.Description,
                    DetectedAt        = now,
                    ResolvedBy        = autoClosed ? userId : null,
                    ResolvedAt        = autoClosed ? now : null,
                    ResolutionNote    = autoClosed ? "系統自動結案退場" : null
                });
            }

            await _db.SaveChangesAsync();
        }

    }
}
