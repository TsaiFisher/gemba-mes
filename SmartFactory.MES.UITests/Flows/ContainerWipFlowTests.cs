using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 生產中容器（container-tracking.md §9.9）端對端：派工報工面板容器預設值與「箱號」欄 →
    /// 裝著貨的箱子被擋時訊息有呈現 → 手機報工掃同一箱由下一工序接手 → 箱子面板清空、停用／啟用。
    ///
    /// 規則已由 Flows/MfgWipBoxFlowTests（整合）守住；這支守畫面接線：箱號欄有沒有送到伺服器、
    /// 錯誤訊息有沒有到使用者眼前、手機容器欄掃箱號有沒有走到裝箱、面板表單有沒有接上。
    /// </summary>
    [Collection("UI Tests")]
    public class ContainerWipFlowTests : FlowTestBase
    {
        private const string BoxType = "E2E-BOX";
        private const string Prefix  = "E2EWB";
        private const string Wo      = "E2E-WOWB";
        private const string Routing = "E2E-RTBX";
        private const string Product = "E2E-FG01";
        private const string Lot     = "E2E-WBLOT";

        private static string Box(int n) => $"{Prefix}-{n:D5}";
        private static string Disp(int op) => $"E2E-DPWB{op}";
        private static string Sess(int op) => $"E2E-SSWB{op}";

        public ContainerWipFlowTests(UiTestFixture fixture) : base(fixture) { }

        /// <summary>
        /// 生產中的兩道工序工單（各 60、各一筆開工中的上工），工序 10 的完工容器＝E2E-BOX；
        /// 三個箱子：1、2 空箱，3 裝著一個庫存批號。途程設為停用，不影響其他測試選途程。
        /// </summary>
        private static Task SeedAsync() => SeedFixture.ExecAsync($"""
            DELETE FROM "ContainerUnitHistories" WHERE "ContainerUnitId" LIKE '{Prefix}-%';
            DELETE FROM "ContainerUnits" WHERE "ContainerUnitId" LIKE '{Prefix}-%';
            DELETE FROM "ProductionReportContainers" WHERE "ReportId" IN (SELECT "ReportId" FROM "ProductionReports" WHERE "WorkOrderId" = '{Wo}');
            DELETE FROM "ProductionReports" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "ProductionSessions" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "Dispatches" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId" = '{Wo}';
            DELETE FROM "ProductionBatches" WHERE "BatchId" = 'E2E-PBWB';
            DELETE FROM "Inventories" WHERE "LotNo" = '{Lot}';

            INSERT INTO "Containers" ("ContainerCode","ContainerName","MaxCapacity","IsActive","CreatedAt","UpdatedAt")
            VALUES ('{BoxType}','E2E 週轉箱',30,true,now(),now())
            ON CONFLICT ("ContainerCode") DO UPDATE SET "MaxCapacity"=30, "IsActive"=true;

            INSERT INTO "Routings" ("RoutingId","ProductCode","RoutingName","BatchSize","BufferDays","AvgLeadTime",
                "IsActive","Version","PlantId","CreatedAt","UpdatedAt")
            VALUES ('{Routing}','{Product}','E2E 在製箱途程',1,0,1,false,'1','P1',now(),now())
            ON CONFLICT ("RoutingId") DO UPDATE SET "IsActive"=false;
            INSERT INTO "RoutingOperations" ("RoutingId","OperationSeq","OperationName","WorkCenterId","TM_SETUP","TM_UNIT",
                "CD_BOR","QT_BOR","UomCode","IsOutsource","RequiresInspection","RequiresFpi",
                "TimeUnit","SetupTimeUnit","MoveTime","QueueTime","YieldRate","ContainerOutCode")
            VALUES ('{Routing}',10,'E2E 前段','E2E-WC01',0,1,0,1,'E2E-EA',false,false,false,2,0,0,0,100,'{BoxType}'),
                   ('{Routing}',20,'E2E 後段','E2E-WC01',0,1,0,1,'E2E-EA',false,false,false,2,0,0,0,100,NULL)
            ON CONFLICT ("RoutingId","OperationSeq") DO UPDATE SET "ContainerOutCode"=EXCLUDED."ContainerOutCode";

            INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority",
                "PlannedEndDate","Status","IsPredictedLate","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('E2E-PBWB','E2E-MRPWB',1,'{Product}',60,0,now(),0,false,now(),now(),'P1','1');
            INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
                "PlannedStartDate","PlannedEndDate","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('{Wo}','E2E-PBWB','{Product}','{Routing}',60,2,now(),now(),now(),now(),'P1','1');
            INSERT INTO "WorkOrderOperations" ("WorkOrderId","OperationSeq","WorkCenterId","SetupTime","UnitTime",
                "BatchMode","BatchQty","IsOutsource","RequiresInspection")
            VALUES ('{Wo}',10,'E2E-WC01',0,1,0,0,false,false), ('{Wo}',20,'E2E-WC01',0,1,0,0,false,false);
            INSERT INTO "Dispatches" ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status","IsOutsource",
                "CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('{Disp(10)}','{Wo}',10,60,0,4,false,now(),now(),'P1','1'),
                   ('{Disp(20)}','{Wo}',20,60,0,4,false,now(),now(),'P1','1');
            INSERT INTO "ProductionSessions" ("SessionId","DispatchId","WorkOrderId","Status","StartAt","PlantId","CreatedAt","UpdatedAt")
            VALUES ('{Sess(10)}','{Disp(10)}','{Wo}',0,now() - interval '1 hour','P1',now(),now()),
                   ('{Sess(20)}','{Disp(20)}','{Wo}',0,now() - interval '1 hour','P1',now(),now());

            INSERT INTO "ContainerUnits" ("ContainerUnitId","ContainerCode","PlantId","IsActive","CreatedAt","UpdatedAt")
            VALUES ('{Box(1)}','{BoxType}','P1',true,now(),now()),
                   ('{Box(2)}','{BoxType}','P1',true,now(),now()),
                   ('{Box(3)}','{BoxType}','P1',true,now(),now());
            INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
            VALUES ('P1','{Product}','E2E-LFG','{Lot}',5,0,10,now());
            UPDATE "ContainerUnits" SET "ProductCode"='{Product}', "LotNo"='{Lot}', "BoundAt"=now() WHERE "ContainerUnitId"='{Box(3)}';
            """);

        private static Task<string?> BoxFieldAsync(string box, string column) => SeedFixture.ScalarAsync<string?>(
            $"""SELECT "{column}"::text FROM "ContainerUnits" WHERE "ContainerUnitId" = '{box}'""");

        private async Task<string> ReportOnDesktopAsync(IPage page, string box, string qty)
        {
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={Disp(10)}&pageSize=50");
            await OpenRowPanelAsync(page, Disp(10), "ReportForm", "#panelForm");
            await page.FillAsync("input[name='qtyOk']", qty);
            await page.FillAsync("input[name='containerUnitId']", box);
            var resp = await page.RunAndWaitForResponseAsync(
                () => page.Locator("#panelForm button[value='report']").ClickAsync(),
                r => r.Url.Contains("/Dispatch/CreateReport"));
            return await resp.TextAsync();
        }

        [Fact]
        public async Task 報工箱號_桌機裝箱_被擋有訊息_手機下一工序接手_面板清空與停用()
        {
            await SeedAsync();
            var page = await NewFlowPageAsync();

            // ── ① 報工面板：容器下拉預設＝工序完工容器（E5），掃箱號報工 ─────────────
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={Disp(10)}&pageSize=50");
            await OpenRowPanelAsync(page, Disp(10), "ReportForm", "#panelForm");
            await Assertions.Expect(page.Locator("#panelForm select[name='containerCode']")).ToHaveValueAsync(BoxType);

            var body = await ReportOnDesktopAsync(page, Box(1).ToLowerInvariant(), "30");   // 掃描槍大小寫不一也要認得
            Assert.True(body.Contains("\"success\":true"), $"報工被拒：{body}{DescribeDialogs()}");
            Assert.Equal(Wo, await BoxFieldAsync(Box(1), "WorkOrderId"));
            Assert.Equal("10", await BoxFieldAsync(Box(1), "OperationSeq"));

            // ── ② 裝著庫存批號的箱子：整筆不報，伺服器訊息要到使用者眼前（E6）──────
            DialogMessages.Clear();
            body = await ReportOnDesktopAsync(page, Box(3), "5");
            Assert.Contains("\"success\":false", body);
            var shown = page.Locator("#sidePanelBody, #toast-container", new PageLocatorOptions { HasText = Lot });
            await page.WaitForTimeoutAsync(800);
            Assert.True(DialogMessages.Any(m => m.Contains(Lot)) || await shown.CountAsync() > 0,
                $"被擋的原因（批號 {Lot}）沒有出現在畫面上{DescribeDialogs()}");
            Assert.Equal("true", await SeedFixture.ScalarAsync<string?>(                 // 擋下來就沒累計
                $"""SELECT ("QtyDone" = 30)::text FROM "Dispatches" WHERE "DispatchId" = '{Disp(10)}'"""));

            // ── ③ 手機報工：工序 20 掃同一個箱號 → 整箱接手 ────────────────────
            var (ctx, mobile) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await mobile.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/reports.html?dispatchId={Disp(20)}&sessionId={Sess(20)}");
                await mobile.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await mobile.ClickAsync("#containerScanArea");
                await mobile.FillAsync("#containerId", Box(1));
                await mobile.FillAsync("#qtyOk", "30");
                var (text, ok) = await MobileUi.ToastAfterAsync(mobile, () => mobile.ClickAsync("#btnSubmit"));
                Assert.True(ok, $"手機報工失敗：{text}");
            }
            finally { await ctx.CloseAsync(); }
            Assert.Equal("20", await BoxFieldAsync(Box(1), "OperationSeq"));

            // ── ④ 箱子畫面：在製狀態、清空；空箱停用／啟用（E7）────────────────
            await GotoAsync(page, $"/ContainerUnit?searched=true&searchTerm={Prefix}&status=wip");
            Assert.Equal(1, await page.Locator("table tbody tr").CountAsync());
            await OpenRowPanelAsync(page, Box(1), "/ContainerUnit/Details", "#sidePanelBody dl");
            await Assertions.Expect(page.Locator("#sidePanelBody")).ToContainTextAsync(Wo);
            await page.RunAndWaitForResponseAsync(
                () => page.Locator("#sidePanelBody form[action*='/ContainerUnit/Clear'] button[type='submit']").ClickAsync(),
                r => r.Url.Contains("/ContainerUnit/Clear"));
            await page.WaitForTimeoutAsync(500);
            Assert.Null(await BoxFieldAsync(Box(1), "WorkOrderId"));

            await GotoAsync(page, $"/ContainerUnit?searched=true&searchTerm={Box(2)}");
            await OpenRowPanelAsync(page, Box(2), "/ContainerUnit/Details", "#sidePanelBody dl");
            await page.RunAndWaitForResponseAsync(
                () => page.Locator("#sidePanelBody form[action*='/ContainerUnit/SetActive'] button[type='submit']").ClickAsync(),
                r => r.Url.Contains("/ContainerUnit/SetActive"));
            await page.WaitForTimeoutAsync(500);
            Assert.Equal("false", await BoxFieldAsync(Box(2), "IsActive"));

            await GotoAsync(page, $"/ContainerUnit?searched=true&searchTerm={Box(2)}");
            await OpenRowPanelAsync(page, Box(2), "/ContainerUnit/Details", "#sidePanelBody dl");
            await page.RunAndWaitForResponseAsync(
                () => page.Locator("#sidePanelBody form[action*='/ContainerUnit/SetActive'] button[type='submit']").ClickAsync(),
                r => r.Url.Contains("/ContainerUnit/SetActive"));
            await page.WaitForTimeoutAsync(500);
            Assert.Equal("true", await BoxFieldAsync(Box(2), "IsActive"));

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }
    }
}
