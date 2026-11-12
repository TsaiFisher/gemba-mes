using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 重工成本（mfg-domain.md §16.4b，2026-10-16 使用者裁決「原值回庫、重工增量轉損失」）。
    ///
    /// 修正前的形狀：重工工單的重工發料（RI）一發出就借 5210 報廢損失；完工只認 <c>Production</c> 類別的料，
    /// 良品以「人工＋製費」入庫——不良品本身的帳面值被當成損失，真正多花的工費反而進了存貨，兩邊都反了。
    /// 重工退料（RR）落到通用規則貸「2152 進貨暫估應付」：退一次料，負債就多一筆沒有供應商的錢。
    /// 每張傳票都平衡、存貨子帳與總帳也一致，所以沒有任何既有稽核會叫。
    ///
    /// 現在：工單的發退料不入帳（料只是進出在製品），完工時良品以「發進來的不良品原值」回存貨，
    /// 其餘（重工用料、人工、製費）借 5250 重工損失。庫存模組的重工發退料單（不屬於任何工單）
    /// 發料借 5250、退料沖回 5250。
    /// </summary>
    [Collection("Integration")]
    public class ReworkCostFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZRWK";

        private const string Mat   = "FLOW-RWK-MAT";
        private const string Fg    = "FLOW-RWK-FG";
        private const string Batch = "FLOW-RWK-PB";
        private const string Wo    = "FLOW-RWK-WO";
        private const string Disp  = "FLOW-RWK-DP";
        private const string Ri    = "FLOW-RWK-RI";
        private const string Rx    = "FLOW-RWK-RX";

        private const decimal MatPrice = 50m, FgBookUnit = 700m;
        private const decimal Labor = 200m, Overhead = 300m;

        private static readonly DateOnly StockDate = new(2097, 9, 1);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public ReworkCostFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "重工流程原料"), (Fg, "重工流程成品"));
            using var scope = NewScope(out var db);
            var slipIds = new[] { Ri, Rx };
            await db.MaterialSlipLines.IgnoreQueryFilters().Where(l => slipIds.Contains(l.SlipId)).ExecuteDeleteAsync();
            await db.MaterialSlips.IgnoreQueryFilters().Where(s => slipIds.Contains(s.SlipId)).ExecuteDeleteAsync();
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
        }

        /// <summary>
        /// FQC 判退、已入庫的 4 個不良品發進重工工單（帳面 700／個），另補料 8、退回 2，人工 200＋製費 300。
        /// 良品應以原值 2,800 回存貨；多花的 6×50＋500＝800 是重工損失。
        /// </summary>
        [Fact]
        public async Task 發進不良品的重工工單_良品以原值回存貨_重工用料與工費轉重工損失()
        {
            const decimal defectQty = 4m, issueMat = 8m, returnMat = 2m;
            await OpeningAsync(Mat, 100m, MatPrice, "FLOW-RWK-OPEN-M");
            await OpeningAsync(Fg, defectQty, FgBookUnit, "FLOW-RWK-OPEN-F");
            await SeedReworkWorkOrderAsync(defectQty, issueMat);

            await SeedSlipAsync(Ri, "RI-RWK-1", MaterialSlipCategory.ReworkIssue,
                (Fg, defectQty, null), (Mat, issueMat, "10_1"));
            (await Slips.IssueAsync(Ri)).AssertOk("重工發料");
            await SeedSlipAsync(Rx, "RX-RWK-1", MaterialSlipCategory.ReworkReturn, (Mat, returnMat, "10_1"));
            (await Slips.IssueAsync(Rx)).AssertOk("重工退料");

            using (var scope = NewScope(out var db))
            {
                // 工單的發退料不入帳：料只是進出在製品
                Assert.False(await db.JournalEntries.IgnoreQueryFilters()
                    .AnyAsync(e => e.PlantId == Plant && (e.SourceDocId == Ri || e.SourceDocId == Rx)),
                    "重工工單的重工發退料不應產生分錄（由完工結算）");
                var wom = await db.WorkOrderMaterials.IgnoreQueryFilters().SingleAsync(m => m.WorkOrderId == Wo);
                Assert.Equal(issueMat, wom.IssuedQty);
                Assert.Equal(returnMat, wom.ReturnedQty);
            }

            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            const decimal originalValue = defectQty * FgBookUnit;                               // 2,800
            const decimal netMat        = originalValue + (issueMat - returnMat) * MatPrice;    // 3,100
            const decimal reworkLoss    = netMat + Labor + Overhead - originalValue;            // 800
            await AssertSettledAsync(defectQty, netMat, originalValue, reworkLoss,
                openingValue: 100m * MatPrice + originalValue);
        }

        /// <summary>
        /// 生產中判 NG、從沒入過庫的不良品：不在帳上，原值 0。它的料錢早已攤進原工單良品的單位成本，
        /// 重工良品若再以原工單單位成本入庫，同一份料在存貨裡算兩次。重工花的錢全部是重工損失。
        /// </summary>
        [Fact]
        public async Task 不良品不在帳上的重工工單_原值為零_重工成本全數轉重工損失()
        {
            const decimal qty = 4m, issueMat = 6m;
            await OpeningAsync(Mat, 100m, MatPrice, "FLOW-RWK-OPEN-M");
            await SeedReworkWorkOrderAsync(qty, issueMat);

            await SeedSlipAsync(Ri, "RI-RWK-2", MaterialSlipCategory.ReworkIssue, (Mat, issueMat, "10_1"));
            (await Slips.IssueAsync(Ri)).AssertOk("重工發料");

            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            const decimal netMat = issueMat * MatPrice;   // 300
            await AssertSettledAsync(qty, netMat, originalValue: 0m, reworkLoss: netMat + Labor + Overhead,
                openingValue: 100m * MatPrice);
        }

        /// <summary>
        /// 庫存模組的重工發退料單（/ReworkMaterial，不屬於任何工單）：料發出去不會以存貨回來，發料借 5250；
        /// 退料沖回 5250。修正前退料貸 2152 進貨暫估應付——GR/IR 稽核會把它當成一筆等發票的進貨。
        /// </summary>
        [Fact]
        public async Task 庫存重工發退料單_發料借重工損失_退料沖回同科目_不碰進貨暫估應付()
        {
            await OpeningAsync(Mat, 100m, MatPrice, "FLOW-RWK-OPEN-M");
            await PostInvReworkAsync("FLOW-RWK-INV-RI", "RI", 10m, null);
            await PostInvReworkAsync("FLOW-RWK-INV-RR", "RR", 4m, MatPrice);

            using var scope = NewScope(out var db);
            await AssertEveryEntryBalancedAsync(db, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);
            Assert.Equal(6m * MatPrice, Net(gl, StandardAccounts.ReworkLossCode));
            Assert.Equal(-100m * MatPrice, Net(gl, StandardAccounts.GrIrAPClearingCode));   // 只剩期初進貨
            Assert.Equal(0m, Net(gl, StandardAccounts.ScrapLossCode));
            Assert.Equal(94m * MatPrice, Net(gl, StandardAccounts.RawMaterialInventoryCode));
            Assert.Equal(94m, await QtyOnHandAsync(db, Plant, Mat));

            var audit = Svc<IDataAuditService>(scope);
            Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task AssertSettledAsync(
            decimal qtyDone, decimal netMat, decimal originalValue, decimal reworkLoss, decimal openingValue)
        {
            using var scope = NewScope(out var db);

            var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
            Assert.Equal(WorkOrderStatus.Closed, wo.Status);
            Assert.Equal(netMat, wo.ActualMaterialCost);   // 重工發退料現在算進工單材料成本

            var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters().SingleAsync(c => c.WorkOrderId == Wo);
            Assert.Equal(reworkLoss, cost.ReworkLoss);
            Assert.Equal(netMat + Labor + Overhead, cost.ActualTotal);

            var receipt = await db.StockMovements.IgnoreQueryFilters()
                .SingleAsync(m => m.DocNo == Wo && m.TypeCode == "IT");
            Assert.Equal(qtyDone, receipt.StockQty);
            Assert.Equal(originalValue / qtyDone, receipt.UnitCost);

            await AssertEveryEntryBalancedAsync(db, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);
            Assert.Equal(reworkLoss, Net(gl, StandardAccounts.ReworkLossCode));
            Assert.Equal(0m, Net(gl, StandardAccounts.ScrapLossCode));
            Assert.Equal(-Labor, Net(gl, StandardAccounts.DirectLaborAbsorbedCode));
            Assert.Equal(-Overhead, Net(gl, StandardAccounts.MfgOverheadAbsorbedCode));
            Assert.Equal(-openingValue, Net(gl, StandardAccounts.GrIrAPClearingCode));   // 退料不再長出暫估應付
            // 總帳存貨＝期初 −（重工損失 − 人工製費）；與子帳逐筆一致
            var expectedInventory = openingValue + Labor + Overhead - reworkLoss;
            Assert.Equal(expectedInventory, Net(gl, StandardAccounts.RawMaterialInventoryCode));
            var subledger = await db.Inventories.IgnoreQueryFilters()
                .Where(i => i.PlantId == Plant).SumAsync(i => i.QtyOnHand * i.UnitCost);
            Assert.Equal(expectedInventory, subledger);

            var audit = Svc<IDataAuditService>(scope);
            Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            Assert.Empty(await audit.AuditWoCostJournalDriftAsync(Plant));
            Assert.Empty(await audit.AuditWorkOrderMaterialIssueAsync(Plant));
        }

        private async Task OpeningAsync(string product, decimal qty, decimal price, string docNo)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = product, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task PostInvReworkAsync(string docNo, string typeCode, decimal qty, decimal? unitPrice)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
            {
                PlantId = Plant, DocNo = docNo, MovementDate = StockDate,
                TypeCode = typeCode, TypeCategory = "Rework",
                Lines =
                [
                    new SimplifiedPostLine
                    {
                        LineNo = 1, ProductCode = Mat, WhCode = Wh, LocationCode = Loc,
                        Qty = qty, UomCode = Uom, StockUomCode = Uom, UnitPrice = unitPrice,
                        SetQtyPutAway = _ => { },
                    },
                ],
                SetDocPosted = () => { },
            });
            await db.SaveChangesAsync();
        }

        /// <summary>已報工完成、等待完工入庫的重工工單（一道工序、一行補料）。</summary>
        private async Task SeedReworkWorkOrderAsync(decimal qty, decimal matRequired)
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = Batch, MrpRunId = "FLOW-RWK-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = qty, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                RoutingId = "FLOW-RWK-RT", Quantity = qty, Type = WorkOrderType.Rework,
                ReworkFromWorkOrderId = "FLOW-RWK-ORIG",
                Status = WorkOrderStatus.Completed,
                ActualLaborCost = Labor, ActualOverheadCost = Overhead,
                StandardMaterialCost = matRequired * MatPrice,
                PlantId = Plant,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-WCR", UnitTime = 1m,
            });
            db.WorkOrderMaterials.Add(new WorkOrderMaterial
            {
                WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                RequiredQty = matRequired, UomCode = Uom, UnitCost = MatPrice,
                IssueMethod = MaterialIssueMethod.Push,
            });
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10,
                Quantity = qty, QtyDone = qty, Status = DispatchStatus.Closed, PlantId = Plant,
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedSlipAsync(string slipId, string slipNo, MaterialSlipCategory category,
            params (string Product, decimal Qty, string? SourceLineId)[] lines)
        {
            var isReturn = MaterialSlipFlow.IsReturn(category);
            using var scope = NewScope(out var db);
            var slip = new MaterialSlip
            {
                SlipId = slipId, SlipNo = slipNo, Category = category,
                Status = MaterialSlipStatus.Confirmed, SlipDate = StockDate,
                SourceDocType = "WorkOrder", SourceDocId = Wo, PlantId = Plant,
            };
            var n = 0;
            foreach (var (product, qty, src) in lines)
                slip.Lines.Add(new MaterialSlipLine
                {
                    SlipId = slipId, LineNo = ++n, SourceLineId = src,
                    ProductCode = product, RequestedQty = qty, ConfirmedQty = qty, UomCode = Uom,
                    FromWarehouseCode = Wh, FromLocationCode = Loc,
                    ToWarehouseCode   = isReturn ? Wh : null,
                    ToLocationCode    = isReturn ? Loc : null,
                    LotNo = "",
                });
            db.MaterialSlips.Add(slip);
            await db.SaveChangesAsync();
        }
    }
}
