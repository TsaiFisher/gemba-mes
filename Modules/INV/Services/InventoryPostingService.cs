using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Services
{
    public class InventoryPostingService : IInventoryPostingService
    {
        private readonly ApplicationDbContext _context;
        private readonly IInventoryService _invSvc;
        private readonly IInventoryCostLayerService _costSvc;
        private readonly ISystemParamService _sysParamSvc;
        private readonly IDateTimeService _dt;
        private readonly IAccountingPeriodService? _periodSvc;
        private readonly IJournalEntryService?      _journalSvc;

        public InventoryPostingService(
            ApplicationDbContext context,
            IInventoryService invSvc,
            IInventoryCostLayerService costSvc,
            ISystemParamService sysParamSvc,
            IDateTimeService dt,
            IAccountingPeriodService? periodSvc  = null,
            IJournalEntryService?     journalSvc = null)
        {
            _context     = context;
            _invSvc      = invSvc;
            _costSvc     = costSvc;
            _sysParamSvc = sysParamSvc;
            _dt          = dt;
            _periodSvc   = periodSvc;
            _journalSvc  = journalSvc;
        }

        public async Task PostLineDetailAsync(PostLineDetailRequest req, CancellationToken ct = default)
        {
            // 期間鎖倉檢查（roadmap-fin-cst-sub.md §8：所有過帳路徑皆須前置檢查）
            if (_periodSvc != null)
                await _periodSvc.AssertCanPostAsync(req.MovementDate, req.PlantId, ct);

            // ① 取得異動類別以確定方向
            var type = await _context.InventoryTransactionTypes.FindAsync(new object[] { req.TypeCode }, ct)
                ?? throw new InvalidOperationException(PzText.Get("Inv_TxnTypeNotFoundFmt", req.TypeCode));
            int direction = type.Direction;

            // ② 取得產品與類別設定，並驗證批號
            var product = await _context.Products.AsNoTracking().Include(p => p.Category).FirstOrDefaultAsync(p => p.ProductCode == req.ProductCode, ct)
                ?? throw new InvalidOperationException(PzText.Get("Inv_ProductNotFoundFmt", req.ProductCode));
            
            var category = product.Category ?? throw new InvalidOperationException(PzText.Get("Inv_ProductCategoryMissingFmt", req.ProductCode));

            bool lotTrackingRequired = product.IsLotControlled || category.EnableLotTracking;
            if (lotTrackingRequired && string.IsNullOrWhiteSpace(req.LotNo))
                throw new InvalidOperationException(PzText.Get("Inv_LotRequiredFmt", req.ProductCode));

            // ③ 決定最終計價方式 (Fallback: Category > Global)
            var finalCostMethod = await ResolveCostMethodAsync(category.CostMethod);

            // ③-b 客供進貨／退回只能落在客供倉。
            //
            // 守門放在過帳服務而不是單據畫面，因為這裡是所有路徑的收口
            // （桌機、手機 PWA、倉儲 API 都走它），畫面層的驗證繞得過。
            //
            // **只擋這一個方向**：客供料進來時必須落在不計價的倉。反向不擋——
            // 客供料本來就要能被領去生產（`IO`）、調撥、退回，那些都從客供倉出庫。
            //
            // 沒有這道守門時，客供料落在一般倉的後果是它取得了成本：
            // 均價把客戶的料算進分母，自有料單價被壓低，並擴散到之後每一張出貨的 COGS
            // ——而數量、傳票、稽核全部正常，只有毛利莫名偏高。
            if (req.TypeCode is "CI" or "CR"
                && !await _invSvc.IsCustomerSuppliedLocationAsync(req.LocationCode, ct))
            {
                throw new InvalidOperationException(
                    PzText.Get("Inv_CustomerSuppliedWrongWarehouseFmt", req.TypeCode, req.LocationCode, req.DocNo));
            }

            // ④ 出庫驗證：儲位可用量（嚴禁負庫存）
            if (direction == -1)
            {
                var avail = await _invSvc.GetAvailableQtyAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, ct);
                if (avail < req.Qty)
                    throw new InvalidOperationException(PzText.Get("Inv_InsufficientAtLocationFmt", req.LocationCode, req.ProductCode, req.Qty, avail));
            }

            // ⑤ 成本計算分支
            if (direction == 1 && req.UnitPrice.HasValue && req.UnitPrice.Value < 0)
                throw new InvalidOperationException(PzText.Get("Inv_NegativeInboundPriceFmt", req.ProductCode, req.UnitPrice.Value));

            // 交易幣單價一律先換算成本位幣再進計價：庫存與成本層沒有幣別欄位，
            // 混入外幣數字之後就再也分不出哪一筆是哪個幣別了。
            var unitPriceLocal = req.UnitPrice.HasValue ? req.UnitPrice.Value * req.ExchangeRate : (decimal?)null;
            // 借出退回以借出成本入庫，不採手填單價（見 ResolveLendReturnUnitCostAsync）
            if (req.TypeCode == "LR" && direction == 1
                && await ResolveLendReturnUnitCostAsync(req.PlantId, req.DocNo, req.ProductCode, ct) is { } lendCost)
                unitPriceLocal = lendCost;
            else if (req.TypeCode is "SR" or "RR" && direction == 1
                && await ResolveReturnSourceUnitCostAsync(req.TypeCode, req.SourceDocId, req.SourceLineNo, ct) is { } srcCost)
                unitPriceLocal = srcCost;

            // 併入既有存貨價值（委外收貨的在外原料）；只影響存貨計價，分錄金額於下方扣回
            // ——那筆錢本來就在存貨科目裡，再借一次會讓存貨憑空翻倍。
            var carriedInUnit = req.CarriedInCost != 0m && req.Qty != 0m ? req.CarriedInCost / req.Qty : 0m;

            decimal unitCost = 0;

            // 客供料完全不計價：客戶的料沒有取得成本，兩種計價方式都跳過
            // （不進移動平均的分母、不建 FIFO 成本層），`unitCost` 留 0。
            //
            // FIFO 那條特別需要這道前置：客供入庫本來就沒有單價可帶，
            // 而沒帶單價的入庫會被下方的守門丟例外——於是「客供料＋FIFO 品號」
            // 這個完全正常的組合根本過不了帳。
            var isCustomerSupplied = await _invSvc.IsCustomerSuppliedLocationAsync(req.LocationCode, ct);
            if (isCustomerSupplied)
            {
                var csInv = await _invSvc.GetOrCreateInventoryAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, ct);
                csInv.UnitCost = 0m;
            }
            else if (finalCostMethod == CostMethod.MovingAverage)
            {
                if (direction == 1 && unitPriceLocal.HasValue)
                {
                    unitCost = unitPriceLocal.Value + carriedInUnit;
                    await _invSvc.UpdateMovingAverageCostAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, req.Qty, unitCost, ct);
                }
                else
                {
                    var inv = await _invSvc.GetOrCreateInventoryAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, ct);
                    unitCost = inv.UnitCost;
                }
            }
            else // FIFO
            {
                if (direction == 1 && unitPriceLocal.HasValue)
                {
                    unitCost = unitPriceLocal.Value + carriedInUnit;
                    await _costSvc.AddLayerAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, req.Qty, unitCost, req.DocNo, req.MovementDate, ct);
                }
                else if (direction == 1)
                {
                    // 入庫沒帶單價時**不可以**落到下面的 ConsumeLayersAsync：那會對一筆入庫去
                    // 扣成本層，數量加回來、成本層被扣光，庫存明細帳與成本帳從此分家，
                    // 而且每一張傳票都平衡、稽核只比數量，全站不會有任何人發現。
                    throw new InvalidOperationException(
                        PzText.Get("Inv_InboundPriceMissingFmt", req.DocNo, req.ProductCode));
                }
                else
                {
                    unitCost = await _costSvc.ConsumeLayersAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, req.Qty, req.DocNo, req.MovementDate, ct);
                }
            }

            // ⑥ 寫 StockMovement
            var totalCost = Math.Round(unitCost * req.Qty, 4, MidpointRounding.AwayFromZero);
            _context.StockMovements.Add(new StockMovement
            {
                TypeCode = req.TypeCode,
                TypeCategory = req.TypeCategory,
                DocNo = req.DocNo,
                DocLineNo = req.LineNo,
                SourceDocNo = req.SourceDocId,
                SourceDocLineNo = req.SourceLineNo,
                MovementDate = req.MovementDate,
                PlantId = req.PlantId,
                ProductCode = req.ProductCode,
                LocationCode = req.LocationCode,
                LotNo = req.LotNo,
                UomCode = req.UomCode,
                Qty = req.BusinessQty ?? req.Qty,
                StockUomCode = req.StockUomCode,
                StockQty = req.Qty,
                Direction = direction,
                UnitCost = unitCost,
                TotalCost = totalCost,
                CreatedBy = req.CreatedBy,
            });

            // 自動日記帳：入庫 借存貨/貸 GR-IR；出庫 借損益科目/貸存貨
            if (_journalSvc != null)
            {
                // 併入的既有存貨價值要從分錄金額扣回：那筆錢已經在存貨科目裡（委外發料時
                // 只出數量不出金額），再借一次會讓存貨憑空翻倍。與批次路徑同一算式。
                var jLines = req.TypeCode == "BR" && direction == -1
                    ? await BuildLoanReturnLinesAsync(req.PlantId, req.DocNo, req.ProductCode, req.Qty, totalCost, ct)
                    : direction == -1 && req.TypeCategory == "Purchase"
                        && await BuildPurchaseReturnLinesAsync(req.DocNo, req.LineNo, req.Qty, totalCost, ct) is { } prLines
                    ? prLines
                    : BuildJournalLines(direction, req.TypeCode, req.TypeCategory,
                        Math.Round(totalCost - req.CarriedInCost, 4, MidpointRounding.AwayFromZero));
                if (jLines.Count > 0)
                    await _journalSvc.CreateAutoEntryAsync(
                        InvDocType, req.DocNo, req.MovementDate, req.PlantId, jLines, ct: ct);
            }

            // ⑥ 更新 Inventory
            await _invSvc.UpdateQtyAsync(req.PlantId, req.ProductCode, req.LocationCode, req.LotNo, req.Qty * direction, ct);

            // 更新來源沖銷量
            if (!string.IsNullOrEmpty(req.SourceDocId) && req.SourceLineNo.HasValue)
            {
                await UpdateSourceClearedQtyAsync(req.TypeCode, req.SourceDocId, req.SourceLineNo.Value, req.Qty, ct);
            }

            // ⑦ 更新 Line.QtyPutAway
            await req.UpdateQtyPutAwayAsync(req.Qty);

            // ⑧ 狀態檢查
            if (await req.CheckAllLinesCompleteAsync())
            {
                req.SetDocPosted();
            }
        }

        public async Task PostDocSimplifiedAsync(PostDocSimplifiedRequest req, CancellationToken ct = default)
        {
            // Phase C：期間鎖倉檢查（IAccountingPeriodService 由 ACT 模組提供）
            if (_periodSvc != null)
                await _periodSvc.AssertCanPostAsync(req.MovementDate, req.PlantId, ct);

            var type = await _context.InventoryTransactionTypes.FindAsync(new object[] { req.TypeCode }, ct)
                ?? throw new InvalidOperationException(PzText.Get("Inv_TxnTypeNotFoundFmt", req.TypeCode));
            int direction = type.Direction;

            // 逐行累計本次過帳的分錄金額，最後彙總成一張分錄（口徑同 PostLineDetailAsync）。
            // 先前簡化模式完全不入總帳，開了 inv_simplified_mode 的部署等於存貨與
            // GR-IR 在總帳上憑空消失，財報與庫存帳從第一天就對不起來。
            var journalLines = new List<JournalLineRequest>();

            foreach (var line in req.Lines)
            {
                int effectiveDirection = line.DirectionOverride ?? direction;

                // 冪等性檢查：同一張單同一行同一異動類別不得重複過帳（防止呼叫端狀態判斷
                // 失準時——如開單自動過帳與 Save/Confirm 等多進入點——重複寫入庫存異動）。
                var alreadyPosted = await _context.StockMovements.IgnoreQueryFilters()
                    .AnyAsync(m => m.DocNo == req.DocNo && m.DocLineNo == line.LineNo && m.TypeCode == req.TypeCode, ct);
                if (alreadyPosted)
                    throw new InvalidOperationException(PzText.Get("Inv_LineAlreadyPostedFmt", req.DocNo, line.LineNo));

                // ① 取得產品與類別設定，並驗證批號
                var product = await _context.Products.AsNoTracking().Include(p => p.Category).FirstOrDefaultAsync(p => p.ProductCode == line.ProductCode, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Inv_ProductNotFoundFmt", line.ProductCode));

                var category = product.Category ?? throw new InvalidOperationException(PzText.Get("Inv_ProductCategoryMissingFmt", line.ProductCode));

                bool lotTrackingRequired = product.IsLotControlled || category.EnableLotTracking;
                if (lotTrackingRequired && string.IsNullOrWhiteSpace(line.LotNo))
                    throw new InvalidOperationException(PzText.Get("Inv_LotRequiredFmt", line.ProductCode));

                // ② 出庫驗證
                if (effectiveDirection == -1)
                {
                    var avail = await _invSvc.GetAvailableQtyAsync(req.PlantId, line.ProductCode, line.LocationCode, line.LotNo, ct);
                    if (avail < line.Qty)
                        throw new InvalidOperationException(PzText.Get("Inv_InsufficientAtLocationFmt", line.LocationCode, line.ProductCode, line.Qty, avail));
                }

                // ③ 決定最終計價方式 (Fallback: Category > Global)
                var finalCostMethod = await ResolveCostMethodAsync(category.CostMethod);

                if (effectiveDirection == 1 && line.UnitPrice.HasValue && line.UnitPrice.Value < 0)
                    throw new InvalidOperationException(PzText.Get("Inv_NegativeInboundPriceFmt", line.ProductCode, line.UnitPrice.Value));

                // 同單筆路徑：交易幣單價先換算本位幣再計價
                var unitPriceLocal = line.UnitPrice.HasValue ? line.UnitPrice.Value * line.ExchangeRate : (decimal?)null;
                if (req.TypeCode == "LR" && effectiveDirection == 1
                    && await ResolveLendReturnUnitCostAsync(req.PlantId, req.DocNo, line.ProductCode, ct) is { } lendCost)
                    unitPriceLocal = lendCost;
                else if (req.TypeCode is "SR" or "RR" && effectiveDirection == 1 && !req.IsWorkOrderMaterial
                    && await ResolveReturnSourceUnitCostAsync(req.TypeCode, line.SourceDocId, line.SourceLineNo, ct) is { } srcCost)
                    unitPriceLocal = srcCost;

                // 併入既有存貨價值（委外收貨的在外原料；見 SimplifiedPostLine.CarriedInCost）。
                // 只影響存貨計價，分錄金額於下方扣回——那筆錢本來就在存貨科目裡。
                var carriedInUnit = line.CarriedInCost != 0m && line.Qty != 0m
                    ? line.CarriedInCost / line.Qty
                    : 0m;

                decimal unitCost = 0;
                if (finalCostMethod == CostMethod.MovingAverage)
                {
                    if (effectiveDirection == 1 && unitPriceLocal.HasValue)
                    {
                        unitCost = unitPriceLocal.Value + carriedInUnit;
                        await _invSvc.UpdateMovingAverageCostAsync(req.PlantId, line.ProductCode, line.LocationCode, line.LotNo, line.Qty, unitCost, ct);
                    }
                    else
                    {
                        var inv = await _invSvc.GetOrCreateInventoryAsync(req.PlantId, line.ProductCode, line.LocationCode, line.LotNo, ct);
                        unitCost = inv.UnitCost;
                    }
                }
                else // FIFO
                {
                    if (effectiveDirection == 1 && unitPriceLocal.HasValue)
                    {
                        unitCost = unitPriceLocal.Value + carriedInUnit;
                        await _costSvc.AddLayerAsync(req.PlantId, line.ProductCode, line.LocationCode, line.LotNo, line.Qty, unitCost, req.DocNo, req.MovementDate, ct);
                    }
                    else if (effectiveDirection == 1)
                    {
                        // 見上方單筆路徑的同一道守門：入庫沒帶單價時寧可擋下，也不能去扣成本層。
                        throw new InvalidOperationException(
                            PzText.Get("Inv_InboundPriceMissingFmt", req.DocNo, line.ProductCode));
                    }
                    else
                    {
                        unitCost = await _costSvc.ConsumeLayersAsync(req.PlantId, line.ProductCode, line.LocationCode, line.LotNo, line.Qty, req.DocNo, req.MovementDate, ct);
                    }
                }

                if (!req.IsWorkOrderMaterial)
                    journalLines.AddRange(req.TypeCode == "BR" && effectiveDirection == -1
                        ? await BuildLoanReturnLinesAsync(req.PlantId, req.DocNo, line.ProductCode, line.Qty,
                            Math.Round(unitCost * line.Qty, 4, MidpointRounding.AwayFromZero), ct)
                        : effectiveDirection == -1 && req.TypeCategory == "Purchase"
                            && await BuildPurchaseReturnLinesAsync(req.DocNo, line.LineNo, line.Qty,
                                Math.Round(unitCost * line.Qty, 4, MidpointRounding.AwayFromZero), ct) is { } prLines
                        ? prLines
                        : BuildJournalLines(
                            effectiveDirection, req.TypeCode, req.TypeCategory,
                            Math.Round(unitCost * line.Qty - line.CarriedInCost, 4, MidpointRounding.AwayFromZero)));

                // ③ 寫 StockMovement
                _context.StockMovements.Add(new StockMovement
                {
                    TypeCode = req.TypeCode,
                    TypeCategory = req.TypeCategory,
                    DocNo = req.DocNo,
                    DocLineNo = line.LineNo,
                    SourceDocNo = line.SourceDocId,
                    SourceDocLineNo = line.SourceLineNo,
                    MovementDate = req.MovementDate,
                    PlantId = req.PlantId,
                    ProductCode = line.ProductCode,
                    LocationCode = line.LocationCode,
                    LotNo = line.LotNo,
                    UomCode = line.UomCode,
                    Qty = line.BusinessQty ?? line.Qty,
                    StockUomCode = line.StockUomCode,
                    StockQty = line.Qty,
                    Direction = effectiveDirection,
                    UnitCost = unitCost,
                    TotalCost = unitCost * line.Qty,
                    CreatedBy = req.CreatedBy,
                });

                // ④ 更新 Inventory
                await _invSvc.UpdateQtyAsync(req.PlantId, line.ProductCode, line.LocationCode, line.LotNo, line.Qty * effectiveDirection, ct);

                // 更新來源沖銷量
                if (!string.IsNullOrEmpty(line.SourceDocId) && line.SourceLineNo.HasValue)
                {
                    await UpdateSourceClearedQtyAsync(req.TypeCode, line.SourceDocId, line.SourceLineNo.Value, line.Qty, ct);
                }

                line.SetQtyPutAway(line.Qty);
            }

            if (_journalSvc != null && journalLines.Count > 0)
            {
                await _journalSvc.CreateAutoEntryAsync(
                    InvDocType, req.DocNo, req.MovementDate, req.PlantId, journalLines, ct: ct);
            }

            req.SetDocPosted();
        }

        public async Task VoidDocAsync(VoidDocRequest req, CancellationToken ct = default)
        {
            // 期間鎖倉檢查：作廢沖銷以「作廢當下日期」入帳，該期間必須可過帳
            if (_periodSvc != null)
                await _periodSvc.AssertCanPostAsync(_dt.GetLocalDate(), req.PlantId, ct);

            var movements = _context.StockMovements
                .IgnoreQueryFilters()
                .Where(sm => sm.DocNo == req.DocNo && sm.PlantId == req.PlantId)
                .ToList();

            if (movements.Count == 0)
            {
                // 單據過帳時無庫存異動（如空白明細），無需沖銷，直接標記作廢
                req.SetDocVoid();
                return;
            }

            // 已經全部沖銷過：什麼都不做（2026-10-21 隨機序列測試 InventoryLedgerModelTests 抓到）。
            // 這裡讀的是這張單的**全部**異動，原本第二次作廢會把原單與第一次的沖銷列一起再沖——
            // 數量正負抵銷看不出來，但移動平均的反向公式被套兩次，均價就被改掉了，之後每一筆出庫成本都偏。
            // 與 VoidByMovementsAsync（2026-09-07 修過同一類問題）同一個原則：重複作廢是 no-op。
            // 每一組（行、品號、儲位、批號）的淨量都是 0 才算「已沖銷」——作廢後又以同單號重新過帳的，淨量不為 0，照常沖。
            if (movements.Any(m => m.Direction < 0) && movements.Any(m => m.Direction > 0)
                && movements.GroupBy(m => (m.DocLineNo, m.ProductCode, m.LocationCode, m.LotNo))
                            .All(g => g.Sum(m => m.StockQty * m.Direction) == 0m))
            {
                req.SetDocVoid();
                return;
            }

            foreach (var sm in movements)
            {
                var reverseDirection = sm.Direction * -1;

                if (reverseDirection == -1)
                {
                    var avail = await _invSvc.GetAvailableQtyAsync(sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, ct);
                    if (avail < sm.StockQty)
                        throw new InvalidOperationException(PzText.Get("Inv_VoidInsufficientFmt", sm.LocationCode, sm.ProductCode));
                }

                _context.StockMovements.Add(new StockMovement
                {
                    TypeCode = sm.TypeCode,
                    TypeCategory = sm.TypeCategory,
                    DocNo = sm.DocNo,
                    SourceDocNo = sm.DocNo, // 記錄來源單據
                    DocLineNo = sm.DocLineNo,
                    MovementDate = _dt.GetLocalDate(), // 以作廢當下日期為異動日（依使用者/廠別時區）
                    PlantId = sm.PlantId,
                    ProductCode = sm.ProductCode,
                    LocationCode = sm.LocationCode,
                    LotNo = sm.LotNo,
                    UomCode = sm.UomCode,
                    Qty = sm.Qty,
                    StockUomCode = sm.StockUomCode,
                    StockQty = sm.StockQty,
                    Direction = reverseDirection,
                    UnitCost = sm.UnitCost,
                    TotalCost = sm.UnitCost * sm.StockQty * -1,
                    CreatedBy = req.CreatedBy,
                    Remark = $"作廢沖銷：{req.Reason}",
                });

                // 成本也要沖，不能只沖數量。
                // 原本這裡只有 UpdateQtyAsync：數量回去了、總帳也被 ReverseJournalAsync
                // 對調沖平了，唯獨**庫存明細帳的單價**留在原地——
                //   · 移動平均品：作廢一筆入庫後均價仍含那筆的汙染，之後每一次出庫的
                //     COGS 都偏，且偏差永遠不會自己收斂；
                //   · FIFO 品：作廢入庫不收回成本層、作廢出庫不還回成本層，
                //     成本層總量與在手量從此分家。
                // 兩種情況數量都完全正確，傳票也張張平衡——INV_ROLLFORWARD 是數量型稽核，
                // 沒有任何既有防線會叫。2026-09-04 Wave 3 銷退作廢實測撞到（均價 526.70 → 895.02，
                // 作廢後數量回到 3 但均價停在 895.02）。
                //
                // ⚠️ 順序：成本沖銷一律在 UpdateQtyAsync **之前**——反向均價公式裡的
                // 「舊量」是尚未扣掉沖銷量的在手量（與入庫端的順序要求相同）。
                await ReverseCostAsync(sm, reverseDirection, ct);

                await _invSvc.UpdateQtyAsync(sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, sm.StockQty * reverseDirection, ct);

                // 反向更新來源沖銷量
                if (!string.IsNullOrEmpty(sm.SourceDocNo) && sm.SourceDocLineNo.HasValue)
                {
                    await UpdateSourceClearedQtyAsync(sm.TypeCode, sm.SourceDocNo, sm.SourceDocLineNo.Value, sm.Qty * -1, ct);
                }
            }

            await ReverseJournalAsync(req.DocNo, req.PlantId, req.Reason, ct);

            req.SetDocVoid();
        }

        /// <summary>
        /// 作廢沖銷的成本腿。<paramref name="reverseDirection"/> 是沖銷方向：
        /// −1 代表原本是入庫（要把成本收回來），+1 代表原本是出庫（要把成本還回去）。
        /// 成本口徑一律取原異動列凍結的 <see cref="StockMovement.UnitCost"/>——
        /// 那是當初實際入帳的數字；用「現況重算」會在單價變動後與原分錄不對稱，
        /// 留下永遠平不掉的尾差（與 ReverseJournalAsync 同一個理由）。
        /// </summary>
        private async Task ReverseCostAsync(StockMovement sm, int reverseDirection, CancellationToken ct)
        {
            var product = await _context.Products.AsNoTracking().Include(p => p.Category)
                .FirstOrDefaultAsync(p => p.ProductCode == sm.ProductCode, ct);
            var method = await ResolveCostMethodAsync(product?.Category?.CostMethod ?? CostMethod.SystemDefault);

            // 舊資料才會有沒帶成本的異動列；當 0 處理＝那筆本來就沒貢獻任何價值，
            // 沖銷自然也不該動到價值，與現行過帳路徑（UnitCost 必為非空）一致。
            var unitCost = sm.UnitCost ?? 0m;

            if (reverseDirection == -1)          // 原本是入庫 → 收回成本
            {
                if (method == CostMethod.MovingAverage)
                    await _invSvc.ReverseMovingAverageCostAsync(
                        sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, sm.StockQty, unitCost, ct);
                else
                    await _costSvc.RemoveLayersAsync(
                        sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, sm.StockQty, sm.DocNo, ct);
            }
            else                                  // 原本是出庫 → 還回成本
            {
                // 移動平均：出庫本來就不動均價，還回去時以原凍結單價當入庫單價，
                // 帳一致時算出來就是原值（同量同價進出，均價不變）。
                if (method == CostMethod.MovingAverage)
                {
                    await _invSvc.UpdateMovingAverageCostAsync(
                        sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, sm.StockQty, unitCost, ct);
                }
                else
                {
                    // FIFO：**還進當初被扣的那一層**，而不是另建一層。
                    //
                    // 另建一層的話總量與總價值都會回到原點，所有既有稽核（數量、借貸、
                    // GL↔明細帳）全都查不出異狀，但 FIFO 的**順序**變了：最舊的那批貨還回來
                    // 以後排到最後面，之後每一筆出庫的取價順序整個位移。
                    // W4-F10 實測：同一張跨廠調撥確認→撤回三次，第 19 層 305 → 290，
                    // 另外多出三個日期是當天的 5@300 新層——數字全平，成本悄悄變了。
                    var restored = await _costSvc.RestoreConsumedAsync(
                        sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, sm.StockQty, sm.DocNo, ct);

                    // 找不到消耗紀錄的部分才退回舊行為。會走到這裡的只有兩種：
                    // ① 消耗紀錄表上線前就已存在的歷史異動；
                    // ② 該出庫當初根本不是走 ConsumeLayersAsync（例如成本方法中途被改過）。
                    // 兩者都不該讓作廢做不下去——結果與修正前相同，不會更糟。
                    var shortfall = sm.StockQty - restored;
                    if (shortfall > 0m)
                        await _costSvc.AddLayerAsync(
                            sm.PlantId, sm.ProductCode, sm.LocationCode, sm.LotNo, shortfall, unitCost, sm.DocNo,
                            sm.MovementDate, ct);
                }
            }
        }

        /// <summary>
        /// 作廢沖銷總帳：把該單原本的自動分錄「借貸對調」寫一筆反向分錄。
        ///
        /// 先前只沖庫存異動、不碰總帳，已過帳單據作廢後 GL 的存貨與 GR-IR 科目永久高估，
        /// 且 §3.6 存貨滾動稽核以異動為基礎計算、抓不到這個差額。
        ///
        /// 用「對調原分錄」而非「依現況重算金額」：重算會在成本方法或單價變動後與原分錄
        /// 不對稱，留下永遠平不掉的尾差。簡化模式過帳本就不入總帳（無原分錄），
        /// 此處自然不產生反向分錄。
        /// 入帳日期取作廢當下（期間鎖已在本方法開頭檢查過），不回頭改已結期間。
        /// </summary>
        private async Task ReverseJournalAsync(string docNo, string plantId, string? reason, CancellationToken ct)
        {
            if (_journalSvc == null) return;

            // 回沖的是「原分錄 − 已沖銷」的**淨額**，不是原分錄的全額。
            //
            // 原本的防護是「已有反向分錄就整個跳過」，在只能作廢一次的單據上等價，
            // 但跨廠調撥的「撤回發出」（CancelConfirm）會把單子退回草稿、可以再確認一次，
            // 於是出現 確認→撤回→確認→撤回 的循環：第二次撤回會因為「已有反向分錄」
            // 而完全不回沖，第二次確認的那筆分錄就永遠留著。改看淨額之後這件事自然成立，
            // 重複作廢也照樣是 no-op（淨額為 0）。
            var lines = await _context.JournalEntries.IgnoreQueryFilters()
                .Where(j => (j.SourceDocType == InvDocType || j.SourceDocType == InvVoidDocType)
                         && j.SourceDocId == docNo && j.PlantId == plantId)
                .SelectMany(j => j.Lines)
                .ToListAsync(ct);
            if (lines.Count == 0) return;

            // 依「科目 + 各項維度」彙總，維度才不會在回沖時被抹掉。
            var reversal = lines
                .GroupBy(l => new { l.AccountCode, l.AccountName, l.CurrencyCode, l.ExchangeRate, l.CostCenter, l.SourceRef })
                .Select(g => new
                {
                    g.Key,
                    Debit       = g.Sum(l => l.Debit)  - g.Sum(l => l.Credit),
                    Amount      = g.Sum(l => l.Amount),
                    AmountLocal = g.Sum(l => l.AmountLocal),
                })
                .Where(x => x.Debit != 0m)
                .Select(x => new JournalLineRequest(
                    x.Key.AccountCode, x.Key.AccountName,
                    Debit: x.Debit < 0m ? -x.Debit : 0m,     // 淨額借貸對調
                    Credit: x.Debit > 0m ? x.Debit : 0m,
                    CurrencyCode: x.Key.CurrencyCode, Amount: x.Amount,
                    ExchangeRate: x.Key.ExchangeRate, AmountLocal: x.AmountLocal,
                    CostCenter: x.Key.CostCenter, SourceRef: x.Key.SourceRef))
                .ToList();
            if (reversal.Count == 0) return;

            await _journalSvc.CreateAutoEntryAsync(
                InvVoidDocType, docNo, _dt.GetLocalDate(), plantId, reversal,
                description: string.IsNullOrWhiteSpace(reason) ? null : reason, ct: ct);
        }

        /// <summary>庫存自動分錄的來源單別；反向分錄另立單別，才不會被下一次作廢再對調一次。</summary>
        private const string InvDocType     = "INV";
        private const string InvVoidDocType = "INV-Void";

        /// <summary>
        /// 單筆庫存異動對應的分錄行；金額為 0 或不需入帳的類別回傳空集合。
        ///
        /// 生產類（Production）刻意不入帳：領料與繳庫共用同一個存貨科目，材料在收料時
        /// 就已計入存貨，生產只是形態轉換，淨額為 0；真正的加值（人工＋製費）由
        /// ProductionCompletionService 的 §16.3 吸收分錄統一借記存貨、貸記已分配科目。
        /// 若這裡也入帳，存貨會被灌成「材料＋人工＋製費＋再一次人工製費」，
        /// 且領料會被錯記為報廢損失、繳庫被錯記為進貨暫估應付。
        ///
        /// 委外類（Outsource）同理不入帳：委外發料只是把料移到供應商廠區，
        /// 所有權未移轉、仍是本公司存貨，認列損失是錯的；加值（加工費）在
        /// 委外收貨（OI，屬 Purchase 類）借存貨／貸暫估應付時才入帳，
        /// 再由委外應付認列借回暫估應付而收斂。
        /// </summary>
        /// <summary>
        /// 所有權不隨異動移轉的異動類別——一律不產生分錄。
        ///
        /// <para><b>`CI`／`CR` 客供料</b>：客戶提供自己的料給我們加工，所有權屬客戶，
        /// **不是我們的財產也不是我們的成本**。進存貨科目等於同時虛增資產與負債；
        /// 我們只就加工費開發票。慣例是只管數量、金額不入帳（要在帳上看到價值時，
        /// 用一組永遠相等的備忘科目對沖，而不是走存貨科目）。</para>
        ///
        /// <para><b>`IL`／`LR` 借出</b>：我們的料借給別人，所有權沒有移轉，只是換了地點。
        /// 原實作讓它落到 fallback 的報廢損失——**料借出去就認一筆損失**，
        /// 而料還在我們帳上，等它還回來（`LR`）又貸一次存貨。</para>
        /// </summary>
        private static readonly HashSet<string> OwnershipUnchangedTypeCodes =
            new(StringComparer.Ordinal) { "CI", "CR", "IL", "LR" };

        /// <summary>
        /// 樣品退回（SR←SO）、重工發退料單的退料（RR←RI）入庫單價：來源出庫行的淨額平均成本（出庫 − 作廢沖銷）。
        ///
        /// <para>退回來的是當初出去的那一批，與銷貨退回取原出貨成本（SalesReturnCostBasis）同一道理。
        /// 原本照單上手填的單價入庫：均價被任意單價污染，樣品費用／重工損失沖回的金額也與當初出庫對不上
        /// （2026-10-24）。只認來源單確實是樣品單／重工發退料單的情形——工單的重工退料由製造模組
        /// 以工單層級成本入帳，不能在這裡覆蓋。查不到來源出庫（淨量 ≤ 0）回 null，維持原行為。</para>
        /// </summary>
        private async Task<decimal?> ResolveReturnSourceUnitCostAsync(
            string typeCode, string? sourceDocId, int? sourceLineNo, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sourceDocId) || sourceLineNo == null) return null;
            string issueType;
            if (typeCode == "SR" && await _context.SampleDocs.AsNoTracking().IgnoreQueryFilters()
                    .AnyAsync(d => d.SampleDocId == sourceDocId, ct))
                issueType = "SO";
            else if (typeCode == "RR" && await _context.ReworkMaterialDocs.AsNoTracking().IgnoreQueryFilters()
                    .AnyAsync(d => d.ReworkMaterialDocId == sourceDocId, ct))
                issueType = "RI";
            else
                return null;

            var net = await _context.StockMovements.AsNoTracking().IgnoreQueryFilters()
                .Where(m => m.DocNo == sourceDocId && m.DocLineNo == sourceLineNo && m.TypeCode == issueType)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Qty   = g.Sum(m => -m.Direction * m.StockQty),
                    Value = g.Sum(m => -m.Direction * m.StockQty * (m.UnitCost ?? 0m)),
                })
                .FirstOrDefaultAsync(ct);
            return net is { Qty: > 0m } && net.Value >= 0m ? Math.Round(net.Value / net.Qty, 6, MidpointRounding.AwayFromZero) : null;
        }

        /// <summary>
        /// 借出退回（LR）的入庫單價：同一往來對象、同一料號「尚未退回」的借出加權平均成本。
        ///
        /// <para>借出（IL）、借出退回（LR）都不入總帳——貨始終是我們的，價值不該因借出去又回來而改變。
        /// 原本 LR 照借出單上手填的單價入庫：填的與借出時的均價不同，明細帳價值就被改了而總帳沒動；
        /// 稽核的「借出在外」橋接又以異動價值計算，差額會被它吸收，全數還回後橋接仍掛著殘值，
        /// INV_GL 不會叫（2026-10-24）。</para>
        ///
        /// <para>尚未退回＝借出異動淨額（含作廢沖銷）− 先前退回異動淨額，各取數量與價值；與先後無關。
        /// 查不到未退回的借出（舊資料、或沒有借出就退回）回 null，維持原行為。</para>
        /// </summary>
        private async Task<decimal?> ResolveLendReturnUnitCostAsync(
            string plantId, string docNo, string productCode, CancellationToken ct)
        {
            var party = await _context.LoanDocs.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.LoanDocId == docNo)
                .Select(d => new { d.EntityType, d.EntityCode }).FirstOrDefaultAsync(ct);
            if (party == null) return null;
            var ids = await _context.LoanDocs.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.PlantId == plantId && d.LoanDocId != docNo
                         && (d.TypeCode == "IL" || d.TypeCode == "LR")
                         && d.EntityType == party.EntityType && d.EntityCode == party.EntityCode)
                .Select(d => d.LoanDocId).ToListAsync(ct);
            var moves = await _context.StockMovements.AsNoTracking().IgnoreQueryFilters()
                .Where(m => m.PlantId == plantId && m.ProductCode == productCode && ids.Contains(m.DocNo)
                         && (m.TypeCode == "IL" || m.TypeCode == "LR"))
                .Select(m => new { m.Direction, m.StockQty, m.UnitCost })
                .ToListAsync(ct);
            // 借出為出庫（−1）、退回為入庫（+1）：在外量＝−Σ方向×量
            var outQty = -moves.Sum(m => m.Direction * m.StockQty);
            var outValue = -moves.Sum(m => m.Direction * m.StockQty * (m.UnitCost ?? 0m));
            return outQty > 0m && outValue >= 0m ? Math.Round(outValue / outQty, 6, MidpointRounding.AwayFromZero) : null;
        }

        /// <summary>
        /// 借入還回（BR）的分錄：存貨照現值出、負債照**借入成本**沖，差額轉損益（2026-10-22 使用者裁決）。
        ///
        /// <para>原本與一般出庫同一套：負債也以出庫現值沖。均價在借入後變動過的話，料全數還清，
        /// 「其他應付款—借入物料」仍留下餘額（借 10@10、自購拉高均價到 15、還 10 → 負債借餘 50）——
        /// 對方的料已經還了，帳上卻還欠／多付。</para>
        ///
        /// <para>借入成本＝同一往來對象、同一料號「尚未還清」的負債 ÷ 尚未還清的量：
        /// 負債＝所有借入的價值 − 先前每張還回單**實際沖掉**的負債（讀那張單 2170 行，SourceRef＝料號；
        /// 作廢的單分錄已對沖，淨額自然為 0）。與時間先後無關——依建立時間重演在同一刻建立的單上排序不定
        /// （2026-10-23 LoanLedgerFlowTests 實測）。舊資料的還回分錄沒有料號註記時，以該單異動的出庫值估。
        /// 查不到未還清的借入時退回以現值沖、不產生差額。差額＝現值 − 借入成本：正數借 5220 存貨盤損，負數貸 4210 存貨盤盈。</para>
        /// </summary>
        private async Task<IList<JournalLineRequest>> BuildLoanReturnLinesAsync(
            string plantId, string docNo, string productCode, decimal qty, decimal inventoryValue, CancellationToken ct)
        {
            if (inventoryValue <= 0m || qty <= 0m) return Array.Empty<JournalLineRequest>();

            var party = await _context.LoanDocs.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.LoanDocId == docNo)
                .Select(d => new { d.EntityType, d.EntityCode }).FirstOrDefaultAsync(ct);
            var docs = await _context.LoanDocs.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.PlantId == plantId && d.LoanDocId != docNo
                         && (d.TypeCode == "IB" || d.TypeCode == "BR")
                         && (party == null || (d.EntityType == party.EntityType && d.EntityCode == party.EntityCode)))
                .Select(d => new { d.LoanDocId, d.TypeCode }).ToListAsync(ct);
            var ids = docs.Select(d => d.LoanDocId).ToList();
            var returnIds = docs.Where(d => d.TypeCode == "BR").Select(d => d.LoanDocId).ToList();

            var moves = await _context.StockMovements.AsNoTracking().IgnoreQueryFilters()
                .Where(m => m.PlantId == plantId && m.ProductCode == productCode && ids.Contains(m.DocNo)
                         && (m.TypeCode == "IB" || m.TypeCode == "BR"))
                .Select(m => new { m.DocNo, m.TypeCode, m.Direction, m.StockQty, m.UnitCost })
                .ToListAsync(ct);
            var borrowedQty   = moves.Where(m => m.TypeCode == "IB").Sum(m => m.Direction * m.StockQty);
            var borrowedValue = moves.Where(m => m.TypeCode == "IB").Sum(m => m.Direction * m.StockQty * (m.UnitCost ?? 0m));
            var returnedQty   = -moves.Where(m => m.TypeCode == "BR").Sum(m => m.Direction * m.StockQty);

            // 先前還回單實際沖掉的負債（借方淨額）：有料號註記的直接讀；沒有的（舊資料）以異動出庫值估
            var cleared = await _context.JournalLines.AsNoTracking().IgnoreQueryFilters()
                .Where(l => l.AccountCode == StandardAccounts.LoanPayableCode
                         && l.JournalEntry!.PlantId == plantId && returnIds.Contains(l.JournalEntry.SourceDocId!))
                .Select(l => new { Doc = l.JournalEntry!.SourceDocId!, l.SourceRef, Net = l.Debit - l.Credit })
                .ToListAsync(ct);
            decimal clearedValue = 0m;
            foreach (var g in moves.Where(m => m.TypeCode == "BR").GroupBy(m => m.DocNo))
            {
                var tagged = cleared.Where(c => c.Doc == g.Key && c.SourceRef == productCode).ToList();
                clearedValue += tagged.Count > 0
                    ? tagged.Sum(c => c.Net)
                    : -g.Sum(m => m.Direction * m.StockQty * (m.UnitCost ?? 0m));
            }

            var outQty = borrowedQty - returnedQty;
            var outValue = borrowedValue - clearedValue;
            var liability = outQty > 0m
                ? Math.Round(qty * (outValue / outQty), 4, MidpointRounding.AwayFromZero)
                : inventoryValue;
            var lines = new List<JournalLineRequest>
            {
                new(StandardAccounts.LoanPayableCode, StandardAccounts.LoanPayableName, liability, 0m, SourceRef: productCode),
                new(StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName, 0m, inventoryValue),
            };
            var diff = inventoryValue - liability;
            if (diff > 0m) lines.Add(new(StandardAccounts.InventoryLossCode, StandardAccounts.InventoryLossName, diff, 0m));
            else if (diff < 0m) lines.Add(new(StandardAccounts.InventoryGainCode, StandardAccounts.InventoryGainName, 0m, -diff));
            return lines;
        }

        /// <summary>
        /// 進貨退出（採購類出庫、連結原進貨明細）的分錄：存貨照現值出，進貨暫估應付照**原進貨單價**沖，
        /// 差額入 5152 進貨退出價差（2026-10-23）。
        ///
        /// <para>應付那一側（ReverseForReturnedQtyAsync／進貨折讓）是以原進貨單價沖 2152；存貨這一側原本以出庫現值借 2152。
        /// 同料號進貨價不同時均價不等於該張進貨價，2152 就留下差額——進 10@100、10@300 後退第一張 5 個，
        /// 留 500 永遠沖不掉，GRIR_AGING 每天報、擋月結（PayableLedgerModelTests 發現）。</para>
        ///
        /// 回 null＝不是連結進貨單的退貨（手動加入的行、或其他採購類出庫），照通用規則。
        /// </summary>
        private async Task<IList<JournalLineRequest>?> BuildPurchaseReturnLinesAsync(
            string docNo, int lineNo, decimal qty, decimal inventoryValue, CancellationToken ct)
        {
            if (inventoryValue <= 0m || qty <= 0m) return null;
            var src = await (
                    from rl in _context.GoodsReturnLines.AsNoTracking().IgnoreQueryFilters()
                    join gl in _context.GoodsReceiptLines.AsNoTracking().IgnoreQueryFilters()
                        on new { Gr = rl.GoodsReceiptId!, Line = rl.ReceiptLineNo!.Value } equals new { Gr = gl.GoodsReceiptId, Line = gl.LineNo }
                    where rl.GoodsReturnId == docNo && rl.LineNo == lineNo
                    select new { gl.LineAmount, gl.ExchangeRate, gl.StockQty, gl.ReceiptQty })
                .FirstOrDefaultAsync(ct);
            if (src == null) return null;
            var baseQty = src.StockQty > 0m ? src.StockQty : src.ReceiptQty;
            if (baseQty <= 0m) return null;
            // 原進貨每一庫存單位的本幣淨額（不含稅），與進貨入庫貸 2152、應付認列借 2152 同一口徑
            var grIr = Math.Round(qty * src.LineAmount * (src.ExchangeRate == 0m ? 1m : src.ExchangeRate) / baseQty, 4, MidpointRounding.AwayFromZero);

            var lines = new List<JournalLineRequest>
            {
                new(StandardAccounts.GrIrAPClearingCode, StandardAccounts.GrIrAPClearingName, grIr, 0m),
                new(StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName, 0m, inventoryValue),
            };
            var diff = inventoryValue - grIr;
            if (diff > 0m) lines.Add(new(StandardAccounts.PurchaseReturnVarianceCode, StandardAccounts.PurchaseReturnVarianceName, diff, 0m));
            else if (diff < 0m) lines.Add(new(StandardAccounts.PurchaseReturnVarianceCode, StandardAccounts.PurchaseReturnVarianceName, 0m, -diff));
            return lines;
        }

        private static IList<JournalLineRequest> BuildJournalLines(
            int direction, string? typeCode, string? typeCategory, decimal totalCost)
        {
            if (totalCost <= 0m || typeCategory is "Production" or "Outsource")
                return Array.Empty<JournalLineRequest>();

            if (typeCode != null && OwnershipUnchangedTypeCodes.Contains(typeCode))
                return Array.Empty<JournalLineRequest>();

            var inventory = new JournalLineRequest(
                StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName, 0m, 0m);

            // 對方科目依「方向 × 業務類別」決定；退貨要沖回原本的對方科目，
            // 不能一律丟報廢損失／進貨暫估應付，否則退一次貨就多一筆假損失。
            // 借入物料（IB 借入／BR 還回）：借貸關係，不是買賣。進存貨並掛「其他應付款—借入物料」，
            // 歸還時借回同一科目。**必須在下方 switch 之前處理**：它的 TypeCategory 是 "Loan"，
            // 沒有自己的分支就會落到 `(1, _)` 的進貨暫估應付與 `_` 的報廢損失兩條都錯的路。
            if (typeCode is "IB" or "BR")
            {
                var inv2 = new JournalLineRequest(
                    StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName, 0m, 0m);
                var loan = new JournalLineRequest(
                    StandardAccounts.LoanPayableCode, StandardAccounts.LoanPayableName, 0m, 0m);

                return direction == 1
                    ? new[] { inv2 with { Debit = totalCost }, loan with { Credit = totalCost } }
                    : new[] { loan with { Debit = totalCost }, inv2 with { Credit = totalCost } };
            }

            var (code, name) = (direction, typeCategory) switch
            {
                // 銷貨退回（入庫）：沖回銷貨成本
                (1,  "Sales")    => (StandardAccounts.CogsCode,          StandardAccounts.CogsName),
                // 樣品出貨／退回：我們自己的料送給客戶＝推廣費用，不是報廢損失。
                // ⚠️ 只能用 TypeCode 判，不能用 TypeCategory——"Sample" 類別裡同時裝著
                // 客供料（CI/CR，別人的財產，上面已回空）與自家樣品（SO/SR，我們的成本），
                // 兩者語意相反。
                (-1, _) when typeCode == "SO" => (StandardAccounts.SampleExpenseCode, StandardAccounts.SampleExpenseName),
                (1,  _) when typeCode == "SR" => (StandardAccounts.SampleExpenseCode, StandardAccounts.SampleExpenseName),
                // 盤盈／庫存調整增加：認其他收入。盤點多出來的貨沒有供應商也沒有人會來請款，
                // 落到「進貨暫估應付」等於在負債裡長出一筆 AP 端根本不知道、永遠沒人來沖的錢。
                (1,  "Adjustment") => (StandardAccounts.InventoryGainCode, StandardAccounts.InventoryGainName),
                // 盤虧／庫存調整減少：認存貨盤損，與「知道是哪一批、為什麼壞」的報廢損失分開列帳。
                (-1, "Adjustment") => (StandardAccounts.InventoryLossCode, StandardAccounts.InventoryLossName),
                // 重工發退料（庫存模組的重工發退料單，不屬於任何工單）：料發去重工就不會以存貨回來，認重工損失；
                // 退料沖回同一科目。原本落到通用規則：發料借報廢損失、**退料貸進貨暫估應付**——
                // 退一次料，負債裡就多一筆沒有供應商、永遠沒人來沖的錢（2026-10-16）。
                // 工單的重工發退料不走這裡（IsWorkOrderMaterial，由完工入庫結算）。
                (_,  "Rework")   => (StandardAccounts.ReworkLossCode,     StandardAccounts.ReworkLossName),
                // 內部訂單領用（NO）／退料（NR）：退料是沖銷領用（ord-module.md §退料訂單），兩邊同一科目。
                // 原本 NO 落到下方 `_` 的報廢損失，NR 卻落到 `(1, _)` 的進貨暫估應付——退一次料，
                // 負債裡就多一筆沒有供應商、永遠沒人來沖的錢（與 2026-10-16 重工退料同型，2026-10-24 補）。
                // 2026-10-24 使用者裁決：另立 5260 內部領用費用，不再與報廢損失混列。
                (_,  "InternalOrder") => (StandardAccounts.InternalUseExpenseCode, StandardAccounts.InternalUseExpenseName),
                // 進貨入庫：貸暫估應付，待 AP 認列時借回
                (1,  _)          => (StandardAccounts.GrIrAPClearingCode, StandardAccounts.GrIrAPClearingName),
                // 出貨：轉銷貨成本
                (-1, "Sales")    => (StandardAccounts.CogsCode,          StandardAccounts.CogsName),
                // 進貨退出：沖回暫估應付（與 AP 退貨回沖分錄對衝歸零）
                (-1, "Purchase") => (StandardAccounts.GrIrAPClearingCode, StandardAccounts.GrIrAPClearingName),
                // 報廢、樣品等其他出庫：認列損失
                _                => (StandardAccounts.ScrapLossCode,     StandardAccounts.ScrapLossName),
            };

            return direction == 1
                ? new[]
                {
                    inventory with { Debit = totalCost },
                    new JournalLineRequest(code, name, 0m, totalCost),
                }
                : new[]
                {
                    new JournalLineRequest(code, name, totalCost, 0m),
                    inventory with { Credit = totalCost },
                };
        }

        private async Task UpdateSourceClearedQtyAsync(string typeCode, string sourceDocId, int sourceLineNo, decimal qty, CancellationToken ct)
        {
            var type = await _context.InventoryTransactionTypes.AsNoTracking().Include(t => t.SourceType)
                .FirstOrDefaultAsync(t => t.TypeCode == typeCode, ct);
            if (type?.SourceType == null) return;

            var sourceCategory = type.SourceType.TypeCategory;
            if (string.IsNullOrEmpty(sourceCategory)) return;

            // 根據 SourceType 的分類找到對應表並更新
            switch (sourceCategory)
            {
                case "Loan":
                    var loanLine = await _context.LoanDocLines.FirstOrDefaultAsync(l => l.LoanDocId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (loanLine != null) loanLine.QtySourceCleared += qty;
                    break;
                case "Sample":
                    var sampleLine = await _context.SampleDocLines.FirstOrDefaultAsync(l => l.SampleDocId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (sampleLine != null) sampleLine.QtySourceCleared += qty;
                    break;
                case "Scrap":
                    var scrapLine = await _context.ScrapDocLines.FirstOrDefaultAsync(l => l.ScrapDocId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (scrapLine != null) scrapLine.QtySourceCleared += qty;
                    break;
                case "Adjustment":
                    var adjLine = await _context.StockAdjustmentLines.FirstOrDefaultAsync(l => l.StockAdjustmentId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (adjLine != null) adjLine.QtySourceCleared += qty;
                    break;
                case "Outsource":
                    var omLine = await _context.OutsourceMaterialDocLines.FirstOrDefaultAsync(l => l.OutsourceMaterialDocId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (omLine != null) omLine.QtySourceCleared += qty;
                    break;
                case "Rework":
                    var rmLine = await _context.ReworkMaterialDocLines.FirstOrDefaultAsync(l => l.ReworkMaterialDocId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (rmLine != null) rmLine.QtySourceCleared += qty;
                    break;
                case "Transfer":
                    var stLine = await _context.StockTransferLines.FirstOrDefaultAsync(l => l.StockTransferId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (stLine != null) stLine.QtySourceCleared += qty;
                    break;
                case "Purchase":
                    var grLine = await _context.GoodsReceiptLines.FirstOrDefaultAsync(l => l.GoodsReceiptId == sourceDocId && l.LineNo == sourceLineNo, ct);
                    if (grLine != null) grLine.QtySourceCleared += qty;
                    break;
            }
        }

        public async Task PostSamePlantTransferAsync(PostSamePlantTransferRequest req, CancellationToken ct = default)
        {
            if (_periodSvc != null)
                await _periodSvc.AssertCanPostAsync(req.MovementDate, req.PlantId, ct);

            foreach (var line in req.Lines)
            {
                // 批號原樣帶到目的儲位；指定撥入批號時換成新批號（裝箱／換箱），成本照樣承接。空字串＝不追蹤批號
                var lot   = line.LotNo ?? "";
                var toLot = string.IsNullOrWhiteSpace(line.ToLotNo) ? lot : line.ToLotNo.Trim();
                var avail = await _invSvc.GetAvailableQtyAsync(req.PlantId, line.ProductCode, line.FromLocationCode, lot, ct);
                if (avail < line.StockQty)
                    throw new InvalidOperationException(PzText.Get("Inv_InsufficientProductAtLocationFmt", line.ProductCode, line.FromLocationCode, line.StockQty, avail));

                var (unitCost, costMethod) = await ResolveTransferOutCostAsync(
                    req.PlantId, line.ProductCode, line.FromLocationCode, lot, line.StockQty, req.DocNo,
                    req.MovementDate, ct);

                _context.StockMovements.Add(new StockMovement
                {
                    TypeCode = "WP", TypeCategory = "Transfer",
                    DocNo = req.DocNo, DocLineNo = line.LineNo,
                    MovementDate = req.MovementDate, PlantId = req.PlantId,
                    ProductCode = line.ProductCode, LocationCode = line.FromLocationCode, LotNo = lot,
                    UomCode = line.UomCode, Qty = line.StockQty,
                    StockUomCode = line.StockUomCode, StockQty = line.StockQty,
                    Direction = -1, UnitCost = unitCost, TotalCost = unitCost * line.StockQty,
                    CreatedBy = req.CreatedBy,
                });
                await _invSvc.UpdateQtyAsync(req.PlantId, line.ProductCode, line.FromLocationCode, lot, -line.StockQty, ct);

                _context.StockMovements.Add(new StockMovement
                {
                    TypeCode = "WI", TypeCategory = "Transfer",
                    DocNo = req.DocNo, DocLineNo = line.LineNo,
                    MovementDate = req.MovementDate, PlantId = req.PlantId,
                    ProductCode = line.ProductCode, LocationCode = line.ToLocationCode, LotNo = toLot,
                    UomCode = line.UomCode, Qty = line.StockQty,
                    StockUomCode = line.StockUomCode, StockQty = line.StockQty,
                    Direction = 1, UnitCost = unitCost, TotalCost = unitCost * line.StockQty,
                    CreatedBy = req.CreatedBy,
                });
                // FIFO 的成本層是**綁儲位**的（plant/product/location/lot）：來源儲位扣掉之後，
                // 目的儲位必須補一層同單價的。少了這一段，貨在新儲位、層還留在舊儲位，
                // 全廠總量看起來還是平的（所以 FIFO_LAYER_QTY_MISMATCH 不會叫），
                // 但下一次從新儲位出庫會直接撞「FIFO 成本層不足以扣除」，
                // 而在手量從頭到尾都正確——現場只會看到一個莫名其妙的錯誤訊息。
                if (costMethod != CostMethod.MovingAverage)
                    await _costSvc.AddLayerAsync(req.PlantId, line.ProductCode, line.ToLocationCode, toLot,
                        line.StockQty, unitCost, req.DocNo, req.MovementDate, ct);

                await _invSvc.UpdateQtyAsync(req.PlantId, line.ProductCode, line.ToLocationCode, toLot, line.StockQty, ct);

                line.SetQtyPutAway(line.StockQty);
            }

            req.SetDocPosted();
        }

        public async Task PostCrossPlantShipAsync(PostCrossPlantShipRequest req, CancellationToken ct = default)
        {
            if (_periodSvc != null)
                await _periodSvc.AssertCanPostAsync(req.MovementDate, req.PlantId, ct);

            var shippedValue = 0m;
            foreach (var line in req.Lines)
            {
                var lot = line.LotNo ?? "";
                var avail = await _invSvc.GetAvailableQtyAsync(req.PlantId, line.ProductCode, line.FromLocationCode, lot, ct);
                if (avail < line.StockQty)
                    throw new InvalidOperationException(PzText.Get("Inv_InsufficientProductAtLocationFmt", line.ProductCode, line.FromLocationCode, line.StockQty, avail));

                var (unitCost, _) = await ResolveTransferOutCostAsync(
                    req.PlantId, line.ProductCode, line.FromLocationCode, lot, line.StockQty, req.DocNo,
                    req.MovementDate, ct);

                _context.StockMovements.Add(new StockMovement
                {
                    TypeCode = "XP", TypeCategory = "Transfer",
                    DocNo = req.DocNo, DocLineNo = line.LineNo,
                    MovementDate = req.MovementDate, PlantId = req.PlantId,
                    ProductCode = line.ProductCode, LocationCode = line.FromLocationCode, LotNo = lot,
                    UomCode = line.UomCode, Qty = line.StockQty,
                    StockUomCode = line.StockUomCode, StockQty = line.StockQty,
                    Direction = -1, UnitCost = unitCost, TotalCost = unitCost * line.StockQty,
                    CreatedBy = req.CreatedBy,
                });
                await _invSvc.UpdateQtyAsync(req.PlantId, line.ProductCode, line.FromLocationCode, lot, -line.StockQty, ct);
                shippedValue += unitCost * line.StockQty;

                line.SetQtyShipped(line.StockQty);
            }

            // 價值必須跟著貨走：貨已離開本廠的庫存明細帳，本廠的存貨科目就不能還掛著它。
            // 對方廠收貨時做相反的一筆，兩廠的 1450 一正一負、全公司合計為 0。
            // 見 StandardAccounts.InterPlantClearingCode。
            await CreateInterPlantJournalAsync(
                req.DocNo, req.PlantId, req.MovementDate, shippedValue, shipping: true, ct);

            req.SetDocInTransit();
        }

        public async Task PostCrossPlantReceiveAsync(PostCrossPlantReceiveRequest req, CancellationToken ct = default)
        {
            if (_periodSvc != null)
                await _periodSvc.AssertCanPostAsync(req.MovementDate, req.ToPlantId, ct);

            var receivedValue = 0m;
            foreach (var line in req.Lines)
            {
                var lot = line.LotNo ?? "";
                // 撥入單價取這一行 XP 的**淨額**（原撥出 − 撤回沖銷）。撤回發出後可再撥出，
                // 同一行會留著「舊撥出＋沖銷＋新撥出」三列；隨便抓一列（原寫法 FirstOrDefault）
                // 可能拿到舊撥出的單價，來源廠 1450 借的是新成本、目的廠貸的是舊成本，
                // 兩廠合計就永遠沖不平（2026-10-23 跨廠隨機序列抓到）。
                var xpNet = await _context.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == req.DocNo && m.DocLineNo == line.LineNo && m.TypeCode == "XP")
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Qty   = g.Sum(m => -m.Direction * m.StockQty),
                        Value = g.Sum(m => -m.Direction * m.StockQty * (m.UnitCost ?? 0m)),
                    })
                    .FirstOrDefaultAsync(ct);
                var unitCost = xpNet is { Qty: > 0m } ? xpNet.Value / xpNet.Qty : 0m;

                _context.StockMovements.Add(new StockMovement
                {
                    TypeCode = "XI", TypeCategory = "Transfer",
                    DocNo = req.DocNo, DocLineNo = line.LineNo,
                    MovementDate = req.MovementDate, PlantId = req.ToPlantId,
                    ProductCode = line.ProductCode, LocationCode = line.ToLocationCode, LotNo = lot,
                    UomCode = line.UomCode, Qty = line.StockQty,
                    StockUomCode = line.StockUomCode, StockQty = line.StockQty,
                    Direction = 1, UnitCost = unitCost, TotalCost = unitCost * line.StockQty,
                    CreatedBy = req.CreatedBy,
                });
                // 目的廠必須承接來源廠的單位成本。UpdateQtyAsync 只改數量，
                // 少了這一段，撥過去的貨在目的廠的庫存明細帳上是**零元**——
                // 之後從該廠出貨時 COGS 取的就是 0，毛利虛增，而數量完全正確，
                // 所以 INV_ROLLFORWARD（只驗數量）與傳票平衡都不會叫。
                // 同廠調撥沒這個問題：新開儲位會承接該廠現行單價（見
                // GetOrCreateInventoryAsync），但目的廠通常根本沒有這個品號的庫存。
                // 單價來源是 XP 異動的凍結成本，與同廠調撥的成本傳遞同一口徑。
                // **必須在 UpdateQtyAsync 之前**：移動均價是以「尚未加上本次入庫量」
                // 的在手量重算的（見 UpdateMovingAverageCostAsync），順序顛倒會把本次
                // 入庫量算進舊庫存裡，均價整個算錯。
                if (unitCost > 0m)
                {
                    var product = await _context.Products.AsNoTracking().Include(p => p.Category)
                        .FirstOrDefaultAsync(p => p.ProductCode == line.ProductCode, ct);
                    var method = await ResolveCostMethodAsync(
                        product?.Category?.CostMethod ?? CostMethod.SystemDefault);
                    if (method == CostMethod.MovingAverage)
                        await _invSvc.UpdateMovingAverageCostAsync(
                            req.ToPlantId, line.ProductCode, line.ToLocationCode, lot, line.StockQty, unitCost, ct);
                    else
                        await _costSvc.AddLayerAsync(
                            req.ToPlantId, line.ProductCode, line.ToLocationCode, lot, line.StockQty, unitCost, req.DocNo,
                            req.MovementDate, ct);
                }

                await _invSvc.UpdateQtyAsync(req.ToPlantId, line.ProductCode, line.ToLocationCode, lot, line.StockQty, ct);

                receivedValue += unitCost * line.StockQty;

                line.SetQtyReceived(line.StockQty);
            }

            await CreateInterPlantJournalAsync(
                req.DocNo, req.ToPlantId, req.MovementDate, receivedValue, shipping: false, ct);

            req.SetDocPosted();
        }

        /// <summary>
        /// 跨廠調撥的價值搬運分錄（<see cref="Modules.ACT.StandardAccounts.InterPlantClearingCode"/>）。
        /// 撥出＝借 廠際往來／貸 存貨；撥入＝借 存貨／貸 廠際往來。
        /// 同廠調撥不走這裡（1401 → 1401，總帳本來就不該動）。
        /// </summary>
        private async Task CreateInterPlantJournalAsync(
            string docNo, string plantId, DateOnly date, decimal value, bool shipping, CancellationToken ct)
        {
            if (_journalSvc == null) return;
            var amount = Math.Round(value, 4, MidpointRounding.AwayFromZero);
            if (amount <= 0m) return;

            var clearing = new JournalLineRequest(
                Modules.ACT.StandardAccounts.InterPlantClearingCode,
                Modules.ACT.StandardAccounts.InterPlantClearingName,
                shipping ? amount : 0m, shipping ? 0m : amount);
            var inventory = new JournalLineRequest(
                Modules.ACT.StandardAccounts.RawMaterialInventoryCode,
                Modules.ACT.StandardAccounts.RawMaterialInventoryName,
                shipping ? 0m : amount, shipping ? amount : 0m);

            await _journalSvc.CreateAutoEntryAsync(
                InvDocType, docNo, date, plantId,
                new List<JournalLineRequest> { clearing, inventory }, ct: ct);
        }

        public async Task VoidByMovementsAsync(IReadOnlyList<StockMovement> movements, VoidByMovementsRequest req, CancellationToken ct = default)
        {
            if (movements.Count == 0)
            {
                // 沒有異動就沒有廠別可檢查，也沒有東西要沖銷。
                req.SetDocVoid?.Invoke();
                return;
            }

            // 期間鎖檢查的廠別取自異動本身（req 沒有帶廠別）。一張單的異動理論上同廠，
            // 但這裡逐一檢查而不是只看第一筆——真的混廠時只擋第一筆等於漏掉其餘的。
            if (_periodSvc != null)
                foreach (var plantId in movements.Select(m => m.PlantId).Distinct())
                    await _periodSvc.AssertCanPostAsync(req.VoidDate, plantId ?? string.Empty, ct);

            // 逐（廠別, 品號, 儲位, 批號, 單別, 明細行）取**淨額**再沖，而不是逐筆反轉。
            //
            // 原本是拿「該單所有異動」逐筆反向。但作廢沖銷本身也是寫進同一張表的異動列，
            // 於是「確認 → 撤回 → 確認 → 撤回」的第二圈會把第一圈的沖銷列也反轉一次。
            // 2026-09-07 實測（XP-2609-00003 跑兩圈）：6 筆異動，其中一組是
            // 「-1 作廢沖銷」緊接「+1 作廢沖銷」——一次**從未發生**的出庫及其沖銷。
            // 帳面總量因為正負相消仍然正確，所以數量稽核與借貸平衡都不會叫，但：
            //   ・庫存異動明細帳上多出使用者從沒做過的進出；
            //   ・沖銷筆數隨撤回圈數平方成長（第 N 圈寫 2N−1 筆）；
            //   ・反轉那筆「+1 沖銷列」時要做可用量檢查——當下該儲位的貨若已被別的單
            //     領走，使用者只是想撤回自己剛剛的確認，卻會收到「可用量不足」；
            //   ・FIFO 成本層被反覆拆解重建，順序也跟著被打亂。
            //
            // 與總帳沖銷同一個原則（見 ReverseJournalAsync：回沖「原分錄 − 已沖銷」的淨額）。
            // 淨額為 0 就什麼都不做，重複作廢自然是 no-op。
            var outstanding = movements
                .GroupBy(m => new
                {
                    m.PlantId, m.ProductCode, m.LocationCode, m.LotNo,
                    m.TypeCode, m.TypeCategory, m.DocNo, m.DocLineNo,
                    m.UomCode, m.StockUomCode,
                })
                .Select(g => new
                {
                    g.Key,
                    NetQty   = g.Sum(x => x.StockQty * x.Direction),
                    NetValue = g.Sum(x => (x.UnitCost ?? 0m) * x.StockQty * x.Direction),
                })
                .Where(x => x.NetQty != 0m)
                .ToList();

            foreach (var grp in outstanding)
            {
                var reverseDirection = grp.NetQty > 0m ? -1 : 1;
                var qty = Math.Abs(grp.NetQty);
                // 淨額的加權單價：同一張單反覆確認／撤回時，各圈的凍結成本未必相同
                // （中間可能有別的單動過成本層），取淨值除以淨量才對得起原分錄。
                var unitCost = Math.Abs(grp.NetValue / grp.NetQty);

                if (reverseDirection == -1)
                {
                    var avail = await _invSvc.GetAvailableQtyAsync(
                        grp.Key.PlantId!, grp.Key.ProductCode, grp.Key.LocationCode, grp.Key.LotNo, ct);
                    if (avail < qty)
                        throw new InvalidOperationException(PzText.Get("Inv_VoidInsufficientFmt", grp.Key.LocationCode, grp.Key.ProductCode));
                }

                var reversal = new StockMovement
                {
                    TypeCode = grp.Key.TypeCode, TypeCategory = grp.Key.TypeCategory,
                    DocNo = grp.Key.DocNo, DocLineNo = grp.Key.DocLineNo,
                    SourceDocNo = grp.Key.DocNo,
                    MovementDate = req.VoidDate, PlantId = grp.Key.PlantId,
                    ProductCode = grp.Key.ProductCode, LocationCode = grp.Key.LocationCode, LotNo = grp.Key.LotNo,
                    UomCode = grp.Key.UomCode, Qty = qty,
                    StockUomCode = grp.Key.StockUomCode, StockQty = qty,
                    Direction = reverseDirection,
                    UnitCost = unitCost, TotalCost = unitCost * qty * -1,
                    CreatedBy = req.CreatedBy,
                    Remark = req.Reason != null ? $"作廢沖銷：{req.Reason}" : "作廢沖銷",
                };
                _context.StockMovements.Add(reversal);

                await _invSvc.UpdateQtyAsync(
                    grp.Key.PlantId!, grp.Key.ProductCode, grp.Key.LocationCode, grp.Key.LotNo,
                    qty * reverseDirection, ct);

                // 成本也要跟著回沖。原本這裡只回沖數量與總帳，成本層完全沒動——
                // 撥出時消耗掉的層不會還回來，於是「確認 → 撤回」一圈之後
                // 在手量回到原點、成本層卻少了一輪，來源廠的成本帳從此對不起來。
                // 這個方法只被兩支調撥用（同廠／跨廠），而另一條作廢路徑
                // （VoidLineDetailAsync）早就在呼叫同一個 ReverseCostAsync 了——
                // 兩條路徑對成本的處理本來就不該不一樣。
                await ReverseCostAsync(reversal, reverseDirection, ct);
            }

            // 沖銷分錄：庫存回沖了，總帳也要回沖，否則就是「只沖一半」。
            // 逐（單號, 廠別）處理——跨廠調撥的兩腳分別掛在來源廠與目的廠，
            // 而同廠調撥本來就沒有分錄，這裡會自然變成 no-op。
            foreach (var key in movements
                         .Where(m => m.PlantId != null)
                         .Select(m => (m.DocNo, Plant: m.PlantId!))
                         .Distinct())
                await ReverseJournalAsync(key.DocNo, key.Plant, req.Reason, ct);

            req.SetDocVoid?.Invoke();
        }

        /// <summary>
        /// 調撥「撥出」那一腳的凍結成本，依計價方式取——並且 FIFO 會**真的消耗成本層**。
        ///
        /// 原本兩支調撥（同廠 <see cref="PostSamePlantTransferAsync"/>、跨廠
        /// <see cref="PostCrossPlantShipAsync"/>）都是無條件讀 <c>Inventory.UnitCost</c>，
        /// 而那是**移動均價**欄位——FIFO 品項的成本住在 InventoryCostLayers，該欄位恆為 0。
        /// 於是跨廠調撥一個 FIFO 品項會同時發生四件事：
        ///   ① XP 異動的凍結成本是 0；
        ///   ② 目的廠承接到 0（收貨端的成本承接被 <c>unitCost &gt; 0</c> 擋掉），
        ///      之後從該廠出貨 COGS 取 0、毛利虛增；
        ///   ③ 廠際往來分錄金額 0，被 CreateInterPlantJournalAsync 略過——
        ///      兩廠的存貨科目都沒動，「每一廠的存貨科目等於該廠的庫存明細帳」不成立；
        ///   ④ 來源廠的成本層沒有人消耗：在手量減了、層沒減，
        ///      成本層總量從此永久大於在手量，每撥一次多歪一次。
        /// 而數量完全正確、每張傳票都平衡（根本沒有分錄），
        /// 只有 FIFO_LAYER_QTY_MISMATCH 這一支稽核會叫。
        ///
        /// 服務層測試看不到它：FlowTestBase 把所有產品類別都種成 MovingAverage，
        /// 於是 CrossPlantTransferFlowTests 從來沒有走到 FIFO 分支。
        ///
        /// 口徑與一般出庫（<see cref="PostLineDetailAsync"/> ⑤）完全相同，
        /// 差別只在調撥沒有 UnitPrice 可帶，必然走「取現況成本」那一路。
        /// </summary>
        private async Task<(decimal UnitCost, CostMethod Method)> ResolveTransferOutCostAsync(
            string plantId, string productCode, string locationCode, string lotNo,
            decimal qty, string docNo, DateOnly movementDate, CancellationToken ct)
        {
            var product = await _context.Products.AsNoTracking().Include(p => p.Category)
                .FirstOrDefaultAsync(p => p.ProductCode == productCode, ct);
            var method = await ResolveCostMethodAsync(product?.Category?.CostMethod ?? CostMethod.SystemDefault);

            if (method == CostMethod.MovingAverage)
            {
                var inv = await _invSvc.GetOrCreateInventoryAsync(plantId, productCode, locationCode, lotNo, ct);
                return (inv.UnitCost, method);
            }

            // FIFO：ConsumeLayersAsync 同時做兩件事——扣掉來源儲位的層、回傳被消耗那些層的
            // 加權單價。取「消耗層」而不是「全部層的平均」才是先進先出的語意。
            return (await _costSvc.ConsumeLayersAsync(plantId, productCode, locationCode, lotNo, qty, docNo, movementDate, ct), method);
        }

        private async Task<CostMethod> ResolveCostMethodAsync(CostMethod categoryMethod)
        {
            if (categoryMethod != CostMethod.SystemDefault) return categoryMethod;

            var globalValue = await _sysParamSvc.GetAsync(ProjectZSystemParamKeys.InvDefaultCostMethod);
            if (string.Equals(globalValue, "FIFO", StringComparison.OrdinalIgnoreCase))
                return CostMethod.Fifo;

            return CostMethod.MovingAverage; // 預設 MA
        }
    }
}
