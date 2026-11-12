using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §5.1 Wave 2 的 2-9：三種發料方式（Push／Pull／Backflush）的差異。
    ///
    /// 既有覆蓋只有單一方式各自成立：<see cref="MfgMaterialSlipFlowTests"/> 全 Push、
    /// <see cref="MfgCompletionFlowTests"/> 全 Backflush，而且兩者都把原料與成品放同一個儲位。
    /// 沒有任何測試問過這兩件事：
    ///
    ///   ① 一張工單同時有三種發料方式時，各走各的路徑而**成本要合得起來**
    ///   ② 後沖料是從**哪裡**扣的
    ///
    /// ② 之所以要問：後沖不經備料單（<c>StagingSourceService.FromWorkOrderAsync</c> 明文
    /// 排除 Backflush），所以它的來源儲位不是任何人填的，只能由系統決定——決定錯了，
    /// 在單倉配置下看不出來，一旦成品倉與原料倉分開就會整個垮掉。
    /// </summary>
    [Collection("Integration")]
    public class MfgIssueMethodFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGI";

        private const string MatPush = "FLOW-MFGI-MP";
        private const string MatPull = "FLOW-MFGI-ML";
        private const string MatBf   = "FLOW-MFGI-MB";
        private const string Fg      = "FLOW-MFGI-FG";
        private const string Batch   = "FLOW-MFGI-PB";
        private const string Wo      = "FLOW-MFGI-WO";
        private const string Disp    = "FLOW-MFGI-DP";
        private const string PullSlip = "FLOW-MFGI-PULL";
        private const string RawCat  = "FLOW-MFGI-CAT";

        /// <summary>成品倉：與原料倉分開，這是任何正常的倉庫規劃。</summary>
        private string FgWh  => $"{Plant}-FGWH";
        private string FgLoc => $"{Plant}-FGLOC";

        private static readonly DateOnly StockDate = new(2098, 1, 6);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public MfgIssueMethodFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync(
                (MatPush, "Push 料"), (MatPull, "Pull 料"), (MatBf, "後沖料"), (Fg, "發料方式成品"));

            using var scope = NewScope(out var db);

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

            // 基底的 Purge 以 WhCode == Wh 為界刪儲位，成品倉的儲位刪不到（倉庫本身會被刪），
            // 不自己清就會在下一輪重建時撞主鍵。
            await db.StorageLocations.IgnoreQueryFilters().Where(l => l.WhCode == FgWh).ExecuteDeleteAsync();
            db.Warehouses.Add(new Warehouse { WhCode = FgWh, WhName = "發料方式成品倉", PlantId = Plant });
            db.StorageLocations.Add(new StorageLocation
            {
                WhCode = FgWh, LocationCode = FgLoc, LocationName = "成品儲位",
            });

            // 原料的「正常存放處」＝產品類別預設倉儲（與進貨上架同一套慣例）。
            // 另開一個類別而不改共用的 FLOW-CAT：那個類別是全域的，動它會波及其他流程測試。
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == RawCat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = RawCat, CategoryName = "發料方式原料類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                    DefaultWarehouseCode = Wh, DefaultLocationCode = Loc,
                });
            await db.SaveChangesAsync();

            foreach (var code in new[] { MatPush, MatPull, MatBf })
            {
                var p = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == code);
                p.CategoryCode = RawCat;
            }
            await db.SaveChangesAsync();
        }

        // ── 共用前置 ────────────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync(decimal qty, decimal cost, params string[] products)
        {
            using var scope = NewScope(out var db);
            var posting = Svc<IInventoryPostingService>(scope);
            int line = 1;
            foreach (var code in products)
            {
                await posting.PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGI-OPEN", LineNo = line++,
                    ProductCode = code, LocationCode = Loc, LotNo = "",
                    Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task SeedWorkOrderAsync(
            decimal woQty, params (string Product, decimal Required, MaterialIssueMethod Method, int BomSeq)[] mats)
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = Batch, MrpRunId = "FLOW-MFGI-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            var wo = new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                RoutingId = "FLOW-MFGI-RT", Quantity = woQty,
                Status = WorkOrderStatus.Planned, PlantId = Plant,
                PlannedStartDate = StockDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                PlannedEndDate   = StockDate.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };
            var op = new WorkOrderOperation
            {
                WorkOrderId = Wo, OperationSeq = 10, OperationName = "OP10",
                WorkCenterId = "FLOW-WCI", UnitTime = 1m,
            };
            foreach (var (product, required, method, bomSeq) in mats)
                op.Materials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = bomSeq, ProductCode = product,
                    RequiredQty = required, UomCode = Uom, UnitCost = 0m,
                    IssueMethod = method,
                    BackflushTiming = BackflushTiming.AtCompletion,
                });
            wo.Operations.Add(op);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
        }

        /// <summary>把工單推到「已完工待入庫」，並記錄實際完工量。</summary>
        private async Task MarkCompletedAsync(decimal woQty, decimal qtyDone)
        {
            using var scope = NewScope(out var db);
            var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
            wo.Status = WorkOrderStatus.Completed;
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10,
                Quantity = woQty, QtyDone = qtyDone,
                Status = DispatchStatus.Closed, PlantId = Plant,
            });
            await db.SaveChangesAsync();
        }

        private async Task IssueSlipAsync(string slipId)
        {
            (await Slips.ApproveAsync(slipId)).AssertOk($"核准 {slipId}");
            (await Slips.MarkPickingAsync(slipId)).AssertOk($"開始揀料 {slipId}");
            (await Slips.ConfirmAsync(slipId)).AssertOk($"確認揀料 {slipId}");
            (await Slips.IssueAsync(slipId)).AssertOk($"過帳 {slipId}");
        }

        // ── 2-9 ①：三種發料方式同在一張工單 ──────────────────────────────────

        /// <summary>
        /// Push 由投產核准自動彙整成備料單；Pull 沒有任何自動入口，得由生產單位自己開單；
        /// Backflush 完工才沖，且沖的是**按完工比例**的量，不是工單全量。
        ///
        /// 三條路徑各自成立不代表帳合得起來——工單實際材料成本必須同時涵蓋三者，
        /// 少算任何一種都會讓完工品單位成本偏低，而三邊都不會有人互相揭穿。
        /// </summary>
        [Fact]
        public async Task ThreeIssueMethods_TakeSeparatePaths_AndAllLandInWorkOrderMaterialCost()
        {
            const decimal openQty = 600m, matCost = 50m;
            const decimal woQty = 100m, qtyDone = 60m;
            const decimal pushReq = 200m, pullReq = 300m, bfReq = 100m;
            const decimal bfIssued = bfReq * qtyDone / woQty;      // 60：後沖按完工比例
            const decimal materialCost = (pushReq + pullReq + bfIssued) * matCost;   // 28,000

            await SeedOpeningStockAsync(openQty, matCost, MatPush, MatPull, MatBf);
            await SeedWorkOrderAsync(woQty,
                (MatPush, pushReq, MaterialIssueMethod.Push,      1),
                (MatPull, pullReq, MaterialIssueMethod.Pull,      2),
                (MatBf,   bfReq,   MaterialIssueMethod.Backflush, 3));

            // ── ① 投產核准：自動備料單**只收 Push** ─────────────────────────
            using (var scope = NewScope(out _))
                Assert.Equal(WorkOrderReleaseOutcome.Released,
                    (await Svc<IWorkOrderReleaseService>(scope).ApplyAsync(Wo, confirmed: true)).Outcome);

            string pushSlip;
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleAsync(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == Wo);
                pushSlip = slip.SlipId;

                // Pull 混進來會讓生產單位還沒申請就先被扣料；
                // Backflush 混進來則會扣兩次（備料單一次、完工後沖一次）。
                var line = Assert.Single(slip.Lines);
                Assert.Equal(MatPush, line.ProductCode);
                Assert.Equal(pushReq, line.RequestedQty);
                Assert.Equal("10_1", line.SourceLineId);
            }

            // 自動建單填不出倉儲，使用者補完才過得了帳（見 MfgMaterialSlipFlowTests）
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleAsync(s => s.SlipId == pushSlip);
                Assert.True(await Slips.SaveAsync(new MaterialSlip
                {
                    SlipId = slip.SlipId, SlipNo = slip.SlipNo, Category = slip.Category,
                    Status = slip.Status, SlipDate = StockDate,
                    SourceDocType = slip.SourceDocType, SourceDocId = slip.SourceDocId,
                    Lines =
                    {
                        new MaterialSlipLine
                        {
                            SlipId = slip.SlipId, LineNo = 1, ProductCode = MatPush,
                            RequestedQty = pushReq, UomCode = Uom,
                            FromWarehouseCode = Wh, FromLocationCode = Loc, LotNo = "",
                        },
                    },
                }), "補儲位存檔應成功");
            }
            await IssueSlipAsync(pushSlip);

            // ── ② Pull：沒有自動入口，生產單位自己開一張 ─────────────────────
            using (var scope = NewScope(out var db))
            {
                var slip = new MaterialSlip
                {
                    SlipId = PullSlip, SlipNo = PullSlip,
                    Category = MaterialSlipCategory.InternalIssue,
                    Status = MaterialSlipStatus.Draft, SlipDate = StockDate,
                    SourceDocType = "WorkOrder", SourceDocId = Wo,
                    PlantId = Plant,
                };
                slip.Lines.Add(new MaterialSlipLine
                {
                    SlipId = PullSlip, LineNo = 1, ProductCode = MatPull,
                    RequestedQty = pullReq, UomCode = Uom,
                    FromWarehouseCode = Wh, FromLocationCode = Loc, LotNo = "",
                    SourceLineId = "10_2",
                });
                db.MaterialSlips.Add(slip);
                await db.SaveChangesAsync();
            }
            await IssueSlipAsync(PullSlip);

            // ── ③ 完工入庫：Backflush 於此才沖 ──────────────────────────────
            await MarkCompletedAsync(woQty, qtyDone);
            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, FgWh, FgLoc);

            // ── ④ 三種料各自扣了該扣的量 ────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(openQty - pushReq,  await QtyOnHandAsync(db, Plant, MatPush));
                Assert.Equal(openQty - pullReq,  await QtyOnHandAsync(db, Plant, MatPull));
                Assert.Equal(openQty - bfIssued, await QtyOnHandAsync(db, Plant, MatBf));
                Assert.Equal(qtyDone,            await QtyOnHandAsync(db, Plant, Fg));

                var mats = await db.WorkOrderMaterials.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.WorkOrderId == Wo).ToDictionaryAsync(m => m.ProductCode);
                Assert.Equal(pushReq,  mats[MatPush].IssuedQty);
                Assert.Equal(pullReq,  mats[MatPull].IssuedQty);
                Assert.Equal(bfIssued, mats[MatBf].IssuedQty);
            }

            // ── ⑤ 成本必須涵蓋三種料 ────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(materialCost, wo.ActualMaterialCost);

                var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(c => c.WorkOrderId == Wo);
                Assert.Equal(materialCost, cost.ActualMaterial);

                // 生產只是形態轉換：存貨科目淨額 = 期初進貨（三種料）不多不少，
                // 本工單沒有人工與製費，故沒有加值。
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(3m * openQty * matCost, Net(gl, StandardAccounts.RawMaterialInventoryCode));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditBackflushConsumptionAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            }
        }

        // ── 2-9 ②：後沖料的來源儲位 ─────────────────────────────────────────

        /// <summary>
        /// 後沖料必須從**原料所在的儲位**扣，不是從成品入庫的儲位扣。
        ///
        /// 兩者在單倉配置下剛好相同，所以既有測試（原料與成品同儲位）驗不到這件事。
        /// 一旦依 2-5 設定了成品倉（<c>mfg_fg_warehouse</c>／WorkCenter 設定），
        /// 成品儲位就不會有原料庫存——後沖的出庫驗證會直接判可用量不足，
        /// 而它包在完工入庫的同一個交易裡，於是**整張工單再也無法入庫**。
        /// 更糟的情況是成品倉恰好有同品號庫存：那就不會報錯，而是扣錯地方。
        /// </summary>
        [Fact]
        public async Task Backflush_DrawsFromMaterialLocation_NotFinishedGoodsLocation()
        {
            const decimal openQty = 600m, matCost = 50m;
            const decimal woQty = 100m, qtyDone = 100m, bfReq = 300m;

            await SeedOpeningStockAsync(openQty, matCost, MatBf);
            await SeedWorkOrderAsync(woQty, (MatBf, bfReq, MaterialIssueMethod.Backflush, 1));
            await MarkCompletedAsync(woQty, qtyDone);

            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, FgWh, FgLoc);

            using (var scope = NewScope(out var db))
            {
                var issue = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == Wo + "-BF");
                Assert.Equal(Loc, issue.LocationCode);       // 原料倉，不是成品倉
                Assert.Equal(bfReq, issue.StockQty);

                var putAway = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.ProductCode == Fg && m.Direction == 1);
                Assert.Equal(FgLoc, putAway.LocationCode);   // 成品入成品倉

                Assert.Equal(openQty - bfReq, await QtyOnHandAsync(db, Plant, MatBf));
                Assert.Equal(qtyDone,         await QtyOnHandAsync(db, Plant, Fg));
            }
        }

        // ── 2-9 ③：PerReport 後沖時機 ────────────────────────────────────────

        /// <summary>
        /// <c>BackflushTiming.PerReport</c>（每次報工按比例分次沖）在列舉裡存在，
        /// 但全庫沒有任何程式碼消費它：投產不擋（後沖料一律免料況閘）、報工不沖、
        /// 完工原本被時機篩掉、§3.8 稽核原本也只查 AtCompletion。四道關卡剛好接不上，
        /// 於是這種料從頭到尾沒被扣過，工單成本少算一整項，而且完全無聲。
        ///
        /// 收法是讓完工一併沖掉、稽核一併納入：<c>CalcBackflushQty</c> 取的是**最終完工量**，
        /// 所以「完工時一次沖」與「分次沖」的總量完全相同——帳一定對，差別只在期中 WIP
        /// 少了時間分布，而那遠優於根本不扣。真要做分次沖時，把完工那一段改回篩
        /// AtCompletion、另外接報工即可，這支測試會如實變紅。
        /// </summary>
        [Fact]
        public async Task PerReportBackflush_IsConsumedAtCompletion_WithSameTotalQty()
        {
            const decimal openQty = 600m, matCost = 50m;
            const decimal woQty = 100m, qtyDone = 80m, bfReq = 300m;
            const decimal expected = bfReq * qtyDone / woQty;   // 240：與 AtCompletion 同公式

            await SeedOpeningStockAsync(openQty, matCost, MatBf);
            await SeedWorkOrderAsync(woQty, (MatBf, bfReq, MaterialIssueMethod.Backflush, 1));

            using (var scope = NewScope(out var db))
            {
                var mat = await db.WorkOrderMaterials.IgnoreQueryFilters()
                    .SingleAsync(m => m.WorkOrderId == Wo);
                mat.BackflushTiming = BackflushTiming.PerReport;
                await db.SaveChangesAsync();
            }

            await MarkCompletedAsync(woQty, qtyDone);
            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, FgWh, FgLoc);

            using (var scope = NewScope(out var db))
            {
                var mv = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == Wo + "-BF");
                Assert.Equal(expected, mv.StockQty);
                Assert.Equal(Loc, mv.LocationCode);

                Assert.Equal(openQty - expected, await QtyOnHandAsync(db, Plant, MatBf));

                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(expected * matCost, wo.ActualMaterialCost);

                // 稽核以「結案時的累計應沖量」為準，與後沖時機無關，故必須乾淨
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditBackflushConsumptionAsync(Plant));
            }
        }
    }
}
