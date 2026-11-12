using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 手機料件作業（<see cref="MobileMaterialController"/>）走真庫存、真過帳：
    /// 倉庫揀料過帳、現場領料申請與退料、委外收料。
    ///
    /// <para>重點不是畫面，而是三件「錯了不會有任何訊號」的事：
    /// ① 揀料過帳要真的扣庫存、累計工單已發量，而且離線重送（同一 clientId）不能扣兩次；
    /// ② 未核准的領料單不能被手機揀走；退料不能超過已發未退；
    /// ③ 委外收料的單價取自委外採購單（手機不輸入金額），連按兩次不會建出兩張收貨單。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileMaterialFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMOBM";

        private const string Mat   = "FLOW-MOBM-MAT";
        private const string Fg    = "FLOW-MOBM-FG";
        private const string Batch = "FLOW-MOBM-PB";
        private const string Wo    = "FLOW-MOBM-WO";
        private const string Disp  = "FLOW-MOBM-DP";
        private const string Mi    = "FLOW-MOBM-MI";
        private const string Opo   = "FLOW-MOBM-OPO";
        private const string OsWo  = "FLOW-MOBM-OSWO";   // 整品委外工單（2026-10-12 委外完工量回寫）
        private const decimal Fee  = 5m;

        private static readonly DateOnly StockDate = new(2097, 9, 1);

        public MobileMaterialFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "手機料件原料"), (Fg, "手機料件成品"));
            using var scope = NewScope(out var db);
            var slipIds = await db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.PlantId == Plant || s.SourceDocId == Wo).Select(s => s.SlipId).ToListAsync();
            await db.MaterialSlipLines.IgnoreQueryFilters().Where(l => slipIds.Contains(l.SlipId)).ExecuteDeleteAsync();
            await db.MaterialSlips.IgnoreQueryFilters().Where(s => slipIds.Contains(s.SlipId)).ExecuteDeleteAsync();
            var recIds = await db.OutsourceReceipts.IgnoreQueryFilters()
                .Where(r => r.OutsourcePurchaseOrderId == Opo).Select(r => r.OutsourceReceiptId).ToListAsync();
            await db.APInvoices.IgnoreQueryFilters().Where(i => i.SourceDocType == "OutsourceReceipt" && recIds.Contains(i.SourceDocId!)).ExecuteDeleteAsync();
            var jIds = await db.JournalEntries.IgnoreQueryFilters().Where(j => recIds.Contains(j.SourceDocId!)).Select(j => j.JournalId).ToListAsync();
            await db.JournalLines.Where(l => jIds.Contains(l.JournalId)).ExecuteDeleteAsync();
            await db.JournalEntries.IgnoreQueryFilters().Where(j => jIds.Contains(j.JournalId)).ExecuteDeleteAsync();
            await db.OutsourceReceiptLines.IgnoreQueryFilters().Where(l => recIds.Contains(l.OutsourceReceiptId)).ExecuteDeleteAsync();
            await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => recIds.Contains(r.OutsourceReceiptId)).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrderLines.IgnoreQueryFilters().Where(l => l.OutsourcePurchaseOrderId == Opo).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.OutsourcePurchaseOrderId == Opo).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == OsWo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == OsWo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == OsWo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == OsWo).ExecuteDeleteAsync();
            await db.MobileIqcRecords.Where(r => recIds.Contains(r.GoodsReceiptId)).ExecuteDeleteAsync();
            await db.InspectionOrders.IgnoreQueryFilters().Where(o => recIds.Contains(o.SourceDocId!)).ExecuteDeleteAsync();
            var stgIds = await db.MaterialStagingOrders.IgnoreQueryFilters().Where(o => o.StagingId.StartsWith("FLOW-MOBM-STG")).Select(o => o.StagingId).ToListAsync();
            await db.MaterialStagingLines.IgnoreQueryFilters().Where(l => stgIds.Contains(l.StagingId)).ExecuteDeleteAsync();
            await db.MaterialStagingOrders.IgnoreQueryFilters().Where(o => stgIds.Contains(o.StagingId)).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }

        // ── ① 倉庫揀料過帳 ─────────────────────────────────────────────────────

        [Fact]
        public async Task 揀料過帳_扣庫存並累計工單已發量_同一clientId重送不重扣()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await SeedIssueSlipAsync(Mi, 30m);

            // 明細帶出有庫存的儲位
            var detail = Data(await Run(c => c.GetSlip(Mi)));
            Assert.Contains(Loc, System.Text.Json.JsonSerializer.Serialize(detail));

            var body = new MobileMaterialController.SlipPostRequest(
                new() { new SlipPickLine(1, null, Loc, "", 30m) }, ClientId: "MOBM-C-" + Guid.NewGuid().ToString("N"));
            Assert.IsType<OkObjectResult>(await Run(c => c.PostSlip(Mi, body)));
            Assert.IsType<OkObjectResult>(await Run(c => c.PostSlip(Mi, body)));   // 離線佇列重送

            using var scope = NewScope(out var db);
            Assert.Equal(MaterialSlipStatus.Posted, (await db.MaterialSlips.IgnoreQueryFilters().FirstAsync(s => s.SlipId == Mi)).Status);
            Assert.Equal(70m, await QtyOnHandAsync(db, Plant, Mat));
            Assert.Equal(30m, (await db.WorkOrderMaterials.IgnoreQueryFilters().FirstAsync(m => m.WorkOrderId == Wo)).IssuedQty);
        }

        [Fact]
        public async Task 揀料只回報儲位_伺服器補倉庫_缺儲位則擋下且不過帳()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await SeedIssueSlipAsync(Mi, 30m);

            var noLoc = new MobileMaterialController.SlipPostRequest(new() { new SlipPickLine(1, null, null, null, 30m) });
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.PostSlip(Mi, noLoc)));

            using (var scope = NewScope(out var db))
            {
                Assert.NotEqual(MaterialSlipStatus.Posted, (await db.MaterialSlips.IgnoreQueryFilters().FirstAsync(s => s.SlipId == Mi)).Status);
                Assert.Equal(100m, await QtyOnHandAsync(db, Plant, Mat));
            }

            var ok = new MobileMaterialController.SlipPostRequest(new() { new SlipPickLine(1, null, Loc, "", 30m) });
            Assert.IsType<OkObjectResult>(await Run(c => c.PostSlip(Mi, ok)));
            using (var scope = NewScope(out var db))
                Assert.Equal(Wh, (await db.MaterialSlipLines.IgnoreQueryFilters().FirstAsync(l => l.SlipId == Mi)).FromWarehouseCode);
        }

        // ── ② 現場領料申請與退料 ────────────────────────────────────────────────

        [Fact]
        public async Task 領料申請_建成待核准_手機揀料清單看不到也揀不了()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);

            var res = await Run(c => c.Requisition(Disp, new MobileMaterialController.SlipRequestBody(
                new() { new SlipRequestLine(Mat, 12m) })));
            Assert.IsType<OkObjectResult>(res);

            string mrId;
            using (var scope = NewScope(out var db))
            {
                var mr = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleAsync(s => s.SourceDocId == Wo && s.Category == MaterialSlipCategory.InternalRequisition);
                Assert.Equal(MaterialSlipStatus.Pending, mr.Status);
                Assert.Equal("10_1", Assert.Single(mr.Lines).SourceLineId);   // 對應到工單用料，過帳才回寫得到
                mrId = mr.SlipId;
            }

            var list = System.Text.Json.JsonSerializer.Serialize(Data(await Run(c => c.GetSlips("issue"))));
            Assert.DoesNotContain(mrId, list);
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.PostSlip(mrId,
                new MobileMaterialController.SlipPostRequest(new() { new SlipPickLine(1, null, Loc, "", 12m) }))));
        }

        [Fact]
        public async Task 退料_不得超過已發未退_收進儲位過帳後累計已退量並回補庫存()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await SeedIssueSlipAsync(Mi, 30m);
            Assert.IsType<OkObjectResult>(await Run(c => c.PostSlip(Mi,
                new MobileMaterialController.SlipPostRequest(new() { new SlipPickLine(1, null, Loc, "", 30m) }))));

            Assert.IsType<BadRequestObjectResult>(await Run(c => c.MaterialReturn(Disp,
                new MobileMaterialController.SlipRequestBody(new() { new SlipRequestLine(Mat, 31m) }))));
            Assert.IsType<OkObjectResult>(await Run(c => c.MaterialReturn(Disp,
                new MobileMaterialController.SlipRequestBody(new() { new SlipRequestLine(Mat, 10m) }))));

            string mxId;
            using (var scope = NewScope(out var db))
                mxId = (await db.MaterialSlips.IgnoreQueryFilters()
                    .SingleAsync(s => s.SourceDocId == Wo && s.Category == MaterialSlipCategory.InternalReturn)).SlipId;

            var returns = System.Text.Json.JsonSerializer.Serialize(Data(await Run(c => c.GetSlips("return"))));
            Assert.Contains(mxId, returns);
            Assert.IsType<OkObjectResult>(await Run(c => c.PostSlip(mxId,
                new MobileMaterialController.SlipPostRequest(new() { new SlipPickLine(1, null, Loc, "", 10m) }))));

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(80m, await QtyOnHandAsync(db, Plant, Mat));
                var wom = await db.WorkOrderMaterials.IgnoreQueryFilters().FirstAsync(m => m.WorkOrderId == Wo);
                Assert.Equal(30m, wom.IssuedQty);
                Assert.Equal(10m, wom.ReturnedQty);
            }
        }

        // 2026-10-10 Android 實測：料已全數發完仍可一再申請領料；委外發料送到廠商的料被廠內退料退進倉庫（憑空多出庫存）。

        private MobileMaterialController.SlipRequestBody Body(decimal qty) => new(new() { new SlipRequestLine(Mat, qty) });

        private async Task TweakAsync(Action<ApplicationDbContext> act)
        {
            using var scope = NewScope(out var db);
            act(db);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task 領料申請_超過需求減已發減已申請未發就擋()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);

            Assert.IsType<OkObjectResult>(await Run(c => c.Requisition(Disp, Body(20m))));
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.Requisition(Disp, Body(11m))));   // 30 − 0 − 20 = 10
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.Requisition(Disp, Body(10.000001m))));   // 剩餘量＋最小單位
            Assert.IsType<OkObjectResult>(await Run(c => c.Requisition(Disp, Body(10m))));
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.Requisition(Disp, Body(1m))));    // 已申請滿

            var json = System.Text.Json.JsonSerializer.Serialize(Data(await Run(c => c.GetDispatchMaterials(Disp))));
            Assert.Matches("\"RequestableQty\":0(\\.0+)?[,}]", json);
        }

        [Fact]
        public async Task 領料申請_已發完的料不能再申請()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await TweakAsync(db => db.WorkOrderMaterials.IgnoreQueryFilters().First(m => m.WorkOrderId == Wo).IssuedQty = 30m);

            Assert.IsType<BadRequestObjectResult>(await Run(c => c.Requisition(Disp, Body(1m))));
        }

        [Fact]
        public async Task 領料申請_發料用料不能申請()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await TweakAsync(db => db.WorkOrderMaterials.IgnoreQueryFilters().First(m => m.WorkOrderId == Wo).IssueMethod = MaterialIssueMethod.Push);

            Assert.IsType<BadRequestObjectResult>(await Run(c => c.Requisition(Disp, Body(5m))));
        }

        [Fact]
        public async Task 委外派工_不能申請領料也不能退料_用料清單標示委外()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await TweakAsync(db =>
            {
                db.Dispatches.IgnoreQueryFilters().First(d => d.DispatchId == Disp).IsOutsource = true;
                db.WorkOrderMaterials.IgnoreQueryFilters().First(m => m.WorkOrderId == Wo).IssuedQty = 30m;
            });

            Assert.IsType<BadRequestObjectResult>(await Run(c => c.Requisition(Disp, Body(5m))));
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.MaterialReturn(Disp, Body(5m))));
            var json = System.Text.Json.JsonSerializer.Serialize(Data(await Run(c => c.GetDispatchMaterials(Disp))));
            Assert.Contains("\"IsOutsource\":true", json);

            using var scope = NewScope(out var db);
            Assert.False(await db.MaterialSlips.IgnoreQueryFilters().AnyAsync(s => s.SourceDocId == Wo));
        }

        [Fact]
        public async Task 退料_委外工序已發的料不算廠內可退()
        {
            // 同一張工單：工序 10 委外（料經委外發料送到廠商），從廠內派工退料不可把它退進倉庫
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await TweakAsync(db =>
            {
                db.WorkOrderOperations.IgnoreQueryFilters().First(o => o.WorkOrderId == Wo).IsOutsource = true;
                db.WorkOrderMaterials.IgnoreQueryFilters().First(m => m.WorkOrderId == Wo).IssuedQty = 30m;
            });

            Assert.IsType<BadRequestObjectResult>(await Run(c => c.MaterialReturn(Disp, Body(9m))));
        }

        [Fact]
        public async Task 退料_未過帳的退料單佔額度()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await TweakAsync(db => db.WorkOrderMaterials.IgnoreQueryFilters().First(m => m.WorkOrderId == Wo).IssuedQty = 30m);

            Assert.IsType<OkObjectResult>(await Run(c => c.MaterialReturn(Disp, Body(20m))));
            Assert.IsType<BadRequestObjectResult>(await Run(c => c.MaterialReturn(Disp, Body(11m))));  // 30 − 20 = 10
            Assert.IsType<OkObjectResult>(await Run(c => c.MaterialReturn(Disp, Body(10m))));
        }

        [Fact]
        public async Task 桌機共用守門_編輯既有單時排除本單自己()
        {
            // 桌機 MaterialSlipController.Save 走同一個 ValidateWorkOrderSlipQtyAsync；
            // 編輯時若不排除本單，它的原數量會被當成「已申請未過帳」，改成剛好滿額也存不了。
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            Assert.IsType<OkObjectResult>(await Run(c => c.Requisition(Disp, Body(20m))));

            using var scope = NewScope(out var db);
            var mrId = (await db.MaterialSlips.IgnoreQueryFilters()
                .SingleAsync(s => s.SourceDocId == Wo && s.Category == MaterialSlipCategory.InternalRequisition)).SlipId;
            var sp  = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            var svc = new MaterialSlipService(db, sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryReservationService>(), sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>());
            var full = new[] { new SlipRequestLine(Mat, 30m) };

            var msg = await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalRequisition, Wo, full);
            Assert.NotNull(msg);
            Assert.DoesNotContain("10.000000", msg);   // 額度數字不帶 DB 的 6 位小數尺度
            Assert.Null(await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalRequisition, Wo, full, mrId));
            Assert.NotNull(await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalRequisition, Wo,
                new[] { new SlipRequestLine(Mat, 31m) }, mrId));
            // 發料類不在這道守門的範圍
            Assert.Null(await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalIssue, Wo, full));
        }

        [Fact]
        public async Task 桌機共用守門_來源單號對不上工單或料不在工單內_要講明而不是顯示可退0()
        {
            // 2026-10-11 實測：桌機來源單號自由輸入，對不上工單時畫面說「已發未退的 0」，看起來像帳錯
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await TweakAsync(db => db.WorkOrderMaterials.IgnoreQueryFilters().First(m => m.WorkOrderId == Wo).IssuedQty = 30m);

            using var scope = NewScope(out var db);
            var sp  = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            var svc = new MaterialSlipService(db, sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryReservationService>(), sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>());
            var loc = sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>();

            var noWo = await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalReturn, Wo + "X",
                new[] { new SlipRequestLine(Mat, 1m) });
            Assert.Equal(string.Format(loc["SlipSourceWorkOrderNotFound"].Value, Wo + "X"), noWo);

            var noMat = await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalReturn, Wo,
                new[] { new SlipRequestLine("NO-SUCH-MAT", 1m) });
            Assert.Equal(string.Format(loc["SlipProductNotInWorkOrder"].Value, "NO-SUCH-MAT", Wo), noMat);

            Assert.Null(await svc.ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory.InternalReturn, Wo,
                new[] { new SlipRequestLine(Mat, 30m) }));
        }

        [Fact]
        public async Task 桌機來源單號下拉_只列還會發退料的工單_退料類保留已完工()
        {
            // 2026-10-12：來源單號改成下拉。已入庫結案、作廢、未釋出的工單不該出現；完工後退回超領是常態，退料類要留著
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            var drv = new MaterialSlipDriver(Factory, Plant, RoleId);

            Assert.Contains(Wo, await drv.SourceDocIdsAsync("WorkOrder", "InternalRequisition"));

            await TweakAsync(db => db.WorkOrders.IgnoreQueryFilters().First(w => w.WorkOrderId == Wo).Status = WorkOrderStatus.Completed);
            Assert.DoesNotContain(Wo, await drv.SourceDocIdsAsync("WorkOrder", "InternalRequisition"));
            Assert.Contains(Wo, await drv.SourceDocIdsAsync("WorkOrder", "InternalReturn"));

            await TweakAsync(db => db.WorkOrders.IgnoreQueryFilters().First(w => w.WorkOrderId == Wo).Status = WorkOrderStatus.Closed);
            Assert.DoesNotContain(Wo, await drv.SourceDocIdsAsync("WorkOrder", "InternalReturn"));
            // 編輯舊單：目前選的那張就算已結案也要帶回，否則下拉一開就是空的
            Assert.Contains(Wo, await drv.SourceDocIdsAsync("WorkOrder", "InternalReturn", current: Wo));
        }

        [Fact]
        public async Task 桌機發退料單明細單位必須是庫存單位_沒填補上庫存單位()
        {
            // 過帳把明細單位直接當庫存單位：選了別的單位就是把那個數字當庫存數量入帳（2026-10-12）
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            var drv = new MaterialSlipDriver(Factory, Plant, RoleId);
            MaterialSlip Slip(string? uom) => new()
            {
                Category = MaterialSlipCategory.InternalRequisition, SlipDate = StockDate,
                SourceDocType = "WorkOrder", SourceDocId = Wo,
                Lines = { new MaterialSlipLine { ProductCode = Mat, RequestedQty = 5m, UomCode = uom ?? "", FromWarehouseCode = Wh, FromLocationCode = Loc } },
            };

            var err = await drv.SaveErrorAsync(Slip("BOX"));
            Assert.NotNull(err);
            Assert.Contains("BOX", err);

            Assert.Null(await drv.SaveErrorAsync(Slip(null)));
            using var scope = NewScope(out var db);
            var line = await db.MaterialSlipLines.IgnoreQueryFilters()
                .Where(l => db.MaterialSlips.IgnoreQueryFilters().Any(s => s.SlipId == l.SlipId && s.SourceDocId == Wo))
                .SingleAsync();
            Assert.Equal(Uom, line.UomCode);
        }

        [Fact]
        public async Task 倉儲選單待辦數_與各頁清單筆數一致()
        {
            // 2026-10-12：工單發退料／備料作業／委外收料三張卡加待辦數。數字與點進去的清單各算一份就會對不上，
            // 所以端點共用清單的查詢；這裡把「數字＝清單筆數」釘住。
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await SeedIssueSlipAsync(Mi, 30m);
            using (var scope = NewScope(out var db))
            {
                db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
                {
                    OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier, OrderDate = StockDate,
                    Status = OutsourcePoStatus.Confirmed, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
                {
                    OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = Wo, OperationSeq = 10,
                    ProductCode = Fg, Qty = 50m, UomCode = Uom, UnitPrice = Fee,
                });
                await db.SaveChangesAsync();
            }

            using var counts = Json(Data(await Run(c => c.GetCounts())));
            int Count(string name) => counts.RootElement.GetProperty(name).GetInt32();

            Assert.Equal(Len(Data(await Run(c => c.GetSlips("issue")))), Count("SlipIssue"));
            Assert.Equal(Len(Data(await Run(c => c.GetSlips("return")))), Count("SlipReturn"));
            Assert.Equal(Len(Data(await Run(c => c.GetStagingOrders()))), Count("Staging"));
            Assert.Equal(Len(Data(await Run(c => c.GetOutsourceOrders(null))))
                       + Len(Data(await Run(c => c.GetApprovedReceipts()))), Count("Outsource"));
            Assert.True(Count("SlipIssue") >= 1, "前置不成立：種了一張發料單，待辦數卻是 0");
            Assert.True(Count("Outsource") >= 1, "前置不成立：種了一張可收料的委外採購單，待辦數卻是 0");
        }

        private static System.Text.Json.JsonDocument Json(object? data)
            => System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(data));
        private static int Len(object? data) { using var d = Json(data); return d.RootElement.GetArrayLength(); }

        // ── ③ 委外收料 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 超收／單價類案例的委外採購單：用整品委外工單。原本指向廠內工單 <c>Wo</c> 的工序 10，
        /// 2026-10-13 起那是工序委外（§19.7 C，收回不入庫、扣該工序投料），測的就不是超收規則了。
        /// </summary>
        private Task SeedOpoAsync() => SeedWholeOutsourceAsync();

        private async Task WithPlantParamsAsync(Func<Task> act, params (string Key, string Value)[] kv)
        {
            using (var scope = NewScope(out _))
                foreach (var (k, v) in kv) await Svc<IPlantParamService>(scope).SetAsync(k, Plant, v);
            try { await act(); }
            finally
            {
                using var scope = NewScope(out _);
                foreach (var (k, _) in kv) await Svc<IPlantParamService>(scope).ClearAsync(k, Plant);
            }
        }

        private Task<IActionResult> ReceiveAsync(decimal qty) => Run(c => c.ReceiveOutsource(
            new MobileMaterialController.OutsourceReceiveRequest(Opo,
                new() { new MobileReceiveLine(1, qty, "LOT-OV", null, null) },
                ClientId: "MOBM-OV-" + Guid.NewGuid().ToString("N"))));

        /// <summary>整品委外工單＋單一委外派工（50），委外採購單行指向它。</summary>
        private async Task SeedWholeOutsourceAsync()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            using var scope = NewScope(out var db);
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = OsWo, ProductionBatchId = Batch, ProductCode = Fg, RoutingId = "",
                Quantity = 50m, Status = WorkOrderStatus.Released, ApprovalStatus = DocumentApprovalStatus.Approved,
                IsOutsource = true, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = OsWo, OperationSeq = 1, WorkCenterId = "", IsOutsource = true });
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = OsWo + "-1", WorkOrderId = OsWo, OperationSeq = 1, Quantity = 50m, IsOutsource = true,
                Status = DispatchStatus.Pending, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier, OrderDate = StockDate,
                Status = OutsourcePoStatus.Confirmed, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = OsWo, OperationSeq = 1,
                ProductCode = Fg, Qty = 50m, UomCode = Uom, UnitPrice = Fee,
            });
            await db.SaveChangesAsync();
        }

        private async Task<(Dispatch D, WorkOrder W)> LoadOsAsync()
        {
            using var scope = NewScope(out var db);
            return (await db.Dispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.WorkOrderId == OsWo),
                    await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkOrderId == OsWo));
        }

        [Fact]
        public async Task 整品委外_合格入庫回寫委外派工_收滿後工單直接結案_不得再完工入庫()
        {
            // mfg-domain.md §19.3a（2026-10-12）：原本收貨不碰派工，委外工單收完貨還停在生產中。
            // 簡化模式確認收貨＝直接過帳進一般倉＝合格入庫的時點。
            await SeedWholeOutsourceAsync();
            await WithPlantParamsAsync(async () =>
            {
                Task<IActionResult> Recv(decimal q) => Run(c => c.ReceiveOutsource(new MobileMaterialController.OutsourceReceiveRequest(Opo,
                    new() { new MobileReceiveLine(1, q, "LOT-OS", null, Loc) }, ClientId: "MOBM-OS-" + Guid.NewGuid().ToString("N"))));

                Assert.IsType<OkObjectResult>(await Recv(30m));
                var (d1, w1) = await LoadOsAsync();
                Assert.Equal(30m, d1.QtyDone);
                Assert.Equal(DispatchStatus.InProgress, d1.Status);
                Assert.Equal(WorkOrderStatus.InProgress, w1.Status);

                Assert.IsType<OkObjectResult>(await Recv(20m));
                var (d2, w2) = await LoadOsAsync();
                Assert.Equal(50m, d2.QtyDone);
                Assert.Equal(DispatchStatus.Closed, d2.Status);
                // 成品已由收貨入庫：直接「入庫完成」，不是「完工、待入庫」
                Assert.Equal(WorkOrderStatus.Closed, w2.Status);

                using var scope = NewScope(out _);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Svc<IProductionCompletionService>(scope).CompleteAsync(OsWo, Plant, "W", "L"));
                Assert.Contains(OsWo, ex.Message);
            }, (ProjectZSystemParamKeys.InvSimplifiedMode, "true"));
        }

        [Fact]
        public async Task 非簡化模式_委外收回品進IQC待檢_品檢前派工不累計()
        {
            // 收貨確認後還沒入庫（要先上架到待驗倉、品檢合格才進一般倉）：派工不得先算完工
            await SeedWholeOutsourceAsync();
            await WithCategoryDefaultAsync(() => WithPlantParamsAsync(async () =>
            {
                Assert.IsType<OkObjectResult>(await ReceiveAsync(50m));
                Assert.Equal(0m, (await LoadOsAsync()).D.QtyDone);

                using var scope = NewScope(out var db);
                var pending = await Svc<SmartFactory.MES.Modules.QCR.Services.IIqcInspectionService>(scope).GetPendingLinesAsync(Plant);
                var row = Assert.Single(pending, p => p.SourceKind == SmartFactory.MES.Modules.QCR.Services.IqcSourceKinds.OutsourceReceipt);
                Assert.Equal(Fg, row.ProductCode);
                Assert.Equal(50m, row.Qty);

                // 手機沒填倉儲：確認時補上類別預設倉儲，品檢合格就放行到那裡
                var line = await db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.OutsourceReceiptId == row.GoodsReceiptId);
                Assert.Equal((Wh, Loc), (line.WhCode, line.LocationCode));
            }, (ProjectZSystemParamKeys.InvSimplifiedMode, "false")));
        }

        [Fact]
        public async Task 非簡化模式_收貨沒填倉儲且類別沒預設倉儲_要品檢的品號擋下並點名()
        {
            // 原本照收：品檢判「合格」時沒有放行目的地，料靜默留在待驗倉、派工不累計（2026-10-14 驗收發現）
            await SeedWholeOutsourceAsync();
            await WithPlantParamsAsync(async () =>
            {
                var bad = Assert.IsType<BadRequestObjectResult>(await ReceiveAsync(50m));
                Assert.Contains(Fg, System.Text.Json.JsonSerializer.Serialize(bad.Value));
                using var scope = NewScope(out var db);
                Assert.False(await db.OutsourceReceipts.IgnoreQueryFilters()
                    .AnyAsync(r => r.OutsourcePurchaseOrderId == Opo && r.Status != DocStatus.Draft && r.Status != DocStatus.Void));
            }, (ProjectZSystemParamKeys.InvSimplifiedMode, "false"));
        }

        [Fact]
        public async Task 品檢合格但委外收回品沒有放行目的地_不記合格並說明()
        {
            // 舊資料：收貨時沒補倉儲、已上架到待驗倉，類別也沒預設倉儲。原本記一筆「合格」卻什麼都沒放行
            const string QcWh = "ZZMOBM-QC", QcLoc = "ZZMOBM-QC-01";
            await SeedWholeOutsourceAsync();
            using (var scope = NewScope(out var db))
            {
                db.Warehouses.Add(new SmartFactory.MES.Modules.BAS.Models.Warehouse
                {
                    WhCode = QcWh, WhName = "待驗倉", WarehouseType = SmartFactory.MES.Modules.BAS.Models.WarehouseType.Quarantine,
                    IsMrpWarehouse = false, IsActive = true, PlantId = Plant,
                });
                db.StorageLocations.Add(new SmartFactory.MES.Modules.BAS.Models.StorageLocation
                {
                    WhCode = QcWh, LocationCode = QcLoc, LocationName = "待驗區", IsActive = true,
                });
                await db.SaveChangesAsync();
            }
            try
            {
                string recId = "";
                await WithCategoryDefaultAsync(() => WithPlantParamsAsync(async () =>
                {
                    Assert.IsType<OkObjectResult>(await ReceiveAsync(50m));
                    using var scope = NewScope(out var db);
                    recId = await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => r.OutsourcePurchaseOrderId == Opo)
                        .Select(r => r.OutsourceReceiptId).SingleAsync();
                }, (ProjectZSystemParamKeys.InvSimplifiedMode, "false")));

                // 模擬舊資料：明細沒有倉儲、50 已在待驗倉、類別沒有預設倉儲
                using (var scope = NewScope(out var db))
                {
                    var line = await db.OutsourceReceiptLines.IgnoreQueryFilters().SingleAsync(l => l.OutsourceReceiptId == recId);
                    line.WhCode = null; line.LocationCode = null;
                    db.Set<OutsourceReceiptLocationDetail>().Add(new OutsourceReceiptLocationDetail
                    {
                        OutsourceReceiptId = recId, LineNo = 1, WhCode = QcWh, LocationCode = QcLoc, LotNo = "", Qty = 50m,
                    });
                    await db.SaveChangesAsync();
                }

                using (var scope = NewScope(out var db))
                {
                    var outcome = await Svc<SmartFactory.MES.Modules.QCR.Services.IIqcInspectionService>(scope).SubmitAsync(
                        new SmartFactory.MES.Modules.QCR.Services.IqcSubmitInput(recId, 1, Fg, 50m, 8m, 0m, 4, null), Plant);
                    Assert.False(outcome.Success);
                    Assert.Equal("NO_RELEASE_TARGET", outcome.ErrorCode);
                    Assert.Contains(Fg, outcome.Message);
                    Assert.False(await db.MobileIqcRecords.AnyAsync(r => r.GoodsReceiptId == recId));   // 沒有記一筆「合格」
                    Assert.Equal(0m, (await LoadOsAsync()).D.QtyDone);
                }
            }
            finally
            {
                using var scope = NewScope(out var db);
                await db.Set<OutsourceReceiptLocationDetail>().Where(d => d.WhCode == QcWh).ExecuteDeleteAsync();
                await db.StorageLocations.IgnoreQueryFilters().Where(l => l.WhCode == QcWh).ExecuteDeleteAsync();
                await db.Warehouses.IgnoreQueryFilters().Where(w => w.WhCode == QcWh).ExecuteDeleteAsync();
            }
        }

        /// <summary>流程類別暫時設預設倉儲（=本廠一般倉），結束還原為空。</summary>
        private async Task WithCategoryDefaultAsync(Func<Task> act)
        {
            await TweakAsync(db =>
            {
                var cat = db.ProductCategories.IgnoreQueryFilters().First(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = Wh; cat.DefaultLocationCode = Loc;
            });
            try { await act(); }
            finally
            {
                await TweakAsync(db =>
                {
                    var cat = db.ProductCategories.IgnoreQueryFilters().First(c => c.CategoryCode == Category);
                    cat.DefaultWarehouseCode = null; cat.DefaultLocationCode = null;
                });
            }
        }

        /// <remarks>
        /// B1 與庫存簡化模式無關（§19.7）：非簡化廠收回品要先進待驗倉，但送外料一樣在確認收貨當下扣，
        /// 兩種模式都要跑——只測簡化模式時，非簡化那條路徑（確認後走上架／IQC）從沒驗過扣料有沒有接上。
        /// </remarks>
        [Theory]
        [InlineData("true")]
        [InlineData("false")]
        public async Task 委外簡化_送外料收貨時依比例自動扣_手動發過的不重扣(string invSimplified)
        {
            // mfg-domain.md §19.7 B1：不另開委外發料單，收貨確認時依「用料 × 累計收回量 ÷ 工單量」補扣
            await SeedWholeOutsourceAsync();
            using (var scope = NewScope(out var db))
            {
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = OsWo, OperationSeq = 1, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = 25m, UomCode = Uom, UnitCost = 10m, IssueMethod = MaterialIssueMethod.Push,
                    IssuedQty = 4m,   // 先前已用委外發料單手動發過 4
                });
                var cat = await db.ProductCategories.IgnoreQueryFilters().FirstAsync(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = Wh; cat.DefaultLocationCode = Loc;
                await db.SaveChangesAsync();
            }
            try
            {
                await WithPlantParamsAsync(async () =>
                {
                    Task<IActionResult> Recv(decimal q) => Run(c => c.ReceiveOutsource(new MobileMaterialController.OutsourceReceiveRequest(Opo,
                        new() { new MobileReceiveLine(1, q, "LOT-AI", null, Loc) }, ClientId: "MOBM-AI-" + Guid.NewGuid().ToString("N"))));

                    Assert.IsType<OkObjectResult>(await Recv(20m));      // 應發 25×20/50＝10，已發 4 → 補 6
                    Assert.Equal(10m, await IssuedAsync());
                    Assert.Equal(94m, await StockAsync());

                    Assert.IsType<OkObjectResult>(await Recv(30m));      // 收滿 50 → 應發 25 → 再補 15
                    Assert.Equal(25m, await IssuedAsync());
                    Assert.Equal(79m, await StockAsync());

                    using var scope = NewScope(out var db);
                    Assert.Equal(2, await db.MaterialSlips.IgnoreQueryFilters().CountAsync(s =>
                        s.SourceDocId == OsWo && s.Category == MaterialSlipCategory.OutsourceIssue && s.Status == MaterialSlipStatus.Posted));
                }, (ProjectZSystemParamKeys.InvSimplifiedMode, invSimplified), (ProjectZSystemParamKeys.MfgOutsourceIssueOnReceipt, "true"));
            }
            finally
            {
                using var scope = NewScope(out var db);
                var cat = await db.ProductCategories.IgnoreQueryFilters().FirstAsync(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = null; cat.DefaultLocationCode = null;
                await db.SaveChangesAsync();
            }

            async Task<decimal> IssuedAsync()
            {
                using var scope = NewScope(out var db);
                return await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == OsWo).SumAsync(m => m.IssuedQty - m.ReturnedQty);
            }
            async Task<decimal> StockAsync()
            {
                using var scope = NewScope(out var db);
                return await db.Inventories.IgnoreQueryFilters().Where(b => b.ProductCode == Mat && b.LocationCode == Loc).SumAsync(b => b.QtyOnHand);
            }
        }

        [Fact]
        public void 容器解析委外收貨服務_當場驗收與自動扣料的依賴都注入且無循環()
        {
            // 這些依賴是選填建構子參數，測試裡手動帶；正式環境靠容器——缺一個就是「參數開了才擲錯」，循環則整頁 500
            using var scope = NewScope(out _);
            var svc = Svc<IOutsourceReceiptService>(scope);
            foreach (var f in new[] { "_iqc", "_slips", "_locations", "_progress" })
                Assert.NotNull(typeof(OutsourceReceiptService).GetField(f,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(svc));
        }

        /// <remarks>B2 規格寫明「不論簡化與否都在確認當下過帳」，兩種庫存模式結果必須相同。</remarks>
        [Theory]
        [InlineData("false")]
        [InlineData("true")]
        public async Task 委外收料當場驗收_合格直接入一般倉回寫派工_不合格整批入待驗倉不算完工(string invSimplified)
        {
            // mfg-domain.md §19.7 B2：非簡化廠也不必「上架待驗倉 → 另開 IQC → 放行」三步；手機收料時填不良數即完成品檢
            const string QcWh = "ZZMOBM-QC", QcLoc = "ZZMOBM-QC-01";
            await SeedWholeOutsourceAsync();
            using (var scope = NewScope(out var db))
            {
                db.Warehouses.Add(new SmartFactory.MES.Modules.BAS.Models.Warehouse
                {
                    WhCode = QcWh, WhName = "待驗倉", WarehouseType = SmartFactory.MES.Modules.BAS.Models.WarehouseType.Quarantine,
                    IsMrpWarehouse = false, IsActive = true, PlantId = Plant,
                });
                db.StorageLocations.Add(new SmartFactory.MES.Modules.BAS.Models.StorageLocation
                {
                    WhCode = QcWh, LocationCode = QcLoc, LocationName = "待驗區", IsActive = true,
                });
                await db.SaveChangesAsync();
            }
            try
            {
                await WithPlantParamsAsync(async () =>
                {
                    Task<IActionResult> Recv(decimal q, decimal fail) => Run(c => c.ReceiveOutsource(new MobileMaterialController.OutsourceReceiveRequest(Opo,
                        new() { new MobileReceiveLine(1, q, "LOT-IR", null, Loc, fail) }, ClientId: "MOBM-IR-" + Guid.NewGuid().ToString("N"))));

                    Assert.IsType<OkObjectResult>(await Recv(30m, 0m));
                    Assert.Equal(30m, (await LoadOsAsync()).D.QtyDone);
                    Assert.Equal(30m, await FgAtAsync(Loc));

                    Assert.IsType<OkObjectResult>(await Recv(20m, 1m));   // Ac=0：一件不良整批拒
                    Assert.Equal(30m, (await LoadOsAsync()).D.QtyDone);
                    Assert.Equal(30m, await FgAtAsync(Loc));
                    Assert.Equal(20m, await FgAtAsync(QcLoc));

                    using var scope = NewScope(out var db);
                    var recIds = await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => r.OutsourcePurchaseOrderId == Opo)
                        .Select(r => r.OutsourceReceiptId).ToListAsync();
                    var results = await db.MobileIqcRecords.Where(r => recIds.Contains(r.GoodsReceiptId)).Select(r => r.Result).ToListAsync();
                    Assert.Equal(new[] { "Fail", "Pass" }, results.OrderBy(x => x).ToArray());
                    // 已經過帳、不在 IQC 待檢清單（不會被要求再驗一次）
                    var pending = await Svc<SmartFactory.MES.Modules.QCR.Services.IIqcInspectionService>(scope).GetPendingLinesAsync(Plant);
                    Assert.DoesNotContain(pending, p => recIds.Contains(p.GoodsReceiptId));
                }, (ProjectZSystemParamKeys.InvSimplifiedMode, invSimplified), (ProjectZSystemParamKeys.MfgOutsourceInspectAtReceipt, "true"));
            }
            finally
            {
                using var scope = NewScope(out var db);
                // 庫存列、異動、成本層一起清：只刪庫存列的話帳就對不上（FlowTestBase 收尾會驗帳）
                await db.StockMovements.IgnoreQueryFilters().Where(m => m.LocationCode == QcLoc).ExecuteDeleteAsync();
                await db.InventoryCostLayers.IgnoreQueryFilters().Where(l => l.LocationCode == QcLoc).ExecuteDeleteAsync();
                await db.Inventories.IgnoreQueryFilters().Where(b => b.LocationCode == QcLoc).ExecuteDeleteAsync();
                await db.StorageLocations.IgnoreQueryFilters().Where(l => l.WhCode == QcWh).ExecuteDeleteAsync();
                await db.Warehouses.IgnoreQueryFilters().Where(w => w.WhCode == QcWh).ExecuteDeleteAsync();
            }

            async Task<decimal> FgAtAsync(string loc)
            {
                using var scope = NewScope(out var db);
                return await db.Inventories.IgnoreQueryFilters().Where(b => b.ProductCode == Fg && b.LocationCode == loc).SumAsync(b => b.QtyOnHand);
            }
        }

        /// <summary>
        /// B1＋B2 同時開、當場驗收判不合格：送外料仍依累計收回量扣（§19.7 B1 公式以收回量計，
        /// 不合格品也是廠商用掉料做出來的），不合格批進待驗倉、不回寫派工。
        /// 使用者 2026-10-20 確認此語意。
        /// </summary>
        [Theory]
        [InlineData("false")]
        [InlineData("true")]
        public async Task 委外自動扣料加當場驗收_不合格批的送外料照扣_品不入一般倉(string invSimplified)
        {
            // 收尾把待驗倉的庫存列與異動一起刪掉：移動平均全廠一個均價，那一列的帳面（20×10）
            // 不等於它那筆異動的價值（20×17.5，結案批一次帶入剩下的在外原料），刪完明細帳就與總帳差 150。
            ExpectInvariantViolation("INV_GL_SUBLEDGER", "測試收尾刪掉待驗倉一列，均價品的帳面與異動價值不同，金額面必然對不上");
            const string QcWh = "ZZMOBM-QC", QcLoc = "ZZMOBM-QC-01";
            await SeedWholeOutsourceAsync();
            using (var scope = NewScope(out var db))
            {
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = OsWo, OperationSeq = 1, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = 25m, UomCode = Uom, UnitCost = 10m, IssueMethod = MaterialIssueMethod.Push,
                });
                var cat = await db.ProductCategories.IgnoreQueryFilters().FirstAsync(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = Wh; cat.DefaultLocationCode = Loc;
                db.Warehouses.Add(new SmartFactory.MES.Modules.BAS.Models.Warehouse
                {
                    WhCode = QcWh, WhName = "待驗倉", WarehouseType = SmartFactory.MES.Modules.BAS.Models.WarehouseType.Quarantine,
                    IsMrpWarehouse = false, IsActive = true, PlantId = Plant,
                });
                db.StorageLocations.Add(new SmartFactory.MES.Modules.BAS.Models.StorageLocation
                {
                    WhCode = QcWh, LocationCode = QcLoc, LocationName = "待驗區", IsActive = true,
                });
                await db.SaveChangesAsync();
            }
            try
            {
                await WithPlantParamsAsync(async () =>
                {
                    Task<IActionResult> Recv(decimal q, decimal fail) => Run(c => c.ReceiveOutsource(new MobileMaterialController.OutsourceReceiveRequest(Opo,
                        new() { new MobileReceiveLine(1, q, "LOT-BB", null, Loc, fail) }, ClientId: "MOBM-BB-" + Guid.NewGuid().ToString("N"))));

                    Assert.IsType<OkObjectResult>(await Recv(30m, 0m));   // 應發 25×30/50＝15
                    Assert.Equal(15m, await IssuedAsync());
                    Assert.Equal(30m, (await LoadOsAsync()).D.QtyDone);

                    Assert.IsType<OkObjectResult>(await Recv(20m, 1m));   // 不合格整批拒，料仍扣到 25
                    Assert.Equal(25m, await IssuedAsync());
                    Assert.Equal(75m, await MatStockAsync());
                    Assert.Equal(30m, (await LoadOsAsync()).D.QtyDone);   // 不合格不算完工
                    Assert.Equal(30m, await FgAtAsync(Loc));
                    Assert.Equal(20m, await FgAtAsync(QcLoc));

                    // A8：不合格批耗掉的材料成本跟著貨進待驗倉（後續由 NCR 處置決定去向），
                    // 不是留在工單或消失——單位成本＝加工費＋材料，與合格批相同（兩批都是 25×?/50×10＝5/件）
                    var passCost = await FgUnitCostAtAsync(Loc);
                    var failCost = await FgUnitCostAtAsync(QcLoc);
                    Assert.True(failCost > Fee, $"待驗倉成品單位成本 {failCost} 應含材料（加工費 {Fee}）");
                    Assert.Equal(Math.Round(passCost, 4), Math.Round(failCost, 4));
                }, (ProjectZSystemParamKeys.InvSimplifiedMode, invSimplified),
                   (ProjectZSystemParamKeys.MfgOutsourceIssueOnReceipt, "true"),
                   (ProjectZSystemParamKeys.MfgOutsourceInspectAtReceipt, "true"));
            }
            finally
            {
                using var scope = NewScope(out var db);
                var cat = await db.ProductCategories.IgnoreQueryFilters().FirstAsync(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = null; cat.DefaultLocationCode = null;
                await db.SaveChangesAsync();
                // 庫存列、異動、成本層一起清：只刪庫存列的話帳就對不上（FlowTestBase 收尾會驗帳）
                await db.StockMovements.IgnoreQueryFilters().Where(m => m.LocationCode == QcLoc).ExecuteDeleteAsync();
                await db.InventoryCostLayers.IgnoreQueryFilters().Where(l => l.LocationCode == QcLoc).ExecuteDeleteAsync();
                await db.Inventories.IgnoreQueryFilters().Where(b => b.LocationCode == QcLoc).ExecuteDeleteAsync();
                await db.StorageLocations.IgnoreQueryFilters().Where(l => l.WhCode == QcWh).ExecuteDeleteAsync();
                await db.Warehouses.IgnoreQueryFilters().Where(w => w.WhCode == QcWh).ExecuteDeleteAsync();
            }

            async Task<decimal> IssuedAsync()
            {
                using var scope = NewScope(out var db);
                return await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == OsWo).SumAsync(m => m.IssuedQty - m.ReturnedQty);
            }
            async Task<decimal> MatStockAsync()
            {
                using var scope = NewScope(out var db);
                return await db.Inventories.IgnoreQueryFilters().Where(b => b.ProductCode == Mat && b.LocationCode == Loc).SumAsync(b => b.QtyOnHand);
            }
            async Task<decimal> FgUnitCostAtAsync(string loc)
            {
                using var scope = NewScope(out var db);
                return await db.Inventories.IgnoreQueryFilters().Where(b => b.ProductCode == Fg && b.LocationCode == loc && b.QtyOnHand > 0)
                    .Select(b => b.UnitCost).FirstAsync();
            }
            async Task<decimal> FgAtAsync(string loc)
            {
                using var scope = NewScope(out var db);
                return await db.Inventories.IgnoreQueryFilters().Where(b => b.ProductCode == Fg && b.LocationCode == loc).SumAsync(b => b.QtyOnHand);
            }
        }

        [Fact]
        public async Task 工序委外_收回不入庫_扣該工序投料_回寫派工_加工費與帶出料計入工單成本()
        {
            // mfg-domain.md §19.7 C：廠內工單的一道工序臨時「轉委外」。收回的是在製品——
            // 不得把成品記進庫存；該工序的投料此時扣（不看 B1 參數）；加工費借存貨／貸暫估應付，
            // 加工費＋帶出料累計到工單委外成本，完工入庫時併入單位成本。非簡化模式也不經上架／品檢。
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);   // Wo 量 10，工序 10 用料 30
            using (var scope = NewScope(out var db))
            {
                var d = await db.Dispatches.IgnoreQueryFilters().SingleAsync(x => x.DispatchId == Disp);
                d.IsOutsource = true; d.MachineCode = null; d.Status = DispatchStatus.Pending;   // 派工「轉委外」
                db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
                {
                    OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier, OrderDate = StockDate,
                    Status = OutsourcePoStatus.Confirmed, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
                {
                    OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = Wo, OperationSeq = 10,
                    ProductCode = Fg, Qty = 10m, UomCode = Uom, UnitPrice = Fee,
                });
                var cat = await db.ProductCategories.IgnoreQueryFilters().FirstAsync(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = Wh; cat.DefaultLocationCode = Loc;
                await db.SaveChangesAsync();
            }
            try
            {
                await WithPlantParamsAsync(async () =>
                {
                    var order = Json(Data(await Run(c => c.GetOutsourceOrder(Opo))));
                    Assert.True(order.RootElement.GetProperty("IsOperation").GetBoolean());
                    Assert.False(order.RootElement.GetProperty("RequiresLocation").GetBoolean());

                    Task<IActionResult> Recv(decimal q) => Run(c => c.ReceiveOutsource(new MobileMaterialController.OutsourceReceiveRequest(Opo,
                        new() { new MobileReceiveLine(1, q, null, null, null) }, ClientId: "MOBM-OP-" + Guid.NewGuid().ToString("N"))));

                    Assert.IsType<OkObjectResult>(await Recv(4m));
                    using (var scope = NewScope(out var db))
                    {
                        var rec = await db.OutsourceReceipts.IgnoreQueryFilters().SingleAsync(r => r.OutsourcePurchaseOrderId == Opo);
                        Assert.Equal(DocStatus.Posted, rec.Status);                         // 不等上架／品檢
                        Assert.Equal(4m, (await db.Dispatches.IgnoreQueryFilters().SingleAsync(x => x.DispatchId == Disp)).QtyDone);
                        Assert.Equal(12m, await db.WorkOrderMaterials.IgnoreQueryFilters()
                            .Where(m => m.WorkOrderId == Wo).SumAsync(m => m.IssuedQty));     // 30 × 4 ÷ 10
                        Assert.Equal(88m, await db.Inventories.IgnoreQueryFilters()
                            .Where(b => b.ProductCode == Mat && b.LocationCode == Loc).SumAsync(b => b.QtyOnHand));
                        Assert.False(await db.Inventories.IgnoreQueryFilters().AnyAsync(b => b.ProductCode == Fg && b.QtyOnHand != 0));
                        Assert.False(await db.StockMovements.IgnoreQueryFilters().AnyAsync(m => m.DocNo == rec.OutsourceReceiptId));

                        var j = await db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                            .SingleAsync(e => e.SourceDocId == rec.OutsourceReceiptId && e.SourceDocType == "INV");
                        Assert.Equal(4m * Fee, j.Lines.Single(l => l.AccountCode == SmartFactory.MES.Modules.ACT.StandardAccounts.RawMaterialInventoryCode).Debit);
                        Assert.Equal(4m * Fee, j.Lines.Single(l => l.AccountCode == SmartFactory.MES.Modules.ACT.StandardAccounts.GrIrAPClearingCode).Credit);
                    }

                    Assert.IsType<OkObjectResult>(await Recv(6m));                          // 收滿＝結案批，帶出料掃尾
                    using (var scope = NewScope(out var db))
                    {
                        var d = await db.Dispatches.IgnoreQueryFilters().SingleAsync(x => x.DispatchId == Disp);
                        Assert.Equal((10m, DispatchStatus.Closed), (d.QtyDone, d.Status));
                        var w = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(x => x.WorkOrderId == Wo);
                        Assert.Equal(10m * Fee + 30m * 10m, w.ActualOutsourceCost);          // 加工費＋帶出料 30 × 10
                        Assert.Equal(WorkOrderStatus.Completed, w.Status);                   // 廠內工單：完工待入庫，不是入庫完成
                    }
                }, (ProjectZSystemParamKeys.InvSimplifiedMode, "false"));
            }
            finally
            {
                using var scope = NewScope(out var db);
                var cat = await db.ProductCategories.IgnoreQueryFilters().FirstAsync(c => c.CategoryCode == Category);
                cat.DefaultWarehouseCode = null; cat.DefaultLocationCode = null;
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task 委外收貨_工序委外與整品委外混在同一張_整張不收()
        {
            await SeedWholeOutsourceAsync();
            using (var scope = NewScope(out var db))
            {
                db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
                {
                    OutsourcePurchaseOrderId = Opo, LineNo = 2, SourceWorkOrderId = Wo, OperationSeq = 10,
                    ProductCode = Fg, Qty = 10m, UomCode = Uom, UnitPrice = Fee,
                });
                await db.SaveChangesAsync();
            }
            Assert.IsNotType<OkObjectResult>(await Run(c => c.ReceiveOutsource(new MobileMaterialController.OutsourceReceiveRequest(Opo,
                new() { new MobileReceiveLine(1, 5m, null, null, Loc), new MobileReceiveLine(2, 5m, null, null, null) },
                ClientId: "MOBM-MX-" + Guid.NewGuid().ToString("N")))));
            using var s2 = NewScope(out var db2);
            Assert.False(await db2.OutsourceReceipts.IgnoreQueryFilters().AnyAsync(r => r.OutsourcePurchaseOrderId == Opo));
        }

        [Fact]
        public async Task 委外超收_設成拒收_超過容許上限整張不收()
        {
            // 2026-10-12 超收規則（mfg-domain.md §4.6）：容許率 0%＋拒收 → 51／50 不收，也不留下收貨單
            await SeedOpoAsync();
            await WithPlantParamsAsync(async () =>
            {
                Assert.IsNotType<OkObjectResult>(await ReceiveAsync(51m));
                using var scope = NewScope(out var db);
                Assert.False(await db.OutsourceReceipts.IgnoreQueryFilters().AnyAsync(r => r.OutsourcePurchaseOrderId == Opo));
            }, (ProjectZSystemParamKeys.MfgOutsourceOverReceiptAction, "Block"));
        }

        [Fact]
        public async Task 委外超收_容許率內照收_設成超收部分不付加工費_應付只算訂購量()
        {
            // 容許率 10%：55／50 在上限內照收；超收的 5 個不付加工費 → 單價攤成 Fee×50÷55，應付＝Fee×50
            await SeedOpoAsync();
            // 非簡化收貨要有放行目的地（類別預設倉儲），否則確認時擋下（2026-10-14）
            await WithCategoryDefaultAsync(async () =>
            {
                await WithPlantParamsAsync(async () =>
                {
                    Assert.IsType<OkObjectResult>(await ReceiveAsync(55m));
                    using var scope = NewScope(out var db);
                    var rec = await db.OutsourceReceipts.IgnoreQueryFilters().Include(r => r.Lines)
                        .SingleAsync(r => r.OutsourcePurchaseOrderId == Opo);
                    var line = Assert.Single(rec.Lines);
                    Assert.Equal(55m, line.ReceiptQty);
                    Assert.Equal(Fee * 50m, Math.Round(line.UnitCost * line.ReceiptQty, 2));
                    Assert.Contains(Fee.ToString("0.######"), line.Remarks);

                    var apAmount = await db.APInvoiceLines.IgnoreQueryFilters()
                        .Where(l => db.APInvoices.IgnoreQueryFilters().Any(i => i.APInvoiceId == l.APInvoiceId
                            && i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == rec.OutsourceReceiptId))
                        .SumAsync(l => l.LineAmount);
                    Assert.Equal(Fee * 50m, apAmount);
                }, (ProjectZSystemParamKeys.MfgOutsourceOverReceiptTolerancePct, "10"),
                   (ProjectZSystemParamKeys.MfgOutsourceOverReceiptFee, "NoPay"));
            });
        }

        [Fact]
        public async Task 委外收料_單價取自委外採購單_確認後未收量遞減_連按兩次只建一張()
        {
            await SeedWholeOutsourceAsync();
            // 非簡化收貨要有放行目的地（類別預設倉儲），否則確認時擋下（2026-10-14）
            await WithCategoryDefaultAsync(async () =>
            {
                Assert.Equal(50m, Remaining(Data(await Run(c => c.GetOutsourceOrder(Opo)))));

                var req = new MobileMaterialController.OutsourceReceiveRequest(Opo,
                    new() { new MobileReceiveLine(1, 20m, "LOT-A", null, null) },
                    ClientId: "MOBM-R-" + Guid.NewGuid().ToString("N"));
                Assert.IsType<OkObjectResult>(await Run(c => c.ReceiveOutsource(req)));
                Assert.IsType<OkObjectResult>(await Run(c => c.ReceiveOutsource(req)));   // 連按兩次

                using (var scope = NewScope(out var db))
                {
                    var rec = await db.OutsourceReceipts.IgnoreQueryFilters().Include(r => r.Lines)
                        .SingleAsync(r => r.OutsourcePurchaseOrderId == Opo);
                    Assert.NotEqual(DocStatus.Draft, rec.Status);                     // 已確認（或簡化模式已過帳）
                    var line = Assert.Single(rec.Lines);
                    Assert.Equal(Fee, line.UnitCost);
                    Assert.Equal(20m, line.StockQty);
                    Assert.Equal(1, line.OpoLineNo);
                    Assert.True(await db.APInvoices.IgnoreQueryFilters()
                        .AnyAsync(i => i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == rec.OutsourceReceiptId));
                }

                Assert.Equal(30m, Remaining(Data(await Run(c => c.GetOutsourceOrder(Opo)))));
            });
        }

        // ── 共用 ───────────────────────────────────────────────────────────────

        private async Task SeedStockAndWorkOrderAsync(decimal openingQty, decimal requiredQty)
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MOBM-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = openingQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 10m,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MOBM-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = 10m, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg, RoutingId = "FLOW-MOBM-RT",
                    Quantity = 10m, Status = WorkOrderStatus.Released, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrderOperations.Add(new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-MOBM-WC", UnitTime = 1m,
                });
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty, UomCode = Uom, UnitCost = 10m, IssueMethod = MaterialIssueMethod.Pull,
                });
                db.Dispatches.Add(new Dispatch
                {
                    DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10, Quantity = 10m,
                    Status = DispatchStatus.InProgress, MachineCode = "FLOW-MOBM-M",
                    ScheduledStart = DateTime.UtcNow, ScheduledEnd = DateTime.UtcNow.AddHours(1),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }
        }

        // ── 已備料的料先從備料區扣（2026-10-13）─────────────────────────────────

        private string StgWh  => $"{Plant}-STG";
        private string StgLoc => $"{Plant}-STG-01";

        /// <summary>走真的備料：備料單（已確認）→ StageAsync 把料從 Loc 移到備料儲位。</summary>
        private async Task StageAsync(decimal qty)
        {
            using (var scope = NewScope(out var db))
            {
                if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(w => w.WhCode == StgWh))
                    db.Warehouses.Add(new SmartFactory.MES.Modules.BAS.Models.Warehouse { WhCode = StgWh, WhName = "流程測試備料倉", PlantId = Plant });
                if (!await db.StorageLocations.IgnoreQueryFilters().AnyAsync(l => l.LocationCode == StgLoc))
                    db.StorageLocations.Add(new SmartFactory.MES.Modules.BAS.Models.StorageLocation { WhCode = StgWh, LocationCode = StgLoc, LocationName = "流程測試備料儲位" });
                db.MaterialStagingOrders.Add(new MaterialStagingOrder
                {
                    StagingId = "FLOW-MOBM-STG-" + Guid.NewGuid().ToString("N")[..6], PlantId = Plant, SourceType = "WorkOrder", SourceId = Wo,
                    StagingWhCode = StgWh, StagingLocationCode = StgLoc, Status = "Confirmed", CreatedBy = UserId, CreatedByRoleId = RoleId,
                    Lines = { new MaterialStagingLine { LineNo = 1, ProductCode = Mat, UomCode = Uom, StockUomCode = Uom, RequiredQty = qty,
                                                       SourceWorkOrderId = Wo, FromWarehouseCode = Wh, FromLocationCode = Loc } },
                });
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                var order = await db.MaterialStagingOrders.IgnoreQueryFilters().Include(o => o.Lines)
                    .FirstAsync(o => o.SourceId == Wo && o.Status == "Confirmed" && o.StagingId.StartsWith("FLOW-MOBM-STG"));
                await Svc<IMaterialStagingService>(scope).StageAsync(order, UserId);
            }
        }

        private Task<IActionResult> PostPicksAsync(params SlipPickLine[] picks) => Run(c => c.PostSlip(Mi,
            new MobileMaterialController.SlipPostRequest(picks.ToList(), ClientId: "MOBM-STG-" + Guid.NewGuid().ToString("N"))));

        private async Task<decimal> OnHandAtAsync(string loc)
        {
            using var scope = NewScope(out var db);
            return await db.Inventories.IgnoreQueryFilters().Where(i => i.ProductCode == Mat && i.LocationCode == loc).SumAsync(i => i.QtyOnHand);
        }

        [Fact]
        public async Task 已備料_從原儲位發料被擋並說明_從備料儲位發料才過帳()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await StageAsync(30m);
            Assert.Equal((70m, 30m), (await OnHandAtAsync(Loc), await OnHandAtAsync(StgLoc)));
            await SeedIssueSlipAsync(Mi, 30m);

            var rejected = await PostPicksAsync(new SlipPickLine(1, null, Loc, "", 30m));
            var bad = Assert.IsType<BadRequestObjectResult>(rejected);
            Assert.Contains(StgLoc, System.Text.Json.JsonSerializer.Serialize(bad.Value));
            Assert.Equal((70m, 30m), (await OnHandAtAsync(Loc), await OnHandAtAsync(StgLoc)));   // 什麼都沒扣

            Assert.IsType<OkObjectResult>(await PostPicksAsync(new SlipPickLine(1, null, StgLoc, "", 30m)));
            Assert.Equal((70m, 0m), (await OnHandAtAsync(Loc), await OnHandAtAsync(StgLoc)));
        }

        [Fact]
        public async Task 備料區不夠_先扣完備料區_不足部分可從其他儲位領_同一行拆成兩個儲位()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await StageAsync(20m);
            await SeedIssueSlipAsync(Mi, 30m);

            Assert.IsType<OkObjectResult>(await PostPicksAsync(
                new SlipPickLine(1, null, StgLoc, "", 20m), new SlipPickLine(1, null, Loc, "", 10m)));

            Assert.Equal((70m, 0m), (await OnHandAtAsync(Loc), await OnHandAtAsync(StgLoc)));
            using var scope = NewScope(out var db);
            var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(x => x.Lines).FirstAsync(x => x.SlipId == Mi);
            Assert.Equal(MaterialSlipStatus.Posted, slip.Status);
            Assert.Equal(2, slip.Lines.Count);
            Assert.All(slip.Lines, l => Assert.Equal("10_1", l.SourceLineId));   // 拆出來的行仍回寫同一筆工單用料
            Assert.Equal(30m, (await db.WorkOrderMaterials.IgnoreQueryFilters().FirstAsync(m => m.WorkOrderId == Wo)).IssuedQty);
        }

        [Fact]
        public async Task 備料區還有料卻沒先用完就從其他儲位領_擋下()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await StageAsync(20m);
            await SeedIssueSlipAsync(Mi, 30m);

            Assert.IsType<BadRequestObjectResult>(await PostPicksAsync(
                new SlipPickLine(1, null, StgLoc, "", 15m), new SlipPickLine(1, null, Loc, "", 15m)));
            Assert.Equal((80m, 20m), (await OnHandAtAsync(Loc), await OnHandAtAsync(StgLoc)));
        }

        [Fact]
        public async Task 備料區的量已經領完_之後的發料可從其他儲位領()
        {
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await StageAsync(20m);
            await SeedIssueSlipAsync(Mi, 20m);
            Assert.IsType<OkObjectResult>(await PostPicksAsync(new SlipPickLine(1, null, StgLoc, "", 20m)));

            await SeedIssueSlipAsync("FLOW-MOBM-MI2", 10m);
            Assert.IsType<OkObjectResult>(await Run(c => c.PostSlip("FLOW-MOBM-MI2",
                new MobileMaterialController.SlipPostRequest(new() { new SlipPickLine(1, null, Loc, "", 10m) }, ClientId: "MOBM-STG2-" + Guid.NewGuid().ToString("N")))));
            Assert.Equal((70m, 0m), (await OnHandAtAsync(Loc), await OnHandAtAsync(StgLoc)));
        }

        [Fact]
        public async Task 桌機發料單存檔_已備料卻從原儲位發_當下擋下_改從備料儲位才存得進去()
        {
            // 離開草稿後明細儲位就不能改：等到過帳才擋，單子會卡在「已確認」既改不了也取消不了
            await SeedStockAndWorkOrderAsync(openingQty: 100m, requiredQty: 30m);
            await StageAsync(30m);
            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().FirstAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(DocumentApprovalStatus.Approved, wo.ApprovalStatus);
            }
            var drv = new MaterialSlipDriver(Factory, Plant, RoleId);
            MaterialSlip Slip(string wh, string loc) => new()
            {
                Category = MaterialSlipCategory.InternalIssue, SlipDate = StockDate,
                SourceDocType = "WorkOrder", SourceDocId = Wo,
                Lines = { new MaterialSlipLine { ProductCode = Mat, RequestedQty = 30m, UomCode = Uom, FromWarehouseCode = wh, FromLocationCode = loc } },
            };

            var err = await drv.SaveErrorAsync(Slip(Wh, Loc));
            Assert.NotNull(err);
            Assert.Contains(StgLoc, err);
            Assert.Null(await drv.SaveErrorAsync(Slip(StgWh, StgLoc)));
        }

        private async Task SeedIssueSlipAsync(string id, decimal qty)
        {
            using var scope = NewScope(out var db);
            var slip = new MaterialSlip
            {
                SlipId = id, SlipNo = id, Category = MaterialSlipCategory.InternalIssue, Status = MaterialSlipStatus.Draft,
                SlipDate = StockDate, SourceDocType = "WorkOrder", SourceDocId = Wo, PlantId = Plant,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            slip.Lines.Add(new MaterialSlipLine
            {
                SlipId = id, LineNo = 1, ProductCode = Mat, RequestedQty = qty, UomCode = Uom, SourceLineId = "10_1",
            });
            db.MaterialSlips.Add(slip);
            await db.SaveChangesAsync();
        }

        private static decimal Remaining(object? data)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(data));
            return doc.RootElement.GetProperty("Lines")[0].GetProperty("RemainingQty").GetDecimal();
        }

        private static object? Data(IActionResult r) =>
            Assert.IsType<MobileOkResult>(Assert.IsType<OkObjectResult>(r).Value).Data;

        private async Task<IActionResult> Run(Func<MobileMaterialController, Task<IActionResult>> act)
        {
            using var scope = NewScope(out var db);
            var sp   = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            var user = sp.GetRequiredService<IUserContext>();
            var loc  = sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>();
            var slips = new MaterialSlipService(db, sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryReservationService>(), sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IDateTimeService>(), user, loc);
            var receipts = new OutsourceReceiptService(db, user, sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IPlantParamService>(), sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IAPInvoiceService>(),
                sp.GetRequiredService<IInventoryReservationService>(), sp.GetRequiredService<IOutsourceReceiptSettlementService>(),
                sp.GetRequiredService<IJournalEntryService>(), loc,
                slips: slips, locations: sp.GetRequiredService<SmartFactory.MES.Modules.BAS.Services.IPlantDefaultLocationResolver>(),
                // 從 sp 建（不是向容器要）：品檢紀錄的檢驗員要吃到測試使用者
                iqc: ActivatorUtilities.CreateInstance<SmartFactory.MES.Modules.QCR.Services.IqcInspectionService>(sp));
            var ctrl = new MobileMaterialController(db, user, new AllowAllMobile(), slips,
                sp.GetRequiredService<IMaterialStagingService>(), receipts, sp.GetRequiredService<IDateTimeService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
            return await act(ctrl);
        }

        private sealed class AllowAllMobile : IMobilePermissionService
        {
            public Task<IEnumerable<MobileModule>> GetAllowedModulesAsync(string roleId)
                => Task.FromResult(Enumerable.Empty<MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey) => Task.FromResult(true);
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult<IEnumerable<string>>(new[] { "view" });
        }
    }
}
