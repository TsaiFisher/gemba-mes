using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 Wave 2 的 2-8：**超領**與**退料**。
    ///
    /// 兩個判準分開看：
    ///
    /// ① 超領——驗收書原寫「應被擋或需授權」，但這件事早已裁決成**不設上限**
    ///    （test-hardening-plan.md §1013：不良重投／耗損補發是正常製造場景），
    ///    超領的金額由工單用量差異（<c>MaterialVarianceCost</c>）承接。本測試釘的是
    ///    已裁決的行為，不是驗收書那句話——否則會照著一份過期的期望去改壞設計。
    ///
    /// ② 退料應回沖成本——這才是有風險的一半。發料按實際成本扣庫存，退料若沒有
    ///    對等的成本回來，工單就被多扣了一次料錢，而**沒有任何一張傳票會不平衡**：
    ///    生產類異動根本不入總帳（<c>BuildJournalLines</c> 對 Production/Outsource 回空），
    ///    差額只存在庫存子帳與工單成本裡。
    ///
    /// 情境刻意用「退到退料暫存儲位」而非退回原領用儲位——那是現場常態
    ///（料退回倉庫的暫存區待驗，不會直接塞回原架位），也是唯一能把
    /// 「入庫成本從哪來」這個問題逼出來的走法：退回原儲位時該儲位的
    /// <c>Inventory.UnitCost</c> 剛好還在，掩蓋了取值來源。
    /// </summary>
    [Collection("Integration")]
    public class MfgMaterialReturnFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGR";

        private const string Mat       = "FLOW-MFGR-MAT";
        private const string Fg        = "FLOW-MFGR-FG";
        private const string Batch     = "FLOW-MFGR-PB";
        private const string Wo        = "FLOW-MFGR-WO";
        private const string Disp      = "FLOW-MFGR-DP";
        private const string IssueSlip = "FLOW-MFGR-MI";
        private const string RtnSlip   = "FLOW-MFGR-MX";

        /// <summary>退料暫存儲位（與領用儲位不同，見類別註解）。</summary>
        private string RtnLoc => $"{Plant}-RTN";

        private static readonly DateOnly StockDate = new(2097, 8, 1);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public MfgMaterialReturnFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "退料流程原料"), (Fg, "退料流程成品"));
            using var scope = NewScope(out var db);
            await db.MaterialSlipLines.IgnoreQueryFilters()
                .Where(l => l.SlipId == IssueSlip || l.SlipId == RtnSlip).ExecuteDeleteAsync();
            await db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.SlipId == IssueSlip || s.SlipId == RtnSlip).ExecuteDeleteAsync();
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();

            // 退料暫存儲位（基底只建一個儲位；Purge 依 WhCode 刪，故掛同一倉即可一併清掉）
            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == Wh && l.LocationCode == RtnLoc))
                db.StorageLocations.Add(new StorageLocation
                { WhCode = Wh, LocationCode = RtnLoc, LocationName = "退料暫存區" });

            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task OverIssueThenReturn_ReturnCarriesBackIssueCost_AndWorkOrderChargedNetOnly()
        {
            const decimal openingQty = 600m, openingPrice = 50m;   // 移動平均 50
            const decimal woQty = 100m, qtyDone = 100m;
            const decimal requiredQty = 300m;                      // 工單需求
            const decimal issuedQty   = 400m;                      // ← 超領 100（不良重投）
            const decimal returnedQty = 100m;                      // ← 退回 100
            const decimal laborCost = 8_000m, ohCost = 12_000m;

            const decimal netIssued   = issuedQty - returnedQty;               // 300
            const decimal netMatCost  = netIssued * openingPrice;              // 15,000
            const decimal openingVal  = openingQty * openingPrice;             // 30,000

            // ── ① 原料期初入庫 ─────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGR-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = openingQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = openingPrice,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 工單（Push 發料，用料單價與現行庫存成本一致，本測試不重測 2-6）──
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MFGR-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGR-RT", Quantity = woQty,
                    Status = WorkOrderStatus.Completed,
                    ActualLaborCost = laborCost, ActualOverheadCost = ohCost,
                    StandardMaterialCost = requiredQty * openingPrice,
                    PlantId = Plant,
                });
                db.WorkOrderOperations.Add(new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-WCR", UnitTime = 1m,
                });
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty, UomCode = Uom, UnitCost = openingPrice,
                    IssueMethod = MaterialIssueMethod.Push,
                });
                db.Dispatches.Add(new Dispatch
                {
                    DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10,
                    Quantity = woQty, QtyDone = qtyDone,
                    Status = DispatchStatus.Closed, PlantId = Plant,
                });
                await db.SaveChangesAsync();
            }

            // ── ③ 發料 400（超領 100）───────────────────────────────────────
            await SeedSlipAsync(IssueSlip, "MI-RTN-1", MaterialSlipCategory.InternalIssue,
                issuedQty, fromLoc: Loc, toLoc: null);
            await IssueAsync(IssueSlip);

            // 超領不擋（已裁決：不良重投／耗損補發屬正常製造場景）
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().SingleAsync(s => s.SlipId == IssueSlip);
                Assert.Equal(MaterialSlipStatus.Posted, slip.Status);

                var wom = await db.WorkOrderMaterials.IgnoreQueryFilters()
                    .SingleAsync(m => m.WorkOrderId == Wo);
                Assert.Equal(issuedQty, wom.IssuedQty);
                Assert.True(wom.IssuedQty > wom.RequiredQty, "超領應放行，不得被擋下");

                Assert.Equal(openingQty - issuedQty, await QtyOnHandAsync(db, Plant, Mat));
            }

            // ── ④ 退料 100 → 退料暫存儲位 ──────────────────────────────────
            await SeedSlipAsync(RtnSlip, "MX-RTN-1", MaterialSlipCategory.InternalReturn,
                returnedQty, fromLoc: Loc, toLoc: RtnLoc);
            await IssueAsync(RtnSlip);

            using (var scope = NewScope(out var db))
            {
                var wom = await db.WorkOrderMaterials.IgnoreQueryFilters()
                    .SingleAsync(m => m.WorkOrderId == Wo);
                Assert.Equal(returnedQty, wom.ReturnedQty);
                Assert.Equal(netIssued, wom.IssuedQty - wom.ReturnedQty);   // §1.3 淨發料量

                // 數量回來了
                Assert.Equal(openingQty - netIssued, await QtyOnHandAsync(db, Plant, Mat));

                // 成本也要回來：退料異動必須帶回發料當下的單位成本，
                // 否則料是回到倉庫了、錢卻永久留在工單上。
                var rtn = await db.StockMovements.IgnoreQueryFilters()
                    .SingleAsync(m => m.DocNo == RtnSlip);
                Assert.Equal(1, rtn.Direction);
                Assert.Equal(openingPrice, rtn.UnitCost);
                Assert.Equal(returnedQty * openingPrice, rtn.TotalCost);

                // InventoryService 明文的廠級不變式：「全廠同一產品的 UnitCost 應保持同步」。
                // 退料開出一列成本 0 的新儲位庫存，除了這一筆算錯，
                // 之後任何一次入庫重算移動均價都會取到任意一列當現行均價（見
                // UpdateMovingAverageCostAsync 的 plantStock.FirstOrDefault()），全廠成本自此不可預測。
                var costs = await db.Inventories.IgnoreQueryFilters()
                    .Where(i => i.PlantId == Plant && i.ProductCode == Mat)
                    .Select(i => i.UnitCost).Distinct().ToListAsync();
                Assert.Equal(new[] { openingPrice }, costs);
            }

            // ── ⑤ 完工：工單只該被扣淨發料的錢 ─────────────────────────────
            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(netMatCost, wo.ActualMaterialCost);

                var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                    .SingleAsync(c => c.WorkOrderId == Wo);
                Assert.Equal(netMatCost, cost.ActualMaterial);
                // 超領 100 個的錢落在用量差異，不會被藏起來
                Assert.Equal(netMatCost - requiredQty * openingPrice, cost.VarMaterial);
            }

            // ── ⑥ 總帳與稽核 ───────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 生產類異動不入總帳，故存貨淨額＝期初進貨＋本工單人工製費吸收
                Assert.Equal(openingVal + laborCost + ohCost,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));
                Assert.Equal(0m, gl.Values.Sum());

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
                Assert.Empty(await audit.AuditWoCostJournalDriftAsync(Plant));
            }
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task SeedSlipAsync(
            string slipId, string slipNo, MaterialSlipCategory category,
            decimal qty, string? fromLoc, string? toLoc)
        {
            using var scope = NewScope(out var db);
            db.MaterialSlips.Add(new MaterialSlip
            {
                SlipId = slipId, SlipNo = slipNo, Category = category,
                Status = MaterialSlipStatus.Confirmed, SlipDate = StockDate,
                SourceDocType = "WorkOrder", SourceDocId = Wo, PlantId = Plant,
                Lines =
                {
                    new MaterialSlipLine
                    {
                        SlipId = slipId, LineNo = 1,
                        SourceLineId = "10_1",            // "{OperationSeq}_{BomSeq}"
                        ProductCode = Mat, RequestedQty = qty, ConfirmedQty = qty,
                        UomCode = Uom,
                        FromWarehouseCode = fromLoc == null ? null : Wh,
                        FromLocationCode  = fromLoc,
                        ToWarehouseCode   = toLoc == null ? null : Wh,
                        ToLocationCode    = toLoc,
                        LotNo = "",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 走正式過帳入口（<see cref="SmartFactory.MES.Modules.MFG.Controllers.MaterialSlipController.Issue"/>），
        /// 不自己複寫一份：測試若自行呼叫過帳服務再手動回寫 IssuedQty／ReturnedQty，
        /// 驗到的是測試自己的實作。
        /// </summary>
        private async Task IssueAsync(string slipId)
            => (await Slips.IssueAsync(slipId)).AssertOk($"過帳 {slipId}");
    }
}
