using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 箱子出廠與回收（container-tracking.md §9.10）端對端：倉庫作業出庫掃箱號整箱下架 → 箱子變廠外 →
    /// 箱子畫面批次回收；手機出庫下架儲位欄掃箱號（帶批號）。
    ///
    /// 規則由 Flows/ContainerOffsiteFlowTests（整合）守住；這支守的是畫面接線：掃箱號有沒有把數量填進
    /// 對的批號列、手機下架有沒有把批號送到伺服器（修正前一律送空批號）、回收面板有沒有接上。
    /// </summary>
    [Collection("UI Tests")]
    public class ContainerOffsiteFlowTests : FlowTestBase
    {
        private const string Product = "E2E-FG01";
        private const string Loc     = "E2E-LFG";
        private const string Prefix  = "E2EOF";
        private const string Loan1   = "E2E-ILOF1";
        private const string Loan2   = "E2E-ILOF2";
        private const string Lot1    = "E2E-OFLOT1";
        private const string Lot2    = "E2E-OFLOT2";

        private static string Box(int n) => $"{Prefix}-{n:D5}";

        public ContainerOffsiteFlowTests(UiTestFixture fixture) : base(fixture) { }

        /// <summary>
        /// 兩張已確認待下架的借出單（10、5），兩個箱子各裝一個批號（10、5），同儲位另有無批號庫存
        /// ——下架若沒帶批號就會從無批號那份扣，箱子的批號原封不動。
        /// </summary>
        private static Task SeedAsync() => SeedFixture.ExecAsync($"""
            DELETE FROM "JournalLines" WHERE "JournalId" IN (SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" IN ('{Loan1}','{Loan2}'));
            DELETE FROM "JournalEntries" WHERE "SourceDocId" IN ('{Loan1}','{Loan2}');
            DELETE FROM "StockMovements" WHERE "DocNo" IN ('{Loan1}','{Loan2}') OR "LotNo" IN ('{Lot1}','{Lot2}');
            DELETE FROM "InventoryCostLayers" WHERE "LotNo" IN ('{Lot1}','{Lot2}');
            DELETE FROM "Inventories" WHERE "LotNo" IN ('{Lot1}','{Lot2}');
            DELETE FROM "LoanDocLines" WHERE "LoanDocId" IN ('{Loan1}','{Loan2}');
            DELETE FROM "LoanDocs" WHERE "LoanDocId" IN ('{Loan1}','{Loan2}');
            DELETE FROM "ContainerUnitHistories" WHERE "ContainerUnitId" LIKE '{Prefix}-%';
            DELETE FROM "ContainerUnits" WHERE "ContainerUnitId" LIKE '{Prefix}-%';

            INSERT INTO "Containers" ("ContainerCode","ContainerName","MaxCapacity","IsActive","CreatedAt","UpdatedAt")
            VALUES ('E2E-BOX','E2E 週轉箱',30,true,now(),now())
            ON CONFLICT ("ContainerCode") DO UPDATE SET "IsActive"=true;

            INSERT INTO "LoanDocs" ("LoanDocId","LoanDate","TypeCode","TypeCategory","EntityType","EntityCode","Status","ApprovalStatus",
                "CreatedBy","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('{Loan1}',current_date,'IL','Loan','Customer','E2E-CO01',1,2,'admin',now(),now(),'P1','1'),
                   ('{Loan2}',current_date,'IL','Loan','Customer','E2E-CO01',1,2,'admin',now(),now(),'P1','1');
            INSERT INTO "LoanDocLines" ("LoanDocId","LineNo","ProductCode","Qty","UomCode","StockQty","StockUomCode","WhCode","QtyPutAway")
            VALUES ('{Loan1}',1,'{Product}',10,'E2E-EA',10,'E2E-EA','E2E-WFG',0),
                   ('{Loan2}',1,'{Product}',5,'E2E-EA',5,'E2E-EA','E2E-WFG',0);

            INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
            VALUES ('P1','{Product}','{Loc}','{Lot1}',10,0,10,now()), ('P1','{Product}','{Loc}','{Lot2}',5,0,10,now());
            INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
            VALUES ('P1','{Product}','{Loc}','',50,0,10,now())
            ON CONFLICT DO NOTHING;
            UPDATE "Inventories" SET "QtyOnHand" = GREATEST("QtyOnHand", 50)
            WHERE "PlantId"='P1' AND "ProductCode"='{Product}' AND "LocationCode"='{Loc}' AND "LotNo"='';

            INSERT INTO "ContainerUnits" ("ContainerUnitId","ContainerCode","PlantId","ProductCode","LotNo","BoundAt","IsActive","CreatedAt","UpdatedAt")
            VALUES ('{Box(1)}','E2E-BOX','P1','{Product}','{Lot1}',now(),true,now(),now()),
                   ('{Box(2)}','E2E-BOX','P1','{Product}','{Lot2}',now(),true,now(),now());
            """);

        private static async Task<decimal> LotQtyAsync(string lot) => await SeedFixture.ScalarAsync<decimal>($"""
            SELECT coalesce(sum("QtyOnHand"), 0) FROM "Inventories" WHERE "LotNo" = '{lot}' AND "PlantId" = 'P1'
            """);

        [Fact]
        public async Task 倉庫作業掃箱號整箱出庫_箱子廠外_批次回收_手機下架掃箱號帶批號()
        {
            await SeedAsync();
            var page = await NewFlowPageAsync();

            // ── ① 倉庫作業：選借出單 → 掃箱號 → 該箱批號那一列自動填滿 → 下架 ───────────
            await GotoAsync(page, "/WarehouseOps");
            await page.ClickAsync("#tab-outbound");
            await page.WaitForTimeoutAsync(600);
            await page.FillAsync("#outboundSearch", Loan1);
            await ClickActionAsync(page, "loadPendingOutbound", 800);
            await page.Locator("#outboundList tr", new PageLocatorOptions { HasText = Loan1 }).First.ClickAsync();
            await page.WaitForSelectorAsync("#pickStockTable .pick-qty");

            await page.FillAsync("#pk_box", Box(1));
            await page.Locator("#pk_box").DispatchEventAsync("change");
            var lotRow = page.Locator("#pickStockTable tr", new PageLocatorOptions { HasText = Lot1 }).First;
            await Assertions.Expect(lotRow.Locator(".pick-qty")).ToHaveValueAsync("10", new() { Timeout = 10_000 });
            var filled = await page.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('#pickStockTable tr')]
                .filter(tr => (tr.querySelector('.pick-qty')?.value ?? '0') !== '0')
                .map(tr => tr.innerText.replace(/\s+/g, ' ').trim())");
            Assert.True(filled.Length == 1 && filled[0].Contains(Lot1),
                $"掃箱號應只填該箱批號那一列，實際填了：{string.Join(" | ", filled)}");

            var pick = await page.RunAndWaitForResponseAsync(() => page.ClickAsync("#pk_submit"),
                r => r.Url.Contains("/api/warehouse/pick"));
            Assert.True(pick.Ok, $"整箱下架失敗：{await pick.TextAsync()}{DescribeDialogs()}");
            Assert.Equal(0m, await LotQtyAsync(Lot1));

            // ── ② 箱子畫面：借出出完 → 廠外，列出出廠單號；批次回收 → 空箱 ────────────
            await GotoAsync(page, $"/ContainerUnit?searched=true&searchTerm={Prefix}&status=offsite");
            var offRow = page.Locator("table tbody tr", new PageLocatorOptions { HasText = Box(1) });
            await Assertions.Expect(offRow).ToContainTextAsync(Loan1);

            await page.Locator("button[data-url$='/ContainerUnit/Recover']").ClickAsync();
            await page.WaitForSelectorAsync("#sideForm textarea[name='unitIds']");
            await page.FillAsync("#sideForm textarea[name='unitIds']", $"{Box(1)}\n");
            var rec = await page.RunAndWaitForResponseAsync(
                () => page.Locator("#sideForm button[type='submit']").ClickAsync(),
                r => r.Url.Contains("/ContainerUnit/Recover") && r.Request.Method == "POST");
            Assert.Contains("\"success\":true", await rec.TextAsync());
            Assert.Null(await SeedFixture.ScalarAsync<string?>(
                $"""SELECT "LotNo" FROM "ContainerUnits" WHERE "ContainerUnitId" = '{Box(1)}'"""));

            // 借出單面板的「出廠箱號」列出這一箱，已回收
            var loanPanel = await page.EvaluateAsync<string>(@"id => fetch('/LoanDoc/Details/' + id,
                { headers: { 'X-Requested-With': 'XMLHttpRequest' } }).then(r => r.text())", Loan1);
            Assert.Contains("units-left", loanPanel);
            Assert.Contains(Box(1), loanPanel);

            // ── ③ 手機出庫下架：儲位欄掃箱號 → 帶出儲位、批號、數量 → 下架扣的是那個批號 ──
            var (ctx, mobile) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await mobile.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/pick.html");
                await mobile.FillAsync("#docSearch", Loan2);
                var item = mobile.Locator(".pending-item", new PageLocatorOptions { HasText = Loan2 }).First;
                await item.WaitForAsync(new() { Timeout = 15_000 });
                await item.ClickAsync();
                await MobileUi.FillChangeAsync(mobile.Locator("#locationCode"), Box(2));
                await Assertions.Expect(mobile.Locator("#locationCode")).ToHaveValueAsync(Loc, new() { Timeout = 10_000 });
                await Assertions.Expect(mobile.Locator("#pickLotInfo")).ToContainTextAsync(Lot2);
                await Assertions.Expect(mobile.Locator("#pickQty")).ToHaveValueAsync("5");
                var (text, ok) = await MobileUi.ToastAfterAsync(mobile, () => mobile.ClickAsync("#btnPick"));
                Assert.True(ok, $"手機整箱下架失敗：{text}");
            }
            finally { await ctx.CloseAsync(); }
            Assert.Equal(0m, await LotQtyAsync(Lot2));

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task 手機自由盤點掃箱號_只列這一箱的批號()
        {
            await SeedAsync();
            var (ctx, mobile) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await mobile.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stockcount.html");
                await mobile.ClickAsync("[data-action='switchMode'][data-mode='free']");
                await mobile.FillAsync("#locInput", Box(2).ToLowerInvariant());
                await mobile.ClickAsync("[data-action='loadLocation']");
                await Assertions.Expect(mobile.Locator("#locTitle")).ToContainTextAsync(Box(2), new() { Timeout = 10_000 });
                await Assertions.Expect(mobile.Locator("#locTitle")).ToContainTextAsync(Loc);
                var rows = mobile.Locator("#productRows .product-row");
                await Assertions.Expect(rows).ToHaveCountAsync(1);                      // 同儲位的另一箱與無批號庫存都不列
                await Assertions.Expect(rows.First).ToContainTextAsync(Lot2);
                await Assertions.Expect(mobile.Locator("#qty-0")).ToHaveValueAsync("5");
            }
            finally { await ctx.CloseAsync(); }
        }
    }
}
