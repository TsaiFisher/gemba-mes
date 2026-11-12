using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 Wave 2 的 2-1／2-2／2-3：備料單從「工單核准自動產生」到「過帳回寫工單」
    /// 的整條狀態機。
    ///
    /// 與 <see cref="MfgMaterialReturnFlowTests"/> 的分工：那一支為了聚焦成本，直接把備料單
    /// 種在 <c>Confirmed</c>，等於跳過了 Draft→Approved→Picking→Confirmed 這四段，也跳過了
    /// 「單子是誰建的、明細從哪來」。本測試補的正是被跳過的前半段，因此**一律走真正的入口**：
    /// 自動建單走 <see cref="IWorkOrderReleaseService"/>，其餘每一步走 MaterialSlipController。
    ///
    /// 情境刻意包含「使用者編輯自動建立的備料單」這一步，因為那不是可選動作——
    /// 自動建立的明細只有品號與數量，**沒有發料倉庫／儲位**（見 WorkOrderReleaseService
    /// 的 CreatePushMaterialSlips），而過帳時出庫儲位取的就是 FromLocationCode，
    /// 空儲位的可用量恆為 0，會被出庫驗證直接擋下。也就是說：這張單非編輯不可，
    /// 「編輯」是主線流程的一部分，不是邊角情境。
    /// </summary>
    [Collection("Integration")]
    public class MfgMaterialSlipFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGS";

        private const string Mat   = "FLOW-MFGS-MAT";
        private const string Fg    = "FLOW-MFGS-FG";
        private const string Batch = "FLOW-MFGS-PB";
        private const string Wo    = "FLOW-MFGS-WO";

        private static readonly DateOnly StockDate = new(2097, 9, 1);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public MfgMaterialSlipFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "備料單流程原料"), (Fg, "備料單流程成品"));
            using var scope = NewScope(out var db);

            // 備料單的單號由 DocNumberService 產生，跑過就留在庫裡；以來源工單為界清乾淨，
            // 否則第二次執行會在同一張工單下看到兩張單，2-1 的「自動建立一張」斷言意義盡失。
            var slipIds = await db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == Wo)
                .Select(s => s.SlipId).ToListAsync();
            if (slipIds.Count > 0)
            {
                await db.MaterialSlipLines.IgnoreQueryFilters()
                    .Where(l => slipIds.Contains(l.SlipId)).ExecuteDeleteAsync();
                await db.MaterialSlips.IgnoreQueryFilters()
                    .Where(s => slipIds.Contains(s.SlipId)).ExecuteDeleteAsync();
            }

            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
        }

        [Fact]
        public async Task WoApproval_CreatesDraftSlip_GatesEachTransition_AndBackfillsIssuedQty()
        {
            const decimal openingQty = 600m, openingPrice = 50m;
            const decimal woQty = 100m, requiredQty = 300m;

            // ── ① 期初庫存 ──────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGS-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = openingQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = openingPrice,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 已規劃工單，用料為 Push ────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MFGS-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                var wo = new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGS-RT", Quantity = woQty,
                    // GrossDemandQty 留 0：投產重算（net-at-release）不介入，
                    // 本測試要驗的是備料單，不是重算量。
                    Status = WorkOrderStatus.Planned, PlantId = Plant,
                    PlannedStartDate = StockDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    PlannedEndDate   = StockDate.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                };
                var op = new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, OperationName = "OP10", WorkCenterId = "FLOW-WCS",
                    UnitTime = 1m,
                };
                op.Materials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty, UomCode = Uom, UnitCost = openingPrice,
                    IssueMethod = MaterialIssueMethod.Push,
                });
                wo.Operations.Add(op);
                db.WorkOrders.Add(wo);
                await db.SaveChangesAsync();
            }

            // ── ③【2-1】投產核准 → 自動建立 Draft 備料單 ─────────────────────
            using (var scope = NewScope(out _))
            {
                var outcome = await Svc<IWorkOrderReleaseService>(scope).ApplyAsync(Wo, confirmed: true);
                Assert.Equal(WorkOrderReleaseOutcome.Released, outcome.Outcome);
            }

            string slipId;
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleAsync(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == Wo);
                slipId = slip.SlipId;

                Assert.Equal(MaterialSlipCategory.InternalIssue, slip.Category);
                Assert.Equal(MaterialSlipStatus.Draft, slip.Status);
                Assert.Equal(Plant, slip.PlantId);

                var line = Assert.Single(slip.Lines);
                Assert.Equal(Mat, line.ProductCode);
                Assert.Equal(requiredQty, line.RequestedQty);   // 數量來自 BOM 展開後的用料
                Assert.Equal("10_1", line.SourceLineId);        // "{OperationSeq}_{BomSeq}"

                // 自動建單填不出倉庫／儲位——這正是下一步「非編輯不可」的原因。
                Assert.Null(line.FromLocationCode);
            }

            // ── ④ 使用者補上發料儲位（走 Save，等同前端送出 _Form）───────────
            //
            // 送出的欄位刻意只有 _Form.cshtml 真的會 render 的那些（品號、數量、單位、
            // 來源/目標倉儲、批號，以及 ConfirmedQty/ReturnedQty/ApprovedQty 三個 hidden）。
            // SourceLineId 沒有任何輸入框，瀏覽器不會送——而 Save 的編輯分支是
            // 「整批刪掉舊明細、以送進來的明細重建」，所以這個欄位能不能活過一次編輯，
            // 只能由伺服器端決定。
            //
            // 它也**不應該**由客戶端攜帶：SourceLineId 決定過帳後把 IssuedQty 記到哪一筆
            // WorkOrderMaterial 上，讓瀏覽器往返這個值等於讓使用者指定回寫對象。
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleAsync(s => s.SlipId == slipId);
                var line = slip.Lines.Single();

                var posted = new MaterialSlip
                {
                    SlipId = slip.SlipId, SlipNo = slip.SlipNo, Category = slip.Category,
                    Status = slip.Status, SlipDate = slip.SlipDate,
                    SourceDocType = slip.SourceDocType, SourceDocId = slip.SourceDocId,
                    Remarks = slip.Remarks,
                    Lines =
                    {
                        new MaterialSlipLine
                        {
                            SlipId = slip.SlipId, LineNo = 1,
                            ProductCode = line.ProductCode,
                            RequestedQty = line.RequestedQty,
                            UomCode = line.UomCode,
                            FromWarehouseCode = Wh, FromLocationCode = Loc,
                            LotNo = "",
                        },
                    },
                };
                Assert.True(await Slips.SaveAsync(posted), "補儲位存檔應成功");
            }

            using (var scope = NewScope(out var db))
            {
                var line = await db.MaterialSlipLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.SlipId == slipId);
                Assert.Equal(Loc, line.FromLocationCode);

                // 編輯不得切斷明細與工單用料的對應。斷掉的話後果是靜默的：
                // 庫存照扣、成本照算（ProductionCompletionService 讀的是庫存異動），
                // 只有 WorkOrderMaterial.IssuedQty 停在 0 —— 於是
                //   ‧ 對帳規格 §1.3 的 WIP＝Σ(IssuedQty−ReturnedQty)×成本 短計整筆料
                //   ‧ 用量差異把「已領 300」算成「完全沒領」
                // 而成本與用量兩邊都沒有人會互相揭穿。
                Assert.Equal("10_1", line.SourceLineId);
            }

            // ── ⑤【2-2】逐段守門 ────────────────────────────────────────────
            (await Slips.SubmitAsync(slipId)).AssertRejected("廠內發料單送審（只有領料申請單走送審）");
            (await Slips.ConfirmAsync(slipId)).AssertRejected("Draft 直接確認揀料");
            (await Slips.IssueAsync(slipId)).AssertRejected("Draft 直接過帳");

            (await Slips.ApproveAsync(slipId)).AssertOk("核准");
            (await Slips.ApproveAsync(slipId)).AssertRejected("重複核准");

            (await Slips.MarkPickingAsync(slipId)).AssertOk("開始揀料");
            (await Slips.IssueAsync(slipId)).AssertRejected("Picking 直接過帳");

            (await Slips.ConfirmAsync(slipId)).AssertOk("確認揀料完成");
            // 已確認後取消自 2026-10-20 起允許（A6），由 MaterialSlipPostingResidueTests 驗；本流程續走過帳

            (await Slips.IssueAsync(slipId)).AssertOk("過帳發料");
            (await Slips.IssueAsync(slipId)).AssertRejected("重複過帳");

            // ── ⑥【2-3】IssuedQty、庫存異動、在手量三者一致 ──────────────────
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().SingleAsync(s => s.SlipId == slipId);
                Assert.Equal(MaterialSlipStatus.Posted, slip.Status);

                var mv = await db.StockMovements.IgnoreQueryFilters()
                    .SingleAsync(m => m.DocNo == slipId);
                Assert.Equal(-1, mv.Direction);
                Assert.Equal(requiredQty, mv.StockQty);
                Assert.Equal(Loc, mv.LocationCode);
                Assert.Equal(openingPrice, mv.UnitCost);

                var wom = await db.WorkOrderMaterials.IgnoreQueryFilters()
                    .SingleAsync(m => m.WorkOrderId == Wo && m.OperationSeq == 10 && m.BomSeq == 1);
                Assert.Equal(requiredQty, wom.IssuedQty);
                Assert.Equal(0m, wom.ReturnedQty);

                Assert.Equal(openingQty - requiredQty, await QtyOnHandAsync(db, Plant, Mat));
            }
        }
    }
}
