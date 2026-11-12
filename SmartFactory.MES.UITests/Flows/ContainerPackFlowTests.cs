using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 進貨上架裝箱、既有庫存換箱（container-tracking.md §9.11）端對端：倉庫作業上架面板「裝入箱號」→
    /// 箱子畫面「裝箱」掃來源箱號（換箱）帶出內容 → 送出。
    ///
    /// 規則由 Flows/ContainerPackFlowTests（整合）守住；這支守畫面接線：裝入箱號有沒有送到上架 API、
    /// 裝箱面板的來源箱號／庫存列 radio／隱藏欄位有沒有接上（這段 JS 在 Index 的 Scripts，面板只是 HTML）。
    /// </summary>
    [Collection("UI Tests")]
    public class ContainerPackFlowTests : FlowTestBase
    {
        private const string Product = "E2E-FG01";
        private const string Wh      = "E2E-WFG";
        private const string Loc     = "E2E-LFG";
        private const string Prefix  = "E2EPK";
        private const string LoanIn  = "E2E-IBPK1";

        private static string Box(int n) => $"{Prefix}-{n:D5}";

        public ContainerPackFlowTests(UiTestFixture fixture) : base(fixture) { }

        /// <summary>一張已確認待上架的借入單（12），兩個空箱。可重跑：先清掉上一輪的上架、裝箱調撥與批號。</summary>
        private static Task SeedAsync() => SeedFixture.ExecAsync($"""
            CREATE TEMP TABLE _pk_tr AS SELECT "StockTransferId" FROM "StockTransfers" WHERE "Remarks" LIKE '%{Prefix}-%';
            CREATE TEMP TABLE _pk_lot AS SELECT DISTINCT "ToLotNo" AS lot FROM "StockTransferLines"
                WHERE "StockTransferId" IN (SELECT "StockTransferId" FROM _pk_tr) AND "ToLotNo" IS NOT NULL;
            DELETE FROM "StockMovements" WHERE "DocNo" IN (SELECT "StockTransferId" FROM _pk_tr) OR "DocNo" = '{LoanIn}';
            DELETE FROM "InventoryCostLayers" WHERE "LotNo" IN (SELECT lot FROM _pk_lot) OR "LotNo" LIKE '{LoanIn}-%';
            DELETE FROM "Inventories" WHERE "LotNo" IN (SELECT lot FROM _pk_lot) OR "LotNo" LIKE '{LoanIn}-%';
            DELETE FROM "StockTransferLines" WHERE "StockTransferId" IN (SELECT "StockTransferId" FROM _pk_tr);
            DELETE FROM "StockTransfers" WHERE "StockTransferId" IN (SELECT "StockTransferId" FROM _pk_tr);
            DROP TABLE _pk_tr; DROP TABLE _pk_lot;
            DELETE FROM "JournalLines" WHERE "JournalId" IN (SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" = '{LoanIn}');
            DELETE FROM "JournalEntries" WHERE "SourceDocId" = '{LoanIn}';
            DELETE FROM "LoanDocLocationDetails" WHERE "LoanDocId" = '{LoanIn}';
            DELETE FROM "LoanDocLines" WHERE "LoanDocId" = '{LoanIn}';
            DELETE FROM "LoanDocs" WHERE "LoanDocId" = '{LoanIn}';
            DELETE FROM "ContainerUnitHistories" WHERE "ContainerUnitId" LIKE '{Prefix}-%';
            DELETE FROM "ContainerUnits" WHERE "ContainerUnitId" LIKE '{Prefix}-%';

            INSERT INTO "Containers" ("ContainerCode","ContainerName","MaxCapacity","IsActive","CreatedAt","UpdatedAt")
            VALUES ('E2E-BOX','E2E 週轉箱',30,true,now(),now())
            ON CONFLICT ("ContainerCode") DO UPDATE SET "IsActive"=true;
            INSERT INTO "LoanDocs" ("LoanDocId","LoanDate","TypeCode","TypeCategory","EntityType","EntityCode","Status","ApprovalStatus",
                "CreatedBy","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('{LoanIn}',current_date,'IB','Loan','Customer','E2E-CO01',1,2,'admin',now(),now(),'P1','1');
            INSERT INTO "LoanDocLines" ("LoanDocId","LineNo","ProductCode","Qty","UomCode","StockQty","StockUomCode","UnitPrice","WhCode","QtyPutAway")
            VALUES ('{LoanIn}',1,'{Product}',12,'E2E-EA',12,'E2E-EA',10,'{Wh}',0);
            INSERT INTO "ContainerUnits" ("ContainerUnitId","ContainerCode","PlantId","IsActive","CreatedAt","UpdatedAt")
            VALUES ('{Box(1)}','E2E-BOX','P1',true,now(),now()), ('{Box(2)}','E2E-BOX','P1',true,now(),now());
            """);

        private static Task<string?> BoxLotAsync(string box) => SeedFixture.ScalarAsync<string?>(
            $"""SELECT "LotNo" FROM "ContainerUnits" WHERE "ContainerUnitId" = '{box}'""");

        private static async Task<decimal> LotQtyAsync(string lot) => await SeedFixture.ScalarAsync<decimal>($"""
            SELECT coalesce(sum("QtyOnHand"), 0) FROM "Inventories" WHERE "LotNo" = '{lot}' AND "PlantId" = 'P1'
            """);

        [Fact]
        public async Task 上架裝入箱號_系統產生批號_箱子畫面換箱帶出內容()
        {
            await SeedAsync();
            var page = await NewFlowPageAsync();

            // ── ① 倉庫作業上架：掃空箱，批號留空 → 系統產生批號、箱子綁上 ──────────
            await GotoAsync(page, "/WarehouseOps");
            await page.FillAsync("#inboundSearch", LoanIn);
            await ClickActionAsync(page, "loadPendingInbound", 800);
            await page.Locator("#inboundList tr", new PageLocatorOptions { HasText = LoanIn }).First.ClickAsync();
            await page.WaitForTimeoutAsync(500);
            await page.SelectOptionAsync("#pa_whCodeSel", Wh);
            await page.WaitForTimeoutAsync(300);
            await page.SelectOptionAsync("#pa_locationCode", Loc);
            await page.FillAsync("#pa_box", Box(1));
            var put = await page.RunAndWaitForResponseAsync(() => page.ClickAsync("#pa_submit"),
                r => r.Url.Contains("/api/warehouse/put-away") && r.Request.Method == "POST");
            var putBody = await put.TextAsync();
            Assert.True(put.Ok, $"上架裝箱失敗：{putBody}{DescribeDialogs()}");
            var lot1 = $"{LoanIn}-01-01";
            Assert.Contains(lot1, putBody);                                         // 訊息告訴使用者產生的批號
            Assert.Equal(lot1, await BoxLotAsync(Box(1)));
            Assert.Equal(12m, await LotQtyAsync(lot1));

            // ── ② 箱子畫面「裝箱」：掃來源箱號（換箱）→ 品號、庫存列、數量帶出 → 送出 ─────
            await GotoAsync(page, "/ContainerUnit");
            await page.Locator("button[data-url$='/ContainerUnit/Pack']").ClickAsync();
            await page.WaitForSelectorAsync("#sideForm #packUnit");
            await page.FillAsync("#packUnit", Box(2));
            await page.FillAsync("#packSourceBox", Box(1));
            await page.Locator("#packSourceBox").DispatchEventAsync("change");
            await Assertions.Expect(page.Locator("#packProduct")).ToHaveValueAsync(Product, new() { Timeout = 10_000 });
            await Assertions.Expect(page.Locator("#packLot")).ToHaveValueAsync(lot1, new() { Timeout = 10_000 });
            await Assertions.Expect(page.Locator("#packLocation")).ToHaveValueAsync(Loc);
            await Assertions.Expect(page.Locator("#packQty")).ToHaveValueAsync("12");

            var pack = await page.RunAndWaitForResponseAsync(
                () => page.Locator("#sideForm button[type='submit']").ClickAsync(),
                r => r.Url.Contains("/ContainerUnit/Pack") && r.Request.Method == "POST");
            var packBody = await pack.TextAsync();
            Assert.True(packBody.Contains("\"success\":true"), $"換箱失敗：{packBody}{DescribeDialogs()}");

            var lot2 = await BoxLotAsync(Box(2));
            Assert.False(string.IsNullOrEmpty(lot2));
            Assert.Equal(12m, await LotQtyAsync(lot2!));
            Assert.Equal(0m, await LotQtyAsync(lot1));                              // 舊箱的批號歸零＝舊箱自動空箱

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }
    }
}
