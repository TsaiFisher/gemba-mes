using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 批號管制料號從畫面走一遍（2026-10-27 驗收：批號原本只有單元測試，沒有任何 E2E 走過有批號的料）：
    /// 借入單（IB）上架時不填批號被擋 → 分兩個批號上架 → 借出單（IL）指定從其中一個批號下架 →
    /// 庫存依批號分列且只扣指定批號 → 異動查詢／庫存餘額可依批號篩選。
    ///
    /// <para>料號 E2E-LT01 是種子中唯一 IsLotControlled=true 的料；單據備註一律 <c>E2E-LOTFLOW</c>，種子依此清掉。
    /// 借入借出單沒有表頭倉別，倉位在上架／下架時才指定（E2E-WRAW／E2E-LRAW）。</para>
    /// </summary>
    public class LotTrackingFlowTests : FlowTestBase
    {
        private const string Mark   = "E2E-LOTFLOW";
        private const string Mat    = "E2E-LT01";
        private const string Wh     = "E2E-WRAW";
        private const string Loc    = "E2E-LRAW";
        private const string LotA   = "E2E-LOT-A";
        private const string LotB   = "E2E-LOT-B";
        private const string SaveBtn = "#loanForm button[type='submit']";

        public LotTrackingFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task 批號料上架不填批號被擋_分批上架_指定批號下架只扣該批_查詢可依批號篩選()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            // ① 借入 10：存檔即已確認（未設簽核），待上架
            await NewLoanDocAsync(page, "IB", 10m);
            var ib = await LatestDocAsync("IB");
            Assert.Equal(1, await StatusAsync(ib));

            // ② 不填批號 → 後端擋下（批號管制），庫存不變
            var (ok, message) = await TryPutAwayAsync(page, ib, Wh, Loc, "借入上架（不填批號）");
            Assert.False(ok, $"批號管制料號不填批號應被擋下，實際成功。{message}");
            Assert.Contains(Mat, message);
            Assert.Equal(0m, await OnHandAsync(null));

            // ③ 分兩個批號上架：A 6、B 4
            await PutAwayAsync(page, ib, Wh, Loc, "借入上架 LOT-A", lotNo: LotA, qty: 6m);
            await PutAwayAsync(page, ib, Wh, Loc, "借入上架 LOT-B", lotNo: LotB, qty: 4m);
            Assert.Equal(3, await StatusAsync(ib));                  // 全數上架 → 已過帳
            Assert.Equal(6m, await OnHandAsync(LotA));
            Assert.Equal(4m, await OnHandAsync(LotB));
            Assert.Equal(0m, await OnHandAsync(""));                 // 沒有落在空批號

            // ④ 借出 5，指定從 LOT-A 下架：只扣 A
            await NewLoanDocAsync(page, "IL", 5m);
            var il = await LatestDocAsync("IL");
            Assert.Equal(1, await StatusAsync(il));
            await PickAsync(page, il, Loc, 5m, "借出下架 LOT-A", lotNo: LotA);
            Assert.Equal(3, await StatusAsync(il));
            Assert.Equal(1m, await OnHandAsync(LotA));
            Assert.Equal(4m, await OnHandAsync(LotB));

            // ⑤ 異動依批號留痕：A 一進一出、B 只有進
            Assert.Equal(2, await MovementCountAsync(LotA));
            Assert.Equal(1, await MovementCountAsync(LotB));

            // ⑥ 畫面查詢可依批號篩選
            await GotoAsync(page, $"/InventoryBalance?searched=true&productCode={Mat}&lotNo={LotB}&pageSize=50");
            await AssertVisibleInListAsync(page, LotB, "庫存餘額依批號篩選");
            Assert.DoesNotContain(LotA, await page.InnerTextAsync("table"));

            await GotoAsync(page, $"/StockMovement?searched=true&productCode={Mat}&lotNo={LotA}&pageSize=50");
            await AssertVisibleInListAsync(page, il, "異動查詢依批號篩選（借出單）");
            await AssertVisibleInListAsync(page, ib, "異動查詢依批號篩選（借入單）");

            Assert.Empty(JsErrors);
        }

        private async Task NewLoanDocAsync(IPage page, string typeCode, decimal qty)
        {
            await GotoAsync(page, "/LoanDoc");
            await page.ClickAsync("button[data-url$='/LoanDoc/Create']");
            await page.WaitForSelectorAsync("#loanForm");
            await page.SelectOptionAsync("#loanForm [name='TypeCode']", typeCode);
            await page.WaitForTimeoutAsync(400);
            await page.SelectOptionAsync("#loanForm #EntityCodeSelect", "E2E-CO01");
            await page.WaitForTimeoutAsync(500);
            await page.FillAsync("#loanForm [name='Remarks']", Mark);

            await page.ClickAsync("#loanForm [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            var row = page.Locator("#loanForm #linesTbody tr").Last;
            await row.Locator("select.product-select").SelectOptionAsync(Mat);
            await page.WaitForTimeoutAsync(1000);
            await row.Locator("input[name$='.Qty']").FillAsync(qty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await SavePanelAsync(page, SaveBtn, $"借入借出單（{typeCode}）存檔");
        }

        private static async Task<string> LatestDocAsync(string typeCode) => await SeedFixture.ScalarAsync<string>(
            $"""
            SELECT "LoanDocId" FROM "LoanDocs"
            WHERE "Remarks"='{Mark}' AND "TypeCode"='{typeCode}' ORDER BY "CreatedAt" DESC LIMIT 1
            """) ?? throw new Xunit.Sdk.XunitException($"存檔後找不到備註 {Mark} 的 {typeCode} 單");

        private static async Task<int> StatusAsync(string id) => await SeedFixture.ScalarAsync<int>(
            $"""SELECT "Status" FROM "LoanDocs" WHERE "LoanDocId"='{id}'""");

        /// <summary>lot＝null：全部批號合計；""：空批號。</summary>
        private static async Task<decimal> OnHandAsync(string? lot) => await SeedFixture.ScalarAsync<decimal>(
            $"""
            SELECT COALESCE(sum("QtyOnHand"),0) FROM "Inventories"
            WHERE "PlantId"='P1' AND "ProductCode"='{Mat}' {(lot == null ? "" : $"AND \"LotNo\"='{lot}'")}
            """);

        private static async Task<int> MovementCountAsync(string lot) => (int)await SeedFixture.ScalarAsync<long>(
            $"""SELECT count(*) FROM "StockMovements" WHERE "ProductCode"='{Mat}' AND "LotNo"='{lot}'""");
    }
}
