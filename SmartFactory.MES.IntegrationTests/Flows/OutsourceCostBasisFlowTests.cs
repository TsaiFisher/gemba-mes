using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 Wave 2 的 2-10（委外全鏈）之**成本口徑**這一段。
    ///
    /// 與 <see cref="OutsourceFlowTests"/> 的分工：那一支驗的是數量與總帳科目的收斂
    /// （在外原料平衡、2152 沖銷、2100 歸零），它對成品入庫單價一律用加工費，
    /// 因此「成品到底值多少」這件事在那裡沒有被檢驗。本測試只問一件事：
    ///
    ///   **委外收回來的成品，帳面價值有沒有包含我們自己投進去的料。**
    ///
    /// mfg-domain.md §19.5：`委外工單成本 = MaterialCost + OutsourcingCost`；
    /// §19.3 Stage 7 亦寫明 `StandardCost = 採購成本 + 委外加工費`。
    ///
    /// 判準用的是與 <see cref="MfgCostBasisFlowTests"/> 同一條：**加工是形態轉換，
    /// 不得憑空增減存貨價值**。委外發料屬 Outsource 類，庫存數量出、總帳不動
    /// （<c>BuildJournalLines</c> 回空），也就是說料的價值一直留在存貨科目裡；
    /// 收貨時若成品只認加工費，這筆料錢就再也沒有落點——
    /// 庫存明細帳的合計會永久少於總帳存貨科目，差額正好是被加工掉的在外原料，
    /// 而成品帶著低估的單位成本出貨，COGS 跟著低估、毛利虛增。
    ///
    /// 過帳走服務層而非 OutsourceReceiptController：該控制器的 Confirm 是把
    /// <c>UnitPrice = l.UnitCost</c>（明細的加工費單價）原樣送進過帳服務，
    /// 本測試重現的就是這個口徑，控制器的其餘職責（罰扣、在途轉換、開 AP）
    /// 由 PUR 既有測試覆蓋。
    /// </summary>
    [Collection("Integration")]
    public class OutsourceCostBasisFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZOSC";

        private const string Mat   = "FLOW-OSC-MAT";
        private const string Fg    = "FLOW-OSC-FG";
        private const string Batch = "FLOW-OSC-PB";
        private const string Routing = "FLOW-OSC-RT";
        private const string Wo    = "FLOW-OSC-WO";
        private const string Opo   = "FLOW-OSC-OPO";
        private const string OoDoc = "FLOW-OSC-OO";
        private const string Rcpt  = "FLOW-OSC-OR";

        private static readonly DateOnly StockDate = new(2097, 11, 1);
        private static readonly DateOnly IssueDate = new(2097, 11, 5);
        private static readonly DateOnly RecvDate  = new(2097, 11, 20);

        public OutsourceCostBasisFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "委外成本原料"), (Fg, "委外成本成品"));
            using var scope = NewScope(out var db);
            await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => r.PlantId == Plant).ExecuteDeleteAsync();
            await db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.OutsourcePurchaseOrderId == Opo).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
            await db.RoutingBoms.IgnoreQueryFilters().Where(b => b.RoutingId == Routing).ExecuteDeleteAsync();
            await db.RoutingOperations.IgnoreQueryFilters().Where(o => o.RoutingId == Routing).ExecuteDeleteAsync();
            await db.Routings.IgnoreQueryFilters().Where(r => r.RoutingId == Routing).ExecuteDeleteAsync();
        }

        [Fact]
        public async Task OutsourceReceipt_FinishedGoodCarriesMaterialCost_NotJustProcessingFee()
        {
            const decimal matQty = 200m, matCost = 30m;
            const decimal issueQty = 100m, recvQty = 100m;   // BOM 1:1
            const decimal fee = 25m;

            const decimal openVal = matQty * matCost;                    // 6,000
            const decimal consumedMatVal = recvQty * matCost;            // 3,000（被加工掉的在外原料）
            const decimal expectedFgUnitCost = matCost + fee;            // 55
            const decimal remainingMatVal = (matQty - issueQty) * matCost;  // 3,000

            // ── ① 原料入庫 ────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-OSC-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = matQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = matCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 委外工單與委外採購單 ───────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                // 途程 BOM：1 個成品耗 1 個原料。委外的「理論消耗」就是靠這個換算
                // （與 §3.3 在外原料平衡稽核同一口徑）。
                db.Routings.Add(new Routing { RoutingId = Routing, ProductCode = Fg, PlantId = Plant });
                db.RoutingOperations.Add(new RoutingOperation
                {
                    RoutingId = Routing, OperationSeq = 10, WorkCenterId = "FLOW-OSC-WC",
                    TM_UNIT = 1m, UomCode = Uom,
                });
                db.RoutingBoms.Add(new RoutingBom
                {
                    RoutingId = Routing, OperationSeq = 10, BomSeq = 1,
                    ProductCode = Mat, Quantity = 1m, UomCode = Uom,
                });
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-OSC-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = recvQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = Routing, Quantity = recvQty,
                    Status = WorkOrderStatus.Released, PlantId = Plant,
                    IsOutsource = true, OutsourceSupplierCode = Supplier,
                });
                db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
                {
                    OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier,
                    OrderDate = IssueDate, Status = OutsourcePoStatus.Confirmed,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
                {
                    OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = Wo,
                    OperationSeq = 10, ProductCode = Fg, Qty = recvQty, UomCode = Uom,
                });
                await db.SaveChangesAsync();
            }

            // ── ③ 委外發料：料出廠，所有權未移轉，總帳不動 ────────────────────
            using (var scope = NewScope(out var db))
            {
                var oo = new OutsourceMaterialDoc
                {
                    OutsourceMaterialDocId = OoDoc, IssueDate = IssueDate,
                    TypeCode = "OO", TypeCategory = "Outsource", SupplierCode = Supplier,
                    WorkOrderCode = Wo, PlantId = Plant, CreatedByRoleId = RoleId,
                };
                db.OutsourceMaterialDocs.Add(oo);
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = OoDoc, MovementDate = IssueDate,
                    TypeCode = "OO", TypeCategory = "Outsource", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Mat, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = issueQty, UomCode = Uom, SetQtyPutAway = _ => { },
                        },
                    },
                    SetDocPosted = () => oo.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                // 發出去的料帶著帳面成本走：這筆錢之後必須落在成品上
                var mv = await db.StockMovements.IgnoreQueryFilters().SingleAsync(m => m.DocNo == OoDoc);
                Assert.Equal(matCost, mv.UnitCost);
                Assert.Equal(consumedMatVal, mv.TotalCost);
            }

            // ── ④ 委外收貨：成品入庫 ─────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var rec = new OutsourceReceipt
                {
                    OutsourceReceiptId = Rcpt, OutsourcePurchaseOrderId = Opo,
                    SupplierCode = Supplier, ReceiptDate = RecvDate, CreatedBy = UserId,
                    ExchangeRate = 1m, TypeCode = "OI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                rec.Lines.Add(new OutsourceReceiptLine
                {
                    OutsourceReceiptId = Rcpt, LineNo = 1, OpoLineNo = 1, ProductCode = Fg,
                    ReceiptQty = recvQty, StockQty = recvQty, UomCode = Uom, StockUomCode = Uom,
                    UnitCost = fee, WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceReceipts.Add(rec);
                await db.SaveChangesAsync();
            }

            // 走真正的收貨確認入口（OutsourceReceiptController.Confirm）而非自己組過帳請求：
            // 成品入庫價值由誰算、有沒有把在外原料併進來，正是這一段的職責。
            // 簡化模式是該入口直接過帳庫存的前提；它是全域參數，故用完必須清回未設定。
            using (var scope = NewScope(out var db))
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, "true");
            try
            {
                using var scope = NewScope(out var db);
                var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
                var ctrl = new OutsourceReceiptController(
                    db,
                    sp.GetRequiredService<IUserContext>(),
                    sp.GetRequiredService<IPermissionService>(),
                    sp.GetRequiredService<IDateTimeService>(),
                    sp.GetRequiredService<IApprovalService>(),
                    sp.GetRequiredService<IDocNumberService>(),
                    sp.GetRequiredService<IInventoryPostingService>(),
                    sp.GetRequiredService<IPlantParamService>(),
                    sp.GetRequiredService<IUomService>(),
                    sp.GetRequiredService<ICurrencyService>(),
                    sp.GetRequiredService<IAPInvoiceService>(),
                    sp.GetRequiredService<IExportService>(),
                    sp.GetRequiredService<IInventoryReservationService>(),
                    sp.GetRequiredService<IOutsourceReceiptSettlementService>(),
                    sp.GetRequiredService<IJournalEntryService>(),
                    sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext { RequestServices = sp },
                    },
                };

                var json = Assert.IsType<JsonResult>(await ctrl.Confirm(Rcpt));
                var ok = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
                var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value);
                Assert.True(Equals(ok, true), $"委外收貨確認應成功：{msg}");
            }
            finally
            {
                using var scope = NewScope(out _);
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, null);
            }

            // ── ⑤ 成品成本必須含料（§19.5）──────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var fgInv = await db.Inventories.IgnoreQueryFilters()
                    .SingleAsync(i => i.PlantId == Plant && i.ProductCode == Fg);
                Assert.Equal(expectedFgUnitCost, fgInv.UnitCost);
            }

            // ── ⑥ 形態轉換不得憑空增減存貨價值 ────────────────────────────────
            //     庫存明細帳合計必須等於總帳存貨科目淨額。委外發料不入總帳，
            //     故被加工掉的在外原料只能靠成品單價把價值接回來；接不回來，
            //     兩邊的差額就永久等於這筆料錢，而且沒有任何一張傳票會不平衡。
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                var glInventory = Net(gl, StandardAccounts.RawMaterialInventoryCode);

                Assert.Equal(openVal + recvQty * fee, glInventory);   // 期初進貨 + 加工費

                var ledger = (await db.Inventories.IgnoreQueryFilters()
                        .Where(i => i.PlantId == Plant)
                        .Select(i => new { i.QtyOnHand, i.UnitCost }).ToListAsync())
                    .Sum(i => i.QtyOnHand * i.UnitCost);

                Assert.Equal(remainingMatVal + recvQty * expectedFgUnitCost, ledger);
                Assert.Equal(glInventory, ledger);

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditOutsourceBalanceAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }
    }
}
