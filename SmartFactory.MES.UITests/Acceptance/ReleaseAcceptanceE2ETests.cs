using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 場次 X 的端到端版：X-7（整批投產時重算為 0 的工單與其子工單略過）、X-8（投產抵掉的庫存與完工產出保留給原需求）。
    /// 單張投產被擋已有 <c>MaterialFlowTests.成品庫存已足_投產被擋…</c>；出貨消耗與作廢回鎖有整合測試以真的出貨控制器驗過。
    /// </summary>
    [Collection("UI Tests")]
    public class ReleaseAcceptanceE2ETests : AcceptanceE2EBase
    {
        public ReleaseAcceptanceE2ETests(UiTestFixture fixture) : base(fixture) { }

        // ── X-7 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X7_整批投產_重算為0的工單與子工單略過_另一張照常投產_結果列出略過的工單()
        {
            const string b = "E2E-X7-B", a = "E2E-X7A-WO", child = "E2E-X7C-WO", other = "E2E-X7B-WO";
            await SeedFixture.ExecAsync($"""
                {X7CleanupSql()}
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('E2E-X7FG','E2E 庫存已足成品','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','M',0,100,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now()),
                       ('E2E-X7SEMI','E2E 半成品','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','M',0,50,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now()),
                       ('E2E-X7FG2','E2E 正常成品','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','M',0,100,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true;
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('{Plant}','E2E-X7FG','{NormalLoc}','',50,0,100,now());
                INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority","PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{b}','E2E-RUN',1,'E2E-X7FG',10,50,now() + interval '7 day',0,false,'{Plant}','1',now(),now());
                INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","GrossDemandQty","ParentWorkOrderId","Status","ApprovalStatus","IsOutsource",
                   "PlannedStartDate","PlannedEndDate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{a}','{b}','E2E-X7FG','',10,10,NULL,0,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now()),
                       ('{child}','{b}','E2E-X7SEMI','',10,10,'{a}',0,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now()),
                       ('{other}','{b}','E2E-X7FG2','',5,5,NULL,0,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now());
                """);
            try
            {
                var page = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(page);
                page.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch (PlaywrightException) { } };
                await page.GotoAsync($"{_fixture.BaseUrl}/BatchRelease?searched=true&searchText={b}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator($"[data-action='openPanel'][data-url*='/BatchRelease/Details/{b}']").First.ClickAsync();
                var btn = page.Locator("#sidePanelBody [data-action='releaseBatch']");
                await btn.WaitForAsync();
                Assert.True(await btn.IsEnabledAsync(), "整批投產鈕被停用（有工單被判料況擋下）");

                var resp = page.WaitForResponseAsync(r => r.Url.Contains($"/BatchRelease/ReleaseBatch/{b}") && r.Request.Method == "POST");
                await btn.ClickAsync();
                // 可能先問一次「重算後數量」，按確定後再送一次 confirmed=true；取最後一個成功的回應
                using var body = JsonDocument.Parse(await (await resp).TextAsync());
                var final = body.RootElement;
                if (!final.TryGetProperty("success", out var s1) || !s1.GetBoolean())
                {
                    var second = await page.WaitForResponseAsync(r => r.Url.Contains("confirmed=true"));
                    final = JsonDocument.Parse(await second.TextAsync()).RootElement;
                }
                Assert.True(final.GetProperty("success").GetBoolean(), final.ToString());
                Assert.Contains(a, final.GetProperty("message").GetString());

                Assert.Equal(0, await StatusAsync(a));
                Assert.Equal(0, await StatusAsync(child));
                Assert.Equal(1, await StatusAsync(other));
                Assert.Equal(10m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "Quantity" FROM "WorkOrders" WHERE "WorkOrderId"='{a}'"""));
                Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                    $"""SELECT count(*) FROM "MaterialSlips" WHERE "SourceDocId" IN ('{a}','{child}')"""));
            }
            finally
            {
                await SeedFixture.ExecAsync(X7CleanupSql());
            }
        }

        private static string X7CleanupSql() => """
            DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId" LIKE 'E2E-X7%');
            DELETE FROM "MaterialSlips" WHERE "SourceDocId" LIKE 'E2E-X7%';
            DELETE FROM "InventoryReservations" WHERE "ProductCode" LIKE 'E2E-X7%' OR "SourceDocId" LIKE 'E2E-X7%';
            DELETE FROM "Dispatches" WHERE "WorkOrderId" LIKE 'E2E-X7%';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId" LIKE 'E2E-X7%';
            DELETE FROM "WorkOrderMaterials" WHERE "WorkOrderId" LIKE 'E2E-X7%';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId" LIKE 'E2E-X7%';
            DELETE FROM "ProductionBatches" WHERE "BatchId" LIKE 'E2E-X7%';
            DELETE FROM "StockMovements" WHERE "ProductCode" LIKE 'E2E-X7%';
            DELETE FROM "Inventories" WHERE "ProductCode" LIKE 'E2E-X7%';
            """;

        // ── X-8 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X8_投產抵掉的庫存保留給原需求_別的需求看到的可用量不含這批_完工產出也保留且合計不超過需求()
        {
            const string run = "E2E-X8RUN", wo = "E2E-X8-WO", wo2 = "E2E-X8-WO2", fg = "E2E-X8FG";
            await SeedFixture.ExecAsync($"""
                {X8CleanupSql()}
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('{fg}','E2E 保留給原需求成品','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','M',0,100,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true;
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('{Plant}','{fg}','{NormalLoc}','',4,0,100,now());
                INSERT INTO "MrpRuns" ("MrpRunId","Status","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{run}',1,'{Plant}','1',now(),now());
                INSERT INTO "MrpRunLines" ("MrpRunId","LineSeq","SalesOrderId","SalesOrderLineSeq","ProductCode","RequiredQty","RequiredDate","Mode","StockQty","PurchaseQty","OutsourceQty","MakeQty")
                VALUES ('{run}',1,'E2E-X8SO',1,'{fg}',10,now() + interval '10 day',0,0,0,0,10),
                       ('{run}',2,'E2E-X8SO2',1,'{fg}',4,now() + interval '12 day',0,0,0,0,4);
                INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority","PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('E2E-X8-B','{run}',1,'{fg}',10,50,now() + interval '7 day',0,false,'{Plant}','1',now(),now());
                INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","GrossDemandQty","SourceMrpRunId","SourceMrpRunLineSeq","Status","ApprovalStatus","IsOutsource",
                   "PlannedStartDate","PlannedEndDate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{wo}','E2E-X8-B','{fg}','',10,10,'{run}',1,0,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now()),
                       ('{wo2}','E2E-X8-B','{fg}','',4,4,'{run}',2,0,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now());
                """);
            try
            {
                // ① 桌機投產：成品在庫 4 → 工單改做 6，抵掉的 4 保留給 MRP 需求列（銷貨訂單 E2E-X8SO）
                var page = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(page);
                page.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch (PlaywrightException) { } };
                await ReleaseAsync(page, wo);
                Assert.Equal(6m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "Quantity" FROM "WorkOrders" WHERE "WorkOrderId"='{wo}'"""));
                Assert.Equal(4m, await ReservedForRunAsync(run));

                // ② 別的需求看到的廠級可用量不含這批：另一張需求 4 的工單投產，不能拿這 4 個抵，仍要做 4
                await ReleaseAsync(page, wo2);
                Assert.Equal(4m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "Quantity" FROM "WorkOrders" WHERE "WorkOrderId"='{wo2}'"""));
                Assert.Equal(4m, await ReservedForRunAsync(run));

                // ③ 完工 6 入庫：產出也保留給原需求，合計 10＝需求量
                await SeedFixture.ExecAsync($"""
                    INSERT INTO "WorkOrderOperations" ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime","BatchMode","BatchQty","IsOutsource","RequiresInspection")
                    VALUES ('{wo}',10,'E2E 加工','E2E-WC01',0,1,0,1,false,false);
                    INSERT INTO "Dispatches" ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status","IsOutsource","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                    VALUES ('{wo}-D','{wo}',10,6,6,5,false,'{Plant}','1',now(),now());
                    UPDATE "WorkOrders" SET "Status"=3 WHERE "WorkOrderId"='{wo}';
                    """);
                await page.GotoAsync($"{_fixture.BaseUrl}/ProductionCompletion?searched=true&pageSize=200");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator($"[data-action='openPanel'][data-url*='/ProductionCompletion/ConfirmForm/{wo}']").First.ClickAsync();
                await page.Locator("#sidePanelBody select[name='whCode']").SelectOptionAsync("E2E-WFG");
                await page.Locator("#sidePanelBody input[name='locationCode']").FillAsync(NormalLoc);
                await page.Locator("#sidePanelBody button[type='submit'][value='complete']").ClickAsync();
                for (var i = 0; i < 50 && await StatusAsync(wo) != 4; i++) await Task.Delay(200);
                Assert.Equal(4, await StatusAsync(wo));
                Assert.Equal(10m, await StockAsync(fg, $"\"LocationCode\"='{NormalLoc}'"));
                Assert.Equal(10m, await ReservedForRunAsync(run));
            }
            finally
            {
                await SeedFixture.ExecAsync(X8CleanupSql());
            }
        }

        private async Task ReleaseAsync(IPage page, string wo)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/WorkOrder?searched=true&searchText={wo}&pageSize=50");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator($"[data-url$='/WorkOrder/Details/{wo}']").First.ClickAsync();
            await page.Locator("#sidePanelBody [data-action='releaseWorkOrder']").ClickAsync();
            for (var i = 0; i < 50 && await StatusAsync(wo) != 1; i++) await Task.Delay(200);
            Assert.Equal(1, await StatusAsync(wo));
        }

        private static Task<decimal> ReservedForRunAsync(string run) => SeedFixture.ScalarAsync<decimal>($"""
            SELECT COALESCE(SUM("ReservedQty"),0) FROM "InventoryReservations"
            WHERE "SourceType"=0 AND "SourceDocId"='{run}' AND "SourceLineSeq"=1 AND "Status"=0 AND "Category"=0
            """);

        private static string X8CleanupSql() => """
            DELETE FROM "InventoryReservations" WHERE "ProductCode"='E2E-X8FG' OR "SourceDocId" IN ('E2E-X8RUN','E2E-X8-WO','E2E-X8-WO2');
            DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId" LIKE 'E2E-X8%');
            DELETE FROM "MaterialSlips" WHERE "SourceDocId" LIKE 'E2E-X8%';
            DELETE FROM "Dispatches" WHERE "WorkOrderId"='E2E-X8-WO';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId"='E2E-X8-WO';
            DELETE FROM "WorkOrderMaterials" WHERE "WorkOrderId"='E2E-X8-WO';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId" IN ('E2E-X8-WO','E2E-X8-WO2');
            DELETE FROM "ProductionBatches" WHERE "BatchId"='E2E-X8-B';
            DELETE FROM "MrpRunLines" WHERE "MrpRunId"='E2E-X8RUN';
            DELETE FROM "MrpRuns" WHERE "MrpRunId"='E2E-X8RUN';
            DELETE FROM "StockMovements" WHERE "ProductCode"='E2E-X8FG';
            DELETE FROM "Inventories" WHERE "ProductCode"='E2E-X8FG';
            """;

        private static Task<int> StatusAsync(string wo) =>
            SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{wo}'""");
    }
}
