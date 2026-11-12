using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 庫存模組的委外發退料單（/OutsourceMaterial）從畫面走一遍：發料（OO）下架過帳 → 以那張發料當來源退料（OR）上架過帳。
    ///
    /// <para>2026-10-16 前完整模式下這張單確認後沒有倉庫作業路由，永遠停在「已確認」（簡化模式確認即過帳，所以沒人發現）；
    /// 與 <see cref="ReworkMaterialFlowTests"/> 同一個缺口。委外發料所有權未移轉，過帳不產生分錄。</para>
    ///
    /// <para>測試開的單備註一律填 <c>E2E-OSMFLOW</c>，種子 ㉘ 依此清掉。料用 E2E-RM01（E2E-WRAW／E2E-LRAW），委外廠 E2E-CO01。</para>
    /// </summary>
    public class OutsourceMaterialFlowTests : FlowTestBase
    {
        private const string Mark = "E2E-OSMFLOW";
        private const string Mat  = "E2E-RM01";
        private const string Wh   = "E2E-WRAW";
        private const string Loc  = "E2E-LRAW";
        private const string SaveBtn = "#outsourceForm button[type='submit']";

        public OutsourceMaterialFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task 委外發料下架過帳_以發料為來源退料_上架過帳_庫存回補且不產生分錄()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync();

            await OpenNewAsync(page, "OO");
            await page.ClickAsync("#outsourceForm [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            var row = page.Locator("#outsourceForm #linesTbody tr").Last;
            await row.Locator("select.product-select").SelectOptionAsync(Mat);
            await page.WaitForTimeoutAsync(1000);
            await row.Locator("input[name$='.Qty']").FillAsync("8");
            await SavePanelAsync(page, SaveBtn, "委外發料存檔");
            var oo = await LatestDocAsync("OO");
            Assert.Equal(1, await StatusAsync(oo));   // 未設簽核：存檔即已確認，待倉庫下架
            await PickAsync(page, oo, Loc, 8m, "委外發料下架");
            Assert.Equal(3, await StatusAsync(oo));
            Assert.Equal(before - 8m, await OnHandAsync());

            await OpenNewAsync(page, "OR");
            await page.ClickAsync("#outsourceForm [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            row = page.Locator("#outsourceForm #linesTbody tr").Last;
            await row.Locator("[data-action='lookupSource']").ClickAsync();
            var pick = page.Locator("#sourcePickerModal tr", new PageLocatorOptions { HasText = oo }).Locator(".btn-pick").First;
            await pick.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await pick.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            Assert.Equal(oo, await row.Locator(".line-source-id").InputValueAsync());
            await row.Locator("input[name$='.Qty']").FillAsync("3");
            await SavePanelAsync(page, SaveBtn, "委外退料存檔");
            var or = await LatestDocAsync("OR");
            Assert.Equal(1, await StatusAsync(or));
            await PutAwayAsync(page, or, Wh, Loc, "委外退料上架");
            Assert.Equal(3, await StatusAsync(or));
            Assert.Equal(before - 5m, await OnHandAsync());

            Assert.Equal(0, await SeedFixture.ScalarAsync<int>(
                $"""SELECT count(*) FROM "JournalEntries" WHERE "SourceDocId" IN ('{oo}','{or}')"""));
            Assert.Empty(JsErrors);
        }

        private async Task OpenNewAsync(IPage page, string typeCode)
        {
            await GotoAsync(page, "/OutsourceMaterial");
            await page.ClickAsync("button[data-url$='/OutsourceMaterial/Create']");
            await page.WaitForSelectorAsync("#outsourceForm");
            await page.SelectOptionAsync("#outsourceForm #TypeCode", typeCode);
            await page.WaitForTimeoutAsync(400);
            await page.SelectOptionAsync("#outsourceForm #SupplierCodeSelect", "E2E-CO01");
            await page.WaitForTimeoutAsync(500);
            await page.FillAsync("#outsourceForm [name='Remarks']", Mark);
        }

        private static async Task<string> LatestDocAsync(string typeCode) => await SeedFixture.ScalarAsync<string>(
            $"""
            SELECT "OutsourceMaterialDocId" FROM "OutsourceMaterialDocs"
            WHERE "Remarks"='{Mark}' AND "TypeCode"='{typeCode}' ORDER BY "CreatedAt" DESC LIMIT 1
            """) ?? throw new Xunit.Sdk.XunitException($"存檔後找不到備註 {Mark} 的 {typeCode} 單");

        private static async Task<int> StatusAsync(string id) => await SeedFixture.ScalarAsync<int>(
            $"""SELECT "Status" FROM "OutsourceMaterialDocs" WHERE "OutsourceMaterialDocId"='{id}'""");

        private static async Task<decimal> OnHandAsync() => await SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(sum("QtyOnHand"),0) FROM "Inventories" WHERE "PlantId"='P1' AND "ProductCode"='{Mat}'""");
    }
}
