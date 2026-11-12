using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 庫存模組的重工發退料單（/ReworkMaterial）從畫面走一遍：發料（RI）→ 以那張發料當來源退料（RR），
    /// 兩張都在倉庫作業下架／上架時過帳；以及「有開工單的重工要走工單發退料單」的分工守門。
    ///
    /// <para>這張單不屬於任何工單，料發出去即認 5250 重工損失、退料沖回同一科目（mfg-domain.md §16.4b）。
    /// 2026-10-16 前退料（RR）從沒測過，而它落到通用規則貸「2152 進貨暫估應付」——退一次料，
    /// 負債就多一筆沒有供應商、永遠沒人來沖的錢。</para>
    ///
    /// <para>測試開的單備註一律填 <c>E2E-RWMFLOW</c>，種子 ㉘ 依此清掉。料用 E2E-RM01（E2E-WRAW／E2E-LRAW）。</para>
    /// </summary>
    public class ReworkMaterialFlowTests : FlowTestBase
    {
        private const string Mark = "E2E-RWMFLOW";
        private const string Mat  = "E2E-RM01";
        private const string Wc   = "E2E-WC01";
        private const string Wh   = "E2E-WRAW";
        private const string Loc  = "E2E-LRAW";
        private const string SaveBtn = "#reworkForm button[type='submit']";

        public ReworkMaterialFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task 發料下架過帳借重工損失_以發料為來源退料_上架過帳沖回重工損失_不碰進貨暫估應付()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync();

            // ① 發料 RI 10
            await OpenNewAsync(page, "RI");
            await AddLineAsync(page, 10m);
            await SavePanelAsync(page, SaveBtn, "重工發料存檔");
            var ri = await LatestDocAsync("RI");
            Assert.Equal(1, await StatusAsync(ri));   // 未設簽核：存檔即已確認，待倉庫下架
            await PickAsync(page, ri, Loc, 10m, "重工發料下架");
            Assert.Equal(3, await StatusAsync(ri));   // Posted
            Assert.Equal(before - 10m, await OnHandAsync());

            var issued = await MovementCostAsync(ri);
            Assert.True(issued > 0m, $"重工發料 {ri} 下架後沒有帶成本的庫存異動");
            Assert.Equal($"1401:0.00/{issued:0.00}|5250:{issued:0.00}/0.00", await JournalAsync(ri));

            // ② 退料 RR 4，來源挑剛才那張發料
            await OpenNewAsync(page, "RR");
            await page.ClickAsync("#reworkForm [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            var row = page.Locator("#reworkForm #linesTbody tr").Last;
            await row.Locator("[data-action='lookupSource']").ClickAsync();
            var pick = page.Locator("#sourcePickerModal tr", new PageLocatorOptions { HasText = ri }).Locator(".btn-pick").First;
            await pick.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await pick.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            Assert.Equal(ri, await row.Locator(".line-source-id").InputValueAsync());
            await row.Locator("input[name$='.Qty']").FillAsync("4");
            await SavePanelAsync(page, SaveBtn, "重工退料存檔");
            var rr = await LatestDocAsync("RR");
            Assert.Equal(1, await StatusAsync(rr));
            await PutAwayAsync(page, rr, Wh, Loc, "重工退料上架");
            Assert.Equal(3, await StatusAsync(rr));
            Assert.Equal(before - 6m, await OnHandAsync());

            var returned = await MovementCostAsync(rr);
            Assert.Equal($"1401:{returned:0.00}/0.00|5250:0.00/{returned:0.00}", await JournalAsync(rr));
            Assert.Equal(0, await SeedFixture.ScalarAsync<int>(
                $"""
                SELECT count(*) FROM "JournalLines" l JOIN "JournalEntries" e USING ("JournalId")
                WHERE e."SourceDocId" IN ('{ri}','{rr}') AND l."AccountCode" IN ('2152','5210')
                """));
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 工單號填了既有工單_存檔被擋並指向工單發退料單()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await OpenNewAsync(page, "RI");
            await page.FillAsync("#reworkForm [name='WorkOrderCode']", "E2E-WO0001");
            await AddLineAsync(page, 1m);
            await page.ClickAsync(SaveBtn);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            var msg = (await page.Locator("#sidePanelBody [data-valmsg-for='WorkOrderCode']").InnerTextAsync()).Trim();
            Assert.Contains("E2E-WO0001", msg);
            Assert.Equal(0, await SeedFixture.ScalarAsync<int>(
                $"""SELECT count(*) FROM "ReworkMaterialDocs" WHERE "Remarks"='{Mark}'"""));
            Assert.Empty(JsErrors);
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task OpenNewAsync(IPage page, string typeCode)
        {
            await GotoAsync(page, "/ReworkMaterial");
            await page.ClickAsync("button[data-url$='/ReworkMaterial/Create']");
            await page.WaitForSelectorAsync("#reworkForm");
            await page.SelectOptionAsync("#reworkForm #TypeCode", typeCode);
            await page.WaitForTimeoutAsync(400);
            await page.SelectOptionAsync("#reworkForm #WorkCenterCodeSelect", Wc);
            await page.WaitForTimeoutAsync(500);
            await page.FillAsync("#reworkForm [name='Remarks']", Mark);
        }

        private static async Task AddLineAsync(IPage page, decimal qty)
        {
            await page.ClickAsync("#reworkForm [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            var row = page.Locator("#reworkForm #linesTbody tr").Last;
            await row.Locator("select.product-select").SelectOptionAsync(Mat);
            await page.WaitForTimeoutAsync(1000);   // onProductChange 帶出單位
            await row.Locator("input[name$='.Qty']").FillAsync(qty.ToString());
        }

        private static async Task<string> LatestDocAsync(string typeCode) => await SeedFixture.ScalarAsync<string>(
            $"""
            SELECT "ReworkMaterialDocId" FROM "ReworkMaterialDocs"
            WHERE "Remarks"='{Mark}' AND "TypeCode"='{typeCode}' ORDER BY "CreatedAt" DESC LIMIT 1
            """) ?? throw new Xunit.Sdk.XunitException($"存檔後找不到備註 {Mark} 的 {typeCode} 單");

        private static async Task<int> StatusAsync(string id) => await SeedFixture.ScalarAsync<int>(
            $"""SELECT "Status" FROM "ReworkMaterialDocs" WHERE "ReworkMaterialDocId"='{id}'""");

        private static async Task<decimal> OnHandAsync() => await SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(sum("QtyOnHand"),0) FROM "Inventories" WHERE "PlantId"='P1' AND "ProductCode"='{Mat}'""");

        private static async Task<decimal> MovementCostAsync(string docNo) => Math.Round(
            await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT COALESCE(sum("TotalCost"),0) FROM "StockMovements" WHERE "DocNo"='{docNo}'"""), 2);

        /// <summary>該單分錄依科目彙總成「科目:借/貸」，以「|」串接、依科目排序。</summary>
        private static async Task<string> JournalAsync(string docNo) => await SeedFixture.ScalarAsync<string>(
            $"""
            SELECT string_agg(a, '|' ORDER BY a) FROM (
              SELECT l."AccountCode"||':'||sum(l."Debit")::numeric(18,2)||'/'||sum(l."Credit")::numeric(18,2) AS a
              FROM "JournalLines" l JOIN "JournalEntries" e USING ("JournalId")
              WHERE e."SourceDocId"='{docNo}' GROUP BY l."AccountCode") t
            """) ?? "(沒有分錄)";
    }
}
