using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機首件（FPI）、巡檢（IPQC）、揀料下架走真後端：清單 → 處理頁 → 送出 → 資料庫狀態。
    ///
    /// <para><b>為什麼要補</b>：2026-10-20 盤點時這三頁只有腳本冒煙測試；API 有整合測試
    /// （MobileFpiInspectionTests 等），但清單卡片帶到處理頁的網址參數、處理頁組出來的請求本文
    /// （FPI 的 orderType 傳數字、IPQC 走另一支端點）、以及送出後的 toast 判定，都只有走畫面才驗得到。</para>
    ///
    /// <para>種子沒有待首件／生產中的派工，測試期間暫時把 E2E-DP0025 改成需要的狀態；
    /// 揀料自建一張已確認的報廢單（E2E-SCRP1）。finally 一律清掉自建資料並 <see cref="SeedFixture.ResetAsync"/>。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MobileQualityPickE2ETests
    {
        private const string Wo = "E2E-WO0021";
        private const string Dispatch = "E2E-DP0025";   // 機台 E2E-MC02

        private readonly UiTestFixture _fixture;
        public MobileQualityPickE2ETests(UiTestFixture fixture) => _fixture = fixture;

        private static async Task<int?> IntAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : null;

        private static async Task<decimal?> DecAsync(string sql) =>
            decimal.TryParse(await SeedFixture.ScalarAsync<string>(sql), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        private static Task CleanInspectionsAsync() => SeedFixture.ExecAsync($"""
            DELETE FROM "InspectionItems" WHERE "InspectionOrderId" IN (SELECT "InspectionOrderId" FROM "InspectionOrders" WHERE "SourceDocId"='{Dispatch}');
            DELETE FROM "InspectionOrders" WHERE "SourceDocId"='{Dispatch}';
            """);

        /// <summary>處理頁：填抽樣數（取畫面算出的建議 n）與不良數後送出，回傳 toast。</summary>
        private static async Task<(string Text, bool Success)> SubmitInspectionAsync(IPage page, int failQty)
        {
            // 規格載入完會把建議 n 寫進 #sampleQty（初始值 0）；等它出現再填，免得與 loadSpec 搶寫。
            await page.WaitForFunctionAsync("() => +document.getElementById('sampleQty').value > 0", null, new() { Timeout = 15_000 });
            var n = await page.Locator("#sampleQty").InputValueAsync();
            await page.FillAsync("#sampleQty", n);
            await page.FillAsync("#failQty", failQty.ToString());
            return await MobileUi.ToastAfterAsync(page, () => page.Locator("#btnSubmit").ClickAsync());
        }

        // ── 首件：待首件清單 → 量測頁 → 合格放行（ReadyToStart）／不合格暫停（OnHold, FpiFailed） ──

        [Theory]
        [InlineData(0, "Pass", 3, null)]   // DispatchStatus.ReadyToStart
        [InlineData(1, "Fail", 6, 2)]      // DispatchStatus.OnHold, DispatchHoldReason.FpiFailed
        public async Task Fpi_FromList_SubmitDrivesDispatchStatus(int failQty, string orderStatus, int dispatchStatus, int? holdReason)
        {
            await CleanInspectionsAsync();
            await SeedFixture.ExecAsync($"""UPDATE "Dispatches" SET "Status"=7 WHERE "DispatchId"='{Dispatch}'""");
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/fpi.html");
                var card = page.Locator("#dispatchList [data-action='openProcess']", new() { HasText = "E2E-MC02" })
                               .Filter(new() { HasText = Wo });
                await Assertions.Expect(card).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                await card.ClickAsync();
                await page.WaitForURLAsync(u => u.Contains("ipqc-process.html") && u.Contains("orderType=FPI") && u.Contains(Dispatch));

                var (text, success) = await SubmitInspectionAsync(page, failQty);
                Assert.Equal(failQty == 0, success);
                Assert.False(string.IsNullOrWhiteSpace(text), "送出後沒有任何 toast");

                Assert.Equal(orderStatus, await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Status" FROM "InspectionOrders" WHERE "SourceDocId"='{Dispatch}' AND "OrderType"=1 ORDER BY "UpdatedAt" DESC LIMIT 1"""));
                Assert.Equal(dispatchStatus, await IntAsync($"""SELECT "Status"::text FROM "Dispatches" WHERE "DispatchId"='{Dispatch}'"""));
                Assert.Equal(holdReason, await IntAsync($"""SELECT "HoldReason"::text FROM "Dispatches" WHERE "DispatchId"='{Dispatch}'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await CleanInspectionsAsync();
                await SeedFixture.ResetAsync();
            }
        }

        // ── 巡檢：生產中清單 → 量測頁 → 不合格即暫停派工（OnHold, IpqcFailed）；合格不動派工 ──

        [Theory]
        [InlineData(0, "Pass", 4, null)]   // 合格：維持 InProgress
        [InlineData(1, "Fail", 6, 3)]      // 不合格：OnHold, IpqcFailed
        public async Task Ipqc_FromList_SubmitRecordsResult_FailHoldsDispatch(int failQty, string orderStatus, int dispatchStatus, int? holdReason)
        {
            await CleanInspectionsAsync();
            await SeedFixture.ExecAsync($"""UPDATE "Dispatches" SET "Status"=4 WHERE "DispatchId"='{Dispatch}'""");
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/ipqc.html");
                var card = page.Locator("#dispatchList [data-action='openProcess']", new() { HasText = "E2E-MC02" })
                               .Filter(new() { HasText = Wo });
                await Assertions.Expect(card).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                await card.ClickAsync();
                await page.WaitForURLAsync(u => u.Contains("ipqc-process.html") && u.Contains(Dispatch));

                var (text, success) = await SubmitInspectionAsync(page, failQty);
                Assert.Equal(failQty == 0, success);
                Assert.False(string.IsNullOrWhiteSpace(text), "送出後沒有任何 toast");

                Assert.Equal(orderStatus, await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Status" FROM "InspectionOrders" WHERE "SourceDocId"='{Dispatch}' AND "InspectionType"='IPQC' ORDER BY "CreatedAt" DESC LIMIT 1"""));
                Assert.Equal(dispatchStatus, await IntAsync($"""SELECT "Status"::text FROM "Dispatches" WHERE "DispatchId"='{Dispatch}'"""));
                Assert.Equal(holdReason, await IntAsync($"""SELECT "HoldReason"::text FROM "Dispatches" WHERE "DispatchId"='{Dispatch}'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await CleanInspectionsAsync();
                await SeedFixture.ResetAsync();
            }
        }

        // ── 揀料下架：待下架清單 → 選儲位 → 確認下架；庫存扣減、單據下架量與過帳狀態更新 ──

        [Fact]
        public async Task Pick_ScrapDoc_FromList_DeductsStockAndPostsDoc()
        {
            const string doc = "E2E-SCRP1", product = "E2E-RM02", loc = "E2E-LRAW";
            const decimal qty = 2m;
            async Task CleanAsync() => await SeedFixture.ExecAsync($"""
                DELETE FROM "ScrapDocLocationDetails" WHERE "ScrapDocId"='{doc}';
                DELETE FROM "ScrapDocLines" WHERE "ScrapDocId"='{doc}';
                DELETE FROM "ScrapDocs" WHERE "ScrapDocId"='{doc}';
                DELETE FROM "InventoryCostLayerConsumptions" WHERE "DocNo"='{doc}';
                DELETE FROM "StockMovements" WHERE "DocNo"='{doc}';
                """);

            await CleanAsync();
            await SeedFixture.ExecAsync($"""
                INSERT INTO "ScrapDocs" ("ScrapDocId","ScrapDate","TypeCode","TypeCategory","Status","ApprovalStatus","CreatedBy","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
                VALUES ('{doc}',current_date,'IJ','Scrap',1,2,'admin',now(),now(),'P1','1');
                INSERT INTO "ScrapDocLines" ("ScrapDocId","LineNo","ProductCode","Qty","UomCode","StockQty","StockUomCode","UnitPrice","QtyPutAway","QtySourceCleared")
                VALUES ('{doc}',1,'{product}',{qty},'E2E-EA',{qty},'E2E-EA',0,0,0);
                """);
            var before = await DecAsync($"""SELECT sum("QtyOnHand")::text FROM "Inventories" WHERE "ProductCode"='{product}' AND "LocationCode"='{loc}'""");

            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/pick.html");
                var item = page.Locator("#pendingList [data-action='selectItem']", new() { HasText = doc });
                await Assertions.Expect(item).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                await item.ClickAsync();

                await Assertions.Expect(page.Locator("#stepPick")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("#selDocNo")).ToHaveTextAsync(doc);
                var stockRow = page.Locator($"#stockRows [data-action='selectLocation'][data-loc-code='{loc}']");
                await Assertions.Expect(stockRow).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await stockRow.ClickAsync();
                await Assertions.Expect(page.Locator("#locationCode")).ToHaveValueAsync(loc);
                Assert.Equal(qty, decimal.Parse(await page.Locator("#pickQty").InputValueAsync(),
                    System.Globalization.CultureInfo.InvariantCulture));   // 建議數量＝min(可用, 需下架)

                var (text, success) = await MobileUi.ToastAfterAsync(page, () => page.Locator("#btnPick").ClickAsync());
                Assert.True(success, $"下架失敗：{text}");

                Assert.Equal(qty, await DecAsync($"""SELECT "QtyPutAway"::text FROM "ScrapDocLines" WHERE "ScrapDocId"='{doc}' AND "LineNo"=1"""));
                Assert.NotEqual(1, await IntAsync($"""SELECT "Status"::text FROM "ScrapDocs" WHERE "ScrapDocId"='{doc}'"""));   // 全數下架即離開「已確認」
                Assert.Equal(before - qty, await DecAsync(
                    $"""SELECT sum("QtyOnHand")::text FROM "Inventories" WHERE "ProductCode"='{product}' AND "LocationCode"='{loc}'"""));
                Assert.Equal(1, await IntAsync($"""SELECT count(*)::text FROM "ScrapDocLocationDetails" WHERE "ScrapDocId"='{doc}' AND "LocationCode"='{loc}'"""));

                // 下架完成後清單重載，這張單不該還在
                await Assertions.Expect(item).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            }
            finally
            {
                await ctx.CloseAsync();
                await CleanAsync();
                await SeedFixture.ResetAsync();
            }
        }
    }
}
