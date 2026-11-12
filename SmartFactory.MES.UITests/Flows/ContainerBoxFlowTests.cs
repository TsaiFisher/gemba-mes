using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 箱子（容器個體，container-tracking.md §9）端對端：建箱 → 完工入庫「帶入空箱」綁箱 →
    /// 同廠調撥批號欄掃箱號帶出內容 → 手機庫存查詢掃箱號。
    ///
    /// 服務層測試已守住帳與規則；這支守的是畫面接線——「帶入空箱」按了有沒有帶、
    /// 箱號欄有沒有送到伺服器、掃箱號的 change 事件有沒有接上、手機有沒有先試箱號。
    /// 這幾段都是 JS，壞了不會有任何伺服端錯誤。
    /// </summary>
    [Collection("UI Tests")]
    public class ContainerBoxFlowTests : FlowTestBase
    {
        private const string BoxType  = "E2E-BOX";
        private const string Prefix   = "E2EBX";
        private const string Wo       = "E2E-WOBX";
        private const string Product  = "E2E-FG01";
        private const string Wh       = "E2E-WFG";
        private const string Loc      = "E2E-LFG";

        private const string StageWh  = "E2E-WSTG";
        private const string StageLoc = "E2E-LSTG";

        public ContainerBoxFlowTests(UiTestFixture fixture) : base(fixture) { }

        private static async Task<decimal> LotQtyAsync(string lot, string location) =>
            await SeedFixture.ScalarAsync<decimal>($"""
                SELECT coalesce(sum("QtyOnHand"), 0) FROM "Inventories" WHERE "LotNo" = '{lot}' AND "LocationCode" = '{location}'
                """);

        private async Task ReopenTransferAsync(IPage page, string id, string view)
        {
            await GotoAsync(page, "/StockTransfer?searched=true");
            await page.Locator($"[data-url*='/StockTransfer/{view}/{id}']").First.ClickAsync();
            await page.WaitForSelectorAsync("#transferForm");
            await page.WaitForTimeoutAsync(400);
        }

        /// <summary>完工 100、單道工序、無用料的工單，加一個容量 30 的容器種類。可重跑：先清掉上一輪。</summary>
        private static Task SeedAsync() => SeedFixture.ExecAsync($"""
            CREATE TEMP TABLE _bx_tr AS SELECT DISTINCT "StockTransferId" FROM "StockTransferLines" WHERE "LotNo" LIKE '{Wo}-%';
            DELETE FROM "StockMovements" WHERE "DocNo" IN (SELECT "StockTransferId" FROM _bx_tr);
            DELETE FROM "StockTransferLines" WHERE "StockTransferId" IN (SELECT "StockTransferId" FROM _bx_tr);
            DELETE FROM "StockTransfers" WHERE "StockTransferId" IN (SELECT "StockTransferId" FROM _bx_tr);
            DROP TABLE _bx_tr;
            DELETE FROM "ContainerUnitHistories" WHERE "ContainerUnitId" LIKE '{Prefix}-%';
            DELETE FROM "ContainerUnits" WHERE "ContainerUnitId" LIKE '{Prefix}-%';
            DELETE FROM "StockMovements" WHERE "DocNo" = '{Wo}' OR "LotNo" LIKE '{Wo}-%';
            DELETE FROM "InventoryCostLayers" WHERE "LotNo" LIKE '{Wo}-%';
            DELETE FROM "Inventories" WHERE "LotNo" LIKE '{Wo}-%';
            DELETE FROM "WorkOrderActualCosts" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "Dispatches" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "ProductionBatches" WHERE "BatchId" = 'E2E-PBBX';

            INSERT INTO "Containers" ("ContainerCode","ContainerName","MaxCapacity","IsActive","CreatedAt","UpdatedAt")
            VALUES ('{BoxType}','E2E 週轉箱',30,true,now(),now())
            ON CONFLICT ("ContainerCode") DO UPDATE SET "MaxCapacity"=30, "IsActive"=true;

            INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority",
                "PlannedEndDate","Status","IsPredictedLate","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('E2E-PBBX','E2E-MRPBX',1,'{Product}',100,0,now(),0,false,now(),now(),'P1','1');
            INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
                "PlannedStartDate","PlannedEndDate","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('{Wo}','E2E-PBBX','{Product}','E2E-RT01',100,3,now(),now(),now(),now(),'P1','1');
            INSERT INTO "WorkOrderOperations" ("WorkOrderId","OperationSeq","WorkCenterId","SetupTime","UnitTime",
                "BatchMode","BatchQty","IsOutsource","RequiresInspection")
            VALUES ('{Wo}',10,'E2E-WC01',0,1,0,0,false,false);
            INSERT INTO "Dispatches" ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status","IsOutsource",
                "CreatedAt","UpdatedAt","PlantId")
            VALUES ('E2E-DPBX','{Wo}',10,100,100,5,false,now(),now(),'P1');
            """);

        [Fact]
        public async Task 建箱_完工入庫帶入空箱_調撥掃箱號_手機查箱號()
        {
            await SeedAsync();
            var page = await NewFlowPageAsync();

            // ── ① 箱子畫面批次建 4 個箱子 ───────────────────────────────────
            await GotoAsync(page, "/ContainerUnit");
            await page.Locator("button[data-url$='/ContainerUnit/Create']").ClickAsync();
            await page.WaitForSelectorAsync("#sideForm");
            await page.Locator("#containerCode").SelectOptionAsync(BoxType);
            await page.FillAsync("#prefix", Prefix);
            await page.FillAsync("#count", "4");
            await page.RunAndWaitForResponseAsync(
                () => page.Locator("#sideForm button[type='submit']").ClickAsync(),
                r => r.Url.Contains("/ContainerUnit/Create") && r.Request.Method == "POST");
            Assert.Equal(4L, await SeedFixture.ScalarAsync<long>(
                $"""SELECT count(*) FROM "ContainerUnits" WHERE "ContainerUnitId" LIKE '{Prefix}-%' AND "PlantId" = 'P1'"""));

            await GotoAsync(page, $"/ContainerUnit?searched=true&searchTerm={Prefix}");
            Assert.Equal(4, await page.Locator("table tbody tr").CountAsync());
            await OpenRowPanelAsync(page, $"{Prefix}-00001", "/ContainerUnit/Details", "#sidePanelBody dl");

            await GotoAsync(page, $"/ContainerUnit/Labels?ids={Prefix}-00001,{Prefix}-00002");
            Assert.Equal(2, await page.Locator(".box-label svg").CountAsync());

            // ── ② 完工入庫：分箱＋帶入空箱 ─────────────────────────────────
            await GotoAsync(page, $"/ProductionCompletion?searched=true&searchText={Wo}");
            await OpenRowPanelAsync(page, Wo, "ConfirmForm", "#panelForm");
            await page.Locator("#whSelect").SelectOptionAsync(Wh);
            await page.FillAsync("#locationInput", Loc);
            await page.Locator("#packByBox").CheckAsync();
            await page.Locator("#boxContainer").SelectOptionAsync(BoxType);
            await Assertions.Expect(page.Locator("#boxQty")).ToHaveValueAsync("30");
            await page.Locator("#fillEmptyBoxes").ClickAsync();
            await Assertions.Expect(page.Locator("#boxUnitIds")).ToHaveValueAsync(
                $"{Prefix}-00001\n{Prefix}-00002\n{Prefix}-00003\n{Prefix}-00004", new() { Timeout = 10_000 });

            var completion = await page.RunAndWaitForResponseAsync(
                () => page.Locator("#panelForm button[value='complete']").ClickAsync(),
                r => r.Url.Contains("/ProductionCompletion/Complete"));
            var body = await completion.TextAsync();
            Assert.True(body.Contains("\"success\":true"), $"完工入庫被拒：{body}；對話框：{DescribeDialogs()}");
            await Assertions.Expect(page.Locator("#boxDone")).ToBeVisibleAsync();
            // 綁了箱號：箱子已有固定 QR，不提供批號箱標籤（container-tracking.md §8.5，2026-11-05 裁決）
            await Assertions.Expect(page.Locator("#boxLabelLink")).ToBeHiddenAsync();

            var bound = await SeedFixture.ScalarAsync<string>($"""
                SELECT string_agg("LotNo", ',' ORDER BY "ContainerUnitId") FROM "ContainerUnits"
                WHERE "ContainerUnitId" LIKE '{Prefix}-%'
                """);
            Assert.Equal($"{Wo}-01,{Wo}-02,{Wo}-03,{Wo}-04", bound);

            // ── ③ 同廠調撥：批號欄掃箱號 → 帶出批號、品號、撥出儲位、整箱數量 ───────
            await GotoAsync(page, "/StockTransfer");
            await page.Locator("button[data-url$='/StockTransfer/Create']").ClickAsync();
            await page.WaitForSelectorAsync("#transferForm");
            await page.Locator("[data-action='addLine']").ClickAsync();
            var row = page.Locator("#linesTbody tr").Last;
            var lot = row.Locator("input.line-lot");
            await lot.FillAsync($"{Prefix}-00002");
            await lot.DispatchEventAsync("change");
            await Assertions.Expect(lot).ToHaveValueAsync($"{Wo}-02", new() { Timeout = 10_000 });
            await Assertions.Expect(row.Locator("select.product-select")).ToHaveValueAsync(Product);
            await Assertions.Expect(row.Locator("[name$='.FromLocationCode']")).ToHaveValueAsync(Loc);
            await Assertions.Expect(row.Locator("input.line-qty")).ToHaveValueAsync("30");

            // ── ③b 存檔 → 送審 → 過帳 → 作廢：批號欄真的送到伺服器，帳隨批號搬過去再搬回來（E8）──
            await row.Locator("select[name$='.ToWhCode']").SelectOptionAsync(StageWh);
            await page.WaitForTimeoutAsync(300);
            await row.Locator("select[name$='.ToLocationCode']").SelectOptionAsync(StageLoc);
            await SavePanelAsync(page, "#transferForm button[type='submit']", "調撥存檔");
            var transferId = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "StockTransferId" FROM "StockTransferLines" WHERE "LotNo" = '{Wo}-02' LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(transferId), "調撥存檔後明細沒有批號——批號欄沒送到伺服器");

            // 沒設簽核流程時存檔即核准並過帳；有流程時要依序按送審、過帳。依實際狀態決定要按哪幾顆，
            // 兩種部署都走得到（過帳鈕曾送錯 action，見 DocActionEndpointGuardTests）
            async Task<(int Status, int Approval)> StateAsync() => (
                await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "StockTransfers" WHERE "StockTransferId" = '{transferId}'"""),
                await SeedFixture.ScalarAsync<int>($"""SELECT "ApprovalStatus" FROM "StockTransfers" WHERE "StockTransferId" = '{transferId}'"""));
            foreach (var action in new[] { "Submit", "Confirm" })
            {
                var (status, approval) = await StateAsync();
                if (status != 0 || (action == "Submit" && approval == 2)) continue;   // 已過帳／已核准
                await ReopenTransferAsync(page, transferId, "Edit");
                var btn = page.Locator($"#transferForm [data-doc-action='{action}']");
                Assert.True(await btn.CountAsync() > 0, $"調撥 {transferId} 面板上找不到「{action}」按鈕");
                var resp = await page.RunAndWaitForResponseAsync(() => btn.ClickAsync(),
                    r => r.Url.Contains("/StockTransfer/") && r.Request.Method == "POST");
                Assert.True(resp.Ok && (await resp.TextAsync()).Contains("\"success\":true"),
                    $"調撥「{action}」失敗：{resp.Status} {resp.Url} {await resp.TextAsync()}{DescribeDialogs()}");
            }
            Assert.Equal(3, (await StateAsync()).Status);                              // Posted
            Assert.Equal(30m, await LotQtyAsync($"{Wo}-02", StageLoc));
            Assert.Equal(0m, await LotQtyAsync($"{Wo}-02", Loc));

            await ReopenTransferAsync(page, transferId, "Details");
            var voidResp = await page.RunAndWaitForResponseAsync(
                () => page.Locator("[data-action='voidDoc']").ClickAsync(),
                r => r.Url.Contains("/StockTransfer/Void"));
            Assert.True(voidResp.Ok, $"調撥作廢失敗：{await voidResp.TextAsync()}{DescribeDialogs()}");
            Assert.Equal(30m, await LotQtyAsync($"{Wo}-02", Loc));
            Assert.Equal(0m, await LotQtyAsync($"{Wo}-02", StageLoc));

            // ── ④ 手機庫存查詢：掃箱號 → 顯示箱子目前內容 ─────────────────────
            var (ctx, mobile) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await mobile.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stock.html");
                await mobile.FillAsync("#productInput", $"{Prefix}-00003");
                await mobile.ClickAsync("[data-action='queryStock']");
                await Assertions.Expect(mobile.Locator("#prodCode")).ToHaveTextAsync(Product, new() { Timeout = 10_000 });
                await Assertions.Expect(mobile.Locator("#prodName")).ToContainTextAsync($"{Wo}-03");
                await Assertions.Expect(mobile.Locator("#locationRows")).ToContainTextAsync(Loc);
                Assert.Equal(0, await mobile.Locator("#toast-container .toast.text-bg-danger").CountAsync());
            }
            finally { await ctx.CloseAsync(); }

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }
    }
}
