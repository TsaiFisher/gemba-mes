using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 完工入庫服務實作（mfg-domain.md §5.4 / §17.2 / §16 Phase F）。
    /// 依賴 IInventoryPostingService（IT 工單繳庫 + IO 後沖）與
    /// IInventoryReservationService（釋放 Hard reservation）。
    /// </summary>
    public class ProductionCompletionService : IProductionCompletionService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryPostingService _posting;
        private readonly IInventoryReservationService _reservations;
        private readonly IDateTimeService _dt;
        private readonly IContainerUnitService _units;
        private readonly IJournalEntryService? _journalSvc;
        private readonly IPlantDefaultLocationResolver? _plantDefaults;
        private readonly IWorkOrderDemandReservation _demand;
        private const string TypeCodeComplete  = "IT";  // 工單繳庫（Direction = +1）
        private const string TypeCodeBackflush = "IO";  // 生產領料（Direction = -1，後沖）

        public ProductionCompletionService(
            ApplicationDbContext db,
            IInventoryPostingService posting,
            IInventoryReservationService reservations,
            IDateTimeService dt,
            IJournalEntryService? journalSvc = null,
            IPlantDefaultLocationResolver? plantDefaults = null,
            IWorkOrderDemandReservation? demand = null,
            IContainerUnitService? units = null)
        {
            _units       = units ?? new ContainerUnitService(db);
            _demand      = demand ?? new WorkOrderDemandReservation(db, reservations);
            _db          = db;
            _posting     = posting;
            _reservations = reservations;
            _dt          = dt;
            _journalSvc  = journalSvc;
            _plantDefaults = plantDefaults;
        }

        /// <summary>
        /// 後沖料的出庫倉儲。
        ///
        /// 後沖不經備料單（<c>StagingSourceService.FromWorkOrderAsync</c> 明文排除 Backflush），
        /// 所以沒有任何人填過它的來源——只能由系統決定。決定的依據是該料件的正常存放處，
        /// 也就是產品類別預設倉儲（與進貨上架同一套慣例）；經
        /// <see cref="IPlantDefaultLocationResolver"/> 確認在本廠可用後才採用。
        ///
        /// 取不到就退回完工入庫的倉儲——那是本方法原本唯一的行為。單倉配置下兩者相同，
        /// 故此變更對「原料與成品同倉」的既有部署完全等價；一旦依 §5.4 設了成品倉，
        /// 成品儲位不會有原料庫存，沿用它會讓出庫驗證判可用量不足，
        /// 而後沖包在完工入庫的同一個交易裡，整張工單於是再也無法入庫。
        /// </summary>
        private async Task<(string WhCode, string LocationCode)> ResolveBackflushSourceAsync(
            string productCode, string plantId, string fallbackWh, string fallbackLoc, CancellationToken ct)
        {
            if (_plantDefaults == null) return (fallbackWh, fallbackLoc);

            // 實作已抽到 IPlantDefaultLocationResolver：完工入庫的預設倉儲要用同一份，
            // 兩邊各自決定的後果見該介面的註解（W2-F6）。
            return await _plantDefaults.ResolveForProductAsync(productCode, plantId, fallbackWh, fallbackLoc, ct);
        }

        public Task CompleteAsync(
            string workOrderId, string plantId,
            string whCode, string locationCode,
            string? lotNo = null,
            CancellationToken ct = default)
            => CompleteAsync(workOrderId, plantId, whCode, locationCode, lotNo, boxing: null, ct);

        public async Task CompleteAsync(
            string workOrderId, string plantId,
            string whCode, string locationCode,
            string? lotNo,
            CompletionBoxing? boxing,
            CancellationToken ct = default)
        {
            var wo = await _db.WorkOrders
                .Include(w => w.Operations)
                    .ThenInclude(op => op.Materials)
                .FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Mfg_WorkOrderNotFoundFmt", workOrderId));

            // 整品委外的成品由委外收貨入庫（成本＝加工費＋送外料），工單隨合格入庫自動結案；
            // 再走完工入庫＝同一批貨入庫兩次、送外料再後沖一次（mfg-domain.md §19.3a）。
            if (wo.IsOutsource)
                throw new InvalidOperationException(PzText.Get("Mfg_OutsourceWoNoCompletionFmt", workOrderId));

            if (wo.Status != WorkOrderStatus.Completed)
                throw new InvalidOperationException(PzText.Get("Mfg_WorkOrderNotCompletedFmt", workOrderId, wo.Status));

            // ① 計算實際完工量＝最後一道工序的完工量（不是全部派工加總，理由見 WorkOrderOutput）
            var qtyDone = await WorkOrderOutput.CompletedQtyAsync(_db, workOrderId, ct);
            if (qtyDone <= 0)
                throw new InvalidOperationException(PzText.Get("Mfg_CompletedQtyZero"));

            // 分箱（§8 L1+）在開交易、動任何帳之前先驗完：箱數、批號長度、批號不得與既有批號重複
            var boxLots = boxing == null ? null : await ResolveBoxLotsAsync(wo, qtyDone, lotNo, boxing, ct);

            // 指定箱號（§9）：箱數要對得上、每個箱子都得是本廠啟用中的空箱且種類相符；同樣在交易前驗完。
            // 裝著本工單在製品的箱子也可以——生產中用的箱子直接轉成在庫（§9.4）
            IReadOnlyList<SmartFactory.MES.Modules.BAS.Models.ContainerUnit>? boxUnits = null;
            if (boxLots != null && boxing!.UnitIds is { Count: > 0 } unitIds)
            {
                if (unitIds.Count != boxLots.Count)
                    throw new InvalidOperationException(PzText.Get("Mfg_BoxUnitCountMismatchFmt", boxLots.Count, unitIds.Count));
                boxUnits = await _units.TakeEmptyForBindingAsync(unitIds, boxing.ContainerCode, workOrderId, ct);
            }

            // 後沖、完工入庫、預留釋放、成本結算、吸收分錄為單一原子單元：兩處過帳與分錄各自
            // 內含 SaveChanges（達 5 次存檔），須以顯式交易包住整段，任一步失敗即整體回滾。
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var today    = _dt.GetLocalDate();
            var product  = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProductCode == wo.ProductCode, ct);
            var uomCode  = product?.StockUom ?? "";

            // ② 計算實際單位成本（Phase F：以 ActualLaborCost + ActualOverheadCost，材料成本由後沖回寫後更新）
            // 先用目前的成本計算（ActualMaterialCost 在後沖完成前可能為 0，入庫後再回寫）
            var actualTotalCost = wo.ActualMaterialCost + wo.ActualLaborCost + wo.ActualOverheadCost + wo.ActualOutsourceCost;
            var unitCost = qtyDone > 0 ? actualTotalCost / qtyDone : 0m;

            // ③ Backflush 後沖：全部後沖料（IO 生產領料），**不分後沖時機**。
            //
            // PerReport（每次報工分次沖）沒有任何實作——投產不擋（後沖料免料況閘）、
            // 報工不沖、完工原本被時機篩掉，於是那種料從頭到尾沒被扣過，工單成本
            // 少算一整項而且全程無聲。在此一併沖掉：CalcBackflushQty 用的是最終完工量，
            // 所以「完工時一次沖」與「分次沖」的**總量完全相同**，帳一定對；
            // 差別只在期中 WIP 少了時間分布，而那遠優於根本不扣。
            // 真要做分次沖時，把這裡改回篩 AtCompletion、另外接報工那一段即可。
            var backflushMats = wo.Operations
                .SelectMany(op => op.Materials)
                .Where(m => m.IssueMethod == MaterialIssueMethod.Backflush)
                .ToList();

            decimal totalMaterialCost = 0m;
            int lineNo = 1;
            var backflushLines = new List<SimplifiedPostLine>();

            foreach (var mat in backflushMats)
            {
                var actualIssued = IProductionCompletionService.CalcBackflushQty(
                    mat.RequiredQty, qtyDone, wo.Quantity);
                if (actualIssued <= 0) continue;

                // 找到品號的 StockUomCode
                var matProduct = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProductCode == mat.ProductCode, ct);

                var (srcWh, srcLoc) = await ResolveBackflushSourceAsync(
                    mat.ProductCode, plantId, whCode, locationCode, ct);

                var capturedMat = mat;
                backflushLines.Add(new SimplifiedPostLine
                {
                    LineNo       = lineNo++,
                    ProductCode  = mat.ProductCode,
                    LocationCode = srcLoc,         // 該料件的正常存放處，非成品入庫儲位
                    WhCode       = srcWh,
                    LotNo        = "",
                    Qty          = actualIssued,
                    UomCode      = mat.UomCode,
                    StockUomCode = matProduct?.StockUom ?? mat.UomCode,
                    UnitPrice    = mat.UnitCost,
                    SourceDocId  = workOrderId,
                    SetQtyPutAway = qty => { capturedMat.IssuedQty = qty; }
                });

            }

            var backflushDocNo = workOrderId + "-BF";
            if (backflushLines.Count > 0)
            {
                var docPosted = false;
                await _posting.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId      = plantId,
                    DocNo        = backflushDocNo,
                    MovementDate = today,
                    TypeCode     = TypeCodeBackflush,
                    TypeCategory = "Production",
                    Lines        = backflushLines,
                    SetDocPosted = () => docPosted = true
                }, ct);

                // 後沖料成本取「實際過帳的出庫成本」，而非工單建單當下的快照價
                // mat.UnitCost。兩者只在原料成本自建單後未曾變動時才相等——一旦原料
                // 進價變了（實務常態），庫存按實際成本扣、完工品按快照價入，差額就會
                // 憑空消失在存貨科目裡，而且每張傳票都各自平衡，對不出來。
                // 口徑與下方 Push/Pull 完全一致（依方向淨計，退料抵減）。
                //
                // 取自 ChangeTracker 而不重查資料庫：本次過帳尚未 SaveChanges
                // （過帳中段存檔是本專案踩過的破口），新異動只存在於追蹤器裡。
                totalMaterialCost = _db.ChangeTracker.Entries<StockMovement>()
                    .Where(e => e.State == EntityState.Added
                             && e.Entity.DocNo == backflushDocNo
                             && e.Entity.TypeCategory == "Production")
                    .Sum(e => -(e.Entity.StockQty * e.Entity.Direction) * (e.Entity.UnitCost ?? 0m));

                // WorkOrderMaterial 無 UpdatedAt，直接 SaveChanges 即可
            }

            // ④ 回寫 ActualMaterialCost + MaterialVarianceCost（D1-A，reconciliation-decisions.md）
            // 後沖料（totalMaterialCost）＋ Push/Pull 發料實際成本一併計入，使完工品單位成本涵蓋
            // 全部耗用材料——否則存貨計價低估、下游 COGS 失真。Push/Pull 成本取本工單已過帳
            // 發退料單（MaterialSlip）對應 StockMovement 的凍結發料成本，依方向淨計（退料抵減）。
            var slipIds = await _db.MaterialSlips.AsNoTracking()
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == workOrderId
                         && s.Status == MaterialSlipStatus.Posted)
                .Select(s => s.SlipId)
                .ToListAsync(ct);
            decimal pushPullMaterialCost = 0m;
            if (slipIds.Count > 0)
            {
                pushPullMaterialCost = (await _db.StockMovements.AsNoTracking()
                    .Where(m => slipIds.Contains(m.DocNo) && WorkOrderMaterialMovement.Categories.Contains(m.TypeCategory))
                    .Select(m => new { m.StockQty, m.Direction, m.UnitCost })
                    .ToListAsync(ct))
                    .Sum(m => -(m.StockQty * m.Direction) * (m.UnitCost ?? 0m));
            }
            wo.ActualMaterialCost   += totalMaterialCost + pushPullMaterialCost;
            wo.MaterialVarianceCost  = wo.ActualMaterialCost - wo.StandardMaterialCost;   // 全料基準（D1-A）
            unitCost = qtyDone > 0
                // 工序委外成本（加工費已於委外收貨時借存貨、帶出料本來就在存貨科目）只進單位成本，不進下方吸收分錄
                ? (wo.ActualMaterialCost + wo.ActualLaborCost + wo.ActualOverheadCost + wo.ActualOutsourceCost) / qtyDone
                : 0m;

            // ④' 重工工單（§16.4b）：良品只以「不良品原值」回存貨，超出的部分（重工用料、人工、製費、委外）是
            //     異常耗損，轉 5250 重工損失（IAS 2 不得計入存貨）。原本重工發料一發出就轉損失、良品只帶人工製費
            //     入庫——不良品本身的帳面值被當損失、真正多花的工費反而進了存貨，兩邊都反了。
            //     原值的取法見 ReworkOriginalValueAsync；回存值不超過實際總成本。
            decimal reworkLoss = 0m;
            if (wo.Type == WorkOrderType.Rework)
            {
                var totalCost    = wo.ActualMaterialCost + wo.ActualLaborCost + wo.ActualOverheadCost + wo.ActualOutsourceCost;
                var receiptValue = Math.Min(totalCost, await ReworkOriginalValueAsync(wo, slipIds, ct));
                reworkLoss = totalCost - receiptValue;
                unitCost   = receiptValue / qtyDone;
            }

            // ⑤ 完工入庫 IT（工單繳庫，Direction = +1）
            var completionPosted = false;
            var capturedWo = wo;
            await _posting.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
            {
                PlantId      = plantId,
                DocNo        = workOrderId,
                MovementDate = today,
                TypeCode     = TypeCodeComplete,
                TypeCategory = "Production",
                // 不分箱＝一行；分箱＝一箱一行一個批號，單位成本相同，總量仍是 qtyDone
                Lines        = (boxLots ?? new[] { (LotNo: lotNo ?? "", Qty: qtyDone) })
                    .Select((b, i) => new SimplifiedPostLine
                    {
                        LineNo       = i + 1,
                        ProductCode  = wo.ProductCode,
                        LocationCode = locationCode,
                        WhCode       = whCode,
                        LotNo        = b.LotNo,
                        Qty          = b.Qty,
                        UomCode      = uomCode,
                        StockUomCode = uomCode,
                        UnitPrice    = unitCost,
                        SourceDocId  = workOrderId,
                        SetQtyPutAway = _ => { }   // 不需回寫，WorkOrder 是 header
                    })
                    .ToList(),
                SetDocPosted = () => completionPosted = true
            }, ct);

            // ⑤' 產出留給原需求（§24.2a）：成品鎖給來源 MRP 需求列（出貨時消耗）、半成品鎖給父工單用料（父工單發料時消耗）。
            //     上限＝毛需求 − 原需求已鎖的量；沒有毛需求（手動工單、升級前舊單）就沒有可追的原需求，不鎖。
            if (wo.GrossDemandQty > 0)
            {
                var stillNeeded = wo.GrossDemandQty - await _demand.ReservedForDemandAsync(wo, ct);
                await _demand.LockForDemandAsync(wo, Math.Min(qtyDone, Math.Max(0m, stillNeeded)), ct);
            }

            // ⑥ 釋放 InventoryReservation（MRP 展開時建立的預留）
            await _reservations.ReleaseBySourceAsync(
                ReservationSourceType.WorkOrderMaterial, workOrderId, ct);

            // 備料單接手的那部分預留（備料確認時由工單預留等額轉移過去）同樣要釋放，
            // 否則工單都結案了，備料單仍永久鎖著庫存，可用量再也回不來。
            await _reservations.ReleaseWorkOrderStagingAsync(workOrderId, ct);

            // ⑦ WorkOrder → Closed
            wo.Status    = WorkOrderStatus.Closed;
            wo.UpdatedAt = DateTime.UtcNow;

            // ⑧ Phase B：建立工單實際成本結算記錄（含差異）
            var existingCost = await _db.WorkOrderActualCosts
                .FindAsync(new object[] { workOrderId }, ct);
            if (existingCost == null)
            {
                _db.WorkOrderActualCosts.Add(new WorkOrderActualCost
                {
                    WorkOrderId      = workOrderId,
                    ActualMaterial   = wo.ActualMaterialCost,
                    ActualLabor      = wo.ActualLaborCost,
                    ActualOverhead   = wo.ActualOverheadCost,
                    ActualOutsource  = wo.ActualOutsourceCost,
                    ReworkLoss       = reworkLoss,
                    StandardMaterial = wo.StandardMaterialCost,   // 全料基準（D1-A）
                    StandardLabor    = wo.StandardLaborCost,
                    StandardOverhead = wo.StandardOverheadCost,
                    VarMaterial          = wo.ActualMaterialCost - wo.StandardMaterialCost,
                    VarLaborEfficiency   = wo.ActualLaborCost    - wo.StandardLaborCost,
                    VarOverheadRate      = wo.ActualOverheadCost - wo.StandardOverheadCost,
                    CostDate  = today,
                    Status    = CostStatus.Preliminary,
                    PlantId   = plantId,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existingCost.ActualMaterial   = wo.ActualMaterialCost;
                existingCost.ActualLabor      = wo.ActualLaborCost;
                existingCost.ActualOverhead   = wo.ActualOverheadCost;
                existingCost.ActualOutsource  = wo.ActualOutsourceCost;
                existingCost.ReworkLoss       = reworkLoss;
                existingCost.StandardMaterial   = wo.StandardMaterialCost;
                existingCost.VarMaterial        = wo.ActualMaterialCost - wo.StandardMaterialCost;
                existingCost.VarLaborEfficiency = wo.ActualLaborCost    - wo.StandardLaborCost;
                existingCost.VarOverheadRate    = wo.ActualOverheadCost - wo.StandardOverheadCost;
            }

            // 箱子綁定與入庫同一交易：入庫回滾箱子就不會被綁；兩張單同時搶同一個空箱時 xmin 讓後到者整段回滾
            if (boxUnits != null)
                for (var i = 0; i < boxUnits.Count; i++)
                    _units.Bind(boxUnits[i], wo.ProductCode, boxLots![i].LotNo, boxLots[i].Qty, workOrderId, boxing!.UserId);

            await _db.SaveChangesAsync(ct);

            // ⑨ §16.3：人工/製費吸收分錄（GL）。完工以實際總成本入庫，材料於收料時已入存貨，
            //    生產僅形態轉換；淨加值＝人工＋製費，故借存貨、貸人工/製費已分配（contra）。
            //    材料、製成品共用單一存貨科目（與銷貨 COGS 貸記存貨一致），不另設 FG 科目以免破帳。
            //    重工工單：存貨只收「回存值 − 已在存貨科目裡的料與委外」＝人工＋製費 − 重工損失（可能為負＝貸存貨，
            //    表示發進來的料有一部分沒回去），差額借 5250 重工損失；人工／製費照樣全額貸已分配科目。
            if (_journalSvc != null)
            {
                var conversionCost = wo.ActualLaborCost + wo.ActualOverheadCost;
                var inventoryLeg   = conversionCost - reworkLoss;
                if (conversionCost > 0m || reworkLoss > 0m)
                {
                    var jLines = new List<JournalLineRequest>();
                    if (reworkLoss > 0m)
                        jLines.Add(new(StandardAccounts.ReworkLossCode, StandardAccounts.ReworkLossName, reworkLoss, 0m));
                    if (inventoryLeg > 0m)
                        jLines.Add(new(StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName, inventoryLeg, 0m));
                    else if (inventoryLeg < 0m)
                        jLines.Add(new(StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName, 0m, -inventoryLeg));
                    if (wo.ActualLaborCost > 0m)
                        jLines.Add(new(StandardAccounts.DirectLaborAbsorbedCode, StandardAccounts.DirectLaborAbsorbedName, 0m, wo.ActualLaborCost));
                    if (wo.ActualOverheadCost > 0m)
                        jLines.Add(new(StandardAccounts.MfgOverheadAbsorbedCode, StandardAccounts.MfgOverheadAbsorbedName, 0m, wo.ActualOverheadCost));

                    await _journalSvc.CreateAutoEntryAsync(
                        "WorkOrderCompletion", workOrderId, today, plantId, jLines,
                        description: $"工單 {workOrderId} 完工人工/製費吸收", ct: ct);
                }
            }

            await tx.CommitAsync(ct);
        }

        /// <summary>
        /// 分箱入庫的每箱批號與數量（container-tracking.md §8 L1+）。
        /// 一箱一個批號的前提是批號不與任何既有批號重複——同品號同批號的庫存會合成一筆餘額，
        /// 「掃標籤＝掃批號＝這一箱」就不成立了；所以只要該品號用過這個批號（含已出清、零餘額的）就拒絕。
        /// </summary>
        private async Task<IReadOnlyList<(string LotNo, decimal Qty)>> ResolveBoxLotsAsync(
            WorkOrder wo, decimal qtyDone, string? lotNo, CompletionBoxing boxing, CancellationToken ct)
        {
            if (boxing.BoxQty <= 0)
                throw new InvalidOperationException(PzText.Get("Mfg_BoxQtyRequired"));
            var count = Math.Ceiling(qtyDone / boxing.BoxQty);
            if (count > CompletionBoxing.MaxBoxes)
                throw new InvalidOperationException(PzText.Get("Mfg_BoxCountTooManyFmt", count, CompletionBoxing.MaxBoxes));

            var prefix = string.IsNullOrWhiteSpace(lotNo) ? wo.WorkOrderId : lotNo.Trim();
            var boxes  = CompletionBoxing.BoxLots(qtyDone, boxing.BoxQty, prefix);

            var tooLong = boxes.FirstOrDefault(b => b.LotNo.Length > 50).LotNo;
            if (tooLong != null)
                throw new InvalidOperationException(PzText.Get("Mfg_BoxLotTooLongFmt", tooLong));

            var lots = boxes.Select(b => b.LotNo).ToList();
            var used = await _db.StockMovements.AsNoTracking()
                .Where(m => m.ProductCode == wo.ProductCode && lots.Contains(m.LotNo))
                .Select(m => m.LotNo)
                .FirstOrDefaultAsync(ct);
            if (used != null)
                throw new InvalidOperationException(PzText.Get("Mfg_BoxLotExistsFmt", wo.ProductCode, used));

            return boxes;
        }

        /// <summary>
        /// 重工工單的「不良品原值」：發進這張重工工單的不良品本身（與工單同品號的發退料淨值，取發料當下凍結的
        /// 帳面成本）。只認真的發進來的那一份——
        /// 生產中判 NG、從沒入過庫的不良品不在帳上，它的料錢早已攤進原工單良品的單位成本，原值就是 0；
        /// 改用「原工單單位成本」或品號標準單價當原值，同一份料會在存貨裡算兩次。
        /// </summary>
        private async Task<decimal> ReworkOriginalValueAsync(
            WorkOrder wo, IReadOnlyCollection<string> slipIds, CancellationToken ct)
        {
            if (slipIds.Count == 0) return 0m;
            var own = await _db.StockMovements.AsNoTracking()
                .Where(m => slipIds.Contains(m.DocNo) && m.ProductCode == wo.ProductCode
                         && WorkOrderMaterialMovement.Categories.Contains(m.TypeCategory))
                .Select(m => new { m.StockQty, m.Direction, m.UnitCost })
                .ToListAsync(ct);
            return Math.Max(0m, own.Sum(m => -(m.StockQty * m.Direction) * (m.UnitCost ?? 0m)));
        }
    }
}
